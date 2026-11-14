using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed partial class MmcInstanceBackend<TNetFile> where TNetFile : notnull {
    public Task<MmcInstanceBackendResult> RepairAsync(MmcInstanceRepairRequest request, CancellationToken cancellationToken = default,
        IProgress<MmcInstanceBackendProgress>? progress = null) => ChangeRuntimeAsync(request, MmcBindingIntent.RepairExisting, cancellationToken, progress);

    public Task<MmcInstanceBackendResult> AdoptVariantAsync(MmcInstanceAdoptVariantRequest request,
        CancellationToken cancellationToken = default, IProgress<MmcInstanceBackendProgress>? progress = null) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedInstanceId == Guid.Empty || request.ExpectedGeneration < 1 || !MmcDurableFiles.IsHash(request.ExpectedPrimarySha256))
            throw new ArgumentException("Explicit variant adoption requires a fixed published identity, generation and digest.", nameof(request));
        return ChangeRuntimeAsync(new(request.VersionRoot, request.Environment, request.ExpectedInstanceId) {
            ExpectedGeneration = request.ExpectedGeneration, ExpectedPrimarySha256 = request.ExpectedPrimarySha256,
            TransactionCheckpoint = request.TransactionCheckpoint
        }, MmcBindingIntent.AdoptDeclaredVariant, cancellationToken, progress);
    }

    private async Task<MmcInstanceBackendResult> ChangeRuntimeAsync(MmcInstanceRepairRequest request, MmcBindingIntent intent,
        CancellationToken cancellationToken, IProgress<MmcInstanceBackendProgress>? progress) {
        string? requestedRoot = request?.VersionRoot;
        try {
            ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
            var initialPaths = MmcInstancePaths.Resolve(request.VersionRoot);
            var primary = MmcSavedSourceStore.ReadPrimary(initialPaths); var definition = primary.Definition;
            if (request.ExpectedInstanceId is { } expectedId && expectedId != definition.InstanceId) throw new IOException("The selected instance identity changed.");
            if (request.ExpectedGeneration is { } expectedGeneration && definition.Generation != expectedGeneration ||
                request.ExpectedPrimarySha256 is { } expectedHash && primary.Sha256 != expectedHash)
                throw new IOException("The selected instance generation/definition changed before repair.");
            if (request.StrictSelection is { } strict && (strict.InstanceId != definition.InstanceId || strict.Generation != definition.Generation ||
                strict.PrimarySha256 != primary.Sha256)) throw new IOException("Strict repair selection differs from its request authority.");
            var paths = MmcInstancePaths.Resolve(request.VersionRoot, definition.Layout.GameRootLeaf);
            using (var readGate = MmcAdmissionGate.Acquire(paths.InstanceRoot, definition.InstanceId, TimeSpan.Zero, cancellationToken)) {
                MmcFileTransactions.AssertNoPending(readGate); MmcSessions.AssertNoActive(readGate);
                if (MmcSavedSourceStore.ReadPrimary(paths).Sha256 != primary.Sha256) throw new IOException("Primary definition changed before repair preparation.");
            }
            string projectionPath = paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(paths.InstanceRoot) + ".json");
            string? oldProjection = MmcDurableFiles.Observe(projectionPath);
            byte[]? divergenceBackup = null;
            if (request.StrictSelection?.DivergedProjectionSha256 is { } inspected) {
                if (!request.AllowRebuildDivergedProjection || oldProjection != inspected)
                    throw new IOException("Rebuilding a changed projection requires its explicitly inspected exact bytes.");
                divergenceBackup = MmcSavedSourceStore.ReadBounded(paths, Path.GetFileName(paths.InstanceRoot) + ".json", MmcManagementReader.MaximumDocumentBytes);
                if (MmcDurableFiles.Hash(divergenceBackup) != inspected) throw new IOException("The inspected projection changed before backup preparation.");
            }
            if (oldProjection is not null && oldProjection != definition.InstalledProjection!.OutputJsonSha256 && !request.AllowRebuildDivergedProjection)
                throw new IOException("The compatibility JSON was externally changed; inspect it before explicitly rebuilding from the definition.");
            progress?.Report(new("loading-immutable-sources"));
            var saved = await MmcSavedSourceStore.LoadAsync(paths, definition, cancellationToken).ConfigureAwait(false);
            var compiled = Compile(saved.Resolved.Resolution, request.Environment);
            if (compiled.ProfileFingerprint != definition.InstalledProjection!.ProfileFingerprint) throw new IOException("Recompiled source identity differs from the published profile.");
            bool sameVariant = compiled.RuntimePlanFingerprint == definition.InstalledProjection.RuntimePlanFingerprint;
            if (!sameVariant && intent == MmcBindingIntent.RepairExisting)
                return new(MmcInstanceBackendStatus.Rejected, WorkingRoot: paths.InstanceRoot,
                    Failure: "A different runtime environment requires explicit variant adoption.",
                    Diagnostics: [new("MMC_INSTANCE_VARIANT_REQUIRED", MmcDiagnosticSeverity.Error, "runtime", "Repair cannot silently change the adopted Java/platform variant.")]);
            if (intent == MmcBindingIntent.AdoptDeclaredVariant) {
                if (compiled.JavaPolicy.Kind != MmcJavaPolicyKind.DeclaredCandidates || !compiled.JavaPolicy.Allows(request.Environment.JavaMajor))
                    throw new IOException("Variant adoption requires an explicit compatible Java declaration in the preserved package.");
                if (sameVariant) {
                    // An already-adopted variant is a read-only validation. Missing inputs need a
                    // separate repair; retain the caller's exact selection across this handoff.
                    var existing = await PrepareLaunchAsync(new(paths.InstanceRoot, request.Environment, definition.InstanceId, definition.Generation, RepairIncomplete: false) {
                        ExpectedPrimarySha256 = request.ExpectedPrimarySha256
                    }, cancellationToken, progress).ConfigureAwait(false);
                    return new(existing.Status, existing.Snapshot?.Definition, existing.Snapshot?.BoundPlan, existing.Snapshot?.Paths,
                        existing.Snapshot?.PackageConfiguration, paths.InstanceRoot, existing.Diagnostics, existing.Failure);
                }
            }
            var locks = ExistingArtifactLocks(definition);
            var preserved = PreservedArtifacts(saved.Baseline, compiled);
            var artifactRequest = new MmcArtifactRequest(paths, compiled.RuntimePlanFingerprint, ActiveArtifacts(compiled), preserved, locks,
                intent == MmcBindingIntent.RepairExisting ? MmcArtifactIntent.RepairExisting : MmcArtifactIntent.AdoptDeclaredVariant, compiled.Assets);
            // All transport/preparation stays outside admission. Only already-locked bytes can satisfy repair.
            var fetched = await prefetch.PrefetchAsync(compiled, artifactRequest, cancellationToken).ConfigureAwait(false);
            using var prepared = await materializer.PrepareAsync(artifactRequest, fetched.CreateAccess((artifact, token) => {
                token.ThrowIfCancellationRequested(); return ValueTask.FromResult(VariantLocalOriginal(paths, definition, saved.Baseline, artifact));
            }), cancellationToken, ArtifactProgress(progress)).ConfigureAwait(false);
            using var localTargets = new MmcArtifactWorkspace(paths);
            var localRepairs = await RepairPreservedTargetsAsync(paths, definition, compiled, prepared, localTargets, cancellationToken).ConfigureAwait(false);
            var observations = MmcArtifactBindingAdapter.Create(artifactRequest, compiled, prepared);
            long generation = checked(definition.Generation + 1);
            string name = Path.GetFileName(paths.InstanceRoot);
            byte[] projection = RenderProjection(name, definition.InstanceId, generation, compiled, prepared, paths);
            var options = observations.ApplyTo(new(definition.Generation, generation, intent, Hash(projection), generation));
            var bound = MmcPlanBinder.Bind(definition, compiled, observations.Facts, options, definition.ArtifactLocks,
                observations.DerivedOutputs, observations.AssetIndexes, publishedBaseline: definition, packageBaseline: saved.BaselineInput);
            RequireBinding(bound);
            byte[] nextDefinition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition!));
            var mutations = ArtifactMutations(prepared).Concat(localRepairs).Append(new MmcFileMutation(name + ".json", oldProjection, projection, Hash(projection)))
                .Append(new MmcFileMutation("PCL/InstanceDefinition.json", primary.Sha256, nextDefinition, Hash(nextDefinition)));
            if (divergenceBackup is not null) {
                string backup = MmcInstanceRepairSelections.ProjectionBackupPath(oldProjection!);
                string? existing = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, backup));
                if (existing is not null && existing != oldProjection) throw new IOException("An immutable projection backup has unknown bytes.");
                // Even an existing identical copy stays inside the transaction:
                // a later deletion/replacement must not remove the promised backup.
                mutations = mutations.Append(new(backup, existing, divergenceBackup, oldProjection));
            }
            progress?.Report(new(intent == MmcBindingIntent.RepairExisting ? "publishing-repair" : "adopting-runtime-variant"));
            using var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, cancellationToken);
            using var gate = MmcAdmissionGate.Acquire(paths.InstanceRoot, definition.InstanceId, TimeSpan.Zero, cancellationToken);
            if (MmcSavedSourceStore.ReadPrimary(paths).Sha256 != primary.Sha256 || MmcDurableFiles.Observe(projectionPath) != oldProjection)
                throw new IOException("Definition or projection changed while repair was preparing; no prepared changes were published.");
            if (request.StrictSelection is { } selected) MmcInstanceRepairSelections.Revalidate(gate, selected);
            var repairVerifiedDirs = MmcInstancePaths.CreatePassDirectoryCache();
            VerifyPreparedInputs(paths, prepared, cancellationToken, repairVerifiedDirs);
            VerifySourceRecords(paths, definition, cancellationToken, repairVerifiedDirs);
            using var settings = quiet.OpenProjectionRepair(gate, definition.Generation, primary.Sha256);
            if (request.StrictSelection is not null) MmcInstanceRepairSelections.AssertConfiguration(definition, settings.ConfigSnapshot);
            SetManifest(settings, saved.Resolved.Resolution.Profile!, compiled, definition.InstanceId, generation);
            MmcTransactionResult result = settings.Execute(new(definition.InstanceId, definition.Generation, generation, MergeMutations(mutations)), cancellationToken, request.TransactionCheckpoint);
            if (result.Outcome != MmcTransactionOutcome.Committed)
                return new(cancellationToken.IsCancellationRequested ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, WorkingRoot: paths.InstanceRoot, Failure: result.Diagnostic);
            progress?.Report(new("completed"));
            return new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, paths, saved.PackageConfiguration, paths.InstanceRoot, []);
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, WorkingRoot: requestedRoot, Failure: error.Message); }
        catch (Exception error) when (IsExpectedFailure(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, WorkingRoot: requestedRoot, Failure: error.Message); }
    }

    private static MmcArtifactSource? VariantLocalOriginal(MmcInstancePaths paths, MmcInstanceDefinition definition,
        MmcValidatedPackageBaseline baseline, MmcArtifactReference artifact) {
        if (artifact.Scope != MmcArtifactScope.InstanceLocal) return null;
        var oldDeclaration = definition.ArtifactInventory.FirstOrDefault(value => value.Scope == MmcArtifactScope.InstanceLocal &&
            value.RelativePath == artifact.RelativePath && value.SourceRevision == artifact.SourceRevision && value.ComponentUid == artifact.ComponentUid);
        var oldLock = oldDeclaration is null ? null : definition.ArtifactLocks.FirstOrDefault(value => value.ArtifactKey == oldDeclaration.ArtifactKey);
        if (oldLock is not null) return new(paths.InstanceRoot, paths.ResolveRelative(MmcPathRole.Instance, oldLock.RecoveryObjectRelativePath));
        string target = "libraries/" + artifact.RelativePath;
        var original = baseline.Baseline.Files.FirstOrDefault(value => value.Role == PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary && value.TargetRelativePath == target);
        var preserved = original is null ? null : definition.PackageFileLocks.FirstOrDefault(value => value.TargetRelativePath == target && value.AdoptedSha256 == original.Sha256 && value.SizeBytes == original.SizeBytes);
        return preserved is null ? null : new(paths.InstanceRoot, paths.ResolveRelative(MmcPathRole.Instance, preserved.RecoveryObjectRelativePath));
    }

    private static async Task<ImmutableArray<MmcFileMutation>> RepairPreservedTargetsAsync(MmcInstancePaths paths, MmcInstanceDefinition definition,
        MmcCompiledProfile compiled, MmcPreparedArtifacts prepared, MmcArtifactWorkspace workspace, CancellationToken token) {
        var already = prepared.StagedMutations.Select(value => value.TargetRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = DeclaredLocalTargets(compiled);
        var candidates = new List<MmcPackageFileLockRecord>();
        foreach (var value in definition.PackageFileLocks) {
            token.ThrowIfCancellationRequested();
            if (!already.Contains(value.TargetRelativePath) && !declared.Contains(value.TargetRelativePath))
                candidates.Add(value);
        }
        if (candidates.Count == 0) return [];
        var verifiedDirectories = MmcInstancePaths.CreatePassDirectoryCache();
        var observedHashes = new string?[candidates.Count];
        await Task.Run(() => MmcInstancePaths.ForEachBounded(candidates, (index, value, ct) => {
            string target = paths.ResolveRelative(MmcPathRole.Instance, value.TargetRelativePath, verifiedDirectories);
            observedHashes[index] = MmcDurableFiles.Observe(target, verifiedDirectories);
        }, token), token).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<MmcFileMutation>(); var io = new MmcArtifactIo(new(), null);
        for (int i = 0; i < candidates.Count; i++) {
            token.ThrowIfCancellationRequested();
            var value = candidates[i];
            string? old = observedHashes[i];
            if (old == value.AdoptedSha256) continue;
            string source = OriginalInput(paths, prepared, value.RecoveryObjectRelativePath);
            string staged = workspace.NewFile();
            await io.CopyAsync(source, staged, value.ArtifactKey, value.AdoptedSha256, value.ObservedSha1, value.SizeBytes, token).ConfigureAwait(false);
            result.Add(new(value.TargetRelativePath, old, null, value.AdoptedSha256, Relative(paths, staged)));
        }
        return result.ToImmutable();
    }
    private static string OriginalInput(MmcInstancePaths paths, MmcPreparedArtifacts prepared, string relative,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte>? verifiedDirectories = null) =>
        prepared.StagedMutations.FirstOrDefault(value => value.TargetRelativePath == relative)?.StagedPath ?? paths.ResolveRelative(MmcPathRole.Instance, relative, verifiedDirectories);
    private static void VerifyPreparedInputs(MmcInstancePaths paths, MmcPreparedArtifacts prepared,
        CancellationToken cancellationToken = default,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        verifiedDirectories ??= MmcInstancePaths.CreatePassDirectoryCache();
        if (!prepared.OrderedBindings.IsDefaultOrEmpty) {
            MmcInstancePaths.ForEachBounded(prepared.OrderedBindings, (_, binding, ct) => {
                ct.ThrowIfCancellationRequested();
                if (MmcDurableFiles.Observe(binding.VerifiedInputPath, verifiedDirectories) != binding.Sha256)
                    throw new IOException("Prepared execution input changed before publication.");
            }, cancellationToken);
        }
        if (!prepared.AdoptedLocks.IsDefaultOrEmpty) {
            Dictionary<string, string>? stagedMap = prepared.AdoptedLocks.Length > 1 && prepared.StagedMutations.Length > 1
                ? prepared.StagedMutations.ToDictionary(value => value.TargetRelativePath, value => value.StagedPath, StringComparer.Ordinal)
                : null;
            MmcInstancePaths.ForEachBounded(prepared.AdoptedLocks, (_, value, ct) => {
                ct.ThrowIfCancellationRequested();
                string inputPath = stagedMap is not null && stagedMap.TryGetValue(value.RecoveryObjectRelativePath, out string? staged)
                    ? staged
                    : stagedMap is not null
                        ? paths.ResolveRelative(MmcPathRole.Instance, value.RecoveryObjectRelativePath, verifiedDirectories)
                        : OriginalInput(paths, prepared, value.RecoveryObjectRelativePath, verifiedDirectories);
                if (MmcDurableFiles.Observe(inputPath, verifiedDirectories) != value.AdoptedSha256)
                    throw new IOException("Locked original input changed before publication.");
            }, cancellationToken);
        }
    }
    private static void VerifySourceRecords(MmcInstancePaths paths, MmcInstanceDefinition definition,
        CancellationToken cancellationToken = default,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        verifiedDirectories ??= MmcInstancePaths.CreatePassDirectoryCache();
        _ = MmcPlatformOriginFiles.ReadSaved(paths, definition);
        _ = MmcInstancePackageOwnership.Read(paths, definition);
        var metadataRecords = definition.SourceBundle.MetadataRecords;
        if (!metadataRecords.IsDefaultOrEmpty) {
            MmcInstancePaths.ForEachBounded(metadataRecords, (_, value, ct) => {
                string fullPath = paths.ResolveRelative(MmcPathRole.Instance, value.ObjectRelativePath, verifiedDirectories);
                if (MmcArtifactIo.HashSha256(fullPath, 16L * 1024 * 1024, ct, verifiedDirectories).Sha256 != value.ContentSha256)
                    throw new IOException("Saved source changed before publication.");
            }, cancellationToken);
        }
        foreach (var source in definition.ComponentOverrides.Select(value => value.PatchReference).OfType<MmcObjectReference>()) {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = paths.ResolveRelative(MmcPathRole.Instance, source.ObjectRelativePath, verifiedDirectories);
            if (MmcArtifactIo.HashSha256(fullPath, 16L * 1024 * 1024, cancellationToken, verifiedDirectories).Sha256 != source.Sha256)
                throw new IOException("Saved component override changed before publication.");
        }
        if (definition.AuthorFileBaseline is not { } baseline)
            throw new IOException("Measured author baseline changed before publication.");
        string baselinePath = paths.ResolveRelative(MmcPathRole.Instance, baseline.ObjectRelativePath, verifiedDirectories);
        if (MmcArtifactIo.HashSha256(baselinePath, 64L * 1024 * 1024, cancellationToken, verifiedDirectories).Sha256 != baseline.ContentSha256)
            throw new IOException("Measured author baseline changed before publication.");
    }
}
