using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcArtifactMaterializer {
    private const string AssetObjectKeyPrefix = "PCLLF:asset-object:v1:";

    /// <summary>Download adapters map these source-only references to the existing global assets cache.</summary>
    public static bool IsAssetObjectReference(MmcArtifactReference artifact) => artifact.Key is not null && artifact.Key.StartsWith(AssetObjectKeyPrefix, StringComparison.Ordinal) &&
        artifact.Key.Length == AssetObjectKeyPrefix.Length + 64 && artifact.Key[AssetObjectKeyPrefix.Length..].All(Uri.IsHexDigit) &&
        artifact.Role == MmcArtifactRole.DownloadOnly && artifact.Coordinate is null && artifact.RelativePath.StartsWith("objects/", StringComparison.Ordinal);

    private sealed record AssetEntry(string Name, MmcArtifactReference Object);
    private sealed record AssetPlan(string IndexId, bool Virtual, bool MapToResources,
        ImmutableArray<AssetEntry> Entries, ImmutableArray<MmcArtifactReference> Objects, MmcAssetIndexContent SourceContent);

    private async Task<AssetPlan?> PrepareAssetInputsAsync(MmcAssetIndex? requestedAssets, ImmutableArray<MmcArtifactReference> artifacts,
        Dictionary<string, PreparedInput> inputs, Dictionary<string, MmcArtifactLock> locks, MmcArtifactIntent intent,
        MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io, MmcArtifactSourceAccess access,
        Dictionary<string, MmcArtifactStagedMutation> mutations, IProgress<MmcArtifactProgress>? progress,
        CancellationToken token, Action<MmcArtifactLock>? originalProduced = null, Action<string>? directoryProduced = null) {
        var indexes = artifacts.Where(x => x.Role == MmcArtifactRole.AssetIndex).ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1) throw new IOException("Exactly one effective asset index is required.");
        var index = indexes[0];
        progress?.Report(new("asset-index-preparation", index.Key, 0));
        if (index.Scope != MmcArtifactScope.Shared || !index.RelativePath.StartsWith("indexes/", StringComparison.Ordinal) ||
            !index.RelativePath.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("Asset index location is not canonical.");
        string id = index.RelativePath[8..^5]; MmcArtifactIo.Relative(id);
        paths.ResolveRelative(MmcPathRole.Runtime, "assets-validation/virtual/" + id);
        if (requestedAssets is not null && (requestedAssets.Id != id || requestedAssets.Artifact?.Key != index.Key))
            throw new IOException("Asset index declaration and selected input disagree.");
        PreparedInput source = inputs[index.Key];
        if (source.Lock.SizeBytes > limits.MaxAssetIndexBytes) throw new IOException("Asset index metadata budget exceeded.");
        byte[] json;
        await using (var file = new FileStream(source.OriginalInput, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true)) {
            if (file.Length > limits.MaxAssetIndexBytes) throw new IOException("Asset index metadata budget exceeded.");
            using var memory = new MemoryStream(); byte[] block = new byte[32768]; int read;
            while ((read = await file.ReadAsync(block, token).ConfigureAwait(false)) != 0) {
                if (memory.Length + read > limits.MaxAssetIndexBytes) throw new IOException("Asset index actual metadata budget exceeded.");
                memory.Write(block, 0, read);
            }
            json = memory.ToArray();
        }
        var parsed = MmcAssetIndexReader.Read(new(index.Key, json), source.Lock.AdoptedSha256, new MmcReadLimits {
            MaxMetadataBytes = limits.MaxAssetIndexBytes, MaxArchiveEntries = limits.MaxAssetObjects,
            MaxEntryBytes = limits.MaxArtifactBytes, MaxArchiveBytes = limits.MaxCopiedBytes
        });
        token.ThrowIfCancellationRequested();
        if (parsed.HasErrors || parsed.Index is null) throw new IOException("Asset index cannot be safely compiled: " + string.Join("; ", parsed.Diagnostics.Select(x => x.Code + ": " + x.Message)));
        if (!parsed.Index.Objects.IsEmpty) {
            await Task.Run(() => MmcInstancePaths.ForEachBounded(parsed.Index.Objects, (_, entry, t) => {
                t.ThrowIfCancellationRequested();
                if (entry.Name.Contains('\\')) throw new IOException("Asset logical names must use forward separators.");
                paths.ResolveRelative(MmcPathRole.Runtime, "assets-validation/" + entry.Name);
            }, token), token).ConfigureAwait(false);
        }
        var entries = ImmutableArray.CreateBuilder<AssetEntry>(parsed.Index.Objects.Length);
        var unique = new Dictionary<string, MmcArtifactReference>(StringComparer.Ordinal);
        foreach (var entry in parsed.Index.Objects) {
            token.ThrowIfCancellationRequested();
            string hash = entry.Sha1; long size = entry.SizeBytes;
            if (unique.TryGetValue(hash, out var repeated) && repeated.Size != size) throw new IOException("One asset hash has conflicting declared sizes.");
            if (repeated is null) {
                string key = MmcDerivedProofs.ComputeAssetObjectKey(index.Key, source.Lock.AdoptedSha256, hash, size);
                var provenance = new MmcFieldSource(index.ComponentUid, index.Provenance.ComponentVersion, index.RelativePath,
                    source.Lock.AdoptedSha256, "objects." + entry.Name, index.Provenance.Position);
                repeated = new(key, index.ComponentUid, source.Lock.AdoptedSha256, MmcArtifactRole.DownloadOnly, MmcArtifactScope.Shared, null,
                    "objects/" + hash[..2] + "/" + hash, ["https://resources.download.minecraft.net/" + hash[..2] + "/" + hash], hash, null, size, null, [], provenance);
                unique.Add(hash, repeated);
            }
            entries.Add(new(entry.Name, repeated));
        }
        var plan = new AssetPlan(id, parsed.Index.Virtual, parsed.Index.MapToResources, entries.ToImmutable(), unique.Values.ToImmutableArray(),
            new(index.Key, parsed.Index.ContentSha256, ImmutableArray.CreateRange(json)));
        long adoptedBytes = inputs.Values.Sum(x => x.Lock.SizeBytes);
        progress?.Report(new("asset-object-preparation", index.Key, 0));
        var jobs = new List<(MmcArtifactReference Artifact, MmcArtifactLock? Adopted, long MaximumBytes)>(plan.Objects.Length);
        long plannedBytes = adoptedBytes;
        for (int i = 0; i < plan.Objects.Length; i++) {
            var artifact = plan.Objects[i];
            if (inputs.Count + i >= limits.MaxArtifacts) throw new IOException("Combined library and asset artifact count budget exceeded.");
            locks.TryGetValue(artifact.Key, out var adopted);
            if (adopted is null && intent == MmcArtifactIntent.RepairExisting) throw new IOException("Repair cannot first-adopt an asset object.");
            if (adopted is not null && (adopted.SourceRevision != artifact.SourceRevision || adopted.SizeBytes != artifact.Size)) throw new IOException("Asset object lock disagrees with its index source.");
            if (artifact.Size > limits.MaxCopiedBytes - plannedBytes) throw new IOException("Aggregate asset object byte budget exceeded.");
            plannedBytes = checked(plannedBytes + artifact.Size!.Value);
            if (artifact.Size > limits.MaxArtifactBytes) throw new IOException("Artifact or asset-index byte budget exceeded.");
            jobs.Add((artifact, adopted, limits.MaxArtifactBytes));
        }
        var fastOriginals = await VerifyAdoptedOriginalsAsync(jobs, paths, token).ConfigureAwait(false);
        for (int i = 0; i < jobs.Count; i++) {
            token.ThrowIfCancellationRequested();
            var job = jobs[i];
            if (job.Artifact.Size > limits.MaxCopiedBytes - adoptedBytes) throw new IOException("Aggregate asset object byte budget exceeded.");
            var prepared = fastOriginals[i] ?? await PrepareInputAsync(job.Artifact, job.Adopted, paths, workspace, io, access, mutations, progress, token, originalProduced, directoryProduced).ConfigureAwait(false);
            if (fastOriginals[i] is not null) originalProduced?.Invoke(prepared.Lock);
            adoptedBytes = checked(adoptedBytes + prepared.Lock.SizeBytes);
            if (adoptedBytes > limits.MaxCopiedBytes) throw new IOException("Aggregate asset object actual byte budget exceeded.");
            inputs.Add(job.Artifact.Key, prepared);
        }
        return plan;
    }

    private async Task<ImmutableArray<MmcAssetFileBinding>> StageAssetsAsync(AssetPlan? plan, Dictionary<string, PreparedInput> inputs,
        string viewKey, MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io,
        Dictionary<string, MmcArtifactStagedMutation> mutations, MmcRuntimeTargetClaims targetClaims,
        CancellationToken token) {
        if (plan is null) return [];
        var objectSpecs = new List<(MmcArtifactReference Artifact, PreparedInput Input, string Relative)>(plan.Objects.Length);
        foreach (var artifact in plan.Objects) {
            var input = inputs[artifact.Key];
            string relative = "PCL/MmcRuntime/" + viewKey + "/assets/" + artifact.RelativePath;
            Claim(relative, input.Lock.AdoptedSha256);
            objectSpecs.Add((artifact, input, relative));
        }
        var objectTargets = new string[objectSpecs.Count];
        if (objectSpecs.Count > 0) {
            await Task.Run(() => MmcInstancePaths.ForEachBounded(objectSpecs, (index, item, t) => {
                t.ThrowIfCancellationRequested();
                objectTargets[index] = paths.ResolveRelative(MmcPathRole.Instance, item.Relative);
            }, token), token).ConfigureAwait(false);
        }
        var objectPlans = new List<(MmcArtifactReference Artifact, PreparedInput Input, string Relative, string Target)>(objectSpecs.Count);
        for (int i = 0; i < objectSpecs.Count; i++) {
            var spec = objectSpecs[i];
            objectPlans.Add((spec.Artifact, spec.Input, spec.Relative, objectTargets[i]));
        }
        var entrySpecs = new List<(AssetEntry Entry, PreparedInput Input, string ObjectRelative, string? Relative)>(plan.Entries.Length);
        foreach (var entry in plan.Entries) {
            var input = inputs[entry.Object.Key];
            string objectRelative = viewKey + "/assets/" + entry.Object.RelativePath;
            string? relative = null;
            if (plan.Virtual || plan.MapToResources) {
                relative = plan.Virtual ? "PCL/MmcRuntime/" + viewKey + "/assets/virtual/" + plan.IndexId + "/" + entry.Name :
                    paths.GameRootLeaf + "/resources/" + entry.Name;
                Claim(relative, input.Lock.AdoptedSha256);
            }
            entrySpecs.Add((entry, input, objectRelative, relative));
        }
        var resolvedEntries = new (string ObjectPath, string? Target)[entrySpecs.Count];
        if (entrySpecs.Count > 0) {
            await Task.Run(() => MmcInstancePaths.ForEachBounded(entrySpecs, (index, item, t) => {
                t.ThrowIfCancellationRequested();
                string objectPath = paths.ResolveRelative(MmcPathRole.Runtime, item.ObjectRelative);
                string? target = item.Relative is null ? null : paths.ResolveRelative(MmcPathRole.Instance, item.Relative);
                resolvedEntries[index] = (objectPath, target);
            }, token), token).ConfigureAwait(false);
        }
        var entryPlans = new List<(AssetEntry Entry, PreparedInput Input, string ObjectPath, string? Relative, string? Target)>(entrySpecs.Count);
        for (int i = 0; i < entrySpecs.Count; i++) {
            var spec = entrySpecs[i];
            var (objectPath, target) = resolvedEntries[i];
            entryPlans.Add((spec.Entry, spec.Input, objectPath, spec.Relative, target));
        }
        var prechecked = await InspectExistingTargetsAsync(
            objectPlans.Select(x => (x.Relative, x.Target)).Concat(entryPlans.Where(x => x.Relative is not null).Select(x => (x.Relative!, x.Target!))),
            limits.MaxArtifactBytes, token).ConfigureAwait(false);
        foreach (var item in objectPlans) {
            await StageTargetAsync(item.Input.OriginalInput, item.Relative, item.Target, item.Input.Lock.AdoptedSha256, item.Input.Lock.SizeBytes,
                item.Artifact.Key, paths, workspace, io, mutations, prechecked, token).ConfigureAwait(false);
        }
        var result = ImmutableArray.CreateBuilder<MmcAssetFileBinding>(entryPlans.Count);
        foreach (var item in entryPlans) {
            string? virtualPath = null, mappedPath = null;
            if (item.Relative is not null) {
                await StageTargetAsync(item.Input.OriginalInput, item.Relative, item.Target!, item.Input.Lock.AdoptedSha256, item.Input.Lock.SizeBytes,
                    item.Entry.Object.Key, paths, workspace, io, mutations, prechecked, token).ConfigureAwait(false);
                if (plan.Virtual) virtualPath = item.Target;
                else mappedPath = item.Target;
            }
            result.Add(new(item.Entry.Name, item.Entry.Object.Key, item.Input.ObservedSha1, item.Input.Lock.AdoptedSha256, item.Input.Lock.SizeBytes,
                item.ObjectPath, virtualPath, mappedPath));
        }
        return result.ToImmutable();

        void Claim(string target, string digest) {
            if (targetClaims.TryGetValue(target, out string? previous) && previous != digest) throw new IOException("Asset outputs collide with another runtime input.");
            targetClaims[target] = digest;
        }
    }

}
