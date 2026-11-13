using System.Collections.Immutable;
using System.Security.Cryptography;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceBackend<TNetFile> where TNetFile : notnull {
    /// <summary>Rebinds an existing published variant. Complete inputs retain their exact structural generation.</summary>
    public async Task<MmcInstancePrepareLaunchResult> PrepareLaunchAsync(MmcInstancePrepareLaunchRequest request,
        CancellationToken cancellationToken = default, IProgress<MmcInstanceBackendProgress>? progress = null) {
        bool repaired = false;
        try {
            ArgumentNullException.ThrowIfNull(request);
            if (request.ExpectedInstanceId == Guid.Empty || request.ExpectedGeneration < 1) throw new ArgumentException("A fixed instance identity and valid optional generation are required.");
            if (request.ExpectedPrimarySha256 is { } selectedHash && !MmcDurableFiles.IsHash(selectedHash))
                throw new ArgumentException("The selected definition digest must be a SHA256 value.");
            var firstPaths = MmcInstancePaths.Resolve(request.VersionRoot);
            var primary = MmcSavedSourceStore.ReadPrimary(firstPaths);
            if (primary.Definition.InstanceId != request.ExpectedInstanceId || request.ExpectedGeneration is { } generation && primary.Definition.Generation != generation ||
                request.ExpectedPrimarySha256 is { } expectedHash && primary.Sha256 != expectedHash)
                throw new IOException("The selected instance identity, generation or definition changed before launch preparation.");
            for (int attempt = 0; attempt < 2; attempt++) {
                var definition = primary.Definition;
                var paths = MmcInstancePaths.Resolve(request.VersionRoot, definition.Layout.GameRootLeaf);
                MmcManagementResult management;
                using (var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, cancellationToken))
                using (var lease = MmcAdmissionGate.Acquire(paths.InstanceRoot, definition.InstanceId, TimeSpan.Zero, cancellationToken)) {
                    AssertPrimary(lease, paths, primary, allowIncomplete: true);
                    management = MmcManagementProbe.Read(paths.InstanceRoot);
                }
                progress?.Report(new("preparing-existing-launch"));
                progress?.Report(new("launch-sources-start"));
                var saved = await MmcSavedSourceStore.LoadAsync(paths, definition, cancellationToken).ConfigureAwait(false);
                progress?.Report(new("launch-sources-complete"));
                progress?.Report(new("launch-compile-start"));
                var compiled = Compile(saved.Resolved.Resolution, request.Environment);
                progress?.Report(new("launch-compile-complete"));
                if (compiled.RuntimePlanFingerprint != definition.InstalledProjection!.RuntimePlanFingerprint) {
                    return new(MmcInstanceBackendStatus.Rejected, Failure: "A different Java/platform runtime variant requires explicit adoption before launch.",
                        Diagnostics: [new("MMC_INSTANCE_VARIANT_REQUIRED", MmcDiagnosticSeverity.Error, "runtime", "Launch preparation cannot silently adopt a different runtime environment.")]);
                }
                var locks = ExistingArtifactLocks(definition);
                var artifactRequest = new MmcArtifactRequest(paths, compiled.RuntimePlanFingerprint, ActiveArtifacts(compiled),
                    PreservedArtifacts(saved.Baseline, compiled), locks, MmcArtifactIntent.RepairExisting, compiled.Assets,
                    PublishedNativeCache(definition, compiled));
                progress?.Report(new("launch-prefetch-start"));
                var fetched = await prefetch.PrefetchAsync(compiled, artifactRequest, cancellationToken).ConfigureAwait(false);
                progress?.Report(new("launch-prefetch-complete"));
                bool incomplete;
                MmcPreparedInstanceLaunch? snapshot = null;
                // This outer phase includes all owned workspace disposal. Inner phases overlap it.
                progress?.Report(new("launch-artifact-scope-start"));
                using (var prepared = await materializer.PrepareAsync(artifactRequest, fetched.CreateAccess(), cancellationToken, ArtifactProgress(progress)).ConfigureAwait(false)) {
                    using var preservedWorkspace = new MmcArtifactWorkspace(paths);
                    progress?.Report(new("launch-preserved-check-start"));
                    var preservedRepairs = await RepairPreservedTargetsAsync(paths, definition, compiled, prepared, preservedWorkspace, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new("launch-preserved-check-complete"));
                    string projectionPath = paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(paths.InstanceRoot) + ".json");
                    string? projectionHash = MmcDurableFiles.Observe(projectionPath);
                    if (projectionHash is not null && projectionHash != definition.InstalledProjection.OutputJsonSha256)
                        throw new IOException("The compatibility JSON was externally changed; explicit projection recovery is required.");
                    incomplete = management.State != MmcManagementState.ManagedMmc || projectionHash is null || !prepared.StagedMutations.IsEmpty || !preservedRepairs.IsEmpty;
                    if (!incomplete) {
                        progress?.Report(new("launch-bind-start"));
                        var observed = MmcArtifactBindingAdapter.Create(artifactRequest, compiled, prepared);
                        var options = observed.ApplyTo(new(definition.Generation, definition.Generation, MmcBindingIntent.RepairExisting,
                            definition.InstalledProjection.OutputJsonSha256, definition.Generation));
                        var bound = MmcPlanBinder.Bind(definition, compiled, observed.Facts, options, definition.ArtifactLocks,
                            observed.DerivedOutputs, observed.AssetIndexes, publishedBaseline: definition, packageBaseline: saved.BaselineInput);
                        RequireBinding(bound);
                        var files = LaunchFiles(paths, definition, prepared, artifactRequest.AdditionalPreservedArtifacts);
                        string view = paths.ResolveRelative(MmcPathRole.Runtime, prepared.ViewKey);
                        snapshot = new(definition, primary.Sha256, bound.Plan!, paths, saved.PackageConfiguration,
                            prepared.OrderedBindings.Select(value => new MmcInstanceLaunchTarget(value.Artifact.Key, value.TargetRelativePath, value.TargetPath)).ToImmutableArray(),
                            new(prepared.ViewKey, prepared.NativeDirectory ?? Path.Combine(view, "natives"), prepared.AssetsRoot ?? Path.Combine(view, "assets"),
                                prepared.GameAssetsRoot ?? Path.Combine(view, "assets")), files);
                        progress?.Report(new("launch-bind-complete"));
                        progress?.Report(new("launch-final-validation-start"));
                        using (var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, cancellationToken))
                        using (var lease = MmcAdmissionGate.Acquire(paths.InstanceRoot, definition.InstanceId, TimeSpan.Zero, cancellationToken))
                            ValidatePreparedLaunch(lease, snapshot, cancellationToken);
                        progress?.Report(new("launch-final-validation-complete"));
                    }
                }
                progress?.Report(new("launch-artifact-scope-complete"));
                if (snapshot is not null) {
                    progress?.Report(new("launch-inputs-ready"));
                    return new(MmcInstanceBackendStatus.Succeeded, snapshot, repaired);
                }
                if (!request.RepairIncomplete || attempt != 0)
                    return new(MmcInstanceBackendStatus.NeedsRecovery, Repaired: repaired, Failure: "Managed runtime inputs or projections are incomplete; repair is required.");
                // No prepared workspace escapes into repair or the caller. Repair rechecks identity,
                // generation, sources and expected old bytes in its own structural transaction.
                var repair = await RepairAsync(new(request.VersionRoot, request.Environment, definition.InstanceId) {
                    ExpectedGeneration = definition.Generation, ExpectedPrimarySha256 = primary.Sha256
                }, cancellationToken, progress).ConfigureAwait(false);
                if (repair.Status != MmcInstanceBackendStatus.Succeeded)
                    return new(repair.Status, Repaired: repaired, Failure: repair.Failure, Diagnostics: repair.Diagnostics);
                repaired = true;
                primary = MmcSavedSourceStore.ReadPrimary(paths);
                if (primary.Definition.InstanceId != definition.InstanceId || primary.Definition.Generation != checked(definition.Generation + 1))
                    throw new IOException("The selected instance changed after launch repair.");
            }
            throw new IOException("Launch input preparation did not produce a complete snapshot.");
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, Repaired: repaired, Failure: error.Message); }
        catch (Exception error) when (IsExpectedFailure(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, Repaired: repaired, Failure: error.Message); }
    }

    /// <summary>Call synchronously inside the same lease as Launching receipt and process registration; no await or UI callbacks.</summary>
    public static void ValidatePreparedLaunch(MmcAdmissionLease lease, MmcPreparedInstanceLaunch snapshot, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(snapshot);
        AssertPrimary(lease, snapshot.Paths, new(snapshot.Definition, snapshot.PrimarySha256), allowIncomplete: false);
        VerifySourceRecords(snapshot.Paths, snapshot.Definition);
        foreach (var file in snapshot.Files) {
            cancellationToken.ThrowIfCancellationRequested();
            string path = snapshot.Paths.ResolveRelative(MmcPathRole.Instance, file.RelativePath);
            MmcInstancePaths.EnsureNoLinks(path);
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != file.Sha256)
                throw new IOException("A bound MMC launch input changed after preparation: " + file.RelativePath);
        }
    }

    private static void AssertPrimary(MmcAdmissionLease lease, MmcInstancePaths paths, MmcStoredPrimaryDefinition expected, bool allowIncomplete) {
        lease.AssertHeld();
        if (lease.InstanceId != expected.Definition.InstanceId || !string.Equals(lease.VersionRoot, paths.InstanceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Launch preparation belongs to a different admission identity/root.");
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var current = MmcSavedSourceStore.ReadPrimary(paths);
        if (current.Sha256 != expected.Sha256 || current.Definition.InstanceId != expected.Definition.InstanceId || current.Definition.Generation != expected.Definition.Generation)
            throw new IOException("The MMC primary definition changed after launch preparation.");
        var management = MmcManagementProbe.Read(paths.InstanceRoot);
        if (!management.ObservationComplete || management.State is MmcManagementState.Legacy or MmcManagementState.PendingTransaction or MmcManagementState.ManagedUnsupported ||
            !allowIncomplete && management.State != MmcManagementState.ManagedMmc)
            throw new IOException("MMC management records require recovery before launch.");
        if (!allowIncomplete && MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(paths.InstanceRoot) + ".json")) != expected.Definition.InstalledProjection!.OutputJsonSha256)
            throw new IOException("The MMC compatibility projection changed after preparation.");
    }
    private static ImmutableArray<MmcArtifactLock> ExistingArtifactLocks(MmcInstanceDefinition definition) => MmcPublishedArtifactLocks.Create(definition);
    private static MmcNativeViewCache? PublishedNativeCache(MmcInstanceDefinition definition, MmcCompiledProfile compiled) {
        var native = definition.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.NativeFile && value.RuntimePlanFingerprint == compiled.RuntimePlanFingerprint).ToArray();
        if (native.Length == 0 || native.Select(value => value.ViewKey).Distinct(StringComparer.Ordinal).Count() != 1 || native.Select(value => value.SourceSetFingerprint).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        return new(native[0].ViewKey, native[0].SourceSetFingerprint, native.Select(value => new MmcNativeFileFact(value.ParentArtifactKey, value.RelativePath, value.Sha256, value.SizeBytes)).ToImmutableArray());
    }
    private static ImmutableArray<MmcInstanceLaunchFile> LaunchFiles(MmcInstancePaths paths, MmcInstanceDefinition definition,
        MmcPreparedArtifacts prepared, ImmutableArray<MmcArtifactReference> preserved) {
        Dictionary<string, MmcInstanceLaunchFile> files = new(StringComparer.OrdinalIgnoreCase);
        void Add(string relative, string sha256, long size) {
            paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (files.TryGetValue(relative, out var existing) && (existing.Sha256 != sha256 || existing.SizeBytes != size)) throw new IOException("Launch input observations conflict at one path.");
            files[relative] = new(relative, sha256, size);
        }
        foreach (var value in prepared.OrderedBindings) Add(value.TargetRelativePath, value.Sha256, value.SizeBytes);
        foreach (var value in prepared.AdoptedLocks) Add(value.RecoveryObjectRelativePath, value.AdoptedSha256, value.SizeBytes);
        var preservedKeys = preserved.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var value in definition.PackageFileLocks.Where(value => preservedKeys.Contains(value.ArtifactKey))) Add(value.TargetRelativePath, value.AdoptedSha256, value.SizeBytes);
        foreach (var value in definition.DerivedOutputs.Where(value => value.ViewKey == prepared.ViewKey)) Add(value.TargetRelativePath, value.Sha256, value.SizeBytes);
        return files.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToImmutableArray();
    }
}
