using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using PCL;
using PCLCS;
using TaskLoader = PCL.ModLoader.LoaderTask<string, int>;
using ComboLoader = PCL.ModLoader.LoaderCombo<string>;

namespace PCLLF.Native.Facade.Tests;

// Only existing typed platform/dependency seams are controlled. Main's format
// dispatcher, MMC parser, task construction and actual pack workload stay real.
// Minecraft game installation is a captured dependency request, NEVER claimed
// installed. The selected actual pack task is run separately from that boundary.
internal sealed class MmcImportScope : IDisposable
{
    internal readonly record struct Request(string Name, string Folder, McComponents Components);
    private readonly FileTaskScope tasks;
    private readonly FixtureEnvironment environment;
    private readonly object host;
    private readonly Stack<Action> restore = new();
    private readonly ConcurrentDictionary<Thread, byte> threads = new();
    private readonly List<TaskLoader> workloads = new();
    private readonly List<string> temporary = new();
    // Bounded observations of synthetic MMC keys only; delegates/results and
    // exceptions stay original. No extra disk reads or global logger changes.
    private readonly ConcurrentQueue<IniObservation> iniTrace = new();
    private int iniTraceCount;
    private readonly record struct IniObservation(string Operation, string File, string Key,
        string State, string? Value, string? ErrorType, string? ErrorMessage);
    internal readonly List<Request> Requests = new();
    internal readonly List<(ComboLoader Loader, string Folder)> Plans = new();
    internal readonly List<ModpackMessage> Messages = new();
    internal readonly List<(string Text, bool Red)> Hints = new();
    internal readonly string Root, Game;
    internal string? PromptResult, DuplicateName;
    internal int PromptCount, AttachCount, RefreshCount, Forbidden;
    internal string LastCandidate = "";
    internal string LastTemp => temporary.Last();
    internal MmcImportScope(FixtureEnvironment environment)
    {
        this.environment = environment; tasks = new FileTaskScope(environment);
        try {
            Root = Path.Combine(tasks.Root, "mmc-import-contract");
            Native.Check(!Directory.Exists(Root), "Refusing existing MMC contract fixture."); Directory.CreateDirectory(Root);
            Game = Path.Combine(Root, "minecraft"); Directory.CreateDirectory(Game); tasks.SelectMinecraft(Game);
            Native.Check(ModMinecraft.McFolderSelected == Game + Path.DirectorySeparatorChar, "MMC fixture must select its root with exactly the FileTaskScope-owned trailing separator.");
            object engine = Native.Field(typeof(ModModpack), "Service").GetValue(null)!; host = Native.Get(engine, "host")!;
            // This retained suite is the legacy adapter oracle. The new production callback
            // is exercised separately by mmc-managed-import with the full owned backend.
            Set("InstallManagedMmc", null!);
            var readBoolean = (Func<string, string, bool, bool>)Native.Get(host, "ReadIniBoolean")!;
            Set("ReadIniBoolean", new Func<string, string, bool, bool>((file, key, fallback) => {
                ObserveIni("ReadIniBoolean", file, key, "call", fallback ? "true" : "false");
                try { bool result = readBoolean(file, key, fallback); ObserveIni("ReadIniBoolean", file, key, "returned", result ? "true" : "false"); return result; }
                catch (Exception error) { ObserveIni("ReadIniBoolean", file, key, "threw", null, error); throw; }
            }));
            var readIni = (Func<string, string, string?, string?>)Native.Get(host, "ReadIni")!;
            Set("ReadIni", new Func<string, string, string?, string?>((file, key, fallback) => {
                ObserveIni("ReadIni", file, key, "call", fallback);
                try { string? result = readIni(file, key, fallback); ObserveIni("ReadIni", file, key, "returned", result); return result; }
                catch (Exception error) { ObserveIni("ReadIni", file, key, "threw", null, error); throw; }
            }));
            var writeIni = (Action<string, string, object>)Native.Get(host, "WriteIni")!;
            Set("WriteIni", new Action<string, string, object>((file, key, value) => {
                // The selected command is string data; never invoke arbitrary
                // object ToString() or replace the value sent to production.
                ObserveIni("WriteIni", file, key, "call", value as string);
                try { writeIni(file, key, value); ObserveIni("WriteIni", file, key, "returned", value as string); }
                catch (Exception error) { ObserveIni("WriteIni", file, key, "threw", null, error); throw; }
            }));
            Set("RequestTempFolder", new Func<string>(() => { string folder = Path.Combine(Root, "temp", Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar; Directory.CreateDirectory(folder); temporary.Add(folder); return folder; }));
            var names = (Func<string, ModpackNameValidator>)Native.Get(host, "NameValidator")!;
            Set("NameValidator", new Func<string, ModpackNameValidator>(folder => { ModpackNameValidator real = names(folder); return new() { Validate = value => { LastCandidate = value ?? ""; return real.Validate(value); }, Prompt = () => { PromptCount++; return PromptResult; } }; }));
            Set("InstallGame", new Func<string, string, McComponents, List<ModLoader.LoaderBase>>((name, folder, components) => { Requests.Add(new(name, folder, components)); return new(); }));
            Set("Start", new Action<ModLoader.LoaderBase, string>((loader, folder) => {
                Native.Check(loader is ComboLoader && loader.GetType().Assembly == Native.Main, "MMC plan is not an actual Main combo.");
                ComboLoader root = (ComboLoader)loader; Plans.Add((root, folder));
                // Isolate the real pack child from the explicitly unselected
                // remote game dependency; this is not a completed root install.
                root.State = ModBase.LoadState.Canceled;
                foreach (ModLoader.LoaderBase child in root.Loaders) child.State = ModBase.LoadState.Canceled;
            }));
            Set("AttachInstallState", new Action<ModLoader.LoaderBase>(_ => AttachCount++)); // Real global install UI is outside this contract.
            Set("AddTaskbar", new Action<ModLoader.LoaderBase>(loader => { ModLoader.LoaderTaskbar.Add(loader); ((HashSet<ModLoader.LoaderBase>)Native.Get(tasks, "createdRoots")!).Add(loader); }));
            var taskbar = (Func<IEnumerable<string>>)Native.Get(host, "TaskbarNames")!;
            Set("TaskbarNames", new Func<IEnumerable<string>>(() => DuplicateName is null ? taskbar() : [DuplicateName]));
            Set("RefreshDownload", new Action(() => RefreshCount++));
            Set("Message", new Func<ModpackMessage, int>(message => { Messages.Add(message); return MessageResult; }));
            Set("Hint", new Action<string, bool>((text, red) => Hints.Add((text, red))));
            foreach (string name in new[] { "ModRequest", "CurseForgeProjects", "Download", "OpenExplorer", "StartProcess", "EndProgram", "SelectPackFile", "SelectInstallFolder", "WaitOptiFine", "OptiFineEntries", "RibbleDownload" }) Guard(name);
            var cache = Settings.Entries["LaunchAdvanceJvm"].ValueCache; bool had = cache.TryGetValue("", out object? before);
            restore.Push(() => { if (had) cache[""] = before!; else cache.TryRemove("", out _); }); cache[""] = "-Dglobal=kept";
        } catch { Dispose(); throw; }
    }
    private void ObserveIni(string operation, string file, string key, string state, string? value, Exception? error = null) {
        if (key is not ("OverrideCommands" or "PreLaunchCommand" or "VersionAdvanceRun")) return;
        string prefix = Root + Path.DirectorySeparatorChar;
        // Existing production paths are absolute under this owned fixture.
        // Pure lexical filtering adds no filesystem accesses or error fallback.
        if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) return;
        if (Interlocked.Increment(ref iniTraceCount) > 48) return;
        static string? Bound(string? text) => text is { Length: > 1024 } ? text[..1024] + "<truncated>" : text;
        iniTrace.Enqueue(new(operation, Bound(file[prefix.Length..])!, key, state,
            Bound(value), error?.GetType().FullName, Bound(error?.Message)));
    }
    private void FlushIniTrace() {
        // Called after the actual worker completion barrier, before its original
        // assertions; output failure is not swallowed or treated as a pass.
        Console.WriteLine("DIAG MMC synthetic host ini events=" + iniTrace.Count);
        while (iniTrace.TryDequeue(out IniObservation item))
            Console.WriteLine("DIAG MMC synthetic host ini " + System.Text.Json.JsonSerializer.Serialize(item));
    }
    internal int MessageResult = 2;
    private void Set(string name, object value) { FieldInfo field = Native.Field(host.GetType(), name); object? before = field.GetValue(host); restore.Push(() => field.SetValue(host, before)); field.SetValue(host, value); }
    private void Guard(string name) {
        FieldInfo field = Native.Field(host.GetType(), name); MethodInfo invoke = field.FieldType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(value => Expression.Parameter(value.ParameterType)).ToArray();
        Action fail = () => { Interlocked.Increment(ref Forbidden); throw new InvalidOperationException("Unselected MMC external dependency: " + name); };
        Set(name, Expression.Lambda(field.FieldType, Expression.Block(Expression.Invoke(Expression.Constant(fail)), Expression.Default(invoke.ReturnType)), parameters).Compile());
    }
    internal ComboLoader Plan(string zip) {
        int before = Plans.Count; ComboLoader loader = ModModpack.ModpackInstall(zip);
        Native.Check(Plans.Count == before + 1 && ReferenceEquals(Plans.Last().Loader, loader), "Actual MMC entry did not return its captured real plan.");
        Native.Check(loader.Loaders.Count == 2 && loader.Loaders[0] is ComboLoader pack && pack.Loaders.Count == 1 && loader.Loaders[1] is ComboLoader game && game.Loaders.Count == 0,
            "Actual MMC pack/game plan boundary changed.");
        return loader;
    }
    internal TaskLoader PackTask(ComboLoader plan) {
        TaskLoader task = (TaskLoader)((ComboLoader)plan.Loaders[0]).Loaders[0];
        Native.Check(task.GetType().Assembly == Native.Main && task.Name == "解压整合包文件" && !task.Block, "MMC workload is not its actual Main nonblocking extraction task.");
        if (!workloads.Contains(task)) {
            workloads.Add(task); FieldInfo field = Native.Field(task.GetType(), "LoadDelegate"); var work = (Action<TaskLoader>)field.GetValue(task)!;
            restore.Push(() => field.SetValue(task, work));
            field.SetValue(task, new Action<TaskLoader>(current => { threads.TryAdd(Thread.CurrentThread, 0); work(current); }));
        }
        return task;
    }
    internal void Execute(TaskLoader task, string folder) {
        task.Start(folder); DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true) {
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            if (task.State != ModBase.LoadState.Loading && threads.Keys.All(thread => ReferenceEquals(thread, Thread.CurrentThread) || !thread.IsAlive)) break;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Real MMC pack thread did not finish."); Thread.Sleep(2);
        }
        FlushIniTrace();
        Native.Check(task.State == ModBase.LoadState.Finished, "Actual MMC pack workload failed: " + task.Error);
    }
    internal void ExecuteCanceled(TaskLoader task) {
        // Exercise the actual workload's existing cancellation-token contract
        // without starting remote game work or claiming root-install rollback.
        task.State = ModBase.LoadState.Loading; CancellationToken token = task.CreateCancellationToken(); task.Cancel();
        Native.Check(token.IsCancellationRequested, "Real MMC loader Cancel did not cancel its token.");
        var work = (Action<TaskLoader>)Native.Get(task, "LoadDelegate")!;
        bool canceled = false; try { work(task); } catch (OperationCanceledException) { canceled = true; }
        Native.Check(canceled, "Actual MMC extraction ignored the canceled real loader token.");
    }
    internal Exception ExecuteRejected(TaskLoader task) {
        // Synchronous actual workload invocation makes the original path failure
        // observable without a replacement extractor or asynchronous fake state.
        try { ((Action<TaskLoader>)Native.Get(task, "LoadDelegate")!)(task); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Invalid MMC extraction unexpectedly succeeded.");
    }
    public void Dispose() {
        try {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            foreach (TaskLoader task in workloads) if (task.State == ModBase.LoadState.Loading) task.Cancel();
            while (threads.Keys.Any(thread => !ReferenceEquals(thread, Thread.CurrentThread) && thread.IsAlive)) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual MMC workload thread did not terminate before restoring its host."); Thread.Sleep(2);
            }
            tasks.FinishActualTasks(); Native.Check(Volatile.Read(ref Forbidden) == 0, "An unselected MMC external dependency was attempted, including caught failures."); environment.AssertNoForbiddenIo();
            while (restore.TryPop(out Action? action)) action(); tasks.Dispose();
        } catch (Exception error) { Native.Call(typeof(FileTaskScope), "FailClosed", tasks, "MMC pack completion barrier failed before restoring platform context.", error); throw; }
    }
}
