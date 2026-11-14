using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Threading;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>Flushes file content. This does not claim atomic multi-file commits or power-loss guarantees.</summary>
internal static class MmcDurableFiles {
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    internal const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    internal static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    internal static bool IsHash(string? value) => value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string? Observe(string path, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        MmcInstancePaths.EnsureNoLinks(path, verifiedDirectories);
        var attributes = MmcInstancePaths.Attributes(path);
        if (attributes is null) return null;
        if ((attributes.Value & FileAttributes.Directory) != 0) throw new IOException("事务文件目标被目录占用。");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes) throw new IOException("事务文件超过预算。");
        if (stream.CanSeek && stream.Length <= 16 * 1024 * 1024) {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(stream, digest);
            long observed = stream.Position;
            if (observed > MaximumFileBytes) throw new IOException("事务文件读取期间超过预算。");
            if (observed > 0) FileTaskProgress.RecordRead((int)observed);
            return Convert.ToHexStringLower(digest);
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(131072);
        try {
            long total = 0; int count;
            while ((count = stream.Read(bytes, 0, bytes.Length)) != 0) {
                FileTaskProgress.RecordRead(count);
                total += count; if (total > MaximumFileBytes) throw new IOException("事务文件读取期间超过预算。");
                hash.AppendData(bytes, 0, count);
            }
            Span<byte> digest = stackalloc byte[32];
            hash.TryGetHashAndReset(digest, out _);
            return Convert.ToHexStringLower(digest);
        } finally {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
    internal static void WriteNew(string path, ReadOnlySpan<byte> content) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough);
        stream.Write(content); FileTaskProgress.RecordWrite(content.Length); stream.Flush(true);
    }
    internal static long CopyNew(string source, string target, string expectedSha256, CancellationToken token = default) {
        MmcInstancePaths.EnsureNoLinks(source); MmcInstancePaths.EnsureNoLinks(target);
        long total = 0;
        using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough)) {
            if (input.Length > MaximumFileBytes) throw new IOException("事务文件超过预算。");
            byte[] buffer = new byte[81920]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
                FileTaskProgress.RecordRead(count);
                token.ThrowIfCancellationRequested(); total += count;
                if (total > MaximumFileBytes) throw new IOException("事务复制期间超过预算。");
                output.Write(buffer, 0, count);
                FileTaskProgress.RecordWrite(count);
            }
            output.Flush(true);
        }
        if (Observe(target) != expectedSha256) throw new IOException("事务副本摘要不符合已锁定内容。");
        return total;
    }
    internal static string WriteRecord<T>(string path, T value) {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxRecordBytes) throw new IOException("事务日志超过预算。");
        string hash = Hash(payload);
        byte[] header = Encoding.ASCII.GetBytes("PCLLF-MMC-RECORD-1\n" + hash + "\n");
        byte[] bytes = new byte[header.Length + payload.Length]; header.CopyTo(bytes, 0); payload.CopyTo(bytes, header.Length);
        WriteNew(path, bytes); return hash;
    }
    internal static (T Value, string Hash) ReadRecord<T>(string path, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxRecordBytes + 128) throw new IOException("事务日志超过预算。");
        using MemoryStream bytes = new(); byte[] buffer = new byte[64 * 1024]; int count;
        do {
            cancellationToken.ThrowIfCancellationRequested();
            count = stream.Read(buffer, 0, buffer.Length); bytes.Write(buffer, 0, count);
            if (bytes.Length > MaxRecordBytes + 128) throw new IOException("事务日志超过预算。");
        } while (count != 0);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] content = bytes.ToArray(); byte[] prefix = Encoding.ASCII.GetBytes("PCLLF-MMC-RECORD-1\n");
        if (content.Length < prefix.Length + 65 || !content.AsSpan(0, prefix.Length).SequenceEqual(prefix) || content[prefix.Length + 64] != '\n')
            throw new IOException("事务日志格式未知或损坏。");
        string expected = Encoding.ASCII.GetString(content, prefix.Length, 64);
        byte[] payload = content[(prefix.Length + 65)..];
        if (!IsHash(expected) || Hash(payload) != expected) throw new IOException("事务日志摘要损坏。");
        try {
            using JsonDocument strict = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(payload));
            T value = JsonSerializer.Deserialize<T>(payload) ?? throw new IOException("事务日志内容为空。");
            cancellationToken.ThrowIfCancellationRequested();
            return (value, expected);
        } catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException) { throw new IOException("事务日志内容无效。", ex); }
    }
}
