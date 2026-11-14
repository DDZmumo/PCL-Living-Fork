using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>
/// Builds verified original objects and an owned staging plan. It never publishes local libraries,
/// runtime views or managed definitions. The caller must commit through the instance transaction
/// executor, under its structural/session gate, then revalidate actual inputs before launch.
/// </summary>
public sealed partial class MmcArtifactMaterializer {
    private readonly MmcArtifactLimits limits;
    public MmcArtifactMaterializer(MmcArtifactLimits? limits = null) {
        this.limits = limits ?? new();
        if (this.limits.MaxArtifactBytes <= 0 || this.limits.MaxCopiedBytes <= 0 || this.limits.MaxArtifacts <= 0 ||
            this.limits.MaxNativeEntries <= 0 || this.limits.MaxNativeBytes <= 0 || this.limits.MaxAssetObjects <= 0 || this.limits.MaxAssetIndexBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<MmcPreparedArtifacts> PrepareAsync(MmcArtifactRequest request, MmcArtifactSourceAccess? access = null,
        CancellationToken cancellationToken = default, IProgress<MmcArtifactProgress>? progress = null) {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.LogicalContext) || request.LogicalContext.Length > 4096) throw new ArgumentException("A bounded logical compiler/runtime context is required.");
        cancellationToken.ThrowIfCancellationRequested();
        access ??= new();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(request.Paths.InstanceRoot, request.Paths.GameRootLeaf);
        var artifacts = request.Artifacts.IsDefault ? ImmutableArray<MmcArtifactReference>.Empty : request.Artifacts;
        if (request.Assets is not null && request.Assets.Artifact is null) throw new IOException("Asset index metadata must be resolved before materialization.");
        if (request.Assets?.Artifact is { } requiredIndex && !artifacts.Any(x => x.Key == requiredIndex.Key)) artifacts = artifacts.Add(requiredIndex);
        var preserved = request.AdditionalPreservedArtifacts.IsDefault ? ImmutableArray<MmcArtifactReference>.Empty : request.AdditionalPreservedArtifacts;
        if ((long)artifacts.Length + preserved.Length > limits.MaxArtifacts || (!request.ExistingLocks.IsDefault && request.ExistingLocks.Length > limits.MaxArtifacts))
            throw new IOException("Artifact count budget exceeded.");
        var locks = new Dictionary<string, MmcArtifactLock>(StringComparer.Ordinal);
        foreach (var value in request.ExistingLocks.IsDefault ? [] : request.ExistingLocks) {
            ValidateLock(value);
            if (!locks.TryAdd(value.ArtifactKey, value)) throw new IOException("Duplicate adopted artifact key.");
        }
        var workspace = new MmcArtifactWorkspace(paths);
        var verifiedDirectories = MmcInstancePaths.CreatePassDirectoryCache();
        try {
            var io = new MmcArtifactIo(limits, progress);
            var mutations = new Dictionary<string, MmcArtifactStagedMutation>(StringComparer.OrdinalIgnoreCase);
            var inputs = new Dictionary<string, PreparedInput>(StringComparer.Ordinal);
            var inputJobs = new List<(MmcArtifactReference Artifact, MmcArtifactLock? Adopted, long MaximumBytes)>();
            var queuedKeys = new Dictionary<string, MmcArtifactReference>(StringComparer.Ordinal);
            long plannedBytes = 0;
            foreach (var artifact in artifacts.Concat(preserved)) {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateReference(artifact);
                if (queuedKeys.TryGetValue(artifact.Key, out var old)) {
                    if (ReferenceFingerprint(old) != ReferenceFingerprint(artifact)) throw new IOException("An artifact key has inconsistent declarations.");
                    continue;
                }
                queuedKeys.Add(artifact.Key, artifact);
                locks.TryGetValue(artifact.Key, out var adopted);
                if (adopted is not null && adopted.SourceRevision != artifact.SourceRevision) throw new IOException("Artifact source revision disagrees with its adopted lock.");
                if (adopted is not null && ((artifact.Sha256 is not null && !artifact.Sha256.Equals(adopted.AdoptedSha256, StringComparison.OrdinalIgnoreCase)) ||
                    (artifact.Size is not null && artifact.Size != adopted.SizeBytes))) throw new IOException("Declared artifact metadata disagrees with its adopted lock.");
                if (adopted is null && request.Intent == MmcArtifactIntent.RepairExisting) throw new IOException("Repair cannot adopt an unrecorded artifact: " + artifact.Key);
                long? declaredBytes = adopted?.SizeBytes ?? artifact.Size;
                if (declaredBytes is not null) {
                    if (declaredBytes > limits.MaxCopiedBytes - plannedBytes) throw new IOException("Aggregate artifact byte budget exceeded.");
                    plannedBytes = checked(plannedBytes + declaredBytes.Value);
                }
                long maximum = artifact.Role == MmcArtifactRole.AssetIndex ? Math.Min(limits.MaxAssetIndexBytes, limits.MaxArtifactBytes) : limits.MaxArtifactBytes;
                if (declaredBytes > maximum) throw new IOException("Artifact or asset-index byte budget exceeded.");
                inputJobs.Add((artifact, adopted, maximum));
            }
            var fastOriginals = await VerifyAdoptedOriginalsAsync(inputJobs, paths, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            long adoptedBytes = 0;
            for (int i = 0; i < inputJobs.Count; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                var job = inputJobs[i];
                long? declaredBytes = job.Adopted?.SizeBytes ?? job.Artifact.Size;
                if (declaredBytes is not null && declaredBytes > limits.MaxCopiedBytes - adoptedBytes) throw new IOException("Aggregate artifact byte budget exceeded.");
                var prepared = fastOriginals[i] ?? await PrepareInputAsync(job.Artifact, job.Adopted, paths, workspace, io, access, mutations, progress, verifiedDirectories, cancellationToken).ConfigureAwait(false);
                adoptedBytes = checked(adoptedBytes + prepared.Lock.SizeBytes);
                if (adoptedBytes > limits.MaxCopiedBytes) throw new IOException("Aggregate artifact actual byte budget exceeded.");
                inputs.Add(job.Artifact.Key, prepared);
            }
            var assetPlan = await PrepareAssetInputsAsync(request.Assets, artifacts, inputs, locks, request.Intent,
                paths, workspace, io, access, mutations, progress, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            string viewKey = ViewKey(request.LogicalContext, artifacts.Select(x => inputs[x.Key]).Concat(
                assetPlan is null ? [] : assetPlan.Objects.Select(x => inputs[x.Key])));
            progress?.Report(new("runtime-view-materialization", viewKey, 0));
            var bindings = ImmutableArray.CreateBuilder<MmcArtifactBinding>();
            var targetClaims = new MmcRuntimeTargetClaims(2L * (limits.MaxArtifacts + (long)limits.MaxNativeEntries + limits.MaxAssetObjects));
            var bindingSpecs = new List<(MmcArtifactReference Artifact, PreparedInput Input, string Relative)>(artifacts.Length);
            foreach (var artifact in artifacts) {
                PreparedInput input = inputs[artifact.Key];
                string relative = TargetRelative(input.Artifact, viewKey);
                if (targetClaims.TryGetValue(relative, out string? previousDigest) && previousDigest != input.Lock.AdoptedSha256)
                    throw new IOException("Different artifacts collide at a runtime target.");
                targetClaims[relative] = input.Lock.AdoptedSha256;
                bindingSpecs.Add((artifact, input, relative));
            }
            var bindingTargets = new string[bindingSpecs.Count];
            if (bindingSpecs.Count > 0) {
                await Task.Run(() => MmcInstancePaths.ForEachBounded(bindingSpecs, (index, item, t) => {
                    t.ThrowIfCancellationRequested();
                    bindingTargets[index] = paths.ResolveRelative(MmcPathRole.Instance, item.Relative, verifiedDirectories);
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            var plannedBindings = new List<(MmcArtifactReference Artifact, PreparedInput Input, string Relative, string Target)>(bindingSpecs.Count);
            for (int i = 0; i < bindingSpecs.Count; i++) {
                var spec = bindingSpecs[i];
                plannedBindings.Add((spec.Artifact, spec.Input, spec.Relative, bindingTargets[i]));
            }
            var existingTargets = await InspectExistingTargetsAsync(
                plannedBindings.Select(item => (item.Relative, item.Target)), limits.MaxArtifactBytes, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            foreach (var item in plannedBindings) {
                string verified = await StageTargetAsync(item.Input.OriginalInput, item.Relative, item.Target, item.Input.Lock.AdoptedSha256,
                    item.Input.Lock.SizeBytes, item.Artifact.Key, paths, workspace, io, mutations, existingTargets, verifiedDirectories, cancellationToken).ConfigureAwait(false);
                bindings.Add(new(item.Artifact, item.Input.Lock.AdoptedSha256, item.Input.Lock.SizeBytes, item.Relative, item.Target, verified, item.Input.ObservedSha1));
            }
            var nativeInputs = artifacts.Where(x => x.Role == MmcArtifactRole.Native).Select(x => inputs[x.Key]).ToArray();
            var cachedNativeFiles = await ReuseNativeCacheAsync(request.TrustedNativeCache, nativeInputs, viewKey, paths, targetClaims, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            var nativeFiles = cachedNativeFiles ?? await ExtractNativesAsync(nativeInputs,
                viewKey, paths, workspace, io, mutations, targetClaims, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            ValidateNativeTree(nativeFiles, viewKey, paths, verifiedDirectories, cancellationToken);
            if (assetPlan is not null) progress?.Report(new("asset-view-materialization", viewKey, 0));
            var assetFiles = await StageAssetsAsync(assetPlan, inputs, viewKey, paths, workspace, io, mutations, targetClaims, verifiedDirectories, cancellationToken).ConfigureAwait(false);
            string? assetsRoot = assetPlan is null ? null : paths.ResolveRelative(MmcPathRole.Runtime, viewKey + "/assets", verifiedDirectories);
            string? mappedRoot = assetPlan is { MapToResources: true, Virtual: false } ? paths.ResolveRelative(MmcPathRole.Game, "resources", verifiedDirectories) : null;
            string? gameAssetsRoot = assetPlan is null ? null : mappedRoot ?? paths.ResolveRelative(MmcPathRole.Runtime, viewKey + "/assets/virtual/" + assetPlan.IndexId, verifiedDirectories);
            return new(workspace, viewKey, bindings.ToImmutable(), inputs.Values.Select(x => x.Lock).ToImmutableArray(),
                mutations.Values.ToImmutableArray(), true, true,
                nativeFiles.IsEmpty ? null : paths.ResolveRelative(MmcPathRole.Runtime, viewKey + "/natives", verifiedDirectories), assetsRoot,
                nativeFiles, assetFiles, gameAssetsRoot, mappedRoot,
                inputs.Values.Select(x => new MmcArtifactObservation(x.Artifact.Key, x.Lock.AdoptedSha256, x.ObservedSha1, x.Lock.SizeBytes)).ToImmutableArray(),
                nativeInputs.Length == 0 ? null : new(viewKey, NativeSourceSet(nativeInputs), nativeFiles.Select(x => new MmcNativeFileFact(x.SourceArtifactKey, x.RelativePath, x.Sha256, x.SizeBytes)).ToImmutableArray()),
                cachedNativeFiles is not null, assetPlan is null ? [] : [assetPlan.SourceContent]);
        } catch (Exception failure) {
            try { workspace.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Artifact preparation failed; owned workspace cleanup also failed.", failure, cleanup); }
            throw;
        }
    }

    private sealed record PreparedInput(MmcArtifactReference Artifact, MmcArtifactLock Lock, string OriginalInput, string ObservedSha1);

    private static async Task<PreparedInput?[]> VerifyAdoptedOriginalsAsync(
        IReadOnlyList<(MmcArtifactReference Artifact, MmcArtifactLock? Adopted, long MaximumBytes)> jobs,
        MmcInstancePaths paths, ConcurrentDictionary<string, byte> verifiedDirectories, CancellationToken cancellationToken) {
        var results = new PreparedInput?[jobs.Count];
        if (!jobs.Any(job => job.Adopted is not null)) return results;
        await Task.Run(() => MmcInstancePaths.ForEachBounded(jobs, (index, job, token) => {
            if (job.Adopted is not { } adopted) return;
            string originalRelative = OriginalRelative(adopted.AdoptedSha256);
            string original = paths.ResolveRelative(MmcPathRole.Instance, originalRelative, verifiedDirectories);
            if (MmcInstancePaths.Attributes(original) is null) return;
            try {
                var observed = MmcArtifactIo.HashFile(original, job.MaximumBytes, token, job.Artifact.Sha1, verifiedDirectories);
                if (observed.Sha256 == adopted.AdoptedSha256.ToLowerInvariant() && observed.Size == adopted.SizeBytes)
                    results[index] = new(job.Artifact, new(job.Artifact.Key, job.Artifact.SourceRevision, observed.Sha256, observed.Size, originalRelative), original, observed.Sha1);
            } catch (IOException) { }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static async Task<Dictionary<string, (string Sha256, long Size)>> InspectExistingTargetsAsync(
        IEnumerable<(string Relative, string TargetPath)> targets, long maximumBytes,
        ConcurrentDictionary<string, byte> verifiedDirectories, CancellationToken cancellationToken) {
        var candidates = new List<(string Relative, string TargetPath)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in targets) {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add(item.Relative)) candidates.Add(item);
        }
        var results = new Dictionary<string, (string Sha256, long Size)>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0) return results;
        var observed = new (bool Exists, (string Sha256, long Size) Hash)[candidates.Count];
        await Task.Run(() => MmcInstancePaths.ForEachBounded(candidates, (index, item, token) => {
            if (MmcInstancePaths.Attributes(item.TargetPath) is not null)
                observed[index] = (true, MmcArtifactIo.HashSha256(item.TargetPath, maximumBytes, token, verifiedDirectories));
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < candidates.Count; i++) {
            if (observed[i].Exists) results[candidates[i].Relative] = observed[i].Hash;
        }
        return results;
    }

    private async Task<PreparedInput> PrepareInputAsync(MmcArtifactReference artifact, MmcArtifactLock? adopted,
        MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io, MmcArtifactSourceAccess access,
        Dictionary<string, MmcArtifactStagedMutation> mutations, IProgress<MmcArtifactProgress>? progress,
        ConcurrentDictionary<string, byte> verifiedDirectories, CancellationToken token) {
        string? expected = adopted?.AdoptedSha256 ?? artifact.Sha256;
        long? expectedSize = adopted?.SizeBytes ?? artifact.Size;
        long maximum = artifact.Role == MmcArtifactRole.AssetIndex ? Math.Min(limits.MaxAssetIndexBytes, limits.MaxArtifactBytes) : limits.MaxArtifactBytes;
        if (expectedSize > maximum) throw new IOException("Artifact or asset-index byte budget exceeded.");
        string? working = null;
        (string Sha256, long Size, string Sha1) observed = (string.Empty, 0, string.Empty);
        var failures = new List<Exception>();
        if (adopted is not null) {
            string original = paths.ResolveRelative(MmcPathRole.Instance, OriginalRelative(adopted.AdoptedSha256), verifiedDirectories);
            if (File.Exists(original)) {
                try {
                    observed = await MmcArtifactIo.HashAsync(original, maximum, token, artifact.Sha1, verifiedDirectories).ConfigureAwait(false);
                    if (observed.Sha256 == adopted.AdoptedSha256.ToLowerInvariant() && observed.Size == adopted.SizeBytes) working = original;
                } catch (IOException error) { failures.Add(error); }
            }
        }
        if (working is null) {
            var candidates = new List<MmcArtifactSource>();
            if (access.TryGetCachedAsync is not null) {
                var cached = await access.TryGetCachedAsync(artifact, token).ConfigureAwait(false);
                if (cached is not null) candidates.Add(cached);
            }
            if (artifact.Scope == MmcArtifactScope.InstanceLocal) {
                string local = paths.ResolveRelative(MmcPathRole.LocalLibraries, artifact.RelativePath, verifiedDirectories);
                if (File.Exists(local)) candidates.Add(new(paths.LocalLibraryRoot, local));
            }
            foreach (var source in candidates) {
                token.ThrowIfCancellationRequested();
                string work = workspace.NewFile();
                try {
                    string trusted = MmcArtifactIo.AuthorizedSource(source, verifiedDirectories);
                    observed = await io.CopyAsync(trusted, work, artifact.Key, expected, artifact.Sha1, expectedSize, token, maximum).ConfigureAwait(false);
                    working = work; break;
                } catch (IOException error) { failures.Add(error); }
            }
            if (working is null && access.FetchAsync is not null && !artifact.DownloadUrls.IsDefaultOrEmpty) {
                var fetched = await access.FetchAsync(artifact, progress, token).ConfigureAwait(false);
                if (fetched is not null) {
                    string trusted = MmcArtifactIo.AuthorizedSource(fetched, verifiedDirectories);
                    string work = workspace.NewFile();
                    observed = await io.CopyAsync(trusted, work, artifact.Key, expected, artifact.Sha1, expectedSize, token, maximum).ConfigureAwait(false);
                    working = work;
                }
            }
        }
        if (working is null) throw new IOException("No verified source can recover artifact " + artifact.Key + ". Existing adopted bytes will not be replaced by unknown content.", failures.LastOrDefault());
        token.ThrowIfCancellationRequested();
        string originalRelative = OriginalRelative(observed.Sha256);
        string originalTarget = paths.ResolveRelative(MmcPathRole.Instance, originalRelative, verifiedDirectories);
        string originalInput;
        if (string.Equals(working, originalTarget, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            originalInput = originalTarget;
        } else if (File.Exists(originalTarget)) {
            var previous = await MmcArtifactIo.HashAsync(originalTarget, limits.MaxArtifactBytes, token, verifiedDirectories: verifiedDirectories).ConfigureAwait(false);
            if (previous.Sha256 == observed.Sha256 && previous.Size == observed.Size) originalInput = originalTarget;
            else {
                // A corrupted object is never overwritten here. Repair is a normal staged transaction mutation.
                string correct = workspace.NewFile();
                await io.CopyAsync(working, correct, artifact.Key, observed.Sha256, artifact.Sha1, observed.Size, token).ConfigureAwait(false);
                AddMutation(paths, mutations, correct, originalRelative, observed.Sha256, observed.Size, false, previous.Sha256);
                originalInput = correct;
            }
        } else {
            MmcArtifactIo.Directory(Path.GetDirectoryName(originalTarget)!, verifiedDirectories);
            string publish = working;
            MmcInstancePaths.EnsureNoLinks(originalTarget, verifiedDirectories);
            try { File.Move(publish, originalTarget, false); }
            catch (IOException) when (File.Exists(originalTarget)) {
                var raced = await MmcArtifactIo.HashAsync(originalTarget, limits.MaxArtifactBytes, token, verifiedDirectories: verifiedDirectories).ConfigureAwait(false);
                if (raced.Sha256 != observed.Sha256 || raced.Size != observed.Size) throw new IOException("A conflicting original object appeared; it was preserved.");
            }
            originalInput = originalTarget;
        }
        var locked = new MmcArtifactLock(artifact.Key, artifact.SourceRevision, observed.Sha256, observed.Size, originalRelative);
        return new(artifact, locked, originalInput, observed.Sha1);
    }

    private async Task<string> StageTargetAsync(string verifiedSource, string relative, string sha256, long size,
        string key, MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io,
        Dictionary<string, MmcArtifactStagedMutation> mutations, CancellationToken token) =>
        await StageTargetAsync(verifiedSource, relative, paths.ResolveRelative(MmcPathRole.Instance, relative), sha256, size,
            key, paths, workspace, io, mutations, null, null, token).ConfigureAwait(false);

    private async Task<string> StageTargetAsync(string verifiedSource, string relative, string target, string sha256, long size,
        string key, MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io,
        Dictionary<string, MmcArtifactStagedMutation> mutations, Dictionary<string, (string Sha256, long Size)>? prechecked,
        ConcurrentDictionary<string, byte>? verifiedDirectories, CancellationToken token) {
        if (mutations.TryGetValue(relative, out var duplicate)) {
            if (duplicate.NewSha256 != sha256) throw new IOException("Different artifacts collide at a runtime target.");
            return duplicate.StagedPath;
        }
        string? old = null;
        if (prechecked is not null) {
            if (prechecked.TryGetValue(relative, out var current)) {
                old = current.Sha256;
                if (old == sha256 && current.Size == size) return target;
            }
        } else if (File.Exists(target)) {
            var current = await MmcArtifactIo.HashAsync(target, limits.MaxArtifactBytes, token, verifiedDirectories: verifiedDirectories).ConfigureAwait(false);
            old = current.Sha256;
            if (old == sha256 && current.Size == size) return target;
        }
        string staged = workspace.NewFile();
        await io.CopyAsync(verifiedSource, staged, key, sha256, null, size, token).ConfigureAwait(false);
        AddMutation(paths, mutations, staged, relative, sha256, size, old is null, old);
        return staged;
    }

    private static void AddMutation(MmcInstancePaths paths, Dictionary<string, MmcArtifactStagedMutation> mutations,
        string staged, string relative, string sha256, long size, bool missing, string? old) {
        string sourceRelative = Path.GetRelativePath(paths.InstanceRoot, staged).Replace('\\', '/');
        if (!sourceRelative.StartsWith("PCL/MmcRuntime/.staging/", StringComparison.Ordinal)) throw new IOException("Staged source escapes the artifact workspace.");
        var mutation = new MmcArtifactStagedMutation(staged, sourceRelative, relative, sha256, size, missing, old);
        if (mutations.TryGetValue(relative, out var previous) && (previous.NewSha256 != sha256 || previous.SizeBytes != size))
            throw new IOException("Different artifacts collide at an original/runtime target.");
        mutations[relative] = mutation;
    }

    private static string OriginalRelative(string digest) => "PCL/MmcArtifacts/sha256/" + digest.ToLowerInvariant();
    private static string TargetRelative(MmcArtifactReference artifact, string viewKey) {
        if (artifact.Scope == MmcArtifactScope.InstanceLocal) return "libraries/" + artifact.RelativePath.Replace('\\', '/');
        string root = "PCL/MmcRuntime/" + viewKey + "/";
        return root + (artifact.Role == MmcArtifactRole.AssetIndex ? "assets/" : artifact.Role == MmcArtifactRole.LoggingConfiguration ? "" : "libraries/") + artifact.RelativePath.Replace('\\', '/');
    }
    private static string ReferenceFingerprint(MmcArtifactReference value) => JsonSerializer.Serialize(value);
    private static string ViewKey(string context, IEnumerable<PreparedInput> inputs) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Format = "PCLLF.MmcRuntimeView/v3", NativeExtraction = "bounded-native-v1", Assets = "owned-assets-v1", Context = context,
            Inputs = inputs.Select(x => new { x.Artifact.Key, x.Artifact.SourceRevision, x.Artifact.Role, x.Artifact.Scope,
                x.Artifact.RelativePath, x.Artifact.NativeClassifier, x.Artifact.ExtractExcludes, x.Lock.AdoptedSha256, x.Lock.SizeBytes }).ToArray() });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static void ValidateReference(MmcArtifactReference artifact) {
        if ((!MmcArtifactIo.Digest(artifact.Key, 64) && !IsAssetObjectReference(artifact)) || !MmcArtifactIo.Digest(artifact.SourceRevision, 64))
            throw new IOException("Artifact identity or source revision is invalid.");
        if (!Enum.IsDefined(artifact.Role) || !Enum.IsDefined(artifact.Scope)) throw new IOException("Unknown artifact role or scope.");
        MmcArtifactIo.Relative(artifact.RelativePath);
        if ((artifact.Sha256 is not null && !MmcArtifactIo.Digest(artifact.Sha256, 64)) ||
            (artifact.Sha1 is not null && !MmcArtifactIo.Digest(artifact.Sha1, 40)) || artifact.Size < 0)
            throw new IOException("Artifact declared digest or size is invalid.");
    }
    private static void ValidateLock(MmcArtifactLock value) {
        if (string.IsNullOrEmpty(value.ArtifactKey) || string.IsNullOrEmpty(value.SourceRevision) ||
            !MmcArtifactIo.Digest(value.AdoptedSha256, 64) || value.SizeBytes < 0 ||
            value.RecoveryObjectRelativePath != OriginalRelative(value.AdoptedSha256)) throw new IOException("Adopted artifact lock is invalid.");
    }
}
