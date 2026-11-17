using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcImportRecoveryTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC import producer Use blocks concurrent recovery and committed data publishes offline", Committed);
        yield return ("MMC undecided initial transaction rolls back and fresh retry requires same ZIP SHA with a different name", RollbackAndRetry);
        yield return ("MMC recovery preserves changed payload and unproven legacy residuals", ForeignAndLegacy);
        yield return ("MMC moved import with an already removed ignore file finishes exact pending cleanup", MovedFinal);
        yield return ("MMC import recovery store blocks old cleanup from a selected PCL folder without versions", PrivateStoreCleanupGuard);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void PrivateStoreCleanupGuard() {
        using Fixture fixture = new(); string pcl = Path.Combine(fixture.Game, "PCL"); Directory.CreateDirectory(pcl);
        MmcImportRecovery.RequireLegacyGameFolderSafe(fixture.Game, "普通目录清理检查");
        MmcImportRecovery.RequireLegacyGameFolderSafe(pcl + Path.DirectorySeparatorChar, "普通 PCL 目录清理检查");
        string store = Path.Combine(pcl, "MmcImportOperations"), keep = Path.Combine(store, "private-owner.json");
        Directory.CreateDirectory(store); File.WriteAllText(keep, "owned recovery bytes");
        Require(!Directory.Exists(Path.Combine(fixture.Game, "versions")) && !Directory.Exists(Path.Combine(pcl, "versions")), "Cleanup guard fixture must not depend on a versions directory.");
        foreach (string target in new[] { fixture.Game, pcl + Path.DirectorySeparatorChar }) {
            bool refused = false;
            try { MmcImportRecovery.RequireLegacyGameFolderSafe(target, "旧目录清理检查"); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "Old cleanup accepted a folder containing the private import recovery store: " + target);
        }
        Require(File.ReadAllText(keep) == "owned recovery bytes", "Cleanup capability check changed private recovery data."); fixture.Complete();
    }
    private static void Committed() {
        using Fixture fixture = new(); bool liveBlocked = false;
        var failed = fixture.Backend().ImportAsync(fixture.Request(), progress: new Progress(value => {
            if (value.Phase != "publishing-instance") return;
            var live = fixture.Catalog().Entries.Single(); liveBlocked = live.State == MmcImportRecoveryState.ProducerActive && !live.CanRecover;
        }), cancellationToken: default).GetAwaiter().GetResult();
        // First complete import proves the normal producer path and configuration migration.
        Require(failed.Status == MmcInstanceBackendStatus.Succeeded && liveBlocked, "A live producer was not blocked or ordinary import regressed: " + failed.Failure);
        var repaired = fixture.Backend().RepairAsync(new(fixture.Final, fixture.Environment, failed.Definition!.InstanceId)).GetAwaiter().GetResult();
        Require(repaired.Status == MmcInstanceBackendStatus.Succeeded && repaired.Definition!.Generation == 2, "Normal post-import repair failed.");
        var audit = fixture.Catalog().Entries.Single();
        Require(audit.State == MmcImportRecoveryState.Completed && !audit.CanRecover && !audit.CanRetry, "Historical import was misclassified as a live producer after normal repair.");
        fixture.Complete();
        using Fixture interrupted = new(); var result = interrupted.Interrupt(MmcTransactionCheckpoint.CommitDecided);
        Require(result.Status == MmcInstanceBackendStatus.NeedsRecovery, "Committed interruption was not preserved.");
        var entry = interrupted.Catalog().Entries.Single(); Require(entry.CanRecover, entry.Description);
        File.Delete(interrupted.Archive); var recovered = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        Require(recovered.Status == MmcInstanceBackendStatus.Succeeded, recovered.Diagnostic ?? "Committed recovery failed.");
        Require(MmcInstanceInspector.Read(interrupted.Final).IsConsistent && File.ReadAllText(Path.Combine(interrupted.Final, "minecraft", "config", "player.cfg")) == "author player bytes", "Published recovery lost payload or projections.");
        Require(interrupted.Catalog().Entries.Single().State == MmcImportRecoveryState.Completed, "Completed import is still actionable.");
        interrupted.Complete();
    }
    private static void RollbackAndRetry() {
        using Fixture fixture = new(); var result = fixture.Interrupt(MmcTransactionCheckpoint.Prepared); string temporary = result.WorkingRoot!;
        var entry = fixture.Catalog().Entries.Single(); Require(entry.CanRecover, entry.Description);
        var rolled = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        Require(rolled.Status == MmcInstanceBackendStatus.NeedsRecovery && rolled.FreshImportRequired && Directory.Exists(temporary) && !Directory.Exists(fixture.Final) &&
            !File.Exists(Path.Combine(temporary, "PCL", "InstanceDefinition.json")), "Undecided transaction was published or its parked payload was deleted.");
        entry = fixture.Catalog().Entries.Single(); Require(entry.CanRetry, "Rollback lost the original-package retry path.");
        string different = Path.Combine(fixture.Root, "different.zip"); fixture.WriteZip(different, "different author bytes"); string retryRoot = Path.Combine(fixture.Game, "versions", "retry");
        var wrong = fixture.Backend().RetryInitialImportAsync(entry, new(different, retryRoot, "retry", fixture.Environment)).GetAwaiter().GetResult();
        Require(wrong.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(retryRoot), "Fresh retry accepted a different original ZIP.");
        string parked = Tree(temporary);
        var changedSha = fixture.Backend().RetryInitialImportAsync(entry with { PackageSha256 = Hash(File.ReadAllBytes(different)) }, new(different, retryRoot, "retry", fixture.Environment)).GetAwaiter().GetResult();
        Require(changedSha.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(retryRoot) && !Directory.Exists(fixture.Final) && Tree(temporary) == parked,
            "A with-edited preview SHA overrode the durable package owner.");
        var changedFinal = fixture.Backend().RetryInitialImportAsync(entry with { FinalRoot = Path.Combine(fixture.Game, "versions", "pretend-final") }, fixture.Request()).GetAwaiter().GetResult();
        Require(changedFinal.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(fixture.Final) && !Directory.Exists(retryRoot) && Tree(temporary) == parked,
            "A with-edited preview FinalRoot authorized retry into the original reserved final name.");
        var retried = fixture.Backend().RetryInitialImportAsync(entry, new(fixture.Archive, retryRoot, "retry", fixture.Environment)).GetAwaiter().GetResult();
        Require(retried.Status == MmcInstanceBackendStatus.Succeeded && Tree(temporary) == parked && MmcInstanceInspector.Read(retryRoot).IsConsistent, "Same-SHA fresh retry touched old parked data or failed publication: " + retried.Failure);
        fixture.Complete();
    }
    private static void ForeignAndLegacy() {
        using Fixture fixture = new(); var failed = fixture.Interrupt(MmcTransactionCheckpoint.CommitDecided); var entry = fixture.Catalog().Entries.Single();
        string player = Path.Combine(failed.WorkingRoot!, "minecraft", "config", "player.cfg"); File.WriteAllText(player, "foreign player bytes");
        var rejected = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        Require(rejected.Status == MmcInstanceBackendStatus.NeedsRecovery && File.ReadAllText(player) == "foreign player bytes" && !Directory.Exists(fixture.Final), "Changed game bytes were re-adopted or removed.");
        string old = Path.Combine(fixture.Game, "versions", ".pcllf-mmc-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(old, "PCL"));
        string package = Hash(File.ReadAllBytes(fixture.Archive));
        File.WriteAllText(Path.Combine(old, "PCL", "MmcImportPending.json"), new JObject { ["SchemaVersion"] = 1, ["InstanceId"] = Guid.NewGuid().ToString("D"), ["PackageSha256"] = package,
            ["TemporaryRoot"] = old, ["FinalRoot"] = Path.Combine(fixture.Game, "versions", "legacy-final") }.ToString());
        var legacy = fixture.Catalog().Entries.Single(value => value.State == MmcImportRecoveryState.LegacyUnproven);
        Require(!legacy.CanRecover && legacy.CanRetry && legacy.PackageSha256 == package, "Old marker alone was treated as producer death proof.");
        var denied = MmcImportRecovery.RecoverAsync(legacy with { CanRecover = true }).GetAwaiter().GetResult();
        Require(denied.Status == MmcInstanceBackendStatus.NeedsRecovery && Directory.Exists(old), "Unproven old residual was recovered or deleted.");
        string oldBytes = Tree(old), legacyFinal = Path.Combine(fixture.Game, "versions", "legacy-final");
        var changedLegacyFinal = fixture.Backend().RetryInitialImportAsync(legacy with { FinalRoot = Path.Combine(fixture.Game, "versions", "forged-final") }, new(fixture.Archive, legacyFinal, "legacy-final", fixture.Environment)).GetAwaiter().GetResult();
        Require(changedLegacyFinal.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(legacyFinal) && !Directory.Exists(fixture.Final) && Tree(old) == oldBytes && File.ReadAllText(player) == "foreign player bytes",
            "Legacy preview FinalRoot overrode the freshly read legacy marker or changed either parked source."); fixture.Complete();
    }
    private static void MovedFinal() {
        using Fixture fixture = new(); FileStream? held = null;
        MmcInstanceBackendResult result;
        try {
            result = fixture.Backend().ImportAsync(fixture.Request(), progress: new Progress(value => {
                if (value.Phase == "private-import-marker-cleanup") held = new FileStream(Path.Combine(fixture.Final, "PCL", "MmcImportPending.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            })).GetAwaiter().GetResult();
        } finally { held?.Dispose(); }
        Require(result.Status == MmcInstanceBackendStatus.NeedsRecovery && Directory.Exists(fixture.Final) && !File.Exists(Path.Combine(fixture.Final, ".pclignore")), "Real cleanup lock did not leave the moved-final boundary.");
        var entry = fixture.Catalog().Entries.Single(); Require(entry.CanRecover, entry.Description);
        string configPath = Path.Combine(fixture.Final, "PCL", "config.json"); byte[] config = File.ReadAllBytes(configPath);
        var foreign = JObject.Parse(File.ReadAllText(configPath)); foreign["MinecraftInstallManifest"]!["pcllfManagement"]!["instanceId"] = Guid.NewGuid().ToString("D"); File.WriteAllText(configPath, foreign.ToString());
        byte[] changed = File.ReadAllBytes(configPath); var refused = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        Require(!fixture.Catalog().Entries.Single().CanRecover, "Moved recovery catalog authorized conflicting management hints.");
        Require(refused.Status == MmcInstanceBackendStatus.NeedsRecovery && File.ReadAllBytes(configPath).SequenceEqual(changed) && File.Exists(Path.Combine(fixture.Final, "PCL", "MmcImportPending.json")), "Moved recovery overwrote foreign configuration or cleared its guard.");
        File.WriteAllBytes(configPath, config); entry = fixture.Catalog().Entries.Single(); Require(entry.CanRecover, entry.Description);
        var recovered = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        Require(recovered.Status == MmcInstanceBackendStatus.Succeeded && MmcInstanceInspector.Read(fixture.Final).IsConsistent && !File.Exists(Path.Combine(fixture.Final, "PCL", "MmcImportPending.json")), recovered.Diagnostic ?? "Moved recovery did not finish.");
        fixture.Complete();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Tree(string root) => string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file) + ":" + Hash(File.ReadAllBytes(file))));
    private sealed class Progress(Action<MmcInstanceBackendProgress> report) : IProgress<MmcInstanceBackendProgress> { public void Report(MmcInstanceBackendProgress value) => report(value); }
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PCLLF-ImportRecoveryTests", Guid.NewGuid().ToString("N"));
        internal string Game => Path.Combine(Root, "minecraft"); internal string Final => Path.Combine(Game, "versions", "fixture"); internal string Archive => Path.Combine(Root, "original.zip");
        internal readonly MmcRuntimeEnvironment Environment = new("windows", "x86_64", 8);
        private readonly HttpClient client = new(new NoNetwork()); private readonly byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); private bool completed;
        internal Fixture() { Directory.CreateDirectory(Root); WriteZip(Archive, "author player bytes"); }
        internal MmcInstanceImportRequest Request() => new(Archive, Final, "fixture", Environment);
        internal void WriteZip(string file, string player) {
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            Add("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
            Add("patches/net.minecraft.json", new JObject { ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main", ["compatibleJavaMajors"] = new JArray(8),
                ["mainJar"] = new JObject { ["name"] = "fixture:main:1", ["MMC-hint"] = "local", ["MMC-filename"] = "main.jar" },
                ["assetIndex"] = new JObject { ["id"] = "fixture", ["url"] = "https://fixture.invalid/index", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)), ["size"] = index.Length } }.ToString());
            Add("libraries/main.jar", "inert original main library"); Add("minecraft/config/player.cfg", player);
            void Add(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
        }
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(new MmcMetadataStore(Path.Combine(Root, "metadata"), new Uri("https://fixture.invalid/meta/"), client),
            new(Path.Combine(Root, "cache", "libraries"), Path.Combine(Root, "cache", "assets"), Path.Combine(Root, "cache", "sources")), value => value,
            async (files, token) => { foreach (var file in files) { Require(file.Urls.SequenceEqual(new[] { "https://fixture.invalid/index" }), "Unexpected fixture download."); await File.WriteAllBytesAsync(file.DownloadPath, index, token); } });
        internal MmcInstanceBackendResult Interrupt(MmcTransactionCheckpoint phase) => Backend().ImportAsync(Request() with { TransactionCheckpoint = (current, _) => { if (current == phase) throw new MmcTransactionInterruptionException("controlled interruption"); } }).GetAwaiter().GetResult();
        internal MmcImportRecoveryCatalog Catalog() => MmcImportRecovery.Read(Game);
        internal void Complete() => completed = true;
        public void Dispose() {
            InstanceConfigProviders.FlushAndDisposeWithin(Root); client.Dispose();
            if (!completed) { Console.Error.WriteLine("Retained import-recovery fixture: " + Root); return; }
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCLLF-ImportRecoveryTests")), full = Path.GetFullPath(Root);
            Require(full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(full), "N", out _), "Unsafe fixture cleanup path."); Directory.Delete(full, true);
        }
    }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("No real network in import recovery tests."); }
}
