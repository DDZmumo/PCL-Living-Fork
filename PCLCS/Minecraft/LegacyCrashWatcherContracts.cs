using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using MeloongCore;

namespace PCLCS;

// Behavior: LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 ModWatcher.vb/ModCrash.vb.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 is the upstream source. LF's
// exactly-once stopped callbacks, healthy-run checks, recent changes and links stay.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModWatcher.cs and CrashAnalysis/
// (Logs/CrashLogPreparer.cs, Presentation/CrashResultFormatter.cs, CrashFileIo.cs)
// were reviewed for C# organization only (CE Plain Craft Launcher 2/LICENCE; LF LICENCE). CE's
// revised classification, detection and presentation policies are not imported.
// Repository LF LICENCE applies; mechanical initial syntax conversion used
// ICSharpCode.CodeConverter 10.0.1.923, followed by explicit typed LF adaptation.
public interface ILegacyCrashAnalyzer {
    void Collect(string VersionPathIndie, IList<string>? LatestLog = null);
    bool Prepare();
    void Analyze();
    void Output(bool IsManualAnalyze, List<string>? ExtraFiles = null);
}
public sealed class LegacyCrashHost<TInstance, TChanges> where TInstance : class where TChanges : class {
    public Func<string> RequestTaskTempFolder = null!, TempPath = null!, LauncherIssuesUrl = null!;
    public Func<int> NewUuid = null!;
    public Action ShowWindowToTop = null!, FeedbackInfo = null!;
    public Func<TInstance, TChanges> BuildRecentChanges = null!;
    public Func<string, TChanges, string> AppendRecentAnalysis = null!;
    public Func<TChanges, bool> HasBaseline = null!;
    public Func<TChanges, int, string> ChangesText = null!;
    public Action<TInstance, TChanges> OpenRestorePoint = null!;
    public Func<string, string, string, string, string, Action?, int> MessageBox = null!;
    public Func<string, string, string?> SaveReportPath = null!;
    public Action<Action> RunInUi = null!, RunInUiWait = null!;
    public Action<string> StartProcess = null!, OpenExplorer = null!, ExportedHint = null!;
    public Func<string, char, string> FilterUserName = null!, FilterAccessToken = null!;
}
public interface ILegacyWatcherData<TInstance, TLoader, TState> {
    Process CoreGameProcess { get; set; }
    TInstance CoreInstance { get; set; }
    TLoader CoreLoader { get; set; }
    TState CoreState { get; set; }
    List<string> CoreWaitingLog { get; set; }
    ConcurrentQueue<string> CoreLatestLog { get; set; }
}
public sealed class LegacyWatcherHost<TWatcher, TInstance, TLoader, TState>
    where TWatcher : ILegacyWatcherData<TInstance, TLoader, TState> {
    public TState Loading = default!, Running = default!, Crashed = default!, Ended = default!, Canceled = default!;
    public Func<List<TWatcher>> ReadWatchers = null!;
    public Action<List<TWatcher>> WriteWatchers = null!;
    public Action<bool> WriteHasRunningMinecraft = null!;
    public Action<string, LogLevel> LaunchLog = null!;
    public Action<Action> RunInUi = null!;
    public Action<Action, string> RunInNewThread = null!;
    public Action UpdateRecentWorldVisibility = null!, RefreshRecentWorlds = null!, RefreshShutdownButton = null!, EndProgram = null!, RestoreLauncher = null!;
    public Func<bool> MusicStop = null!, MusicStart = null!, MusicResume = null!, MusicPause = null!;
    public Func<int> LauncherVisibility = null!, WindowType = null!;
    public Func<TLoader, Process> LoaderInput = null!;
    public Func<TLoader, bool> LoaderCanceled = null!;
    public Action<TLoader, double> LoaderProgress = null!;
    public Action<TInstance, Process, bool> ProcessStopped = null!;
    public Action? VerifiedGameExit;
    public Action<string, string?, bool> MarkHealthy = null!;
    public Func<TInstance, DateTime> ReleaseTime = null!;
    public Func<TInstance, string> InstancePath = null!, IndiePath = null!, InstanceName = null!;
    public Func<string> BasePath = null!;
    public Action CrashHint = null!, FeedbackInfo = null!;
    public Func<TInstance, ILegacyCrashAnalyzer> CreateCrashAnalyzer = null!;
    public Action<Action<IntPtr, int>, int> EnumWindows = null!;
    public Func<IntPtr, StringBuilder, int, int> GetClassName = null!, GetWindowText = null!;
    public Action<IntPtr, string> SetWindowText = null!;
    public Action<IntPtr, uint> ShowWindow = null!;
    public Func<IntPtr, bool> IsWindowVisible = null!;
    public Func<IntPtr, int> GetWindowProcessId = null!;
}
