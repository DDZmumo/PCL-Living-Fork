using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;
// The production Native facades, loaders and workers perform all work. These are
// the pinned LF progress fixtures plus controlled platform/UI setup, not new engines.
internal static class FileTaskScenarios
{
    internal static void Run(FixtureEnvironment environment)
    {
        using FileTaskScope scope = new(environment);
        string backup = Path.Combine(scope.Root, "backup");
        scope.SelectMinecraft(Path.Combine(backup, "minecraft"));
        Console.WriteLine("PASS native file-tasks backup: " + FileTaskBackupScenarios.Run(Native.Main, backup));
        string clone = Path.Combine(scope.Root, "clone");
        scope.SelectMinecraft(Path.Combine(clone, "minecraft"));
        FileTaskCloneScenarios.Run(Native.Main, clone);
        FileTaskNavigationScenarios.Run(scope);
        TransactionRollback(scope.Root);
        FileTaskUpdateCancellationScenarios.Run(scope.Root);
        scope.FinishActualTasks();
        environment.AssertNoForbiddenIo();
    }

    private static void TransactionRollback(string root)
    {
        string game = Path.Combine(root, "transaction", "versions", "fixture");
        Directory.CreateDirectory(game);
        string first = Path.Combine(game, "first.txt"), second = Path.Combine(game, "second.txt");
        string firstPayload = Path.Combine(root, "first-payload.txt"), secondPayload = Path.Combine(root, "second-payload.txt");
        File.WriteAllText(first, "first original");
        File.WriteAllText(second, "second original");
        File.WriteAllText(firstPayload, "first replacement");
        File.WriteAllText(secondPayload, "second replacement");
        string Digest(string path) => (string)Native.Static("PCL.ModpackUpdate", "Digest", path, "sha1")!;
        var operations = new List<Tuple<string, string, string>>
        {
            Tuple.Create(first, firstPayload, Digest(first)),
            Tuple.Create(second, secondPayload, Digest(second))
        };
        var reports = new List<double>();
        var sentinel = new IOException("Synthetic second-operation transaction failure.");
        Action<int> failSecond = index =>
        {
            if (index == 1)
                throw sentinel;
        };
        bool failed = false;
        try
        {
            Native.Static("PCL.ModpackUpdate", "ApplyTransactionWithProgress", game, game, operations, CancellationToken.None, new Action<double>(reports.Add), failSecond, null);
        }
        catch (IOException error)when (ReferenceEquals(error, sentinel))
        {
            failed = true;
        }

        Native.Check(failed && reports.Any(x => x > 0 && x < 1) && !reports.Contains(1), "Actual Main transaction did not stop during its second operation.");
        Native.Check(File.ReadAllText(first) == "first original" && File.ReadAllText(second) == "second original", "Actual Main transaction did not roll back the first applied replacement.");
        Native.Check(!Directory.EnumerateFiles(game, "*.pcl-update-*", SearchOption.AllDirectories).Any(), "Actual transaction left a staging file after rollback.");
        Native.Check(!((IEnumerable<string>)Native.Static("PCL.ModpackUpdate", "Pending", game)!).Any(), "Rolled-back transaction remained pending.");
        foreach (string path in new[]
        {
            first,
            second
        }

        )
        {
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        Console.WriteLine("PASS native file-tasks transaction: real Main two-file write, injected second-step failure, reverse rollback, completed recovery log and released handles.");
    }
}

internal static class FileTaskShell
{
    private static readonly List<(FormMain Window, FormMain? PreviousMain)> OwnedWindows = [];

    internal static FormMain Install()
    {
        var previousMain = ModMain.FrmMain;
        var main = new FormMain(host => {
            host.ReadInteger = key => key == "SystemLastVersionReg" ? ModBase.VersionCode : Settings.Get<int>(key);
            host.HasSaved = _ => true;
            host.ThemeRefresh = _ => { };
            host.IsAdmin = () => false;
            host.IsDebug = () => false;
            host.ApplyPackVisibility = _ => { };
            host.StartFolderLoader = () => { };
            host.Hint = (_, _) => { };
            host.WriteSetting = (key, _) => throw new InvalidOperationException("Unexpected startup setting write: " + key);
        });
        OwnedWindows.Add((main, previousMain));
        return main;
    }

    internal static void CloseUnshownHosts()
    {
        for (int index = OwnedWindows.Count - 1; index >= 0; index--)
        {
            var (window, previousMain) = OwnedWindows[index];
            if (System.Windows.Application.Current.Windows.Cast<Window>().Contains(window))
            {
                Native.Check(!window.IsVisible, "A headless facade host was unexpectedly shown.");
                window.Closing -= (System.ComponentModel.CancelEventHandler)Delegate.CreateDelegate(
                    typeof(System.ComponentModel.CancelEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Closing"));
                window.Close();
            }
            if (ReferenceEquals(ModMain.FrmMain, window)) ModMain.FrmMain = previousMain;
        }
        OwnedWindows.Clear();
    }
}

internal sealed class FileTaskScope : IDisposable
{
    private readonly FixtureEnvironment environment;
    private readonly Stack<Action> restore = new();
    private readonly HashSet<ModLoader.LoaderBase> originalTasks;
    private readonly HashSet<IWorker> originalWorkers;
    private readonly HashSet<ModLoader.LoaderBase> createdRoots = new(), createdNodes = new();
    private readonly HashSet<Thread> loaderThreads = new();
    private readonly ConcurrentDictionary<IWorker, byte> workers = new();
    private readonly ConcurrentBag<Thread> executionThreads = new();
    private readonly ConcurrentBag<Completion> completions = new();
    private readonly bool verifiedChild;
    private bool finished;
    internal string Root { get; }
    private sealed class Completion(string name)
    {
        internal readonly string Name = name;
        internal int Done, Refused;
        internal Exception? Error;
        internal bool Complete => Volatile.Read(ref Done) != 0 || Volatile.Read(ref Refused) != 0;
    }

    internal FileTaskScope(FixtureEnvironment environment)
    {
        VerifyIsolatedChild(environment); // Before changing roots or permitting any actual work.
        verifiedChild = true;
        this.environment = environment;
        Root = Path.Combine(environment.Data, "file-tasks");
        Directory.CreateDirectory(Root);
        originalTasks = new(ModLoader.LoaderTaskbar);
        originalWorkers = new(CurrentLeases().Values.Select(lease => lease.Worker));
        foreach (FieldInfo field in typeof(ModMain).GetFields(Native.Flags).Where(field => field.IsStatic && field.Name.StartsWith("Frm", StringComparison.Ordinal)))
        {
            object? previous = field.GetValue(null);
            restore.Push(() => field.SetValue(null, previous));
        }
        PreserveField(typeof(LauncherInstanceContext), "Instance");
        foreach (string name in new[] { "_McFolderSelected", "_McInstanceSelected", "McInstanceList", "McInstanceListForceRefresh", "IsFirstMcInstanceListLoad" })
            PreserveField(typeof(ModMinecraft), name);
        foreach (string name in new[] { "PCL.LocalGameData", "PCL.WorldBackups", "PCL.EnvironmentHistory" })
        {
            PropertyInfo field = Native.Property(Native.Type(name), "TestRoot");
            object? previous = field.GetValue(null);
            restore.Push(() => field.SetValue(null, previous));
            field.SetValue(null, Path.Combine(Root, name.Split('.').Last()));
        }
        Native.Check(Native.Full((string)Native.Property(Native.Type("PCL.LocalGameData"), "Root").GetValue(null)!).StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Actual personal-data root escaped the controlled fixture.");
        ObserveProductionCallbacks();
        ModBase.RunInUi(); // Initialize the genuine Native UI-thread identity on this STA.
    }

    private static void VerifyIsolatedChild(FixtureEnvironment environment)
    {
        string[] args = Environment.GetCommandLineArgs();
        int option = Array.IndexOf(args, "--fixture");
        Native.Check(Assembly.GetEntryAssembly() == typeof(FileTaskScope).Assembly && typeof(FileTaskScope).Assembly.GetName().Name == "PCLLF.Native.Facade.Tests" &&
            args.Contains("--isolated", StringComparer.Ordinal) && option >= 0 && option + 1 < args.Length,
            "File-task cleanup may terminate only the genuine isolated Native test child.");
        string root = args[option + 1];
        Program.ValidateRoot(root);
        Native.Check(Native.Full(root) == Native.Full(environment.Root) && Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(root, "output")), "File-task child/fixture/output identities differ.");
    }
    private static ConcurrentDictionary<string, LegacyMinecraftInstanceLease> CurrentLeases() =>
        (ConcurrentDictionary<string, LegacyMinecraftInstanceLease>)Native.Field(typeof(ModMinecraft.McInstanceLock), "LockedPaths").GetValue(null)!;
    private void PreserveField(Type owner, string name)
    {
        var field = Native.State(owner, name); object? previous = field.GetValue(null);
        restore.Push(() => field.SetValue(null, previous));
    }
    internal void SelectMinecraft(string root)
    {
        Directory.CreateDirectory(root);
        Native.State(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, root + Path.DirectorySeparatorChar);
    }
    internal static void CacheInstance(ModMinecraft.McInstance instance)
    {
        foreach (Settings.Setting setting in Settings.Entries.Values)
            if (setting.Source == Settings.Sources.Instance)
                setting.ValueCache[instance.PathVersion] = setting.DefaultValue!;
        Settings.Entries["VersionArgumentIndieV2"].ValueCache[instance.PathVersion] = true;
    }

    // These are the real workflow DTOs and real platform factories. Observe their
    // original callbacks; never replace a worker, transaction, state or algorithm.
    private Completion ObserveWork(string name, ref Action stopped, ref Action<CancellationToken, Action<string?>> execute)
    {
        Completion completion = new(name); completions.Add(completion);
        Action originalStopped = stopped;
        Action<CancellationToken, Action<string?>> originalExecute = execute;
        stopped = () => { originalStopped(); Volatile.Write(ref completion.Done, 1); };
        execute = (token, output) => { executionThreads.Add(Thread.CurrentThread); originalExecute(token, output); };
        return completion;
    }
    private void ObserveProductionCallbacks()
    {
        object worldService = Native.Field(Native.Type("PCL.WorldBackupsUi"), "Service").GetValue(null)!;
        var world = (WorldBackupWorkflowHost<ModMinecraft.McInstance>)Native.Get(worldService, "_host")!;
        var worldStart = world.StartWork;
        restore.Push(() => world.StartWork = worldStart);
        world.StartWork = (instance, work) =>
        {
            Completion completion = ObserveWork("WorldBackups.Work.Stopped", ref work.Stopped, ref work.Execute);
            bool accepted = worldStart(instance, work);
            if (!accepted) Volatile.Write(ref completion.Refused, 1);
            ObserveCreated(); return accepted;
        };
        object historyService = Native.Field(Native.Type("PCL.EnvironmentHistoryUi"), "Service").GetValue(null)!;
        var history = (EnvironmentHistoryWorkflowHost<ModMinecraft.McInstance>)Native.Get(historyService, "host")!;
        var historyStart = history.StartWork;
        restore.Push(() => history.StartWork = historyStart);
        history.StartWork = (instance, work) =>
        {
            Completion completion = ObserveWork("EnvironmentHistory.Work.Stopped", ref work.Stopped, ref work.Execute);
            bool accepted = historyStart(instance, work);
            if (!accepted) Volatile.Write(ref completion.Refused, 1);
            ObserveCreated(); return accepted;
        };
        object cloneService = Native.Field(Native.Type("PCL.InstanceClonerUi"), "Service").GetValue(null)!;
        var clone = (InstanceCloneWorkflowHost<ModMinecraft.McInstance>)Native.Get(cloneService, "host")!;
        var cloneStart = clone.StartWork;
        restore.Push(() => clone.StartWork = cloneStart);
        clone.StartWork = (instance, work) =>
        {
            Completion completion = ObserveWork("InstanceCloner.Work.Stopped", ref work.Stopped, ref work.Execute);
            Action release = work.ReleaseBusy;
            work.ReleaseBusy = () => { release(); Volatile.Write(ref completion.Refused, 1); };
            cloneStart(instance, work); ObserveCreated();
        };
        object flowEngine = Native.Field(Native.Type("PCL.ModpackUpdateFlow"), "Engine").GetValue(null)!;
        object flow = Native.Get(flowEngine, "host")!;
        FieldInfo stoppedField = Native.Field(flow.GetType(), "OnStopped");
        var flowStopped = (Action<RedoableWorker, Action>)stoppedField.GetValue(flow)!;
        restore.Push(() => stoppedField.SetValue(flow, flowStopped));
        stoppedField.SetValue(flow, new Action<RedoableWorker, Action>((worker, release) =>
        {
            workers.TryAdd(worker, 0);
            Completion completion = new("Modpack.Worker.Stopped"); completions.Add(completion);
            flowStopped(worker, () => { release(); Volatile.Write(ref completion.Done, 1); });
        }));
        var loaderHost = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action<Action> run = loaderHost.RunInThread;
        restore.Push(() => loaderHost.RunInThread = run);
        loaderHost.RunInThread = work =>
        {
            Completion completion = new("Loader state/cancel producer"); completions.Add(completion);
            run(() =>
            {
                try { work(); Volatile.Write(ref completion.Done, 1); }
                catch (Exception error) { completion.Error = error; throw; }
            });
        };
    }

    private void ObserveCreated()
    {
        foreach (ModLoader.LoaderBase task in ModLoader.LoaderTaskbar)
            if (!originalTasks.Contains(task)) createdRoots.Add(task);
        foreach (ModLoader.LoaderBase task in createdRoots.ToArray()) ObserveNode(task, new HashSet<ModLoader.LoaderBase>());
        foreach (LegacyMinecraftInstanceLease lease in CurrentLeases().Values)
            if (!originalWorkers.Contains(lease.Worker)) workers.TryAdd(lease.Worker, 0);
        if (ModMinecraft.McInstanceListLoader.LastRunningThread is Thread listThread) loaderThreads.Add(listThread);
    }
    private void ObserveNode(ModLoader.LoaderBase task, HashSet<ModLoader.LoaderBase> chain)
    {
        Native.Check(chain.Add(task), "Actual file-task combo unexpectedly contains a cycle.");
        createdNodes.Add(task);
        Type type = task.GetType();
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            Type definition = current.GetGenericTypeDefinition();
            if (definition == typeof(ModLoader.LoaderTask<,>))
            {
                if (Native.Field(type, "LastRunningThread").GetValue(task) is Thread thread) loaderThreads.Add(thread);
                break;
            }
            if (definition == typeof(ModLoader.LoaderCombo<>))
            {
                var children = (List<ModLoader.LoaderBase>)Native.Field(type, "Loaders").GetValue(task)!;
                foreach (ModLoader.LoaderBase child in children.ToArray()) ObserveNode(child, chain);
                break;
            }
        }
        if (task is ModLoader.LoaderWorker worker) workers.TryAdd(worker.Worker, 0);
        chain.Remove(task);
    }
    private static bool CoreFileGatesIdle()
    {
        // Original direct-engine snapshot tasks already join in their fixture
        // finally blocks. Also retain roots if a failed join leaves real IO
        // holding one of these genuine production gates.
        foreach (string name in new[] { "PCL.WorldBackups", "PCL.EnvironmentHistory", "PCL.InstanceCloner" })
        {
            object gate = Native.Field(Native.Type(name), "Gate").GetValue(null)!;
            if (!Monitor.TryEnter(gate)) return false;
            Monitor.Exit(gate);
        }
        return true;
    }
    private bool Quiescent()
    {
        ObserveCreated();
        return completions.All(completion => completion.Complete) && workers.Keys.All(worker => !worker.Running) && CoreFileGatesIdle() &&
            loaderThreads.All(thread => !thread.IsAlive) && executionThreads.All(thread => !thread.IsAlive) &&
            createdRoots.All(task => task.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading)) &&
            // A canceled combo can retain a never-started Waiting child. Its
            // terminal parent + returned threads/Stopped are the real barrier.
            createdNodes.All(task => task.State != ModBase.LoadState.Loading) &&
            !new[] { "PCL.WorldBackupsUi", "PCL.EnvironmentHistoryUi", "PCL.InstanceClonerUi" }.Any(type => (bool)Native.Property(Native.Type(type), "IsBusy").GetValue(null)!) &&
            !(bool)Native.Property(typeof(ModMinecraft.McInstanceLock), "IsAnyLocked").GetValue(null)! &&
            !(bool)Native.Property(Native.Type("PCL.ModpackUpdateFlow"), "IsUpdating").GetValue(null)! &&
            ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading && ModMinecraft.McInstanceListLoader.LastRunningThread?.IsAlive != true;
    }
    internal void FinishActualTasks()
    {
        if (finished) return;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                ObserveCreated(); // Keep real thread references before cancellation can clear fields.
                foreach (ModLoader.LoaderBase task in createdRoots.ToArray())
                    if (task.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) task.Cancel();
                foreach (IWorker worker in workers.Keys) if (worker.Running) worker.Cancel();
                DrainPostedUi(deadline);
                if (Quiescent())
                {
                    DrainPostedUi(deadline);
                    if (Quiescent()) break;
                }
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual file-task worker/stopped/thread/posted refresh did not settle.");
                Thread.Sleep(2);
            }
            foreach (ModLoader.LoaderBase task in createdRoots) ModLoader.LoaderTaskbar.Remove(task);
            environment.AssertNoForbiddenIo();
            finished = true;
        }
        catch (Exception error) { FailClosed("Completion barrier failed before context restoration.", error); }
    }
    private static void DrainPostedUi(DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("File-task UI drain exceeded its shared cleanup deadline.");
        DispatcherFrame frame = new(); bool drained = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherTimer timer = new(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds))
        };
        EventHandler expired = (_, _) => frame.Continue = false;
        timer.Tick += expired;
        DispatcherOperation idle = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { drained = true; frame.Continue = false; }));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= expired; if (idle.Status == DispatcherOperationStatus.Pending) idle.Abort(); }
        if (!drained || DateTime.UtcNow >= deadline) throw new TimeoutException("Actual file-task posted UI callbacks did not drain before the shared deadline.");
    }
    [DoesNotReturn]
    private void FailClosed(string message, Exception error)
    {
        if (!verifiedChild) throw new InvalidOperationException("No isolated file-task child proof.", error);
        Console.Error.WriteLine("FAIL isolated Native file-task cleanup; terminating ONLY current verified test child PID " + Environment.ProcessId + " with context retained: " + message + " " + error);
        Console.Error.WriteLine("Pending original producers: " + string.Join("; ", completions.Where(item => !item.Complete).Select(item => item.Name + (item.Error is null ? "" : " / " + item.Error))));
        Console.Error.Flush(); Console.Out.Flush();
        Environment.Exit(94);
        Environment.FailFast("The isolated file-task test child unexpectedly survived Environment.Exit.");
    }
    public void Dispose()
    {
        try
        {
            FinishActualTasks();
            // No finally restore: producer faults/timeouts retain every real
            // root/host callback until the verified test child exits nonzero.
            while (restore.Count != 0) restore.Pop()();
        }
        catch (Exception error) { FailClosed("Context restoration failed after quiescence.", error); }
    }
}
