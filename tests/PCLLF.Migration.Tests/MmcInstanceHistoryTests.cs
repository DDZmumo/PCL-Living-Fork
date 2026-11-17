using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcInstanceHistoryTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC history restores its published runtime and private environment into a new generation while retaining current worlds and preferences", RoundTrip);
        yield return ("MMC history rejects damaged point objects, unanchored references and missing adopted originals before live mutations", Damaged);
        yield return ("MMC history refuses to omit or overwrite a player-edited mapped resource from a complete undo point", EditedMappedResource);
        yield return ("MMC history refuses active sessions, pending operations, old coverage and a different instance identity", Barriers);
        yield return ("MMC history cancels before decision and recovers controlled interrupted transactions on either side of the decision", Transactions);
        yield return ("MMC history preserves an unknown private library when a later point reintroduces its old path", UnknownReintroducedLibrary);
    }
    private static void UnknownReintroducedLibrary() {
        using Fixture fixture = new(); fixture.Import(); var history = new MmcInstanceHistoryBackend();
        var early = Capture(fixture, history, "before library addition");
        PublishLibraryAddition(fixture);
        var late = Capture(fixture, history, "after library addition");
        Equal(early.InstanceId, late.InstanceId); Equal(2L, late.Generation);
        var restored = history.RestoreAsync(Request(fixture, early)).GetAwaiter().GetResult();
        if (restored.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(restored.Failure);
        Equal(3L, fixture.Primary().Definition.Generation); Equal(false, File.Exists(Path.Combine(fixture.VersionRoot, "libraries", "later.jar")));
        fixture.Put("libraries/later.jar", "unknown personal replacement");
        string primary = fixture.Primary().Sha256;
        var refused = history.RestoreAsync(Request(fixture, late)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, refused.Status);
        Equal(primary, fixture.Primary().Sha256); Equal(3L, fixture.Primary().Definition.Generation);
        Equal("unknown personal replacement", fixture.Read("libraries/later.jar"));
        // Identical bytes at an otherwise unowned path can be safely reused, and an absent
        // path can still be rebuilt from the point's immutable original.
        fixture.Put("libraries/later.jar", "declared later library");
        var reused = history.RestoreAsync(Request(fixture, late)).GetAwaiter().GetResult();
        if (reused.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(reused.Failure);
        Equal(4L, fixture.Primary().Definition.Generation);
        var back = history.RestoreAsync(Request(fixture, early)).GetAwaiter().GetResult();
        if (back.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(back.Failure);
        var rebuilt = history.RestoreAsync(Request(fixture, late)).GetAwaiter().GetResult();
        if (rebuilt.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(rebuilt.Failure);
        Equal(6L, fixture.Primary().Definition.Generation); Equal("declared later library", fixture.Read("libraries/later.jar"));
    }

    // A real explicit Core definition update plus the production transaction creates the
    // later point; no handwritten published definition or synthetic artifact lock is accepted.
    private static void PublishLibraryAddition(Fixture fixture) {
        var primary = fixture.Primary(); var definition = primary.Definition; var paths = MmcInstancePaths.Resolve(fixture.VersionRoot, definition.Layout.GameRootLeaf);
        var saved = MmcSavedSourceStore.LoadAsync(paths, definition).GetAwaiter().GetResult();
        var originalPatch = definition.SourceBundle.MetadataRecords.Single(value => value.Kind == MmcSourceRecordKind.Component && value.Uid == "net.minecraft");
        var patch = JObject.Parse(Encoding.UTF8.GetString(MmcSavedSourceStore.ReadBounded(paths, originalPatch.ObjectRelativePath, 16 * 1024 * 1024)));
        byte[] laterBytes = Encoding.UTF8.GetBytes("declared later library");
        patch["+libraries"] = new JArray(new JObject { ["name"] = "fixture:later:1", ["MMC-hint"] = "local", ["MMC-filename"] = "later.jar",
            ["downloads"] = new JObject { ["artifact"] = new JObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(laterBytes)), ["size"] = laterBytes.Length } } });
        byte[] patchBytes = Encoding.UTF8.GetBytes(patch.ToString(Newtonsoft.Json.Formatting.None)); string patchHash = MmcDurableFiles.Hash(patchBytes);
        string patchPath = "PCL/MmcOverrides/" + patchHash + ".json";
        var resolution = MmcComponentResolver.Resolve(saved.Package, saved.Resolved.Snapshots,
            [new MmcComponentOverride("net.minecraft", PatchContentUtf8: Encoding.UTF8.GetString(patchBytes))]);
        if (!resolution.IsComplete) throw new Exception(string.Join("; ", resolution.Diagnostics));
        var compilation = MmcProfileCompiler.Compile(resolution.Profile!, definition.InstalledProjection!.RuntimeEnvironment!);
        if (compilation.HasErrors || compilation.Profile is null) throw new Exception(string.Join("; ", compilation.Diagnostics));
        var compiled = compilation.Profile;
        using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.VersionRoot);
        using var lease = MmcAdmissionGate.Acquire(fixture.VersionRoot, definition.InstanceId, TimeSpan.Zero);
        MmcInstanceHistoryStore.AssertCurrent(lease, definition.InstanceId, definition.Generation, primary.Sha256);
        fixture.Put("libraries/later.jar", Encoding.UTF8.GetString(laterBytes));
        var active = compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts)
            .GroupBy(value => value.Key, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
        var declared = active.Where(value => value.Scope == MmcArtifactScope.InstanceLocal).Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var artifactRequest = new MmcArtifactRequest(paths, compiled.RuntimePlanFingerprint, active,
            MmcPackageFilePreservation.CreateLibraryArtifacts(saved.Baseline).Where(value => !declared.Contains("libraries/" + value.RelativePath)).ToImmutableArray(),
            MmcPublishedArtifactLocks.Create(definition), MmcArtifactIntent.AdoptDeclaredVariant, compiled.Assets);
        var index = definition.ArtifactLocks.Single(value => definition.ArtifactInventory.Single(item => item.ArtifactKey == value.ArtifactKey).Role == MmcArtifactRole.AssetIndex);
        using var prepared = new MmcArtifactMaterializer().PrepareAsync(artifactRequest, new((artifact, _) => ValueTask.FromResult<MmcArtifactSource?>(
            artifact.Role == MmcArtifactRole.AssetIndex ? new(paths.InstanceRoot, paths.ResolveRelative(MmcPathRole.Instance, index.RecoveryObjectRelativePath)) : null))).GetAwaiter().GetResult();
        var observations = MmcArtifactBindingAdapter.Create(artifactRequest, compiled, prepared); long next = definition.Generation + 1;
        byte[] projection = MmcVersionProjection.Render("fixture", definition.InstanceId, next, compiled,
            prepared.OrderedBindings.ToDictionary(value => value.Artifact.Key, value => value.TargetRelativePath),
            nativeDirectoryRelative: null, assetsRootRelative: "PCL/MmcRuntime/" + prepared.ViewKey + "/assets");
        var candidate = definition with { InstalledProjection = null,
            SourceBundle = definition.SourceBundle with { ResolutionFingerprint = resolution.Profile!.SourceFingerprint },
            ComponentOverrides = [new("net.minecraft", PatchReference: new(patchHash, patchPath))] };
        var bound = MmcPlanBinder.Bind(candidate, compiled, observations.Facts,
            observations.ApplyTo(new(definition.Generation, next, MmcBindingIntent.UpdateDefinition, MmcDurableFiles.Hash(projection), next)),
            definition.ArtifactLocks, observations.DerivedOutputs, observations.AssetIndexes, publishedBaseline: definition, packageBaseline: saved.BaselineInput);
        if (bound.HasErrors || bound.Definition is null) throw new Exception(string.Join("; ", bound.Diagnostics));
        byte[] primaryBytes = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
        using var workspace = new MmcArtifactWorkspace(paths); string patchInput = workspace.NewFile(); MmcDurableFiles.WriteNew(patchInput, patchBytes);
        var mutations = prepared.StagedMutations.Select(value => new MmcFileMutation(value.TargetRelativePath, value.ExpectedMissing ? null : value.ExpectedExistingSha256,
                null, value.NewSha256, value.NewSourceRelativePath)).Append(new(patchPath, null, null, patchHash, Path.GetRelativePath(paths.InstanceRoot, patchInput).Replace('\\', '/')))
            .Append(new("fixture.json", definition.InstalledProjection.OutputJsonSha256, projection, MmcDurableFiles.Hash(projection)))
            .Append(new("PCL/InstanceDefinition.json", primary.Sha256, primaryBytes, MmcDurableFiles.Hash(primaryBytes))).ToArray();
        using var scope = quiet.OpenStructuralWrite(lease, definition.Generation);
        var manifest = scope.ConfigSnapshot["MinecraftInstallManifest"] as JObject ?? new();
        manifest["pcllfManagement"] = JObject.Parse(MmcVersionProjection.ManagementHint(definition.InstanceId, next, compiled).ToJsonString());
        scope.SetConfig("MinecraftInstallManifest", manifest);
        Equal(MmcTransactionOutcome.Committed, scope.Execute(new(definition.InstanceId, definition.Generation, next, mutations)).Outcome);
    }
    private static void RoundTrip() {
        using Fixture fixture = new(); var imported = fixture.Import(); var history = new MmcInstanceHistoryBackend();
        using var provider = new InstanceConfigProvider(fixture.VersionRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "old preference" },
            new JObject { ["account-secret-sentinel"] = "never copy launcher preferences" }); provider.Save();
        var first = Capture(fixture, history, "original environment");
        string pointRoot = MmcInstanceHistoryStore.PointRoot(imported.Paths!, first.PointId);
        Equal(false, Directory.EnumerateFiles(pointRoot, "*", SearchOption.AllDirectories).Any(path => Path.GetFileName(path) is "Setup.ini" or "config.json"));
        Equal(false, Directory.EnumerateFiles(pointRoot, "*", SearchOption.AllDirectories).Any(path => path.Contains("/saves/", StringComparison.OrdinalIgnoreCase) || path.Contains("\\saves\\", StringComparison.OrdinalIgnoreCase)));
        Equal(false, Directory.EnumerateFiles(pointRoot, "*", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains("never copy launcher preferences", StringComparison.Ordinal)));
        var selected = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
        var variant = fixture.Backend().AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 21), selected.Definition.InstanceId, selected.Definition.Generation, selected.Sha256)).GetAwaiter().GetResult();
        if (variant.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(variant.Failure);
        fixture.Put("minecraft/mods/test.jar", "new mod"); fixture.Put("minecraft/config/added.cfg", "new config");
        fixture.Put("minecraft/saves/world/level.dat", "current world"); fixture.Put(".minecraft/saves/world/level.dat", "alternate current world");
        fixture.Put(".minecraft/config/alternate.cfg", "alternate current config"); fixture.Put("minecraft/config/xaero/map.cfg", "current map markers");
        File.Delete(Path.Combine(fixture.VersionRoot, "libraries", "active.jar")); File.Delete(Path.Combine(fixture.VersionRoot, "libraries", "orphan.jar"));
        File.Delete(Path.Combine(fixture.Root, "fixture.zip")); Directory.Delete(Path.Combine(fixture.Root, "global-assets"), true);
        var request = Request(fixture, first) with { Checkpoint = phase => {
            if (phase == MmcInstanceHistoryCheckpoint.BeforeRestoreTransaction)
                MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "" }, new JObject { ["unknown-current-setting"] = "latest overlay" });
        } };
        var restored = history.RestoreAsync(request).GetAwaiter().GetResult();
        if (restored.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(restored.Failure);
        Equal(3L, restored.Definition!.Generation); Equal(8, restored.BoundPlan!.CompiledProfile.Environment.JavaMajor);
        Equal(first.InstanceId, restored.Definition.InstanceId); Equal("old mod", fixture.Read("minecraft/mods/test.jar"));
        Equal(false, File.Exists(Path.Combine(fixture.VersionRoot, "minecraft", "config", "added.cfg")));
        Equal("active library", fixture.Read("libraries/active.jar")); Equal("preserved library", fixture.Read("libraries/orphan.jar"));
        AssertPlayers(fixture);
        var preferences = MmcIniConfiguration.CapturePreferences(provider);
        Equal("", preferences.Ini["VersionAdvanceJvm"]); Equal("Custom", preferences.Ini["MmcJvmArgsMode"]);
        Equal("latest overlay", preferences.Config.Value<string>("unknown-current-setting")); Equal("never copy launcher preferences", preferences.Config.Value<string>("account-secret-sentinel"));
        Equal(2L, restored.UndoPoint!.Generation);
        var undo = history.RestoreAsync(Request(fixture, restored.UndoPoint)).GetAwaiter().GetResult();
        if (undo.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(undo.Failure);
        Equal(4L, undo.Definition!.Generation); Equal(21, undo.BoundPlan!.CompiledProfile.Environment.JavaMajor);
        Equal("new mod", fixture.Read("minecraft/mods/test.jar")); Equal("new config", fixture.Read("minecraft/config/added.cfg")); AssertPlayers(fixture);
        Equal(3, history.ListAsync(fixture.VersionRoot).GetAwaiter().GetResult().Length);
        var ready = fixture.Backend().PrepareLaunchAsync(new(fixture.VersionRoot, new("windows", "x86_64", 21), undo.Definition.InstanceId, 4) { ExpectedPrimarySha256 = fixture.Primary().Sha256 }).GetAwaiter().GetResult();
        if (ready.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(ready.Failure); Equal(false, ready.Repaired);
    }
    private static void AssertPlayers(Fixture fixture) {
        Equal("current world", fixture.Read("minecraft/saves/world/level.dat")); Equal("alternate current world", fixture.Read(".minecraft/saves/world/level.dat"));
        Equal("alternate current config", fixture.Read(".minecraft/config/alternate.cfg")); Equal("current map markers", fixture.Read("minecraft/config/xaero/map.cfg"));
    }
    private static void Damaged() {
        using Fixture fixture = new(); var imported = fixture.Import(); var history = new MmcInstanceHistoryBackend(); var point = Capture(fixture, history, "known point");
        string before = fixture.Primary().Sha256; fixture.Put("minecraft/saves/world/level.dat", "preserved world");
        Equal(MmcInstanceBackendStatus.NeedsRecovery, history.RestoreAsync(Request(fixture, point) with { ExpectedPointManifestSha256 = new string('e', 64) }).GetAwaiter().GetResult().Status);
        string pointRoot = MmcInstanceHistoryStore.PointRoot(imported.Paths!, point.PointId);
        var original = imported.Definition!.ArtifactLocks.First();
        string snapshotOriginal = Path.Combine(pointRoot, "versions", "snapshot", original.RecoveryObjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(snapshotOriginal, "damaged snapshot original");
        var damaged = history.RestoreAsync(Request(fixture, point)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, damaged.Status); Equal(null, damaged.UndoPoint); Equal(before, fixture.Primary().Sha256);
        string liveOriginal = imported.Paths!.ResolveRelative(MmcPathRole.Instance, original.RecoveryObjectRelativePath); File.Delete(liveOriginal);
        var capture = history.CaptureAsync(CaptureRequest(fixture, "missing original")).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, capture.Status); Equal(null, capture.Point);
        Equal("preserved world", fixture.Read("minecraft/saves/world/level.dat")); Equal(before, fixture.Primary().Sha256);
    }
    private static void Barriers() {
        using Fixture fixture = new(); var imported = fixture.Import(); var history = new MmcInstanceHistoryBackend(); var point = Capture(fixture, history, "barriers");
        string before = fixture.Primary().Sha256;
        foreach (string marker in new[] { "PCL/MmcSessions/uncertain/receipt.record", "PCL/MmcImportPending.json" }) {
            fixture.Put(marker, "foreign pending bytes");
            Equal(MmcInstanceBackendStatus.NeedsRecovery, history.RestoreAsync(Request(fixture, point)).GetAwaiter().GetResult().Status);
            Equal(MmcInstanceBackendStatus.NeedsRecovery, history.CaptureAsync(CaptureRequest(fixture, "blocked")).GetAwaiter().GetResult().Status);
            Equal(before, fixture.Primary().Sha256); Equal("foreign pending bytes", fixture.Read(marker));
            File.Delete(Path.Combine(fixture.VersionRoot, marker.Replace('/', Path.DirectorySeparatorChar)));
            if (marker.Contains("MmcSessions", StringComparison.Ordinal)) Directory.Delete(Path.Combine(fixture.VersionRoot, "PCL", "MmcSessions", "uncertain"));
        }
        // Even a valid foreign index/manifest cannot transplant another instance into this identity.
        string root = MmcInstanceHistoryStore.PointRoot(imported.Paths!, point.PointId), manifestPath = Path.Combine(root, "manifest.record");
        var manifest = MmcDurableFiles.ReadRecord<MmcHistoryManifest>(manifestPath).Value;
        File.Delete(manifestPath); string unknown = MmcDurableFiles.WriteRecord(manifestPath, manifest with { CoverageVersion = 2 });
        string indexPath = imported.Paths!.ResolveRelative(MmcPathRole.Instance, "PCL/MmcHistory/index/" + point.PointId.ToString("N") + ".record");
        File.Delete(indexPath); MmcDurableFiles.WriteRecord(indexPath, new MmcHistoryIndex(1, point.PointId, point.InstanceId, unknown));
        Equal(MmcInstanceBackendStatus.NeedsRecovery, history.RestoreAsync(Request(fixture, point) with { ExpectedPointManifestSha256 = unknown }).GetAwaiter().GetResult().Status);
        Equal(before, fixture.Primary().Sha256);
        var selected = fixture.Primary(); var definition = selected.Definition;
        var changed = definition with { InstanceId = Guid.NewGuid(), InstalledProjection = definition.InstalledProjection! with { BoundFingerprint = new string('0', 64) } };
        changed = changed with { InstalledProjection = changed.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(changed) } };
        var saved = MmcSavedSourceStore.LoadAsync(imported.Paths, definition).GetAwaiter().GetResult();
        var compiled = MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, definition, []);
        var rejected = MmcPlanBinder.BindRestore(definition, changed, compiled, [], new(definition.InstanceId, definition.Generation,
            definition.InstalledProjection!.BoundFingerprint, changed.InstalledProjection!.BoundFingerprint, definition.InstalledProjection.OutputJsonSha256));
        Equal(true, rejected.HasErrors); Equal("MMC_HISTORY_AUTHORITY", rejected.Diagnostics.Single().Code);
        var future = definition with { Generation = definition.Generation + 1,
            InstalledProjection = definition.InstalledProjection! with { ManifestGeneration = definition.Generation + 1, BoundFingerprint = new string('0', 64) } };
        future = future with { InstalledProjection = future.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(future) } };
        var futureRejected = MmcPlanBinder.BindRestore(definition, future, compiled, [], new(definition.InstanceId, definition.Generation,
            definition.InstalledProjection!.BoundFingerprint, future.InstalledProjection!.BoundFingerprint, definition.InstalledProjection.OutputJsonSha256));
        Equal(true, futureRejected.HasErrors); Equal("MMC_HISTORY_AUTHORITY", futureRejected.Diagnostics.Single().Code);
    }
    private static void Transactions() {
        using Fixture fixture = new(); fixture.Import(); var history = new MmcInstanceHistoryBackend(); var point = Capture(fixture, history, "transaction source");
        foreach (string mode in new[] { "cancel-prepared", "interrupt-prepared", "interrupt-decided", "cancel-decided" }) {
            fixture.Put("minecraft/mods/test.jar", "current " + mode); fixture.Put("minecraft/saves/world/level.dat", "world " + mode);
            var selected = fixture.Primary(); using CancellationTokenSource cancellation = new();
            bool afterDecision = mode.EndsWith("decided", StringComparison.Ordinal);
            var result = history.RestoreAsync(Request(fixture, point) with { TransactionCheckpoint = (phase, _) => {
                if (phase != (afterDecision ? MmcTransactionCheckpoint.CommitDecided : MmcTransactionCheckpoint.Prepared)) return;
                if (mode.StartsWith("cancel", StringComparison.Ordinal)) cancellation.Cancel(); else throw new MmcTransactionInterruptionException("controlled history interruption");
            } }, cancellation.Token).GetAwaiter().GetResult();
            if (mode.StartsWith("interrupt", StringComparison.Ordinal)) {
                Equal(MmcInstanceBackendStatus.NeedsRecovery, result.Status);
                if (result.UndoPoint is null) throw new Exception("Restore failed before transaction: " + result.Failure);
                using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.VersionRoot);
                using var gate = MmcAdmissionGate.Acquire(fixture.VersionRoot, selected.Definition.InstanceId, TimeSpan.Zero);
                var recovered = MmcFileTransactions.RecoverPending(gate); Equal(1, recovered.Count);
                Equal(afterDecision ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, recovered[0].Outcome);
            } else Equal(afterDecision ? MmcInstanceBackendStatus.Succeeded : MmcInstanceBackendStatus.Cancelled, result.Status);
            Equal(selected.Definition.Generation + (afterDecision ? 1 : 0), fixture.Primary().Definition.Generation);
            Equal(afterDecision ? "old mod" : "current " + mode, fixture.Read("minecraft/mods/test.jar"));
            Equal("world " + mode, fixture.Read("minecraft/saves/world/level.dat"));
            Equal(true, result.UndoPoint is not null);
        }
    }
    private static MmcInstanceHistoryCaptureRequest CaptureRequest(Fixture fixture, string label) { var current = fixture.Primary(); return new(fixture.VersionRoot, current.Definition.InstanceId, current.Definition.Generation, current.Sha256, label); }
    private static MmcInstanceHistoryPoint Capture(Fixture fixture, MmcInstanceHistoryBackend history, string label) {
        var result = history.CaptureAsync(CaptureRequest(fixture, label)).GetAwaiter().GetResult();
        if (result.Status != MmcInstanceBackendStatus.Succeeded || result.Point is null) throw new Exception(result.Failure); return result.Point;
    }
    private static MmcInstanceHistoryRestoreRequest Request(Fixture fixture, MmcInstanceHistoryPoint point) { var current = fixture.Primary(); return new(fixture.VersionRoot, current.Definition.InstanceId, current.Definition.Generation, current.Sha256, point.PointId, point.ManifestSha256); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void EditedMappedResource() {
        using Fixture fixture = new(mappedResources: true); fixture.Import(); var history = new MmcInstanceHistoryBackend();
        var point = Capture(fixture, history, "mapped original"); string before = fixture.Primary().Sha256;
        fixture.Put("minecraft/resources/owned.ogg", "player edited resource");
        var capture = history.CaptureAsync(CaptureRequest(fixture, "must retain every byte")).GetAwaiter().GetResult();
        var restore = history.RestoreAsync(Request(fixture, point)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, capture.Status); Equal(null, capture.Point);
        Equal(MmcInstanceBackendStatus.NeedsRecovery, restore.Status); Equal(null, restore.UndoPoint);
        Equal(before, fixture.Primary().Sha256); Equal("player edited resource", fixture.Read("minecraft/resources/owned.ogg"));
    }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-history-" + Guid.NewGuid().ToString("N"));
        internal string VersionRoot => Path.Combine(Root, "game", "versions", "fixture");
        private readonly HttpClient client = new(new NoNetwork());
        private readonly byte[] index;
        private readonly byte[] mappedBytes = Encoding.UTF8.GetBytes("owned mapped original");
        private bool imported;
        internal Fixture(bool mappedResources = false) {
            string objectHash = Convert.ToHexStringLower(SHA1.HashData(mappedBytes));
            index = mappedResources ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
                map_to_resources = true, objects = new Dictionary<string, object> { ["owned.ogg"] = new { hash = objectHash, size = mappedBytes.Length } }
            }) : Encoding.UTF8.GetBytes("{\"objects\":{}}");
            Directory.CreateDirectory(Root); string digest = Convert.ToHexStringLower(SHA1.HashData(index));
            using var zip = ZipFile.Open(Path.Combine(Root, "fixture.zip"), ZipArchiveMode.Create);
            Add("pack/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
            Add("pack/instance.cfg", "name=Fixture\n");
            Add("pack/patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"active.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/index.json","sha1":"{{digest}}","size":{{index.Length}}},"compatibleJavaMajors":[8,21]}""");
            Add("pack/libraries/active.jar", "active library"); Add("pack/libraries/orphan.jar", "preserved library");
            Add("pack/minecraft/mods/test.jar", "old mod"); Add("pack/minecraft/config/first.cfg", "old config"); Add("outer-doc.txt", "opaque original");
            void Add(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(new(Path.Combine(Root, "metadata"), new Uri("https://metadata.invalid/"), client),
            new(Path.Combine(Root, "global-libraries"), Path.Combine(Root, "global-assets"), Path.Combine(Root, "global-sources")), value => value,
            async (batch, token) => { if (imported) throw new IOException("No network after initial fixture import."); foreach (var file in batch) await File.WriteAllBytesAsync(file.DownloadPath, file.Urls.Any(url => url.EndsWith("index.json", StringComparison.Ordinal)) ? index : mappedBytes, token); });
        internal MmcInstanceBackendResult Import() {
            var result = Backend().ImportAsync(new(Path.Combine(Root, "fixture.zip"), VersionRoot, "fixture", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure); imported = true; return result;
        }
        internal MmcStoredPrimaryDefinition Primary() => MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(VersionRoot));
        internal void Put(string relative, string text) { string path = Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        internal string Read(string relative) => File.ReadAllText(Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        public void Dispose() { client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("History fixture is offline."); }
}
