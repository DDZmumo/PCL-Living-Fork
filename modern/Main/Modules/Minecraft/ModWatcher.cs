using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MeloongCore;
using PCLCS;

namespace PCL
{
    public static class ModWatcher
    {
        public static List<Watcher> McWatcherList = new List<Watcher>();
        public static bool HasRunningMinecraft = false;
        private readonly static LegacyWatcherHost<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState> Host = CreateWatcherHost();
        private static LegacyWatcherHost<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState> CreateWatcherHost()
        {
            return new LegacyWatcherHost<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState>()
            {
                Loading = Watcher.MinecraftState.Loading,
                Running = Watcher.MinecraftState.Running,
                Crashed = Watcher.MinecraftState.Crashed,
                Ended = Watcher.MinecraftState.Ended,
                Canceled = Watcher.MinecraftState.Canceled,
                ReadWatchers = new Func<List<Watcher>>(() => McWatcherList),
                WriteWatchers = new Action<List<Watcher>>(Value => McWatcherList = Value),
                WriteHasRunningMinecraft = new Action<bool>(Value => HasRunningMinecraft = Value),
                LaunchLog = new Action<string, LogLevel>((Text, Level) => PCL.ModLaunch.McLaunchLog(Text, Level)),
                RunInUi = new Action<Action>(Work => PCL.ModBase.RunInUi(Work)),
                RunInNewThread = new Action<Action, string>((Work, Name) => PCL.ModBase.RunInNewThread(Work, Name)),
                UpdateRecentWorldVisibility = new Action(() => PCL.ModMain.FrmLaunchRight?.UpdateRecentWorldVisibility()),
                RefreshRecentWorlds = new Action(() => PCL.ModMain.FrmLaunchRight?.RefreshRecentWorlds()),
                RefreshShutdownButton = new Action(() => PCL.ModMain.FrmMain.BtnExtraShutdown.ShowRefresh()),
                EndProgram = new Action(() => PCL.ModMain.FrmMain.EndProgram(false)),
                RestoreLauncher = new Action(() => PCL.ModMain.FrmMain.Hidden = false),
                MusicStop = new Func<bool>(() => PCL.Settings.Get<bool>("UiMusicStop")),
                MusicStart = new Func<bool>(() => PCL.Settings.Get<bool>("UiMusicStart")),
                MusicResume = new Func<bool>(() => PCL.ModMusic.MusicResume()),
                MusicPause = new Func<bool>(() => PCL.ModMusic.MusicPause()),
                LauncherVisibility = new Func<int>(() => PCL.Settings.Get<int>("LaunchArgumentVisible")),
                WindowType = new Func<int>(() => PCL.Settings.Get<int>("LaunchArgumentWindowType")),
                LoaderInput = new Func<PCL.ModLoader.LoaderTask<Process, int>, Process>(Loader => Loader.Input),
                LoaderCanceled = new Func<PCL.ModLoader.LoaderTask<Process, int>, bool>(Loader => Loader.State == PCL.ModBase.LoadState.Canceled),
                LoaderProgress = new Action<PCL.ModLoader.LoaderTask<Process, int>, double>((Loader, Value) => Loader.Progress = Value),
                ProcessStopped = new Action<PCL.ModMinecraft.McInstance, Process, bool>((Instance, GameProcess, NormalExit) => PCL.WorldBackups.OnProcessStopped(Instance, GameProcess, NormalExit)),
                MarkHealthy = new Action<string, string, bool>((VersionPath, Point, NormalExit) => PCL.EnvironmentHistory.MarkHealthy(VersionPath, Point, NormalExit)),
                ReleaseTime = new Func<PCL.ModMinecraft.McInstance, DateTime>(Instance => Instance.ReleaseTime),
                InstancePath = new Func<PCL.ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                IndiePath = new Func<PCL.ModMinecraft.McInstance, string>(Instance => Instance.PathIndie),
                InstanceName = new Func<PCL.ModMinecraft.McInstance, string>(Instance => Instance.Name),
                BasePath = new Func<string>(() => Paths.Base),
                CrashHint = new Action(() => PCL.ModMain.Hint("检测到 Minecraft 出现错误，错误分析已开始……", PCL.ModMain.HintType.Red)),
                FeedbackInfo = new Action(() => PCL.ModMain.FeedbackInfo()),
                CreateCrashAnalyzer = new Func<PCL.ModMinecraft.McInstance, ILegacyCrashAnalyzer>(Instance => new PCL.CrashAnalyzer(Instance)),
                EnumWindows = NativeEnumWindows,
                GetClassName = new Func<IntPtr, StringBuilder, int, int>((Handle, Buffer, Count) => GetClassName(Handle, Buffer, Count)),
                GetWindowText = new Func<IntPtr, StringBuilder, int, int>((Handle, Buffer, Count) => GetWindowText(Handle, Buffer, Count)),
                SetWindowText = new Action<IntPtr, string>((Handle, Text) => ModWatcher.SetWindowText(Handle.ToInt32(), ref Text)),
                ShowWindow = new Action<IntPtr, uint>((Handle, Command) => ShowWindow(Handle, Command)),
                IsWindowVisible = new Func<IntPtr, bool>(Handle => IsWindowVisible(Handle)),
                GetWindowProcessId = NativeWindowProcessId
            };
        }

        private readonly static LegacyWatcherGroup<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState> Group = new LegacyWatcherGroup<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState>(Host);
        private static void NativeEnumWindows(Action<IntPtr, int> Callback, int Parameter)
        {
            EnumWindows((Handle, _) =>
            {
                Callback(Handle, Parameter);
                return true;
            }, Parameter);
        }

        private static int NativeWindowProcessId(IntPtr Handle)
        {
            var ProcessId = default(int);
            GetWindowThreadProcessId(Handle, ref ProcessId);
            return ProcessId;
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsSub(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern bool EnumWindows(EnumWindowsSub hWnd, IntPtr lParam);
        [DllImport("user32", EntryPoint = "GetClassNameA", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder str, int maxCount);
        [DllImport("user32", EntryPoint = "GetWindowTextA", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder str, int maxCount);
#pragma warning disable CS0618 // Exact LF VBByRefStr ABI; .NET10 retains its Windows FEATURE_COMINTEROP marshaller.
        [DllImport("user32", EntryPoint = "SetWindowTextA", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern bool SetWindowText(int hWnd, [MarshalAs(UnmanagedType.VBByRefStr)] ref string str);
#pragma warning restore CS0618
        [DllImport("user32", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern bool ShowWindow(IntPtr hWnd, uint cmdWindow);
        [DllImport("user32", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = false)]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, ref int lpdwProcessId);
        public class Watcher : ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, Watcher.MinecraftState>
        {
            public Process GameProcess;
            public PCL.ModMinecraft.McInstance Instance;
            public PCL.ModLoader.LoaderTask<Process, int> Loader;
            public List<string> WaitingLog = new List<string>(1000);
            public ConcurrentQueue<string> LatestLog = new ConcurrentQueue<string>();
            private readonly LegacyWatcher<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState> Native;
            public Watcher(PCL.ModLoader.LoaderTask<Process, int> Loader, PCL.ModMinecraft.McInstance Instance, string WindowTitle, Action<Process> BeforeMonitoring = null, Action OnStopped = null, string EnvironmentPoint = null, Func<string, string> LogRedactor = null, int? WindowTypeOverride = null, Action OnMonitoringFinished = null, bool RequireVerifiedExit = false, Action<Action<string>, Action<string>> AttachPipeOutput = null, Func<bool> ArePipesDrained = null, Action CustomTerminate = null, Func<bool> HasGroupExited = null)
            {
                Native = new LegacyWatcher<Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>(this, Host, Group);
                Native.Initialize(Loader, Instance, WindowTitle, BeforeMonitoring, OnStopped, EnvironmentPoint, LogRedactor, WindowTypeOverride, OnMonitoringFinished, RequireVerifiedExit, AttachPipeOutput, ArePipesDrained, CustomTerminate, HasGroupExited);
            }

            public MinecraftState State
            {
                get
                {
                    return Native.State;
                }

                set
                {
                    Native.State = value;
                }
            }

            public enum MinecraftState
            {
                Loading,
                Running,
                Crashed,
                Ended,
                Canceled
            }

            internal MinecraftState WaitForStartupState(int milliseconds) => Native.WaitForStartupState(milliseconds);

            public void Kill()
            {
                Native.Kill();
            }

            private Process CoreGameProcess
            {
                get
                {
                    return GameProcess;
                }

                set
                {
                    GameProcess = value;
                }
            }

            Process ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreGameProcess { get => CoreGameProcess; set => CoreGameProcess = value; }

            private PCL.ModMinecraft.McInstance CoreInstance
            {
                get
                {
                    return Instance;
                }

                set
                {
                    Instance = value;
                }
            }

            PCL.ModMinecraft.McInstance ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreInstance { get => CoreInstance; set => CoreInstance = value; }

            private PCL.ModLoader.LoaderTask<Process, int> CoreLoader
            {
                get
                {
                    return Loader;
                }

                set
                {
                    Loader = value;
                }
            }

            PCL.ModLoader.LoaderTask<Process, int> ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreLoader { get => CoreLoader; set => CoreLoader = value; }

            private MinecraftState CoreState
            {
                get
                {
                    return State;
                }

                set
                {
                    State = value;
                }
            }

            MinecraftState ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreState { get => CoreState; set => CoreState = value; }

            private List<string> CoreWaitingLog
            {
                get
                {
                    return WaitingLog;
                }

                set
                {
                    WaitingLog = value;
                }
            }

            List<string> ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreWaitingLog { get => CoreWaitingLog; set => CoreWaitingLog = value; }

            private ConcurrentQueue<string> CoreLatestLog
            {
                get
                {
                    return LatestLog;
                }

                set
                {
                    LatestLog = value;
                }
            }

            ConcurrentQueue<string> ILegacyWatcherData<PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<Process, int>, MinecraftState>.CoreLatestLog { get => CoreLatestLog; set => CoreLatestLog = value; }
        }
    }
}
