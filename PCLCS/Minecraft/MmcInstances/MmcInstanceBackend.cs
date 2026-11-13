using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>Token-free MMC instance publication around the existing bounded download/materialization facilities.</summary>
public sealed partial class MmcInstanceBackend<TNetFile> where TNetFile : notnull {
    private readonly MmcMetadataStore metadata;
    private readonly MmcArtifactPrefetchAdapter<TNetFile> prefetch;
    private readonly MmcArtifactMaterializer materializer;
    public MmcInstanceBackend(MmcMetadataStore metadata, MmcArtifactCacheRoots roots, Func<MmcArtifactDownloadSpec, TNetFile> build,
        Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch, MmcArtifactLimits? limits = null) {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        prefetch = new(roots, build, runBatch, limits); materializer = new(limits);
    }

    public async Task<MmcInstanceBackendResult> ImportAsync(MmcInstanceImportRequest request, CancellationToken cancellationToken = default,
        IProgress<MmcInstanceBackendProgress>? progress = null) {
        try {
            ArgumentNullException.ThrowIfNull(request);
            using var source = await MmcArchiveSourceReader.ReadAsync(request.ArchivePath,
                progress: new CallbackProgress<MmcArchiveProgress>(value => progress?.Report(new(value.Phase, value.CompletedBytes, value.TotalBytes))), cancellationToken: cancellationToken).ConfigureAwait(false);
            return await ImportPreparedAsync(source, request, cancellationToken, progress).ConfigureAwait(false);
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, Failure: error.Message); }
        catch (Exception error) when (IsExpectedFailure(error)) { return new(MmcInstanceBackendStatus.Rejected, Failure: error.Message); }
    }

    /// <summary>The caller owns the single read-locked/hash-bound source and keeps it alive through publication.</summary>
    public async Task<MmcInstanceBackendResult> ImportPreparedAsync(MmcArchiveSource source, MmcInstanceImportRequest request,
        CancellationToken cancellationToken = default, IProgress<MmcInstanceBackendProgress>? progress = null,
        MmcResolvedSourceBundle? preparedResolution = null) {
        MmcOwnedImportReservation? reservation = null;
        try {
            ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(request); ValidateName(request.VersionRoot, request.InstanceName);
            if (!string.Equals(source.OriginalPath, Path.GetFullPath(request.ArchivePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Prepared package source differs from its import request.");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("metadata-resolution"));
            var resolved = preparedResolution ?? await MmcMetadataResolution.ResolveAsync(source.Package, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (resolved.Resolution.Profile is not null && !ReferenceEquals(resolved.Resolution.Profile.Package, source.Package))
                throw new InvalidDataException("Prepared metadata resolution belongs to another package source.");
            var compiled = Compile(resolved.Resolution, request.Environment);
            reservation = MmcOwnedImportReservation.Create(request.VersionRoot, Guid.NewGuid(), source.PackageSha256);
            var paths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, source.Package.GameRootLeaf);
            MmcBindingResult bound;
            {
                progress?.Report(new("package-staging"));
                using var packageFiles = await MmcPackageFileStager.PrepareInitialAsync(source, paths, cancellationToken, ArtifactProgress(progress)).ConfigureAwait(false);
                using var sources = MmcSourceBundleWriter.Prepare(source, resolved, paths, cancellationToken);
                byte[] baselineBytes = MmcPackageFileBaselineCodec.Serialize(CreateBaseline(source, packageFiles));
                var baselineReference = sources.StageAuthorBaseline(baselineBytes, packageFiles.Files.Length); sources.StageOpaqueOriginals(packageFiles);
                var baseline = RequireBaseline(baselineBytes, baselineReference, source.PackageSha256, source.Package.RelativeRoot);
                var preserved = PreservedArtifacts(baseline, compiled);
                var artifactsRequest = new MmcArtifactRequest(paths, compiled.RuntimePlanFingerprint, ActiveArtifacts(compiled), preserved, [],
                    MmcArtifactIntent.AdoptDeclaredVariant, compiled.Assets);
                progress?.Report(new("preparing-runtime"));
                var fetched = await prefetch.PrefetchAsync(compiled, artifactsRequest, cancellationToken).ConfigureAwait(false);
                progress?.Report(new("materializing-runtime"));
                using var prepared = await materializer.PrepareAsync(artifactsRequest, fetched.CreateAccess((artifact, token) => {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(packageFiles.LocalLibrarySources.GetValueOrDefault(artifact.RelativePath));
                }), cancellationToken, ArtifactProgress(progress)).ConfigureAwait(false);
                var observations = MmcArtifactBindingAdapter.Create(artifactsRequest, compiled, prepared);
                byte[] projection = RenderProjection(request.InstanceName, reservation.InstanceId, 1, compiled, prepared, paths);
                MmcInstanceDefinition draft = new(1, "Mmc", compiled.FormatProfile, reservation.InstanceId, 1,
                    new(source.PackageSha256, Path.GetFileName(request.ArchivePath)), sources.Bundle, new(source.Package.GameRootLeaf), [], [], [], AuthorFileBaseline: baselineReference);
                bound = MmcPlanBinder.Bind(draft, compiled, observations.Facts, observations.ApplyTo(new(1, 1, MmcBindingIntent.InitialImport, Hash(projection), 1)),
                    derivedOutputs: observations.DerivedOutputs, assetIndexes: observations.AssetIndexes, packageBaseline: new(baselineBytes));
                RequireBinding(bound);
                byte[] definition = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition!));
                // The exclusive unpublished root has no player-owned live targets to replace.
                // Move already flushed/CRC-checked bulk game data there once; only structural,
                // source, library and definition publications need the recoverable file journal.
                progress?.Report(new("installing-package"));
                reservation.AdoptPreparedGameFiles(packageFiles, baseline, cancellationToken, progress, request.PrivatePayloadCheckpoint);
                var mutations = new List<MmcFileMutation>();
                var declaredLocal = DeclaredLocalTargets(compiled);
                foreach (var file in packageFiles.Files.Where(file => file.Role == MmcPackageFileRole.LocalLibrary && !declaredLocal.Contains(file.TargetRelativePath!)))
                    mutations.Add(new(file.TargetRelativePath!, null, null, file.Sha256, Path.GetRelativePath(paths.InstanceRoot, file.StagedPath!).Replace('\\', '/')));
                mutations.AddRange(sources.Mutations); mutations.AddRange(ArtifactMutations(prepared));
                mutations.Add(new(request.InstanceName + ".json", null, projection, Hash(projection)));
                mutations.Add(new("PCL/InstanceDefinition.json", null, definition, Hash(definition)));
                reservation.RecordRecoveryPreparation(packageFiles.StageRoot, sources.StageRoot, prepared.StageRoot);
                progress?.Report(new("publishing-instance"));
                using var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, cancellationToken);
                using var gate = MmcAdmissionGate.Acquire(paths.InstanceRoot, reservation.InstanceId, TimeSpan.Zero, cancellationToken);
                using var pendingPermit = reservation.EnterPending(gate);
                using var settings = quiet.OpenStructuralWrite(gate, 0);
                SetManifest(settings, resolved.Resolution.Profile!, compiled, reservation.InstanceId, 1);
                SetInitialPreferences(settings, source.Package.Configuration);
                MmcTransactionResult result = settings.Execute(new(reservation.InstanceId, 0, 1, MergeMutations(mutations)), cancellationToken, request.TransactionCheckpoint);
                if (result.Outcome != MmcTransactionOutcome.Committed)
                    return new(cancellationToken.IsCancellationRequested ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery,
                        WorkingRoot: reservation.TemporaryRoot, Failure: result.Diagnostic ?? "The import transaction did not commit.");
            }
            // Every source/runtime workspace has been consumed and disposed before its absolute root moves.
            var final = reservation.Publish(source.Package.GameRootLeaf, progress);
            progress?.Report(new("completed"));
            return new(MmcInstanceBackendStatus.Succeeded, bound.Definition, bound.Plan, final, source.Package.Configuration,
                final.InstanceRoot, PreferenceDiagnostics(source.Package.Configuration));
        } catch (OperationCanceledException error) { return new(MmcInstanceBackendStatus.Cancelled, WorkingRoot: WorkingRoot(reservation), Failure: error.Message); }
        catch (Exception error) when (IsExpectedFailure(error)) {
            return new(reservation is null ? MmcInstanceBackendStatus.Rejected : MmcInstanceBackendStatus.NeedsRecovery, WorkingRoot: WorkingRoot(reservation), Failure: error.Message);
        }
        finally { reservation?.Dispose(); }
    }

    private static MmcCompiledProfile Compile(MmcResolutionResult resolution, MmcRuntimeEnvironment environment) {
        if (!resolution.IsComplete || resolution.Profile is null) throw new InvalidDataException("MMC metadata resolution is incomplete: " + string.Join("; ", resolution.Diagnostics.Select(value => value.Code)));
        var result = MmcProfileCompiler.Compile(resolution.Profile, environment);
        if (result.HasErrors || result.Profile is null) throw new InvalidDataException("MMC launch configuration is unsupported: " + string.Join("; ", result.Diagnostics.Select(value => value.Code)));
        if (!result.Profile.JavaPolicy.Allows(environment.JavaMajor)) throw new InvalidDataException("The requested Java is not allowed by this package's compiled policy.");
        return result.Profile;
    }
    private static ImmutableArray<MmcArtifactReference> ActiveArtifacts(MmcCompiledProfile profile) => profile.Classpath.Concat(profile.NativeArtifacts)
        .Concat(profile.DownloadOnlyArtifacts).Concat(profile.AuxiliaryArtifacts).GroupBy(value => value.Key, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
    private static HashSet<string> DeclaredLocalTargets(MmcCompiledProfile profile) => ActiveArtifacts(profile).Where(value => value.Scope == MmcArtifactScope.InstanceLocal)
        .Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static ImmutableArray<MmcArtifactReference> PreservedArtifacts(MmcValidatedPackageBaseline baseline, MmcCompiledProfile profile) {
        var declared = DeclaredLocalTargets(profile);
        return MmcPackageFilePreservation.CreateLibraryArtifacts(baseline).Where(value => !declared.Contains("libraries/" + value.RelativePath)).ToImmutableArray();
    }
    private static byte[] RenderProjection(string name, Guid id, long generation, MmcCompiledProfile compiled, MmcPreparedArtifacts prepared, MmcInstancePaths paths) =>
        MmcVersionProjection.Render(name, id, generation, compiled, prepared.OrderedBindings.ToDictionary(value => value.Artifact.Key, value => value.TargetRelativePath),
            prepared.NativeDirectory is null ? null : Relative(paths, prepared.NativeDirectory),
            prepared.AssetsRoot is null ? "PCL/MmcRuntime/" + prepared.ViewKey + "/assets" : Relative(paths, prepared.AssetsRoot));
    private static string Relative(MmcInstancePaths paths, string value) => Path.GetRelativePath(paths.InstanceRoot, value).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireBinding(MmcBindingResult result) {
        if (result.HasErrors || result.Definition is null || result.Plan is null) throw new InvalidDataException("MMC byte binding failed: " + string.Join("; ", result.Diagnostics.Select(value => value.Code)));
    }
    private static MmcValidatedPackageBaseline RequireBaseline(byte[] bytes, MmcExternalBundleReference reference, string package, string root) {
        var result = MmcPackageFileBaselineCodec.Read(new(bytes), reference, package, root);
        return !result.HasErrors && result.Baseline is not null ? result.Baseline : throw new InvalidDataException("Measured package baseline is not valid.");
    }
    private static MmcPackageFileBaseline CreateBaseline(MmcArchiveSource source, MmcPreparedPackageFiles files) => new(1, source.PackageSha256, source.Package.RelativeRoot,
        source.ArchiveEntries.Select(entry => new MmcPackageArchiveDescriptor(entry.Path, entry.IsDirectory, entry.SizeBytes, entry.Crc32)).ToImmutableArray(),
        files.Files.Select(file => new MmcPackageFileRecord(file.ArchiveRelativePath!, Enum.Parse<PCLLF.MMCC.Core.MmcPackageFileRole>(file.Role.ToString()), file.TargetRelativePath,
            file.Sha256, file.Sha1, file.SizeBytes, file.Crc32)).ToImmutableArray());
    private static IEnumerable<MmcFileMutation> ArtifactMutations(MmcPreparedArtifacts prepared) => prepared.StagedMutations.Select(value =>
        new MmcFileMutation(value.TargetRelativePath, value.ExpectedMissing ? null : value.ExpectedExistingSha256, null, value.NewSha256, value.NewSourceRelativePath));
    private static MmcFileMutation[] MergeMutations(IEnumerable<MmcFileMutation> mutations) {
        Dictionary<string, MmcFileMutation> merged = new(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        foreach (var value in mutations) {
            if (merged.TryGetValue(value.RelativePath, out var prior)) {
                if (prior.ExpectedOldSha256 != value.ExpectedOldSha256 || prior.ExpectedNewSha256 != value.ExpectedNewSha256) throw new IOException("Different inputs collide at a publication target: " + value.RelativePath);
                continue;
            }
            merged.Add(value.RelativePath, value); bytes = checked(bytes + (value.NewContent?.LongLength ?? 0));
            if (merged.Count > 100000 || bytes > 128L * 1024 * 1024) throw new InvalidDataException("Publication metadata/count budget exceeded.");
        }
        return merged.Values.ToArray();
    }
    private static void SetManifest(MmcConfigurationWriteScope scope, MmcResolvedProfile resolved, MmcCompiledProfile compiled, Guid id, long generation) {
        var value = scope.ConfigSnapshot["MinecraftInstallManifest"] as JObject ?? new();
        value["SchemaVersion"] = 1;
        JObject loaders = new();
        foreach (var component in resolved.Components.Where(component => component.Enabled && component.Version is not null)) {
            string? name = component.Uid switch { "net.minecraftforge" => "Forge", "net.neoforged" => "NeoForge", "net.fabricmc.fabric-loader" => "Fabric", "com.mumfrey.liteloader" => "LiteLoader", _ => null };
            if (name is not null) loaders[name] = component.Version;
        }
        value["Components"] = new JObject { ["MinecraftVersion"] = compiled.MinecraftVersion, ["ModLoaderVersions"] = loaders };
        value["ManagedFiles"] = new JArray(); value["LastModifiedUtc"] = DateTime.UtcNow;
        value["pcllfManagement"] = JObject.Parse(MmcVersionProjection.ManagementHint(id, generation, compiled).ToJsonString());
        scope.SetConfig("MinecraftInstallManifest", value);
    }
    private static void SetInitialPreferences(MmcConfigurationWriteScope scope, MmcConfiguration configuration) {
        var values = configuration.Values;
        bool Enabled(string key) => values.TryGetValue(key, out string? value) && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        scope.SetIni("VersionArgumentIndieV2", "True");
        foreach (var (mode, flag) in new[] { ("MmcJavaLocationMode", "OverrideJavaLocation"), ("MmcJvmArgsMode", "OverrideJavaArgs"), ("MmcMemoryMode", "OverrideMemory"), ("MmcWindowMode", "OverrideWindow") })
            scope.SetIni(mode, Enabled(flag) ? "UsePackage" : "InheritGlobal");
        if (Enabled("OverrideJavaArgs") && values.TryGetValue("JvmArgs", out string? arguments) && arguments.IndexOfAny(['\r', '\n']) < 0) scope.SetIni("VersionAdvanceJvm", arguments);
        if (Enabled("JoinServerOnLaunch") && values.TryGetValue("JoinServerOnLaunchAddress", out string? server) && server.IndexOfAny(['\r', '\n']) < 0) scope.SetIni("VersionServerEnter", server);
    }
    private static ImmutableArray<MmcDiagnostic> PreferenceDiagnostics(MmcConfiguration configuration) {
        var result = ImmutableArray.CreateBuilder<MmcDiagnostic>();
        foreach (string field in new[] { "OverrideJavaLocation", "OverrideMemory", "OverrideWindow" })
            if (configuration.Values.TryGetValue(field, out string? raw) && bool.TryParse(raw, out bool active) && active)
                result.Add(new("MMC_PACKAGE_PREFERENCE_RUNTIME", MmcDiagnosticSeverity.Information, "instance.cfg:" + field,
                    "Package preference is retained for runtime resolution; existing Java/memory/window UI values were not copied or forced."));
        if (configuration.Values.TryGetValue("JvmArgs", out string? args) && args.IndexOfAny(['\r', '\n']) >= 0)
            result.Add(new("MMC_PACKAGE_JVM_UI_DIFFERENCE", MmcDiagnosticSeverity.Warning, "instance.cfg:JvmArgs", "Multiline package JVM arguments remain in source; they were not flattened into the single-line legacy setting."));
        // The two capability groups with no equivalent here are reported from the same helper Core uses, so the
        // parse-time notice and this user-visible one cannot state different reasons under a drifting code.
        MmcDiagnostic? console = MmcDisplayDifference.ConsoleDifference(configuration.Values,
            ReadFlag(configuration.Values, "OverrideConsole"), "instance.cfg");
        if (console is not null) result.Add(console);
        MmcDiagnostic? permanentGeneration = MmcDisplayDifference.PermanentGenerationDifference(configuration.Values,
            ReadFlag(configuration.Values, "OverrideMemory"), "instance.cfg");
        if (permanentGeneration is not null) result.Add(permanentGeneration);
        return result.ToImmutable();
    }
    /// <summary>
    /// Reads an already-validated cfg Boolean without re-raising. The reader has already refused an
    /// unparsable value, so an unreadable one here reports no requirement rather than failing a second time.
    /// </summary>
    private static bool ReadFlag(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    private static void ValidateName(string root, string name) {
        MmcInstancePaths.ValidateVersionRoot(root);
        if (name != Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))) throw new ArgumentException("Instance name must match its selected final directory.");
    }
    private static string? WorkingRoot(MmcOwnedImportReservation? reservation) => reservation?.RecoveryRoot;
    private static bool IsExpectedFailure(Exception value) => value is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or AggregateException;
    private static IProgress<MmcArtifactProgress> ArtifactProgress(IProgress<MmcInstanceBackendProgress>? progress) => new CallbackProgress<MmcArtifactProgress>(value => progress?.Report(new(value.Phase, value.CompletedBytes)));
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
