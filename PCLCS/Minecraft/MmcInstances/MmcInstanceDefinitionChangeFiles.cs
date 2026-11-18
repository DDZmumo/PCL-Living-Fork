using System.Collections.Immutable;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceDefinitionChangeBackend<TNetFile> where TNetFile : notnull {
    private static ImmutableArray<MmcHistoryFile> MappedResourceFacts(MmcInstancePaths paths, MmcCompiledProfile compiled,
        MmcPrefetchedSources fetched, CancellationToken token) {
        if (compiled.Assets.Artifact is not { } artifact) return [];
        if (!fetched.Sources.TryGetValue(artifact.Key, out var source)) throw new IOException("The candidate asset index has no fixed prefetched source.");
        string input = MmcArtifactIo.AuthorizedSource(source); byte[] bytes;
        using (FileStream stream = new(input, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (stream.Length > 16 * 1024 * 1024) throw new IOException("Candidate asset index exceeds its metadata budget.");
            using MemoryStream content = new(); byte[] buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) {
                token.ThrowIfCancellationRequested(); if (content.Length + count > 16 * 1024 * 1024) throw new IOException("Candidate asset index grew beyond its metadata budget.");
                content.Write(buffer, 0, count);
            }
            bytes = content.ToArray();
        }
        string indexHash = MmcDurableFiles.Hash(bytes);
        if (artifact.Sha256 is { } expected && indexHash != expected || artifact.Sha1 is { } sha1 && Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)) != sha1 ||
            artifact.Size is { } size && bytes.LongLength != size) throw new IOException("Candidate asset index bytes differ from their declared source.");
        var read = MmcAssetIndexReader.Read(new(artifact.Key, bytes), indexHash);
        if (read.HasErrors || read.Index is null) throw new IOException("Candidate asset index is unsupported: " + string.Join("; ", read.Diagnostics.Select(value => value.Code)));
        if (!read.Index.EffectiveMapToResources) return [];
        var result = ImmutableArray.CreateBuilder<MmcHistoryFile>();
        foreach (var item in read.Index.Objects) {
            string key = MmcDerivedProofs.ComputeAssetObjectKey(artifact.Key, indexHash, item.Sha1, item.SizeBytes);
            if (!fetched.Sources.TryGetValue(key, out var original)) throw new IOException("A mapped resource lacks its exact prefetched object source.");
            var observed = MmcArtifactIo.HashAsync(MmcArtifactIo.AuthorizedSource(original), MmcDurableFiles.MaximumFileBytes, token, item.Sha1).GetAwaiter().GetResult();
            if (observed.Size != item.SizeBytes) throw new IOException("A mapped resource differs from its indexed size.");
            string relative = paths.GameRootLeaf + "/resources/" + item.Name; paths.ResolveRelative(MmcPathRole.Instance, relative);
            result.Add(new(relative, observed.Sha256, observed.Size));
        }
        return result.ToImmutable();
    }
    private static void AssertMappedResourceTargets(MmcInstancePaths paths, MmcInstanceDefinition current, ImmutableArray<MmcHistoryFile> desired) {
        var owned = current.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource &&
            value.RuntimePlanFingerprint == current.InstalledProjection!.RuntimePlanFingerprint)
            .GroupBy(value => value.TargetRelativePath, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in desired) {
            string? actual = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, file.RelativePath));
            if (actual is null || actual == file.Sha256) continue;
            if (!owned.TryGetValue(file.RelativePath, out var previous) || actual != previous.Sha256)
                throw new IOException("A mapped resource target contains unknown or edited player bytes; preserve it before changing components: " + file.RelativePath);
        }
    }
    private static MmcArtifactRequest ArtifactRequest(MmcInstancePaths paths, MmcCompiledProfile compiled, MmcInstanceDefinition current,
        MmcValidatedPackageBaseline baseline, CancellationToken token) {
        var active = compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts)
            .DistinctBy(value => value.Key, StringComparer.Ordinal).ToImmutableArray();
        var local = active.Where(value => value.Scope == MmcArtifactScope.InstanceLocal).Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prior = MmcPublishedArtifactLocks.Create(current).ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
        var bySha256 = current.ArtifactLocks.GroupBy(value => value.AdoptedSha256, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var bySha1 = current.ArtifactLocks.Where(value => value.ObservedSha1 is not null).GroupBy(value => value.ObservedSha1!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        // An exact declared digest may reuse existing measured bytes across metadata revisions.
        // This is a source observation for explicit adoption, never published old-key authority.
        foreach (var artifact in active.Where(value => value.Scope == MmcArtifactScope.Shared && !prior.ContainsKey(value.Key))) {
            var source = artifact.Sha256 is { } sha && bySha256.TryGetValue(sha, out var by256) ? by256 :
                artifact.Sha1 is { } sha1 && bySha1.TryGetValue(sha1, out var by1) ? by1 : null;
            if (source is null || artifact.Size is { } size && source.SizeBytes != size) continue;
            var observed = MmcArtifactIo.HashAsync(paths.ResolveRelative(MmcPathRole.Instance, source.RecoveryObjectRelativePath), MmcDurableFiles.MaximumFileBytes, token, artifact.Sha1).GetAwaiter().GetResult();
            if (observed.Sha256 != source.AdoptedSha256 || observed.Size != source.SizeBytes) throw new IOException("A reused original source is damaged.");
            prior.Add(artifact.Key, new(artifact.Key, artifact.SourceRevision, observed.Sha256, observed.Size, source.RecoveryObjectRelativePath));
        }
        return new(paths, compiled.RuntimePlanFingerprint, active,
            MmcPackageFilePreservation.CreateLibraryArtifacts(baseline).Where(value => !local.Contains("libraries/" + value.RelativePath)).ToImmutableArray(),
            prior.Values.ToImmutableArray(), MmcArtifactIntent.AdoptDeclaredVariant, compiled.Assets);
    }
    private static ImmutableArray<MmcDefinitionChangeLocalFact> LocalFacts(MmcInstanceDefinitionChangeRequest request, MmcArtifactRequest artifacts,
        MmcInstanceDefinition current, MmcValidatedPackageBaseline baseline, MmcResolvedProfile resolved, MmcInstancePaths paths, CancellationToken token) {
        Dictionary<string, MmcInstanceLocalLibraryInput> supplied = new(StringComparer.OrdinalIgnoreCase);
        foreach (var input in request.LocalInputs) {
            if (input is null || input.FileName.Contains('/') || input.FileName.Contains('\\') || !MmcDurableFiles.IsHash(input.ExpectedSha256) || !supplied.TryAdd(input.FileName, input))
                throw new IOException("Local replacement identities/digests are invalid or duplicated.");
            paths.ResolveRelative(MmcPathRole.LocalLibraries, input.FileName);
            MmcArtifactIo.AuthorizedSource(new(Path.GetDirectoryName(Path.GetFullPath(input.AbsolutePath))!, input.AbsolutePath));
        }
        var desired = artifacts.Artifacts.Concat(artifacts.AdditionalPreservedArtifacts).Where(value => value.Scope == MmcArtifactScope.InstanceLocal).ToArray();
        var before = MmcInstanceHistoryFiles.PrivateLibraries(current); var result = ImmutableArray.CreateBuilder<MmcDefinitionChangeLocalFact>();
        var kinds = resolved.Components.ToDictionary(value => value.Uid, value => value.SourceKind, StringComparer.Ordinal);
        var authored = baseline.Baseline.Files.Where(value => value.Role == PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary && value.TargetRelativePath is not null)
            .ToDictionary(value => value.TargetRelativePath!, value => value.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var group in desired.GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)) {
            string fileName = group.Key; List<string> candidates = [];
            if (supplied.TryGetValue(fileName, out var input)) candidates.Add(input.AbsolutePath);
            else {
                bool author = group.Any(value => value.PackageFileOrigin is not null || kinds[value.ComponentUid] == MmcComponentSourceKind.PackageLocal);
                string? original = authored.TryGetValue("libraries/" + fileName, out var authorHash) ? authorHash : null;
                if (author && original is not null) candidates.Add(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + original));
                if (before.TryGetValue("libraries/" + fileName, out var old)) candidates.Add(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + old.Sha256));
                if (!author && original is not null) candidates.Add(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + original));
            }
            MmcDefinitionChangeLocalFact? chosen = null;
            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
                token.ThrowIfCancellationRequested(); if (!File.Exists(path)) continue;
                var observed = MmcArtifactIo.HashAsync(path, MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
                if (input is not null && input.ExpectedSha256 != observed.Sha256 || group.Any(value =>
                    value.Sha256 is { } sha && sha != observed.Sha256 || value.Sha1 is { } sha1 && sha1 != observed.Sha1 || value.Size is { } size && size != observed.Size)) continue;
                chosen = new(fileName, Path.GetFullPath(path), observed.Sha256, observed.Size, observed.Sha1); break;
            }
            if (chosen is null) throw new IOException("No fixed original or explicit replacement can satisfy local input: " + fileName);
            result.Add(chosen); supplied.Remove(fileName);
        }
        if (supplied.Count != 0) throw new IOException("Replacement input is not declared by the candidate: " + supplied.Keys.First());
        return result.OrderBy(value => value.FileName, StringComparer.Ordinal).ToImmutableArray();
    }
    private static void VerifyLocalFacts(ImmutableArray<MmcDefinitionChangeLocalFact> facts, CancellationToken token) {
        foreach (var fact in facts) {
            var observed = MmcArtifactIo.HashAsync(fact.AbsolutePath, MmcDurableFiles.MaximumFileBytes, token, fact.Sha1).GetAwaiter().GetResult();
            if (observed.Sha256 != fact.Sha256 || observed.Size != fact.SizeBytes) throw new IOException("The candidate's fixed local source changed.");
        }
    }
    private static void AssertPrivateTargets(MmcInstancePaths paths, MmcInstanceDefinition current, IReadOnlyDictionary<string, string> desired) {
        var before = MmcInstanceHistoryFiles.PrivateLibraries(current);
        foreach (string relative in before.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase)) {
            string? actual = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, relative)); if (actual is null) continue;
            if (before.TryGetValue(relative, out var old)) { if (actual != old.Sha256) throw new IOException("Current private target contains unknown edited bytes: " + relative); }
            else if (actual != desired[relative]) throw new IOException("New private target contains unknown unowned bytes: " + relative);
        }
    }
    private static IEnumerable<MmcFileMutation> StageSources(MmcInstancePaths paths, MmcInstanceDefinitionChangePreview preview,
        MmcArtifactWorkspace workspace, CancellationToken token) {
        var io = new MmcArtifactIo(new(), null);
        foreach (var value in preview.SourceObjects) {
            string hash = MmcDurableFiles.Hash(value.Value.ToArray()); string? old = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.Key));
            if (old == hash) continue; if (old is not null) throw new IOException("An immutable source/patch object has unknown bytes.");
            string staged = workspace.NewFile(); MmcDurableFiles.WriteNew(staged, value.Value.AsSpan()); yield return new(value.Key, null, null, hash, Relative(paths, staged));
        }
        foreach (var value in preview.SourceCopies) {
            token.ThrowIfCancellationRequested(); string? old = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.TargetRelativePath));
            if (old == value.Sha256) continue; if (old is not null) throw new IOException("An immutable author original/baseline has unknown bytes.");
            string staged = workspace.NewFile(); io.CopyAsync(value.AbsolutePath, staged, value.TargetRelativePath, value.Sha256, null, value.SizeBytes, token).GetAwaiter().GetResult();
            yield return new(value.TargetRelativePath, null, null, value.Sha256, Relative(paths, staged));
        }
    }
    private static IEnumerable<MmcFileMutation> StagePrivate(MmcInstancePaths paths, MmcInstanceDefinition current,
        ImmutableArray<MmcDefinitionChangeLocalFact> desired, MmcArtifactWorkspace workspace, CancellationToken token) {
        var after = desired.ToDictionary(value => "libraries/" + value.FileName, StringComparer.OrdinalIgnoreCase);
        var before = MmcInstanceHistoryFiles.PrivateLibraries(current); var io = new MmcArtifactIo(new(), null);
        foreach (string relative in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)) {
            string? old = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, relative));
            if (!after.TryGetValue(relative, out var next)) { if (old is not null) yield return new(relative, old, null, null); continue; }
            if (old == next.Sha256) continue;
            string staged = workspace.NewFile(); io.CopyAsync(next.AbsolutePath, staged, relative, next.Sha256, next.Sha1, next.SizeBytes, token).GetAwaiter().GetResult();
            yield return new(relative, old, null, next.Sha256, Relative(paths, staged));
        }
    }
    private static MmcFileMutation[] Merge(IEnumerable<MmcFileMutation> values) {
        Dictionary<string, MmcFileMutation> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) {
            if (result.TryGetValue(value.RelativePath, out var old) && (old.ExpectedOldSha256 != value.ExpectedOldSha256 || old.ExpectedNewSha256 != value.ExpectedNewSha256))
                throw new IOException("Revision mutations conflict at one target.");
            result[value.RelativePath] = value; if (result.Count > 100000) throw new IOException("Revision mutation budget exceeded.");
        }
        return result.Values.ToArray();
    }
    private static void SetManifest(MmcConfigurationWriteScope scope, MmcResolvedProfile profile, MmcCompiledProfile compiled, Guid id, long generation) {
        var manifest = scope.ConfigSnapshot["MinecraftInstallManifest"] as JObject ?? new(); JObject loaders = new();
        foreach (var component in profile.Components.Where(value => value.Enabled && value.Version is not null)) {
            string? key = component.Uid switch { "net.minecraftforge" => "Forge", "net.neoforged" => "NeoForge", "net.fabricmc.fabric-loader" => "Fabric", "com.mumfrey.liteloader" => "LiteLoader", _ => null };
            if (key is not null) loaders[key] = component.Version;
        }
        manifest["Components"] = new JObject { ["MinecraftVersion"] = compiled.MinecraftVersion, ["ModLoaderVersions"] = loaders };
        manifest["ManagedFiles"] = new JArray(); manifest["LastModifiedUtc"] = DateTime.UtcNow; manifest["SchemaVersion"] = 1;
        var hint = JObject.Parse(MmcVersionProjection.ManagementHint(id, generation, compiled).ToJsonString()); manifest["pcllfManagement"] = hint;
        scope.SetConfig("MinecraftInstallManifest", manifest); var config = scope.ConfigSnapshot;
        if (config.ContainsKey("pcllfManagement")) scope.SetConfig("pcllfManagement", hint);
        if (config.ContainsKey("generation")) scope.SetConfig("generation", generation);
        if (LegacyBaseOperations.ParseIni(scope.IniSnapshotForRelocation).ContainsKey("MmcDefinitionGeneration")) scope.SetIni("MmcDefinitionGeneration", generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.SetIni("VersionArgumentIndieV2", "True");
    }
    private static ImmutableArray<MmcInstanceDefinitionDifference> Differences(MmcResolvedProfile previous, MmcCompiledProfile before, MmcResolvedProfile candidate, MmcCompiledProfile after) {
        var changes = ImmutableArray.CreateBuilder<MmcInstanceDefinitionDifference>();
        void Add(string field, string old, string next) { if (old != next) changes.Add(new(field, old, next)); }
        foreach (string uid in previous.Components.Select(value => value.Uid).Union(candidate.Components.Select(value => value.Uid))) {
            string Description(MmcResolvedComponent? value) => value is null ? "absent" : $"{value.Enabled}/{value.Version}/{value.SourceKind}/{value.Patch.ContentSha256}";
            Add("component:" + uid, Description(previous.Components.FirstOrDefault(value => value.Uid == uid)), Description(candidate.Components.FirstOrDefault(value => value.Uid == uid)));
        }
        Add("mainClass", before.MainClass, after.MainClass);
        Add("minecraft", before.MinecraftVersion, after.MinecraftVersion);
        Add("mainJar", before.MainJar.Key, after.MainJar.Key);
        Add("jvmArguments", string.Join("\n", before.JvmArguments.Select(value => value.Value)), string.Join("\n", after.JvmArguments.Select(value => value.Value)));
        Add("gameArguments", string.Join("\n", before.GameArguments.Select(value => value.Value)), string.Join("\n", after.GameArguments.Select(value => value.Value)));
        Add("tweakers", string.Join("\n", before.Tweakers), string.Join("\n", after.Tweakers));
        Add("classpath", string.Join("\n", before.Classpath.Select(value => value.Key)), string.Join("\n", after.Classpath.Select(value => value.Key)));
        Add("natives", string.Join("\n", before.NativeArtifacts.Select(value => value.Key)), string.Join("\n", after.NativeArtifacts.Select(value => value.Key)));
        Add("javaPolicy", System.Text.Json.JsonSerializer.Serialize(before.JavaPolicy), System.Text.Json.JsonSerializer.Serialize(after.JavaPolicy));
        Add("environment", before.Environment.ToString(), after.Environment.ToString());
        return changes.ToImmutable();
    }
}
