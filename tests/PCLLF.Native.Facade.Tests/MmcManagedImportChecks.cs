using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Network;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcManagedImportChecks {
    internal const string Group = "mmc-managed-import";
    private const string ChildSwitch = "--mmc-managed-import-child", Receipt = "mmc-managed-import.done";
    private static Scope? retained;
    internal static void Run(FixtureEnvironment environment) {
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!args.Contains(ChildSwitch, StringComparer.Ordinal)) { RunChild(); return; }
        try {
            Native.Check(args.Length == 7 && args[0] == "--groups" && args[1] == Group && args[2] == ChildSwitch && args[4] == "--isolated" && args[5] == "--fixture" && Native.Full(args[6]) == Native.Full(environment.Root), "Managed MMC child identity differs.");
            ModpackUpdateCleanup.VerifyIsolatedChild(); retained = new(environment);
            retained.MixedRootRouting(); retained.UnstartedCancellation(); retained.Success(); retained.ScheduledCancellation(); retained.DecisionCancellation(); retained.UnsupportedAndMultipleRoots(); retained.Finish(); environment.AssertNoForbiddenIo();
            Native.Check(System.Windows.Application.Current.Windows.Count == 0, "Managed MMC opened a window.");
            File.WriteAllText(Path.Combine(environment.Root, Receipt), string.Join('\n', args[3], Environment.ProcessId, MainHash(), "published-and-drained"));
            Console.WriteLine("PASS mmc-managed-import: production Main callback/Loader/Java probe, owned HTTP NetFile download, missing cfg/name, full publication, pre/post-decision cancel fence and failed MMC refusal; no game launch.");
            Console.Out.Flush(); Console.Error.Flush(); using ManualResetEventSlim life = new(); life.Wait();
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(environment.Root, Receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("Managed MMC assertions failed with guards retained: " + error);
        }
    }
    private sealed class Scope {
        private readonly FixtureEnvironment environment;
        private readonly PackRootInstallChecksLoopback server = new();
        private readonly ConcurrentBag<Thread> workers = new();
        private readonly List<ModLoader.LoaderCombo<string>> roots = [];
        private readonly List<MmcInstanceBackendResult> published = [];
        private readonly List<ModNet.LoaderDownload> downloads = [];
        private readonly List<string?> candidates = [];
        private readonly HttpClient metadataClient = new();
        private readonly string root, game;
        private readonly int javaMajor;
        private ModLoader.LoaderCombo<string>? current;
        private MmcTransactionCheckpoint? cancelAt;
        private int prompts, forbidden;
        private string prompt = "";
        private ManualResetEventSlim? heldPrepared, preparedEntered;
        private static readonly byte[] AssetIndex = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal Scope(FixtureEnvironment environment) {
            this.environment = environment; root = Path.Combine(environment.Data, Group); game = Path.Combine(root, "minecraft"); Directory.CreateDirectory(game);
            ModBase.RunInUi(); _ = FileTaskShell.Install(); Native.Field(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, game + Path.DirectorySeparatorChar);
            // Genuine list refresh evaluates these two uncached legacy account records even
            // for an offline pack. Model only an empty owned store; every other read stays guarded.
            object settings = Native.Get(Native.Field(typeof(Settings), "Backend").GetValue(null)!, "host")!;
            var registry = (Func<string, string, string?, string?>)Native.Get(settings, "ReadRegistry")!;
            Native.Set(settings, "ReadRegistry", new Func<string, string, string?, string?>((path, key, fallback) =>
                path == @"HKEY_CURRENT_USER\Software\PCL" && key is "CacheNideServer" or "CacheAuthServerServer" ? null : registry(path, key, fallback)));
            var profile = new JsonConfigProvider(Path.Combine(root, "private-config.json")); ConfigProviders.Default = profile;
            Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "Java entries captured an external profile; refusing real user settings.");
            Java selected = FindJava(); javaMajor = selected.Version!.Major; Configs.JavaList.Set(new ConcurrentList<Java>([selected])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            var host = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            host.SignCdn = address => server.Owns(address) ? address : throw new IOException("Managed MMC fixture source escaped owned HTTP.");
            var sign = host.SignHeaders; host.SignHeaders = (string address, ref HttpRequestMessage request, bool browser, bool credentials) => {
                Native.Check(server.Owns(address) && !credentials, "Managed MMC used an external or credential-enabled request."); sign(address, ref request, browser, credentials);
            };
            host.TempFolder = () => Path.Combine(root, "network-temp") + Path.DirectorySeparatorChar;
            host.RequestTempFolder = () => Path.Combine(root, "network-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
            host.AppData = () => Path.Combine(root, "app-data") + Path.DirectorySeparatorChar; host.MinecraftFolders = () => [game + Path.DirectorySeparatorChar];
            var start = host.RunNewThread; host.RunNewThread = (work, name, priority) => { var thread = start(work, name, priority); workers.Add(thread); return thread; };
            object engine = Native.Field(typeof(ModModpack), "Service").GetValue(null)!; object packHost = Native.Get(engine, "host")!;
            Native.Set(packHost, "InstallGame", new Func<string, string, McComponents, List<ModLoader.LoaderBase>>((_, _, _) => { forbidden++; throw new IOException("Managed MMC reached ordinary InstallGame."); }));
            Native.Check(Native.Get(packHost, "InstallManagedMmc") is not null, "Production MMC callback is not installed.");
            Native.Property(typeof(MmcInstances), "ServicesFactory").SetValue(null, new Func<ModpackMmcInstallRequest, MmcImportServices>(_ => new() {
                Metadata = new MmcMetadataStore(Path.Combine(root, "metadata"), new Uri(server.Url("/metadata/")), metadataClient),
                CacheRoots = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(root, "source-cache")),
                SelectName = candidate => { candidates.Add(candidate); if (string.IsNullOrEmpty(candidate)) { prompts++; return prompt; } return candidate; },
                RegisterTask = value => { roots.Add(value); current = value; ModLoader.LoaderTaskbar.Add(value); },
                Published = value => published.Add(value),
                DownloadCreated = value => downloads.Add(value),
                TransactionObserved = (phase, _) => {
                    if(phase==MmcTransactionCheckpoint.Prepared&&heldPrepared is not null){preparedEntered!.Set();if(!heldPrepared.Wait(10000))throw new TimeoutException("Owned scheduled import was not released.");}
                    if (cancelAt == phase) current!.Cancel();
                }
            }));
        }
        internal void UnstartedCancellation(){
            using var pause=new MmcRootUpdatePause(()=>current);var canceled=Start("UnstartedCancel","name=UnstartedCancel\n");
            Native.Check(pause.Captured,"Managed import root Update was not captured at its actual scheduler boundary.");canceled.Cancel();pause.Release();pause.AwaitReturn();
            var worker=canceled.Loaders.Single();
            Native.Check((bool)Native.Property(worker.GetType(),"OperationCompleted").GetValue(worker)!&&Native.Get(worker,"LastRunningThread")is null,"Unstarted import did not release its fence or started an I/O producer.");
            worker.Start("late-start-must-not-run");
            Native.Check(Native.Get(worker,"LastRunningThread")is null&&!Directory.Exists(Path.Combine(game,"versions","UnstartedCancel")),"Late canceled import Start wrote its target.");
        }
        internal void ScheduledCancellation(){
            using var held=new ManualResetEventSlim();using var entered=new ManualResetEventSlim();heldPrepared=held;preparedEntered=entered;
            var canceled=Start("ScheduledCancel","name=ScheduledCancel\n");
            try {
                Until(()=>entered.IsSet);canceled.Cancel();var worker=canceled.Loaders.Single();
                Native.Check(!(bool)Native.Property(worker.GetType(),"OperationCompleted").GetValue(worker)!&&Native.Get(worker,"LastRunningThread")is Thread {IsAlive:true},"Scheduled import released resources before its actual delegate drained.");
            }finally{held.Set();}
            Complete(canceled,ModBase.LoadState.Canceled);heldPrepared=null;preparedEntered=null;
            Native.Check((bool)Native.Property(canceled.Loaders.Single().GetType(),"OperationCompleted").GetValue(canceled.Loaders.Single())!,"Returned canceled import retained its fence.");
        }
        internal void Success() {
            var first = Start("ManagedFirstLine", "name=ManagedFirstLine\nOverrideJavaArgs=true\nJvmArgs=-Dmanaged=yes\n"); Complete(first, ModBase.LoadState.Finished);
            Native.Check(candidates.Last() == "ManagedFirstLine" && prompts == 0, "Core first-line instance name was lost.");
            VerifyPublished("ManagedFirstLine");
            prompt = "ManagedPrompt"; var missing = Start("missing-cfg", null); Complete(missing, ModBase.LoadState.Finished);
            Native.Check(prompts == 1 && candidates.Last() is null, "Missing cfg did not request a name."); VerifyPublished("ManagedPrompt");
            Native.Check(published.Count == 2 && forbidden == 0, "Managed roots did not finish through the new backend only.");
            Native.Check(server.Requested("/assets-managedfirstline") && server.Requested("/assets-missing-cfg") && downloads.Count >= 2, "Managed import did not use the owned HTTP endpoints through actual download loaders.");
        }
        internal void MixedRootRouting() {
            foreach (var (markers, expected) in new[] {
                (new[] { "mcbbs.packmeta", "mmc-pack.json" }, true),
                (new[] { "mmc-pack.json", "manifest.json" }, true),
                (new[] { "mmc-pack.json", "modrinth.index.json" }, true),
                (new[] { "manifest.json", "nested/mmc-pack.json", "CON/ordinary-unchecked" }, false),
                (new[] { "modrinth.index.json", "nested/mmc-pack.json" }, false)
            }) {
                string path = Path.Combine(root, "routing-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string marker in markers) Add(archive, marker, "{}");
                Native.Check(MmcBoundedArchiveClassifier.IsMmcCandidate(path) == expected, "Root MMC must use Core; nested loose markers must preserve an explicitly ordinary package.");
            }
        }
        internal void DecisionCancellation() {
            cancelAt = MmcTransactionCheckpoint.Prepared;
            var early = Start("BeforeDecision", "name=BeforeDecision\n"); Complete(early, ModBase.LoadState.Canceled);
            Native.Check(!Directory.Exists(Path.Combine(game, "versions", "BeforeDecision")), "Cancelled predecision import was published.");
            cancelAt = MmcTransactionCheckpoint.CommitDecided;
            var late = Start("AfterDecision", "name=AfterDecision\n"); Complete(late, ModBase.LoadState.Finished);
            VerifyPublished("AfterDecision"); cancelAt = null;
        }
        internal void UnsupportedAndMultipleRoots() {
            int before = published.Count;
            var unsupported = Start("Unsupported", "name=Unsupported\nOverrideGameTime=true\n"); Complete(unsupported, ModBase.LoadState.Failed);
            string archive = Zip("two-roots", "name=Ambiguous\n", true); var ambiguous = ModModpack.ModpackInstall(archive); Complete(ambiguous, ModBase.LoadState.Failed);
            Native.Check(published.Count == before && forbidden == 0 && !Directory.Exists(Path.Combine(game, "versions", "Unsupported")), "Unsupported MMC fell back or published success.");
        }
        private ModLoader.LoaderCombo<string> Start(string id, string? config) => ModModpack.ModpackInstall(Zip(id, config));
        private string Zip(string id, string? config, bool duplicate = false) {
            string assetId = id.ToLowerInvariant(); string route = "/assets-" + assetId; server.Add(route, AssetIndex);
            string archive = Path.Combine(root, id + ".zip");
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
            foreach (string prefix in duplicate ? new[] { "one/", "two/" } : new[] { "pack/" }) {
                Add(zip, prefix + "mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
                if (config is not null) Add(zip, prefix + "instance.cfg", config);
                string patch = $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"assetIndex":{"id":"{{assetId}}","url":"{{server.Url(route)}}","sha1":"{{Convert.ToHexStringLower(SHA1.HashData(AssetIndex))}}","size":{{AssetIndex.Length}}},"compatibleJavaMajors":[{{javaMajor}}]}""";
                Add(zip, prefix + "patches/net.minecraft.json", patch); Add(zip, prefix + "libraries/fixture.jar", "launcher private");
                Add(zip, prefix + "minecraft/libraries/fixture.jar", "game private"); Add(zip, prefix + "minecraft/config/example.cfg", "initial game config");
            }
            return archive;
        }
        private static void Add(ZipArchive zip, string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); }
        private void Complete(ModLoader.LoaderCombo<string> loader, ModBase.LoadState expected) {
            Until(() => loader.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting) && loader.Loaders.OfType<ModLoader.LoaderTask<string, MmcInstanceBackendResult>>().All(value => value.LastRunningThread is null || !value.LastRunningThread.IsAlive));
            Native.Check(loader.State == expected, "Managed loader state mismatch: " + loader.State + " expected=" + expected + " error=" + loader.Error);
            foreach (var value in ModNet.NetManager.Tasks) if (value.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting) throw new IOException("Managed downloader still active after root completion.");
            Until(() => ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading && (ModMinecraft.McInstanceListLoader.LastRunningThread is null || !ModMinecraft.McInstanceListLoader.LastRunningThread.IsAlive));
        }
        private void VerifyPublished(string name) {
            string version = Path.Combine(game, "versions", name); var inspected = MmcInstanceInspector.Read(version);
            Native.Check(inspected.IsConsistent && inspected.Definition!.Generation == 1, "Managed publication is not inspectable: " + inspected.Error);
            Native.Check(File.ReadAllText(Path.Combine(version, "libraries", "fixture.jar")) == "launcher private" && File.ReadAllText(Path.Combine(version, "minecraft", "libraries", "fixture.jar")) == "game private", "Managed library roots were flattened.");
            Native.Check(File.ReadAllText(Path.Combine(version, "minecraft", "config", "example.cfg")) == "initial game config", "Managed game data is missing.");
            Native.Check(!File.Exists(Path.Combine(version, "PCL", "MmcImportPending.json")), "Successful root remained pending.");
        }
        private void Until(Func<bool> complete) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            try { while (!complete()) { Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Managed import actual worker did not drain."); Thread.Sleep(2); } }
            catch (TimeoutException error) {
                throw new TimeoutException("Managed import did not drain: " + string.Join("; ", roots.SelectMany(value => new[] { value }.Cast<ModLoader.LoaderBase>().Concat(value.Loaders)).Select(value => value.Name + "=" + value.State + " progress=" + value.Progress + " error=" + value.Error)) + "; downloads=" + string.Join("; ", ModNet.NetManager.Tasks.Select(value => value.Name + "=" + value.State + " error=" + value.Error)), error);
            }
        }
        internal void Finish() {
            foreach (var download in downloads) {
                Native.Check(((ILegacyNetworkDownload<ModNet.NetFile>)download).CoreRecord.ActiveIoProducerCount == 0 && download.Files.All(file => file.ActiveIoProducerCount == 0), "Managed download retained an active file producer.");
            }
            Native.Check(ModNet.NetManager.FileRemain == 0, "Managed manager retains unfinished files.");
            server.Dispose(); metadataClient.Dispose(); InstanceConfigProviders.SaveAll(); Native.Check(forbidden == 0, "Managed import used ordinary installer.");
        }
        private static Java FindJava() {
            var paths = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Where(value => ((string)value.Key).StartsWith("JAVA_HOME", StringComparison.OrdinalIgnoreCase)).Select(value => Path.Combine((string)value.Value!, "bin"))
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths) {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(Path.Combine(path, "java.exe"))) continue;
                Java java = new(path); if (java.CheckAsync().GetAwaiter().GetResult() && java.Version?.Major is 8 or 17 or 21 or 24 or 26) return java;
            }
            throw new InvalidOperationException("Managed fixture needs an installed compatible Java; no runtime or game was downloaded.");
        }
    }
    private static string MainHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location)));
    private static void RunChild() {
        ModpackUpdateCleanup.VerifyIsolatedChild(); string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N")), nonce = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root); Program.ValidateRoot(root); Process? child = null; bool stopped = false;
        try {
            string output = Path.Combine(root, "output"); Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
            var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "--groups", Group, ChildSwitch, nonce, "--isolated", "--fixture", root }) start.ArgumentList.Add(value);
            child = Process.Start(start) ?? throw new IOException("Managed import child did not start."); Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            string receipt = Path.Combine(root, Receipt); var timer = Stopwatch.StartNew();
            while (!File.Exists(receipt)) {
                if (File.Exists(receipt + ".failed")) throw new IOException(File.ReadAllText(receipt + ".failed"));
                if (child.WaitForExit(10)) {
                    Native.Check(Task.WaitAll([stdout, stderr], 5000), "Failed managed child output did not drain.");
                    throw new IOException("Managed import child exited before its receipt: exit=" + child.ExitCode + "\n" + stdout.Result + "\n" + stderr.Result);
                }
                if (timer.ElapsedMilliseconds > 90000) throw new TimeoutException("Managed import child did not produce a completion receipt.");
            }
            Native.Check(File.ReadAllText(receipt) == string.Join('\n', nonce, child.Id, MainHash(), "published-and-drained"), "Managed receipt is not bound to the owned child/Main.");
            child.Kill(true); stopped = child.WaitForExit(5000); Native.Check(stopped, "Owned manager child did not stop.");
            Native.Check(Task.WaitAll([stdout, stderr], 5000), "Managed import output did not drain."); Console.Write(stdout.Result); Native.Check(stderr.Result.Length == 0, stderr.Result);
        } finally { if (child is not null) { if (!child.HasExited) { child.Kill(true); stopped = child.WaitForExit(5000); } child.Dispose(); } if (stopped) { Program.ValidateRoot(root); Directory.Delete(root, true); } }
    }
}
