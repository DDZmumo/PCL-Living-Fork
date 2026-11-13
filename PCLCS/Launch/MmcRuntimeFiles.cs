using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcRuntimeFiles {
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static string JavaPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');
    internal static string RelativeJavaPath(string gameRoot, string path) {
        string relative = Path.GetRelativePath(gameRoot, path);
        // A different volume cannot be represented relative to GameRoot. All
        // bound artifacts normally share its volume; retain the exact absolute
        // representation if a future owned support role permits otherwise.
        return relative.Replace('\\', '/');
    }
    internal static void Hash(string? hash) {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new MmcRuntimeLaunchException("MMC_RUNTIME_DIGEST", "A verified runtime identity is missing or malformed.");
    }
    internal static void SamePath(string actual, string expected, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        if (!Path.IsPathFullyQualified(actual) || !Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), PathComparison))
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_PATH", "A runtime path does not match its approved instance role.");
        MmcInstancePaths.EnsureNoLinks(expected, verifiedDirectories);
    }
    internal static void Owned(string actual, string parent, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        if (!Path.IsPathFullyQualified(actual) || !Path.GetFullPath(actual).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, PathComparison))
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_PATH", "A runtime support path is outside the approved owned view.");
        MmcInstancePaths.EnsureNoLinks(actual, verifiedDirectories);
    }
    internal static MmcRuntimeInputObservation Observe(string identity, string path, string expectedHash,
        long? expectedSize, CancellationToken token, bool owned = true, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        Hash(expectedHash);
        if (!Path.IsPathFullyQualified(path)) throw new MmcRuntimeLaunchException("MMC_RUNTIME_PATH", "Runtime inputs require an approved absolute path.");
        if (owned) MmcInstancePaths.EnsureNoLinks(path, verifiedDirectories);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        string observed = HashStream(stream, token);
        if (!observed.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) || expectedSize is not null && stream.Length != expectedSize)
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_INPUT_CHANGED", "An actual runtime input differs from its adopted byte identity.");
        return new(identity, Path.GetFullPath(path), observed, stream.Length);
    }
    internal static Task<MmcRuntimeInputObservation> ObserveAsync(string identity, string path, string expectedHash,
        long? expectedSize, CancellationToken token, bool owned = true) =>
        Task.FromResult(Observe(identity, path, expectedHash, expectedSize, token, owned));
    internal static string HashStream(Stream stream, CancellationToken token) {
        if (!stream.CanSeek || stream.Length > 4L * 1024 * 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_RUNTIME_INPUT_BUDGET", "Runtime input exceeds its verified byte budget.");
        stream.Position = 0;
        if (stream.Length <= 16 * 1024 * 1024) {
            token.ThrowIfCancellationRequested();
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(stream, digest);
            if (stream.Position > 4L * 1024 * 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_RUNTIME_INPUT_BUDGET", "Runtime input grew beyond its byte budget.");
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(digest);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(262144);
        try {
            long read = 0;
            while (true) {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                read += count;
                if (read > 4L * 1024 * 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_RUNTIME_INPUT_BUDGET", "Runtime input grew beyond its byte budget.");
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            Span<byte> result = stackalloc byte[32];
            hash.TryGetHashAndReset(result, out _);
            return Convert.ToHexStringLower(result);
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    internal static Task<string> HashStreamAsync(Stream stream, CancellationToken token) =>
        Task.FromResult(HashStream(stream, token));
    internal static string Fingerprint(IEnumerable<string?> values) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (string? value in values) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? ""); BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
