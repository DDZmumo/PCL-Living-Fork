using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed class LegacyWatcherGroup<TWatcher, TInstance, TLoader, TState>
    where TWatcher : ILegacyWatcherData<TInstance, TLoader, TState> {
    private readonly LegacyWatcherHost<TWatcher, TInstance, TLoader, TState> host;
    private bool isWatcherRunning;
    public LegacyWatcherGroup(LegacyWatcherHost<TWatcher, TInstance, TLoader, TState> host) => this.host = host;
    internal bool Is(TState state, TState expected) => EqualityComparer<TState>.Default.Equals(state, expected);
    internal bool ProcessAlive(TWatcher watcher) {
        if (watcher.CoreGameProcess is null) return Is(watcher.CoreState, host.Loading) || Is(watcher.CoreState, host.Running);
        // MMC can retain Loading/Running until the owned group and pipes drain;
        // ordinary stop callbacks also finish before publishing the terminal state.
        try { return !watcher.CoreGameProcess.HasExited || Is(watcher.CoreState, host.Loading) || Is(watcher.CoreState, host.Running); }
        catch { return true; } // An uncertain process handle never authorizes session cleanup.
    }
    public void StateChanged() {
        bool running = false, shutdown = true;
        foreach (TWatcher watcher in host.ReadWatchers()) {
            if (ProcessAlive(watcher)) { running = true; break; }
            else if (Is(watcher.CoreState, host.Crashed) || Is(watcher.CoreState, host.Canceled)) shutdown = false;
        }
        if (isWatcherRunning == running) return;
        isWatcherRunning = running;
        if (running) {
            host.LaunchLog("[全局] 出现运行中的 Minecraft", LogLevel.Info);
            host.WriteHasRunningMinecraft(true); host.RunInUi(host.UpdateRecentWorldVisibility); host.RefreshShutdownButton();
        } else {
            host.LaunchLog("[全局] 已无运行中的 Minecraft", LogLevel.Info);
            host.WriteHasRunningMinecraft(false); host.RunInUi(host.RefreshRecentWorlds); host.RefreshShutdownButton();
            if (host.MusicStop()) host.RunInUi(() => { if (host.MusicResume()) Logger.Info("已根据设置，在结束后开始音乐播放"); });
            else if (host.MusicStart()) host.RunInUi(() => { if (host.MusicPause()) Logger.Info("已根据设置，在结束后暂停音乐播放"); });
            switch (host.LauncherVisibility()) {
                case 2: host.RunInUi(shutdown ? host.EndProgram : host.RestoreLauncher); break;
                case 3: host.RunInUi(host.RestoreLauncher); break;
            }
        }
    }
}

public sealed class LegacyWatcher<TWatcher, TInstance, TLoader, TState>
    where TWatcher : ILegacyWatcherData<TInstance, TLoader, TState> {
    private readonly TWatcher owner;
    private readonly LegacyWatcherHost<TWatcher, TInstance, TLoader, TState> host;
    private readonly LegacyWatcherGroup<TWatcher, TInstance, TLoader, TState> group;
    private string? windowTitle = "";
    private int pid, stopCallbackCompleted, logProgress;
    private Action? afterStopped;
    private Func<string, string>? logRedactor;
    private int? windowTypeOverride;
    private Action? monitoringFinished;
    private Action<Action<string?>, Action<string?>>? attachPipeOutput;
    private Func<bool>? arePipesDrained;
    private Action? customTerminate;
    private Func<bool>? hasGroupExited;
    private bool requireVerifiedExit;
    private Process entryProcess = null!;
    private LegacyJavaProcessGroup? legacyJavaGroup;
    private bool entryReachedGame, restartRequested, waitingForRelaunch;
    private int deferredCancellation;
    private string? environmentPoint;
    private bool reachedRunning, isWindowAppeared, isWindowFinished, reportedLoaderWindow;
    private const int FastWindowProbeMs = 48;
    private const int BackoffWindowProbeMs = 96;
    private const int FastWindowProbeDurationMs = 12000;
    private IntPtr windowHandle;
    private readonly object waitingLogLock = new();
    private readonly object stateGate = new();
    private List<string> spareLogBuffer = new();
    private TState state;
    public LegacyWatcher(TWatcher owner, LegacyWatcherHost<TWatcher, TInstance, TLoader, TState> host,
        LegacyWatcherGroup<TWatcher, TInstance, TLoader, TState> group) { this.owner = owner; this.host = host; this.group = group; state = host.Loading; }
    private bool Is(TState value, TState expected) => group.Is(value, expected);
    private bool LegacyRelaunchPending() =>
        legacyJavaGroup is not null && ReferenceEquals(entryProcess, owner.CoreGameProcess) &&
        entryProcess.HasExited && entryProcess.ExitCode == 0 && (!entryReachedGame || restartRequested) &&
        legacyJavaGroup.HasActiveJava();

    private bool AllProcessesAndPipesFinished() {
        if (!entryProcess.HasExited || !owner.CoreGameProcess.HasExited ||
            !(hasGroupExited?.Invoke() ?? true) || !(arePipesDrained?.Invoke() ?? true)) return false;
        return Volatile.Read(ref deferredCancellation) != 0
            ? legacyJavaGroup?.HasActiveJava() != true
            : !LegacyRelaunchPending();
    }

    public TState State {
        get { lock (stateGate) return state; }
        set {
            if ((requireVerifiedExit || legacyJavaGroup is not null) && (Is(value, host.Ended) || Is(value, host.Canceled)) &&
                owner.CoreGameProcess is Process && !AllProcessesAndPipesFinished()) {
                if (Is(value, host.Canceled)) Volatile.Write(ref deferredCancellation, 1);
                return;
            }
            lock (stateGate) {
                if (Is(state, value)) return;
                state = value;
                if (Is(value, host.Running)) reachedRunning = true;
                Monitor.PulseAll(stateGate);
            }
            group.StateChanged();
        }
    }

    public TState WaitForStartupState(int timeoutMilliseconds) {
        lock (stateGate) {
            if (Is(state, host.Loading)) Monitor.Wait(stateGate, timeoutMilliseconds);
            return state;
        }
    }

    private bool CanPublishStartup() => Is(State, host.Loading) && Volatile.Read(ref deferredCancellation) == 0 &&
        !host.LoaderCanceled(owner.CoreLoader) && !owner.CoreGameProcess.HasExited;

    public void Initialize(TLoader Loader, TInstance Instance, string? WindowTitle, Action<Process>? BeforeMonitoring = null, Action? OnStopped = null, string? EnvironmentPoint = null, Func<string, string>? LogRedactor = null, int? WindowTypeOverride = null, Action? OnMonitoringFinished = null, bool RequireVerifiedExit = false, Action<Action<string?>, Action<string?>>? AttachPipeOutput = null, Func<bool>? ArePipesDrained = null, Action? CustomTerminate = null, Func<bool>? HasGroupExited = null) {
        logRedactor = LogRedactor;
        windowTypeOverride = WindowTypeOverride;
        monitoringFinished = OnMonitoringFinished;
        attachPipeOutput = AttachPipeOutput;
        arePipesDrained = ArePipesDrained;
        customTerminate = CustomTerminate;
        hasGroupExited = HasGroupExited;
        requireVerifiedExit = RequireVerifiedExit;
        owner.CoreLoader = Loader; owner.CoreInstance = Instance; windowTitle = WindowTitle;
        entryProcess = owner.CoreGameProcess = host.LoaderInput(Loader);
        pid = entryProcess.Id;
        if (!RequireVerifiedExit) legacyJavaGroup = LegacyJavaProcessGroup.TryAttach(entryProcess);
        afterStopped = OnStopped; environmentPoint = EnvironmentPoint;
        WatcherLog("开始 Minecraft 日志监控");
        if (!string.IsNullOrEmpty(windowTitle)) WatcherLog("要求窗口标题：" + WindowTitle);
        List<TWatcher> watchers = new();
        foreach (TWatcher watcher in host.ReadWatchers()) {
            if (!group.ProcessAlive(watcher)) continue;
            watchers.Add(watcher);
        }
        watchers.Add(owner); host.WriteWatchers(watchers); group.StateChanged();
        BeforeMonitoring?.Invoke(owner.CoreGameProcess);
        if (attachPipeOutput is not null) {
            attachPipeOutput(LogLine, LogLine);
        } else {
            // LF starts asynchronous reads before subscribing; preserve that timing.
            if (logRedactor is not null) {
                owner.CoreGameProcess.OutputDataReceived += LogReceived; owner.CoreGameProcess.ErrorDataReceived += LogReceived;
                owner.CoreGameProcess.BeginOutputReadLine(); owner.CoreGameProcess.BeginErrorReadLine();
            } else {
                owner.CoreGameProcess.BeginOutputReadLine(); owner.CoreGameProcess.BeginErrorReadLine();
                owner.CoreGameProcess.OutputDataReceived += LogReceived; owner.CoreGameProcess.ErrorDataReceived += LogReceived;
            }
        }
        host.RunInNewThread(() => {
            try {
                // A closing log or kill request is not process completion. Keep
                // observing until the owned process group and output pipes have exited.
                var startup = Stopwatch.StartNew();
                while (true) {
                    // Consume cancellation and crash lines first, then publish a window
                    // from the current process instead of waiting for its next stdout line.
                    TimerLog();
                    if (AllProcessesAndPipesFinished() && Volatile.Read(ref stopCallbackCompleted) != 0) break;
                    TimerWindow();
                    if (!isWindowFinished && (Is(State, host.Loading) || Is(State, host.Running))) {
                        Thread.Sleep(startup.ElapsedMilliseconds < FastWindowProbeDurationMs ? FastWindowProbeMs : BackoffWindowProbeMs);
                        continue;
                    }
                    for (int i = 1; i <= 3; i++) {
                        if (isWindowFinished && isWindowAppeared && !string.IsNullOrEmpty(windowTitle) && Is(State, host.Running) &&
                            !owner.CoreGameProcess.HasExited && host.GetWindowProcessId(windowHandle) == owner.CoreGameProcess.Id) {
                            string title = windowTitle!.Replace("{date}", DateTime.Now.ToString("yyyy'/'M'/'d")).Replace("{time}", DateTime.Now.ToString("HH':'mm':'ss"));
                            host.SetWindowText(windowHandle, new string(title.ToCharArray()));
                        }
                        Thread.Sleep(64);
                    }
                }
                group.StateChanged();
                WatcherLog("Minecraft 日志监控已退出");
            } catch (Exception error) {
                Logger.Error(error, "Minecraft 日志监控主循环出错");
                if (requireVerifiedExit) {
                    State = host.Crashed;
                    try { owner.CoreGameProcess.WaitForExit(); CompleteStopCallbacks(); }
                    catch (Exception exitError) { Logger.Warn(exitError, "MMC 日志监控退出仍待进程确认"); }
                } else State = host.Ended;
            } finally {
                legacyJavaGroup?.Dispose();
                monitoringFinished?.Invoke();
            }
        }, "Minecraft Watcher PID " + pid);
    }
    private void LogReceived(object sender, DataReceivedEventArgs e) => LogLine(e.Data);
    private void LogLine(string? text) {
        if (text is null) return;
        if (logRedactor is not null) text = logRedactor(text);
        lock (waitingLogLock) owner.CoreWaitingLog.Add(text);
    }
    private bool IsNormalExit() => reachedRunning && Volatile.Read(ref deferredCancellation) == 0 && owner.CoreGameProcess.ExitCode == 0 && !Is(State, host.Crashed) && !Is(State, host.Canceled);
    private bool CompleteStopCallbacks() {
        if (!AllProcessesAndPipesFinished()) return false;
        if (Interlocked.Exchange(ref stopCallbackCompleted, 1) != 0) return false;
        // Freeze the outcome before backup callbacks or concurrent kill UI, and finish
        // callbacks before changing the terminal state can close the launcher.
        bool normal = IsNormalExit();
        WatcherLog("Minecraft 已退出，返回值：" + owner.CoreGameProcess.ExitCode);
        // Backup and settings sessions were recorded against the original launch.
        // Use the successor's exit result, but close the original session record.
        try { host.ProcessStopped(owner.CoreInstance, entryProcess, normal); }
        catch (Exception error) { Logger.Warn(error, "游戏退出后的存档自动备份失败"); }
        try { host.MarkHealthy(host.InstancePath(owner.CoreInstance), environmentPoint, normal); }
        catch (Exception error) { Logger.Warn(error, "标记正常运行的环境还原点失败"); }
        try { afterStopped?.Invoke(); }
        catch (Exception error) { Logger.Warn(error, "处理游戏退出后的本机资料失败"); }
        return true;
    }
    private void TimerLog() {
        try {
            if ((requireVerifiedExit || legacyJavaGroup is not null) && Is(State, host.Loading) && host.LoaderCanceled(owner.CoreLoader))
                Volatile.Write(ref deferredCancellation, 1);
            if (legacyJavaGroup is not null && Volatile.Read(ref deferredCancellation) != 0 && legacyJavaGroup.HasActiveJava())
                legacyJavaGroup.TerminateJava();
            bool readyToSettle = AllProcessesAndPipesFinished();
            List<string>? copied = null;
            lock (waitingLogLock) {
                if (owner.CoreWaitingLog.Count != 0) {
                    copied = owner.CoreWaitingLog;
                    owner.CoreWaitingLog = spareLogBuffer;
                    spareLogBuffer = copied;
                }
            }
            if (copied is not null) {
                try {
                    foreach (string text in copied) GameLog(text);
                    if (CanPublishStartup()) ProgressUpdate();
                }
                finally { copied.Clear(); }
            }
            if (readyToSettle && CompleteStopCallbacks()) {
                try {
                    if (Volatile.Read(ref deferredCancellation) != 0) State = host.Canceled;
                    else if (Is(State, host.Loading)) { WatcherLog("Minecraft 尚未加载完成，可能已崩溃", LogLevel.Error); Crashed(); }
                    else if (owner.CoreGameProcess.ExitCode != 0 && Is(State, host.Running) && host.ReleaseTime(owner.CoreInstance).Year >= 2012) { WatcherLog("Minecraft 返回值异常，可能已崩溃", LogLevel.Error); Crashed(); }
                    else if (!Is(State, host.Crashed) && !Is(State, host.Canceled)) State = host.Ended;
                } catch (Exception error) { Logger.Warn(error, "判断游戏退出状态失败，继续完成退出收尾"); }
                try { group.StateChanged(); }
                catch (Exception error) { Logger.Warn(error, "刷新游戏退出状态失败，继续完成退出收尾"); }
            }
        } catch (Exception error) { Logger.Error(error, "输出 Minecraft 日志失败"); }
    }
    private void GameLog(string? Text) {
        if (Text is null) return;
        Text = Text.ReplaceLineEndings("\r\n");
        owner.CoreLatestLog.Enqueue(Text);
        if (owner.CoreLatestLog.Count >= 501) owner.CoreLatestLog.TryDequeue(out _);
        Logger.Trace(Text);
        if (logProgress < 1) { WatcherLog("日志 1/5：已出现日志输出"); logProgress = 1; }
        if (logProgress < 2 && Text.Contains("Setting user:")) { WatcherLog("日志 2/5：游戏用户已设置"); logProgress = 2; }
        else if (logProgress < 3 && Text.ContainsIgnoreCase("lwjgl version")) { WatcherLog("日志 3/5：LWJGL 版本已确认"); logProgress = 3; }
        else if (logProgress < 4 && (Text.Contains("OpenAL initialized") || Text.Contains("Starting up SoundSystem"))) { WatcherLog("日志 4/5：OpenAL 已加载"); logProgress = 4; }
        else if (logProgress < 5 && ((Text.Contains("Created") && Text.Contains("textures") && Text.Contains("-atlas")) || Text.Contains("Found animation info"))) { WatcherLog("日志 5/5：材质已加载"); logProgress = 5; }
        if (logProgress == 5 && ReferenceEquals(entryProcess, owner.CoreGameProcess)) entryReachedGame = true;
        if (!Text.Contains("[CHAT]")) {
            if (legacyJavaGroup is not null && Text.Contains("Restarting Minecraft with command")) {
                restartRequested = true;
                WatcherLog("Minecraft 已请求重启");
            }
            else if (Text.Contains("Someone is closing me!") || Text.Contains("Restarting Minecraft with command")) { WatcherLog("识别为关闭的 Log：" + Text); State = host.Ended; }
            else if (Text.Contains("Crash report saved to") || Text.Contains("This crash report has been saved to:") || Text.Contains("Could not save crash report to") || Text.Contains("/ERROR]: Unable to launch") || Text.Contains("An exception was thrown, the game will display an error screen and halt.")) { WatcherLog("识别为崩溃的 Log：" + Text, LogLevel.Error); Crashed(); }
        }
    }
    private void WatcherLog(string text, LogLevel level = LogLevel.Info) => host.LaunchLog("[" + pid + "] " + text, level);
    private void ProgressUpdate() {
        double progress;
        if (isWindowAppeared || logProgress == 5) { progress = 0.95; WatcherLog("Minecraft 加载已完成"); State = host.Running; }
        else progress = Math.Min(logProgress, 3) / 3d * 0.9;
        host.LoaderProgress(owner.CoreLoader, progress);
    }
    private void TimerWindow() {
        try {
            if (owner.CoreGameProcess.HasExited) {
                if (!LegacyRelaunchPending()) return;
                if (!waitingForRelaunch) {
                    waitingForRelaunch = true;
                    isWindowAppeared = isWindowFinished = reachedRunning = false;
                    if (Is(State, host.Running)) State = host.Loading;
                    WatcherLog("启动入口已正常退出，正在等待同次启动的 Java 游戏窗口");
                }
            } else if (isWindowFinished) return;
            (IntPtr Handle, string Title, Process? Successor)? window = null;
            try { window = TryGetMinecraftWindow(); }
            catch (Win32Exception error) { Logger.Error(error, "由于反作弊或安全软件拦截，PCL 无法操作游戏窗口", LogBehavior.Toast); isWindowFinished = true; }
            if (window is null) return;
            var (handle, name, successor) = window.Value;
            if (successor is not null) {
                owner.CoreGameProcess = successor;
                WatcherLog($"已接续监控同一启动会话中的 Java 进程（PID {successor.Id}）");
            } else entryReachedGame = true;

            windowHandle = handle;
            WatcherLog($"Minecraft 窗口已加载：{name}（{handle.ToInt64()}）");
            isWindowFinished = isWindowAppeared = true;
            waitingForRelaunch = false;
            if (CanPublishStartup()) ProgressUpdate();

            Process windowProcess = owner.CoreGameProcess;
            if ((windowTypeOverride ?? host.WindowType()) == 4) host.RunInNewThread(() => {
                try {
                    Thread.Sleep(2000);
                    if (!windowProcess.HasExited && host.GetWindowProcessId(handle) == windowProcess.Id) {
                        host.ShowWindow(handle, 3);
                        WatcherLog($"已最大化 Minecraft 窗口：{handle.ToInt64()}");
                    }
                }
                catch (Exception error) { Logger.Warn(error, "最大化 Minecraft 窗口时出现错误"); }
            }, "MinecraftWindowMaximize");
        } catch (Exception error) { Logger.Error(error, "检查 Minecraft 窗口失败"); }
    }
    private (IntPtr Handle, string Title, Process? Successor)? TryGetMinecraftWindow() {
        (IntPtr Handle, string Title, Process? Successor)? result = null;
        Process current = owner.CoreGameProcess;
        bool mayFollow = legacyJavaGroup is not null && ReferenceEquals(entryProcess, current) && (!entryReachedGame || restartRequested);
        int currentPid = current.Id;
        var text = new StringBuilder(512);
        host.EnumWindows((handle, _) => {
            if (result is not null) return;
            int windowPid = host.GetWindowProcessId(handle);
            bool own = windowPid == currentPid;
            if ((!own && !mayFollow) || !host.IsWindowVisible(handle)) return;

            text.Clear();
            if (host.GetClassName(handle, text, text.Capacity) <= 0) return;
            string name = text.ToString();
            bool awt = name == "SunAwtFrame";
            if (name is not ("GLFW30" or "SDL_app" or "LWJGL" or "SunAwtFrame") || (awt && !own)) return;
            text.Clear();
            if (host.GetWindowText(handle, text, text.Capacity) <= 0) return;
            string title = text.ToString();
            if (string.IsNullOrWhiteSpace(title) || title == "SDL_app" || title == "PopupMessageWindow" ||
                title.StartsWithF("GLFW") || title.EndsWithF("Hidden Utility Window")) return;
            // A loader splash or a relay console is not a game window. Keep scanning past it.
            if (title.StartsWithF("FML") || title.StartsWithF("Quilt Loader")) {
                if (own && !reportedLoaderWindow) {
                    reportedLoaderWindow = true;
                    WatcherLog("Mod Loader 窗口已加载：" + title + "（" + handle.ToInt64() + "）");
                }
                return;
            }
            // Old Minecraft uses AWT; unknown Java tool windows keep the log-based readiness path.
            if (awt && (!title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase) ||
                title.ContainsIgnoreCase("console") || title.ContainsIgnoreCase("launcher"))) return;
            if (own) {
                if (!current.HasExited) result = (handle, title, null);
                return;
            }
            if (legacyJavaGroup?.OpenJava(windowPid) is { } successor) result = (handle, title, successor);
        }, 0);
        return result;
    }
    private void Crashed() {
        if (Is(State, host.Crashed) || Is(State, host.Ended) || Is(State, host.Canceled) || Volatile.Read(ref deferredCancellation) != 0) return;
        State = host.Crashed; WatcherLog("Minecraft 已崩溃，将在 2 秒后开始崩溃分析", LogLevel.Error); host.CrashHint(); host.FeedbackInfo();
        host.RunInNewThread(() => {
            try {
                Thread.Sleep(2000); WatcherLog("崩溃分析开始"); ILegacyCrashAnalyzer analyzer = host.CreateCrashAnalyzer(owner.CoreInstance);
                analyzer.Collect(host.IndiePath(owner.CoreInstance), owner.CoreLatestLog.ToList()); analyzer.Prepare(); analyzer.Analyze();
                analyzer.Output(false, new List<string> { host.InstancePath(owner.CoreInstance) + host.InstanceName(owner.CoreInstance) + ".json", host.BasePath() + @"PCL\Log1.txt", host.BasePath() + @"PCL\LatestLaunch.bat" });
            } catch (Exception error) { Logger.Error(error, "崩溃分析失败"); }
        }, "Crash Analyzer");
    }
    public void Kill() {
        if (legacyJavaGroup is not null) Volatile.Write(ref deferredCancellation, 1);
        State = host.Canceled; WatcherLog("尝试强制结束 Minecraft 进程");
        try {
            if (customTerminate is not null) customTerminate();
            else if (legacyJavaGroup?.TerminateJava() != true && !owner.CoreGameProcess.HasExited) owner.CoreGameProcess.Kill();
            WatcherLog("已强制结束 Minecraft 进程");
        } catch (Exception error) { Logger.Error(error, "强制结束 Minecraft 进程失败", LogBehavior.Toast); }
    }
}
