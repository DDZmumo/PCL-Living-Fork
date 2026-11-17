using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

if (args is not ["--group", var group] || group is not ("world-success" or "world-refusals" or "ordinary")) {
    Console.Error.WriteLine("Use --group world-success|world-refusals|ordinary. No JVM or game is launched."); return 2;
}
try {
    if (group == "world-success") Success();
    else if (group == "world-refusals") Refusals();
    else Ordinary();
    Console.WriteLine("PASS " + group + ": actual production PCLCS backend/transaction and WorldBackupEngine; no JVM/game."); return 0;
} catch (Exception error) { Console.Error.WriteLine("FAIL " + group + ": " + error); return 1; }

static void Success() {
    using (Fixture data = new("backup")) {
        data.World(); bool admission = false;
        var result = data.Changes((root, token) => {
            Guid id = data.Primary.Definition!.InstanceId;
            var probe = new Thread(() => {
                try { using var forbidden = MmcAdmissionGate.Acquire(data.Version, id, TimeSpan.Zero); }
                catch (IOException) { admission = true; }
            });
            probe.Start(); if (!probe.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Admission probe did not return.");
            return data.Worlds.CaptureFolder(root, "修改 Minecraft 组件前", 5, token);
        }).CommitAsync(data.MinecraftChange()).GetAwaiter().GetResult();
        Check(admission && result.Status == MmcInstanceBackendStatus.Succeeded && result.WorldBackupIds.Length == 1 && result.UndoPoint is not null, "World backup did not run inside final admission: " + result.Status + "; " + result.Failure);
        string point = Path.Combine(data.Worlds.Store(data.GameRoot), "points", result.WorldBackupIds.Single(), "world.zip");
        using var zip = ZipFile.OpenRead(point); using var reader = new StreamReader(zip.GetEntry("level.dat")!.Open());
        Check(reader.ReadToEnd() == "current world" && data.Read("minecraft/saves/world/level.dat") == "current world" && data.Read(".minecraft/saves/world/level.dat") == "alternate world", "Actual backup or either current world differs.");
        var receipt = JObject.Parse(File.ReadAllText(Directory.GetFiles(data.PathOf("PCL/MmcDefinitionChanges"), "*.json").Single()));
        Check(receipt["WorldBackupIds"]!.Values<string>().SequenceEqual(result.WorldBackupIds), "Durable receipt lost world backup references.");
    }
    using (Fixture data = new("no-world")) {
        var result = data.Changes().CommitAsync(data.MinecraftChange()).GetAwaiter().GetResult();
        Check(result.Status == MmcInstanceBackendStatus.Succeeded && result.WorldBackupIds.IsEmpty, "No-world Minecraft change incorrectly required a backup adapter: " + result.Failure);
    }
}
static void Refusals() {
    foreach (string mode in new[] { "no-adapter", "callback-fails", "locked", "changed" }) {
        using Fixture data = new(mode); data.World(); string before = data.Primary.PrimarySha256!;
        using var held = mode == "locked" ? new FileStream(data.PathOf("minecraft/saves/world/session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var backend = mode == "no-adapter" ? data.Changes() : data.Changes((root, token) => mode == "callback-fails"
            ? throw new IOException("controlled backup failure") : data.Worlds.CaptureFolder(root, "修改 Minecraft 组件前", 5, token));
        var request = data.MinecraftChange();
        if (mode == "changed") request = request with { TransactionCheckpoint = (phase, _) => { if (phase == MmcTransactionCheckpoint.BeforeDecision) data.Put("minecraft/saves/world/late.dat", "late world data"); } };
        var result = backend.CommitAsync(request).GetAwaiter().GetResult();
        Check(result.Status == MmcInstanceBackendStatus.NeedsRecovery && data.Primary.PrimarySha256 == before && data.Read("minecraft/saves/world/level.dat") == "current world", "World refusal changed current publication or data: " + mode + "; " + result.Failure);
        if (mode == "changed") Check(result.UndoPoint is not null && result.WorldBackupIds.Length == 1 && data.Read("minecraft/saves/world/late.dat") == "late world data", "Late-world refusal erased backup/undo/live scene.");
    }
}
static void Ordinary() {
    using Fixture data = new("ordinary"); data.World();
    using var held = new FileStream(data.PathOf("minecraft/saves/world/session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var current = data.Primary;
    var backend = data.Changes((_, _) => throw new IOException("Ordinary component edit called world backup."));
    var preview = backend.PreviewAsync(new(data.Version, current.Definition!.InstanceId, current.Definition.Generation, current.PrimarySha256!, current.Definition.InstalledProjection!.RuntimeEnvironment!,
        [new("fixture.extra", Enabled: false)])).GetAwaiter().GetResult();
    Check(preview.Preview is not null, "Ordinary preview failed: " + preview.Failure);
    var result = backend.CommitAsync(new(preview.Preview!, preview.Preview!.CandidateSha256)).GetAwaiter().GetResult();
    Check(result.Status == MmcInstanceBackendStatus.Succeeded && result.WorldBackupIds.IsEmpty && data.Read("minecraft/saves/world/level.dat") == "current world", "Unchanged Minecraft version was affected by world locks/adapter: " + result.Failure);
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

internal sealed class Fixture : IDisposable {
    internal readonly string Root, Version, GameRoot;
    private readonly MmcMetadataStore metadata;
    private readonly MmcArtifactCacheRoots caches;
    private readonly HttpClient client = new(new NoNetwork());
    internal readonly WorldBackupEngine<object> Worlds;
    internal MmcInstanceInspection Primary => MmcInstanceInspector.Read(Version);
    internal Fixture(string name) {
        Root = Path.Combine(Path.GetTempPath(), "PclMmcComponentWorlds-" + name + "-" + Guid.NewGuid().ToString("N"));
        Version = Path.Combine(Root, "game", "versions", "owned"); GameRoot = Path.Combine(Version, "minecraft"); Directory.CreateDirectory(Root);
        metadata = new(Path.Combine(Root, "metadata"), new Uri("https://fixture.invalid/meta/"), client);
        caches = new(Path.Combine(Root, "cache", "libraries"), Path.Combine(Root, "cache", "assets"), Path.Combine(Root, "cache", "sources"));
        byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); string asset = Path.Combine(caches.AssetsRoot, "indexes", "fixture.json"); Directory.CreateDirectory(Path.GetDirectoryName(asset)!); File.WriteAllBytes(asset, index);
        string pack = Path.Combine(Root, "owned.zip");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create)) {
            Add("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},{"uid":"fixture.extra","version":"1"}]}"""); Add("instance.cfg", "name=Owned\n");
            Add("patches/net.minecraft.json", new JObject { ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main", ["compatibleJavaMajors"] = new JArray(8),
                ["mainJar"] = new JObject { ["name"] = "fixture:game:1", ["MMC-hint"] = "local", ["MMC-filename"] = "game.jar" },
                ["assetIndex"] = new JObject { ["id"] = "fixture", ["url"] = "https://fixture.invalid/index", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)), ["size"] = index.Length } }.ToString());
            Add("patches/fixture.extra.json", """{"formatVersion":1,"uid":"fixture.extra","version":"1","+jvmArgs":["-Downed=true"]}"""); Add("libraries/game.jar", "owned game bytes");
            void Add(string relative, string text) { using var writer = new StreamWriter(zip.CreateEntry(relative).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        var imported = new MmcInstanceBackend<MmcArtifactDownloadSpec>(metadata, caches, value => value, NoDownloads).ImportAsync(new(pack, Version, "owned", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new IOException(imported.Failure);
        Worlds = new(new WorldBackupHost<object> { RemoveExtendedPrefix = value => value, ProfileAddress = name => Path.Combine(Root, name),
            Watchers = () => [], UiBusy = () => false, WorldLabel = _ => null, Warn = (_, _) => { }, NotifyChanged = () => { }, Dispatch = action => action() });
    }
    internal void World() { Put("minecraft/saves/world/level.dat", "current world"); Put("minecraft/saves/world/session.lock", "stopped"); Put(".minecraft/saves/world/level.dat", "alternate world"); }
    internal MmcInstanceDefinitionChangeBackend<MmcArtifactDownloadSpec> Changes(Func<string, CancellationToken, IReadOnlyList<string>>? backup = null) => new(metadata, caches, value => value, NoDownloads, backupWorlds: backup);
    internal MmcInstanceDefinitionChangeCommitRequest MinecraftChange() {
        var current = Primary; var record = current.Definition!.SourceBundle.MetadataRecords.Single(value => value.Uid == "net.minecraft" && value.Kind == MmcSourceRecordKind.Component);
        var patch = JObject.Parse(Read(record.ObjectRelativePath)); patch["version"] = "1.7.11";
        var preview = Changes().PreviewAsync(new(Version, current.Definition.InstanceId, current.Definition.Generation, current.PrimarySha256!, current.Definition.InstalledProjection!.RuntimeEnvironment!, [new("net.minecraft", PatchContentUtf8: patch.ToString())])).GetAwaiter().GetResult();
        if (preview.Preview is null) throw new IOException(preview.Failure); return new(preview.Preview, preview.Preview.CandidateSha256);
    }
    private static Task NoDownloads(ImmutableArray<MmcArtifactDownloadSpec> batch, CancellationToken token) { token.ThrowIfCancellationRequested(); return batch.IsEmpty ? Task.CompletedTask : throw new IOException("Owned component originals unexpectedly requested download."); }
    internal string PathOf(string relative) => Path.Combine(Version, relative.Replace('/', Path.DirectorySeparatorChar));
    internal string Read(string relative) => File.ReadAllText(PathOf(relative));
    internal void Put(string relative, string text) { string path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    public void Dispose() { InstanceConfigProviders.FlushAndDisposeWithin(Root); client.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Owned component fixture forbids external metadata."); }
}
