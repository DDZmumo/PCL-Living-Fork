using System.Collections.Immutable;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed class MmcInstanceCloneBackend {
    private readonly MmcArtifactMaterializer materializer;
    public MmcInstanceCloneBackend(MmcArtifactLimits? limits = null) => materializer = new(limits);

    /// <summary>All synchronous admission leases remain on one dedicated worker. Progress/checkpoints must not wait on UI.</summary>
    public Task<MmcInstanceCloneResult> CloneAsync(MmcInstanceCloneRequest request, CancellationToken cancellationToken = default,
        IProgress<MmcInstanceCloneProgress>? progress = null) => Task.Run(() => Clone(request, cancellationToken, progress), CancellationToken.None);

    private MmcInstanceCloneResult Clone(MmcInstanceCloneRequest request, CancellationToken token, IProgress<MmcInstanceCloneProgress>? progress) {
        MmcInstanceCloneReservation? reservation = null; List<MmcConfigurationQuiescence> quiet = []; List<MmcAdmissionLease> leases = []; bool decided = false;
        try {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Options); token.ThrowIfCancellationRequested();
            if (request.ExpectedSourceInstanceId == Guid.Empty || request.ExpectedSourceGeneration < 1 || !MmcDurableFiles.IsHash(request.ExpectedSourcePrimarySha256)) throw new ArgumentException("Clone needs a fixed source ID, generation and primary SHA256.");
            string sourceRoot = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.SourceRoot));
            string targetRoot = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.TargetRoot));
            MmcInstanceCloneReservation.ValidateRoots(sourceRoot, targetRoot);
            if (request.TargetName != Path.GetFileName(targetRoot)) throw new ArgumentException("Clone name must match its final directory.");
            var initial = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(sourceRoot));
            AssertExpected(initial, request);
            reservation = new(sourceRoot, targetRoot, initial.Definition.Origin.PackageSha256);
            string[] roots = new[] { sourceRoot, reservation.TemporaryRoot, reservation.FinalRoot }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string root in roots) quiet.Add(MmcConfigurationCoordinator.Quiesce(root, token));
            foreach (string root in roots) leases.Add(MmcAdmissionGate.Acquire(root, root == sourceRoot ? initial.Definition.InstanceId : reservation.InstanceId, TimeSpan.Zero, token));
            var sourceLease = leases.Single(lease => lease.VersionRoot == sourceRoot);
            var privateLease = leases.Single(lease => lease.VersionRoot == reservation.TemporaryRoot);
            var finalLease = leases.Single(lease => lease.VersionRoot == reservation.FinalRoot);
            using var pendingPermit = reservation.EnterPending(privateLease);
            var sourceQuiet = quiet.Single(value => value.State.Root == sourceRoot);
            var targetQuiet = quiet.Single(value => value.State.Root == reservation.TemporaryRoot);
            AssertSource(sourceLease, request); reservation.Verify(privateLease);
            if (MmcInstancePaths.Attributes(targetRoot) is not null) throw new IOException("Clone target exists; its player data was preserved.");
            var sourcePaths = MmcInstancePaths.Resolve(sourceRoot, initial.Definition.Layout.GameRootLeaf);
            var targetPaths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, initial.Definition.Layout.GameRootLeaf);
            var saved = MmcSavedSourceStore.LoadAsync(sourcePaths, initial.Definition, token).GetAwaiter().GetResult();
            var compiled = CompileEnvironment(saved.Resolved.Resolution, initial.Definition, request.LegacyEnvironmentCandidates);
            var inventory = MmcInstanceCloneSnapshot.Capture(sourcePaths, initial.Definition, request.Options, token);
            var preferences = MmcInstanceCloneSnapshot.Preferences(sourceQuiet, initial.Definition.InstanceId);
            VerifyAuthoritativeInputs(initial.Definition, compiled, inventory);
            var manifest = new MmcClonePreparedManifest(1, reservation.InstanceId, initial.Definition.InstanceId, initial.Definition.Generation,
                initial.Sha256, preferences.Fingerprint, sourceRoot, targetRoot, request.Options, inventory);
            reservation.Prepare(manifest, privateLease); request.Checkpoint?.Invoke(MmcInstanceCloneCheckpoint.SnapshotPrepared, -1);
            MmcInstanceCloneSnapshot.Copy(sourcePaths, targetPaths, inventory, token, progress, request.Checkpoint, () => reservation.Verify(privateLease));
            AssertSource(sourceLease, request);
            MmcBindingResult bound;
            {
                var active = Active(compiled);
                var declared = active.Where(value => value.Scope == MmcArtifactScope.InstanceLocal).Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var preserved = MmcPackageFilePreservation.CreateLibraryArtifacts(saved.Baseline).Where(value => !declared.Contains("libraries/" + value.RelativePath)).ToImmutableArray();
                var locks = MmcPublishedArtifactLocks.Create(initial.Definition);
                var artifacts = new MmcArtifactRequest(targetPaths, compiled.RuntimePlanFingerprint, active, preserved, locks, MmcArtifactIntent.RepairExisting, compiled.Assets);
                using var prepared = materializer.PrepareAsync(artifacts, cancellationToken: token).GetAwaiter().GetResult();
                var observations = MmcArtifactBindingAdapter.Create(artifacts, compiled, prepared);
                byte[] projection = MmcVersionProjection.Render(request.TargetName, reservation.InstanceId, 1, compiled,
                    prepared.OrderedBindings.ToDictionary(value => value.Artifact.Key, value => value.TargetRelativePath),
                    prepared.NativeDirectory is null ? null : Relative(targetPaths, prepared.NativeDirectory),
                    prepared.AssetsRoot is null ? "PCL/MmcRuntime/" + prepared.ViewKey + "/assets" : Relative(targetPaths, prepared.AssetsRoot));
                bound = MmcPlanBinder.BindClone(initial.Definition, compiled, observations.Facts,
                    new(initial.Definition.InstanceId, initial.Definition.Generation, initial.Definition.InstalledProjection!.BoundFingerprint, reservation.InstanceId, MmcInstanceCloneSnapshot.Hash(projection)) { NativeDerivation = observations.NativeCompletion },
                    observations.DerivedOutputs, observations.AssetIndexes, packageBaseline: saved.BaselineInput);
                if (bound.HasErrors || bound.Definition is null || bound.Plan is null) throw new IOException("Clone binding rejected its source authority: " + string.Join("; ", bound.Diagnostics.Select(value => value.Code)));
                byte[] definition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
                var mutations = prepared.StagedMutations.Select(value => new MmcFileMutation(value.TargetRelativePath, value.ExpectedMissing ? null : value.ExpectedExistingSha256,
                    null, value.NewSha256, value.NewSourceRelativePath)).Append(new(request.TargetName + ".json", null, projection, MmcInstanceCloneSnapshot.Hash(projection)))
                    .Append(new("PCL/InstanceDefinition.json", null, definition, MmcInstanceCloneSnapshot.Hash(definition))).ToArray();
                request.Checkpoint?.Invoke(MmcInstanceCloneCheckpoint.BeforeTransaction, -1);
                AssertSource(sourceLease, request);
                MmcInstanceCloneSnapshot.Verify(sourcePaths, inventory, initial.Definition, request.Options, token);
                if (MmcInstanceCloneSnapshot.Preferences(sourceQuiet, initial.Definition.InstanceId).Fingerprint != preferences.Fingerprint) throw new IOException("Source preferences changed before clone commit.");
                using var settings = targetQuiet.OpenStructuralWrite(privateLease, 0);
                CopyPreferences(settings, preferences, sourceRoot, targetRoot, initial.Definition, reservation.InstanceId, compiled);
                var committed = settings.Execute(new(reservation.InstanceId, 0, 1, mutations), token, (phase, index) => {
                    if (phase == MmcTransactionCheckpoint.CommitDecided) decided = true;
                    request.TransactionCheckpoint?.Invoke(phase, index);
                });
                if (committed.Outcome == MmcTransactionOutcome.Committed) decided = true;
                if (committed.Outcome != MmcTransactionOutcome.Committed) return new(token.IsCancellationRequested ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery,
                    WorkingRoot: reservation.RecoveryRoot, Failure: committed.Diagnostic);
            }
            // After CommitDecided, finish validation/publication regardless of a later cancellation.
            request.Checkpoint?.Invoke(MmcInstanceCloneCheckpoint.BeforePublication, -1);
            AssertSource(sourceLease, request);
            MmcInstanceCloneSnapshot.Verify(sourcePaths, inventory, initial.Definition, request.Options, CancellationToken.None);
            if (MmcInstanceCloneSnapshot.Preferences(sourceQuiet, initial.Definition.InstanceId).Fingerprint != preferences.Fingerprint) throw new IOException("Source preferences changed before clone publication.");
            MmcInstanceCloneSnapshot.VerifyCopied(targetPaths, inventory, CancellationToken.None);
            var final = reservation.Publish(privateLease, finalLease, manifest, bound.Definition!);
            progress?.Report(new("clone-completed", inventory.Files.Length, inventory.Files.Length, inventory.TotalBytes, inventory.TotalBytes));
            return new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, final, final.InstanceRoot, inventory.Files.Length, inventory.TotalBytes);
        } catch (OperationCanceledException error) { return new(decided ? MmcInstanceBackendStatus.NeedsRecovery : MmcInstanceBackendStatus.Cancelled, WorkingRoot: reservation?.RecoveryRoot, Failure: error.Message); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or AggregateException) {
            return new(reservation is null ? MmcInstanceBackendStatus.Rejected : MmcInstanceBackendStatus.NeedsRecovery, WorkingRoot: reservation?.RecoveryRoot, Failure: error.Message);
        } finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); for (int index = quiet.Count - 1; index >= 0; index--) quiet[index].Dispose(); reservation?.Dispose(); }
    }
    private static void AssertExpected(MmcStoredPrimaryDefinition value, MmcInstanceCloneRequest request) {
        if (value.Sha256 != request.ExpectedSourcePrimarySha256 || value.Definition.InstanceId != request.ExpectedSourceInstanceId || value.Definition.Generation != request.ExpectedSourceGeneration)
            throw new IOException("The selected clone source ID/generation/primary hash changed.");
    }
    private static void AssertSource(MmcAdmissionLease lease, MmcInstanceCloneRequest request) {
        lease.AssertHeld(); MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var management = MmcManagementProbe.Read(lease.VersionRoot);
        if (management.State != MmcManagementState.ManagedMmc || management.Envelope?.InstanceId != request.ExpectedSourceInstanceId || management.Envelope.Generation != request.ExpectedSourceGeneration)
            throw new IOException("Clone source is pending, unsupported or inconsistent.");
        var source = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(lease.VersionRoot)); AssertExpected(source, request);
        if (MmcDurableFiles.Observe(MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance, Path.GetFileName(lease.VersionRoot) + ".json")) != source.Definition.InstalledProjection!.OutputJsonSha256)
            throw new IOException("Clone source compatibility projection changed.");
    }
    internal static MmcCompiledProfile CompileEnvironment(MmcResolutionResult resolved, MmcInstanceDefinition definition, ImmutableArray<MmcRuntimeEnvironment> supplied) {
        if (!resolved.IsComplete || resolved.Profile is null) throw new IOException("Clone source metadata is incomplete.");
        List<MmcRuntimeEnvironment> candidates = [];
        if (definition.InstalledProjection!.RuntimeEnvironment is { } stored) candidates.Add(stored);
        else if (!supplied.IsDefaultOrEmpty) { if (supplied.Length > 512) throw new IOException("Too many legacy environment candidates."); candidates.AddRange(supplied); }
        else {
            foreach (string os in new[] { "windows", "linux", "osx" }) foreach (string arch in new[] { "x86", "x86_64", "arm64" }) {
                var provisional = MmcProfileCompiler.Compile(resolved.Profile, new(os, arch, 0, arch == "x86" ? 32 : 64));
                foreach (int major in provisional.Profile?.JavaPolicy.AllowedMajors ?? []) candidates.Add(new(os, arch, major, arch == "x86" ? 32 : 64));
            }
        }
        if (candidates.Count > 512) throw new IOException("Legacy runtime environment search exceeds its bounded candidate budget; supply explicit candidates.");
        List<MmcCompiledProfile> matches = [];
        foreach (var environment in candidates.Distinct()) {
            var compiled = MmcProfileCompiler.Compile(resolved.Profile, environment);
            if (!compiled.HasErrors && compiled.Profile is { } profile && profile.JavaPolicy.Allows(environment.JavaMajor) && profile.RuntimePlanFingerprint == definition.InstalledProjection.RuntimePlanFingerprint) matches.Add(profile);
        }
        var unique = matches.GroupBy(value => value.Environment).Select(group => group.First()).ToArray();
        return unique.Length == 1 ? unique[0] : throw new IOException("Clone needs one exact published runtime environment; supply matching legacy environment candidates when the old definition has no unique recoverable match.");
    }
    private static ImmutableArray<MmcArtifactReference> Active(MmcCompiledProfile compiled) => compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts)
        .GroupBy(value => value.Key, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
    private static void VerifyAuthoritativeInputs(MmcInstanceDefinition definition, MmcCompiledProfile compiled, MmcCloneInventory inventory) {
        var files = inventory.Files.ToDictionary(value => value.RelativePath, StringComparer.OrdinalIgnoreCase);
        void Require(string path, string hash, long? size = null) { if (!files.TryGetValue(path, out var fact) || fact.Sha256 != hash || size is not null && fact.SizeBytes != size) throw new IOException("Clone source lacks exact authoritative bytes: " + path); }
        foreach (var record in definition.SourceBundle.MetadataRecords) Require(record.ObjectRelativePath, record.ContentSha256);
        if (definition.Origin.PlatformEvidenceReference is { } evidence) Require(evidence.ObjectRelativePath, evidence.Sha256);
        if (definition.AuthorFileBaseline is { } baseline) Require(baseline.ObjectRelativePath, baseline.ContentSha256);
        foreach (var record in definition.ComponentOverrides) if (record.PatchReference is { } patch) Require(patch.ObjectRelativePath, patch.Sha256);
        foreach (var record in MmcPublishedArtifactLocks.Create(definition)) Require(record.RecoveryObjectRelativePath, record.AdoptedSha256, record.SizeBytes);
        var active = Active(compiled).Where(value => value.Scope == MmcArtifactScope.InstanceLocal).ToArray();
        foreach (var artifact in active) { var record = definition.ArtifactLocks.Single(value => value.ArtifactKey == artifact.Key); Require(record.TargetRelativePath, record.AdoptedSha256, record.SizeBytes); }
        var declared = active.Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in definition.PackageFileLocks.Where(value => !declared.Contains(value.TargetRelativePath))) Require(record.TargetRelativePath, record.AdoptedSha256, record.SizeBytes);
    }
    private static string Relative(MmcInstancePaths paths, string value) => Path.GetRelativePath(paths.InstanceRoot, value).Replace('\\', '/');
    private static void CopyPreferences(MmcConfigurationWriteScope scope, MmcClonePreferences prefs, string source, string target, MmcInstanceDefinition sourceDefinition, Guid id, MmcCompiledProfile compiled) {
        JObject config = (JObject)prefs.Config.DeepClone();
        string Rewrite(string text) {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (text.Equals(source, comparison)) return target;
            string slashSource = source.Replace('\\', '/'), slashTarget = target.Replace('\\', '/');
            return text.Replace(source + "\\", target + "\\", comparison).Replace(source + "/", slashTarget + "/", comparison)
                .Replace(slashSource + "/", slashTarget + "/", comparison).Replace(slashSource + "\\", slashTarget + "\\", comparison);
        }
        foreach (var value in config.Descendants().OfType<JValue>().Where(value => value.Type == JTokenType.String)) value.Value = Rewrite(value.Value<string>()!);
        JObject hint = JObject.Parse(MmcVersionProjection.ManagementHint(id, 1, compiled).ToJsonString());
        var manifest = config["MinecraftInstallManifest"] as JObject ?? new(); manifest["pcllfManagement"] = hint.DeepClone(); manifest["LastModifiedUtc"] = DateTime.UtcNow; config["MinecraftInstallManifest"] = manifest;
        if (config.ContainsKey("pcllfManagement")) config["pcllfManagement"] = hint.DeepClone();
        if (config.ContainsKey("instanceId")) config["instanceId"] = id.ToString("D"); if (config.ContainsKey("generation")) config["generation"] = 1;
        foreach (var property in config.Properties()) scope.SetConfig(property.Name, property.Value);
        foreach (var pair in LegacyBaseOperations.ParseIni(prefs.Ini)) scope.SetIni(pair.Key, Rewrite(pair.Value));
        foreach (var pair in new Dictionary<string, string> { ["MmcInstanceId"] = id.ToString("D"), ["MmcDefinitionGeneration"] = "1", ["MmcManagementKind"] = "Mmc", ["MmcSchemaVersion"] = "1", ["MmcFormatProfile"] = compiled.FormatProfile, ["MmcLayoutGameRoot"] = sourceDefinition.Layout.GameRootLeaf })
            if (LegacyBaseOperations.ParseIni(prefs.Ini).ContainsKey(pair.Key)) scope.SetIni(pair.Key, pair.Value);
        scope.SetIni("VersionArgumentIndieV2", "True");
    }
}
