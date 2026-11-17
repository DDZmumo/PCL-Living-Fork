using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>Owned preparation only; publication belongs to the instance file transaction.</summary>
public sealed class MmcPreparedSourceBundle : IDisposable {
    private readonly MmcArtifactWorkspace workspace;
    private readonly MmcInstancePaths paths;
    private readonly List<MmcFileMutation> mutations = [];
    public MmcSourceBundle Bundle { get; }
    public IReadOnlyList<MmcFileMutation> Mutations => mutations.AsReadOnly();
    public string StageRoot => workspace.Root;

    internal MmcPreparedSourceBundle(MmcInstancePaths paths, MmcSourceBundle bundle) {
        this.paths = paths; Bundle = bundle; workspace = new(paths);
    }

    internal void Stage(string relativePath, byte[] content, string digest) {
        string target = paths.ResolveRelative(MmcPathRole.Instance, relativePath);
        string? old = MmcDurableFiles.Observe(target);
        if (old is not null) {
            if (old != digest) throw new IOException("Preserved source metadata was changed; it cannot be silently re-adopted.");
            return;
        }
        string staged = workspace.NewFile();
        MmcDurableFiles.WriteNew(staged, content);
        mutations.Add(new(relativePath, null, null, digest, Path.GetRelativePath(paths.InstanceRoot, staged).Replace('\\', '/')));
    }

    public MmcExternalBundleReference StageAuthorBaseline(byte[] content, int fileCount) {
        if (fileCount < 0 || fileCount > 100000 || content.Length > 128 * 1024 * 1024)
            throw new InvalidDataException("Author file baseline exceeds its external-bundle budget.");
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        string relative = "PCL/MmcSource/" + Bundle.BundleSha256 + "/author/" + hash + ".json";
        Stage(relative, content, hash);
        return new(hash, relative, fileCount);
    }
    /// <summary>Keep opaque/foreign/root-external originals without importing their settings or giving them execution authority.</summary>
    public void StageOpaqueOriginals(MmcPreparedPackageFiles files) {
        HashSet<string> seen = mutations.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Files.Where(file => file.TargetRelativePath is null && file.Role != MmcPackageFileRole.LocalLibrary && file.StagedPath is not null)) {
            string relative = "PCL/MmcSource/" + Bundle.BundleSha256 + "/package-files/" + file.Sha256;
            if (!seen.Add(relative)) continue;
            string? old = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, relative));
            if (old is not null) { if (old != file.Sha256) throw new IOException("Opaque package source was changed."); continue; }
            mutations.Add(new(relative, null, null, file.Sha256, Path.GetRelativePath(paths.InstanceRoot, file.StagedPath!).Replace('\\', '/')));
        }
    }
    public void Dispose() => workspace.Dispose();
}

public static class MmcSourceBundleWriter {
    private sealed record Input(string RelativePath, byte[] Content, MmcSourceRecordKind Kind,
        string? Uid, string? Version, MmcComponentSourceKind? SourceKind, string? SourceIdentity);

    public static MmcPreparedSourceBundle Prepare(MmcArchiveSource source, MmcResolvedSourceBundle resolved,
        MmcInstancePaths paths, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(paths);
        if (!resolved.Resolution.IsComplete || resolved.Resolution.Profile is null)
            throw new InvalidDataException("Only a complete source resolution can be preserved for publication.");
        List<Input> inputs = [];
        foreach (MmcArchiveMetadata metadata in source.Metadata) {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.RelativePath.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase))
                inputs.Add(new(metadata.RelativePath, metadata.Bytes.ToArray(), MmcSourceRecordKind.Manifest, null, null, null, null));
            else if (metadata.RelativePath.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase))
                inputs.Add(new(metadata.RelativePath, metadata.Bytes.ToArray(), MmcSourceRecordKind.Configuration, null, null, null, null));
            else {
                string uid = Path.GetFileNameWithoutExtension(metadata.RelativePath);
                MmcResolvedComponent? component = resolved.Resolution.Profile.Components.FirstOrDefault(c => c.Uid == uid && c.SourceKind == MmcComponentSourceKind.PackageLocal);
                inputs.Add(component is null
                    ? new(metadata.RelativePath, metadata.Bytes.ToArray(), MmcSourceRecordKind.PackageMetadata, null, null, null, "package:" + metadata.RelativePath)
                    : new(metadata.RelativePath, metadata.Bytes.ToArray(), MmcSourceRecordKind.Component,
                        uid, component.Patch.Version, MmcComponentSourceKind.PackageLocal, "package:" + metadata.RelativePath));
            }
        }
        foreach (MmcMetadataSnapshot snapshot in resolved.Snapshots) {
            cancellationToken.ThrowIfCancellationRequested();
            string key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { snapshot.Uid, snapshot.Version, snapshot.Kind, snapshot.Source })));
            inputs.Add(new("remote/" + key + ".json", new UTF8Encoding(false, true).GetBytes(snapshot.ContentUtf8),
                snapshot.Kind == MmcMetadataRequestKind.Recommendation ? MmcSourceRecordKind.RecommendationIndex : MmcSourceRecordKind.Component,
                snapshot.Uid, snapshot.Kind == MmcMetadataRequestKind.Recommendation ? null : snapshot.Version,
                snapshot.Kind == MmcMetadataRequestKind.Recommendation ? null : MmcComponentSourceKind.RemoteMetadata, snapshot.Source));
        }
        long total = 0;
        var ordered = inputs.OrderBy(i => i.Kind).ThenBy(i => i.RelativePath, StringComparer.Ordinal).ToArray();
        var digests = new Dictionary<Input, string>();
        foreach (Input input in ordered) {
            total = checked(total + input.Content.LongLength);
            if (input.Content.Length > 16 * 1024 * 1024 || total > 128L * 1024 * 1024) throw new InvalidDataException("Preserved metadata byte budget exceeded.");
            digests.Add(input, Convert.ToHexStringLower(SHA256.HashData(input.Content)));
        }
        string bundleHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Protocol = "PCLLF.MmcSourceBundle/v1", source.PackageSha256, source.Package.RelativeRoot,
            Inputs = ordered.Select(input => new { input.RelativePath, input.Kind, input.Uid, input.Version,
                input.SourceKind, input.SourceIdentity, Sha256 = digests[input] })
        })));
        ImmutableArray<MmcSourceMetadataRecord> records = ordered.Select(input => new MmcSourceMetadataRecord(input.RelativePath,
            digests[input], "PCL/MmcSource/" + bundleHash + "/objects/" + digests[input], input.Kind,
            input.Uid, input.Version, input.SourceKind, input.SourceIdentity)).ToImmutableArray();
        MmcPreparedSourceBundle prepared = new(paths, new(source.Package.RelativeRoot, bundleHash,
            resolved.Resolution.Profile.SourceFingerprint, records));
        try {
            HashSet<string> staged = new(StringComparer.Ordinal);
            foreach (Input input in ordered) {
                cancellationToken.ThrowIfCancellationRequested();
                string target = "PCL/MmcSource/" + bundleHash + "/objects/" + digests[input];
                if (staged.Add(target)) prepared.Stage(target, input.Content, digests[input]);
            }
            return prepared;
        } catch { prepared.Dispose(); throw; }
    }
}
