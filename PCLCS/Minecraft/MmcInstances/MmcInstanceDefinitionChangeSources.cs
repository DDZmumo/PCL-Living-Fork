using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstanceDefinitionChangeSources {
    internal static (MmcInstanceDefinition Source, ImmutableDictionary<string, ImmutableArray<byte>> Objects, ImmutableArray<MmcDefinitionChangeSourceCopy> Copies)
        Build(MmcInstancePaths paths, MmcInstanceDefinition current, MmcSavedInstanceSources saved,
        MmcResolvedSourceBundle resolved, ImmutableArray<MmcComponentOverride> overrides, CancellationToken token) {
        Dictionary<string, (MmcSourceMetadataRecord Record, byte[] Bytes)> inputs = new(StringComparer.Ordinal);
        long total = 0;
        void Add(MmcSourceMetadataRecord record, byte[] bytes) {
            token.ThrowIfCancellationRequested(); total = checked(total + bytes.Length);
            if (total > 128L * 1024 * 1024 || MmcDurableFiles.Hash(bytes) != record.ContentSha256) throw new IOException("Revision source metadata budget/digest is invalid.");
            string key = record.Kind + ":" + record.RelativePath;
            if (inputs.TryGetValue(key, out var old)) { if (old.Record != record || !old.Bytes.SequenceEqual(bytes)) throw new IOException("Revision source identities conflict."); return; }
            inputs.Add(key, (record, bytes));
        }
        foreach (var record in current.SourceBundle.MetadataRecords) Add(record, MmcSavedSourceStore.ReadBounded(paths, record.ObjectRelativePath, 16 * 1024 * 1024));
        foreach (var snapshot in resolved.Snapshots) {
            string sha = MmcDurableFiles.Hash(Encoding.UTF8.GetBytes(snapshot.ContentUtf8));
            var old = current.SourceBundle.MetadataRecords.FirstOrDefault(value => value.Uid == snapshot.Uid &&
                (value.Kind == MmcSourceRecordKind.RecommendationIndex ? snapshot.Kind == MmcMetadataRequestKind.Recommendation :
                    value.ComponentSourceKind == MmcComponentSourceKind.RemoteMetadata && value.Version == snapshot.Version) && value.SourceIdentity == snapshot.Source);
            if (old is not null) { if (old.ContentSha256 != sha) throw new IOException("An adopted metadata identity changed bytes."); continue; }
            string key = MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { snapshot.Uid, snapshot.Version, snapshot.Kind, snapshot.Source }));
            Add(new("remote/" + key + ".json", sha, "", snapshot.Kind == MmcMetadataRequestKind.Recommendation ? MmcSourceRecordKind.RecommendationIndex : MmcSourceRecordKind.Component,
                snapshot.Uid, snapshot.Kind == MmcMetadataRequestKind.Recommendation ? null : snapshot.Version,
                snapshot.Kind == MmcMetadataRequestKind.Recommendation ? null : MmcComponentSourceKind.RemoteMetadata, snapshot.Source), Encoding.UTF8.GetBytes(snapshot.ContentUtf8));
        }
        var ordered = inputs.Values.OrderBy(value => value.Record.Kind).ThenBy(value => value.Record.RelativePath, StringComparer.Ordinal).ToArray();
        string bundle = MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcSourceBundle/v1", current.Origin.PackageSha256,
            current.SourceBundle.RelativeRoot, Inputs = ordered.Select(value => new { value.Record.RelativePath, value.Record.Kind, value.Record.Uid, value.Record.Version,
                SourceKind = value.Record.ComponentSourceKind, value.Record.SourceIdentity, Sha256 = value.Record.ContentSha256 }) }));
        string prefix = "PCL/MmcSource/" + bundle + "/";
        var objects = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var records = ImmutableArray.CreateBuilder<MmcSourceMetadataRecord>();
        foreach (var input in ordered) { string path = prefix + "objects/" + input.Record.ContentSha256; objects[path] = input.Bytes.ToImmutableArray(); records.Add(input.Record with { ObjectRelativePath = path }); }
        var storedOverrides = ImmutableArray.CreateBuilder<MmcStoredComponentOverride>();
        foreach (var change in overrides.OrderBy(value => value.Uid, StringComparer.Ordinal)) {
            MmcObjectReference? patch = null;
            if (change.PatchContentUtf8 is { } text) {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(text); total = checked(total + bytes.Length);
                if (bytes.Length > 16 * 1024 * 1024 || total > 128L * 1024 * 1024) throw new IOException("Revision patch budget exceeded.");
                string sha = MmcDurableFiles.Hash(bytes), path = "PCL/MmcOverrides/" + sha + ".json"; objects[path] = bytes.ToImmutableArray(); patch = new(sha, path);
            }
            storedOverrides.Add(new(change.Uid, change.Enabled, change.Version, patch));
        }
        var baseline = current.AuthorFileBaseline! with { ObjectRelativePath = prefix + "author/" + current.AuthorFileBaseline!.ContentSha256 + ".json" };
        var copies = ImmutableArray.CreateBuilder<MmcDefinitionChangeSourceCopy>();
        copies.Add(new(paths.ResolveRelative(MmcPathRole.Instance, current.AuthorFileBaseline.ObjectRelativePath), baseline.ObjectRelativePath,
            baseline.ContentSha256, new FileInfo(paths.ResolveRelative(MmcPathRole.Instance, current.AuthorFileBaseline.ObjectRelativePath)).Length));
        foreach (var file in saved.Baseline.Baseline.Files.Where(value => value.TargetRelativePath is null && value.Role != PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary &&
            !current.SourceBundle.MetadataRecords.Any(record => current.SourceBundle.RelativeRoot + record.RelativePath == value.RelativePath)))
            copies.Add(new(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSource/" + current.SourceBundle.BundleSha256 + "/package-files/" + file.Sha256), prefix + "package-files/" + file.Sha256, file.Sha256, file.SizeBytes));
        var source = new MmcInstanceDefinition(current.SchemaVersion, current.ManagementKind, current.FormatProfile, current.InstanceId, current.Generation,
            current.Origin, new MmcSourceBundle(current.SourceBundle.RelativeRoot, bundle, resolved.Resolution.Profile!.SourceFingerprint, records.ToImmutable()) {
                PackageLocalScopeSha256 = resolved.Resolution.Profile.Package.PackageLocalScopeSha256
            }, current.Layout,
            storedOverrides.ToImmutable(), [], [], ParentInstanceId: current.ParentInstanceId, AuthorFileBaseline: baseline) {
                PackageOwnershipReference = current.PackageOwnershipReference
            };
        return (source, objects.ToImmutable(), copies.DistinctBy(value => value.TargetRelativePath, StringComparer.Ordinal).ToImmutableArray());
    }

    internal static string Fingerprint(MmcInstanceDefinitionChangeRequest selection, ImmutableArray<MmcComponentOverride> overrides,
        ImmutableArray<MmcMetadataSnapshot> snapshots, ImmutableArray<MmcDefinitionChangeLocalFact> locals, MmcInstanceDefinition source,
        ImmutableDictionary<string, ImmutableArray<byte>> objects, ImmutableArray<MmcDefinitionChangeSourceCopy> copies) =>
        MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcDefinitionCandidate/v1", selection.VersionRoot,
            selection.ExpectedInstanceId, selection.ExpectedGeneration, selection.ExpectedPrimarySha256, selection.Environment,
            Edits = selection.Edits.OrderBy(value => value.Uid, StringComparer.Ordinal),
            Inputs = selection.LocalInputs.OrderBy(value => value.FileName, StringComparer.Ordinal),
            Overrides = overrides.OrderBy(value => value.Uid, StringComparer.Ordinal),
            Snapshots = snapshots.OrderBy(value => value.Uid, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal).ThenBy(value => value.Kind),
            Locals = locals.OrderBy(value => value.FileName, StringComparer.Ordinal), Source = source,
            Objects = objects.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { Path = value.Key, Sha256 = MmcDurableFiles.Hash(value.Value.ToArray()) }),
            Copies = copies.OrderBy(value => value.TargetRelativePath, StringComparer.Ordinal) }));
}
