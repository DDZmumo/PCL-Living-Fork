using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    private MmcInstancePackageUpdateResult Commit(MmcInstancePackageUpdateCommitRequest request, CancellationToken token) {
        MmcInstanceHistoryPoint? undo = null; ImmutableArray<string> backups = []; bool decided = false;
        List<MmcConfigurationQuiescence> quiet = []; List<MmcAdmissionLease> leases = [];
        try {
            ArgumentNullException.ThrowIfNull(request); var preview = request.Preview ?? throw new ArgumentNullException(nameof(request.Preview));
            using var jobUse = preview.Job.Use();
            VerifyPreview(preview, request.ExpectedPreviewSha256, token);
            RecheckPlatformOrigin(preview, token);
            if (preview.RequiresChoices || !preview.Errors.IsEmpty) throw new IOException("Resolve the explicit author/user/dependency choices before committing this ZIP update.");
            var currentPaths = MmcInstancePaths.Resolve(preview.Selection.VersionRoot, preview.Source.Layout.GameRootLeaf);
            var primary = MmcSavedSourceStore.ReadPrimary(currentPaths); var current = primary.Definition;
            var currentSaved = MmcSavedSourceStore.LoadAsync(currentPaths, current, token).GetAwaiter().GetResult();
            var candidate = preview.Job.VerifyCandidate(preview.CandidateSnapshot, token);
            var candidatePaths = MmcInstancePaths.Resolve(preview.Job.CandidateRoot, candidate.Definition.Layout.GameRootLeaf);
            var saved = MmcSavedSourceStore.LoadAsync(candidatePaths, candidate.Definition, token).GetAwaiter().GetResult();
            var package = saved.Package with { PackageLocalScopeSha256 = preview.Source.SourceBundle.PackageLocalScopeSha256 };
            var resolution = MmcComponentResolver.Resolve(package, preview.Snapshots, preview.Overrides);
            var compiled = Compile(resolution, preview.Selection.Environment);
            if (compiled.SourceFingerprint != preview.Source.SourceBundle.ResolutionFingerprint || preview.Source.Origin.PackageSha256 != preview.PackageSha256)
                throw new IOException("The captured new-package source no longer resolves to its fixed selection.");
            var engine = MmcInstancePackageUpdatePlanning<TNetFile>.Engine(Path.Combine(preview.Job.Root, "preview"));
            _ = engine.ManagedFileOperations(preview.Plan, token); // Re-evaluate the final enabled dependency graph before transport.
            var pool = PrepareInputPool(preview, currentPaths, current, candidatePaths, candidate.Definition, compiled, token);
            var artifacts = new MmcArtifactRequest(currentPaths, compiled.RuntimePlanFingerprint, Active(compiled), Preserved(compiled, saved.Baseline),
                pool.Locks, MmcArtifactIntent.AdoptDeclaredVariant, compiled.Assets);
            var fetched = prefetch.PrefetchAsync(compiled, artifacts with { Paths = pool.Paths }, token).GetAwaiter().GetResult();
            var mapped = MmcInstanceDefinitionChangeBackend<TNetFile>.PackageMappedFacts(currentPaths, compiled, fetched, token);
            string[] roots = new[] { currentPaths.InstanceRoot, candidatePaths.InstanceRoot }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string root in roots) quiet.Add(MmcConfigurationCoordinator.Quiesce(root, token));
            foreach (string root in roots) leases.Add(MmcAdmissionGate.Acquire(root, root == currentPaths.InstanceRoot ? current.InstanceId : candidate.Definition.InstanceId, TimeSpan.Zero, token));
            var currentLease = leases.Single(value => value.VersionRoot == currentPaths.InstanceRoot);
            var candidateLease = leases.Single(value => value.VersionRoot == candidatePaths.InstanceRoot);
            MmcInstanceHistoryStore.AssertCurrent(currentLease, current.InstanceId, current.Generation, preview.Selection.ExpectedPrimarySha256);
            MmcFileTransactions.AssertNoPending(candidateLease); MmcSessions.AssertNoActive(candidateLease);
            preview.Job.VerifyCandidate(preview.CandidateSnapshot, token);
            ValidateLocalSnapshot(preview, currentPaths, token); VerifyFixedLocals(preview.LocalInputs, token);
            var desired = preview.LocalInputs.ToDictionary(value => "libraries/" + value.FileName, value => value.Sha256, StringComparer.OrdinalIgnoreCase);
            MmcInstanceDefinitionChangeBackend<TNetFile>.PackageAssertPrivate(currentPaths, current, desired);
            MmcInstanceDefinitionChangeBackend<TNetFile>.PackageAssertMapped(currentPaths, current, mapped);
            var operations = engine.ManagedFileOperations(preview.Plan, token);
            var requiredUndo = PackageUndoPaths(currentPaths, operations);
            if (preview.ChangesMinecraftVersion) backups = BackupWorlds(currentPaths, token);
            undo = MmcInstanceHistoryStore.Capture(currentLease, primary, currentSaved, "整合包更新前", token, null, requiredUndo, fullSafeEnvironment: true);
            var locals = preview.LocalInputs.ToDictionary(value => value.FileName, StringComparer.OrdinalIgnoreCase);
            using var prepared = materializer.PrepareAsync(artifacts, fetched.CreateAccess((artifact, ct) => {
                ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<MmcArtifactSource?>(locals.TryGetValue(artifact.RelativePath, out var input)
                    ? new(Path.GetDirectoryName(input.AbsolutePath)!, input.AbsolutePath) : null);
            }), token).GetAwaiter().GetResult();
            using var workspace = new MmcArtifactWorkspace(currentPaths);
            var observed = MmcArtifactBindingAdapter.Create(artifacts, compiled, prepared);
            var actualMapped = observed.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource)
                .Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)).OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
            if (!mapped.OrderBy(value => value.RelativePath, StringComparer.Ordinal).SequenceEqual(actualMapped)) throw new IOException("Prepared mappings differ from the guarded new-package sources.");
            operations = engine.ManagedFileOperations(preview.Plan, token);
            if (!requiredUndo.SequenceEqual(PackageUndoPaths(currentPaths, operations))) throw new IOException("Selected package operations changed their exact undo coverage.");
            byte[] ownershipBytes = Encoding.UTF8.GetBytes(engine.BuildNextManifest(preview.Plan, token).ToString(Formatting.Indented));
            string ownershipHash = MmcDurableFiles.Hash(ownershipBytes), ownershipPath = "PCL/MmcSource/" + preview.Source.SourceBundle.BundleSha256 + "/ownership/" + ownershipHash + ".json";
            var source = preview.Source with { PackageOwnershipReference = new(ownershipHash, ownershipPath) };
            long next = checked(current.Generation + 1); string name = Path.GetFileName(currentPaths.InstanceRoot);
            byte[] projection = MmcVersionProjection.Render(name, current.InstanceId, next, compiled,
                prepared.OrderedBindings.ToDictionary(value => value.Artifact.Key, value => value.TargetRelativePath),
                prepared.NativeDirectory is null ? null : Relative(currentPaths, prepared.NativeDirectory),
                prepared.AssetsRoot is null ? "PCL/MmcRuntime/" + prepared.ViewKey + "/assets" : Relative(currentPaths, prepared.AssetsRoot));
            var bound = MmcPlanBinder.BindPackageRevision(current, source, compiled, observed.Facts,
                new(current.InstanceId, current.Generation, primary.Sha256, preview.PackageSha256, MmcDurableFiles.Hash(projection),
                    new(undo.PointId, undo.ManifestSha256, current.InstanceId, current.Generation, primary.Sha256, current.InstalledProjection!.BoundFingerprint) {
                        CoverageVersion = undo.CoverageVersion, AdditionalEnvironmentPaths = undo.AdditionalEnvironmentPaths,
                        FullSafePolicyVersion = undo.FullSafeEnvironment ? 1 : null
                    }) {
                    NativeDerivation = observed.NativeCompletion, PackageOwnershipContent = ownershipBytes, RequiredUndoPaths = requiredUndo
                }, saved.BaselineInput, observed.DerivedOutputs, observed.AssetIndexes);
            if (bound.HasErrors || bound.Definition is null || bound.Plan is null) throw new IOException("New package binding rejected its measured authority: " + string.Join("; ", bound.Diagnostics.Select(value => value.Code)));
            byte[] definition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
            var mutations = StageSourceObjects(currentPaths, preview, workspace, token)
                .Concat(prepared.StagedMutations.Select(value => new MmcFileMutation(value.TargetRelativePath, value.ExpectedMissing ? null : value.ExpectedExistingSha256, null, value.NewSha256, value.NewSourceRelativePath)))
                .Concat(MmcInstanceDefinitionChangeBackend<TNetFile>.PackagePrivateMutations(currentPaths, current, preview.LocalInputs, workspace, token))
                .Concat(GameMutations(preview, currentPaths, operations, workspace, token)).ToList();
            string? ownershipBefore = MmcDurableFiles.Observe(currentPaths.ResolveRelative(MmcPathRole.Instance, ownershipPath));
            if (ownershipBefore is not null && ownershipBefore != ownershipHash) throw new IOException("New immutable ownership object has unknown bytes.");
            if (ownershipBefore is null) mutations.Add(new(ownershipPath, null, ownershipBytes, ownershipHash));
            string? mirrorBefore = preview.LocalSnapshot.FirstOrDefault(value => value.RelativePath == "PCL/ModpackOrigin.json")?.Sha256;
            mutations.Add(new("PCL/ModpackOrigin.json", mirrorBefore, ownershipBytes, ownershipHash));
            mutations.Add(new(name + ".json", current.InstalledProjection.OutputJsonSha256, projection, MmcDurableFiles.Hash(projection)));
            mutations.Add(new("PCL/InstanceDefinition.json", primary.Sha256, definition, MmcDurableFiles.Hash(definition)));
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcZipUpdate/v1", OperationId = preview.Job.Owner.OperationId,
                InstanceId = current.InstanceId, PreviousGeneration = current.Generation, NewGeneration = next, PreviousPrimarySha256 = primary.Sha256,
                NewPrimarySha256 = MmcDurableFiles.Hash(definition), PackageSha256 = preview.PackageSha256, preview.PreviewSha256,
                UndoPointId = undo.PointId, UndoManifestSha256 = undo.ManifestSha256, WorldBackupIds = backups, Choices = preview.Choices, Ownership = source.PackageOwnershipReference });
            mutations.Add(new("PCL/MmcPackageUpdateReceipts/" + preview.Job.Owner.OperationId.ToString("N") + ".json", null, receipt, MmcDurableFiles.Hash(receipt)));
            request.BeforeTransaction?.Invoke();
            MmcInstanceHistoryStore.AssertCurrent(currentLease, current.InstanceId, current.Generation, primary.Sha256);
            ValidateLocalSnapshot(preview, currentPaths, token); VerifyFixedLocals(preview.LocalInputs, token);
            preview.Job.VerifyCandidate(preview.CandidateSnapshot, token);
            MmcInstanceDefinitionChangeBackend<TNetFile>.PackageAssertPrivate(currentPaths, current, desired);
            MmcInstanceDefinitionChangeBackend<TNetFile>.PackageAssertMapped(currentPaths, current, mapped);
            _ = MmcInstanceHistoryStore.Read(currentPaths, undo.PointId, undo.ManifestSha256, token);
            engine.ValidateChoices(preview.Plan, token);
            if (Fingerprint(preview, token) != request.ExpectedPreviewSha256) throw new IOException("The chosen source/file/component state changed before the update transaction.");
            using var scope = quiet.Single(value => value.State.Root == currentPaths.InstanceRoot).OpenStructuralWrite(currentLease, current.Generation);
            MmcInstanceDefinitionChangeBackend<TNetFile>.PackageManifest(scope, resolution.Profile!, compiled, current.InstanceId, next);
            var result = scope.Execute(new(current.InstanceId, current.Generation, next, MmcInstanceDefinitionChangeBackend<TNetFile>.PackageMerge(mutations)), token, (phase, index) => {
                if (phase == MmcTransactionCheckpoint.CommitDecided) decided = true;
                request.TransactionCheckpoint?.Invoke(phase, index);
                if (phase == MmcTransactionCheckpoint.BeforeDecision) {
                    _ = MmcPlatformOriginFiles.ReadSaved(currentPaths, bound.Definition);
                    MmcInstanceHistoryFiles.Verify(currentPaths, prepared.OrderedBindings.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes))
                        .Concat(prepared.AdoptedLocks.Select(value => new MmcHistoryFile(value.RecoveryObjectRelativePath, value.AdoptedSha256, value.SizeBytes)))
                        .Concat(observed.DerivedOutputs.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)))
                        .Concat(preview.SourceCopies.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)))
                        .GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()), token);
                    _ = MmcInstancePackageOwnership.Read(currentPaths, bound.Definition);
                }
            });
            return result.Outcome == MmcTransactionOutcome.Committed ? new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, undo, backups) :
                new(token.IsCancellationRequested && result.Outcome == MmcTransactionOutcome.Aborted ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, WorldBackupIds: backups, Failure: result.Diagnostic);
        } catch (OperationCanceledException error) { return new(decided ? MmcInstanceBackendStatus.NeedsRecovery : MmcInstanceBackendStatus.Cancelled, UndoPoint: undo, WorldBackupIds: backups, Failure: error.Message); }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, WorldBackupIds: backups, Failure: error.Message); }
        finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); for (int index = quiet.Count - 1; index >= 0; index--) quiet[index].Dispose(); }
    }
    private ImmutableArray<string> BackupWorlds(MmcInstancePaths paths, CancellationToken token) {
        string saves = paths.ResolveRelative(MmcPathRole.Game, "saves"); if (!Directory.Exists(saves)) return [];
        int worlds = 0;
        foreach (string directory in Directory.EnumerateDirectories(saves)) {
            MmcInstancePaths.EnsureNoLinks(directory); if (Path.GetFileName(directory).StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(directory, "level.dat"))) worlds++;
            if (worlds > 100000) throw new IOException("World backup count exceeds its budget.");
        }
        if (worlds == 0) return [];
        if (backupWorlds is null) throw new IOException("Changing Minecraft version with existing worlds requires the host's existing WorldBackupEngine adapter.");
        var result = backupWorlds(paths.GameRoot, token).ToImmutableArray();
        if (result.Length < worlds || result.Any(string.IsNullOrWhiteSpace)) throw new IOException("Existing world backup did not complete for every current world; the update was not committed.");
        return result;
    }
    private static void ValidateLocalSnapshot(MmcInstancePackageUpdatePreview preview, MmcInstancePaths paths, CancellationToken token) {
        if (!preview.LocalSnapshot.SequenceEqual(MmcInstancePackageUpdatePlanning<TNetFile>.LocalSnapshot(paths, token))) throw new IOException("Local files or their namespace changed after ZIP update preview.");
    }
    private static ImmutableArray<string> PackageUndoPaths(MmcInstancePaths paths, IReadOnlyList<Tuple<string, string?, string?>> operations) {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var operation in operations) {
            MmcInstancePackageUpdatePlanning<TNetFile>.Within(paths.GameRoot, operation.Item1);
            result.Add(paths.GameRootLeaf + "/" + Path.GetRelativePath(paths.GameRoot, operation.Item1).Replace('\\', '/'));
        }
        return MmcInstanceHistoryFiles.NormalizeAdditionalPaths(paths, result.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
    }
    private static IEnumerable<MmcFileMutation> GameMutations(MmcInstancePackageUpdatePreview preview, MmcInstancePaths paths,
        IReadOnlyList<Tuple<string, string?, string?>> operations, MmcArtifactWorkspace workspace, CancellationToken token) {
        var before = preview.LocalSnapshot.ToDictionary(value => value.RelativePath, StringComparer.OrdinalIgnoreCase); var io = new MmcArtifactIo(new(), null);
        foreach (var operation in operations) {
            MmcInstancePackageUpdatePlanning<TNetFile>.Within(paths.GameRoot, operation.Item1);
            string gameRelative = Path.GetRelativePath(paths.GameRoot, operation.Item1).Replace('\\', '/');
            if (!MmcInstancePackageUpdatePlanning<TNetFile>.SafeAuthorFile(gameRelative)) throw new IOException("Selected operations reach player runtime data or launcher/account records.");
            string relative = paths.GameRootLeaf + "/" + gameRelative; before.TryGetValue(relative, out var previous);
            string? staged = null, digest = null;
            if (operation.Item2 is { } payload) {
                if (MmcConfigurationCoordinator.Within(preview.Job.Root, Path.GetFullPath(payload))) MmcInstancePackageUpdatePlanning<TNetFile>.Within(preview.Job.Root, payload);
                else MmcInstancePackageUpdatePlanning<TNetFile>.Within(paths.GameRoot, payload);
                var measured = MmcArtifactIo.HashAsync(payload, MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
                digest = measured.Sha256; staged = workspace.NewFile();
                io.CopyAsync(payload, staged, relative, digest, measured.Sha1, measured.Size, token).GetAwaiter().GetResult();
            }
            yield return new(relative, previous?.Sha256, null, digest, staged is null ? null : Relative(paths, staged));
        }
    }
}
