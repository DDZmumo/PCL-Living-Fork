using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed class MmcArtifactWorkspace : IDisposable {
    private readonly string owner;
    private readonly string runtimeRoot;
    private bool disposed;
    internal string Root { get; }

    internal MmcArtifactWorkspace(MmcInstancePaths paths) {
        runtimeRoot = paths.RuntimeRoot;
        string id = Guid.NewGuid().ToString("N");
        Root = paths.ResolveRelative(MmcPathRole.Runtime, ".staging/" + id);
        owner = "PCLLF.MmcArtifactWorkspace/v1\n" + id + "\n" + paths.InstanceRoot;
        if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Artifact workspace already exists.");
        MmcArtifactIo.Directory(Root);
        using var stream = new FileStream(Path.Combine(Root, "owner.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(owner);
        stream.Write(bytes); stream.Flush(true);
    }

    internal string NewFile() {
        if (disposed) throw new ObjectDisposedException(nameof(MmcArtifactWorkspace));
        string result = Path.Combine(Root, "files", Guid.NewGuid().ToString("N"));
        MmcArtifactIo.Directory(Path.GetDirectoryName(result)!);
        MmcInstancePaths.EnsureNoLinks(result);
        return result;
    }

    public void Dispose() {
        if (disposed) return;
        if (!Directory.Exists(Root)) { disposed = true; return; }
        string parent = Path.GetDirectoryName(Root)!;
        if (!string.Equals(Path.GetDirectoryName(parent), runtimeRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(parent) != ".staging" || !Guid.TryParseExact(Path.GetFileName(Root), "N", out _))
            throw new IOException("Artifact cleanup ownership path changed.");
        MmcInstancePaths.EnsureNoLinks(Root);
        string marker = Path.Combine(Root, "owner.txt");
        MmcInstancePaths.EnsureNoLinks(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096 || File.ReadAllText(marker) != owner)
            throw new IOException("Artifact cleanup ownership marker changed; workspace preserved.");
        DeleteOwnedDirectory(Root, marker);
        disposed = true;
    }
    /// <summary>Lifecycle recovery only: its recorded workspace and original producer share the held instance gate for their entire lifetime.</summary>
    internal static void RecoverOwned(MmcAdmissionLease lease,string relative,string expectedOwnerRoot) {
        lease.AssertHeld();string[] parts=relative.Split('/');
        if(parts.Length!=4||parts[0]!="PCL"||parts[1]!="MmcRuntime"||parts[2]!=".staging"||!Guid.TryParseExact(parts[3],"N",out Guid id)||id==Guid.Empty)
            throw new IOException("Lifecycle workspace pointer is not an exact owned GUID boundary.");
        string root=MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance,relative);
        if(MmcInstancePaths.Attributes(root)is null)return;MmcInstancePaths.EnsureNoLinks(root);
        string marker=Path.Combine(root,"owner.txt");MmcInstancePaths.EnsureNoLinks(marker);
        if(new FileInfo(marker).Length>4096||File.ReadAllText(marker)!="PCLLF.MmcArtifactWorkspace/v1\n"+parts[3]+"\n"+expectedOwnerRoot)
            throw new IOException("Lifecycle workspace owner bytes changed; preserving every file.");
        DeleteOwnedDirectory(root,marker);
    }

    private static void DeleteOwnedDirectory(string path, string? finalMarker = null) {
        MmcInstancePaths.EnsureNoLinks(path);
        foreach (string child in System.IO.Directory.EnumerateFileSystemEntries(path)) {
            if (finalMarker is not null && string.Equals(child, finalMarker, StringComparison.Ordinal)) continue;
            MmcInstancePaths.EnsureNoLinks(child);
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) DeleteOwnedDirectory(child);
            else File.Delete(child);
        }
        if (finalMarker is not null) { MmcInstancePaths.EnsureNoLinks(finalMarker); File.Delete(finalMarker); }
        System.IO.Directory.Delete(path, false);
    }
}

internal sealed class MmcArtifactIo {
    private static readonly uint[] CrcTable = BuildCrcTable();
    internal static uint Crc32(ReadOnlySpan<byte> bytes) {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
    private readonly MmcArtifactLimits limits;
    private readonly IProgress<MmcArtifactProgress>? progress;
    private long copied;
    internal MmcArtifactIo(MmcArtifactLimits limits, IProgress<MmcArtifactProgress>? progress) { this.limits = limits; this.progress = progress; }

    internal static void Directory(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        System.IO.Directory.CreateDirectory(path);
        MmcInstancePaths.EnsureNoLinks(path);
    }

    internal static string AuthorizedSource(MmcArtifactSource source) {
        if (!Path.IsPathFullyQualified(source.ApprovedRoot) || !Path.IsPathFullyQualified(source.FilePath))
            throw new IOException("Artifact source must have an absolute approved boundary.");
        if (source.ApprovedRoot.StartsWith("\\\\?\\", StringComparison.Ordinal) || source.ApprovedRoot.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            source.FilePath.StartsWith("\\\\?\\", StringComparison.Ordinal) || source.FilePath.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new IOException("Artifact sources cannot use device namespaces.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.ApprovedRoot));
        string file = Path.GetFullPath(source.FilePath);
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Artifact source escapes its approved boundary.");
        MmcInstancePaths.EnsureNoLinks(root); MmcInstancePaths.EnsureNoLinks(file);
        if ((File.GetAttributes(file) & FileAttributes.Directory) != 0) throw new IOException("Artifact source is a directory.");
        return file;
    }

    internal static void Relative(string path) {
        if (string.IsNullOrEmpty(path) || path.Length > 32767 || Path.IsPathRooted(path) || path[0] is '/' or '\\')
            throw new IOException("Artifact logical path is invalid.");
        int count = 0;
        ReadOnlySpan<char> remaining = path.AsSpan();
        while (true) {
            if (++count > 128) throw new IOException("Artifact logical path exceeds its segment budget.");
            int sep = remaining.IndexOfAny('/', '\\');
            ReadOnlySpan<char> segment = sep < 0 ? remaining : remaining[..sep];
            if (segment.Length is 0 or > 255 || segment is "." or "..")
                throw new IOException("Artifact logical path exceeds its segment budget.");
            if (sep < 0) return;
            remaining = remaining[(sep + 1)..];
        }
    }

    internal static bool Digest(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    internal static (string Sha256, long Size, string Sha1) HashFile(string path, long maximum, CancellationToken token,
        string? expectedSha1 = null) {
        MmcInstancePaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        long initialLength = input.CanSeek ? input.Length : -1;
        if (initialLength > maximum) throw new IOException("Artifact byte budget exceeded.");
        Span<byte> sha1Digest = stackalloc byte[20];
        Span<byte> sha256Digest = stackalloc byte[32];
        if (initialLength is >= 0 and <= 1024 * 1024) {
            int len = (int)initialLength;
            byte[] small = len == 0 ? Array.Empty<byte>() : System.Buffers.ArrayPool<byte>.Shared.Rent(len);
            try {
                int filled = 0;
                while (filled < len) {
                    int chunk = input.Read(small, filled, len - filled);
                    if (chunk == 0) break;
                    token.ThrowIfCancellationRequested();
                    FileTaskProgress.RecordRead(chunk);
                    filled += chunk;
                }
                token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> span = small.AsSpan(0, filled);
                SHA1.HashData(span, sha1Digest);
                string fastSha1 = Convert.ToHexStringLower(sha1Digest);
                if (expectedSha1 is not null && !fastSha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Artifact content does not match its declared SHA1.");
                SHA256.HashData(span, sha256Digest);
                return (Convert.ToHexStringLower(sha256Digest), filled, fastSha1);
            } finally {
                if (small.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(small);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
        try {
            long size = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0) {
                token.ThrowIfCancellationRequested();
                FileTaskProgress.RecordRead(read);
                size = checked(size + read);
                if (size > maximum) throw new IOException("Artifact actual byte budget exceeded.");
                hash.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            sha1.TryGetHashAndReset(sha1Digest, out _);
            string observedSha1 = Convert.ToHexStringLower(sha1Digest);
            if (expectedSha1 is not null && !observedSha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Artifact content does not match its declared SHA1.");
            hash.TryGetHashAndReset(sha256Digest, out _);
            return (Convert.ToHexStringLower(sha256Digest), size, observedSha1);
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static (string Sha256, long Size) HashSha256(string path, long maximum, CancellationToken token) {
        MmcInstancePaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (input.Length > maximum) throw new IOException("Artifact byte budget exceeded.");
        if (input.CanSeek && input.Length <= 16 * 1024 * 1024) {
            token.ThrowIfCancellationRequested();
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(input, digest);
            long observed = input.Position;
            if (observed > maximum) throw new IOException("Artifact actual byte budget exceeded.");
            if (observed > 0) FileTaskProgress.RecordRead((int)observed);
            token.ThrowIfCancellationRequested();
            return (Convert.ToHexStringLower(digest), observed);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
        try {
            long size = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0) {
                token.ThrowIfCancellationRequested();
                FileTaskProgress.RecordRead(read);
                size = checked(size + read);
                if (size > maximum) throw new IOException("Artifact actual byte budget exceeded.");
                hash.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            Span<byte> digest = stackalloc byte[32];
            hash.TryGetHashAndReset(digest, out _);
            return (Convert.ToHexStringLower(digest), size);
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<(string Sha256, long Size, string Sha1)> HashAsync(string path, long maximum, CancellationToken token,
        string? expectedSha1 = null) {
        MmcInstancePaths.EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximum) throw new IOException("Artifact byte budget exceeded.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
        try {
            long size = 0; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
                FileTaskProgress.RecordRead(read);
                size = checked(size + read);
                if (size > maximum) throw new IOException("Artifact actual byte budget exceeded.");
                hash.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
            }
            string observedSha1 = Convert.ToHexStringLower(sha1.GetHashAndReset());
            if (expectedSha1 is not null && !observedSha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Artifact content does not match its declared SHA1.");
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), size, observedSha1);
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal async Task<(string Sha256, long Size, string Sha1)> CopyAsync(string source, string target, string key,
        string? expectedSha256, string? expectedSha1, long? expectedSize, CancellationToken token, long? maximumBytes = null) {
        MmcInstancePaths.EnsureNoLinks(source); MmcInstancePaths.EnsureNoLinks(target);
        Directory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long maximum = Math.Min(limits.MaxArtifactBytes, maximumBytes ?? limits.MaxArtifactBytes);
        if (input.Length > maximum) throw new IOException("Artifact declared byte budget exceeded: " + key);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
        try {
            long size = 0; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
                FileTaskProgress.RecordRead(read);
                size = checked(size + read);
                long totalCopied = Interlocked.Add(ref copied, read);
                if (size > maximum || totalCopied > limits.MaxCopiedBytes) throw new IOException("Artifact actual copy budget exceeded: " + key);
                sha256.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                FileTaskProgress.RecordWrite(read);
                progress?.Report(new("copy", key, totalCopied));
            }
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            string digest = Convert.ToHexStringLower(sha256.GetHashAndReset());
            string digest1 = Convert.ToHexStringLower(sha1.GetHashAndReset());
            if ((expectedSha256 is not null && !digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) ||
                (expectedSha1 is not null && !digest1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase)) ||
                (expectedSize is not null && expectedSize != size))
                throw new IOException("Artifact content does not match the locked/declared digest or size: " + key);
            return (digest, size, digest1);
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal async Task<(string Sha256, long Size, string Sha1)> ExtractAsync(Stream input, string target, string key,
        long expectedSize, uint expectedCrc32, long maximum, CancellationToken token, string phase = "native-extract") {
        MmcInstancePaths.EnsureNoLinks(target); Directory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
        try {
            long size = 0; uint crc = uint.MaxValue; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
                FileTaskProgress.RecordRead(read);
                size = checked(size + read);
                long totalCopied = Interlocked.Add(ref copied, read);
                if (size > maximum || size > expectedSize || totalCopied > limits.MaxCopiedBytes) throw new IOException("Native actual output budget or declared size exceeded.");
                hash.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                for (int index = 0; index < read; index++) crc = CrcTable[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                FileTaskProgress.RecordWrite(read);
                progress?.Report(new(phase, key, totalCopied));
            }
            if (size != expectedSize || ~crc != expectedCrc32) throw new IOException("Native actual output differs from its declared size or CRC32.");
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), size, Convert.ToHexStringLower(sha1.GetHashAndReset()));
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static uint[] BuildCrcTable() {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++) {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
