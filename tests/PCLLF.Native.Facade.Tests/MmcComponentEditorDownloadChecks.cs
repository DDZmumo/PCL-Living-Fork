using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLCS.Network;

namespace PCLLF.Native.Facade.Tests;

// Original foreground NetManager loops retain their fixture guards until their
// dedicated, receipt-bound owner process is terminated by its verified parent.
internal static class MmcComponentEditorDownloadChecks {
    internal const string Group = "mmc-component-editor-download";
    private const string ChildSwitch = "--mmc-component-editor-download-child", Receipt = "mmc-component-editor-download.done";
    private static readonly string[] Managers = ["NetManager ThreadStarter 0", "NetManager ThreadStarter 1", "NetManager StatRefresher"];
    private static Scope? retained;
    internal static void Run(FixtureEnvironment environment) {
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!args.Contains(ChildSwitch, StringComparer.Ordinal)) { RunOwnedChild(); return; }
        try {
            Native.Check(args.Length == 7 && args[0] == "--groups" && args[1] == Group && args[2] == ChildSwitch && Guid.TryParseExact(args[3], "N", out _) && args[4] == "--isolated" && args[5] == "--fixture" && Native.Full(args[6]) == Native.Full(environment.Root), "Component download child is not bound to its owned fixture.");
            ModpackUpdateCleanup.VerifyIsolatedChild(); retained = new(environment); retained.CancelAndDrain(); retained.Finish(); environment.AssertNoForbiddenIo();
            Native.Check(System.Windows.Application.Current.Windows.Count == 0, "Component downloader opened a window.");
            string value = string.Join('\n', args[3], Environment.ProcessId, Native.Full(environment.Root), MainHash(), "component-cancel-and-producers-drained");
            string pending = Path.Combine(environment.Root, Receipt + ".pending");
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(Encoding.UTF8.GetBytes(value)); output.Flush(true); }
            Console.WriteLine("PASS mmc-component-editor-download: actual Main editor/fixed Preview/Commit prefetch through real NetFile/Loader HTTP; canceled busy retained until file/start/copy producers and workers drain; original foreground manager lifetime retained; no JVM/game.");
            Console.Out.Flush(); Console.Error.Flush(); File.Move(pending, Path.Combine(environment.Root, Receipt));
            using ManualResetEventSlim life = new(); life.Wait();
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(environment.Root, Receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("Component download child failed with guards retained: " + error);
        }
    }
    private sealed class Scope {
        private readonly FixtureEnvironment environment;
        private readonly FileTaskScope files;
        private readonly MmcComponentEditorUiChecks.Fixture data;
        private readonly PackRootInstallChecksLoopback server = new();
        private readonly ConcurrentBag<Thread> workers = new(), transfers = new();
        private readonly ManualResetEventSlim entered = new(), release = new();
        private readonly List<ModNet.LoaderDownload> downloads = [];
        private ModLoader.LoaderCombo<string>? current;
        private bool published;
        private bool Busy => (bool)Native.Property(Native.Type("PCL.MmcComponentEditorUi"), "HasActiveOperation").GetValue(null)!;
        internal Scope(FixtureEnvironment environment) {
            this.environment = environment; files = new(environment);
            string root = Path.Combine(files.Root, "component-download"); data = new(root); files.SelectMinecraft(data.Game);
            var scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            scheduler.RefreshExtraDownload = () => { };
            byte[] bytes = Enumerable.Range(0, 2048).Select(value => (byte)value).ToArray(); server.Add("/component.jar", bytes, entered, release);
            var host = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            Native.Check(ModNet.NetManager.FileRemain == 0 && ModNet.NetManager.Tasks.IsEmpty, "Component transport requires a fresh actual native manager.");
            host.SignCdn = value => server.Owns(value) ? value : throw new IOException("Component fixture transport escaped its owned HTTP source.");
            var sign = host.SignHeaders;
            host.SignHeaders = (string address, ref HttpRequestMessage request, bool browser, bool credentials) => {
                Native.Check(server.Owns(address) && !credentials, "Component input used an external or credential-enabled request."); sign(address, ref request, browser, credentials);
                Native.Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("x-api-key"), "Component loopback carried credentials.");
            };
            host.TempFolder = () => Path.Combine(root, "network-temp") + Path.DirectorySeparatorChar;
            host.RequestTempFolder = () => Path.Combine(root, "network-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
            host.AppData = () => Path.Combine(root, "app-data") + Path.DirectorySeparatorChar; host.MinecraftFolders = () => [data.Game + Path.DirectorySeparatorChar];
            var start = host.RunNewThread; host.RunNewThread = (work, name, priority) => { var thread = start(work, name, priority); workers.Add(thread); return thread; };
            var integer = host.IntegerSetting; host.IntegerSetting = name => name == "ToolDownloadThread" ? 4 : integer(name);
            Type ui = Native.Type("PCL.MmcComponentEditorUi");
            Native.Property(ui, "ServicesFactory").SetValue(null, new Func<ModMinecraft.McInstance, MmcComponentEditorServices>(_ => new() {
                Metadata = data.Metadata, CacheRoots = data.Caches,
                RegisterTask = task => { current = task; ModLoader.LoaderTaskbar.Add(task); },
                DownloadCreated = loader => downloads.Add(loader), Published = _ => published = true
            }));
            Native.Property(ui, "SelectEdit").SetValue(null, new Func<MmcComponentEditorModel, MmcComponentEditorChoice?, MmcComponentEditorChoice?>((model, _) => {
                var patch = JObject.Parse(model.PatchTexts["net.minecraftforge"]);
                ((JArray)patch["+libraries"]!).Add(new JObject { ["name"] = "fixture:remote:1", ["downloads"] = new JObject { ["artifact"] = new JObject {
                    ["url"] = server.Url("/component.jar"), ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)), ["size"] = bytes.Length } } });
                return new(new("net.minecraftforge", PatchContentUtf8: patch.ToString()));
            }));
            Native.Property(ui, "ConfirmPreview").SetValue(null, new Func<MmcComponentEditorConfirmation, int>(preview => {
                using var lease = MmcAdmissionGate.Acquire(data.Version, data.Primary.Definition!.InstanceId, TimeSpan.Zero);
                object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, data.Version)!;
                Native.Check(preview.CanCommit && (int)Native.Get(state, "PauseCount")! == 0, "Actual candidate confirmation was invalid or retained settings pause."); return 1;
            }));
        }
        internal void CancelAndDrain() {
            string before = data.Primary.PrimarySha256!; Native.Static("PCL.MmcComponentEditorUi", "Start", data.Owner());
            Until(() => entered.IsSet && downloads.Count == 1 && downloads[0].Files.Any(file => file.ActiveIoProducerCount > 0), "real HTTP producer starts");
            var loader = downloads.Single();
            Native.Check(loader.GetType().Assembly == Native.Main && loader.Files.Count == 1, "Component input bypassed the actual Main batch loader.");
            foreach (var file in loader.Files) lock (file.LockChain) if (file.Threads is not null) foreach (var part in file.Threads) if (part.Thread is not null) transfers.Add(part.Thread);
            try {
                current!.Cancel();
                Native.Check(Busy && Native.Get(current.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true } && loader.Files.Any(file => file.ActiveIoProducerCount > 0),
                    "Editor released its operation fence on canceled state before real HTTP drain.");
            } finally { release.Set(); }
            Until(() => !Busy && current!.State == ModBase.LoadState.Canceled && Native.Get(current.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }, "component worker returns after downloader drain");
            Native.Check(!published && data.Primary.PrimarySha256 == before && loader.Files.All(file => file.ActiveIoProducerCount == 0) && ((ILegacyNetworkDownload<ModNet.NetFile>)loader).CoreRecord.ActiveIoProducerCount == 0,
                "Canceled component transport published, changed primary or retained a native I/O producer.");
            Until(() => transfers.All(thread => !thread.IsAlive) && workers.Where(thread => !Managers.Contains(thread.Name, StringComparer.Ordinal)).All(thread => !thread.IsAlive), "actual native download workers exit");
        }
        private void Until(Func<bool> predicate, string phase) {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!predicate()) { Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Component download phase: " + phase + "; " + current?.Error); Thread.Sleep(2); }
        }
        internal void Finish() {
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); server.Dispose(); release.Dispose(); entered.Dispose();
            Native.Check(ModNet.NetManager.FileRemain == 0 && ModNet.NetManager.Tasks.All(task => task.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed), "Actual component manager retains unfinished files.");
            var managers = workers.Where(thread => Managers.Contains(thread.Name, StringComparer.Ordinal)).ToArray();
            Native.Check(managers.Length == 3 && managers.All(thread => thread.IsAlive && !thread.IsBackground), "Component test changed original foreground manager lifetime.");
            environment.AssertNoForbiddenIo();
        }
    }
    private static string MainHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location)));
    private static void RunOwnedChild() {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N")), nonce = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root); Program.ValidateRoot(root); Process? child = null; Task<string>? stdout = null, stderr = null; bool stopped = false;
        try {
            string output = Path.Combine(root, "output"); Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
            var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) {
                UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "--groups", Group, ChildSwitch, nonce, "--isolated", "--fixture", root }) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new IOException("Owned component downloader child did not start."); stdout = child.StandardOutput.ReadToEndAsync(); stderr = child.StandardError.ReadToEndAsync();
            string receipt = Path.Combine(root, Receipt); var timer = Stopwatch.StartNew();
            while (!File.Exists(receipt)) {
                if (File.Exists(receipt + ".failed")) throw new IOException("Owned component child failed: " + File.ReadAllText(receipt + ".failed"));
                if (timer.Elapsed > TimeSpan.FromSeconds(55)) throw new TimeoutException("Owned component downloader did not prove completion.");
                if (child.WaitForExit(10)) throw new IOException("Component child exited before its drain receipt: " + child.ExitCode);
            }
            Native.Check(File.ReadAllText(receipt) == string.Join('\n', nonce, child.Id, Native.Full(root), MainHash(), "component-cancel-and-producers-drained"), "Component drain receipt does not bind nonce/PID/root/Main.");
            child.Kill(true); Native.Check(child.WaitForExit(5000), "Owned component manager child did not terminate."); stopped = true;
            Native.Check(Task.WaitAll([stdout, stderr], 5000) && stderr.Result.Length == 0, "Owned component output did not drain without error."); Console.Write(stdout.Result);
        } finally {
            if (child is not null) { if (!child.HasExited) { child.Kill(true); stopped = child.WaitForExit(5000); } child.Dispose(); }
            if (stdout is not null && stderr is not null && Task.WaitAll([stdout, stderr], 5000) && stderr.Result.Length != 0) { Console.Write(stdout.Result); Console.Error.Write(stderr.Result); }
            if (stopped) { Program.ValidateRoot(root); Directory.Delete(root, true); }
        }
    }
}
