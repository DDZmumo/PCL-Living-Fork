using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS.Loaders;

namespace PCL
{
    // LF 的公开类型、事件委托、字段和原 UI/调度接线保留；完整加载器算法由 C# 承载。
    public static class ModLoader
    {
        static ModLoader() { }
        internal static void EnsureApplicationServicesInitialized() { }
        private readonly static LegacyLoaderHost<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> NativeLoaderHost = new LegacyLoaderHost<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>()
        {
            Waiting = PCL.ModBase.LoadState.Waiting,
            Loading = PCL.ModBase.LoadState.Loading,
            Finished = PCL.ModBase.LoadState.Finished,
            Failed = PCL.ModBase.LoadState.Failed,
            Canceled = PCL.ModBase.LoadState.Canceled,
            UiRun = PCL.LauncherLoadingState.Run,
            UiStop = PCL.LauncherLoadingState.Stop,
            UiError = PCL.LauncherLoadingState.Error,
            NewUuid = new Func<int>(() => PCL.ModBase.GetUuid()),
            Clock = new Func<long>(() => PCL.ModBase.GetTimeMs()),
            DebugFinishDelay = new Func<bool>(() => PCL.Settings.Get<bool>("SystemDebugDelay")),
            RandomInteger = new Func<int, int, int>((Min, Max) => PCL.ModBase.RandomInteger(Min, Max)),
            RunInUi = new Action<Action>(Work => PCL.ModBase.RunInUi(Work)),
            RunInUiWait = new Action<Action>(Work => PCL.ModBase.RunInUiWait(Work)),
            RunInThread = new Action<Action>(Work => PCL.ModBase.RunInThread(Work)),
            RefreshExtraDownload = new Action(() => PCL.ModMain.FrmMain.RefreshTaskButton()),
            Kind = NativeLoaderKind,
            IsDownloadInput = new Func<object, bool>(Input => Input is List<PCL.ModNet.NetFile>)
        };
        private static LegacyLoaderKind NativeLoaderKind(object Loader)
        {
            if (GeneralUtils.IsGenericInstanceOf(Loader, typeof(LoaderTask<, >)))
                return LegacyLoaderKind.Task;
            if (GeneralUtils.IsGenericInstanceOf(Loader, typeof(LoaderCombo<>)))
                return LegacyLoaderKind.Combo;
            if (Loader is LoaderWorker)
                return LegacyLoaderKind.Worker;
            if (Loader is PCL.ModNet.LoaderDownload)
                return LegacyLoaderKind.Download;
            return LegacyLoaderKind.Other;
        }

        public abstract class LoaderBase : LegacyLoaderBase<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>, PCL.ILoadingTrigger
        {
            protected LoaderBase() : base(NativeLoaderHost)
            {
            }

            public new bool IsLoader
            {
                get
                {
                    return base.IsLoader;
                }
            }

            public new PCL.LauncherLoadingState LoadingState
            {
                get
                {
                    return base.LoadingState;
                }

                set
                {
                    base.LoadingState = value;
                }
            }

            public event OnStateChangedThreadEventHandler OnStateChangedThread;
            public delegate void OnStateChangedThreadEventHandler(LoaderBase Loader, PCL.ModBase.LoadState NewState, PCL.ModBase.LoadState OldState);
            public event OnStateChangedUiEventHandler OnStateChangedUi;
            public delegate void OnStateChangedUiEventHandler(LoaderBase Loader, PCL.ModBase.LoadState NewState, PCL.ModBase.LoadState OldState);
            public event PreviewFinishEventHandler PreviewFinish;
            public delegate void PreviewFinishEventHandler(LoaderBase Loader);
            public event PCL.ILoadingTrigger.LoadingStateChangedEventHandler LoadingStateChanged;
            public event PCL.ILoadingTrigger.ProgressChangedEventHandler ProgressChanged;
            protected override void ConnectUiHandler(Action<LoaderBase, PCL.ModBase.LoadState, PCL.ModBase.LoadState> Handler)
            {
                OnStateChangedUi += (Loader, NewState, OldState) => Handler(Loader, NewState, OldState);
            }

            protected override void ConnectThreadHandler(Action<LoaderBase, PCL.ModBase.LoadState, PCL.ModBase.LoadState> Handler)
            {
                OnStateChangedThread += (Loader, NewState, OldState) => Handler(Loader, NewState, OldState);
            }

            protected override void RaiseStateUi(LoaderBase Loader, PCL.ModBase.LoadState NewState, PCL.ModBase.LoadState OldState)
            {
                OnStateChangedUi?.Invoke(Loader, NewState, OldState);
            }

            protected override void RaiseStateThread(LoaderBase Loader, PCL.ModBase.LoadState NewState, PCL.ModBase.LoadState OldState)
            {
                OnStateChangedThread?.Invoke(Loader, NewState, OldState);
            }

            protected override void RaiseLoadingState(PCL.LauncherLoadingState NewState, PCL.LauncherLoadingState OldState)
            {
                LoadingStateChanged?.Invoke(NewState, OldState);
            }

            protected override void RaiseProgress(double NewProgress, double OldProgress)
            {
                ProgressChanged?.Invoke(NewProgress, OldProgress);
            }

            protected override void RaisePreviewFinishCore(LoaderBase Loader)
            {
                PreviewFinish?.Invoke(Loader);
            }

            protected override void ApplyFileWorkloadGuard(Func<Action, Action> Wrap)
            {
                GuardFileWorkload(Wrap);
            }

            internal virtual void GuardFileWorkload(Func<Action, Action> Wrap)
            {
            }

            public abstract override void Start(object Input = null, bool IsForceRestart = false);
            public abstract override void Cancel();
            public abstract override void Failed(Exception Ex);
        }

        public class LoaderTask<TIn, TOut> : LoaderBase, ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>
        {
            protected internal ThreadPriority ThreadPriority;
            protected internal Action<LoaderTask<TIn, TOut>> LoadDelegate;
            protected internal Func<object> InputDelegate;
            public int ReloadTimeout = -1;
            public long LastFinishedTime = 0L;
            public Thread LastRunningThread = null;
            internal bool InterruptOnCancel { get; set; } = true;

            public TIn Input = default;
            public TOut Output = default;
            private readonly LegacyLoaderTaskEngine<LoaderTask<TIn, TOut>, TIn, TOut, LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> Native;
            public override bool? HasActiveWorkload => Native.HasActiveWorkload;
            public LoaderTask()
            {
                Native = new LegacyLoaderTaskEngine<LoaderTask<TIn, TOut>, TIn, TOut, LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>(this);
            }

            public LoaderTask(string Name, Action<LoaderTask<TIn, TOut>> LoadDelegate, Func<TIn> InputDelegate = null, ThreadPriority Priority = ThreadPriority.Normal) : this()
            {
                this.Name = Name;
                this.LoadDelegate = LoadDelegate;
                // VB stores this exact Func<TIn> reference under its erased Func<object> field.
                // Keep the runtime delegate type: Core.StartGetInput performs the original strict inverse cast.
                this.InputDelegate = global::System.Runtime.CompilerServices.Unsafe.As<Func<TIn>, Func<object>>(ref InputDelegate);
                ThreadPriority = Priority;
            }

            public bool IsCanceled
            {
                get
                {
                    return Native.IsCanceled;
                }
            }

            public bool IsCanceledWithThread(Thread Thread)
            {
                return Native.IsCanceledWithThread(Thread);
            }

            internal Func<bool> CaptureCancellationCheck()
            {
                Thread worker = Thread.CurrentThread;
                return () => IsCanceledWithThread(worker);
            }

            public TIn StartGetInput(TIn Input = default, Func<TIn> InputDelegate = null)
            {
                return Native.StartGetInput(Input, InputDelegate);
            }

            public bool ShouldStart(ref TIn Input, bool IsForceRestart = false, bool IgnoreReloadTimeout = false)
            {
                return Native.ShouldStart(ref Input, IsForceRestart, IgnoreReloadTimeout);
            }

            protected override bool ProbeTaskStart(object Input, bool IgnoreReloadTimeout)
            {
                return Native.ShouldStartForCombo(Input, IgnoreReloadTimeout);
            }

            protected override object ReadTaskOutput()
            {
                return Output;
            }

            public override void Start(object Input = null, bool IsForceRestart = false)
            {
                Native.Start(Input, IsForceRestart);
            }

            public override void Failed(Exception ex)
            {
                Native.Failed(ex);
            }

            public override void Cancel()
            {
                Native.Cancel();
            }

            internal override void GuardFileWorkload(Func<Action, Action> Wrap)
            {
                Native.GuardFileWorkload(Wrap);
            }

            private ThreadPriority CoreThreadPriority
            {
                get
                {
                    return ThreadPriority;
                }

                set
                {
                    ThreadPriority = value;
                }
            }

            ThreadPriority ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreThreadPriority { get => CoreThreadPriority; set => CoreThreadPriority = value; }

            private Action<LoaderTask<TIn, TOut>> CoreLoadDelegate
            {
                get
                {
                    return LoadDelegate;
                }

                set
                {
                    LoadDelegate = value;
                }
            }

            Action<LoaderTask<TIn, TOut>> ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreLoadDelegate { get => CoreLoadDelegate; set => CoreLoadDelegate = value; }

            private Func<object> CoreInputDelegate
            {
                get
                {
                    return InputDelegate;
                }

                set
                {
                    InputDelegate = value;
                }
            }

            Func<object> ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreInputDelegate { get => CoreInputDelegate; set => CoreInputDelegate = value; }

            private int CoreReloadTimeout
            {
                get
                {
                    return ReloadTimeout;
                }

                set
                {
                    ReloadTimeout = value;
                }
            }

            int ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreReloadTimeout { get => CoreReloadTimeout; set => CoreReloadTimeout = value; }

            private long CoreLastFinishedTime
            {
                get
                {
                    return LastFinishedTime;
                }

                set
                {
                    LastFinishedTime = value;
                }
            }

            long ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreLastFinishedTime { get => CoreLastFinishedTime; set => CoreLastFinishedTime = value; }

            private Thread CoreLastRunningThread
            {
                get
                {
                    return LastRunningThread;
                }

                set
                {
                    LastRunningThread = value;
                }
            }

            Thread ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreLastRunningThread { get => CoreLastRunningThread; set => CoreLastRunningThread = value; }

            private bool CoreInterruptOnCancel
            {
                get
                {
                    return InterruptOnCancel;
                }

                set
                {
                    InterruptOnCancel = value;
                }
            }

            bool ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreInterruptOnCancel { get => CoreInterruptOnCancel; set => CoreInterruptOnCancel = value; }

            private TIn CoreInput
            {
                get
                {
                    return Input;
                }

                set
                {
                    Input = value;
                }
            }

            TIn ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreInput { get => CoreInput; set => CoreInput = value; }

            private TOut CoreOutput
            {
                get
                {
                    return Output;
                }

                set
                {
                    Output = value;
                }
            }

            TOut ILegacyLoaderTaskData<LoaderTask<TIn, TOut>, TIn, TOut>.CoreOutput { get => CoreOutput; set => CoreOutput = value; }
        }

        public class LoaderWorker : LoaderBase
        {
            public override bool? HasActiveWorkload => State == PCL.ModBase.LoadState.Loading || Worker?.Running == true;
            private IWorker _Worker;
            public virtual IWorker Worker
            {
                [MethodImpl(MethodImplOptions.Synchronized)]
                get
                {
                    return _Worker;
                }

                [MethodImpl(MethodImplOptions.Synchronized)]
                set
                {
                    if (_Worker != null)
                    {
                        _Worker.Succeeded -= Worker_Succeeded;
                        _Worker.Canceled -= Cancel;
                        _Worker.Failed -= Failed;
                    }

                    _Worker = value;
                    if (_Worker != null)
                    {
                        _Worker.Succeeded += Worker_Succeeded;
                        _Worker.Canceled += Cancel;
                        _Worker.Failed += Failed;
                    }
                }
            }

            private readonly LegacyLoaderWorkerEngine<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> Native;
            public LoaderWorker(string Name, IWorker Worker)
            {
                Native = new LegacyLoaderWorkerEngine<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>(this, () => this.Worker);
                this.Name = Name;
                this.Worker = Worker;
                Native.BindInitialProgress(Worker);
            }

            public override void Start(object Input = null, bool IsForceRestart = false)
            {
                Native.Start(Input, IsForceRestart);
            }

            private void Worker_Succeeded()
            {
                Native.Succeeded();
            }

            public override void Cancel()
            {
                Native.Cancel();
            }

            public override void Failed(Exception Ex)
            {
                Native.Failed(Ex);
            }
        }

        public class LoaderCombo<InputType> : LoaderBase, ILegacyLoaderComboData<LoaderBase, InputType>
        {
            public override bool? HasActiveWorkload {
                get {
                    if (State == PCL.ModBase.LoadState.Loading) return true;
                    bool unknown = false;
                    foreach (LoaderBase child in Loaders) {
                        bool? active = child.HasActiveWorkload;
                        if (active == true) return true;
                        if (active is null) unknown = true;
                    }
                    return unknown ? null : false;
                }
            }
            public List<LoaderBase> Loaders = new List<LoaderBase>();
            public InputType Input;
            private readonly LegacyLoaderComboEngine<LoaderCombo<InputType>, InputType, LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> Native;
            public LoaderCombo(string Name, IEnumerable<LoaderBase> Loaders)
            {
                Native = new LegacyLoaderComboEngine<LoaderCombo<InputType>, InputType, LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>(this);
                Native.Initialize(Name, Loaders);
            }

            public override double Progress
            {
                get
                {
                    return Native.Progress;
                }

                set
                {
                    Native.Progress = value;
                }
            }

            public override void InitParent(LoaderBase Parent)
            {
                Native.InitParent(Parent);
            }

            public override void Start(object Input = null, bool IsForceRestart = false)
            {
                Native.Start(Input, IsForceRestart);
            }

            public override void Cancel()
            {
                Native.Cancel();
            }

            public override void Failed(Exception Ex)
            {
                Native.Failed(Ex);
            }

            internal override void GuardFileWorkload(Func<Action, Action> Wrap)
            {
                Native.GuardFileWorkload(Wrap);
            }

            public static void GetLoaderList(object Loader, ref List<LoaderBase> List, bool RequireShow = true)
            {
                LegacyLoaderLists.Append(Loader, ref List, RequireShow, NativeLoaderHost.Kind);
            }

            public void GetLoaderList(ref List<LoaderBase> List, bool RequireShow = true)
            {
                Native.GetLoaderList(ref List, RequireShow);
            }

            public List<LoaderBase> GetLoaderList(bool RequireShow = true)
            {
                return Native.GetLoaderList(RequireShow);
            }

            private List<LoaderBase> CoreLoaders
            {
                get
                {
                    return Loaders;
                }

                set
                {
                    Loaders = value;
                }
            }

            List<LoaderBase> ILegacyLoaderComboData<LoaderBase, InputType>.CoreLoaders { get => CoreLoaders; set => CoreLoaders = value; }

            private InputType CoreInput
            {
                get
                {
                    return Input;
                }

                set
                {
                    Input = value;
                }
            }

            InputType ILegacyLoaderComboData<LoaderBase, InputType>.CoreInput { get => CoreInput; set => CoreInput = value; }
        }

        public static ConcurrentList<LoaderBase> LoaderTaskbar {
            get => LauncherApplicationServices.TaskOwner.RuntimeTasks;
            set => LauncherApplicationServices.TaskOwner.RuntimeTasks = value;
        }
        public static double LoaderTaskbarProgress = 0d;
        private static System.Windows.Shell.TaskbarItemProgressState LoaderTaskbarProgressLast = System.Windows.Shell.TaskbarItemProgressState.None;
        private readonly static LegacyLoaderTaskbar<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> NativeTaskbar = CreateNativeTaskbar();
        private static LegacyLoaderTaskbar<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> CreateNativeTaskbar()
        {
            return new LegacyLoaderTaskbar<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>(NativeLoaderHost, new() {
                ReadTasks = () => LoaderTaskbar,
                ReadProgress = () => LoaderTaskbarProgress,
                WriteProgress = value => LoaderTaskbarProgress = value,
                ReadLastState = () => (int)LoaderTaskbarProgressLast,
                WriteLastState = value => LoaderTaskbarProgressLast = (System.Windows.Shell.TaskbarItemProgressState)value,
                RemoveTaskFromSpeedPage = loader => {
                    LauncherApplicationServices.TaskManager.OnTaskAdded(loader);
                },
                RefreshTaskOnSpeedPage = loader => {
                    LauncherApplicationServices.TaskManager.OnTaskRetiring(loader);
                },
                SetExtraDownloadProgress = _ => PCL.ModMain.FrmMain?.RefreshTaskButton(),
                SetTaskbarValue = value => PCL.ModMain.FrmMain.TaskbarItemInfo.ProgressValue = value,
                SetTaskbarState = value => PCL.ModMain.FrmMain.TaskbarItemInfo.ProgressState = (System.Windows.Shell.TaskbarItemProgressState)value
            });
        }

        public static void LoaderTaskbarAdd<T>(LoaderCombo<T> Loader)
        {
            NativeTaskbar.Add(Loader);
        }

        public static void LoaderTaskbarProgressRefresh()
        {
            NativeTaskbar.Refresh();
        }

        public static double LoaderTaskbarProgressGet()
        {
            return NativeTaskbar.GetProgress();
        }

        public enum LoaderFolderRunType
        {
            RunOnUpdated,
            ForceRun,
            UpdateOnly
        }

        private readonly static LegacyLoaderFolders<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState> NativeFolders = new LegacyLoaderFolders<LoaderBase, PCL.ModBase.LoadState, PCL.LauncherLoadingState>();
        public static bool LoaderFolderRun(LoaderBase Loader, string FolderPath, LoaderFolderRunType Type, int MaxDepth = 0, string ExtraPath = "", bool WaitForExit = false)
        {
            return NativeFolders.Run(Loader, FolderPath, (LegacyLoaderFolderRunType)Type, MaxDepth, ExtraPath, WaitForExit);
        }
    }
}
