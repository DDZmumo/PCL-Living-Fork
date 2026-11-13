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
    public void StateChanged() {
        bool running = false, shutdown = true;
        foreach (TWatcher watcher in host.ReadWatchers()) {
            if (Is(watcher.CoreState, host.Loading) || Is(watcher.CoreState, host.Running) ||
                (Is(watcher.CoreState, host.Crashed) && !watcher.CoreGameProcess.HasExited)) { running = true; break; }
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
    private bool requireVerifiedExit, deferredCancellation;
    private string? environmentPoint;
    private bool reachedRunning, isWindowAppeared, isWindowFinished;
    private IntPtr windowHandle;
    private readonly object waitingLogLock = new();
    private TState state;
    public LegacyWatcher(TWatcher owner, LegacyWatcherHost<TWatcher, TInstance, TLoader, TState> host,
        LegacyWatcherGroup<TWatcher, TInstance, TLoader, TState> group) { this.owner = owner; this.host = host; this.group = group; state = host.Loading; }
    private bool Is(TState value, TState expected) => group.Is(value, expected);
    private bool AllProcessesAndPipesFinished() =>
        owner.CoreGameProcess.HasExited && (hasGroupExited?.Invoke() ?? true) && (arePipesDrained?.Invoke() ?? true);

    public TState State {
        get => state;
        set {
            if (requireVerifiedExit && (Is(value, host.Ended) || Is(value, host.Canceled)) &&
                owner.CoreGameProcess is Process && !AllProcessesAndPipesFinished()) {
                if (Is(value, host.Canceled)) deferredCancellation = true;
                return;
            }
            if (Is(state, value)) return; state = value; if (Is(value, host.Running)) reachedRunning = true; group.StateChanged();
        }
    }
    public void Initialize(TLoader Loader, TInstance Instance, string? WindowTitle, Action<Process>? BeforeMonitoring = null, Action? OnStopped = null, string? EnvironmentPoint = null, Func<string, string>? LogRedactor = null, int? WindowTypeOverride = null, Action? OnMonitoringFinished = null, bool RequireVerifiedExit = false, Action<Action<string?>, Action<string?>>? AttachPipeOutput = null, Func<bool>? ArePipesDrained = null, Action? CustomTerminate = null, Func<bool>? HasGroupExited = null) {
        logRedactor = LogRedactor;
        windowTypeOverride = WindowTypeOverride;
        monitoringFinished = OnMonitoringFinished;
        attachPipeOutput = AttachPipeOutput;
        arePipesDrained = ArePipesDrained;
        customTerminate = CustomTerminate;
        hasGroupExited = HasGroupExited;
        requireVerifiedExit = RequireVerifiedExit;
        owner.CoreLoader = Loader; owner.CoreInstance = Instance; windowTitle = WindowTitle; pid = host.LoaderInput(Loader).Id;
        afterStopped = OnStopped; environmentPoint = EnvironmentPoint;
        WatcherLog("开始 Minecraft 日志监控");
        if (!string.IsNullOrEmpty(windowTitle)) WatcherLog("要求窗口标题：" + WindowTitle);
        List<TWatcher> watchers = new();
        foreach (TWatcher watcher in host.ReadWatchers()) {
            if (Is(watcher.CoreState, host.Crashed) || Is(watcher.CoreState, host.Ended) || Is(watcher.CoreState, host.Canceled)) continue;
            watchers.Add(watcher);
        }
        watchers.Add(owner); host.WriteWatchers(watchers); group.StateChanged();
        owner.CoreGameProcess = host.LoaderInput(Loader);
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
                while (requireVerifiedExit ? !AllProcessesAndPipesFinished() || Volatile.Read(ref stopCallbackCompleted) == 0 :
                    !Is(State, host.Ended) && !Is(State, host.Canceled) &&
                    (!host.LoaderCanceled(owner.CoreLoader) || Is(State, host.Crashed)) &&
                    (!Is(State, host.Crashed) || !AllProcessesAndPipesFinished())) {
                    TimerWindow(); TimerLog();
                    for (int i = 1; i <= 3; i++) {
                        if (isWindowFinished && isWindowAppeared && !string.IsNullOrEmpty(windowTitle) && Is(State, host.Running) && !owner.CoreGameProcess.HasExited) {
                            string title = windowTitle!.Replace("{date}", DateTime.Now.ToString("yyyy'/'M'/'d")).Replace("{time}", DateTime.Now.ToString("HH':'mm':'ss"));
                            host.SetWindowText(windowHandle, new string(title.ToCharArray()));
                        }
                        Thread.Sleep(64);
                    }
                }
                if (Is(State, host.Crashed)) group.StateChanged();
                WatcherLog("Minecraft 日志监控已退出");
            } catch (Exception error) {
                Logger.Error(error, "Minecraft 日志监控主循环出错");
                if (requireVerifiedExit) {
                    State = host.Crashed;
                    try { owner.CoreGameProcess.WaitForExit(); CompleteStopCallbacks(); }
                    catch (Exception exitError) { Logger.Warn(exitError, "MMC 日志监控退出仍待进程确认"); }
                } else State = host.Ended;
            } finally {
                if (!requireVerifiedExit || Volatile.Read(ref stopCallbackCompleted) != 0) monitoringFinished?.Invoke();
            }
        }, "Minecraft Watcher PID " + pid);
    }
    private void LogReceived(object sender, DataReceivedEventArgs e) => LogLine(e.Data);
    private void LogLine(string? text) {
        if (text is null) return;
        if (logRedactor is not null) text = logRedactor(text);
        lock (waitingLogLock) owner.CoreWaitingLog.Add(text);
    }
    private bool IsNormalExit() => reachedRunning && !deferredCancellation && owner.CoreGameProcess.ExitCode == 0 && !Is(State, host.Crashed) && !Is(State, host.Canceled);
    private void CompleteStopCallbacks() {
        if (!AllProcessesAndPipesFinished()) return;
        if (Interlocked.Exchange(ref stopCallbackCompleted, 1) != 0) return;
        try { host.ProcessStopped(owner.CoreInstance, owner.CoreGameProcess, IsNormalExit()); }
        catch (Exception error) { Logger.Warn(error, "游戏退出后的存档自动备份失败"); }
        try { host.MarkHealthy(host.InstancePath(owner.CoreInstance), environmentPoint, IsNormalExit()); }
        catch (Exception error) { Logger.Warn(error, "标记正常运行的环境还原点失败"); }
        try { afterStopped?.Invoke(); }
        catch (Exception error) { Logger.Warn(error, "处理游戏退出后的本机资料失败"); }
    }
    private void TimerLog() {
        try {
            bool readyToSettle = AllProcessesAndPipesFinished();
            List<string> copied = new();
            lock (waitingLogLock) {
                if (!owner.CoreWaitingLog.Any() && (!requireVerifiedExit || !readyToSettle)) return;
                copied = owner.CoreWaitingLog; owner.CoreWaitingLog = new List<string>(1000);
            }
            foreach (string text in copied) GameLog(text);
            if (Is(State, host.Loading)) ProgressUpdate();
            if (readyToSettle) {
                CompleteStopCallbacks();
                WatcherLog("Minecraft 已退出，返回值：" + owner.CoreGameProcess.ExitCode);
                if (deferredCancellation) State = host.Canceled;
                else if (Is(State, host.Loading)) { WatcherLog("Minecraft 尚未加载完成，可能已崩溃", LogLevel.Error); Crashed(); }
                else if (owner.CoreGameProcess.ExitCode != 0 && Is(State, host.Running) && host.ReleaseTime(owner.CoreInstance).Year >= 2012) { WatcherLog("Minecraft 返回值异常，可能已崩溃", LogLevel.Error); Crashed(); }
                else if (!Is(State, host.Crashed)) State = host.Ended;
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
        if (!Text.Contains("[CHAT]")) {
            if (Text.Contains("Someone is closing me!") || Text.Contains("Restarting Minecraft with command")) { WatcherLog("识别为关闭的 Log：" + Text); State = host.Ended; }
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
            if (owner.CoreGameProcess.HasExited || isWindowFinished) return;
            KeyValuePair<IntPtr, string>? window = null;
            try { window = TryGetMinecraftWindow(); }
            catch (Win32Exception error) { Logger.Error(error, "由于反作弊或安全软件拦截，PCL 无法操作游戏窗口", LogBehavior.Toast); isWindowFinished = true; }
            if (window is null) return;
            string name = window.Value.Value; IntPtr handle = window.Value.Key;
            if (!name.StartsWithF("FML") && !name.StartsWithF("Quilt Loader")) {
                windowHandle = handle; WatcherLog($"Minecraft 窗口已加载：{name}（{handle.ToInt64()}）"); isWindowFinished = true;
                if ((windowTypeOverride ?? host.WindowType()) == 4) host.RunInNewThread(() => {
                    try { Thread.Sleep(2000); host.ShowWindow(windowHandle, 3); WatcherLog($"已最大化 Minecraft 窗口：{handle.ToInt64()}"); }
                    catch (Exception error) { Logger.Warn(error, "最大化 Minecraft 窗口时出现错误"); }
                }, "MinecraftWindowMaximize");
            } else if (!isWindowAppeared) WatcherLog("Mod Loader 窗口已加载：" + name + "（" + handle.ToInt64() + "）");
            isWindowAppeared = true;
        } catch (Exception error) { Logger.Error(error, "检查 Minecraft 窗口失败"); }
    }
    private KeyValuePair<IntPtr, string>? TryGetMinecraftWindow() {
        KeyValuePair<IntPtr, string>? result = null;
        host.EnumWindows((handle, _) => {
            if (result is not null) return;
            StringBuilder text = new(512); host.GetClassName(handle, text, text.Capacity); string name = text.ToString();
            if (!(name == "GLFW30" || name == "SDL_app" || name == "LWJGL" || name == "SunAwtFrame")) return;
            if (name == "SDL_app" && !host.IsWindowVisible(handle)) return;
            text = new StringBuilder(512); host.GetWindowText(handle, text, text.Capacity); string title = text.ToString();
            if (string.IsNullOrWhiteSpace(title) || title == "SDL_app" || title == "PopupMessageWindow" || title.StartsWithF("GLFW") || title.EndsWithF("Hidden Utility Window")) return;
            int processId = host.GetWindowProcessId(handle);
            try { if (Process.GetProcessById(processId).StartTime < owner.CoreGameProcess.StartTime) return; }
            catch (Exception error) { Logger.Warn(error, "枚举 Minecraft 窗口进程失败"); return; }
            result = new KeyValuePair<IntPtr, string>(handle, title);
        }, 0);
        return result;
    }
    private void Crashed() {
        if (Is(State, host.Crashed) || Is(State, host.Ended)) return;
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
        State = host.Canceled; WatcherLog("尝试强制结束 Minecraft 进程");
        try {
            if (customTerminate is not null) customTerminate();
            else if (!owner.CoreGameProcess.HasExited) owner.CoreGameProcess.Kill();
            WatcherLog("已强制结束 Minecraft 进程");
        } catch (Exception error) { Logger.Error(error, "强制结束 Minecraft 进程失败", LogBehavior.Toast); }
    }
}
