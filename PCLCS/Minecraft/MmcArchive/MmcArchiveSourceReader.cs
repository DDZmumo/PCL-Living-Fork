using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed record MmcArchiveProgress(string Phase, long CompletedBytes, long TotalBytes);
public sealed record MmcArchiveFile(string RelativePath, long SizeBytes, uint Crc32);
public sealed record MmcArchiveEntryObservation(string Path, bool IsDirectory, long SizeBytes, uint Crc32);
public sealed record MmcArchiveMetadata(string RelativePath, string Sha256, ImmutableArray<byte> Bytes);

/// <summary>One read-locked source handle; paths here are host facts, not portable instance settings.</summary>
public sealed class MmcArchiveSource : IDisposable {
    private readonly FileStream source;
    private readonly ZipArchive archive;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> entries;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> archiveFiles;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private readonly object lifetime = new();
    private bool disposed;
    private bool activeRead, sourceClosed;
    public string OriginalPath { get; }
    public string PackageSha256 { get; }
    public MmcPackage Package { get; }
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; }
    public ImmutableArray<MmcArchiveMetadata> Metadata { get; }
    public ImmutableArray<MmcArchiveFile> Files { get; }
    public ImmutableArray<MmcArchiveEntryObservation> ArchiveEntries { get; }

    internal MmcArchiveSource(string path, string digest, FileStream source, ZipArchive archive,
        Dictionary<string, ZipArchiveEntry> entries, MmcPackage package,
        ImmutableArray<MmcDiagnostic> diagnostics, ImmutableArray<MmcArchiveMetadata> metadata) {
        OriginalPath = path; PackageSha256 = digest; this.source = source; this.archive = archive;
        this.entries = entries; Package = package; Diagnostics = diagnostics; Metadata = metadata;
        archiveFiles = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\'))
            .ToDictionary(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        Files = entries.Select(item => new MmcArchiveFile(item.Key, item.Value.Length, item.Value.Crc32)).ToImmutableArray();
        ArchiveEntries = archive.Entries.Select(entry => new MmcArchiveEntryObservation(entry.FullName.Replace('\\', '/'),
            entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'), entry.Length, entry.Crc32)).ToImmutableArray();
    }

    /// <summary>Open only a validated file inside the discovered instance root. Caller enforces actual copy budgets and CRC.</summary>
    public Stream OpenFile(string relativePath) {
        lock (lifetime) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!readGate.Wait(0)) throw new InvalidOperationException("A ZIP entry stream is still active; dispose it or use OpenFileAsync.");
            try { return OpenLeased(relativePath); }
            catch { readGate.Release(); throw; }
        }
    }

    /// <summary>Shared ZIP seek/read state is leased until the returned stream is disposed.</summary>
    public async ValueTask<Stream> OpenFileAsync(string relativePath, CancellationToken token = default) {
        return await OpenLeasedAsync(relativePath, entries, token).ConfigureAwait(false);
    }

    /// <summary>Read a member of the already-inspected complete ZIP index, including outer opaque docs; never authorizes a live target.</summary>
    public async ValueTask<Stream> OpenArchiveFileAsync(string archiveRelativePath, CancellationToken token = default) {
        return await OpenLeasedAsync(archiveRelativePath, archiveFiles, token).ConfigureAwait(false);
    }

    private async ValueTask<Stream> OpenLeasedAsync(string relativePath, IReadOnlyDictionary<string, ZipArchiveEntry> index, CancellationToken token) {
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        try { await readGate.WaitAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { throw new ObjectDisposedException(nameof(MmcArchiveSource)); }
        lock (lifetime) {
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                return OpenLeased(relativePath, index);
            } catch { readGate.Release(); throw; }
        }
    }

    private Stream OpenLeased(string relativePath, IReadOnlyDictionary<string, ZipArchiveEntry>? index = null) {
        MmcArtifactIo.Relative(relativePath);
        if (!(index ?? entries).TryGetValue(relativePath.Replace('\\', '/'), out ZipArchiveEntry? entry))
            throw new FileNotFoundException("Declared file is absent from the fixed package source.", relativePath);
        Stream stream = entry.Open();
        activeRead = true;
        return new EntryLeaseStream(stream, this);
    }

    private void ReleaseRead() {
        try {
            lock (lifetime) { activeRead = false; if (disposed) CloseSource(); }
        } finally { readGate.Release(); }
    }

    private void CloseSource() {
        if (sourceClosed) return;
        sourceClosed = true;
        try { archive.Dispose(); } finally { source.Dispose(); }
    }

    public void Dispose() {
        lock (lifetime) { if (disposed) return; disposed = true; }
        closing.Cancel();
        lock (lifetime) { if (!activeRead) CloseSource(); }
    }

    private sealed class EntryLeaseStream(Stream inner, MmcArchiveSource owner) : Stream {
        private Stream? input = inner;
        private Stream Input => input ?? throw new ObjectDisposedException(nameof(EntryLeaseStream));
        public override bool CanRead => input?.CanRead ?? false;
        public override bool CanSeek => input?.CanSeek ?? false;
        public override bool CanWrite => false;
        public override long Length => Input.Length;
        public override long Position { get => Input.Position; set => Input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => Input.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Input.ReadAsync(buffer, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Input.ReadAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => Input.Seek(offset, origin);
        public override void Flush() => Input.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) {
            if (disposing && Interlocked.Exchange(ref input, null) is { } held)
                try { held.Dispose(); } finally { owner.ReleaseRead(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync() {
            if (Interlocked.Exchange(ref input, null) is { } held)
                try { await held.DisposeAsync().ConfigureAwait(false); } finally { owner.ReleaseRead(); }
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Bounded source acquisition around Core's logical index/parser; no extraction or launch authorization.</summary>
public static class MmcArchiveSourceReader {
    public static async Task<MmcArchiveSource> ReadAsync(string archivePath, MmcReadLimits? limits = null,
        Encoding? legacyEntryNameEncoding = null, IProgress<MmcArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) {
        limits ??= new();
        if (limits.MaxArchiveEntries < 1 || limits.MaxArchiveBytes < 1 || limits.MaxEntryBytes < 1 ||
            limits.MaxMetadataBytes < 1 || limits.MaxTotalMetadataBytes < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        string path = Path.GetFullPath(archivePath);
        MmcInstancePaths.EnsureNoLinks(path);
        Encoding names = StrictEncoding(legacyEntryNameEncoding ??
            CodePagesEncodingProvider.Instance.GetEncoding(437) ?? Encoding.UTF8);
        FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        ZipArchive? archive = null;
        try {
            string packageDigest = await HashSourceAsync(source, progress, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            int expectedCount = MmcArtifactMaterializer.PreflightArchive(source, limits.MaxArchiveEntries,
                limits.MaxArchiveBytes, names, allowZip64EntrySizes: true, maximumEntryBytes: limits.MaxEntryBytes);
            cancellationToken.ThrowIfCancellationRequested();
            source.Position = 0;
            archive = new(source, ZipArchiveMode.Read, true, names);
            if (archive.Entries.Count != expectedCount) throw new InvalidDataException("Archive index changed after bounded preflight.");
            MmcArchiveEntry[] descriptors = archive.Entries.Select(entry => new MmcArchiveEntry(
                entry.FullName.Replace('\\', '/'), entry.Length, DirectoryEntry(entry))).ToArray();
            MmcLegacyAdoptionCapabilities.RefusePrivateRecoveryArchive(descriptors.Select(entry => entry.Path));
            MmcArchiveInspection inspection = MmcPackageReader.InspectArchive(descriptors, limits);
            if (inspection.HasErrors || inspection.RelativeRoot is null)
                throw new InvalidDataException(Describe(inspection.Diagnostics));
            Dictionary<string, ZipArchiveEntry> fullIndex = new(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
                if (!DirectoryEntry(entry) && !fullIndex.TryAdd(entry.FullName.Replace('\\', '/'), entry))
                    throw new InvalidDataException("Archive has colliding file names.");
            Dictionary<string, string> content = new(StringComparer.OrdinalIgnoreCase);
            var metadata = ImmutableArray.CreateBuilder<MmcArchiveMetadata>();
            HashSet<string> undecodable = new(StringComparer.OrdinalIgnoreCase);
            long metadataBytes = 0;
            long expectedMetadataBytes = inspection.MetadataPaths.Sum(item => fullIndex[item].Length);
            foreach (string name in inspection.MetadataPaths) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!fullIndex.TryGetValue(name, out ZipArchiveEntry? entry)) throw new InvalidDataException("Inspected metadata is absent from the source.");
                if (entry.Length > limits.MaxMetadataBytes || metadataBytes > limits.MaxTotalMetadataBytes - entry.Length)
                    throw new InvalidDataException("Archive metadata byte budget exceeded.");
                using MemoryStream buffer = new();
                await using (Stream stream = entry.Open()) {
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0) {
                        if (buffer.Length + read > limits.MaxMetadataBytes || metadataBytes > limits.MaxTotalMetadataBytes - read)
                            throw new InvalidDataException("Archive metadata actual bytes exceed the budget.");
                        buffer.Write(chunk, 0, read); metadataBytes += read;
                    }
                }
                byte[] bytes = buffer.ToArray();
                if (bytes.LongLength != entry.Length || MmcArtifactIo.Crc32(bytes) != entry.Crc32)
                    throw new InvalidDataException("Archive metadata size or CRC does not match its fixed index.");
                try { content.Add(name, new UTF8Encoding(false, true).GetString(bytes)); }
                catch (DecoderFallbackException) { undecodable.Add(name); } // Opaque, unreferenced package metadata is preserved as bytes.
                metadata.Add(new(name[inspection.RelativeRoot.Length..], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.ToImmutableArray()));
                progress?.Report(new("metadata", metadataBytes, expectedMetadataBytes));
            }
            MmcParseResult parsed = MmcPackageReader.Parse(new(descriptors, content), limits);
            if (parsed.HasErrors || parsed.Package is null) {
                string? requiredBadEncoding = parsed.Diagnostics.Where(d => d.Severity == MmcDiagnosticSeverity.Error)
                    .Select(d => d.Source).FirstOrDefault(undecodable.Contains);
                if (requiredBadEncoding is not null) throw new InvalidDataException("Required MMC metadata is not valid UTF-8: " + requiredBadEncoding);
                throw new InvalidDataException(Describe(parsed.Diagnostics));
            }
            Dictionary<string, ZipArchiveEntry> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in fullIndex)
                if (item.Key.StartsWith(inspection.RelativeRoot, StringComparison.OrdinalIgnoreCase))
                    files.Add(item.Key[inspection.RelativeRoot.Length..], item.Value);
            return new(path, packageDigest, source, archive, files, parsed.Package, parsed.Diagnostics, metadata.ToImmutable());
        } catch {
            archive?.Dispose(); source.Dispose(); throw;
        }
    }

    private static async Task<string> HashSourceAsync(FileStream source, IProgress<MmcArchiveProgress>? progress, CancellationToken token) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072];
        long completed = 0, reported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) {
            FileTaskProgress.RecordRead(read);
            hash.AppendData(buffer, 0, read); completed += read;
            if (completed - reported >= 1024 * 1024 || completed == source.Length) {
                progress?.Report(new("package-hash", completed, source.Length)); reported = completed;
            }
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Encoding StrictEncoding(Encoding encoding) {
        Encoding copy = (Encoding)encoding.Clone();
        copy.DecoderFallback = DecoderFallback.ExceptionFallback;
        copy.EncoderFallback = EncoderFallback.ExceptionFallback;
        return copy;
    }
    private static bool DirectoryEntry(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
    private static string Describe(IEnumerable<MmcDiagnostic> diagnostics) =>
        "MMC source cannot be safely parsed: " + string.Join("; ", diagnostics.Where(d => d.Severity == MmcDiagnosticSeverity.Error).Select(d => d.Code + ": " + d.Message));
}
