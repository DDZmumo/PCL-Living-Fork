using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed class MmcInstancePlatformOriginBackend {
    private readonly MmcPlatformOriginCatalog catalog;
    public MmcInstancePlatformOriginBackend(MmcPlatformOriginCatalog catalog) => this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<MmcPlatformOriginPreviewResult> PreviewAsync(MmcPlatformOriginAttachRequest request, CancellationToken token = default) {
        try {
            ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
            request = request with { Instance = Normalize(request.Instance), OriginalZip = Path.GetFullPath(request.OriginalZip) };
            var primary = ReadCurrent(request.Instance, token);
            // Never infer an original archive from the saved/extracted members.
            var measurement = await MmcPlatformOriginFiles.MeasureAsync(request.OriginalZip, token).ConfigureAwait(false);
            if (measurement.Sha256 != primary.Definition.Origin.PackageSha256) throw new IOException("Select the exact original ZIP whose SHA256 matches this instance's adopted package.");
            using (var archive = await MmcArchiveSourceReader.ReadAsync(request.OriginalZip, cancellationToken: token).ConfigureAwait(false))
                if (archive.PackageSha256 != measurement.Sha256) throw new IOException("The original ZIP changed during verification.");
            var release = await catalog.RecheckAsync(request.Release, token).ConfigureAwait(false);
            var evidence = MmcPlatformOriginCodec.Create(release, request.Release.File, measurement).ToImmutableArray();
            _ = ReadCurrent(request.Instance, token);
            string hash = Fingerprint(request, evidence, measurement.Sha256);
            return new(MmcInstanceBackendStatus.Succeeded, new(request, evidence, hash, measurement.Sha256));
        } catch (OperationCanceledException) { return new(MmcInstanceBackendStatus.Cancelled, Failure: "Origin verification was cancelled after active producers stopped."); }
        catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.Rejected, Failure: error.Message); }
    }

    public Task<MmcPlatformOriginResult> CommitAsync(MmcPlatformOriginCommitRequest request, CancellationToken token = default) =>
        Task.Run(() => Commit(request, token), CancellationToken.None);

    public async Task<ImmutableArray<MmcPlatformReleaseDescriptor>> GetAvailableReleasesAsync(MmcPlatformInstanceSelection selection, CancellationToken token = default) {
        selection = Normalize(selection); var primary = ReadCurrent(selection, token);
        var paths = MmcInstancePaths.Resolve(selection.VersionRoot, primary.Definition.Layout.GameRootLeaf);
        var evidence = MmcPlatformOriginFiles.ReadSaved(paths, primary.Definition) ?? throw new IOException("The current platform strings have no measured evidence; verify the original ZIP before online updating.");
        var result = await catalog.ListAsync(evidence, token).ConfigureAwait(false); _ = ReadCurrent(selection, token); return result;
    }
    /// <summary>Returns null for historical platform strings. Mutable ModpackOrigin.json is never consulted.</summary>
    public static MmcPlatformOriginEvidence? ReadVerified(MmcPlatformInstanceSelection selection, CancellationToken token = default) {
        selection = Normalize(selection); var primary = ReadCurrent(selection, token);
        return MmcPlatformOriginFiles.ReadSaved(MmcInstancePaths.Resolve(selection.VersionRoot, primary.Definition.Layout.GameRootLeaf), primary.Definition);
    }

    private MmcPlatformOriginResult Commit(MmcPlatformOriginCommitRequest request, CancellationToken token) {
        MmcInstanceHistoryPoint? undo = null; bool decided = false;
        try {
            ArgumentNullException.ThrowIfNull(request); var preview = request.Preview ?? throw new ArgumentNullException(nameof(request.Preview));
            var selection = preview.Selection; token.ThrowIfCancellationRequested();
            if (!MmcDurableFiles.IsHash(request.ExpectedCandidateSha256) || request.ExpectedCandidateSha256 != preview.CandidateSha256 ||
                request.ExpectedCandidateSha256 != Fingerprint(selection, preview.Evidence, preview.PackageSha256)) throw new IOException("The fixed origin selection or evidence changed.");
            var primary = ReadCurrent(selection.Instance, token); var current = primary.Definition;
            var paths = MmcInstancePaths.Resolve(selection.Instance.VersionRoot, current.Layout.GameRootLeaf);
            byte[] evidenceBytes = preview.Evidence.ToArray();
            var origin = current.Origin with { PlatformProjectIdentity = new(selection.Release.Platform.ToString(), selection.Release.ProjectId, selection.Release.VersionId), PlatformEvidenceReference = preview.EvidenceReference };
            var evidence = MmcPlatformOriginCodec.Read(evidenceBytes, origin);
            // Metadata and the actual original file are rechecked outside quiescence/admission.
            _ = catalog.RecheckAsync(selection.Release, token).GetAwaiter().GetResult();
            var measurement = MmcPlatformOriginFiles.MeasureAsync(selection.OriginalZip, token).GetAwaiter().GetResult();
            if (measurement != evidence.Package || measurement.Sha256 != current.Origin.PackageSha256) throw new IOException("The selected original ZIP changed after confirmation.");
            using (var archive = MmcArchiveSourceReader.ReadAsync(selection.OriginalZip, cancellationToken: token).GetAwaiter().GetResult())
                if (archive.PackageSha256 != measurement.Sha256) throw new IOException("The selected ZIP changed during container verification.");
            var saved = MmcSavedSourceStore.LoadAsync(paths, current, token).GetAwaiter().GetResult();
            var compiled = MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, current, []);
            byte[] projection = Projection(paths, current, checked(current.Generation + 1));
            using var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, token);
            using var lease = MmcAdmissionGate.Acquire(paths.InstanceRoot, current.InstanceId, TimeSpan.Zero, token);
            MmcInstanceHistoryStore.AssertCurrent(lease, current.InstanceId, current.Generation, primary.Sha256);
            undo = MmcInstanceHistoryStore.Capture(lease, primary, saved, "附加已核实平台来源前", token, null);
            var bound = MmcPlanBinder.BindOriginRevision(current, compiled, evidenceBytes, evidence.Identity,
                new(current.InstanceId, current.Generation, primary.Sha256, MmcDurableFiles.Hash(projection),
                    new(undo.PointId, undo.ManifestSha256, current.InstanceId, current.Generation, primary.Sha256, current.InstalledProjection!.BoundFingerprint)));
            if (bound.HasErrors || bound.Definition is null || bound.Plan is null) throw new IOException("Origin revision was rejected: " + string.Join("; ", bound.Diagnostics.Select(value => value.Code)));
            byte[] definition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
            var reference = preview.EvidenceReference; string? before = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, reference.ObjectRelativePath));
            if (before is not null && before != reference.Sha256) throw new IOException("An immutable origin evidence path has unknown bytes.");
            List<MmcFileMutation> mutations = [new(Path.GetFileName(paths.InstanceRoot) + ".json", current.InstalledProjection.OutputJsonSha256, projection, MmcDurableFiles.Hash(projection)),
                new("PCL/InstanceDefinition.json", primary.Sha256, definition, MmcDurableFiles.Hash(definition))];
            if (before is null) mutations.Add(new(reference.ObjectRelativePath, null, evidenceBytes, reference.Sha256));
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcOriginRevision/v1", InstanceId = current.InstanceId,
                PreviousGeneration = current.Generation, NewGeneration = bound.Definition.Generation, PreviousPrimarySha256 = primary.Sha256,
                NewPrimarySha256 = MmcDurableFiles.Hash(definition), Evidence = reference, preview.CandidateSha256,
                UndoPointId = undo.PointId, UndoManifestSha256 = undo.ManifestSha256 });
            mutations.Add(new("PCL/MmcOriginChanges/" + Guid.NewGuid().ToString("N") + ".json", null, receipt, MmcDurableFiles.Hash(receipt)));
            request.BeforeTransaction?.Invoke();
            MmcInstanceHistoryStore.AssertCurrent(lease, current.InstanceId, current.Generation, primary.Sha256);
            _ = MmcInstanceHistoryStore.Read(paths, undo.PointId, undo.ManifestSha256, token);
            using var scope = quiet.OpenStructuralWrite(lease, current.Generation);
            UpdateGenerationHints(scope, current, compiled, bound.Definition.Generation);
            var result = scope.Execute(new(current.InstanceId, current.Generation, bound.Definition.Generation, mutations.ToArray()), token, (phase, index) => {
                if (phase == MmcTransactionCheckpoint.CommitDecided) decided = true;
                request.TransactionCheckpoint?.Invoke(phase, index);
                if (phase == MmcTransactionCheckpoint.BeforeDecision)
                    _ = MmcPlatformOriginCodec.Read(MmcSavedSourceStore.ReadBounded(paths, reference.ObjectRelativePath, MmcPlatformOriginCodec.MaximumEvidenceBytes), bound.Definition.Origin);
            });
            return result.Outcome == MmcTransactionOutcome.Committed ? new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, undo) :
                new(token.IsCancellationRequested && result.Outcome == MmcTransactionOutcome.Aborted ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, Failure: result.Diagnostic);
        } catch (OperationCanceledException) { return new(decided ? MmcInstanceBackendStatus.NeedsRecovery : MmcInstanceBackendStatus.Cancelled, UndoPoint: undo, Failure: "Origin revision was cancelled."); }
        catch (Exception error) when (Expected(error)) { return new(undo is null ? MmcInstanceBackendStatus.Rejected : MmcInstanceBackendStatus.NeedsRecovery, UndoPoint: undo, Failure: error.Message); }
    }
    private static byte[] Projection(MmcInstancePaths paths, MmcInstanceDefinition current, long next) {
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, Path.GetFileName(paths.InstanceRoot) + ".json", 16 * 1024 * 1024);
        if (MmcDurableFiles.Hash(bytes) != current.InstalledProjection!.OutputJsonSha256) throw new IOException("The selected projection changed.");
        var json = JsonNode.Parse(bytes) as JsonObject ?? throw new IOException("Missing projection object.");
        var hint = json["pcllfManagement"] as JsonObject ?? throw new IOException("Missing managed projection generation.");
        if (hint["instanceId"]?.GetValue<string>() != current.InstanceId.ToString("D") || hint["generation"]?.GetValue<long>() != current.Generation)
            throw new IOException("Projection identity/generation differs from the selected publication.");
        hint["generation"] = next; return JsonSerializer.SerializeToUtf8Bytes(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
    private static void UpdateGenerationHints(MmcConfigurationWriteScope scope, MmcInstanceDefinition current, MmcCompiledProfile compiled, long next) {
        var hint = JObject.Parse(MmcVersionProjection.ManagementHint(current.InstanceId, next, compiled).ToJsonString());
        if (scope.ConfigSnapshot["MinecraftInstallManifest"] is JObject existing) { var manifest = (JObject)existing.DeepClone(); manifest["pcllfManagement"] = hint.DeepClone(); scope.SetConfig("MinecraftInstallManifest", manifest); }
        if (scope.ConfigSnapshot.ContainsKey("pcllfManagement")) scope.SetConfig("pcllfManagement", hint);
        if (scope.ConfigSnapshot.ContainsKey("generation")) scope.SetConfig("generation", next);
        if (LegacyBaseOperations.ParseIni(scope.IniSnapshotForRelocation).ContainsKey("MmcDefinitionGeneration")) scope.SetIni("MmcDefinitionGeneration", next.ToString(CultureInfo.InvariantCulture));
    }
    internal static MmcPlatformInstanceSelection Normalize(MmcPlatformInstanceSelection selection) {
        ArgumentNullException.ThrowIfNull(selection);
        return selection with { VersionRoot = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(selection.VersionRoot)) };
    }
    internal static MmcStoredPrimaryDefinition ReadCurrent(MmcPlatformInstanceSelection selection, CancellationToken token) {
        using var lease = MmcAdmissionGate.Acquire(selection.VersionRoot, selection.ExpectedInstanceId, TimeSpan.Zero, token);
        return MmcInstanceHistoryStore.AssertCurrent(lease, selection.ExpectedInstanceId, selection.ExpectedGeneration, selection.ExpectedPrimarySha256);
    }
    private static string Fingerprint(MmcPlatformOriginAttachRequest request, ImmutableArray<byte> evidence, string packageSha256) =>
        MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcOriginPreview/v1", Selection = request, PackageSha256 = packageSha256, Evidence = MmcPlatformOriginCodec.Reference(evidence.AsSpan()) }));
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        System.Text.Json.JsonException or Newtonsoft.Json.JsonException or System.Net.Http.HttpRequestException or AggregateException;
}
