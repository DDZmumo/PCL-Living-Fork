using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static void LaunchCancelAndDrain(ModLoader.LoaderCombo<int> task)
    {
        // A failed cleanup may terminate only the dedicated, GUID-owned test
        // child. Verify this before touching the actual scheduling primitive.
        string[] args = Environment.GetCommandLineArgs();
        int fixtureIndex = Array.IndexOf(args, "--fixture");
        Native.Check(args.Contains("--isolated", StringComparer.Ordinal) && fixtureIndex >= 0 && fixtureIndex + 1 < args.Length &&
            System.Reflection.Assembly.GetEntryAssembly() == typeof(FixtureEnvironment).Assembly,
            "Dependency cancel cleanup can terminate only its isolated NativeFacade test child.");
        string fixture = Native.Full(args[fixtureIndex + 1]);
        Native.Check(fixture.StartsWith(Native.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(fixture), "^PclNativeFacade-[a-f0-9]{32}$") &&
            Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(fixture, "output")),
            "Dependency cancel cleanup cannot terminate a nonfixture process.");

        // LF50 Combo.Cancel synchronously marks its parent but queues child
        // cancellation. Preserve actual inline/new-thread behavior and observe
        // queued actions, their tails and real producer threads, not State alone.
        var host = (PCLCS.Loaders.LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)
            Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action<Action> run = host.RunInThread;
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<Thread, byte>();
        int pending = 0, generation = 0, cleanupVerified = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int Remaining() => Math.Max(0, 5000 - checked((int)elapsed.ElapsedMilliseconds));
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        void FailCleanup(string message, Exception? error = null)
        {
            // Never unwind outer fixture restoration while a real action/UI
            // callback might still access its guarded settings/index/paths.
            Console.Error.WriteLine("FAIL isolated native dependency cancel cleanup; context retained until child exit: " + message +
                (error is null ? "" : " / " + error));
            Console.Error.Flush(); Environment.Exit(1);
            throw new InvalidOperationException("Isolated dependency child exit unexpectedly returned.");
        }
        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref cleanupVerified) == 0)
                FailCleanup("The shared 5s producer/UI cleanup deadline expired, including a possibly blocked dispatcher callback.");
        }, null, 5000, Timeout.Infinite);
        host.RunInThread = action =>
        {
            Interlocked.Increment(ref pending); Interlocked.Increment(ref generation);
            int entered = 0;
            try
            {
                run(() =>
                {
                    Interlocked.Exchange(ref entered, 1); threads.TryAdd(Thread.CurrentThread, 0);
                    try { action(); }
                    finally { Interlocked.Decrement(ref pending); }
                });
            }
            catch
            {
                if (Volatile.Read(ref entered) == 0) Interlocked.Decrement(ref pending);
                throw;
            }
        };
        void DrainUiWithinBudget()
        {
            if (Remaining() == 0) FailCleanup("No time remains for the posted UI fence.");
            var frame = new System.Windows.Threading.DispatcherFrame(); bool drained = false;
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send,
                System.Windows.Threading.Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, Remaining())) };
            timer.Tick += (_, _) => frame.Continue = false;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => { drained = true; frame.Continue = false; }));
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!drained || Remaining() == 0) FailCleanup("Posted UI notifications did not drain inside the shared 5s budget.");
        }
        void FinishProducers()
        {
            int quietPasses = 0;
            while (true)
            {
                if (Remaining() == 0 || !SpinWait.SpinUntil(() => Volatile.Read(ref pending) == 0, Remaining()))
                    FailCleanup("Dependency cancel producer did not reach its real action tail.");
                int before = Volatile.Read(ref generation);
                foreach (Thread thread in threads.Keys)
                    if (!ReferenceEquals(thread, Thread.CurrentThread) && !thread.Join(Remaining()))
                        FailCleanup("Dependency cancel producer thread did not terminate.");
                DrainUiWithinBudget();
                bool quiet = Volatile.Read(ref pending) == 0 && Volatile.Read(ref generation) == before &&
                    threads.Keys.All(thread => ReferenceEquals(thread, Thread.CurrentThread) || !thread.IsAlive);
                quietPasses = quiet ? quietPasses + 1 : 0;
                if (quietPasses == 2) return;
                if (Remaining() == 0) FailCleanup("Dependency cancellation/UI producers did not settle.");
            }
        }
        try
        {
            task.Cancel();
            Console.WriteLine("DIAG activation:69 immediately after real Cancel parent=" + task.State + ", child=" + task.Loaders[0].State);
            Native.Check(task.State == ModBase.LoadState.Canceled, "LF Combo.Cancel did not synchronously cancel its parent.");
            FinishProducers();
            AD(69, task.State.ToString() == "Canceled" && task.Loaders[0].State.ToString() == "Canceled");
            Console.WriteLine("DIAG activation:69 completed parent=" + task.State + ", child=" + task.Loaders[0].State +
                ", pending=" + Volatile.Read(ref pending) + ", observedThreads=" + threads.Count);
        }
        finally
        {
            try { FinishProducers(); }
            catch (Exception error) { FailCleanup("Refusing scheduling/platform restoration after unsafe cleanup.", error); }
            // Deliberately NOT a nested finally: failure exits the verified
            // child with all isolation guards still installed. Same 5s budget.
            Volatile.Write(ref cleanupVerified, 1);
            watchdog.Change(Timeout.Infinite, Timeout.Infinite);
            host.RunInThread = run;
        }
    }

    private static void LaunchActivation(string parent)
    {
        string root = Path.Combine(parent, "PclDependencyActivation-" + Guid.NewGuid().ToString("N"));
        string mods = Path.Combine(root, "mods"); Directory.CreateDirectory(mods);
        try
        {
            TContext context = new() { MinecraftVersion = "1.21.11", JavaVersion = "21.0.2", LoaderKnown = true };
            context.LoaderVersions.Add("fabricloader", "0.19.3");
            string[] ids = ["fabric-resource-conditions-api-v1", "fabric-events-interaction-v0", "fabric-networking-api-v1"];
            Dictionary<string, object> entries = new(StringComparer.Ordinal); JArray jars = new();
            for (int i = 0; i < ids.Length; i++)
            {
                string child = LaunchFabric(Path.Combine(root, "child" + i + ".jar"), ids[i], version: "5.0.35+test");
                string name = "META-INF/jars/child" + i + ".jar"; entries[name] = File.ReadAllBytes(child); jars.Add(new JObject { ["file"] = name });
            }
            entries["fabric.mod.json"] = new JObject
            {
                ["schemaVersion"] = 1, ["id"] = "fabric-api", ["name"] = "Fabric API", ["version"] = "0.141.6+1.21.11", ["jars"] = jars,
                ["depends"] = new JObject { ["minecraft"] = ">=1.21.11", ["fabricloader"] = ">=0.18.0" }
            }.ToString(Newtonsoft.Json.Formatting.None);
            string disabled = Jar(mods, "fabric-api-test.jar.disabled", entries);
            LaunchFabric(Path.Combine(mods, "lamb.jar"), "lamb", new JObject { [ids[0]] = ">=5.0.35" }, "1.0", "LambDynamicLights (Runtime)");
            LaunchFabric(Path.Combine(mods, "opti.jar"), "opti", new JObject { [ids[1]] = "*", [ids[2]] = "*" }, "1.0", "OptiGUI");
            TReport ScanActivation() => Scan(mods, context);
            object PlanActivation() => ActivationPlan(mods, ScanActivation());
            TReport report = ScanActivation(); object plan = PlanActivation();
            AD(35, PlanList(plan, "Enables").Count == 1 && PlanList(plan, "Items").Count == 0 && ((ICollection)Native.Get(PlanList(plan, "Enables")[0]!, "Requirements")!).Count == 3, string.Join("; ", PlanNotes(plan)));
            AD(36, (string)Native.Property(plan.GetType(), "ActionText").GetValue(plan)! == "启用并启动");
            AD(37, !report.Mods.Any(mod => mod.RootPath == disabled && mod.Enabled));
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            var view = LaunchCheckView(report, plan);
            string[] captions = LaunchViewText(view).ToArray();
            AD(41, !captions.Any(text => text.StartsWith("缺少依赖", StringComparison.Ordinal)) &&
                captions.Any(text => text.StartsWith("可启用", StringComparison.Ordinal) && text.Contains("1 个文件", StringComparison.Ordinal)));
            string sourceHash = Hash(disabled, "SHA256"), destination = (string)Native.Get(PlanList(plan, "Enables")[0]!, "Destination")!;
            using (CancellationTokenSource cancel = new())
            {
                cancel.Cancel(); LaunchReject("activation", 48, () => LaunchInstall(plan, cancel.Token));
            }
            AD(49, File.Exists(disabled) && !File.Exists(destination));
            int count = LaunchInstall(plan);
            AD(51, count == 1 && !ScanActivation().HasErrors && !File.Exists(disabled) && Hash(destination, "SHA256") == sourceHash);
            File.Move(destination, disabled); plan = PlanActivation();
            string collision = (string)Native.Get(PlanList(plan, "Enables")[0]!, "Destination")!;
            File.WriteAllText(collision, "existing-do-not-overwrite");
            LaunchReject("activation", 54, () => LaunchInstall(plan));
            AD(55, File.ReadAllText(collision) == "existing-do-not-overwrite" && File.Exists(disabled)); File.Delete(collision);
            plan = PlanActivation(); string extra = LaunchFabric(Path.Combine(root, "extra.jar"), "extra", version: "1.0", displayName: "Extra");
            object item = LaunchNew("PCL.ModDependencyAutoFill+Item"); Native.Set(item, "Name", "Extra");
            ResourceVersion version = new()
            {
                Id = "extra", ResourceType = ResourceTypes.Mod, ModLoaders = ModLoaders.Fabric, GameVersions = ["1.21.11"],
                FileName = "conflict.jar", Size = checked((int)new FileInfo(extra).Length), Hash = Hash(extra), DownloadUrls = ["https://cdn.modrinth.com/data/extra.jar"]
            };
            Native.Set(item, "Version", version); PlanList(plan, "Items").Add(item);
            string conflict = Path.Combine(mods, "conflict.jar"); Directory.CreateDirectory(conflict);
            Action<List<ModNet.NetFile>, CancellationToken> download = (files, token) => { foreach (var file in files) { token.ThrowIfCancellationRequested(); File.Copy(extra, file.LocalPath); } };
            LaunchReject("activation", 63, () => LaunchInstall(plan, download: download));
            AD(64, File.Exists(disabled) && !File.Exists(destination) && Hash(disabled, "SHA256") == sourceHash); Directory.Delete(conflict);
            List<ModNet.NetFile> files = [version.ToNetFile(Path.Combine(root, "target.jar"))];
            var task = (ModLoader.LoaderCombo<int>)Native.Call(LaunchAutoFill, "CreateDownloadTask", null, plan, files)!;
            AD(67, task.GetType().Assembly == Native.Main && task.Loaders.Count == 1 && task.Loaders[0] is ModNet.LoaderDownload && task.Loaders[0].Name.Contains("Extra", StringComparison.Ordinal));
            LaunchCancelAndDrain(task);
            context.MinecraftVersion = "1.20.1"; AD(70, PlanList(PlanActivation(), "Enables").Count == 0); context.MinecraftVersion = "1.21.11";
            string duplicate = Path.Combine(mods, "another-api.jar.disabled"); File.Copy(disabled, duplicate);
            AD(71, PlanList(PlanActivation(), "Enables").Count == 0); File.Delete(duplicate);
            Console.WriteLine("PASS activation original 35..71: true nested ZIP providers grouped by outer JAR, actual named Main download task cancellation, byte-preserving enable/recheck, no-overwrite and post-enable mixed rollback/ambiguity guards.");
        }
        finally
        {
            string full = Path.GetFullPath(root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            AD(85, full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "\\APclDependencyActivation-[a-f0-9]{32}\\z"));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
