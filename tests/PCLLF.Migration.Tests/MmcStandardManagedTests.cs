using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PCLCS;
using PCLLF.MMCC.Core;

// Original LF integration fixtures; repository LICENCE applies. Standard component
// names and entry classes are format data. All served metadata/JAR bytes are our
// own tiny inert examples; no Forge/Fabric binary, Java or Minecraft is executed.
internal static class MmcStandardManagedTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC managed standard Forge preserves LaunchWrapper/tweaker/LWJGL2 through offline repair", () => Run(false).GetAwaiter().GetResult());
        yield return ("MMC managed standard Fabric resolves intermediary and preserves Knot classpath through offline repair", () => Run(true).GetAwaiter().GetResult());
    }

    private static async Task Run(bool fabric) {
        using var fixture = new Fixture(fabric);
        var backend = fixture.Backend(offline: false);
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, fixture.Name, fixture.Environment));
        Success(imported.Status, imported.Failure);
        var definition = imported.Definition!; var paths = imported.Paths!; var compiled = imported.BoundPlan!.CompiledProfile;
        Require(definition.Generation == 1 && definition.InstanceId != Guid.Empty, "Import did not publish a new managed definition.");
        Require(MmcInstanceInspector.Read(paths.InstanceRoot).IsConsistent, "The standard managed import is not inspectable.");
        Require(compiled.MainClass == fixture.MainClass && compiled.MinecraftVersion == fixture.MinecraftVersion,
            "The loader's entry or Minecraft component was replaced by vanilla installation.");
        Require(compiled.Classpath.Select(a => a.Coordinate!.Name).SequenceEqual(fixture.OrderedCoordinates, StringComparer.Ordinal),
            "Standard loader classpath order/main-JAR position changed.");
        Require(compiled.Classpath[^1].Role == MmcArtifactRole.MainJar && compiled.JavaPolicy.AllowedMajors.SequenceEqual([fixture.JavaMajor]),
            "Main JAR or declared Java candidate changed.");
        Require(compiled.JvmArguments.Any(token => token.Value == "-Dstandard.fixture=" + fixture.Name), "The component JVM argument was dropped.");
        Require(compiled.Tweakers.SequenceEqual(fabric ? [] : new[] { "cpw.mods.fml.common.launcher.FMLTweaker" }), "The standard Forge tweaker or Fabric no-tweaker state changed.");
        string loaderUid = fabric ? "net.fabricmc.fabric-loader" : "net.minecraftforge";
        Require(definition.SourceBundle.MetadataRecords.Any(record => record.Uid == loaderUid), "Loader metadata was not retained independently of the remote cache.");
        Require(fixture.Server.WasRequested("/metadata/" + loaderUid + "/" + fixture.LoaderVersion + ".json"), "The loader was not resolved through the real metadata transport.");
        if (fabric) Require(fixture.Server.WasRequested("/metadata/net.fabricmc.intermediary/1.20.1.json") &&
            definition.SourceBundle.MetadataRecords.Any(record => record.Uid == "net.fabricmc.intermediary"), "Fabric intermediary dependency was not fetched and preserved.");
        else Require(compiled.Classpath.Any(a => a.Coordinate!.Name == "org.lwjgl.lwjgl:lwjgl:2.9.4"), "Ordinary Forge lost its declared LWJGL2 library.");
        CheckProjection(paths.InstanceRoot, fixture.Name, fixture.MainClass, definition.InstanceId, 1, compiled, definition);

        string playerConfig = Path.Combine(paths.GameRoot, "config", "author.cfg");
        File.WriteAllText(playerConfig, "personal settings", new UTF8Encoding(false));
        string world = Path.Combine(paths.GameRoot, "saves", "player-world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "current player world", new UTF8Encoding(false));
        using (var preferences = new InstanceConfigProvider(paths.InstanceRoot)) {
            MmcIniConfiguration.QueuePreferenceBatch(preferences,
                new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "" },
                new JObject { ["personal-standard-fixture"] = "keep" }); preferences.Save();
        }

        // A mutable public Maven cache is never an execution authority after import.
        var loader = compiled.Classpath.Single(a => a.Coordinate!.Name == fixture.LoaderCoordinate);
        string mutable = Path.Combine(fixture.CacheRoot, "libraries", loader.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        fixture.Owned(mutable); Directory.CreateDirectory(Path.GetDirectoryName(mutable)!); File.WriteAllText(mutable, "another instance's cache bytes");
        int requests = fixture.Server.RequestCount;
        var prepared = await backend.PrepareLaunchAsync(new(paths.InstanceRoot, fixture.Environment, definition.InstanceId, 1, RepairIncomplete: false));
        Success(prepared.Status, prepared.Failure);
        Require(!prepared.Repaired && prepared.Snapshot is not null && prepared.Snapshot.Definition.Generation == 1 && fixture.Server.RequestCount == requests,
            "Healthy prepare changed generation or fetched a changed global cache.");
        var target = prepared.Snapshot!.Targets.Single(item => item.ArtifactKey == loader.Key);
        var adopted = definition.ArtifactLocks.Single(item => item.ArtifactKey == loader.Key);
        Require(Hash(File.ReadAllBytes(target.ActualPath)) == adopted.AdoptedSha256 && !target.ActualPath.StartsWith(fixture.CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Prepared loader input escaped the instance-owned adopted bytes.");

        // Only our deliberately damaged owned file/projection are repair targets.
        File.WriteAllText(target.ActualPath, "damaged instance input");
        File.Delete(Path.Combine(paths.InstanceRoot, fixture.Name + ".json"));
        File.Delete(fixture.Archive); Directory.Delete(fixture.CacheRoot, true);
        fixture.Server.Dispose();
        var offline = fixture.Backend(offline: true);
        var incomplete = await offline.PrepareLaunchAsync(new(paths.InstanceRoot, fixture.Environment, definition.InstanceId, 1, RepairIncomplete: false));
        Require(incomplete.Status == MmcInstanceBackendStatus.NeedsRecovery && incomplete.Snapshot is null && !incomplete.Repaired,
            "Read-only prepare adopted damaged bytes or silently performed a repair.");
        var repaired = await offline.RepairAsync(new(paths.InstanceRoot, fixture.Environment, definition.InstanceId) {
            ExpectedGeneration = 1, ExpectedPrimarySha256 = prepared.Snapshot.PrimarySha256
        });
        Success(repaired.Status, repaired.Failure);
        Require(repaired.Definition!.InstanceId == definition.InstanceId && repaired.Definition.Generation == 2 &&
            repaired.BoundPlan!.CompiledProfile.MainClass == fixture.MainClass && repaired.BoundPlan.CompiledProfile.ProfileFingerprint == compiled.ProfileFingerprint,
            "Repair replaced the custom loader source, instance identity or component semantics.");
        Require(repaired.BoundPlan!.CompiledProfile.Classpath.Select(a => a.Coordinate!.Name).SequenceEqual(fixture.OrderedCoordinates, StringComparer.Ordinal), "Repair changed classpath order.");
        Require(Hash(File.ReadAllBytes(target.ActualPath)) == adopted.AdoptedSha256 && fixture.OfflineRequests == 0,
            "Repair did not use retained adopted originals exclusively.");
        CheckProjection(paths.InstanceRoot, fixture.Name, fixture.MainClass, definition.InstanceId, 2, repaired.BoundPlan.CompiledProfile, repaired.Definition);
        var second = await offline.PrepareLaunchAsync(new(paths.InstanceRoot, fixture.Environment, definition.InstanceId, 2, RepairIncomplete: false));
        Success(second.Status, second.Failure); Require(!second.Repaired && second.Snapshot!.Definition.Generation == 2, "Second prepare repaired or changed the published generation.");
        Require(File.ReadAllText(world) == "current player world" && File.ReadAllText(playerConfig) == "personal settings", "Repair changed current world/configuration bytes.");
        using (var preferences = new InstanceConfigProvider(paths.InstanceRoot)) {
            var current = MmcIniConfiguration.CapturePreferences(preferences);
            Require(current.Ini.GetValueOrDefault("MmcJvmArgsMode") == "Custom" && current.Ini.GetValueOrDefault("VersionAdvanceJvm", "not empty") == "" &&
                current.Config.Value<string>("personal-standard-fixture") == "keep", "Repair lost explicit-empty or unknown personal preferences.");
        }
        Require(fixture.OfflineRequests == 0, "Offline recovery attempted metadata or artifact transport.");
    }

    private static void CheckProjection(string root, string name, string main, Guid id, long generation, MmcCompiledProfile compiled, MmcInstanceDefinition definition) {
        var json = JObject.Parse(File.ReadAllText(Path.Combine(root, name + ".json")));
        Require(json.Value<string>("mainClass") == main && json["pcllfManagement"]!.Value<string>("instanceId") == id.ToString("D") &&
            json["pcllfManagement"]!.Value<long>("generation") == generation, "Compatibility projection lost entry or management identity.");
        string[] expected = compiled.Classpath.Select(a => definition.ArtifactLocks.Single(item => item.ArtifactKey == a.Key).TargetRelativePath).ToArray();
        Require(json["pcllfRuntime"]!["classpath"]!.Values<string>().SequenceEqual(expected), "Compatibility projection changed the bound classpath order.");
    }
    private static void Success(MmcInstanceBackendStatus status, string? failure) => Require(status == MmcInstanceBackendStatus.Succeeded, "Managed backend failed: " + status + ": " + failure);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-StandardMmcTests-" + Guid.NewGuid().ToString("N"));
        internal string Name { get; }
        internal string MinecraftVersion { get; }
        internal string LoaderVersion { get; }
        internal string LoaderCoordinate { get; }
        internal string MainClass { get; }
        internal int JavaMajor { get; }
        internal string[] OrderedCoordinates { get; }
        internal string Archive => Path.Combine(Root, Name + ".zip");
        internal string VersionRoot => Path.Combine(Root, "minecraft", "versions", Name);
        internal string CacheRoot => Path.Combine(Root, "cache");
        internal MmcRuntimeEnvironment Environment => new("windows", "x86_64", JavaMajor);
        internal Loopback Server { get; } = new();
        internal int OfflineRequests;
        private readonly List<HttpClient> clients = [];
        internal Fixture(bool fabric) {
            Directory.CreateDirectory(Root); Name = fabric ? "standard-fabric" : "standard-forge";
            MinecraftVersion = fabric ? "1.20.1" : "1.7.10"; LoaderVersion = fabric ? "0.16.0" : "10.13.4.1614"; JavaMajor = fabric ? 17 : 8;
            MainClass = fabric ? "net.fabricmc.loader.impl.launch.knot.KnotClient" : "net.minecraft.launchwrapper.Launch";
            LoaderCoordinate = fabric ? "net.fabricmc:fabric-loader:0.16.0" : "net.minecraftforge:forge:1.7.10-10.13.4.1614:universal";
            string lwjgl = fabric ? "org.lwjgl:lwjgl:3.3.1" : "org.lwjgl.lwjgl:lwjgl:2.9.4";
            string clientCoordinate = "net.minecraft:client:" + MinecraftVersion;
            var minecraft = new JObject {
                ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = MinecraftVersion, ["order"] = 0,
                ["mainClass"] = "net.minecraft.client.main.Main", ["compatibleJavaMajors"] = new JArray(JavaMajor),
                ["minecraftArguments"] = "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory}",
                ["mainJar"] = Library(clientCoordinate, "minecraft.jar", "net/minecraft/client/main/Main.class"),
                ["libraries"] = new JArray(Library(lwjgl, "lwjgl.jar", fabric ? "org/lwjgl/Version.class" : "org/lwjgl/Sys.class"))
            };
            byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); Server.Add("/assets/index.json", index);
            minecraft["assetIndex"] = new JObject { ["id"] = Name, ["url"] = Server.Url("/assets/index.json"),
                ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)), ["size"] = index.Length };
            string uid = fabric ? "net.fabricmc.fabric-loader" : "net.minecraftforge";
            var loader = new JObject { ["formatVersion"] = 1, ["uid"] = uid, ["version"] = LoaderVersion, ["order"] = 100,
                ["mainClass"] = MainClass, ["requires"] = new JArray(new JObject { ["uid"] = "net.minecraft", ["equals"] = MinecraftVersion }),
                ["+jvmArgs"] = new JArray("-Dstandard.fixture=" + Name) };
            if (fabric) {
                ((JArray)loader["requires"]!).Add(new JObject { ["uid"] = "net.fabricmc.intermediary", ["equals"] = MinecraftVersion });
                var intermediary = new JObject { ["formatVersion"] = 1, ["uid"] = "net.fabricmc.intermediary", ["version"] = MinecraftVersion, ["order"] = 50,
                    ["requires"] = new JArray(new JObject { ["uid"] = "net.minecraft", ["equals"] = MinecraftVersion }),
                    ["+libraries"] = new JArray(Library("net.fabricmc:intermediary:1.20.1", "intermediary.jar", "fixture/Intermediary.class")) };
                Server.Add("/metadata/net.fabricmc.intermediary/1.20.1.json", Encoding.UTF8.GetBytes(intermediary.ToString(Formatting.None)));
                loader["+libraries"] = new JArray(Library(LoaderCoordinate, "loader.jar", "net/fabricmc/loader/impl/launch/knot/KnotClient.class"),
                    Library("org.ow2.asm:asm:9.6", "asm.jar", "org/objectweb/asm/ClassReader.class"));
                OrderedCoordinates = [lwjgl, "net.fabricmc:intermediary:1.20.1", LoaderCoordinate, "org.ow2.asm:asm:9.6", clientCoordinate];
            } else {
                loader["+tweakers"] = new JArray("cpw.mods.fml.common.launcher.FMLTweaker");
                loader["+libraries"] = new JArray(Library("net.minecraft:launchwrapper:1.12", "launchwrapper.jar", "net/minecraft/launchwrapper/Launch.class"),
                    Library(LoaderCoordinate, "loader.jar", "cpw/mods/fml/common/launcher/FMLTweaker.class"));
                OrderedCoordinates = [lwjgl, "net.minecraft:launchwrapper:1.12", LoaderCoordinate, clientCoordinate];
            }
            Server.Add("/metadata/net.minecraft/" + MinecraftVersion + ".json", Encoding.UTF8.GetBytes(minecraft.ToString(Formatting.None)));
            Server.Add("/metadata/" + uid + "/" + LoaderVersion + ".json", Encoding.UTF8.GetBytes(loader.ToString(Formatting.None)));
            using var zip = ZipFile.Open(Archive, ZipArchiveMode.Create);
            Put(zip, "pack/mmc-pack.json", new JObject { ["formatVersion"] = 1, ["components"] = new JArray(
                new JObject { ["uid"] = "net.minecraft", ["version"] = MinecraftVersion }, new JObject { ["uid"] = uid, ["version"] = LoaderVersion }) }.ToString(Formatting.None));
            Put(zip, "pack/instance.cfg", "name=" + Name + "\nOverrideJavaArgs=true\nJvmArgs=-Dpackage=kept\n");
            Put(zip, "pack/minecraft/config/author.cfg", "author configuration");
        }
        private JObject Library(string coordinate, string leaf, string className) {
            byte[] bytes;
            using (var buffer = new MemoryStream()) {
                using (var jar = new ZipArchive(buffer, ZipArchiveMode.Create, true)) Put(jar, className, "self-authored inert fixture, never executed");
                bytes = buffer.ToArray();
            }
            Server.Add("/artifacts/" + leaf, bytes);
            return new JObject { ["name"] = coordinate, ["downloads"] = new JObject { ["artifact"] = new JObject {
                ["url"] = Server.Url("/artifacts/" + leaf), ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)), ["size"] = bytes.Length } } };
        }
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend(bool offline) {
            HttpClient client = offline ? new(new Offline(this)) : new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            clients.Add(client);
            var metadata = new MmcMetadataStore(Path.Combine(CacheRoot, "metadata"), new Uri(Server.Url("/metadata/")), client);
            return new(metadata, new(Path.Combine(CacheRoot, "libraries"), Path.Combine(CacheRoot, "assets"), Path.Combine(CacheRoot, "sources")), spec => spec,
                async (files, token) => {
                    if (offline) { OfflineRequests++; throw new IOException("Offline standard MMC recovery cannot download."); }
                    foreach (var file in files) {
                        Require(file.Urls.Length == 1 && Server.Owns(file.Urls[0]), "Artifact download escaped the fixture HTTP server.");
                        Owned(file.DownloadPath); byte[] bytes = await client.GetByteArrayAsync(file.Urls[0], token);
                        Directory.CreateDirectory(Path.GetDirectoryName(file.DownloadPath)!); await File.WriteAllBytesAsync(file.DownloadPath, bytes, token);
                    }
                });
        }
        private sealed class Offline(Fixture fixture) : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
                fixture.OfflineRequests++; throw new IOException("Offline standard MMC recovery cannot request metadata.");
            }
        }
        internal void Owned(string path) => Require(Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture IO escaped its exclusive root.");
        public void Dispose() {
            foreach (var client in clients) client.Dispose(); Server.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root);
            string root = Path.GetFullPath(Root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string leaf = Path.GetFileName(root); Require(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.StartsWith("PCLLF-StandardMmcTests-", StringComparison.Ordinal) &&
                Guid.TryParseExact(leaf["PCLLF-StandardMmcTests-".Length..], "N", out _), "Fixture cleanup lost its exclusive GUID boundary.");
            MmcInstancePaths.EnsureNoLinks(root); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void Put(ZipArchive zip, string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }

    private sealed class Loopback : IDisposable {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<string, byte[]> bodies = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> requests = new();
        private readonly Task worker;
        private TcpClient? active;
        private int disposed;
        private readonly string origin;
        internal Loopback() { listener.Start(); origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; worker = Task.Run(Serve); }
        internal string Url(string path) => origin + path;
        internal bool Owns(string value) => value.StartsWith(origin + "/", StringComparison.Ordinal);
        internal void Add(string path, byte[] content) { Require(bodies.TryAdd(path, content), "Duplicate fixture HTTP route."); }
        internal bool WasRequested(string path) => requests.Contains(path);
        internal int RequestCount => requests.Count;
        private async Task Serve() {
            try {
                while (!stop.IsCancellationRequested) {
                    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token); active = client;
                    await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string first = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Missing HTTP request line.");
                    Require(first.Length < 8192 && first.StartsWith("GET /", StringComparison.Ordinal), "Unexpected fixture HTTP request.");
                    string path = first.Split(' ')[1]; int count = 0;
                    while (true) { string? header = await reader.ReadLineAsync(stop.Token); if (string.IsNullOrEmpty(header)) break; Require(++count <= 64 && header.Length < 8192, "Oversized fixture HTTP headers."); }
                    requests.Enqueue(path); bool found = bodies.TryGetValue(path, out byte[]? body); body ??= Encoding.UTF8.GetBytes("missing owned route");
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, stop.Token); await stream.WriteAsync(body, stop.Token); await stream.FlushAsync(stop.Token); active = null;
                }
            } catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
        }
        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel(); listener.Stop(); active?.Dispose(); worker.GetAwaiter().GetResult(); stop.Dispose();
        }
    }
}
