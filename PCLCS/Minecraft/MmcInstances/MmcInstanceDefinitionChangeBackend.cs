using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceDefinitionChangeBackend<TNetFile> where TNetFile : notnull {
    private readonly MmcMetadataStore metadata;
    private readonly MmcArtifactMaterializer materializer;
    private readonly MmcArtifactPrefetchAdapter<TNetFile> prefetch;
    private readonly Func<string, CancellationToken, IReadOnlyList<string>>? backupWorlds;
    public MmcInstanceDefinitionChangeBackend(MmcMetadataStore metadata, MmcArtifactCacheRoots roots,
        Func<MmcArtifactDownloadSpec, TNetFile> build, Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch, MmcArtifactLimits? limits = null,
        Func<string, CancellationToken, IReadOnlyList<string>>? backupWorlds = null) {
        this.metadata = metadata; this.backupWorlds = backupWorlds; materializer = new(limits); prefetch = new(roots, build, runBatch, limits);
    }
    public Task<MmcInstanceDefinitionChangePreviewResult> PreviewAsync(MmcInstanceDefinitionChangeRequest request, CancellationToken token = default) =>
        Task.Run(() => Preview(request, token), CancellationToken.None);
    public Task<MmcInstanceDefinitionChangeResult> CommitAsync(MmcInstanceDefinitionChangeCommitRequest request, CancellationToken token = default) =>
        Task.Run(() => Commit(request, token), CancellationToken.None);

    private MmcInstanceDefinitionChangePreviewResult Preview(MmcInstanceDefinitionChangeRequest request, CancellationToken token) {
        try {
            ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
            string root = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.VersionRoot));
            request = request with { VersionRoot = root, Edits = request.Edits.IsDefault ? [] : request.Edits.ToImmutableArray(),
                LocalInputs = request.LocalInputs.IsDefault ? [] : request.LocalInputs.ToImmutableArray() };
            if (request.Edits.Length is < 1 or > 4096 || request.LocalInputs.Length > 100000 || request.Edits.Any(value => value is null) ||
                request.Edits.Select(value => value.Uid).Distinct(StringComparer.Ordinal).Count() != request.Edits.Length)
                throw new IOException("Component edit count/identities are invalid.");
            var paths = MmcInstancePaths.Resolve(root); MmcStoredPrimaryDefinition current;
            MmcSavedInstanceSources saved;
            using (var lease = MmcAdmissionGate.Acquire(root, request.ExpectedInstanceId, TimeSpan.Zero, token)) {
                current = MmcInstanceHistoryStore.AssertCurrent(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
                paths = MmcInstancePaths.Resolve(root, current.Definition.Layout.GameRootLeaf);
                saved = MmcSavedSourceStore.LoadAsync(paths, current.Definition, token).GetAwaiter().GetResult();
            }
            var overrides = current.Definition.ComponentOverrides.ToDictionary(value => value.Uid, value => new MmcComponentOverride(value.Uid, value.Enabled, value.Version,
                value.PatchReference is null ? null : Encoding.UTF8.GetString(MmcSavedSourceStore.ReadBounded(paths, value.PatchReference.ObjectRelativePath, 16 * 1024 * 1024))), StringComparer.Ordinal);
            var snapshots = saved.Resolved.Snapshots.ToList();
            foreach (var edit in request.Edits) {
                if (!saved.Package.Components.Any(value => value.Uid == edit.Uid)) throw new IOException("Unknown component UID: " + edit.Uid);
                if (edit.Reset) {
                    if (edit.Enabled is not null || edit.ExactVersion is not null || edit.PatchContentUtf8 is not null) throw new IOException("Reset cannot carry replacement fields.");
                    overrides.Remove(edit.Uid); continue;
                }
                overrides.TryGetValue(edit.Uid, out var old);
                string? patch = edit.PatchContentUtf8 ?? old?.PatchContentUtf8;
                if (edit.ExactVersion is { } version) {
                    if (string.IsNullOrWhiteSpace(version)) throw new IOException("An explicit component version cannot be empty.");
                    if (edit.PatchContentUtf8 is null) {
                        var content = metadata.FetchBatchAsync([new(edit.Uid, version)], token).GetAwaiter().GetResult().Single();
                        patch = content.ContentUtf8;
                        var snapshot = new MmcMetadataSnapshot(edit.Uid, version, content.Source, content.ContentUtf8, content.Sha256);
                        if (!snapshots.Any(value => value == snapshot)) snapshots.Add(snapshot);
                    }
                }
                overrides[edit.Uid] = new(edit.Uid, edit.Enabled ?? old?.Enabled, edit.ExactVersion ?? old?.Version, patch);
            }
            var normalized = overrides.Values.OrderBy(value => value.Uid, StringComparer.Ordinal).ToImmutableArray();
            var resolved = MmcMetadataResolution.ResolveAsync(saved.Package, metadata, snapshots, normalized, cancellationToken: token).GetAwaiter().GetResult();
            var compiled = Compile(resolved.Resolution, request.Environment, out var compilationDiagnostics);
            foreach (var edit in request.Edits) {
                var effective = resolved.Resolution.Profile!.Components.Single(value => value.Uid == edit.Uid);
                if (!edit.Reset && (edit.Enabled is { } enabled && effective.Enabled != enabled || edit.ExactVersion is { } exact && effective.Version != exact))
                    throw new IOException("The effective component differs from the requested enable/version change: " + edit.Uid);
            }
            var (source, objects, copies) = MmcInstanceDefinitionChangeSources.Build(paths, current.Definition, saved, resolved, normalized, token);
            string oldOverrides = JsonSerializer.Serialize(current.Definition.ComponentOverrides.OrderBy(value => value.Uid, StringComparer.Ordinal));
            string newOverrides = JsonSerializer.Serialize(source.ComponentOverrides.OrderBy(value => value.Uid, StringComparer.Ordinal));
            if (oldOverrides == newOverrides && current.Definition.SourceBundle.BundleSha256 == source.SourceBundle.BundleSha256 &&
                current.Definition.SourceBundle.ResolutionFingerprint == source.SourceBundle.ResolutionFingerprint)
                throw new IOException("The component request makes no source or override change.");
            var artifacts = ArtifactRequest(paths, compiled, current.Definition, saved.Baseline, token);
            var locals = LocalFacts(request, artifacts, current.Definition, saved.Baseline, resolved.Resolution.Profile!, paths, token);
            AssertPrivateTargets(paths, current.Definition, locals.ToDictionary(value => "libraries/" + value.FileName, value => value.Sha256, StringComparer.OrdinalIgnoreCase));
            var previous = MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, current.Definition, []);
            var differences = Differences(saved.Resolved.Resolution.Profile!, previous, resolved.Resolution.Profile!, compiled);
            if (oldOverrides != newOverrides) differences = differences.Add(new("userOverrides", oldOverrides, newOverrides));
            string hash = MmcInstanceDefinitionChangeSources.Fingerprint(request, normalized, resolved.Snapshots, locals, source, objects, copies);
            using (var lease = MmcAdmissionGate.Acquire(root, current.Definition.InstanceId, TimeSpan.Zero, token))
                MmcInstanceHistoryStore.AssertCurrent(lease, current.Definition.InstanceId, current.Definition.Generation, current.Sha256);
            return new(MmcInstanceBackendStatus.Succeeded, new(request, hash, differences, resolved.Resolution.Diagnostics.AddRange(compilationDiagnostics),
                normalized, resolved.Snapshots, locals, source, objects, copies));
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, Failure: error.Message); }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.Rejected, Failure: error.Message); }
    }

    private MmcInstanceDefinitionChangeResult Commit(MmcInstanceDefinitionChangeCommitRequest request, CancellationToken token) {
        MmcInstanceHistoryPoint? undo = null; ImmutableArray<string> backups = []; bool decided = false;
        try {
            ArgumentNullException.ThrowIfNull(request); var candidate = request.Preview ?? throw new ArgumentNullException(nameof(request.Preview));
            var selection = candidate.Selection; token.ThrowIfCancellationRequested();
            if (!MmcDurableFiles.IsHash(request.ExpectedCandidateSha256) || request.ExpectedCandidateSha256 != candidate.CandidateSha256 ||
                request.ExpectedCandidateSha256 != MmcInstanceDefinitionChangeSources.Fingerprint(selection, candidate.Overrides, candidate.Snapshots,
                    candidate.LocalFacts, candidate.Source, candidate.SourceObjects, candidate.SourceCopies))
                throw new IOException("The selected component candidate changed or has no fixed digest.");
            var paths = MmcInstancePaths.Resolve(selection.VersionRoot); var primary = MmcSavedSourceStore.ReadPrimary(paths);
            if (primary.Sha256 != selection.ExpectedPrimarySha256 || primary.Definition.InstanceId != selection.ExpectedInstanceId || primary.Definition.Generation != selection.ExpectedGeneration)
                throw new IOException("The selected component publication is stale.");
            var current = primary.Definition; paths = MmcInstancePaths.Resolve(paths.InstanceRoot, current.Layout.GameRootLeaf);
            var saved = MmcSavedSourceStore.LoadAsync(paths, current, token).GetAwaiter().GetResult();
            // Re-resolve/recompile the captured source inputs, never trust a self-reported preview plan.
            var resolution = MmcComponentResolver.Resolve(saved.Package, candidate.Snapshots, candidate.Overrides);
            var compiled = Compile(resolution, selection.Environment);
            var previous = MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, current, []);
            if (compiled.SourceFingerprint != candidate.Source.SourceBundle.ResolutionFingerprint) throw new IOException("Captured candidate sources no longer compile to their fixed fingerprint.");
            VerifyLocalFacts(candidate.LocalFacts, token);
            var artifacts = ArtifactRequest(paths, compiled, current, saved.Baseline, token);
            var fetched = prefetch.PrefetchAsync(compiled, artifacts, token).GetAwaiter().GetResult();
            var mappedResources = MappedResourceFacts(paths, compiled, fetched, token);
            using var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, token);
            using var lease = MmcAdmissionGate.Acquire(paths.InstanceRoot, current.InstanceId, TimeSpan.Zero, token);
            MmcInstanceHistoryStore.AssertCurrent(lease, current.InstanceId, current.Generation, primary.Sha256);
            var desired = candidate.LocalFacts.ToDictionary(value => "libraries/" + value.FileName, value => value.Sha256, StringComparer.OrdinalIgnoreCase);
            AssertPrivateTargets(paths, current, desired); VerifyLocalFacts(candidate.LocalFacts, token);
            AssertMappedResourceTargets(paths, current, mappedResources);
            // The admission lease prevents a managed game from starting between the
            // final no-session check, the real host backup and the commit decision.
            using var worlds = previous.MinecraftVersion != compiled.MinecraftVersion ? new MmcDefinitionChangeWorldGuard(paths, token) : null;
            if (worlds is not null) backups = worlds.Backup(backupWorlds, token);
            undo = MmcInstanceHistoryStore.Capture(lease, primary, saved, "修改游戏组件前", token, null);
            var local = candidate.LocalFacts.ToDictionary(value => value.FileName, StringComparer.OrdinalIgnoreCase);
            using var prepared = materializer.PrepareAsync(artifacts, fetched.CreateAccess((artifact, ct) => {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<MmcArtifactSource?>(local.TryGetValue(artifact.RelativePath, out var source) ? new(Path.GetDirectoryName(source.AbsolutePath)!, source.AbsolutePath) : null);
            }), token).GetAwaiter().GetResult();
            using var workspace = new MmcArtifactWorkspace(paths);
            var sourceMutations = StageSources(paths, candidate, workspace, token);
            var observations = MmcArtifactBindingAdapter.Create(artifacts, compiled, prepared);
            var preparedMapped = observations.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource)
                .Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)).OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
            if (!mappedResources.OrderBy(value => value.RelativePath, StringComparer.Ordinal).SequenceEqual(preparedMapped))
                throw new IOException("Prepared mapped resources differ from the guarded candidate source snapshot.");
            long next = checked(current.Generation + 1); string name = Path.GetFileName(paths.InstanceRoot);
            byte[] projection = MmcVersionProjection.Render(name, current.InstanceId, next, compiled,
                prepared.OrderedBindings.ToDictionary(value => value.Artifact.Key, value => value.TargetRelativePath),
                prepared.NativeDirectory is null ? null : Relative(paths, prepared.NativeDirectory),
                prepared.AssetsRoot is null ? "PCL/MmcRuntime/" + prepared.ViewKey + "/assets" : Relative(paths, prepared.AssetsRoot));
            var bound = MmcPlanBinder.BindDefinitionRevision(current, candidate.Source, compiled, observations.Facts,
                new(primary.Sha256, MmcDurableFiles.Hash(projection), new(undo.PointId, undo.ManifestSha256, current.InstanceId, current.Generation, primary.Sha256,
                    current.InstalledProjection!.BoundFingerprint)) { NativeDerivation = observations.NativeCompletion }, observations.DerivedOutputs,
                observations.AssetIndexes, packageBaseline: saved.BaselineInput);
            if (bound.HasErrors || bound.Definition is null || bound.Plan is null) throw new IOException("Definition revision binding failed: " + string.Join("; ", bound.Diagnostics.Select(value => value.Code)));
            byte[] definition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
            var mutations = sourceMutations.Concat(prepared.StagedMutations.Select(value => new MmcFileMutation(value.TargetRelativePath,
                value.ExpectedMissing ? null : value.ExpectedExistingSha256, null, value.NewSha256, value.NewSourceRelativePath)))
                .Concat(StagePrivate(paths, current, candidate.LocalFacts, workspace, token))
                .Append(new(name + ".json", current.InstalledProjection.OutputJsonSha256, projection, MmcDurableFiles.Hash(projection)))
                .Append(new("PCL/InstanceDefinition.json", primary.Sha256, definition, MmcDurableFiles.Hash(definition))).ToList();
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcDefinitionRevision/v1", InstanceId = current.InstanceId,
                PreviousGeneration = current.Generation, NewGeneration = next, PreviousPrimarySha256 = primary.Sha256, NewPrimarySha256 = MmcDurableFiles.Hash(definition),
                CandidateSha256 = candidate.CandidateSha256, UndoPointId = undo.PointId, UndoManifestSha256 = undo.ManifestSha256, WorldBackupIds = backups });
            mutations.Add(new("PCL/MmcDefinitionChanges/" + Guid.NewGuid().ToString("N") + ".json", null, receipt, MmcDurableFiles.Hash(receipt)));
            request.BeforeTransaction?.Invoke();
            MmcInstanceHistoryStore.AssertCurrent(lease, current.InstanceId, current.Generation, primary.Sha256);
            AssertPrivateTargets(paths, current, desired); VerifyLocalFacts(candidate.LocalFacts, token);
            AssertMappedResourceTargets(paths, current, mappedResources);
            worlds?.AssertUnchanged(token);
            _ = MmcInstanceHistoryStore.Read(paths, undo.PointId, undo.ManifestSha256, token);
            using var scope = quiet.OpenStructuralWrite(lease, current.Generation);
            SetManifest(scope, resolution.Profile!, compiled, current.InstanceId, next);
            var result = scope.Execute(new(current.InstanceId, current.Generation, next, Merge(mutations)), token, (phase, index) => {
                if (phase == MmcTransactionCheckpoint.CommitDecided) decided = true;
                request.TransactionCheckpoint?.Invoke(phase, index);
                if (phase == MmcTransactionCheckpoint.BeforeDecision) {
                    worlds?.AssertUnchanged(token);
                    foreach (var fact in candidate.LocalFacts) if (MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.LocalLibraries, fact.FileName)) != fact.Sha256)
                        throw new IOException("A revised private target changed before the commit decision.");
                    foreach (var value in candidate.SourceObjects) if (MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.Key)) != MmcDurableFiles.Hash(value.Value.ToArray()))
                        throw new IOException("A revised source object changed before the commit decision.");
                    MmcInstanceHistoryFiles.Verify(paths, candidate.SourceCopies.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes))
                        .Concat(prepared.OrderedBindings.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)))
                        .Concat(prepared.AdoptedLocks.Select(value => new MmcHistoryFile(value.RecoveryObjectRelativePath, value.AdoptedSha256, value.SizeBytes)))
                        .Concat(observations.DerivedOutputs.Select(value => new MmcHistoryFile(value.TargetRelativePath, value.Sha256, value.SizeBytes)))
                        .GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()), token);
                    _ = MmcPlatformOriginFiles.ReadSaved(paths, bound.Definition);
                    request.BeforeCommitDecision?.Invoke();
                }
            });
            return result.Outcome == MmcTransactionOutcome.Committed ? new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, undo) { WorldBackupIds = backups } :
                new(token.IsCancellationRequested && result.Outcome == MmcTransactionOutcome.Aborted ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, Failure: result.Diagnostic) { WorldBackupIds = backups };
        } catch (OperationCanceledException error) { return new(decided ? MmcInstanceBackendStatus.NeedsRecovery : MmcInstanceBackendStatus.Cancelled, UndoPoint: undo, Failure: error.Message) { WorldBackupIds = backups }; }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, Failure: error.Message) { WorldBackupIds = backups }; }
    }
    private static MmcCompiledProfile Compile(MmcResolutionResult resolution, MmcRuntimeEnvironment environment) => Compile(resolution, environment, out _);
    private static MmcCompiledProfile Compile(MmcResolutionResult resolution, MmcRuntimeEnvironment environment, out ImmutableArray<MmcDiagnostic> diagnostics) {
        if (!resolution.IsComplete || resolution.Profile is null) throw new IOException("Component resolution is incomplete/unsupported: " + string.Join("; ", resolution.Diagnostics.Select(value => value.Code)));
        var result = MmcProfileCompiler.Compile(resolution.Profile, environment);
        diagnostics = result.Diagnostics;
        return !result.HasErrors && result.Profile is { } compiled && compiled.JavaPolicy.Allows(environment.JavaMajor) ? compiled :
            throw new IOException("Candidate runtime/Java semantics are unsupported: " + string.Join("; ", result.Diagnostics.Select(value => value.Code)));
    }
    private static string Relative(MmcInstancePaths paths, string path) => Path.GetRelativePath(paths.InstanceRoot, path).Replace('\\', '/');
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or AggregateException or System.Net.Http.HttpRequestException or EncoderFallbackException or DecoderFallbackException;
}
