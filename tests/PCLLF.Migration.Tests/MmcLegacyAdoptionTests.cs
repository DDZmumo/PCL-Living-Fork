using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcLegacyAdoptionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        if (OperatingSystem.IsWindows()) {
            yield return ("MMC strict legacy copy preserves source worlds preferences deletions and actual package authority", StrictCopy);
        } else yield return ("MMC managed-copy publication refuses an unverified non-Windows volume boundary and preserves source and private evidence", UnsupportedCopyPublication);
        yield return ("MMC source rebinding requires its own preview intent and cannot silently replace strict verification", ExplicitRebinding);
        yield return ("MMC legacy copy rejects shared ambiguous active and changed sources and cancels before publication", RefusalsAndCancellation);
        if (OperatingSystem.IsWindows()) yield return ("MMC legacy copy uses a real producer file lock and recovers a decided no-overwrite publication offline", Recovery);
        yield return ("MMC legacy private backup blocks old cleanup from a selected PCL folder without versions", PrivateStoreCleanupGuard);
        yield return ("MMC automatic copy cleanup never claims a foreign root file between producer phases", () => AutomaticCleanupRefusal("foreign"));
        yield return (OperatingSystem.IsWindows() ? "MMC automatic copy cleanup preserves foreign NTFS data streams" : "MMC unsupported native copy cleanup preserves a foreign companion file without claiming NTFS stream coverage", () => AutomaticCleanupRefusal("ads"));
        yield return ("MMC automatic copy cleanup rejects replacement after its fixed inventory", () => AutomaticCleanupRefusal("replacement"));
        yield return ("MMC automatic copy cleanup interruption atomically preserves root and ownership marker", () => AutomaticCleanupRefusal("interruption"));
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Tree(string root) => string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Hash(File.ReadAllBytes(path))));
    private static void Denied(Action work) { try { work(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException) { return; } throw new Exception("Expected a clear refusal."); }
    private static void UnsupportedCopyPublication() {
        using Fixture fixture = new(retainedMetadata: true); string before = Tree(fixture.Source);
        using var preview = fixture.Preview();
        var result = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified);
        Require(result.Status == MmcInstanceBackendStatus.NeedsRecovery && result.Failure?.Contains("Windows", StringComparison.Ordinal) == true &&
            !Directory.Exists(fixture.Target) && Tree(fixture.Source) == before && File.Exists(result.BackupPath) && result.OperationDirectory is not null,
            "Unsupported copy publication changed the source, claimed the target, or lost its backup/operation evidence: " + result.Failure);
        string privateRoot = Path.Combine(result.OperationDirectory!, "private-minecraft", "versions", "managed-copy");
        Require(File.ReadAllText(Path.Combine(privateRoot, "minecraft", "saves", "world", "level.dat")) == "player world" &&
            File.Exists(Path.Combine(privateRoot, "PCL", "MmcLegacyAdoptionPending.json")), "Unsupported publication lost private player data or its ownership guard.");
        fixture.Complete();
    }
    private static void PrivateStoreCleanupGuard() {
        using Fixture fixture = new(true); string game = Path.Combine(fixture.Root, "cleanup-game"), pcl = Path.Combine(game, "PCL"); Directory.CreateDirectory(pcl);
        MmcInstanceCapabilities.RequireLegacyGameFolder(game, "普通目录清理检查");
        MmcInstanceCapabilities.RequireLegacyGameFolder(pcl + Path.DirectorySeparatorChar, "普通 PCL 目录清理检查");
        string store = Path.Combine(pcl, "MmcLegacyAdoption"), keep = Path.Combine(store, "private-backup.zip");
        Directory.CreateDirectory(store); File.WriteAllText(keep, "owned private backup bytes");
        Require(!Directory.Exists(Path.Combine(game, "versions")) && !Directory.Exists(Path.Combine(pcl, "versions")), "Cleanup guard fixture must not depend on a versions directory.");
        Denied(() => MmcInstanceCapabilities.RequireLegacyGameFolder(game, "旧目录清理检查"));
        Denied(() => MmcInstanceCapabilities.RequireLegacyGameFolder(pcl + Path.DirectorySeparatorChar, "旧目录清理检查"));
        Require(File.ReadAllText(keep) == "owned private backup bytes", "Cleanup capability check changed private backup data."); fixture.Complete();
    }
    private static void StrictCopy() {
        using Fixture fixture = new(retainedMetadata: true); string before = Tree(fixture.Source);
        using var old = new InstanceConfigProvider(fixture.Source);
        using var preview = fixture.Preview(); Require(preview.SourceCheck.HistoricalSourceVerified, "Retained declaration evidence was not verified.");
        var created = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified);
        fixture.Success(created); Require(created.Definition!.Generation == 1 && created.Definition.ParentInstanceId is null && created.Definition.Origin.PackageSha256 == preview.PackageSha256, "Fresh copy adopted a fake old identity or package origin.");
        Require(Tree(fixture.Source) == before && MmcManagementProbe.Read(fixture.Source).State == MmcManagementState.Legacy && !File.Exists(Path.Combine(fixture.Source, "PCL", "InstanceDefinition.json")), "Creating a copy managed or modified the old source.");
        string game = created.Paths!.GameRoot;
        Require(File.ReadAllText(Path.Combine(game, "saves", "world", "level.dat")) == "player world" && File.ReadAllText(Path.Combine(game, "config", "user.cfg")) == "personal config" &&
            File.Exists(Path.Combine(game, "custom", "unknown.bin")) && !File.Exists(Path.Combine(game, "mods", "deleted.jar")), "Current personal data or author-file deletions were lost.");
        using var settings = new InstanceConfigProvider(created.Paths.InstanceRoot);
        Require(settings.Read<string>("UnknownPreference", "", false) == "kept", "Unknown instance preference was lost.");
        string ini = File.ReadAllText(Path.Combine(created.Paths.InstanceRoot, "PCL", "Setup.ini"));
        Require(ini.Contains("MmcJvmArgsMode:Custom", StringComparison.Ordinal) && ini.Split('\n').Any(line => line.TrimEnd('\r') == "VersionAdvanceJvm:"), "Explicit empty JVM preference was replaced by a package/global value.");
        var saved = MmcSavedSourceStore.LoadAsync(created.Paths, created.Definition).GetAwaiter().GetResult();
        Require(saved.Baseline.Baseline.Files.Single(file => file.RelativePath == "minecraft/config/user.cfg").Sha256 == Hash(Encoding.UTF8.GetBytes("author config")), "Personal bytes were made into author baseline.");
        old.Set("LateSourceOnly", "source", false); old.Save();
        Require(!settings.HasValue("LateSourceOnly") && old.ContextStamp.Kind == MmcConfigurationContextKind.Unmanaged, "A late ordinary provider wrote into the managed copy.");
        preview.Dispose(); File.Delete(fixture.Archive); int downloads = fixture.Downloads;
        var prepared = fixture.Backend().PrepareLaunchAsync(new(created.Paths.InstanceRoot, fixture.Environment, created.Definition.InstanceId, 1)).GetAwaiter().GetResult();
        Require(prepared.Status == MmcInstanceBackendStatus.Succeeded && prepared.Snapshot is not null && !prepared.Repaired && fixture.Downloads == downloads, "Copy could not prepare offline from its own preserved originals.");
        Denied(() => MmcArchiveSourceReader.ReadAsync(created.BackupPath!).GetAwaiter().GetResult());
        fixture.Complete();
    }
    private static void ExplicitRebinding() {
        using Fixture fixture = new(retainedMetadata: false); string before = Tree(fixture.Source);
        using var preview = fixture.Preview(); Require(!preview.SourceCheck.HistoricalSourceVerified, "A plain old JSON was guessed to have historical MMC provenance.");
        var strict = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified); Require(strict.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(fixture.Target), "Strict conversion silently accepted missing provenance.");
        string strictConfirmation = preview.ConfirmationFingerprint(fixture.Target, "managed-copy", MmcLegacyAdoptionIntent.StrictVerified);
        var hiddenFallback = fixture.Service.CreateManagedCopyAsync(preview, new(fixture.Target, "managed-copy", strictConfirmation, MmcLegacyAdoptionIntent.CreateCopyWithExplicitSourceRebinding)).GetAwaiter().GetResult();
        Require(hiddenFallback.Status == MmcInstanceBackendStatus.Rejected, "A strict confirmation was silently changed to rebinding.");
        var created = fixture.Create(preview, MmcLegacyAdoptionIntent.CreateCopyWithExplicitSourceRebinding);
        if (!OperatingSystem.IsWindows()) {
            Require(created.Status == MmcInstanceBackendStatus.NeedsRecovery && created.Failure?.Contains("Windows", StringComparison.Ordinal) == true &&
                !Directory.Exists(fixture.Target) && Tree(fixture.Source) == before && File.Exists(created.BackupPath),
                "Explicit source intent bypassed the unsupported volume boundary or changed its old source: " + created.Failure);
            fixture.Complete(); return;
        }
        fixture.Success(created);
        Require(created.Definition!.Origin.PackageSha256 == preview.PackageSha256 && Tree(fixture.Source) == before, "Explicit new source did not stay separate from current personal bytes.");
        fixture.Complete();
    }
    private static void RefusalsAndCancellation() {
        using (Fixture fixture = new(true)) {
            fixture.Activity = MmcLegacyAdoptionActivity.Active; Denied(() => fixture.Preview());
            fixture.Activity = MmcLegacyAdoptionActivity.Unknown; Denied(() => fixture.Preview()); fixture.Activity = MmcLegacyAdoptionActivity.Idle;
            Denied(() => fixture.Service.PreviewAsync(new(fixture.Source, fixture.Game, fixture.Archive, fixture.Environment)).GetAwaiter().GetResult());
            string second = Path.Combine(fixture.Source, ".minecraft"); Directory.CreateDirectory(second); Denied(() => fixture.Preview()); Directory.Delete(second, false);
            using var preview = fixture.Preview(); string before = Tree(fixture.Source); using CancellationTokenSource cancel = new();
            var result = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified, cancel.Token, (phase, _) => { if (phase == MmcLegacyAdoptionCheckpoint.BeforeDecision) cancel.Cancel(); });
            Require(result.Status == MmcInstanceBackendStatus.Cancelled && !Directory.Exists(fixture.Target) && Tree(fixture.Source) == before && File.Exists(result.BackupPath), "Predecision cancellation changed or published the old instance.");
            fixture.Complete();
        }
        using (Fixture fixture = new(true)) {
            using var preview = fixture.Preview(); string world = Path.Combine(fixture.Source, "saves", "world", "level.dat");
            var changed = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified, checkpoint: (phase, _) => { if (phase == MmcLegacyAdoptionCheckpoint.PersonalSnapshotApplied) File.WriteAllText(world, "new player data"); });
            Require(changed.Status == MmcInstanceBackendStatus.NeedsRecovery && !Directory.Exists(fixture.Target) && File.ReadAllText(world) == "new player data", "A changed source was published or rolled back over newer player data.");
            fixture.Complete();
        }
    }
    private static void Recovery() {
        using Fixture fixture = new(true); string before = Tree(fixture.Source); using var preview = fixture.Preview(); MmcLegacyAdoptionResult? concurrent = null;
        var failed = fixture.Create(preview, MmcLegacyAdoptionIntent.StrictVerified, checkpoint: (phase, operation) => {
            if (phase != MmcLegacyAdoptionCheckpoint.CommitDecided) return;
            concurrent = fixture.Service.RecoverAsync(operation).GetAwaiter().GetResult();
            throw new IOException("controlled interruption after decision");
        });
        Require(failed.Status == MmcInstanceBackendStatus.NeedsRecovery && concurrent?.Status == MmcInstanceBackendStatus.NeedsRecovery && !Directory.Exists(fixture.Target), "A live producer was mistaken for an abandoned operation.");
        preview.Dispose(); File.Delete(fixture.Archive);
        Directory.CreateDirectory(fixture.Target); string foreign = Path.Combine(fixture.Target, "keep.txt"); File.WriteAllText(foreign, "foreign bytes");
        var conflict = fixture.Service.RecoverAsync(failed.OperationDirectory!).GetAwaiter().GetResult();
        Require(conflict.Status == MmcInstanceBackendStatus.NeedsRecovery && File.ReadAllText(foreign) == "foreign bytes", "Recovery overwrote a claimed destination.");
        File.Delete(foreign); Directory.Delete(fixture.Target, false);
        var recovered = fixture.Service.RecoverAsync(failed.OperationDirectory!).GetAwaiter().GetResult(); fixture.Success(recovered);
        Require(Tree(fixture.Source) == before && File.ReadAllText(Path.Combine(recovered.Paths!.GameRoot, "saves", "world", "level.dat")) == "player world", "Forward recovery changed old source or copied world data.");
        Require(fixture.Service.RecoverAsync(failed.OperationDirectory!).GetAwaiter().GetResult().Status == MmcInstanceBackendStatus.Succeeded, "Completed recovery was not idempotent.");
        fixture.Complete();
    }
    private static void AutomaticCleanupRefusal(string mode) {
        using Fixture fixture = new(false); using var preview = fixture.Preview(); using var cancellation = new CancellationTokenSource();
        string before = Tree(fixture.Source); string? privateRoot = null, operationDirectory = null, foreign = null; bool boundary = false;
        var intent = MmcLegacyAdoptionIntent.CreateCopyWithExplicitSourceRebinding;
        var result = fixture.Service.CreateManagedCopyAutomaticallyAsync(preview, new(fixture.Target, "managed-copy", preview.ConfirmationFingerprint(fixture.Target, "managed-copy", intent), intent) {
            Checkpoint = (phase, operation) => {
                operationDirectory = operation; privateRoot = Path.Combine(operation, "private-minecraft", "versions", "managed-copy");
                if (phase == MmcLegacyAdoptionCheckpoint.PrivateImportComplete) {
                    if (mode == "foreign") { foreign = Path.Combine(privateRoot, "foreign-note.txt"); File.WriteAllText(foreign, "foreign root note must remain"); }
                    if (mode == "ads") {
                        foreign = Path.Combine(privateRoot, "PCL", "InstanceDefinition.json") + (OperatingSystem.IsWindows() ? ":foreign-note" : ".foreign-note");
                        File.WriteAllText(foreign, "foreign stream must remain");
                    }
                }
                if (phase == MmcLegacyAdoptionCheckpoint.PersonalSnapshotApplied) cancellation.Cancel();
            },
            AutomaticCleanupCheckpoint = (phase, _) => {
                if (mode == "replacement" && phase == MmcInstancePurgeCheckpoint.PlanPrepared) {
                    boundary = true; foreign = Path.Combine(privateRoot!, "minecraft", "config", "user.cfg");
                    File.Move(foreign, foreign + ".original"); File.WriteAllText(foreign, "replacement after fixed inventory must remain");
                }
                if (mode == "interruption" && phase == MmcInstancePurgeCheckpoint.BeforeDecision) { boundary = true; throw new IOException("owned interruption before atomic cleanup commit"); }
            }
        }, cancellation.Token).GetAwaiter().GetResult();
        Require(result.Status == MmcInstanceBackendStatus.NeedsRecovery && !Directory.Exists(fixture.Target) && privateRoot is not null &&
            Directory.Exists(privateRoot) && File.Exists(Path.Combine(privateRoot, "PCL", "MmcLegacyAdoptionPending.json")) && Tree(fixture.Source) == before && File.Exists(result.BackupPath),
            "Automatic cleanup deleted foreign bytes/ownership, committed an interrupted tree, or changed the old source: " + mode + " / " + result.Failure);
        if (mode is "foreign" or "ads") Require(foreign is not null && File.ReadAllText(foreign).Contains("must remain", StringComparison.Ordinal), "A producer rescan claimed and deleted the foreign file/stream.");
        if (!OperatingSystem.IsWindows()) {
            Require(result.Failure?.Contains("Windows", StringComparison.Ordinal) == true && !boundary &&
                File.ReadAllText(Path.Combine(privateRoot!, "minecraft", "saves", "world", "level.dat")) == "player world",
                "Unsupported automatic cleanup did not explicitly refuse while preserving the complete private copy.");
            fixture.Complete(); return;
        }
        if (mode is "replacement" or "interruption") Require(boundary, "Automatic cleanup did not reach its injection checkpoint; no replacement/interruption was attempted: " + mode + " / " + result.Failure + " / PrivateRoot=" + privateRoot);
        if (mode == "replacement") Require(File.ReadAllText(foreign!).Contains("must remain", StringComparison.Ordinal) && File.Exists(foreign + ".original"), "A same-name replacement was deleted after observation.");
        if (mode == "interruption") Require(File.ReadAllText(Path.Combine(privateRoot!, "minecraft", "saves", "world", "level.dat")) == "player world" &&
            File.Exists(Path.Combine(operationDirectory!, "automatic-cleanup.intent.record")) && !File.Exists(Path.Combine(operationDirectory!, "automatic-cleanup.completed.record")),
            "Interrupted cleanup lost bytes or the external durable intent.");
        fixture.Complete();
    }
    private sealed class Fixture : IDisposable {
        private readonly HttpClient client = new(new NoNetwork());
        private readonly MmcMetadataStore metadata;
        private readonly MmcArtifactCacheRoots cache;
        private readonly Dictionary<string, byte[]> downloads;
        private readonly string lockName = "PCLLF.LegacyAdoptionFixture." + Guid.NewGuid().ToString("N");
        internal readonly string Root, Source, Target, Game, Archive;
        internal readonly MmcRuntimeEnvironment Environment = new("windows", "x86_64", 8);
        internal readonly MmcLegacyAdoptionService<MmcArtifactDownloadSpec> Service;
        internal MmcLegacyAdoptionActivity Activity = MmcLegacyAdoptionActivity.Idle;
        internal int Downloads;
        private bool completed;
        internal void Complete() => completed = true;
        internal Fixture(bool retainedMetadata) {
            Root = Path.Combine(Path.GetTempPath(), "PCLLF-LegacyAdoptionTests", Guid.NewGuid().ToString("N")); Game = Path.Combine(Root, "minecraft");
            Source = Path.Combine(Game, "versions", "old"); Target = Path.Combine(Game, "versions", "managed-copy"); Archive = Path.Combine(Root, "original.zip"); Directory.CreateDirectory(Source);
            byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"), library = Encoding.UTF8.GetBytes("owned remote library");
            downloads = new() { ["https://fixture.invalid/index.json"] = index, ["https://fixture.invalid/library.jar"] = library };
            string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
            string cfg = "name=Author\nOverrideJavaArgs=true\nJvmArgs=-Dauthor=true\n";
            var patch = new JObject { ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main", ["compatibleJavaMajors"] = new JArray(8),
                ["mainJar"] = new JObject { ["name"] = "fixture:game:1", ["MMC-hint"] = "local", ["MMC-filename"] = "fixture.jar" },
                ["libraries"] = new JArray(new JObject { ["name"] = "fixture:remote:1", ["downloads"] = new JObject { ["artifact"] = new JObject { ["url"] = "https://fixture.invalid/library.jar", ["sha1"] = Sha1(library), ["size"] = library.Length } } }),
                ["assetIndex"] = new JObject { ["id"] = "fixture", ["url"] = "https://fixture.invalid/index.json", ["sha1"] = Sha1(index), ["size"] = index.Length } };
            using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create)) {
                foreach (var pair in new Dictionary<string, string> { ["mmc-pack.json"] = manifest, ["instance.cfg"] = cfg, ["patches/net.minecraft.json"] = patch.ToString(),
                    ["libraries/fixture.jar"] = "owned local library", ["minecraft/mods/author.jar"] = "author mod", ["minecraft/mods/deleted.jar"] = "deleted author mod", ["minecraft/config/user.cfg"] = "author config" }) {
                    using var writer = new StreamWriter(zip.CreateEntry(pair.Key, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)); writer.Write(pair.Value);
                }
            }
            Put("old.json", "{\"id\":\"old\",\"mainClass\":\"legacy.Main\",\"libraries\":[]}");
            Put("PCL/config.json", new JObject { ["UnknownPreference"] = "kept", ["MinecraftInstallManifest"] = new JObject { ["SchemaVersion"] = 1,
                ["Components"] = new JObject { ["MinecraftVersion"] = "1.7.10", ["ModLoaderVersions"] = new JObject() }, ["ManagedFiles"] = new JArray() } }.ToString());
            Put("PCL/Setup.ini", "VersionArgumentIndie:1\nVersionArgumentIndieV2:True\nVersionAdvanceJvm:\nVersionRamType:2\nUnknownIni:kept\n");
            Put("mods/author.jar", "author mod"); Put("config/user.cfg", "personal config"); Put("saves/world/level.dat", "player world");
            Put("screenshots/one.png", "owned screenshot bytes"); Put("servers.dat", "owned server bytes"); Put("custom/unknown.bin", "unknown personal bytes");
            if (retainedMetadata) { Put("mmc-pack.json", manifest); Put("instance.cfg", cfg); Put("patches/net.minecraft.json", patch.ToString()); }
            metadata = new(Path.Combine(Root, "metadata"), new Uri("https://fixture.invalid/meta/"), client);
            cache = new(Path.Combine(Root, "cache", "libraries"), Path.Combine(Root, "cache", "assets"), Path.Combine(Root, "cache", "sources"));
            Service = new(metadata, cache, file => file, Download, new() { ObserveActivity = (root, game) => root == Source && game == Source ? Activity : MmcLegacyAdoptionActivity.Unknown,
                AcquireLegacyPathLock = root => root == Source ? new SourceLock(lockName) : throw new IOException("Unknown fixture source.") });
        }
        private static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(metadata, cache, file => file, Download);
        private async Task Download(ImmutableArray<MmcArtifactDownloadSpec> files, CancellationToken token) {
            foreach (var file in files) {
                token.ThrowIfCancellationRequested(); Require(file.Urls.Length != 0 && downloads.TryGetValue(file.Urls[0], out _), "Unexpected network request.");
                Downloads++; Directory.CreateDirectory(Path.GetDirectoryName(file.DownloadPath)!); await File.WriteAllBytesAsync(file.DownloadPath, downloads[file.Urls[0]], token);
            }
        }
        internal MmcLegacyAdoptionPreview Preview() => Service.PreviewAsync(new(Source, Source, Archive, Environment)).GetAwaiter().GetResult();
        internal MmcLegacyAdoptionResult Create(MmcLegacyAdoptionPreview preview, MmcLegacyAdoptionIntent intent, CancellationToken token = default,
            Action<MmcLegacyAdoptionCheckpoint, string>? checkpoint = null) => Service.CreateManagedCopyAsync(preview,
                new(Target, "managed-copy", preview.ConfirmationFingerprint(Target, "managed-copy", intent), intent) { Checkpoint = checkpoint }, token).GetAwaiter().GetResult();
        internal void Success(MmcLegacyAdoptionResult result) => Require(result.Status == MmcInstanceBackendStatus.Succeeded && result.Definition is not null && result.Paths is not null &&
            MmcManagementProbe.Read(result.Paths.InstanceRoot).State == MmcManagementState.ManagedMmc && File.Exists(result.BackupPath), result.Failure ?? "Managed copy did not publish.");
        private void Put(string relative, string value) { string path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false)); }
        public void Dispose() {
            InstanceConfigProviders.FlushAndDisposeWithin(Root); client.Dispose();
            if (!completed) { Console.Error.WriteLine("Retained failed owned adoption fixture: " + Root); return; }
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCLLF-LegacyAdoptionTests")), root = Path.GetFullPath(Root);
            Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(root), "N", out _), "Fixture cleanup escaped its owned GUID root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class SourceLock : IDisposable {
        private readonly Mutex mutex;
        internal SourceLock(string name) { mutex = new(false, name); if (!mutex.WaitOne(0)) { mutex.Dispose(); throw new IOException("Source is busy."); } }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new IOException("Fixture metadata cannot access network.");
    }
}
