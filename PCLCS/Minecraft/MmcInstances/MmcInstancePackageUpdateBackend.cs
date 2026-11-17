using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    private readonly MmcMetadataStore metadata;
    private readonly MmcInstanceBackend<TNetFile> importer;
    private readonly MmcArtifactMaterializer materializer;
    private readonly MmcArtifactPrefetchAdapter<TNetFile> prefetch;
    private readonly Func<string, CancellationToken, IReadOnlyList<string>>? backupWorlds;
    public MmcInstancePackageUpdateBackend(MmcMetadataStore metadata, MmcArtifactCacheRoots roots, Func<MmcArtifactDownloadSpec, TNetFile> build,
        Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch,
        Func<string, CancellationToken, IReadOnlyList<string>>? backupWorlds = null, MmcArtifactLimits? limits = null) {
        this.metadata = metadata; this.backupWorlds = backupWorlds; importer = new(metadata, roots, build, runBatch, limits);
        materializer = new(limits); prefetch = new(roots, build, runBatch, limits);
    }
    public Task<MmcInstancePackageUpdatePreviewResult> PrepareAsync(MmcInstancePackageUpdateRequest request, CancellationToken token = default,
        IProgress<MmcInstanceBackendProgress>? progress = null) => Task.Run(() => Prepare(request, token, progress), CancellationToken.None);
    public Task<MmcInstancePackageUpdatePreviewResult> SelectAsync(MmcInstancePackageUpdatePreview preview, string expectedPreviewSha256,
        MmcInstancePackageUpdateChoices choices, CancellationToken token = default) => Task.Run(() => Select(preview, expectedPreviewSha256, choices, token), CancellationToken.None);
    public Task<MmcInstancePackageUpdateResult> CommitAsync(MmcInstancePackageUpdateCommitRequest request, CancellationToken token = default) =>
        Task.Run(() => Commit(request, token), CancellationToken.None);

    private MmcInstancePackageUpdatePreviewResult Prepare(MmcInstancePackageUpdateRequest request, CancellationToken token,
        IProgress<MmcInstanceBackendProgress>? progress) {
        MmcPackageUpdateJob? job = null;
        try {
            ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
            request = request with { VersionRoot = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.VersionRoot)), ArchivePath = Path.GetFullPath(request.ArchivePath) };
            MmcStoredPrimaryDefinition primary; ImmutableArray<MmcHistoryFile> local;
            using (var lease = MmcAdmissionGate.Acquire(request.VersionRoot, request.ExpectedInstanceId, TimeSpan.Zero, token)) {
                primary = MmcInstanceHistoryStore.AssertCurrent(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
                local = MmcInstancePackageUpdatePlanning<TNetFile>.LocalSnapshot(MmcInstancePaths.Resolve(request.VersionRoot, primary.Definition.Layout.GameRootLeaf), token);
            }
            using var archive = MmcArchiveSourceReader.ReadAsync(request.ArchivePath, cancellationToken: token).GetAwaiter().GetResult();
            VerifyPlatformOrigin(request, archive.PackageSha256);
            if (archive.PackageSha256 == primary.Definition.Origin.PackageSha256) throw new IOException("This is the already-adopted ZIP; use repair or an explicit environment restore.");
            if (archive.Package.GameRootLeaf != primary.Definition.Layout.GameRootLeaf) throw new IOException("The new ZIP changes active/inactive game-tree roles; explicit layout migration is required before updating.");
            job = MmcPackageUpdateJob.Create(request, archive.PackageSha256);
            using var jobUse = job.Use();
            var imported = importer.ImportPreparedAsync(archive, new(request.ArchivePath, job.CandidateRoot, "candidate", request.Environment), token, progress).GetAwaiter().GetResult();
            if (imported.Status != MmcInstanceBackendStatus.Succeeded) return new(imported.Status, WorkingRoot: job.Root, Failure: imported.Failure);
            var candidate = job.Snapshot(token); job.Seal(candidate);
            return BuildPreview(request, MmcInstancePackageUpdateChoices.Default, job, local, candidate, token);
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, WorkingRoot: job?.Root, Failure: error.Message); }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.Rejected, WorkingRoot: job?.Root, Failure: error.Message); }
    }
    private MmcInstancePackageUpdatePreviewResult Select(MmcInstancePackageUpdatePreview preview, string expected,
        MmcInstancePackageUpdateChoices choices, CancellationToken token) {
        try {
            ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(choices); VerifyPreview(preview, expected, token);
            using var jobUse = preview.Job.Use();
            return BuildPreview(preview.Selection, Normalize(choices), preview.Job, preview.LocalSnapshot, preview.CandidateSnapshot, token);
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, WorkingRoot: preview?.Job.Root, Failure: error.Message); }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.Rejected, WorkingRoot: preview?.Job.Root, Failure: error.Message); }
    }
    private MmcInstancePackageUpdatePreviewResult BuildPreview(MmcInstancePackageUpdateRequest request, MmcInstancePackageUpdateChoices choices,
        MmcPackageUpdateJob job, ImmutableArray<MmcHistoryFile> local, ImmutableArray<MmcHistoryFile> candidateSnapshot, CancellationToken token) {
        choices = Normalize(choices); job.Verify(); var candidate = job.VerifyCandidate(candidateSnapshot, token);
        var currentPaths = MmcInstancePaths.Resolve(request.VersionRoot); MmcStoredPrimaryDefinition current;
        using (var lease = MmcAdmissionGate.Acquire(request.VersionRoot, request.ExpectedInstanceId, TimeSpan.Zero, token)) {
            current = MmcInstanceHistoryStore.AssertCurrent(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
            currentPaths = MmcInstancePaths.Resolve(request.VersionRoot, current.Definition.Layout.GameRootLeaf);
            if (!local.SequenceEqual(MmcInstancePackageUpdatePlanning<TNetFile>.LocalSnapshot(currentPaths, token))) throw new IOException("Local files changed while the new ZIP was being prepared; refresh the preview.");
        }
        var oldSaved = MmcSavedSourceStore.LoadAsync(currentPaths, current.Definition, token).GetAwaiter().GetResult();
        var candidatePaths = MmcInstancePaths.Resolve(job.CandidateRoot, candidate.Definition.Layout.GameRootLeaf);
        var saved = MmcSavedSourceStore.LoadAsync(candidatePaths, candidate.Definition, token).GetAwaiter().GetResult();
        var oldAuthor = MmcMetadataResolution.ResolveAsync(oldSaved.Package, metadata, oldSaved.Resolved.Snapshots, cancellationToken: token).GetAwaiter().GetResult();
        if (!oldAuthor.Resolution.IsComplete) throw new IOException("The old author component sources are incomplete; matching original-source recovery is required.");
        var (components, overrides, needsComponentChoice) = Components(currentPaths, current.Definition, oldAuthor.Resolution.Profile!, saved.Resolved.Resolution.Profile!, choices, token);
        var snapshots = MergeSnapshots(saved.Resolved.Snapshots, oldSaved.Resolved.Snapshots);
        var scopedPackage = saved.Package with { PackageLocalScopeSha256 = candidate.Definition.Origin.PackageSha256 };
        var resolved = MmcMetadataResolution.ResolveAsync(scopedPackage, metadata, snapshots, overrides, cancellationToken: token).GetAwaiter().GetResult();
        var compiled = Compile(resolved.Resolution, request.Environment);
        var localInputs = FixedLocalInputs(currentPaths, current.Definition, candidatePaths, candidate.Definition, saved.Baseline, resolved.Resolution.Profile!, compiled, token);
        var (source, objects, copies) = MmcInstanceDefinitionChangeSources.Build(candidatePaths, candidate.Definition, saved, resolved, overrides, token);
        source = source with { InstanceId = current.Definition.InstanceId, Generation = current.Definition.Generation, ParentInstanceId = current.Definition.ParentInstanceId,
            Origin = SelectedPlatformOrigin(request, source.Origin) };
        if (request.VerifiedOrigin is { } verifiedOrigin) objects = objects.SetItem(verifiedOrigin.Reference.ObjectRelativePath, verifiedOrigin.Evidence);
        var selectedSaved = new MmcSavedInstanceSources(saved.Package, resolved, saved.Baseline, saved.BaselineInput);
        string stage = Path.Combine(job.Root, "preview"); MmcArtifactIo.Directory(stage);
        var engine = MmcInstancePackageUpdatePlanning<TNetFile>.Engine(stage);
        var previousCompiled = MmcInstanceCloneBackend.CompileEnvironment(oldSaved.Resolved.Resolution, current.Definition, []);
        var baseline = MmcInstancePackageUpdatePlanning<TNetFile>.Baseline(currentPaths, current, oldSaved, previousCompiled, stage);
        var package = MmcInstancePackageUpdatePlanning<TNetFile>.Package(candidate, selectedSaved, compiled, candidatePaths, true);
        var plan = engine.Plan(currentPaths.GameRoot, baseline, package, token);
        engine.DownloadChanges(plan, stage, token); // Existing payload verification, ownership and collision analysis; no new queue.
        var byId = plan.Changes.Concat(plan.PreviewChanges).ToDictionary(MmcInstancePackageUpdatePlanning<TNetFile>.ChangeId, StringComparer.Ordinal);
        foreach (var choice in choices.Files) {
            if (!byId.TryGetValue(choice.ChangeId, out var change) || !(change.CanChoose || change.AllowAuthor && !change.DisplayOnly && change.Ownership == PackUpdateOwnership.Disabled)) throw new IOException("The selected file choice is stale or not selectable.");
            change.Choice = choice.Choice; change.KeepDisabled = choice.KeepDisabled;
        }
        plan.AllowUnverifiedDependencies = choices.AllowUnverifiedDependencies;
        var review = engine.ReviewPreview(plan, token);
        bool needsFileChoice = byId.Any(pair => pair.Value.Conflict && pair.Value.CanChoose && !choices.Files.Any(choice => choice.ChangeId == pair.Key));
        bool requires = needsComponentChoice || needsFileChoice || review.Dependencies.HasUnknown && !choices.AllowUnverifiedDependencies;
        var files = MmcInstancePackageUpdatePlanning<TNetFile>.Describe(plan);
        var errors = review.Dependencies.Errors.ToImmutableArray();
        try { MmcInstanceDefinitionChangeBackend<TNetFile>.PackageAssertPrivate(currentPaths, current.Definition, localInputs.ToDictionary(value => "libraries/" + value.FileName, value => value.Sha256, StringComparer.OrdinalIgnoreCase)); }
        catch (IOException error) { errors = errors.Add(error.Message); }
        var runtimeChanges = RuntimeDifferences(previousCompiled, compiled, current.Definition, localInputs);
        var warnings = plan.Warnings.Concat(plan.RuntimeWarnings).Concat(review.Dependencies.Warnings).Concat(resolved.Resolution.Diagnostics.Select(value => value.Code + ": " + value.Message)).Distinct().ToImmutableArray();
        var preview = new MmcInstancePackageUpdatePreview(request, choices, "", job.Owner.PackageSha256, components, files, runtimeChanges, warnings, errors, requires,
            previousCompiled.MinecraftVersion != compiled.MinecraftVersion, job, local, candidateSnapshot, source, overrides, resolved.Snapshots, objects, copies, plan, localInputs);
        string hash = Fingerprint(preview, token);
        if (!local.SequenceEqual(MmcInstancePackageUpdatePlanning<TNetFile>.LocalSnapshot(currentPaths, token))) throw new IOException("Local files changed during update selection.");
        using (var lease = MmcAdmissionGate.Acquire(request.VersionRoot, request.ExpectedInstanceId, TimeSpan.Zero, token))
            MmcInstanceHistoryStore.AssertCurrent(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
        return new(MmcInstanceBackendStatus.Succeeded, new(request, choices, hash, job.Owner.PackageSha256, components, files, runtimeChanges, warnings, errors, requires,
            preview.ChangesMinecraftVersion, job, local, candidateSnapshot, source, overrides, resolved.Snapshots, objects, copies, plan, localInputs), job.Root);
    }
    private static MmcInstancePackageUpdateChoices Normalize(MmcInstancePackageUpdateChoices value) {
        if ((!value.Components.IsDefault && value.Components.Any(item => item is null)) || (!value.Files.IsDefault && value.Files.Any(item => item is null))) throw new IOException("Package-update choices contain null entries.");
        ImmutableArray<MmcPackageComponentDecision> components = value.Components.IsDefault ? [] : value.Components.OrderBy(item => item.Uid, StringComparer.Ordinal).ToImmutableArray();
        ImmutableArray<MmcPackageFileDecision> files = value.Files.IsDefault ? [] : value.Files.OrderBy(item => item.ChangeId, StringComparer.Ordinal).ToImmutableArray();
        if (components.Length > 4096 || files.Length > 100000 || files.Any(item => !MmcDurableFiles.IsHash(item.ChangeId) || !Enum.IsDefined(item.Choice)) ||
            components.Select(item => item.Uid).Distinct(StringComparer.Ordinal).Count() != components.Length || files.Select(item => item.ChangeId).Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw new IOException("Package-update choices are invalid or duplicated.");
        return new(components, files, value.AllowUnverifiedDependencies);
    }
    private static (ImmutableArray<MmcPackageComponentDifference>, ImmutableArray<MmcComponentOverride>, bool) Components(MmcInstancePaths paths,
        MmcInstanceDefinition current, MmcResolvedProfile oldAuthor, MmcResolvedProfile newAuthor, MmcInstancePackageUpdateChoices choices, CancellationToken token) {
        var decisions = choices.Components.ToDictionary(value => value.Uid, value => value.KeepUserOverride, StringComparer.Ordinal);
        var user = current.ComponentOverrides.ToDictionary(value => value.Uid, StringComparer.Ordinal);
        var differences = ImmutableArray.CreateBuilder<MmcPackageComponentDifference>(); var overrides = ImmutableArray.CreateBuilder<MmcComponentOverride>(); bool requires = false;
        foreach (string uid in oldAuthor.Components.Select(value => value.Uid).Union(newAuthor.Components.Select(value => value.Uid))) {
            token.ThrowIfCancellationRequested();
            string Describe(MmcResolvedProfile profile) {
                var declaration = profile.Package.Components.FirstOrDefault(value => value.Uid == uid); var component = profile.Components.FirstOrDefault(value => value.Uid == uid);
                return component is null ? "absent" : JsonSerializer.Serialize(new { component.Uid, Root = declaration is not null, RequestedVersion = declaration?.RequestedVersion,
                    Disabled = declaration?.Disabled, Important = declaration?.Important, DependencyOnly = declaration?.DependencyOnly, Version = component.Version, PatchSha256 = component.Patch.ContentSha256 });
            }
            string before = Describe(oldAuthor), after = Describe(newAuthor); bool exists = after != "absent", hasUser = user.TryGetValue(uid, out var change);
            bool conflict = hasUser && before != after; bool? keep = hasUser ? conflict ? decisions.TryGetValue(uid, out bool selected) ? selected : null : true : null;
            if (decisions.TryGetValue(uid, out bool explicitChoice)) { if (!hasUser) throw new IOException("Component choice does not identify a current user override: " + uid); keep = explicitChoice; }
            if (hasUser && keep is null) requires = true;
            if (keep == true) {
                if (!newAuthor.Package.Components.Any(value => value.Uid == uid)) throw new IOException("The new ZIP removed this root UID; retaining its override needs explicit component/layout migration: " + uid);
                string? patch = change!.PatchReference is { } reference ? Encoding.UTF8.GetString(MmcSavedSourceStore.ReadBounded(paths, reference.ObjectRelativePath, 16 * 1024 * 1024)) : null;
                overrides.Add(new(uid, change.Enabled, change.Version, patch));
            }
            bool removedRoot = oldAuthor.Package.Components.Any(value => value.Uid == uid) && !newAuthor.Package.Components.Any(value => value.Uid == uid);
            if (before != after || hasUser) differences.Add(new(uid, before, after, hasUser, conflict, removedRoot || !exists, keep));
            decisions.Remove(uid);
        }
        if (decisions.Count != 0) throw new IOException("Component decision identifies an unknown UID.");
        return (differences.ToImmutable(), overrides.OrderBy(value => value.Uid, StringComparer.Ordinal).ToImmutableArray(), requires);
    }
    private static ImmutableArray<MmcMetadataSnapshot> MergeSnapshots(ImmutableArray<MmcMetadataSnapshot> incoming, ImmutableArray<MmcMetadataSnapshot> previous) {
        Dictionary<(string, string, MmcMetadataRequestKind), MmcMetadataSnapshot> result = [];
        foreach (var value in incoming.Concat(previous)) {
            var key = (value.Uid, value.Version, value.Kind);
            if (result.TryGetValue(key, out var old)) {
                if (old.ContentUtf8 != value.ContentUtf8 || old.Source != value.Source) throw new IOException("The old/new package metadata fixes conflicting bytes for one exact component identity; choose a distinct revision.");
            } else result.Add(key, value);
        }
        return result.Values.OrderBy(value => value.Uid, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal).ThenBy(value => value.Kind).ToImmutableArray();
    }
    private static MmcCompiledProfile Compile(MmcResolutionResult resolution, MmcRuntimeEnvironment environment) {
        if (!resolution.IsComplete || resolution.Profile is null) throw new IOException("Updated component resolution is incomplete: " + string.Join("; ", resolution.Diagnostics.Select(value => value.Code)));
        var result = MmcProfileCompiler.Compile(resolution.Profile, environment);
        return !result.HasErrors && result.Profile is { } plan && plan.JavaPolicy.Allows(environment.JavaMajor) ? plan : throw new IOException("Updated runtime/Java semantics are unsupported: " + string.Join("; ", result.Diagnostics.Select(value => value.Code)));
    }
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or AggregateException or System.Net.Http.HttpRequestException;
}
