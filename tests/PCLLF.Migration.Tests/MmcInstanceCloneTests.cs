using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcInstanceCloneTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC clone snapshot copies current personal bytes and preserves both game roles with source-specific options", Snapshot);
        yield return ("MMC clone publishes a fresh child identity with current preferences and offline rebuilt runtime", RoundTrip);
        yield return ("MMC clone cancellation, interrupted journals, changed sources and uncertain sessions preserve both roots", Failures);
        yield return ("MMC clone uniquely recompiles an old source environment without migrating its original definition", LegacyEnvironment);
        yield return ("MMC clone preserves one indexed asset original and rebuilds its view with global caches removed", IndexedAsset);
    }
    private static void IndexedAsset() {
        using Fixture fixture = new(withAsset: true); var source = fixture.Import();
        var original = source.Definition!.DerivedOutputs.Single(value => value.Kind == MmcDerivedOutputKind.AssetObject);
        Directory.Delete(Path.Combine(fixture.Root, "global-assets"), true);
        var result = new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source)).GetAwaiter().GetResult();
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure);
        var asset = result.Definition!.DerivedOutputs.Single(value => value.Kind == MmcDerivedOutputKind.AssetObject);
        Equal(original.Sha256, asset.Sha256); Equal(original.RecoveryObjectRelativePath, asset.RecoveryObjectRelativePath);
        Equal(original.Sha256, MmcDurableFiles.Observe(result.Paths!.ResolveRelative(MmcPathRole.Instance, asset.RecoveryObjectRelativePath!)));
        Equal(original.Sha256, MmcDurableFiles.Observe(result.Paths!.ResolveRelative(MmcPathRole.Instance, asset.TargetRelativePath)));
        Equal(false, Directory.Exists(Path.Combine(fixture.Root, "global-assets")));
        var ready = fixture.Backend().PrepareLaunchAsync(new(fixture.TargetRoot, new("windows", "x86_64", 8), result.Definition.InstanceId, 1)).GetAwaiter().GetResult();
        if (ready.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(ready.Failure); Equal(false, ready.Repaired);
        string badOriginal = source.Paths!.ResolveRelative(MmcPathRole.Instance, original.RecoveryObjectRelativePath!);
        File.WriteAllText(badOriginal, "damaged owned object");
        string rejectedTarget = Path.Combine(Path.GetDirectoryName(fixture.TargetRoot)!, "bad-copy");
        var rejected = new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source) with { TargetRoot = rejectedTarget, TargetName = "bad-copy" }).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, rejected.Status); Equal(false, Directory.Exists(rejectedTarget));
        Equal("damaged owned object", File.ReadAllText(badOriginal));
    }
    private static MmcInstanceCloneRequest Request(Fixture fixture, MmcInstanceBackendResult source, MmcInstanceCloneOptions? options = null) =>
        new(fixture.SourceRoot, fixture.TargetRoot, "clone", source.Definition!.InstanceId, source.Definition.Generation, MmcSavedSourceStore.ReadPrimary(source.Paths!).Sha256, options ?? new());
    private static void RoundTrip() {
        using Fixture fixture = new(); var source = fixture.Import();
        Put(fixture.SourceRoot, "minecraft/config/personal.cfg", "my edited config"); Put(fixture.SourceRoot, "minecraft/saves/world/level.dat", "my current world");
        Put(fixture.SourceRoot, ".minecraft/config/inactive.cfg", "inactive edited config"); Put(fixture.SourceRoot, ".minecraft/screenshots/one.png", "inactive screenshot");
        Put(fixture.SourceRoot, ".minecraft/libraries/private.jar", "game-scope library"); Put(fixture.SourceRoot, "PCL/MmcRuntime/old/cache.bin", "never copy this cache");
        using (var provider = new InstanceConfigProvider(fixture.SourceRoot)) {
            MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJavaLocationMode"] = "Custom", ["VersionArgumentJavaV2"] = "3",
                ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "", ["MmcMemoryMode"] = "Custom", ["VersionRamType"] = "1", ["VersionRamCustom"] = "20",
                ["MmcWindowMode"] = "Custom", ["MmcWindowWidth"] = "1024", ["MmcWindowHeight"] = "700" },
                new JObject { ["InstanceForcedJava"] = new JObject { ["Folder"] = fixture.SourceRoot + "/minecraft/private-java/bin/" }, ["unknown-player-value"] = "keep me" });
            provider.Save();
        }
        var before = Tree(fixture.SourceRoot); var globals = Tree(Path.Combine(fixture.Root, "global-assets"));
        var result = new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source, new(true, true, true))).GetAwaiter().GetResult();
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure);
        Equal(true, result.Definition!.InstanceId != source.Definition!.InstanceId); Equal(source.Definition.InstanceId, result.Definition.ParentInstanceId); Equal(1L, result.Definition.Generation);
        Equal(source.Definition.ArtifactLocks.Length, result.Definition.ArtifactLocks.Length); Equal(source.Definition.PackageFileLocks.Length, result.Definition.PackageFileLocks.Length);
        Equal("my edited config", File.ReadAllText(Path.Combine(fixture.TargetRoot, "minecraft", "config", "personal.cfg")));
        Equal("my current world", File.ReadAllText(Path.Combine(fixture.TargetRoot, "minecraft", "saves", "world", "level.dat")));
        Equal("inactive edited config", File.ReadAllText(Path.Combine(fixture.TargetRoot, ".minecraft", "config", "inactive.cfg")));
        Equal("game-scope library", File.ReadAllText(Path.Combine(fixture.TargetRoot, ".minecraft", "libraries", "private.jar")));
        Equal(false, File.Exists(Path.Combine(fixture.TargetRoot, "PCL", "MmcRuntime", "old", "cache.bin")));
        Equal(false, File.Exists(Path.Combine(fixture.TargetRoot, "PCL", "MmcImportPayload.json"))); Equal(false, File.Exists(Path.Combine(fixture.TargetRoot, "PCL", "MmcImportPending.json")));
        Equal(true, Tree(fixture.SourceRoot).SequenceEqual(before)); Equal(true, Tree(Path.Combine(fixture.Root, "global-assets")).SequenceEqual(globals));
        using (var provider = new InstanceConfigProvider(fixture.TargetRoot)) {
            var prefs = MmcIniConfiguration.CapturePreferences(provider);
            Equal("Custom", prefs.Ini["MmcJvmArgsMode"]); Equal("", prefs.Ini["VersionAdvanceJvm"]); Equal("20", prefs.Ini["VersionRamCustom"]);
            Equal("keep me", prefs.Config.Value<string>("unknown-player-value")); Equal(fixture.TargetRoot.Replace('\\','/') + "/minecraft/private-java/bin/", prefs.Config["InstanceForcedJava"]!.Value<string>("Folder"));
        }
        var prepared = fixture.Backend().PrepareLaunchAsync(new(fixture.TargetRoot, new("windows", "x86_64", 8), result.Definition.InstanceId, 1)).GetAwaiter().GetResult();
        if (prepared.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(prepared.Failure); Equal(false, prepared.Repaired); Equal(1L, prepared.Snapshot!.Definition.Generation);
        Put(fixture.TargetRoot, "player-owned.txt", "coexisting target");
        Equal(MmcInstanceBackendStatus.Rejected, new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source)).GetAwaiter().GetResult().Status);
        Equal("coexisting target", File.ReadAllText(Path.Combine(fixture.TargetRoot, "player-owned.txt")));
    }
    private static void Failures() {
        foreach (string mode in new[] { "cancel", "journal", "source-change", "session" }) {
            using Fixture fixture = new(); var source = fixture.Import(); var before = Tree(fixture.SourceRoot); using CancellationTokenSource cancel = new();
            if (mode == "session") Put(fixture.SourceRoot, "PCL/MmcSessions/uncertain/receipt.record", "unknown session");
            var result = new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source) with {
                Checkpoint = (phase, _) => { if (mode == "cancel" && phase == MmcInstanceCloneCheckpoint.FileCopied) cancel.Cancel();
                    if (mode == "source-change" && phase == MmcInstanceCloneCheckpoint.BeforePublication) File.Delete(Path.Combine(fixture.SourceRoot, "minecraft", "config", "personal.cfg")); },
                TransactionCheckpoint = (phase, _) => { if (mode == "journal" && phase == MmcTransactionCheckpoint.Prepared) throw new MmcTransactionInterruptionException("owned clone interruption"); }
            }, cancel.Token).GetAwaiter().GetResult();
            Equal(mode == "cancel" ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, result.Status);
            Equal(false, Directory.Exists(fixture.TargetRoot)); Equal(true, File.Exists(Path.Combine(result.WorkingRoot!, "PCL", "MmcImportPending.json")));
            if (mode is "cancel" or "journal") Equal(true, Tree(fixture.SourceRoot).SequenceEqual(before));
            if (mode == "source-change") Equal(false, File.Exists(Path.Combine(fixture.SourceRoot, "minecraft", "config", "personal.cfg")));
        }
    }
    private static void LegacyEnvironment() {
        using Fixture fixture = new(); var source = fixture.Import();
        var old = source.Definition! with { InstalledProjection = source.Definition.InstalledProjection! with { RuntimeEnvironment = null } };
        old = old with { InstalledProjection = old.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(old) } };
        string primary = Path.Combine(fixture.SourceRoot, "PCL", "InstanceDefinition.json"); File.WriteAllText(primary, MmcDefinitionCodec.Serialize(old), new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(primary);
        var result = new MmcInstanceCloneBackend().CloneAsync(Request(fixture, source)).GetAwaiter().GetResult();
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure);
        Equal(true, original.SequenceEqual(File.ReadAllBytes(primary))); Equal(null, MmcSavedSourceStore.ReadPrimary(source.Paths!).Definition.InstalledProjection!.RuntimeEnvironment);
        Equal(new MmcRuntimeEnvironment("windows", "x86_64", 8), result.Definition!.InstalledProjection!.RuntimeEnvironment);
    }
    private static KeyValuePair<string,string>[] Tree(string root) => Directory.Exists(root) ? Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
        .Select(path => new KeyValuePair<string,string>(Path.GetRelativePath(root,path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(pair => pair.Key,StringComparer.Ordinal).ToArray() : [];
    private static void Snapshot() {
        using Fixture fixture = new();
        var source = fixture.Import(); var sourcePaths = source.Paths!;
        Put(sourcePaths.InstanceRoot, "minecraft/config/personal.cfg", "edited current config");
        Put(sourcePaths.InstanceRoot, "minecraft/saves/world/level.dat", "active world"); Put(sourcePaths.InstanceRoot, "minecraft/screenshots/one.png", "active screenshot");
        Put(sourcePaths.InstanceRoot, ".minecraft/saves/world/level.dat", "inactive world"); Put(sourcePaths.InstanceRoot, ".minecraft/screenshots/one.png", "inactive screenshot");
        Put(sourcePaths.InstanceRoot, ".minecraft/libraries/private.jar", "inactive game library"); Put(sourcePaths.InstanceRoot, ".minecraft/map-data/current.bin", "unknown personal neighbor");
        Put(sourcePaths.InstanceRoot, "minecraft/servers.dat", "active servers"); Put(sourcePaths.InstanceRoot, ".minecraft/servers.dat", "inactive servers");
        Put(sourcePaths.InstanceRoot, "PCL/MmcRuntime/do-not-copy/cache.bin", "derived garbage"); Put(sourcePaths.InstanceRoot, "accounts.json", "do not adopt account data");
        using var provider = new InstanceConfigProvider(sourcePaths.InstanceRoot);
        using var quiet = MmcConfigurationCoordinator.Quiesce(sourcePaths.InstanceRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "" });
        // The reservation holds a producer-use lease (FileShare.Read) on the private root; without
        // deterministic release the fixture cannot delete its temporary tree. Disposing it here is the
        // same contract the product honours in MmcInstanceCloneBackend (leases first, then the reservation).
        using var reservation = new MmcInstanceCloneReservation(fixture.SourceRoot, fixture.TargetRoot, source.Definition!.Origin.PackageSha256);
        var targetPaths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, source.Definition.Layout.GameRootLeaf);
        List<MmcAdmissionLease> leases = [];
        try {
            foreach (string root in new[] { sourcePaths.InstanceRoot, reservation.TemporaryRoot, reservation.FinalRoot }.Order(StringComparer.OrdinalIgnoreCase))
                leases.Add(MmcAdmissionGate.Acquire(root, root == sourcePaths.InstanceRoot ? source.Definition.InstanceId : reservation.InstanceId, TimeSpan.Zero));
            var inventory = MmcInstanceCloneSnapshot.Capture(sourcePaths, source.Definition, new(), CancellationToken.None);
            var prefs = MmcInstanceCloneSnapshot.Preferences(quiet, source.Definition.InstanceId);
            Equal("Custom", LegacyBaseOperations.ParseIni(prefs.Ini)["MmcJvmArgsMode"]); Equal("", LegacyBaseOperations.ParseIni(prefs.Ini)["VersionAdvanceJvm"]);
            Equal(false, inventory.Files.Any(file => file.RelativePath.Contains("/saves/", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("/screenshots/", StringComparison.OrdinalIgnoreCase) || file.RelativePath.EndsWith("/servers.dat", StringComparison.OrdinalIgnoreCase)));
            Equal(false, inventory.Files.Any(file => file.RelativePath.StartsWith("PCL/MmcRuntime/", StringComparison.OrdinalIgnoreCase) || file.RelativePath == "accounts.json" || file.RelativePath == "PCL/MmcImportPayload.json"));
            var manifest = new MmcClonePreparedManifest(1, reservation.InstanceId, source.Definition.InstanceId, source.Definition.Generation,
                MmcSavedSourceStore.ReadPrimary(sourcePaths).Sha256, prefs.Fingerprint, sourcePaths.InstanceRoot, reservation.FinalRoot, new(), inventory);
            reservation.Prepare(manifest, leases.Single(lease => lease.VersionRoot == reservation.TemporaryRoot));
            MmcInstanceCloneSnapshot.Copy(sourcePaths, targetPaths, inventory, CancellationToken.None, null, null);
            MmcInstanceCloneSnapshot.Verify(sourcePaths, inventory, source.Definition, new(), CancellationToken.None);
            MmcInstanceCloneSnapshot.VerifyCopied(targetPaths, inventory, CancellationToken.None);
            Equal("edited current config", File.ReadAllText(Path.Combine(targetPaths.GameRoot, "config", "personal.cfg")));
            Equal("inactive game library", File.ReadAllText(Path.Combine(targetPaths.InstanceRoot, ".minecraft", "libraries", "private.jar")));
            Equal("unknown personal neighbor", File.ReadAllText(Path.Combine(targetPaths.InstanceRoot, ".minecraft", "map-data", "current.bin")));
            var all = MmcInstanceCloneSnapshot.Capture(sourcePaths, source.Definition, new(true, true, true), CancellationToken.None);
            Equal(6, all.Files.Count(file => file.RelativePath.Contains("/saves/", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("/screenshots/", StringComparison.OrdinalIgnoreCase) || file.RelativePath.EndsWith("/servers.dat", StringComparison.OrdinalIgnoreCase)));
            bool denied = false; try { MmcInstanceCloneReservation.ValidateRoots(fixture.SourceRoot, Path.Combine(fixture.SourceRoot, "minecraft", "versions", "nested")); } catch (IOException) { denied = true; } Equal(true, denied);
            Equal(false, Directory.Exists(fixture.TargetRoot)); Equal(true, File.Exists(Path.Combine(reservation.TemporaryRoot, "PCL", "MmcImportPending.json")));
        } finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); }
    }
    private static void Put(string root, string relative, string value) { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pcllf-mmc-clone-" + Guid.NewGuid().ToString("N"));
        internal string SourceRoot => Path.Combine(Root, "game", "versions", "source");
        internal string TargetRoot => Path.Combine(Root, "game", "versions", "clone");
        private readonly HttpClient client = new(new NoNetwork());
        private readonly byte[] index;
        private static readonly byte[] ObjectBytes = Encoding.UTF8.GetBytes("owned indexed object");
        internal Fixture(bool withAsset = false) {
            string objectHash = Convert.ToHexStringLower(SHA1.HashData(ObjectBytes));
            index = Encoding.UTF8.GetBytes(withAsset ? new JObject { ["objects"] = new JObject { ["fixture/object.bin"] = new JObject { ["hash"] = objectHash, ["size"] = ObjectBytes.Length } } }.ToString(Newtonsoft.Json.Formatting.None) : "{\"objects\":{}}");
            Directory.CreateDirectory(Root); string sha1 = Convert.ToHexStringLower(SHA1.HashData(index));
            using var zip = ZipFile.Open(Path.Combine(Root, "fixture.zip"), ZipArchiveMode.Create);
            Add("pack/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
            Add("pack/instance.cfg", "name=Source\n");
            Add("pack/patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/index.json","sha1":"{{sha1}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
            Add("pack/libraries/fixture.jar", "active local bytes"); Add("pack/libraries/orphan.jar", "preserved local bytes"); Add("pack/minecraft/config/personal.cfg", "author config");
            Add("outer-doc.txt", "opaque original"); Add("pack/PCL/config.json", "{\"foreign\":true}");
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(new(Path.Combine(Root, "metadata"), new Uri("https://metadata.invalid/"), client),
                new(Path.Combine(Root, "global-libraries"), Path.Combine(Root, "global-assets"), Path.Combine(Root, "global-sources")), value => value,
                async (files, token) => { foreach (var file in files) await File.WriteAllBytesAsync(file.DownloadPath, file.Artifact.Role == MmcArtifactRole.AssetIndex ? index : ObjectBytes, token); });
        internal MmcInstanceBackendResult Import() {
            var backend = Backend();
            var result = backend.ImportAsync(new(Path.Combine(Root, "fixture.zip"), SourceRoot, "source", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure); return result;
        }
        public void Dispose() { client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Clone fixture is offline."); }
}
