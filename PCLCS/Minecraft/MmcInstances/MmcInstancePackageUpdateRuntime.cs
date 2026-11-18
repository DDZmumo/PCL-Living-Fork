using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    private static ImmutableArray<MmcInstanceDefinitionDifference> RuntimeDifferences(MmcCompiledProfile before, MmcCompiledProfile after,
        MmcInstanceDefinition current, ImmutableArray<MmcDefinitionChangeLocalFact> locals) {
        var result = ImmutableArray.CreateBuilder<MmcInstanceDefinitionDifference>();
        void Add(string field, string old, string next) { if (old != next) result.Add(new(field, old, next)); }
        Add("minecraft", before.MinecraftVersion, after.MinecraftVersion); Add("mainClass", before.MainClass, after.MainClass);
        Add("jvmArguments", string.Join("\n", before.JvmArguments.Select(value => value.Value)), string.Join("\n", after.JvmArguments.Select(value => value.Value)));
        Add("gameArguments", string.Join("\n", before.GameArguments.Select(value => value.Value)), string.Join("\n", after.GameArguments.Select(value => value.Value)));
        Add("classpath", string.Join("\n", before.Classpath.Select(value => value.Key)), string.Join("\n", after.Classpath.Select(value => value.Key)));
        Add("natives", string.Join("\n", before.NativeArtifacts.Select(value => value.Key)), string.Join("\n", after.NativeArtifacts.Select(value => value.Key)));
        Add("javaPolicy", System.Text.Json.JsonSerializer.Serialize(before.JavaPolicy), System.Text.Json.JsonSerializer.Serialize(after.JavaPolicy));
        Add("assetIndex", System.Text.Json.JsonSerializer.Serialize(before.Assets), System.Text.Json.JsonSerializer.Serialize(after.Assets));
        var oldPrivate = MmcInstanceHistoryFiles.PrivateLibraries(current); var nextPrivate = locals.ToDictionary(value => "libraries/" + value.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (string path in oldPrivate.Keys.Union(nextPrivate.Keys, StringComparer.OrdinalIgnoreCase)) {
            string old = oldPrivate.TryGetValue(path, out var previous) ? previous.Sha256 : "absent";
            string next = nextPrivate.TryGetValue(path, out var value) ? value.Sha256 : "absent"; Add("private:" + path, old, next);
        }
        return result.ToImmutable();
    }
    private static ImmutableArray<MmcArtifactReference> Active(MmcCompiledProfile compiled) => compiled.Classpath.Concat(compiled.NativeArtifacts)
        .Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts).DistinctBy(value => value.Key, StringComparer.Ordinal).ToImmutableArray();
    private static ImmutableArray<MmcArtifactReference> Preserved(MmcCompiledProfile compiled, MmcValidatedPackageBaseline baseline) {
        var executing = Active(compiled).Where(value => value.Scope == MmcArtifactScope.InstanceLocal).Select(value => value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MmcPackageFilePreservation.CreateLibraryArtifacts(baseline).Where(value => !executing.Contains(value.RelativePath)).ToImmutableArray();
    }
    private static ImmutableArray<MmcDefinitionChangeLocalFact> FixedLocalInputs(MmcInstancePaths currentPaths, MmcInstanceDefinition current,
        MmcInstancePaths candidatePaths, MmcInstanceDefinition candidate, MmcValidatedPackageBaseline baseline, MmcResolvedProfile resolved,
        MmcCompiledProfile compiled, CancellationToken token) {
        var oldLocks = current.ArtifactLocks.ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
        var candidateLocks = candidate.ArtifactLocks.ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
        var oldTargets = MmcInstanceHistoryFiles.PrivateLibraries(current);
        var authored = baseline.Baseline.Files.Where(value => value.Role == PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary && value.TargetRelativePath is not null)
            .ToDictionary(value => value.TargetRelativePath!, StringComparer.OrdinalIgnoreCase);
        var kinds = resolved.Components.ToDictionary(value => value.Uid, value => value.SourceKind, StringComparer.Ordinal);
        var facts = ImmutableArray.CreateBuilder<MmcDefinitionChangeLocalFact>();
        foreach (var group in Active(compiled).Concat(Preserved(compiled, baseline)).Where(value => value.Scope == MmcArtifactScope.InstanceLocal).GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)) {
            List<string> sources = [];
            bool author = group.Any(value => value.PackageFileOrigin is not null || kinds[value.ComponentUid] == MmcComponentSourceKind.PackageLocal);
            if (author && authored.TryGetValue("libraries/" + group.Key, out var original)) sources.Add(candidatePaths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + original.Sha256));
            if (!author) {
                foreach (var item in group) if (oldLocks.TryGetValue(item.Key, out var old)) sources.Add(currentPaths.ResolveRelative(MmcPathRole.Instance, old.RecoveryObjectRelativePath));
                if (oldTargets.TryGetValue("libraries/" + group.Key, out var previous)) sources.Add(currentPaths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + previous.Sha256));
                foreach (var item in group) if (candidateLocks.TryGetValue(item.Key, out var adopted)) sources.Add(candidatePaths.ResolveRelative(MmcPathRole.Instance, adopted.RecoveryObjectRelativePath));
                if (authored.TryGetValue("libraries/" + group.Key, out var fallback)) sources.Add(candidatePaths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + fallback.Sha256));
            }
            MmcDefinitionChangeLocalFact? selected = null;
            foreach (string source in sources.Distinct(StringComparer.OrdinalIgnoreCase)) {
                token.ThrowIfCancellationRequested(); if (!File.Exists(source)) continue;
                var observed = MmcArtifactIo.HashAsync(source, MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
                if (group.Any(value => value.Sha256 is { } sha && sha != observed.Sha256 || value.Sha1 is { } sha1 && sha1 != observed.Sha1 || value.Size is { } size && size != observed.Size)) continue;
                selected = new(group.Key, source, observed.Sha256, observed.Size, observed.Sha1); break;
            }
            if (selected is null) throw new IOException("A retained user/author local input lacks its fixed original bytes: " + group.Key);
            facts.Add(selected);
        }
        return facts.OrderBy(value => value.FileName, StringComparer.Ordinal).ToImmutableArray();
    }
    private static void VerifyFixedLocals(ImmutableArray<MmcDefinitionChangeLocalFact> facts, CancellationToken token) {
        foreach (var value in facts) {
            var observed = MmcArtifactIo.HashAsync(value.AbsolutePath, MmcDurableFiles.MaximumFileBytes, token, value.Sha1).GetAwaiter().GetResult();
            if (observed.Sha256 != value.Sha256 || observed.Size != value.SizeBytes) throw new IOException("A fixed update local input changed after preview.");
        }
    }
    private sealed record InputPool(MmcInstancePaths Paths, ImmutableArray<MmcArtifactLock> Locks);
    private static InputPool PrepareInputPool(MmcInstancePackageUpdatePreview preview, MmcInstancePaths currentPaths,
        MmcInstanceDefinition current, MmcInstancePaths candidatePaths, MmcInstanceDefinition candidate, MmcCompiledProfile compiled, CancellationToken token) {
        preview.Job.Verify(); var paths = MmcInstancePaths.Resolve(Path.Combine(preview.Job.Root, "versions", "runtime-inputs"), preview.Source.Layout.GameRootLeaf);
        MmcArtifactIo.Directory(paths.InstanceRoot); var io = new MmcArtifactIo(new(), null);
        Dictionary<string, (string Sha1, long Size)> objects = new(StringComparer.Ordinal);
        var originSources = MmcPublishedArtifactLocks.Create(current).Select(value => (Root: currentPaths, Lock: value))
            .Concat(MmcPublishedArtifactLocks.Create(candidate).Select(value => (Root: candidatePaths, Lock: value)));
        foreach (var item in originSources) {
            token.ThrowIfCancellationRequested(); preview.Job.Verify(); if (objects.ContainsKey(item.Lock.AdoptedSha256)) continue;
            string source = item.Root.ResolveRelative(MmcPathRole.Instance, item.Lock.RecoveryObjectRelativePath);
            string destination = paths.ResolveRelative(MmcPathRole.Instance, item.Lock.RecoveryObjectRelativePath); MmcArtifactIo.Directory(Path.GetDirectoryName(destination)!);
            var observed = MmcInstancePaths.Attributes(destination) is null
                ? io.CopyAsync(source, destination, item.Lock.ArtifactKey, item.Lock.AdoptedSha256, null, item.Lock.SizeBytes, token).GetAwaiter().GetResult()
                : MmcArtifactIo.HashAsync(destination, MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
            if (observed.Sha256 != item.Lock.AdoptedSha256 || observed.Size != item.Lock.SizeBytes) throw new IOException("Owned update input-pool bytes changed.");
            objects.Add(observed.Sha256, (observed.Sha1, observed.Size));
        }
        var old = current.ArtifactLocks.ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
        var incoming = candidate.ArtifactLocks.ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
        var bySha1 = objects.GroupBy(value => value.Value.Sha1, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var locals = preview.LocalInputs.ToDictionary(value => value.FileName, StringComparer.OrdinalIgnoreCase);
        var locks = new Dictionary<string, MmcArtifactLock>(StringComparer.Ordinal);
        foreach (var artifact in Active(compiled).Concat(Preserved(compiled, MmcSavedSourceStore.LoadAsync(candidatePaths, candidate, token).GetAwaiter().GetResult().Baseline))) {
            string? digest = artifact.Scope == MmcArtifactScope.InstanceLocal && locals.TryGetValue(artifact.RelativePath, out var local) ? local.Sha256 :
                old.TryGetValue(artifact.Key, out var previous) ? previous.AdoptedSha256 : incoming.TryGetValue(artifact.Key, out var adopted) ? adopted.AdoptedSha256 :
                artifact.Sha256 is { } sha && objects.ContainsKey(sha) ? sha : artifact.Sha1 is { } sha1 && bySha1.TryGetValue(sha1, out var by1) ? by1.Key : null;
            if (digest is null) continue;
            if (!objects.TryGetValue(digest, out var measured)) {
                if (!locals.TryGetValue(artifact.RelativePath, out var file)) throw new IOException("A selected original is missing from the owned input pool.");
                string destination = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcArtifacts/sha256/" + digest); MmcArtifactIo.Directory(Path.GetDirectoryName(destination)!);
                var copied = io.CopyAsync(file.AbsolutePath, destination, artifact.Key, file.Sha256, file.Sha1, file.SizeBytes, token).GetAwaiter().GetResult();
                objects[digest] = measured = (copied.Sha1, copied.Size);
            }
            if (artifact.Sha256 is { } expected && digest != expected || artifact.Sha1 is { } expected1 && measured.Sha1 != expected1 || artifact.Size is { } size && measured.Size != size)
                throw new IOException("Preserved current remote/local authority differs from a new same-key declaration.");
            locks[artifact.Key] = new(artifact.Key, artifact.SourceRevision, digest, measured.Size, "PCL/MmcArtifacts/sha256/" + digest);
        }
        if (compiled.Assets.Artifact is { } index && locks.TryGetValue(index.Key, out var indexLock)) {
            byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, indexLock.RecoveryObjectRelativePath, 16 * 1024 * 1024);
            var read = MmcAssetIndexReader.Read(new(index.Key, bytes), indexLock.AdoptedSha256);
            if (read.HasErrors || read.Index is null) throw new IOException("The owned update asset index is invalid.");
            foreach (var item in read.Index.Objects) if (bySha1.TryGetValue(item.Sha1, out var original) && original.Value.Size == item.SizeBytes) {
                string key = MmcDerivedProofs.ComputeAssetObjectKey(index.Key, indexLock.AdoptedSha256, item.Sha1, item.SizeBytes);
                locks[key] = new(key, indexLock.AdoptedSha256, original.Key, item.SizeBytes, "PCL/MmcArtifacts/sha256/" + original.Key);
            }
        }
        return new(paths, locks.Values.ToImmutableArray());
    }
}
