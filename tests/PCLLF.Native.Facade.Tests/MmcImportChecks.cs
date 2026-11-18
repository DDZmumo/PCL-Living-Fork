using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using Combo = PCL.ModLoader.LoaderCombo<string>;
using PackTask = PCL.ModLoader.LoaderTask<string, int>;

namespace PCLLF.Native.Facade.Tests;

// The production MMC dispatcher has one Core/managed boundary. Ordinary-format
// workloads below retain their real extractor/override/cancellation regressions.
internal static class MmcImportChecks {
    private const string Manifest = """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.20.1"}]}""";
    private const string Patch = """{"formatVersion":1,"uid":"net.minecraft","version":"1.20.1","mainClass":"fixture.Main","compatibleJavaMajors":[17]}""";

    internal static void Run(FixtureEnvironment environment) {
        MmcFoundationChecks.Run(environment);
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) {
            CheckRemovedBoundary(scope);
            StandardCoreRouting(scope);
            CoreFailuresCannotFallback(scope);
            OrdinaryFormatsAndExtraction(scope);
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-import-contract: sole actual Main/MMCC Core route, standard root/nested source parsing, missing services/parser/semantic refusal without legacy fallback; ordinary HMCL/MCBBS overrides, extraction cancellation and traversal retained. No Java/network/game.");
    }

    private static void CheckRemovedBoundary(Scope scope) {
        Native.Check(typeof(ModModpack).GetMethod("InstallPackMMC", Native.Flags) is null &&
            scope.Engine.GetType().GetMethod("InstallPackMMC", Native.Flags) is null,
            "The obsolete ordinary MMC installer/wrapper is still reachable.");
        foreach (string field in new[] { "ReadIni", "ReadIniBoolean", "SettingString" })
            Native.Check(scope.Host.GetType().GetField(field, Native.Flags) is null, "An obsolete MMC-only INI conversion contract remains: " + field);
        Native.Check(scope.ProductionMmc.Method.DeclaringType == typeof(MmcInstances) && scope.ProductionMmc.Method.Name == "StartImport",
            "Production Main does not bind its single MMC installer to MmcInstances.StartImport.");
    }

    private static void StandardCoreRouting(Scope scope) {
        foreach (string prefix in new[] { "", "Prism/" }) {
            string path = scope.Zip(prefix.Length == 0 ? "standard-root" : "standard-nested",
                (prefix + "mmc-pack.json", Manifest), (prefix + "instance.cfg", "name=FirstLineStandard\n"),
                (prefix + "patches/net.minecraft.json", Patch), (prefix + "minecraft/config/owned.cfg", "author-data"));
            using (var source = MmcArchiveSourceReader.ReadAsync(path).GetAwaiter().GetResult()) {
                Native.Check(source.Package.Components.Length == 1 && source.Package.Components[0].Uid == "net.minecraft" &&
                    source.Package.Components[0].LocalPatch is not null && source.Package.Configuration.Name == "FirstLineStandard" &&
                    source.Package.GameRootLeaf == "minecraft", "The actual new Core did not retain the standard MMC source and first-line name.");
            }
            int before = scope.ManagedRequests.Count;
            using var pause = new MmcRootUpdatePause(() => scope.CurrentTask);
            var task = (Combo)ModModpack.ModpackInstall(path, "ExplicitCoreName", "owned-logo", new JObject { ["project"] = "owned-fixture" });
            Console.WriteLine("DIAG strict MMC standard: prefix=" + prefix + "; type=" + task.GetType().FullName +
                "; returned-state=" + task.State + "; update-captured=" + pause.Captured +
                "; managed-request-count=" + scope.ManagedRequests.Count + "; expected-count=" + (before + 1));
            Native.Check(pause.Captured, "Standard MMC did not reach its genuine initial root Update boundary.");
            // Cancel only after the native combo has entered Loading and before its
            // first Update can schedule the producer. Canceling from RegisterTask
            // races the later Combo.Start reset and is not an observation boundary.
            task.Cancel(); pause.Release(); pause.AwaitReturn(); scope.Wait(task, ModBase.LoadState.Canceled);
            Native.Check(task.GetType().Assembly == Native.Main, "Standard MMC returned a substitute assembly's loader.");
            Native.Check(scope.ManagedRequests.Count == before + 1, "Standard MMC request count differs: actual=" + scope.ManagedRequests.Count + "; expected=" + (before + 1));
            var selected = scope.ManagedRequests.Last();
            Console.WriteLine("DIAG strict MMC selection: " + selected + "; state=" + task.State +
                "; child-state=" + task.Loaders.Single().State + "; producer-completed=" +
                Native.Property(task.Loaders.Single().GetType(), "OperationCompleted").GetValue(task.Loaders.Single()) +
                "; producer-thread=" + (Native.Get(task.Loaders.Single(), "LastRunningThread") as Thread)?.ManagedThreadId);
            Native.Check(selected.ArchivePath == path, "Standard MMC archive path changed: " + selected.ArchivePath);
            Native.Check(selected.InstanceName == "ExplicitCoreName", "Standard MMC explicit instance name changed: " + selected.InstanceName);
            Native.Check(selected.LogoPath == "owned-logo", "Standard MMC logo selection changed: " + selected.LogoPath);
            Native.Check(selected.PlatformSource?.Value<string>("project") == "owned-fixture", "Standard MMC platform selection changed: " + selected.PlatformSource);
            Native.Check(Native.Get(task.Loaders.Single(), "LastRunningThread") is null &&
                (bool)Native.Property(task.Loaders.Single().GetType(), "OperationCompleted").GetValue(task.Loaders.Single())!,
                "Canceled standard MMC started a producer or retained its live cancellation fence.");
            scope.AssertNoOrdinaryMmcWork();
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        string loose = Path.Combine(scope.Game, "versions", "MultiMC-name-only"); Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, "mmc-pack.json"), Manifest);
        Native.Check(MmcInstanceInspector.Read(loose).IsLegacy, "A loose manifest or instance name was mistaken for installed management ownership.");
    }

    private static void CoreFailuresCannotFallback(Scope scope) {
        string standard = scope.Zip("services", ("mmc-pack.json", Manifest), ("patches/net.minecraft.json", Patch));
        object? callback = Native.Get(scope.Host, "InstallManagedMmc");
        Native.Set(scope.Host, "InstallManagedMmc", null);
        try {
            var error = Rejected(() => ModModpack.ModpackInstall(standard));
            Native.Check(error is InvalidOperationException && error.Message.Contains("MMC", StringComparison.Ordinal) && error.Message.Contains("服务", StringComparison.Ordinal),
                "A missing managed callback did not explicitly refuse MMC import.");
        } finally { Native.Set(scope.Host, "InstallManagedMmc", callback); }
        scope.ServicesMissing = true;
        var missing = Rejected(() => ModModpack.ModpackInstall(standard));
        Native.Check(missing is InvalidOperationException && missing.Message.Contains("服务", StringComparison.Ordinal), "Missing Core host services were hidden as a ZIP-open error or fell back.");
        scope.ServicesMissing = false;
        foreach (var sample in new[] {
            ("bad-json", "{broken", Patch),
            ("unknown-format", """{"formatVersion":999,"components":[{"uid":"net.minecraft","version":"1.20.1"}]}""", Patch),
            ("missing-components", """{"formatVersion":1}""", Patch),
            ("missing-runtime-semantics", Manifest, """{"formatVersion":1,"uid":"net.minecraft","version":"1.20.1"}""")
        }) {
            string path = scope.Zip(sample.Item1, ("mmc-pack.json", sample.Item2), ("patches/net.minecraft.json", sample.Item3));
            var task = (Combo)ModModpack.ModpackInstall(path, sample.Item1);
            scope.Wait(task, ModBase.LoadState.Failed);
            Native.Check(task.Error is not null && !Directory.Exists(Path.Combine(scope.Game, "versions", sample.Item1)), "Invalid MMC published a target instead of refusing Core parsing/required runtime semantics.");
            scope.AssertNoOrdinaryMmcWork();
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        // Root MMC cannot be rescued by adding an ordinary-format marker. Nested
        // loose markers inside a declared ordinary package are covered below.
        string mixed = scope.Zip("root-mixed", ("mcbbs.packmeta", "{}"), ("mmc-pack.json", "{broken"));
        var rejected = (Combo)ModModpack.ModpackInstall(mixed, "mixed-core-refusal");
        scope.Wait(rejected, ModBase.LoadState.Failed); scope.AssertNoOrdinaryMmcWork();
    }

    private static void OrdinaryFormatsAndExtraction(Scope scope) {
        scope.MmcPhase = false;
        foreach (string kind in new[] { "HMCL", "MCBBS" }) {
            string name = "ordinary-" + kind;
            string metadata = kind == "HMCL" ? "modpack.json" : "mcbbs.packmeta";
            string json = kind == "HMCL" ? "{\"name\":\"" + name + "\",\"gameVersion\":\"1.20.1\"}" :
                "{\"name\":\"" + name + "\",\"addons\":[{\"id\":\"game\",\"version\":\"1.20.1\"}]}";
            string overrides = kind == "HMCL" ? "minecraft/" : "overrides/";
            string path = scope.Zip(name, (metadata, json), (overrides + "options.txt", "gamma:1.0"),
                (overrides + "config/owned.cfg", "ordinary-author-data"), (overrides + "config/mmc-pack.json", "ordinary payload, not an instance owner"));
            int before = scope.ManagedRequests.Count;
            var plan = (Combo)ModModpack.ModpackInstall(path);
            Native.Check(scope.ManagedRequests.Count == before && scope.OrdinaryRequests.Last().Components.MinecraftVersion == "1.20.1" && plan.Name.StartsWith(kind, StringComparison.Ordinal),
                "Removing legacy MMC changed an ordinary format's genuine installation plan.");
            string folder = scope.OrdinaryRequests.Last().Folder;
            scope.Execute(scope.Pack(plan), folder);
            Native.Check(File.ReadAllText(Path.Combine(folder, "options.txt")) == "gamma:1.0" && File.ReadAllText(Path.Combine(folder, "config", "owned.cfg")) == "ordinary-author-data" &&
                ModBase.ReadIni(Path.Combine(folder, "PCL", "Setup.ini"), "VersionArgumentIndieV2", "") == "True",
                "Ordinary extraction/override isolation changed with MMC removal.");
        }
        string canceled = scope.Zip("ordinary-cancel", ("modpack.json", """{"name":"ordinary-cancel","gameVersion":"1.20.1"}"""), ("minecraft/options.txt", "must-not-copy"));
        var cancelPlan = (Combo)ModModpack.ModpackInstall(canceled); PackTask cancelTask = scope.Pack(cancelPlan);
        string temporary = scope.LastTemporary;
        File.WriteAllText(Path.Combine(temporary, "owned-before-cancel"), "owned fixture");
        cancelTask.State = ModBase.LoadState.Loading; var token = cancelTask.CreateCancellationToken(); cancelTask.Cancel();
        Native.Check(token.IsCancellationRequested && Rejected(() => { ((Action<PackTask>)Native.Get(cancelTask, "LoadDelegate")!)(cancelTask); return null; }) is OperationCanceledException &&
            !Directory.Exists(temporary), "Shared ordinary extraction no longer honors its genuine cancellation boundary.");
        string traversal = scope.Zip("ordinary-traversal", ("modpack.json", """{"name":"ordinary-traversal","gameVersion":"1.20.1"}"""), ("../escaped.txt", "must-not-write"));
        var traversalPlan = (Combo)ModModpack.ModpackInstall(traversal); var task = scope.Pack(traversalPlan);
        string extraction = scope.LastTemporary;
        Native.Check(Rejected(() => { ((Action<PackTask>)Native.Get(task, "LoadDelegate")!)(task); return null; }) is InvalidDataException &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(extraction.TrimEnd(Path.DirectorySeparatorChar))!, "escaped.txt")), "Shared extraction allowed archive traversal.");
    }

    private static Exception Rejected(Func<object?> action) { try { action(); } catch (Exception error) { return error; } throw new InvalidOperationException("Expected explicit refusal did not occur."); }

    private sealed class Scope : IDisposable {
        internal readonly record struct GameRequest(string Name, string Folder, McComponents Components);
        private readonly FileTaskScope files;
        private readonly Stack<Action> restore = new();
        private readonly List<Combo> tasks = [];
        private readonly ConcurrentDictionary<Thread, byte> workers = new();
        private readonly HttpClient client = new(new NoNetwork());
        private readonly List<string> temporary = [];
        internal readonly object Engine, Host;
        internal readonly Func<ModpackMmcInstallRequest, ModLoader.LoaderBase> ProductionMmc;
        internal readonly List<ModpackMmcInstallRequest> ManagedRequests = [];
        internal readonly List<GameRequest> OrdinaryRequests = [];
        internal readonly string Root, Game;
        internal bool ServicesMissing, MmcPhase = true;
        internal Combo? CurrentTask => tasks.LastOrDefault();
        private int forbidden;
        internal string LastTemporary => temporary.Last();

        internal Scope(FileTaskScope files) {
            this.files = files; Root = Path.Combine(files.Root, "mmc-core-dispatch"); Game = Path.Combine(Root, "minecraft");
            Directory.CreateDirectory(Game); files.SelectMinecraft(Game);
            // Genuine failed/completed combo transitions call the original scheduler's
            // FrmMain.BtnExtraDownload.ShowRefresh. Install the same native shell and
            // real controller used by the full managed-import fixture before any task
            // can run; do not replace that callback with a no-op or swallow its errors.
            _ = FileTaskShell.Install();
            Engine = Native.Field(typeof(ModModpack), "Service").GetValue(null)!; Host = Native.Get(Engine, "host")!;
            ProductionMmc = (Func<ModpackMmcInstallRequest, ModLoader.LoaderBase>)Native.Get(Host, "InstallManagedMmc")!;
            Set("InstallManagedMmc", new Func<ModpackMmcInstallRequest, ModLoader.LoaderBase>(request => { ManagedRequests.Add(request); return ProductionMmc(request); }));
            var factory = Native.Property(typeof(MmcInstances), "ServicesFactory"); object? previousFactory = factory.GetValue(null);
            restore.Push(() => factory.SetValue(null, previousFactory));
            factory.SetValue(null, new Func<ModpackMmcInstallRequest, MmcImportServices>(_ => ServicesMissing ? new() : new() {
                Metadata = new MmcMetadataStore(Path.Combine(Root, "metadata"), new Uri("https://fixture.invalid/metadata/"), client),
                CacheRoots = new(Path.Combine(Root, "libraries"), Path.Combine(Root, "assets"), Path.Combine(Root, "sources")),
                SelectName = candidate => string.IsNullOrEmpty(candidate) ? "core-owned" : candidate,
                RegisterTask = task => { tasks.Add(task); ModLoader.LoaderTaskbar.Add(task); },
                DownloadCreated = _ => { forbidden++; throw new IOException("Refused MMC fixtures cannot start downloads."); }
            }));
            Set("RequestTempFolder", new Func<string>(() => { if (MmcPhase) Forbidden("ordinary temporary folder"); string folder = Path.Combine(Root, "temporary", Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar; Directory.CreateDirectory(folder); temporary.Add(folder); return folder; }));
            Set("InstallGame", new Func<string, string, McComponents, List<ModLoader.LoaderBase>>((name, folder, components) => { if (MmcPhase) Forbidden("ordinary InstallGame"); OrdinaryRequests.Add(new(name, folder, components)); return []; }));
            Set("Start", new Action<ModLoader.LoaderBase, string>((loader, _) => { var task = (Combo)loader; tasks.Add(task); task.State = ModBase.LoadState.Canceled; foreach (var child in task.Loaders) child.State = ModBase.LoadState.Canceled; }));
            Set("AttachInstallState", new Action<ModLoader.LoaderBase>(_ => { if (MmcPhase) Forbidden("ordinary installation cleanup"); }));
            Set("AddTaskbar", new Action<ModLoader.LoaderBase>(task => ModLoader.LoaderTaskbar.Add(task)));
            Set("RefreshDownload", new Action(() => { }));
            Set("Message", new Func<ModpackMessage, int>(_ => { Forbidden("ordinary message fallback"); return 1; }));
            Set("SelectInstallFolder", new Func<string?>(() => { Forbidden("ordinary archive fallback"); return null; }));
        }
        private void Forbidden(string operation) { forbidden++; throw new IOException("MMC cannot use " + operation + "."); }
        private void Set(string name, object value) { var field = Native.Field(Host.GetType(), name); object? previous = field.GetValue(Host); restore.Push(() => field.SetValue(Host, previous)); field.SetValue(Host, value); }
        internal string Zip(string name, params (string Name, string Text)[] entries) {
            string path = Path.Combine(Root, name + ".zip"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open(), new UTF8Encoding(false)); writer.Write(entry.Text); }
            return path;
        }
        internal void AssertNoOrdinaryMmcWork() => Native.Check(forbidden == 0 && OrdinaryRequests.Count == 0 && temporary.Count == 0,
            "A refused/standard MMC package reached an ordinary installer, extractor or external dependency.");
        internal void Wait(Combo task, ModBase.LoadState expected) {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            do {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (task.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting) && task.Loaders.All(value => Native.Get(value, "LastRunningThread") is not Thread { IsAlive: true })) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual strict MMC parser task did not drain: " + task.Error);
                Thread.Sleep(2);
            } while (true);
            Native.Check(task.State == expected, "Strict MMC task state differs: " + task.State + "; " + task.Error);
        }
        internal PackTask Pack(Combo plan) {
            var task = (PackTask)((Combo)plan.Loaders[0]).Loaders.Single();
            var work = (Action<PackTask>)Native.Get(task, "LoadDelegate")!;
            Native.Set(task, "LoadDelegate", new Action<PackTask>(value => { workers.TryAdd(Thread.CurrentThread, 0); work(value); }));
            return task;
        }
        internal void Execute(PackTask task, string folder) {
            task.Start(folder); var deadline = DateTime.UtcNow.AddSeconds(8);
            while (task.State == ModBase.LoadState.Loading || workers.Keys.Any(thread => !ReferenceEquals(thread, Thread.CurrentThread) && thread.IsAlive)) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Ordinary pack extraction did not drain."); Thread.Sleep(2);
            }
            Native.Check(task.State == ModBase.LoadState.Finished, "Ordinary pack extraction failed: " + task.Error);
        }
        public void Dispose() {
            files.FinishActualTasks();
            Native.Check(forbidden == 0 && workers.Keys.All(thread => ReferenceEquals(thread, Thread.CurrentThread) || !thread.IsAlive), "MMC/ordinary fixture retained a producer or attempted a forbidden fallback.");
            while (restore.TryPop(out var action)) action(); foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task); client.Dispose();
        }
        private sealed class NoNetwork : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Strict parser fixtures must resolve only supplied local metadata; no network: " + request.RequestUri);
        }
    }
}
