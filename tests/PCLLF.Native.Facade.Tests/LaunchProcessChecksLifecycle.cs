using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal sealed partial class LaunchProcessChecksScope
{
    private readonly ConcurrentDictionary<Process, (DataReceivedEventHandler Output, DataReceivedEventHandler Error)> handlers = new();

    private void InstallObservers()
    {
        var loaderHost = (PCLCS.Loaders.LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action<Action> loaderSchedule = loaderHost.RunInThread;
        Set(loaderHost, "RunInThread", new Action<Action>(work => Schedule(loaderSchedule, work, true)));
        foreach (string name in new[] { "Request", "SelectJava", "AvailablePhysicalMemory", "SetGpuPreference", "RunAdminTaskDone", "OAuthClientId", "OpenWebsite", "OpenExplorer", "LoadImage", "ClientFix", "MemoryOptimize" }) Guard(launch, name);
        Set(launch, "Debug", new Func<bool>(() => false));
        Set(launch, "RamGb", new Func<ModMinecraft.McInstance, double>(_ => 1)); // Reporting only; no RAM sizing/discovery is tested.
        Set(launch, "AssetsIndexName", new Func<ModMinecraft.McInstance, string>(_ => "controlled-no-assets"));
        Set(launch, "HideLauncher", new Action(() => Interlocked.Increment(ref Hidden)));
        Set(watcher, "RestoreLauncher", new Action(() => Interlocked.Increment(ref Restored)));
        Set(watcher, "RefreshShutdownButton", new Action(() => { }));
        Set(watcher, "CrashHint", new Action(() => Interlocked.Increment(ref CrashHints)));
        Set(watcher, "FeedbackInfo", new Action(() => { }));
        Set(watcher, "EnumWindows", new Action<Action<IntPtr, int>, int>((_, _) => { })); // No fabricated HWND or fake Running state.
        foreach (string name in new[] { "GetClassName", "GetWindowText", "SetWindowText", "ShowWindow", "IsWindowVisible", "GetWindowProcessId", "EndProgram", "MusicPause", "MusicResume" }) Guard(watcher, name);
        foreach (string name in new[] { "EndProgram", "MinimizeLauncher", "MusicPause", "MusicResume" }) Guard(launch, name);
        Action<Action> dispatch = launch.Dispatch; Set(launch, "Dispatch", new Action<Action>(work => Schedule(action => dispatch(action), work)));
        Action<Action> ui = watcher.RunInUi; Set(watcher, "RunInUi", new Action<Action>(work => Schedule(action => ui(action), work)));
        Action<Action, string> background = watcher.RunInNewThread;
        Set(watcher, "RunInNewThread", new Action<Action, string>((work, name) => Schedule(action => background(action, name), work, true)));
        Action<Action, string> launchBackground = launch.NewThread;
        Set(launch, "NewThread", new Action<Action, string>((work, name) => Schedule(action => launchBackground(action, name), work, true)));
        var stopped = watcher.ProcessStopped;
        Set(watcher, "ProcessStopped", new Action<ModMinecraft.McInstance, Process, bool>((owner, process, normal) =>
        {
            Native.Check(owned.ContainsKey(process), "Watcher tried to stop a foreign process."); stopped(owner, process, normal); Interlocked.Increment(ref ProcessStopped);
        }));
        var healthy = watcher.MarkHealthy;
        Set(watcher, "MarkHealthy", new Action<string, string?, bool>((version, point, normal) => { healthy(version, point, normal); LastHealthy = normal; Interlocked.Increment(ref HealthyCalls); }));
        var personal = launch.FinishPersonal;
        Set(launch, "FinishPersonal", new Action<PersonalOptionsSessionData>(session => { personal(session); Interlocked.Increment(ref PersonalFinished); }));
        var quick = launch.FinishQuickPlay;
        Set(launch, "FinishQuickPlay", new Action<QuickPlaySessionData>(session => { quick(session); Interlocked.Increment(ref QuickFinished); }));
        var start = launch.StartProcess;
        Set(launch, "StartProcess", new Func<ProcessStartInfo, Process>(info =>
        {
            Native.Check(info.FileName == fakeJava && info.Arguments == command && info.WorkingDirectory == instance.PathIndie &&
                !info.UseShellExecute && info.RedirectStandardOutput && info.RedirectStandardError && !info.RedirectStandardInput && info.CreateNoWindow &&
                info.StandardOutputEncoding?.CodePage == 65001 && info.StandardErrorEncoding?.CodePage == 65001,
                "Actual Run ProcessStartInfo lost its executable/arguments/working-directory/encoding/redirect contract.");
            Native.Check(info.EnvironmentVariables["appdata"] == MeloongCore.PathUtils.ToShortPath(launch.SelectedFolder()) &&
                info.EnvironmentVariables["Path"]!.Split(';').Contains(MeloongCore.PathUtils.ToShortPath(ModLaunch.McLaunchJavaSelected.Folder)),
                "Actual Run did not set its appdata/Java PATH environment.");
            CheckOwned(info.WorkingDirectory); CheckOwned(options); CheckOwned(prepared.QuickPlay.LogFile!);
            // Only the process platform boundary changes: the original Main
            // StartProcess implementation still creates the real child with
            // the original argument string and remaining production settings.
            info.FileName = cmd; info.RedirectStandardInput = true;
            info.EnvironmentVariables["PCLLF_OPTIONS"] = options; info.EnvironmentVariables["PCLLF_QUICKLOG"] = prepared.QuickPlay.LogFile;
            Process process = start(info); owned.TryAdd(process, 0); Interlocked.Increment(ref ProcessStarts);
            Native.Check(process.Id != Environment.ProcessId && process.StartInfo.FileName == cmd, "Process seam did not start the explicit owned Windows cmd fixture.");
            DataReceivedEventHandler output = (_, e) => ObserveOutput(process, e, outputClosed);
            DataReceivedEventHandler error = (_, e) => ObserveOutput(process, e, errorClosed);
            handlers[process] = (output, error); process.OutputDataReceived += output; process.ErrorDataReceived += error;
            if (CancelAfterStart) { CaptureNativeThreads(); ((ModLoader.LoaderTask<int, Process>)Plan.Loaders[0]).Cancel(); } // Genuine cancellation, including the actual thread identity contract.
            return process;
        }));
    }

    private void ObserveOutput(Process process, DataReceivedEventArgs data, ConcurrentDictionary<Process, int> closed)
    {
        Interlocked.Increment(ref activeHandlers); Interlocked.Increment(ref generation);
        try { if (data.Data is null) closed[process] = 1; }
        finally { Interlocked.Decrement(ref activeHandlers); }
    }

    private void Schedule(Action<Action> schedule, Action work, bool background = false)
    {
        Interlocked.Increment(ref pending); Interlocked.Increment(ref generation);
        int entered = 0, released = 0;
        void Release() { if (Interlocked.Exchange(ref released, 1) == 0) { Interlocked.Decrement(ref pending); Interlocked.Increment(ref generation); } }
        try
        {
            schedule(() =>
            {
                Interlocked.Exchange(ref entered, 1);
                if (background && !ReferenceEquals(Thread.CurrentThread, uiThread)) threads.TryAdd(Thread.CurrentThread, 0);
                try { work(); }
                catch (Exception error) { asyncErrors.Enqueue(error); throw; }
                finally { Release(); }
            });
        }
        catch
        {
            schedulingFailed = true; // A scheduler that threw may still enter later; never authorize restoration.
            if (Volatile.Read(ref entered) == 0) Release();
            throw;
        }
    }

    private void InstallCrashBoundary()
    {
        object crashHost = Native.Field(typeof(CrashAnalyzer), "Host").GetValue(null)!;
        Set(crashHost, "RequestTaskTempFolder", new Func<string>(() => { string path = Path.Combine(root, "crash", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path + Path.DirectorySeparatorChar; }));
        Set(crashHost, "TempPath", new Func<string>(() => root + Path.DirectorySeparatorChar));
        Set(crashHost, "ShowWindowToTop", new Action(() => { })); Set(crashHost, "FeedbackInfo", new Action(() => { }));
        Set(crashHost, "MessageBox", new Func<string, string, string, string, string, Action?, int>((detail, title, _, _, _, _) =>
        {
            Native.Check(!string.IsNullOrWhiteSpace(detail) && title == "Minecraft 出现错误", "Actual crash output did not reach its controlled presentation boundary.");
            Interlocked.Increment(ref AnalysisDialogs); return 1;
        }));
        foreach (string name in new[] { "SaveReportPath", "StartProcess", "OpenExplorer", "OpenRestorePoint", "ExportedHint" }) Guard(crashHost, name);
        var create = watcher.CreateCrashAnalyzer;
        Set(watcher, "CreateCrashAnalyzer", new Func<ModMinecraft.McInstance, ILegacyCrashAnalyzer>(owner =>
        {
            ILegacyCrashAnalyzer actual = create(owner); Native.Check(actual.GetType() == typeof(CrashAnalyzer), "Crash factory did not return the real Main analyzer.");
            Interlocked.Increment(ref Analyzers); return new ObservedAnalyzer(this, actual);
        }));
    }

    // A transparent observer only: every analysis stage invokes its original
    // compiled Main object, with no substitute classification/report algorithm.
    private sealed class ObservedAnalyzer(LaunchProcessChecksScope scope, ILegacyCrashAnalyzer actual) : ILegacyCrashAnalyzer
    {
        private int stage;
        public void Collect(string version, IList<string>? log = null)
        {
            Native.Check(stage == 0 && version == scope.instance.PathIndie && log is not null && log.Contains(scope.OutputMarker) && log.Contains(scope.ErrorMarker),
                "Actual crash analyzer lost the owned Watcher's game folder or redirected log."); actual.Collect(version, log); stage = 1;
        }
        public bool Prepare() { Native.Check(stage == 1, "Crash analysis stage order changed."); bool result = actual.Prepare(); stage = 2; return result; }
        public void Analyze() { Native.Check(stage == 2, "Crash analysis stage order changed."); actual.Analyze(); stage = 3; }
        public void Output(bool manual, List<string>? extra = null)
        {
            Native.Check(stage == 3 && !manual, "Crash output stage order changed."); actual.Output(manual, extra); stage = 4; Interlocked.Increment(ref scope.AnalysisCompleted);
        }
    }

    internal void Wait(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            DrainUi(deadline);
            if (!asyncErrors.IsEmpty) throw new AggregateException("Actual launch asynchronous producer failed during " + message, asyncErrors);
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual launch contract did not finish " + message + ".");
            if (condition()) return;
            if (Plan is not null && Plan.State == ModBase.LoadState.Failed) throw new InvalidOperationException("Actual launch combo failed during " + message, Plan.Error);
            Thread.Sleep(2);
        }
    }
    private static void DrainUi(DateTime deadline) => Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
    private void StartDrain(Process process)
    {
        if (drainThreads.ContainsKey(process)) return;
        Thread thread = new(() => { try { process.WaitForExit(); } catch (Exception error) { asyncErrors.Enqueue(error); } })
        { Name = "Owned launch async-output completion", IsBackground = true };
        drainThreads[process] = thread; threads.TryAdd(thread, 0); Interlocked.Increment(ref generation); thread.Start();
    }
    private bool Quiet()
    {
        CaptureNativeThreads();
        if (schedulingFailed || Volatile.Read(ref firstStartReservation) != 0 || Volatile.Read(ref pending) != 0 || Volatile.Read(ref activeHandlers) != 0 || threads.Keys.Any(thread => thread.IsAlive)) return false;
        if (Plan is not null && (Plan.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting || Plan.Loaders.Any(task => task.State == ModBase.LoadState.Loading))) return false;
        foreach (Process process in owned.Keys)
        {
            if (!process.HasExited || !drainThreads.TryGetValue(process, out Thread? drain) || drain.IsAlive) return false;
            // Canceled-before-Watcher Run never began async reads. Its private
            // redirected handles close below, after the genuine process stops.
            if (ModLaunch.McLaunchWatcher is not null && (!outputClosed.ContainsKey(process) || !errorClosed.ContainsKey(process))) return false;
        }
        return true;
    }
    internal void WaitStopped()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        WaitStopped(deadline);
        if (!asyncErrors.IsEmpty) throw new AggregateException("Actual launch process/thread/output completion failed.", asyncErrors);
    }
    private void WaitStopped(DateTime deadline, bool terminateOwned = false)
    {
        HashSet<Process> killRequested = new();
        while (true)
        {
            foreach (Process process in owned.Keys)
            {
                if (terminateOwned && !process.HasExited && killRequested.Add(process))
                    try { process.Kill(entireProcessTree: true); } catch (Exception error) { asyncErrors.Enqueue(error); }
                if (process.HasExited) StartDrain(process);
            }
            DrainUi(deadline);
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual launch process/Watcher/analyzer/async-output/UI tails did not settle.");
            if (Quiet())
            {
                int observed = Volatile.Read(ref generation); DrainUi(deadline);
                if (observed == Volatile.Read(ref generation) && Quiet())
                {
                    DrainUi(deadline);
                    if (DateTime.UtcNow < deadline && observed == Volatile.Read(ref generation) && Quiet()) return;
                }
            }
            Thread.Sleep(2);
        }
    }
    internal void RecordPrimary(Exception error)
    {
        if (Interlocked.CompareExchange(ref primaryFailure, error, null) is not null) return;
        // Keep the original exception object/stack and print it before a failed
        // safety barrier can terminate this verified isolated child.
        try { Console.Error.WriteLine("FAIL native launch primary before bounded cleanup: " + error); }
        catch { } // A diagnostic sink cannot replace the actual failure.
    }
    private void ReportCleanup(Exception error)
    {
        void Part(string name, Func<string> describe)
        {
            try { Console.Error.WriteLine("Launch cleanup " + name + ": " + describe()); }
            catch (Exception diagnostic) { try { Console.Error.WriteLine("Launch cleanup diagnostic failed: " + name + " / " + diagnostic.GetType().Name); } catch { } }
        }
        Part("primary", () => primaryFailure?.ToString() ?? "No earlier exception was recorded.");
        Part("barrier", () => $"first-start={Volatile.Read(ref firstStartReservation)} pending={Volatile.Read(ref pending)} handlers={Volatile.Read(ref activeHandlers)} scheduler-failed={schedulingFailed} generation={Volatile.Read(ref generation)}");
        Part("plan", () => Plan is null ? "not constructed" : Plan.State + " / " + string.Join(", ", Plan.Loaders.Select(task => task.Name + "=" + task.State)));
        Part("store", () => $"launch-count writes={Volatile.Read(ref launchCountWrites)} reads={Volatile.Read(ref launchCountReads)} exists={File.Exists(launchCountStore)}");
        foreach (Process process in owned.Keys) Part("owned process", () => $"pid={process.Id} exited={process.HasExited} stdout-eof={outputClosed.ContainsKey(process)} stderr-eof={errorClosed.ContainsKey(process)}");
        foreach (Thread thread in threads.Keys.Take(32)) Part("real thread", () => $"id={thread.ManagedThreadId} name={thread.Name} alive={thread.IsAlive} state={thread.ThreadState}");
        Part("failure", error.ToString);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            if (primaryFailure is not null) ReportCleanup(primaryFailure); // Before the nested FileTaskScope may fail closed itself.
            // Do not erase an active task's LastRunningThread before its
            // delegate enters. Original Run/Watcher stops on the owned child's
            // actual exit; keep startup/scheduler reservations in force.
            CaptureNativeThreads();
            if (Plan is not null && Plan.State == ModBase.LoadState.Waiting && Volatile.Read(ref startRequested) == 0) Plan.Cancel();
            WaitStopped(deadline, terminateOwned: true); // Producer completion, not terminal state alone, authorizes restoration.
            tasks.FinishActualTasks();
            if (!asyncErrors.IsEmpty) throw new AggregateException("Actual launch/cleanup producer reported a failure.", asyncErrors);
            Native.Check(Volatile.Read(ref forbidden) == 0, "An unselected launch/account/network/hardware/report boundary was attempted, even if production caught its failure.");
            environment.AssertNoForbiddenIo();
            List<Exception> closeErrors = new();
            foreach (Process process in owned.Keys)
            {
                if (handlers.TryGetValue(process, out var pair)) { process.OutputDataReceived -= pair.Output; process.ErrorDataReceived -= pair.Error; }
                // Watcher owns BeginOutputReadLine/BeginErrorReadLine. Their
                // EOF callbacks and actual WaitForExit drain settled above;
                // synchronous getters would now select an incompatible mode.
                // Process.Dispose closes its async or never-read pipe readers.
                foreach (Action close in new Action[] { () => process.StandardInput.Close(), process.Dispose })
                    try { close(); } catch (Exception error) { closeErrors.Add(error); }
            }
            if (closeErrors.Count != 0) throw new AggregateException("Owned launch process/pipe handles did not all close.", closeErrors);
            while (restore.TryPop(out Action? action)) action();
            tasks.Dispose();
        }
        catch (Exception error)
        {
            // FileTaskScope constructed only after verifying the actual isolated
            // facade child and GUID output. Keep every guard/root installed if
            // a real producer cannot be proved stopped; never unwind App/GL.
            ReportCleanup(error);
            Native.Call(typeof(FileTaskScope), "FailClosed", tasks, "Launch process/Watcher/output cleanup failed before context restoration; original primary and per-item diagnostics are recorded above.", error);
            throw;
        }
    }
}
