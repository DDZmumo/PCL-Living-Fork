using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcConfigurationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC configuration separate processes rebase own preference keys onto latest disk", CrossProcessRebase);
        yield return ("MMC configuration old cross-process manifest queue cannot overwrite a published generation", StaleManifest);
        yield return ("MMC configuration Set remains nonblocking while a structural scope owns admission", NonblockingSet);
        yield return ("MMC configuration Shared PreviewGet Reset uses the same queued key writer", PreviewReset);
        yield return ("MMC configuration rollback invalidates snapshots without consuming pending preferences", RollbackEpoch);
        yield return ("MMC configuration admission reload and paused inflight writer follow one lock order", LockOrder);
        yield return ("MMC configuration typed values preserve Set and repeated Read reference identity", TypedIdentity);
        yield return ("MMC INI preserves ordinary synchronous writes and queues busy or managed writes with structural fencing", IniAdmission);
        yield return ("MMC INI pending queue survives without an instance provider and flushes at owned shutdown", IniWithoutProvider);
        yield return ("MMC configuration queued writes never recreate a deleted version root", DeletedRoot);
        yield return ("MMC configuration a reused path cannot migrate old Java preferences into a new instance ID", ReusedIdentity);
        yield return ("MMC configuration ordinary full temporary roots retain writable preference behavior", OrdinaryTemporaryRoot);
        yield return ("MMC configuration trusts ordinary root junctions but rejects linked configuration descendants", OrdinaryRootJunction);
        yield return ("MMC configuration malformed input constructs a readonly error provider without overwriting bytes", MalformedConstructor);
        yield return ("MMC settings engine validates fixed identity before path caches and retains context at INI writes", SettingsContextBridge);
        yield return ("MMC projection repair requires a hash-bound published primary without relaxing normal edits", ProjectionRepair);
        yield return ("MMC new identity reads and edits never adopt old JSON or INI pending overlays", PendingReadIsolation);
        yield return ("MMC settings cache namespace survives controlled validation to identity handoff races", CacheIdentityBarriers);
        yield return ("MMC managed layout rejects before cache mutation and before INI overlay", ImmediateLayoutFence);
        yield return ("MMC ordinary invalid integer and boolean preferences retain conversion fallback without masking read failures", OrdinaryConversionFallback);
        if (typeof(InstanceConfigProvider).Assembly.GetType("PCLCS.Configs") is not null)
            yield return ("MMC configuration actual JavaForced deserialization and PreviewGet Reset use the coordinated provider", ActualJavaReset);
    }
    internal static bool TryRunChild(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--mmc-configuration-child") return false;
        try {
            if (args.Length != 5) throw new Exception("Invalid controlled config child.");
            using var provider = new InstanceConfigProvider(args[2]);
            if (args[1] == "java-reset") { RunActualJavaReset(provider, args[2]); return true; }
            if (args[1] == "lockorder") { RunLockOrder(provider, args[2]); return true; }
            if (args[1] == "rebase") { provider.Set("childPreference", "B", false); provider.Save(); return true; }
            using (MmcConfigurationCoordinator.Quiesce(args[2])) {
                provider.Set("MinecraftInstallManifest", new JObject { ["stale"] = true }, false);
                provider.Set("childPreference", "B-after-conversion", false);
                File.WriteAllText(args[3], "ready"); WaitFile(args[4], 10000);
            }
            try { provider.Save(); } catch (IOException) { }
            JObject disk = JObject.Parse(File.ReadAllText(Path.Combine(args[2], "PCL", "config.json")));
            if (disk["MinecraftInstallManifest"]?["pcllfManagement"]?["generation"]?.Value<int>() != 1) exitCode = 32;
        } catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 31; }
        return true;
    }
    private static void CrossProcessRebase() {
        using Fixture fixture = new(); using var provider = new InstanceConfigProvider(fixture.Root);
        Equal("kept", provider.Read("unknown", "", false));
        using (Process child = StartChild("rebase", fixture)) WaitChild(child);
        provider.Set("parentPreference", "A", false); provider.Save();
        JObject disk = fixture.Config(); Equal("B", disk.Value<string>("childPreference")); Equal("A", disk.Value<string>("parentPreference")); Equal("kept", disk.Value<string>("unknown"));
    }
    private static void StaleManifest() {
        using Fixture fixture = new();
        using Process child = StartChild("stale", fixture); WaitFile(fixture.Ready, 10000);
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var lease = fixture.Lease())
        using (var scope = quiet.OpenStructuralWrite(lease, 0)) {
            scope.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = fixture.Hint(1) });
            Equal(MmcTransactionOutcome.Committed, scope.Execute(fixture.Publish(0)).Outcome);
        }
        File.WriteAllText(fixture.Go, "go"); WaitChild(child);
        Equal(1, fixture.Config()["MinecraftInstallManifest"]!["pcllfManagement"]!.Value<int>("generation"));
        Equal(false, fixture.Config().ContainsKey("childPreference"));
    }
    private static void NonblockingSet() {
        using Fixture fixture = new(); fixture.Install(); using var first = new InstanceConfigProvider(fixture.Root); using var second = new InstanceConfigProvider(fixture.Root);
        first.Set("before", "one", false); Equal("one", second.Read("before", "", false));
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var lease = fixture.Lease())
        using (var scope = quiet.OpenStructuralWrite(lease, 1)) {
            Task setter = Task.Run(() => second.Set("during", "two", false));
            Equal(true, setter.Wait(300));
            scope.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = fixture.Hint(2) });
            Equal(MmcTransactionOutcome.Committed, scope.Execute(fixture.Publish(1)).Outcome);
        }
        first.Save(); Equal("one", fixture.Config().Value<string>("before")); Equal("two", fixture.Config().Value<string>("during"));
    }
    private static void PreviewReset() {
        using Fixture fixture = new(); using var provider = new InstanceConfigProvider(fixture.Root);
        // Actual fixed Shared DynamicConfigEntry callback/Reset contract, with a self-owned synthetic
        // value. Full Java deserialization is left to the existing isolated Java/product fixture.
        DynamicConfigEntry<string> forced = new("InstanceForcedJava", null);
        forced.PreviewGet += (value, owner) => { if (value.Value == "not-installed") { value.Value = null; forced.Reset(owner); } };
        forced.Set("not-installed", provider); provider.Save();
        Equal<string?>(null, forced.Get(provider)); provider.Save();
        Equal(false, fixture.Config().ContainsKey("InstanceForcedJava")); Equal("kept", fixture.Config().Value<string>("unknown"));
    }
    private static void RollbackEpoch() {
        using Fixture fixture = new(); fixture.Install(); using var provider = new InstanceConfigProvider(fixture.Root);
        provider.Set("queued", "retained", false); long before = provider.Epoch;
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var lease = fixture.Lease())
        using (var scope = quiet.OpenStructuralWrite(lease, 1)) {
            scope.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = fixture.Hint(2) });
            Equal(MmcTransactionOutcome.Aborted, scope.Execute(fixture.Publish(1), checkpoint: (phase, _) => {
                if (phase == MmcTransactionCheckpoint.BeforeDecision) throw new IOException("controlled rollback");
            }).Outcome);
        }
        Equal(true, provider.Epoch > before); Equal("retained", provider.Read("queued", "", false)); provider.Save();
        Equal("retained", fixture.Config().Value<string>("queued")); Equal(1, JObject.Parse(File.ReadAllText(fixture.DefinitionFile)).Value<int>("generation"));
    }
    private static void LockOrder() {
        using Fixture fixture = new(); using Process child = StartChild("lockorder", fixture); WaitChild(child);
    }
    private static void RunLockOrder(InstanceConfigProvider provider, string root) {
        using ManualResetEventSlim registered = new(), paused = new();
        MmcConfigurationCoordinator.OperationObserver = (path, phase) => {
            if (!path.Equals(provider.VersionRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (phase == "Registered") registered.Set(); else if (phase == "QuiescePaused") paused.Set();
        };
        try {
            provider.Set("pending", "B", false); provider.DiscardCache();
            using var gate = MmcAdmissionGate.Acquire(root, Guid.NewGuid(), TimeSpan.Zero);
            Task writer = Task.Run(provider.Save);
            if (!registered.Wait(3000)) throw new Exception("Writer did not register before admission wait.");
            Task quiescer = Task.Run(() => { using var quiet = MmcConfigurationCoordinator.Quiesce(root); });
            if (!paused.Wait(3000)) throw new Exception("Quiescer did not enter pause/drain.");
            // A real gate owner refreshes without waiting for the registered writer that needs its gate.
            Equal("kept", provider.Read("unknown", "", false));
            gate.Dispose();
            if (!Task.WaitAll([writer, quiescer], 5000)) throw new Exception("Admission/read/drain cycle did not complete.");
        } finally { MmcConfigurationCoordinator.OperationObserver = null; }
    }
    private static void TypedIdentity() {
        using Fixture fixture = new(); using var provider = new InstanceConfigProvider(fixture.Root);
        List<string> original = ["value"]; provider.Set("typed", original, false);
        Equal(true, ReferenceEquals(original, provider.Read<List<string>>("typed", null, false)));
        provider.Save(); List<string>? read = provider.Read<List<string>>("typed", null, false);
        Equal(true, ReferenceEquals(read, provider.Read<List<string>>("typed", null, false)));
        provider.DiscardCache(); Equal(false, ReferenceEquals(read, provider.Read<List<string>>("typed", null, false)));
    }
    private static void IniAdmission() {
        using Fixture fixture = new(); var state = MmcConfigurationCoordinator.StateFor(fixture.Root); int invalidations = 0;
        using var provider = new InstanceConfigProvider(fixture.Root, () => { Equal(false, Monitor.IsEntered(state.Sync)); Interlocked.Increment(ref invalidations); });
        Equal(0, invalidations);
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();
        string file = Path.Combine(fixture.Root, "PCL", "Setup.ini"); File.WriteAllText(file, "Unknown:kept\r\n");
        LegacyBaseOperations.WriteIni(file, "CustomInfo", "ordinary-sync", cache);
        Equal("ordinary-sync", LegacyBaseOperations.ParseIni(File.ReadAllText(file))["CustomInfo"]);
        using (var gate = fixture.Lease()) {
            Task queued = Task.Run(() => LegacyBaseOperations.WriteIni(file, "CustomInfo", "busy-queued", cache));
            Equal(true, queued.Wait(300));
            Equal("busy-queued", LegacyBaseOperations.ReadIni(file, "CustomInfo", "", cache));
            Equal("ordinary-sync", LegacyBaseOperations.ParseIni(File.ReadAllText(file))["CustomInfo"]);
        }
        provider.Save(); Equal("busy-queued", LegacyBaseOperations.ParseIni(File.ReadAllText(file))["CustomInfo"]);
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var lease = fixture.Lease())
        using (var scope = quiet.OpenStructuralWrite(lease, 0)) {
            scope.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = fixture.Hint(1) });
            scope.SetIni("VersionArgumentIndieV2", "True"); scope.SetIni("CustomInfo", "pack");
            Equal(MmcTransactionOutcome.Committed, scope.Execute(fixture.Publish(0)).Outcome);
        }
        using var managedProvider = new InstanceConfigProvider(fixture.Root);
        bool manifestRejected = false;
        try { managedProvider.Set("MinecraftInstallManifest", new JObject { ["stale"] = true }, false); } catch (IOException) { manifestRejected = true; }
        Equal(true, manifestRejected);
        MmcIniConfiguration.WithInstanceContext(managedProvider, () => LegacyBaseOperations.WriteIni(file, "CustomInfo", "next-launch", cache));
        MmcIniConfiguration.WithInstanceContext(managedProvider, () => LegacyBaseOperations.WriteIni(file, "VersionArgumentIndieV2", "False", cache));
        try { managedProvider.Save(); } catch (IOException) { }
        var saved = LegacyBaseOperations.ParseIni(File.ReadAllText(file));
        Equal("True", saved["VersionArgumentIndieV2"]); Equal("next-launch", saved["CustomInfo"]); Equal("kept", saved["Unknown"]);
        bool rejected = false;
        try { LegacyBaseOperations.UpdateIniValue(file, "VersionArgumentIndieV2", _ => "False", cache); } catch (IOException) { rejected = true; }
        Equal(true, rejected);
        Equal(true, invalidations > 0);
    }
    private static void IniWithoutProvider() {
        using Fixture fixture = new();
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();
        string file = Path.Combine(fixture.Root, "PCL", "Setup.ini"); File.WriteAllText(file, "Unknown:keep\r\n");
        using (var gate = fixture.Lease()) {
            Task queued = Task.Run(() => LegacyBaseOperations.WriteIni(file, "CustomInfo", "providerless", cache));
            Equal(true, queued.Wait(300));
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
        InstanceConfigProviders.SaveAll();
        var saved = LegacyBaseOperations.ParseIni(File.ReadAllText(file)); Equal("providerless", saved["CustomInfo"]); Equal("keep", saved["Unknown"]);
    }
    private static void DeletedRoot() {
        using Fixture fixture = new(); using var provider = new InstanceConfigProvider(fixture.Root);
        provider.Set("pending", "must-not-recreate", false); Directory.Delete(fixture.Root, true);
        bool blocked = false;
        try { provider.Save(); } catch (IOException) { blocked = true; }
        Equal(true, blocked); Equal(false, Directory.Exists(fixture.Root));
        MmcConfigurationCoordinator.DetachDeletedRoot(fixture.Root);
        Equal(false, Directory.Exists(fixture.Root));
        fixture.RecreateManaged(Guid.NewGuid());
        using var attached = new InstanceConfigProvider(fixture.Root); attached.Set("new-owner", "allowed", false); attached.Save();
        bool oldRejected = false; try { provider.Set("new-owner", "old-provider", false); } catch (IOException) { oldRejected = true; }
        Equal(true, oldRejected); Equal("allowed", fixture.Config().Value<string>("new-owner"));
    }
    private static void ReusedIdentity() {
        using Fixture fixture = new(); fixture.Install(); using var old = new InstanceConfigProvider(fixture.Root);
        InstanceConfigProvider? fresh = null;
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var gate = fixture.Lease()) {
            old.Set("oldOnly", "java-A", false);
            Directory.Delete(fixture.Root, true); fixture.RecreateManaged(Guid.NewGuid());
            fresh = new InstanceConfigProvider(fixture.Root); fresh.Set("InstanceForcedJava", "java-B", false);
            bool blocked = false;
            try { old.Set("InstanceForcedJava", "java-A-late", false); } catch (IOException) { blocked = true; }
            Equal(true, blocked);
        }
        using (fresh) { try { fresh!.Save(); } catch (IOException) { } }
        Equal("java-B", fixture.Config().Value<string>("InstanceForcedJava")); Equal(false, fixture.Config().ContainsKey("oldOnly"));
    }
    private static void OrdinaryTemporaryRoot() {
        string scope = Path.Combine(Path.GetTempPath(), "pcllf-config-plain-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(scope, "temporary-version"); Directory.CreateDirectory(root);
        try {
            Directory.CreateDirectory(Path.Combine(root, "PCL"));
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding("GB18030")!;
            File.WriteAllBytes(Path.Combine(root, "PCL", "config.json"), encoding.GetBytes("{\"historical\":\"旧配置中文\"}"));
            File.WriteAllBytes(Path.Combine(root, "temporary-version.json"), encoding.GetBytes("{\"id\":\"temp\",\"description\":\"普通版本中文\"}"));
            using var provider = new InstanceConfigProvider(root); provider.Set("plain", "value", false); provider.Save();
            Equal("value", provider.Read("plain", "", false));
            Equal("旧配置中文", provider.Read("historical", "", false));
            Equal("value", JObject.Parse(File.ReadAllText(Path.Combine(root, "PCL", "config.json"))).Value<string>("plain"));
        } finally { InstanceConfigProviders.FlushAndDisposeWithin(scope); Directory.Delete(scope, true); }
    }
    private static void OrdinaryRootJunction() {
        if (!OperatingSystem.IsWindows()) return;
        string scope = Path.Combine(Path.GetTempPath(), "pcllf-config-junction-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(scope, "actual-target"), link = Path.Combine(scope, "logical-version"), outside = Path.Combine(scope, "separate-pcl");
        Directory.CreateDirectory(target); Directory.CreateDirectory(outside); CreateJunction(link, target);
        try {
            using (var provider = new InstanceConfigProvider(link)) {
                provider.Set("ordinary", "through-root-link", false); provider.Save();
                Equal("through-root-link", provider.Read("ordinary", "", false));
            }
            string logicalHint = Path.Combine(target, "logical-version.json");
            File.WriteAllText(logicalHint, "{\"pcllfManagement\":{\"schemaVersion\":99,\"managementKind\":\"Future\"}}");
            Equal(MmcManagementState.ManagedUnsupported, MmcConfigurationPaths.ReadManagement(link, MmcConfigurationPaths.PhysicalRoot(link)).State);
            File.Delete(logicalHint);
            string pcl = Path.Combine(target, "PCL"); Directory.Move(pcl, Path.Combine(scope, "pcl-backup")); CreateJunction(pcl, outside);
            try {
                File.WriteAllText(Path.Combine(outside, "config.json"), "{\"external\":\"keep\"}");
                using var rejected = new InstanceConfigProvider(link); bool blocked = false;
                try { rejected.Set("ordinary", "must-not-write-child-link", false); rejected.Save(); } catch (IOException) { blocked = true; }
                Equal(true, blocked); Equal("keep", JObject.Parse(File.ReadAllText(Path.Combine(outside, "config.json"))).Value<string>("external"));
            } finally { Directory.Delete(pcl); }
        } finally { InstanceConfigProviders.FlushAndDisposeWithin(scope); Directory.Delete(link); Directory.Delete(scope, true); }
    }
    private static void MalformedConstructor() {
        using Fixture fixture = new(); string file = Path.Combine(fixture.Root, "PCL", "config.json"); File.WriteAllText(file, "{");
        using var provider = new InstanceConfigProvider(fixture.Root); Equal(true, provider.LastSaveFailure is not null);
        bool rejected = false; try { provider.Set("bad", "must-not-write", false); provider.Save(); } catch (IOException) { rejected = true; }
        Equal(true, rejected); Equal("{", File.ReadAllText(file));
    }
    private static void ActualJavaReset() {
        using Fixture fixture = new(); using Process child = StartChild("java-reset", fixture); WaitChild(child);
    }
    private static void SettingsContextBridge() {
        using Fixture fixture = new(); fixture.Install();
        string path = fixture.Root + Path.DirectorySeparatorChar, file = Path.Combine(fixture.Root, "PCL", "Setup.ini");
        File.WriteAllText(file, "VersionArgumentTitle:from-A\r\n");
        PreferenceEntry entry = new("VersionArgumentTitle"); int invalidations = 0;
        using var oldProvider = new InstanceConfigProvider(fixture.Root);
        Equal(0, invalidations);
        PreferenceOwner old = new(path, oldProvider);
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();
        Dictionary<string, PreferenceEntry> entries = new() { [entry.Key] = entry };
        var host = new LegacySettingsHost<PreferenceOwner> {
            InstancePath = owner => owner.Path, Identify = () => "synthetic", RegistryFolder = () => "unused",
            ReadIni = (address, key, fallback) => LegacyBaseOperations.ReadIni(address, key, fallback, cache),
            WriteIni = (_, _, _) => throw new Exception("Instance context was lost before writing."),
            DeleteIni = (_, _) => throw new Exception("Instance context was lost before resetting."),
            InstanceCacheKey = owner => owner.Path + owner.Config.PreferenceCacheNamespace,
            ValidateInstanceConfiguration = owner => { owner.Config.EnsurePreferenceCacheInvalidator(() => { entry.ValueCache.TryRemove(owner.Path + owner.Config.PreferenceCacheNamespace, out _); invalidations++; }); owner.Config.AssertAttached(); },
            ValidateInstanceSettingWrite = (owner, key) => MmcIniConfiguration.ValidatePreferenceWrite(owner.Config, key),
            ReadInstanceIni = (owner, key, fallback) => MmcIniConfiguration.WithInstanceContext<string?>(owner.Config, () => LegacyBaseOperations.ReadIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), key, fallback, cache)),
            HasInstanceIni = (owner, key) => MmcIniConfiguration.WithInstanceContext<bool>(owner.Config, () => LegacyBaseOperations.HasIniKey(Path.Combine(owner.Path, "PCL", "Setup.ini"), key, cache)),
            WriteInstanceIni = (owner, key, value) => MmcIniConfiguration.WithInstanceContext(owner.Config, () => LegacyBaseOperations.WriteIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), key, value, cache)),
            DeleteInstanceIni = (owner, key) => MmcIniConfiguration.WithInstanceContext(owner.Config, () => LegacyBaseOperations.WriteIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), key, null, cache)),
            Log = (_, _, _, _) => { }
        };
        var engine = new LegacySettingsEngine<PreferenceOwner, PreferenceEntry, LegacyAccountMigrationPlan>(() => entries, () => new object(), () => new(), host);
        entry.SaveAction = owner => engine.SaveEntry(entry, owner);
        Equal("from-A", engine.Get(entry.Key, old));
        InstanceConfigProvider? freshProvider = null;
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var gate = fixture.Lease()) {
            Directory.Delete(fixture.Root, true); fixture.RecreateManaged(Guid.NewGuid()); File.WriteAllText(file, "VersionArgumentTitle:from-B\r\n");
            int beforeConstruction = invalidations; freshProvider = new(fixture.Root);
            Equal(beforeConstruction, invalidations); // No callback against a half-constructed model.
            PreferenceOwner current = new(path, freshProvider);
            Equal("from-B", engine.Get(entry.Key, current));
            engine.Set(entry.Key, "saved-B", current); engine.Set(entry.Key, "stale-A", old);
            Equal("saved-B", entry.GetCache(current));
        }
        using (freshProvider) freshProvider!.Save();
        Equal("saved-B", LegacyBaseOperations.ParseIni(File.ReadAllText(file))[entry.Key]);
    }
    private static void ProjectionRepair() {
        foreach (bool removeConfigHint in new[] { false, true }) {
            using Fixture fixture = new();
            fixture.Install();
            var published = MmcDefinitionTests.Published(MmcDefinitionTests.Draft() with { InstanceId = fixture.Id });
            byte[] original = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(published)); File.WriteAllBytes(fixture.DefinitionFile, original);
            if (removeConfigHint) File.WriteAllText(Path.Combine(fixture.Root, "PCL", "config.json"), "{\"unknown\":\"kept\"}");
            else File.Delete(Path.Combine(fixture.Root, "sample.json"));
            using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root); using var lease = fixture.Lease();
            bool defaultRejected = false; try { using var invalid = quiet.OpenStructuralWrite(lease, 1); } catch (IOException) { defaultRejected = true; }
            Equal(true, defaultRejected);
            string expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            using var repair = quiet.OpenProjectionRepair(lease, 1, expectedHash);
            repair.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = fixture.Hint(2) });
            byte[] updated = Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(MmcDefinitionTests.Published(published with { Generation = 2 })));
            byte[] version = Encoding.UTF8.GetBytes(new JObject { ["pcllfManagement"] = fixture.Hint(2) }.ToString());
            var request = new MmcFileTransactionRequest(fixture.Id, 1, 2, [
                new("PCL/InstanceDefinition.json", expectedHash, updated, Convert.ToHexString(SHA256.HashData(updated)).ToLowerInvariant()),
                new("sample.json", removeConfigHint ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Root, "sample.json")))).ToLowerInvariant() : null, version, Convert.ToHexString(SHA256.HashData(version)).ToLowerInvariant())]);
            Equal(MmcTransactionOutcome.Committed, repair.Execute(request).Outcome);
            Equal(MmcManagementState.ManagedMmc, MmcManagementProbe.Read(fixture.Root).State);
            Equal("kept", fixture.Config().Value<string>("unknown"));
        }
        foreach (string failure in new[] { "hash", "unsupported", "damaged", "identity", "generation", "draft", "future-hint" }) {
            using Fixture fixture = new(); fixture.Install();
            var primary = MmcDefinitionTests.Published(MmcDefinitionTests.Draft() with { InstanceId = failure == "identity" ? Guid.NewGuid() : fixture.Id, Generation = failure == "generation" ? 2 : 1 });
            string text = MmcDefinitionCodec.Serialize(primary);
            if (failure == "unsupported") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
            if (failure == "damaged") text = "{";
            if (failure == "draft") {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
                options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                text = System.Text.Json.JsonSerializer.Serialize(primary with { InstalledProjection = null }, options);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(fixture.DefinitionFile, bytes); File.Delete(Path.Combine(fixture.Root, "sample.json"));
            string expected = failure == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (failure == "future-hint") {
                JObject config = fixture.Config(); config["MinecraftInstallManifest"]!["pcllfManagement"]!["schemaVersion"] = 99;
                File.WriteAllText(Path.Combine(fixture.Root, "PCL", "config.json"), config.ToString());
            }
            using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root); using var lease = fixture.Lease();
            bool rejected = false; try { using var repair = quiet.OpenProjectionRepair(lease, 1, expected); } catch (IOException) { rejected = true; }
            Equal(true, rejected); Equal(false, File.Exists(Path.Combine(fixture.Root, "sample.json"))); Equal(text, File.ReadAllText(fixture.DefinitionFile));
        }
    }
    private sealed record PreferenceOwner(string Path, IInstanceConfigProvider Config);
    private sealed class PreferenceEntry(string key, object? fallback = null) : LegacySettingData<PreferenceOwner>(key, fallback ?? "", false, null, (error, _) => throw error) {
        public Action<PreferenceOwner?> SaveAction = null!;
        public override LegacySettingSource GetSourceCode() => LegacySettingSource.Instance;
        protected override string GetInstancePath(PreferenceOwner owner) => owner.Path;
        public override void Save(PreferenceOwner? owner = null) => SaveAction(owner);
    }
    private sealed class PreferenceHarness {
        public readonly PreferenceEntry Entry;
        public readonly LegacySettingsHost<PreferenceOwner> Host;
        public readonly LegacySettingsEngine<PreferenceOwner, PreferenceEntry, LegacyAccountMigrationPlan> Engine;
        public PreferenceHarness(string key, object? fallback = null) {
            Entry = new(key, fallback);
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();
            Host = new() {
                InstancePath = owner => owner.Path, Identify = () => "synthetic", RegistryFolder = () => "unused",
                InstanceCacheKey = owner => owner.Path + owner.Config.PreferenceCacheNamespace,
                ValidateInstanceConfiguration = owner => {
                    owner.Config.EnsurePreferenceCacheInvalidator(() => Entry.ValueCache.TryRemove(owner.Path + owner.Config.PreferenceCacheNamespace, out _));
                    owner.Config.AssertAttached();
                },
                ValidateInstanceSettingWrite = (owner, name) => MmcIniConfiguration.ValidatePreferenceWrite(owner.Config, name),
                ReadInstanceIni = (owner, name, value) => MmcIniConfiguration.WithInstanceContext<string?>(owner.Config, () => LegacyBaseOperations.ReadIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), name, value, cache)),
                HasInstanceIni = (owner, name) => MmcIniConfiguration.WithInstanceContext<bool>(owner.Config, () => LegacyBaseOperations.HasIniKey(Path.Combine(owner.Path, "PCL", "Setup.ini"), name, cache)),
                WriteInstanceIni = (owner, name, value) => MmcIniConfiguration.WithInstanceContext(owner.Config, () => LegacyBaseOperations.WriteIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), name, value, cache)),
                DeleteInstanceIni = (owner, name) => MmcIniConfiguration.WithInstanceContext(owner.Config, () => LegacyBaseOperations.WriteIni(Path.Combine(owner.Path, "PCL", "Setup.ini"), name, null, cache)),
                Log = (_, _, _, _) => { }
            };
            Dictionary<string, PreferenceEntry> entries = new() { [key] = Entry };
            Engine = new(() => entries, () => new object(), () => new(), Host); Entry.SaveAction = owner => Engine.SaveEntry(Entry, owner);
        }
    }
    private static void PendingReadIsolation() {
        using Fixture fixture = new(); fixture.Install(); using var old = new InstanceConfigProvider(fixture.Root);
        var iniCache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();
        string file = Path.Combine(fixture.Root, "PCL", "Setup.ini"); InstanceConfigProvider? fresh = null;
        using (var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root))
        using (var gate = fixture.Lease()) {
            old.Set("carry", "A", false); old.Set("onlyA", "A", false); old.Remove("onlyB");
            MmcIniConfiguration.WithInstanceContext(old, () => { LegacyBaseOperations.WriteIni(file, "CustomInfo", "A", iniCache); LegacyBaseOperations.WriteIni(file, "onlyA", "A", iniCache); });
            Directory.Delete(fixture.Root, true); fixture.RecreateManaged(Guid.NewGuid());
            JObject config = fixture.Config(); config["carry"] = "B"; config["onlyB"] = "keep";
            File.WriteAllText(Path.Combine(fixture.Root, "PCL", "config.json"), config.ToString()); File.WriteAllText(file, "CustomInfo:B\r\n");
            byte[] before = File.ReadAllBytes(Path.Combine(fixture.Root, "PCL", "config.json"));
            fresh = new(fixture.Root); Equal("B", fresh.Read("carry", "", false)); Equal(true, fresh.HasValue("onlyB")); Equal(false, fresh.HasValue("onlyA"));
            PreferenceHarness settings = new("CustomInfo"); PreferenceOwner current = new(fixture.Root + Path.DirectorySeparatorChar, fresh);
            Equal("B", settings.Engine.Get("CustomInfo", current)); Equal(true, settings.Engine.HasSaved("CustomInfo", current));
            Equal(false, MmcIniConfiguration.WithInstanceContext<bool>(fresh, () => LegacyBaseOperations.HasIniKey(file, "onlyA", iniCache)));
            new DynamicConfigEntry<string>("carry", "").Edit(value => value + "!", fresh);
            Equal("B!", fresh.Read("carry", "", false)); Equal(true, before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Root, "PCL", "config.json"))));
        }
        using (fresh) fresh!.Save(); Equal("B!", fixture.Config().Value<string>("carry")); Equal(false, fixture.Config().ContainsKey("onlyA"));
        // The old writer can be the first observer of B, with the same generation and no INI patch.
        // Its JSON flush must invalidate/refresh the INI snapshot rather than leaving A visible.
        using Fixture observed = new(); observed.Install(); string observedIni = Path.Combine(observed.Root, "PCL", "Setup.ini");
        File.WriteAllText(observedIni, "CustomInfo:A\r\n"); using var oldObserver = new InstanceConfigProvider(observed.Root);
        PreferenceHarness observedSettings = new("CustomInfo");
        Equal("A", observedSettings.Engine.Get("CustomInfo", new PreferenceOwner(observed.Root + Path.DirectorySeparatorChar, oldObserver)));
        using (var quiet = MmcConfigurationCoordinator.Quiesce(observed.Root))
        using (var gate = observed.Lease()) {
            oldObserver.Set("old-only-json", "A", false);
            Directory.Delete(observed.Root, true); observed.RecreateManaged(Guid.NewGuid()); File.WriteAllText(observedIni, "CustomInfo:B\r\n");
        }
        try { oldObserver.Save(); } catch (IOException) { }
        using var newObserver = new InstanceConfigProvider(observed.Root);
        var observedOwner = new PreferenceOwner(observed.Root + Path.DirectorySeparatorChar, newObserver);
        Equal("B", observedSettings.Engine.Get("CustomInfo", observedOwner)); Equal(true, observedSettings.Engine.HasSaved("CustomInfo", observedOwner));
        Equal(false, observed.Config().ContainsKey("old-only-json"));
    }
    private static void CacheIdentityBarriers() {
        foreach (string operation in new[] { "Set", "Get", "Reset" }) {
            using Fixture fixture = new(); fixture.Install(); string path = fixture.Root + Path.DirectorySeparatorChar, file = Path.Combine(fixture.Root, "PCL", "Setup.ini");
            File.WriteAllText(file, "VersionArgumentTitle:A\r\n"); using var old = new InstanceConfigProvider(fixture.Root); PreferenceOwner prior = new(path, old);
            PreferenceHarness settings = new("VersionArgumentTitle", "default");
            if (operation != "Get") Equal("A", settings.Engine.Get(settings.Entry.Key, prior));
            using ManualResetEventSlim reached = new(), proceed = new(); int calls = 0; int target = operation == "Get" ? 2 : 3;
            settings.Host.InstanceCacheKey = owner => {
                string key = owner.Path + owner.Config.PreferenceCacheNamespace;
                if (ReferenceEquals(owner, prior) && Interlocked.Increment(ref calls) == target) { reached.Set(); if (!proceed.Wait(10000)) throw new Exception("Cache handoff barrier timed out."); }
                return key;
            };
            Task actor = Task.Run(() => { if (operation == "Set") settings.Engine.Set(settings.Entry.Key, "late-A", prior); else if (operation == "Reset") settings.Engine.Reset(settings.Entry.Key, prior); else settings.Engine.Get(settings.Entry.Key, prior); });
            InstanceConfigProvider? currentProvider = null;
            try {
                if (!reached.Wait(5000)) throw new Exception("Old actor did not reach the pre-cache-write barrier.");
                using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.Root); using var gate = fixture.Lease();
                Directory.Delete(fixture.Root, true); fixture.RecreateManaged(Guid.NewGuid()); File.WriteAllText(file, "VersionArgumentTitle:B\r\n");
                currentProvider = new(fixture.Root); PreferenceOwner current = new(path, currentProvider);
                Equal("B", settings.Engine.Get(settings.Entry.Key, current));
                proceed.Set(); if (!actor.Wait(5000)) throw new Exception("Old cache actor failed to finish.");
                Equal("B", settings.Entry.GetCache(current)); Equal("B", LegacyBaseOperations.ParseIni(File.ReadAllText(file))[settings.Entry.Key]);
                Equal("B", settings.Engine.Get(settings.Entry.Key, current));
            } finally { proceed.Set(); actor.Wait(10000); currentProvider?.Dispose(); }
        }
    }
    private static void ImmediateLayoutFence() {
        foreach (bool managed in new[] { true, false }) {
            using Fixture fixture = new(); if (managed) fixture.Install(); string file = Path.Combine(fixture.Root, "PCL", "Setup.ini");
            File.WriteAllText(file, "VersionArgumentIndieV2:True\r\n"); using var provider = new InstanceConfigProvider(fixture.Root);
            PreferenceOwner owner = new(fixture.Root + Path.DirectorySeparatorChar, provider); PreferenceHarness settings = new("VersionArgumentIndieV2", false);
            Equal(true, settings.Engine.Get(settings.Entry.Key, owner));
            settings.Engine.Set(settings.Entry.Key, false, owner); settings.Engine.Reset(settings.Entry.Key, owner);
            if (managed) {
                bool rejected = false; try { MmcConfigurationCoordinator.StateFor(fixture.Root).EnqueueIni(settings.Entry.Key, "False", provider.ContextStamp); } catch (IOException) { rejected = true; }
                Equal(true, rejected); Equal(true, settings.Entry.GetCache(owner)); Equal(true, settings.Engine.Get(settings.Entry.Key, owner));
                Equal("True", LegacyBaseOperations.ParseIni(File.ReadAllText(file))[settings.Entry.Key]);
            } else { Equal(false, settings.Engine.Get(settings.Entry.Key, owner)); }
        }
    }
    private static void OrdinaryConversionFallback() {
        foreach (object fallback in new object[] { 123, true }) {
            using Fixture fixture = new(); string file = Path.Combine(fixture.Root, "PCL", "Setup.ini");
            const string text = "InvalidPreference:not-a-valid-number-or-bool\r\n"; File.WriteAllText(file, text);
            using var provider = new InstanceConfigProvider(fixture.Root); PreferenceOwner owner = new(fixture.Root + Path.DirectorySeparatorChar, provider);
            PreferenceHarness settings = new("InvalidPreference", fallback); int errors = 0;
            settings.Host.Log = (error, _, _, _) => { if (error is not null) errors++; };
            Equal(fallback, settings.Engine.Get("InvalidPreference", owner)); Equal(fallback, settings.Entry.GetCache(owner));
            Equal(fallback, settings.Engine.Get("InvalidPreference", owner)); Equal(1, errors); Equal(text, File.ReadAllText(file));
        }
        using Fixture failed = new(); using var failedProvider = new InstanceConfigProvider(failed.Root);
        PreferenceOwner failedOwner = new(failed.Root + Path.DirectorySeparatorChar, failedProvider); PreferenceHarness blocked = new("BlockedRead", 7);
        int blockedLogs = 0; blocked.Host.Log = (_, _, _, _) => blockedLogs++;
        blocked.Host.ReadInstanceIni = (_, _, _) => throw new FormatException("controlled read/gate failure, not a value conversion");
        bool rejected = false; try { blocked.Engine.Get("BlockedRead", failedOwner); } catch (FormatException) { rejected = true; }
        Equal(true, rejected); Equal<object?>(null, blocked.Entry.GetCache(failedOwner)); Equal(0, blockedLogs);
    }
    private static void RunActualJavaReset(InstanceConfigProvider provider, string root) {
        // Each invocation is a fresh controlled process. Bind global Java entries to a private profile
        // before their type initializer runs, and verify the binding before touching any value.
        string globalPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root))!, "isolated-java-config.json");
        var isolated = new JsonConfigProvider(globalPath); ConfigProviders.Default = isolated;
        Type configuration = typeof(InstanceConfigProvider).Assembly.GetType("PCLCS.Configs") ?? throw new Exception("Actual Java config class is required.");
        object javaList = configuration.GetField("JavaList")!.GetValue(null)!;
        Equal(true, ReferenceEquals(isolated, javaList.GetType().GetProperty("Provider")!.GetValue(javaList)));
        object empty = Activator.CreateInstance(javaList.GetType().GenericTypeArguments[0])!;
        javaList.GetType().GetMethod("Set")!.Invoke(javaList, [empty]);
        Type java = typeof(InstanceConfigProvider).Assembly.GetType("PCLCS.Java") ?? throw new Exception("Actual Java model is required.");
        object invalid = Activator.CreateInstance(java, [Path.Combine(root, "missing-java"), new Version(17, 0, 0, 0)])!;
        object forced = configuration.GetField("JavaForced")!.GetValue(null)!;
        forced.GetType().GetMethod("Set")!.Invoke(forced, [invalid, provider]); provider.Save(); provider.DiscardCache();
        Equal<object?>(null, forced.GetType().GetMethod("Get")!.Invoke(forced, [provider])); provider.Save(); isolated.Save();
        JObject disk = JObject.Parse(File.ReadAllText(Path.Combine(root, "PCL", "config.json")));
        Equal(false, disk.ContainsKey("InstanceForcedJava")); Equal("kept", disk.Value<string>("unknown"));
    }
    private static void CreateJunction(string path, string target) {
        string command = "New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(value);
        using Process process = Process.Start(start) ?? throw new Exception("Could not create controlled junction.");
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Controlled junction command timed out."); } Equal(0, process.ExitCode);
    }
    private static Process StartChild(string mode, Fixture fixture) {
        string executable = Environment.ProcessPath!; ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string value in new[] { "--mmc-configuration-child", mode, fixture.Root, fixture.Ready, fixture.Go }) start.ArgumentList.Add(value);
        return Process.Start(start) ?? throw new Exception("Could not start controlled config writer.");
    }
    private static void WaitFile(string path, int budget) { var timer = Stopwatch.StartNew(); while (!File.Exists(path) && timer.ElapsedMilliseconds < budget) Thread.Sleep(10); if (!File.Exists(path)) throw new Exception("Controlled config writer timed out."); }
    private static void WaitChild(Process child) { if (!child.WaitForExit(12000)) { child.Kill(true); throw new Exception("Controlled config writer timed out."); } Equal(0, child.ExitCode); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private sealed class Fixture : IDisposable {
        public string Scope { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-config-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Scope, "versions", "sample");
        public string DefinitionFile => Path.Combine(Root, "PCL", "InstanceDefinition.json");
        public string Ready => Path.Combine(Scope, "ready"); public string Go => Path.Combine(Scope, "go");
        private readonly Guid id = Guid.NewGuid();
        public Guid Id => id;
        public Fixture() { Directory.CreateDirectory(Path.Combine(Root, "PCL")); File.WriteAllText(Path.Combine(Root, "PCL", "config.json"), "{\"unknown\":\"kept\"}"); }
        public JObject Config() => JObject.Parse(File.ReadAllText(Path.Combine(Root, "PCL", "config.json")));
        public JObject Hint(long generation) => new() { ["schemaVersion"] = 1, ["managementKind"] = "Mmc", ["instanceId"] = id.ToString("D"), ["generation"] = generation };
        public MmcAdmissionLease Lease() => MmcAdmissionGate.Acquire(Root, id, TimeSpan.Zero);
        public void Install() {
            using var quiet = MmcConfigurationCoordinator.Quiesce(Root); using var lease = Lease(); using var scope = quiet.OpenStructuralWrite(lease, 0);
            scope.SetConfig("MinecraftInstallManifest", new JObject { ["pcllfManagement"] = Hint(1) });
            Equal(MmcTransactionOutcome.Committed, scope.Execute(Publish(0)).Outcome);
        }
        public void RecreateManaged(Guid newId) {
            Directory.CreateDirectory(Path.Combine(Root, "PCL"));
            JObject hint = Hint(1); hint["instanceId"] = newId.ToString("D");
            JObject definition = (JObject)hint.DeepClone(); definition["formatProfile"] = "prism-v1"; definition["layout"] = new JObject { ["gameRootLeaf"] = "minecraft" };
            File.WriteAllText(DefinitionFile, definition.ToString());
            File.WriteAllText(Path.Combine(Root, "PCL", "config.json"), new JObject { ["MinecraftInstallManifest"] = new JObject { ["pcllfManagement"] = hint.DeepClone() } }.ToString());
            File.WriteAllText(Path.Combine(Root, "sample.json"), new JObject { ["pcllfManagement"] = hint.DeepClone() }.ToString());
        }
        public MmcFileTransactionRequest Publish(long previous) {
            JObject definition = Hint(previous + 1); definition["formatProfile"] = "prism-v1"; definition["layout"] = new JObject { ["gameRootLeaf"] = "minecraft" };
            byte[] bytes = Encoding.UTF8.GetBytes(definition.ToString()); byte[] version = Encoding.UTF8.GetBytes(new JObject { ["pcllfManagement"] = Hint(previous + 1) }.ToString());
            return new(id, previous, previous + 1, [new("PCL/InstanceDefinition.json", previous == 0 ? null : Hash(File.ReadAllBytes(DefinitionFile)), bytes, Hash(bytes)),
                new("sample.json", previous == 0 ? null : Hash(File.ReadAllBytes(Path.Combine(Root, "sample.json"))), version, Hash(version))]);
        }
        private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        public void Dispose() { InstanceConfigProviders.FlushAndDisposeWithin(Scope); if (Directory.Exists(Scope)) Directory.Delete(Scope, true); }
    }
}
