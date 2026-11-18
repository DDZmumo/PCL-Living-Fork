using System.Collections.Immutable;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host integration; governed by the repository LICENCE.
/// <summary>Online acquisition only. Preview/selection/undo/world backup/publication stay in the existing MMC ZIP backend.</summary>
public sealed class MmcInstancePlatformOnlineBackend<TNetFile> where TNetFile : notnull {
    private readonly MmcPlatformOriginCatalog catalog;
    private readonly MmcInstancePackageUpdateBackend<TNetFile> updates;
    private readonly MmcArtifactCacheRoots roots;
    private readonly Func<MmcArtifactDownloadSpec, TNetFile> build;
    private readonly Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch;
    public MmcInstancePlatformOnlineBackend(MmcPlatformOriginCatalog catalog, MmcInstancePackageUpdateBackend<TNetFile> updates,
        MmcArtifactCacheRoots roots, Func<MmcArtifactDownloadSpec, TNetFile> build, Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch) {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); this.updates = updates ?? throw new ArgumentNullException(nameof(updates));
        this.roots = roots; this.build = build ?? throw new ArgumentNullException(nameof(build)); this.runBatch = runBatch ?? throw new ArgumentNullException(nameof(runBatch));
    }
    public Task<MmcInstancePackageUpdatePreviewResult> PrepareAsync(MmcPlatformOnlineUpdateRequest request, CancellationToken token = default,
        IProgress<MmcInstanceBackendProgress>? progress = null) => PrepareCoreAsync(request, null, token, progress);

    /// <summary>The host selects Java from the downloaded, verified MMC container. Its actual selector must return before cancellation completes.</summary>
    public Task<MmcInstancePackageUpdatePreviewResult> PrepareAsync(MmcPlatformInstanceSelection instance, MmcPlatformReleaseDescriptor release,
        Func<string, CancellationToken, Task<MmcRuntimeEnvironment>> selectEnvironment, CancellationToken token = default,
        IProgress<MmcInstanceBackendProgress>? progress = null) {
        ArgumentNullException.ThrowIfNull(selectEnvironment);
        return PrepareCoreAsync(new(instance, release, new("windows", "x86_64")), selectEnvironment, token, progress);
    }
    private async Task<MmcInstancePackageUpdatePreviewResult> PrepareCoreAsync(MmcPlatformOnlineUpdateRequest request,
        Func<string, CancellationToken, Task<MmcRuntimeEnvironment>>? selectEnvironment, CancellationToken token,
        IProgress<MmcInstanceBackendProgress>? progress) {
        try {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Release); ArgumentNullException.ThrowIfNull(request.Environment);
            request = request with { Instance = MmcInstancePlatformOriginBackend.Normalize(request.Instance) };
            var current = MmcInstancePlatformOriginBackend.ReadCurrent(request.Instance, token);
            var verified = MmcPlatformOriginFiles.ReadSaved(MmcInstancePaths.Resolve(request.Instance.VersionRoot, current.Definition.Layout.GameRootLeaf), current.Definition)
                ?? throw new IOException("This MMC instance has no measured platform origin; verify its exact original ZIP first.");
            if (request.Release.Platform.ToString() != verified.Identity.Platform || request.Release.ProjectId != verified.Identity.ProjectId ||
                request.Release.CanonicalProjectKey != ResourceProjectKey.Get(request.Release.Platform, verified.Identity.ProjectId))
                throw new IOException("Online update candidates must belong to the same verified platform project.");
            var release = await catalog.RecheckAsync(request.Release, token).ConfigureAwait(false);
            var (archivePath, measurement) = await AcquireAsync(request.Release, release, token).ConfigureAwait(false);
            // Refresh precise official relationships after downloading, before granting the new evidence.
            release = await catalog.RecheckAsync(request.Release, token).ConfigureAwait(false);
            var evidence = MmcPlatformOriginCodec.Create(release, request.Release.File, measurement).ToImmutableArray();
            using (var archive = await MmcArchiveSourceReader.ReadAsync(archivePath, cancellationToken: token).ConfigureAwait(false))
                if (archive.PackageSha256 != measurement.Sha256) throw new IOException("The downloaded original changed while checking the MMC container.");
            var environment = selectEnvironment is null ? request.Environment : await selectEnvironment(archivePath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(environment);
            _ = MmcInstancePlatformOriginBackend.ReadCurrent(request.Instance, token);
            var selection = request.Instance;
            return await updates.PrepareAsync(new(selection.VersionRoot, selection.ExpectedInstanceId, selection.ExpectedGeneration, selection.ExpectedPrimarySha256,
                archivePath, environment) { VerifiedOrigin = new(selection, request.Release, evidence, catalog) }, token, progress).ConfigureAwait(false);
        } catch (OperationCanceledException) { return new(MmcInstanceBackendStatus.Cancelled, Failure: "Online package acquisition was cancelled after producers stopped."); }
        catch (Exception error) when (MmcInstancePlatformOriginBackend.Expected(error)) { return new(MmcInstanceBackendStatus.Rejected, Failure: error.Message); }
    }
    public Task<MmcInstancePackageUpdatePreviewResult> SelectAsync(MmcInstancePackageUpdatePreview preview, string expectedPreviewSha256,
        MmcInstancePackageUpdateChoices choices, CancellationToken token = default) => updates.SelectAsync(preview, expectedPreviewSha256, choices, token);
    public Task<MmcInstancePackageUpdateResult> CommitAsync(MmcInstancePackageUpdateCommitRequest request, CancellationToken token = default) => updates.CommitAsync(request, token);
    public Task<MmcInstancePackageUpdateResult> DiscardAsync(string workingRoot, Guid operationId, CancellationToken token = default) => updates.DiscardAsync(workingRoot, operationId, token);

    private async Task<(string Path, MmcPlatformPackageMeasurement Measurement)> AcquireAsync(MmcPlatformReleaseDescriptor selected, MmcPlatformRelease release, CancellationToken token) {
        var file = MmcPlatformOriginCodec.SelectFile(release, selected.File);
        string key = MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcPlatformArchiveCache/v1", selected.CanonicalProjectKey, selected.VersionId, selected.MetadataSha256, file }));
        string cache = Path.Combine(Path.GetFullPath(roots.SourceCacheRoot), "platform-packages"); MmcArtifactIo.Directory(cache);
        string stable = Path.Combine(cache, key + ".zip"), fresh = Path.Combine(cache, key + "." + Guid.NewGuid().ToString("N") + ".download.zip");
        if (File.Exists(stable)) {
            var existing = await MmcPlatformOriginFiles.MeasureAsync(stable, token).ConfigureAwait(false);
            _ = MmcPlatformOriginCodec.Create(release, file, existing); return (stable, existing);
        }
        try {
            const string uid = "pcllf.mmc.platform-origin";
            var artifact = new MmcArtifactReference(key, uid, selected.MetadataSha256, MmcArtifactRole.DownloadOnly, MmcArtifactScope.Shared, null,
                "platform-packages/" + key + ".zip", [file.DownloadUrl], file.Sha1, null, file.SizeBytes, null, [],
                new(uid, selected.VersionId, release.VersionSource, selected.MetadataSha256, "selectedReleaseFile", 0));
            var spec = new MmcArtifactDownloadSpec(artifact, [key], fresh, [file.DownloadUrl], null, file.Sha1, file.SizeBytes, MmcPlatformOriginCodec.MaximumPackageBytes);
            // Host runner owns cancellation and drains all producers before returning/throwing.
            await runBatch([build(spec)], token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            var measured = await MmcPlatformOriginFiles.MeasureAsync(fresh, token).ConfigureAwait(false);
            _ = MmcPlatformOriginCodec.Create(release, file, measured);
            MmcInstancePaths.EnsureNoLinks(stable);
            try { File.Move(fresh, stable); }
            catch (IOException) when (File.Exists(stable)) {
                var raced = await MmcPlatformOriginFiles.MeasureAsync(stable, token).ConfigureAwait(false);
                if (raced != measured) throw new IOException("The existing package cache acquired different bytes.");
            }
            return (stable, measured);
        } finally {
            MmcInstancePaths.EnsureNoLinks(fresh); if (File.Exists(fresh)) File.Delete(fresh);
        }
    }
}
