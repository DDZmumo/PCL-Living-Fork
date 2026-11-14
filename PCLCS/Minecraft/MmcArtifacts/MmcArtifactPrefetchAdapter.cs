using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcArtifactCacheRoots(string LibrariesRoot, string AssetsRoot, string SourceCacheRoot, string? VersionsRoot = null);
public sealed record MmcArtifactDownloadSpec(MmcArtifactReference Artifact, ImmutableArray<string> ArtifactKeys,
    string DownloadPath, ImmutableArray<string> Urls, string? ExpectedSha256, string? ExpectedSha1, long? ExpectedSize, long MaximumSizeBytes) {
    // Legacy Nide metadata can supply MD5. This is checked against that official
    // source, then adopted as SHA256 before any owned execution copy is published.
    public string? ExpectedMd5 { get; init; }
}
public sealed record MmcPrefetchedSources(ImmutableDictionary<string, MmcArtifactSource> Sources, int BatchCount, int RequestedFileCount) {
    /// <summary>No per-artifact network fallback: missing/raced inputs require rerunning the batch preparation.</summary>
    public MmcArtifactSourceAccess CreateAccess(Func<MmcArtifactReference, CancellationToken, ValueTask<MmcArtifactSource?>>? localSource = null) =>
        new((artifact, token) => {
            token.ThrowIfCancellationRequested();
            return Sources.TryGetValue(artifact.Key, out var source) ? ValueTask.FromResult<MmcArtifactSource?>(source) :
                artifact.Scope == MmcArtifactScope.InstanceLocal && localSource is not null ? localSource(artifact, token) : ValueTask.FromResult<MmcArtifactSource?>(null);
        });
}

/// <summary>
/// Plans at most one static and one indexed-object batch. The supplied builder/runner wrap the
/// existing NetFile/Loader implementation, including bounded concurrency, cancellation and draining.
/// This adapter creates neither HTTP clients nor another download queue.
/// </summary>
public sealed class MmcArtifactPrefetchAdapter<TNetFile> where TNetFile : notnull {
    private readonly MmcArtifactCacheRoots roots;
    private readonly Func<MmcArtifactDownloadSpec, TNetFile> build;
    private readonly Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch;
    private readonly MmcArtifactLimits limits;
    private sealed record SourcePin(int SchemaVersion, string SourceRevision, string UriFingerprint,
        string FileName, string Sha256, string Sha1, long SizeBytes);
    private sealed record VerifiedSource(MmcArtifactSource Source, string Sha256, string Sha1, long Size);
    private sealed class Group(MmcArtifactReference artifact, string address, string root, string? uriFingerprint) {
        internal MmcArtifactReference Artifact = artifact;
        internal List<MmcArtifactReference> References = [artifact];
        internal string Address = address, Root = root;
        internal string? UriFingerprint = uriFingerprint;
        internal string? Sha256, Sha1;
        internal long? Size;
        internal string? DownloadPath;
        internal SourcePin? Pin;
    }

    public MmcArtifactPrefetchAdapter(MmcArtifactCacheRoots roots, Func<MmcArtifactDownloadSpec, TNetFile> build,
        Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch, MmcArtifactLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(roots); ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(runBatch);
        this.roots = new(NormalizeRoot(roots.LibrariesRoot), NormalizeRoot(roots.AssetsRoot), NormalizeRoot(roots.SourceCacheRoot),
            roots.VersionsRoot is null ? null : NormalizeRoot(roots.VersionsRoot));
        this.build = build; this.runBatch = runBatch; this.limits = limits ?? new();
        if (this.limits.MaxArtifactBytes <= 0 || this.limits.MaxCopiedBytes <= 0 || this.limits.MaxArtifacts <= 0 || this.limits.MaxAssetIndexBytes <= 0 || this.limits.MaxAssetObjects <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<MmcPrefetchedSources> PrefetchAsync(MmcCompiledProfile compiled, MmcArtifactRequest request,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(compiled); ArgumentNullException.ThrowIfNull(request);
        if (!MmcProfileFingerprints.Matches(compiled)) throw new IOException("Compiled profile fingerprints are not canonical.");
        var prior = (request.ExistingLocks.IsDefault ? [] : request.ExistingLocks).ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<string, VerifiedSource>(StringComparer.OrdinalIgnoreCase);
        int batches = 0, requested = 0;
        var freshCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedDirectories = MmcInstancePaths.CreatePassDirectoryCache();
        try {
            var staticArtifacts = compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts)
                .Concat(request.AdditionalPreservedArtifacts.IsDefault ? [] : request.AdditionalPreservedArtifacts).Where(x => x.Scope == MmcArtifactScope.Shared)
                .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            await PrefetchSetAsync(staticArtifacts).ConfigureAwait(false);
            if (compiled.Assets.Artifact is { } index) {
                if (!known.TryGetValue(index.Key, out var parent)) throw new IOException("Asset index was not obtained by the static batch.");
                if (parent.Size > limits.MaxAssetIndexBytes) throw new IOException("Asset index exceeds its metadata budget.");
                byte[] bytes = await ReadIndexAsync(parent.Source, verifiedDirectories, cancellationToken).ConfigureAwait(false);
                var read = MmcAssetIndexReader.Read(new(index.Key, bytes), parent.Sha256, new MmcReadLimits {
                    MaxMetadataBytes = limits.MaxAssetIndexBytes, MaxArchiveEntries = limits.MaxAssetObjects,
                    MaxArchiveBytes = limits.MaxCopiedBytes, MaxEntryBytes = limits.MaxArtifactBytes
                });
                if (read.HasErrors || read.Index is null) throw new IOException("Asset index is not safe for batch prefetch: " + string.Join("; ", read.Diagnostics.Select(x => x.Message)));
                var objects = read.Index.Objects.Select(item => {
                    string hash = item.Sha1;
                    return new MmcArtifactReference(MmcDerivedProofs.ComputeAssetObjectKey(index.Key, parent.Sha256, hash, item.SizeBytes),
                        index.ComponentUid, parent.Sha256, MmcArtifactRole.DownloadOnly, MmcArtifactScope.Shared, null,
                        "objects/" + hash[..2] + "/" + hash, ["https://resources.download.minecraft.net/" + hash[..2] + "/" + hash], hash, null,
                        item.SizeBytes, null, [], new(index.ComponentUid, index.Provenance.ComponentVersion, index.RelativePath,
                            parent.Sha256, "objects." + item.Name, index.Provenance.Position));
                }).ToArray();
                if ((long)staticArtifacts.Length + objects.DistinctBy(x => x.Key, StringComparer.Ordinal).Count() > limits.MaxArtifacts) throw new IOException("Combined prefetch artifact count budget exceeded.");
                await PrefetchSetAsync(objects).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(known.ToImmutableDictionary(x => x.Key, x => x.Value.Source, StringComparer.OrdinalIgnoreCase), batches, requested);
        } finally {
            // The existing batch runner must stop/drain its I/O before returning/throwing.
            // Only this invocation's unpinned fresh candidates can be removed here.
            foreach (string path in freshCandidates.Where(path => !published.Contains(path))) {
                MmcInstancePaths.EnsureNoLinks(path, verifiedDirectories);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        async Task PrefetchSetAsync(IReadOnlyList<MmcArtifactReference> artifacts) {
            if (artifacts.Count > limits.MaxArtifacts) throw new IOException("Prefetch artifact count budget exceeded.");
            var preparedLocations = new (MmcArtifactLock? Adopted, string Address, string Root, string? UriFingerprint)[artifacts.Count];
            if (artifacts.Count > 0) {
                await Task.Run(() => MmcInstancePaths.ForEachBounded(artifacts, (index, artifact, token) => {
                    token.ThrowIfCancellationRequested();
                    if (!MmcArtifactIo.Digest(artifact.SourceRevision, 64) || artifact.Size < 0 || artifact.Size > limits.MaxArtifactBytes)
                        throw new IOException("Prefetch artifact declaration is invalid.");
                    prior.TryGetValue(artifact.Key, out var adopted);
                    if (adopted is not null && !adopted.SourceRevision.Equals(artifact.SourceRevision, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Prefetch cannot replace an adopted source revision.");
                    var location = Address(artifact, verifiedDirectories);
                    preparedLocations[index] = (adopted, location.Address, location.Root, location.UriFingerprint);
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < artifacts.Count; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = artifacts[i];
                var (adopted, address, root, uriFingerprint) = preparedLocations[i];
                if (!groups.TryGetValue(address, out var group)) {
                    group = new(artifact, address, root, uriFingerprint); groups.Add(address, group);
                } else group.References.Add(artifact);
                Merge(group, artifact.Sha256, artifact.Sha1, artifact.Size);
                if (adopted is not null) Merge(group, adopted.AdoptedSha256, null, adopted.SizeBytes);
            }
            var groupList = groups.Values.ToArray();
            var verifiedList = new VerifiedSource?[groupList.Length];
            await Task.Run(() => MmcInstancePaths.ForEachBounded(groupList, (index, group, token) => {
                verifiedList[index] = ResolveExistingGroup(group, token);
            }, cancellationToken), cancellationToken).ConfigureAwait(false);

            var missing = new List<Group>();
            for (int i = 0; i < groupList.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                Group group = groupList[i];
                if (verifiedList[i] is { } verified) {
                    foreach (var item in group.References) known[item.Key] = verified;
                    continue;
                }
                if (group.References.SelectMany(x => x.DownloadUrls.IsDefault ? [] : x.DownloadUrls).Any() == false) throw new IOException("Missing artifact has no fixed download source.");
                if (group.UriFingerprint is not null && group.Pin is null) {
                    if (File.Exists(group.DownloadPath) || Directory.Exists(group.DownloadPath)) throw new IOException("Fresh source candidate unexpectedly exists.");
                    freshCandidates.Add(group.DownloadPath!);
                }
                MmcArtifactIo.Directory(Path.GetDirectoryName(group.DownloadPath)!, verifiedDirectories);
                missing.Add(group);
            }
            if (missing.Count == 0) return;
            long expectedTotal = 0;
            foreach (var group in missing) {
                if (group.Size is { } size && size > limits.MaxCopiedBytes - expectedTotal) throw new IOException("Batch declared byte budget exceeded.");
                expectedTotal += group.Size ?? 0;
            }
            var files = missing.Select(group => build(new(group.Artifact, group.References.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), group.DownloadPath!,
                group.References.SelectMany(x => x.DownloadUrls.IsDefault ? [] : x.DownloadUrls).Distinct(StringComparer.Ordinal).ToImmutableArray(), group.Sha256, group.Sha1, group.Size,
                group.Artifact.Role == MmcArtifactRole.AssetIndex ? Math.Min(limits.MaxAssetIndexBytes, limits.MaxArtifactBytes) : limits.MaxArtifactBytes))).ToImmutableArray();
            cancellationToken.ThrowIfCancellationRequested(); batches++; requested = checked(requested + files.Length);
            await runBatch(files, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var downloaded = new VerifiedSource?[missing.Count];
            await Task.Run(() => MmcInstancePaths.ForEachBounded(missing, (index, group, token) => {
                downloaded[index] = VerifyFile(new(group.Root, group.DownloadPath!), group, verifiedDirectories, token);
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < missing.Count; i++) {
                Group group = missing[i];
                var verified = downloaded[i] ?? throw new IOException("Batch completion did not produce the expected artifact bytes.");
                if (group.UriFingerprint is not null) {
                    var pin = new SourcePin(1, group.Artifact.SourceRevision, group.UriFingerprint, Path.GetFileName(group.DownloadPath!), verified.Sha256, verified.Sha1, verified.Size);
                    SourcePin accepted = PublishPin(group, pin, verifiedDirectories);
                    if (accepted.Sha256 != pin.Sha256 || accepted.Sha1 != pin.Sha1 || accepted.SizeBytes != pin.SizeBytes)
                        throw new IOException("Source scope already pins different bytes; explicit source revision is required.");
                    Merge(group, accepted.Sha256, accepted.Sha1, accepted.SizeBytes);
                    if (accepted.FileName != pin.FileName) {
                        verified = VerifyFile(new(group.Root, SafePath(group.Root, Path.GetRelativePath(group.Root, Path.Combine(Path.GetDirectoryName(group.Address)!, accepted.FileName)), verifiedDirectories)), group, verifiedDirectories, cancellationToken)
                            ?? throw new IOException("Pinned source candidate is not available.");
                    } else published.Add(group.DownloadPath!);
                }
                foreach (var item in group.References) known[item.Key] = verified;
            }
        }

        VerifiedSource? ResolveExistingGroup(Group group, CancellationToken token) {
            VerifiedSource? verified = null;
            foreach (var artifact in group.References) {
                if (!prior.TryGetValue(artifact.Key, out var locked)) continue;
                string expectedObject = "PCL/MmcArtifacts/sha256/" + locked.AdoptedSha256.ToLowerInvariant();
                if (locked.RecoveryObjectRelativePath != expectedObject || !MmcArtifactIo.Digest(locked.AdoptedSha256, 64)) throw new IOException("Adopted recovery object is invalid.");
                verified = VerifyFile(new(request.Paths.InstanceRoot, request.Paths.ResolveRelative(MmcPathRole.Instance, expectedObject, verifiedDirectories)), group,
                    verifiedDirectories, token);
                if (verified is not null) break;
            }
            if (verified is null && group.UriFingerprint is not null) {
                group.Pin = ReadPin(group, verifiedDirectories);
                if (group.Pin is not null) { Merge(group, group.Pin.Sha256, group.Pin.Sha1, group.Pin.SizeBytes); group.DownloadPath = SafePath(group.Root, Path.GetRelativePath(group.Root, Path.Combine(Path.GetDirectoryName(group.Address)!, group.Pin.FileName)), verifiedDirectories); }
            }
            group.DownloadPath ??= group.UriFingerprint is null ? group.Address : SafePath(group.Root,
                Path.GetRelativePath(group.Root, Path.Combine(Path.GetDirectoryName(group.Address)!, "payload-" + Guid.NewGuid().ToString("N") + ".bin")), verifiedDirectories);
            if (verified is null && (group.UriFingerprint is null || group.Pin is not null))
                verified = VerifyFile(new(group.Root, group.DownloadPath), group, verifiedDirectories, token);
            if (verified is null && roots.VersionsRoot is not null && group.Artifact.Role == MmcArtifactRole.MainJar && (group.Sha256 is not null || group.Sha1 is not null)) {
                ValidateSegment(compiled.MinecraftVersion);
                verified = VerifyFile(new(roots.VersionsRoot, SafePath(roots.VersionsRoot, compiled.MinecraftVersion + "/" + compiled.MinecraftVersion + ".jar", verifiedDirectories)), group, verifiedDirectories, token);
            }
            return verified;
        }
    }

    private (string Address, string Root, string? UriFingerprint) Address(MmcArtifactReference artifact, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        if (artifact.Sha256 is null && artifact.Sha1 is null) {
            string uris = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(artifact.DownloadUrls.IsDefault ? [] : artifact.DownloadUrls)));
            return (SafePath(roots.SourceCacheRoot, "by-source/" + artifact.SourceRevision + "/" + uris + "/pin.json", verifiedDirectories), roots.SourceCacheRoot, uris);
        }
        string root = artifact.Role == MmcArtifactRole.AssetIndex || MmcArtifactMaterializer.IsAssetObjectReference(artifact) ? roots.AssetsRoot : roots.LibrariesRoot;
        if (artifact.Role == MmcArtifactRole.LoggingConfiguration) root = roots.SourceCacheRoot;
        return (SafePath(root, artifact.RelativePath, verifiedDirectories), root, null);
    }

    private VerifiedSource? VerifyFile(MmcArtifactSource source, Group group, ConcurrentDictionary<string, byte>? verifiedDirectories, CancellationToken token) {
        if (!File.Exists(source.FilePath)) return null;
        try {
            string file = MmcArtifactIo.AuthorizedSource(source, verifiedDirectories);
            var actual = MmcArtifactIo.HashFile(file, group.Artifact.Role == MmcArtifactRole.AssetIndex ? Math.Min(limits.MaxArtifactBytes, limits.MaxAssetIndexBytes) : limits.MaxArtifactBytes, token, verifiedDirectories: verifiedDirectories);
            if (group.Sha256 is not null && !actual.Sha256.Equals(group.Sha256, StringComparison.OrdinalIgnoreCase) ||
                group.Sha1 is not null && !actual.Sha1.Equals(group.Sha1, StringComparison.OrdinalIgnoreCase) || group.Size is not null && group.Size != actual.Size) return null;
            return new(source, actual.Sha256, actual.Sha1, actual.Size);
        } catch (IOException) { return null; }
    }

    private SourcePin? ReadPin(Group group, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        if (!File.Exists(group.Address)) return null;
        MmcInstancePaths.EnsureNoLinks(group.Address, verifiedDirectories);
        if (new FileInfo(group.Address).Length > 4096) throw new IOException("Source cache pin exceeds its metadata budget.");
        byte[] bytes = File.ReadAllBytes(group.Address);
        if (bytes.Length > 4096) throw new IOException("Source cache pin actual metadata budget exceeded.");
        try {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Source cache pin must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("SchemaVersion" or "SourceRevision" or "UriFingerprint" or "FileName" or "Sha256" or "Sha1" or "SizeBytes")) throw new IOException("Source cache pin has unknown or duplicate fields.");
            var pin = JsonSerializer.Deserialize<SourcePin>(bytes) ?? throw new IOException("Source cache pin is empty.");
            if (pin.SchemaVersion != 1 || pin.SourceRevision != group.Artifact.SourceRevision || pin.UriFingerprint != group.UriFingerprint ||
                !MmcArtifactIo.Digest(pin.Sha256, 64) || !MmcArtifactIo.Digest(pin.Sha1, 40) || pin.SizeBytes < 0 || pin.SizeBytes > limits.MaxArtifactBytes ||
                pin.FileName is null || !pin.FileName.StartsWith("payload-", StringComparison.Ordinal) || !pin.FileName.EndsWith(".bin", StringComparison.Ordinal) ||
                !Guid.TryParseExact(pin.FileName[8..^4], "N", out Guid id) || id == Guid.Empty)
                throw new IOException("Source cache pin does not match its fixed source scope.");
            return pin;
        } catch (JsonException error) { throw new IOException("Source cache pin JSON is invalid.", error); }
    }

    private SourcePin PublishPin(Group group, SourcePin pin, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        MmcArtifactIo.Directory(Path.GetDirectoryName(group.Address)!, verifiedDirectories);
        string temporary = Path.Combine(Path.GetDirectoryName(group.Address)!, ".pin-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                owned = true; file.Write(JsonSerializer.SerializeToUtf8Bytes(pin)); file.Flush(true);
            }
            try { File.Move(temporary, group.Address, false); owned = false; return pin; }
            catch (IOException) when (File.Exists(group.Address)) { return ReadPin(group, verifiedDirectories) ?? throw new IOException("Source cache pin disappeared."); }
        } finally {
            if (owned) { MmcInstancePaths.EnsureNoLinks(temporary, verifiedDirectories); File.Delete(temporary); }
        }
    }

    private async Task<byte[]> ReadIndexAsync(MmcArtifactSource source, ConcurrentDictionary<string, byte>? verifiedDirectories, CancellationToken token) {
        string path = MmcArtifactIo.AuthorizedSource(source, verifiedDirectories);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        if (file.Length > limits.MaxAssetIndexBytes) throw new IOException("Asset index exceeds its metadata budget.");
        using var bytes = new MemoryStream(); byte[] buffer = new byte[32768]; int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
            if (bytes.Length + read > limits.MaxAssetIndexBytes) throw new IOException("Asset index actual metadata budget exceeded.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
    private static void Merge(Group group, string? sha256, string? sha1, long? size) {
        if (sha256 is not null && !MmcArtifactIo.Digest(sha256, 64) || sha1 is not null && !MmcArtifactIo.Digest(sha1, 40) || size < 0)
            throw new IOException("Prefetch expected digest or size is invalid.");
        if (group.Sha256 is not null && sha256 is not null && !group.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) ||
            group.Sha1 is not null && sha1 is not null && !group.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase) || group.Size is not null && size is not null && group.Size != size)
            throw new IOException("Artifacts sharing a source target have conflicting expected contents.");
        group.Sha256 ??= sha256; group.Sha1 ??= sha1; group.Size ??= size;
    }
    private static string NormalizeRoot(string root) {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\?\\", StringComparison.Ordinal) || root.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new IOException("Cache roots must be ordinary absolute paths.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); MmcInstancePaths.EnsureNoLinks(path); return path;
    }
    private static readonly SearchValues<char> InvalidSegmentChars = SearchValues.Create("<>:\"|?*/\\");

    private static string SafePath(string root, string relative, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        if (relative.Contains('\\')) relative = relative.Replace('\\', '/');
        MmcArtifactIo.Relative(relative);
        ReadOnlySpan<char> remaining = relative.AsSpan();
        while (true) {
            int sep = remaining.IndexOf('/');
            if (sep < 0) { ValidateSegment(remaining); break; }
            ValidateSegment(remaining[..sep]);
            remaining = remaining[(sep + 1)..];
        }
        string normalized = OperatingSystem.IsWindows() ? relative.Replace('/', '\\') : relative;
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Cache path escapes its approved root.");
        MmcInstancePaths.EnsureNoLinks(path, verifiedDirectories); return path;
    }
    private static void ValidateSegment(ReadOnlySpan<char> value) {
        if (value.Length is 0 or > 255 || value is "." or ".." || value[^1] is ' ' or '.' || value.IndexOfAny(InvalidSegmentChars) >= 0)
            throw new IOException("Cache path contains an unsafe segment.");
        foreach (char c in value) {
            if (c < 32) throw new IOException("Cache path contains an unsafe segment.");
        }
        int dot = value.IndexOf('.');
        ReadOnlySpan<char> stem = dot >= 0 ? value[..dot] : value;
        bool reserved = stem.Length switch {
            3 => stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                 stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase),
            4 => (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 stem[3] is >= '1' and <= '9' or '¹' or '²' or '³',
            6 => stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (reserved) throw new IOException("Cache path contains a reserved Windows device name.");
    }
}
