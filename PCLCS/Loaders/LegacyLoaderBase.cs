using System;
using System.Collections.Generic;
using System.Threading;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS.Loaders;

/// <summary>Common LF state owns one set of fields; TLoader/state values are the original closed types.</summary>
public abstract class LegacyLoaderBase<TLoader, TState, TLoadingState>
    where TLoader : LegacyLoaderBase<TLoader, TState, TLoadingState> {
    protected LegacyLoaderBase(LegacyLoaderHost<TLoader, TState, TLoadingState> host) {
        Host = host;
        Uuid = host.NewUuid(); Name = "未命名任务 " + Uuid + "#";
        state = host.Waiting; loadingState = host.UiStop;
    }
    internal LegacyLoaderHost<TLoader, TState, TLoadingState> Host { get; }
    protected TLoader Self => (TLoader)this;
    public bool IsLoader => true;
    public int Uuid;
    public string Name;
    public readonly object LockState = new();
    public TLoader? Parent;
    public TLoader? RealParent {
        get {
            try {
                TLoader? value = Parent;
                while (value is not null && value.Parent is not null) value = value.Parent;
                return value;
            } catch (Exception error) { Logger.Error(error, $"获取父加载器失败（{Name}）"); return null; }
        }
    }
    public virtual void InitParent(TLoader? Parent) => this.Parent = Parent;
    public bool HasOnStateChangedThread;
    public Action<TLoader> OnStateChanged { set => ConnectUiHandler((loader, _, _) => value(loader)); }
    protected abstract void ConnectUiHandler(Action<TLoader, TState, TState> handler);
    protected abstract void ConnectThreadHandler(Action<TLoader, TState, TState> handler);
    protected abstract void RaiseStateUi(TLoader Loader, TState NewState, TState OldState);
    protected abstract void RaiseStateThread(TLoader Loader, TState NewState, TState OldState);
    protected abstract void RaiseLoadingState(TLoadingState NewState, TLoadingState OldState);
    protected abstract void RaiseProgress(double NewProgress, double OldProgress);
    protected abstract void RaisePreviewFinishCore(TLoader Loader);
    protected abstract void ApplyFileWorkloadGuard(Func<Action, Action> Wrap);
    protected virtual bool ProbeTaskStart(object? Input, bool IgnoreReloadTimeout) => throw new MissingMethodException();
    protected virtual object? ReadTaskOutput() => throw new MissingFieldException();
    protected void RaisePreviewFinish() => RaisePreviewFinishCore(Self);
    internal void NotifyPreviewFinish() => RaisePreviewFinish();
    internal void AddThreadHandler(Action<TLoader, TState, TState> handler) => ConnectThreadHandler(handler);
    internal void GuardWorkload(Func<Action, Action> Wrap) => ApplyFileWorkloadGuard(Wrap);
    internal bool ShouldStartForCombo(object? Input, bool IgnoreReloadTimeout) => ProbeTaskStart(Input, IgnoreReloadTimeout);
    internal object? TaskOutput => ReadTaskOutput();
    internal bool IsState(TState value, TState expected) => EqualityComparer<TState>.Default.Equals(value, expected);
    internal bool AtOrAfterFinished => Convert.ToInt32(State) >= Convert.ToInt32(Host.Finished);

    private TState state;
    public TState State {
        get => state;
        set {
            if (IsState(state, value)) return;
            TState previous = state;
            if (IsState(value, Host.Finished) && Host.DebugFinishDelay()) Thread.Sleep(Host.RandomInteger(100, 2000));
            state = value;
            if (IsState(value, Host.Canceled)) cancellationTokenSource?.Cancel();
            Logger.Log($"加载器 {GetType().Name} {Name} 状态改变：{value}", IsState(value, Host.Failed) ? LogLevel.Error : LogLevel.Info);
            Host.RunInUi(() => {
                LoadingState = IsState(value, Host.Loading) ? Host.UiRun : IsState(value, Host.Failed) ? Host.UiError : Host.UiStop;
                RaiseStateUi(Self, value, previous);
            });
            if (HasOnStateChangedThread) Host.RunInThread(() => RaiseStateThread(Self, value, previous));
        }
    }
    private TLoadingState loadingState;
    public TLoadingState LoadingState {
        get => loadingState;
        set {
            if (EqualityComparer<TLoadingState>.Default.Equals(loadingState, value)) return;
            TLoadingState previous = loadingState; loadingState = value;
            RaiseLoadingState(value, previous);
        }
    }
    public Exception? Error { get; set; }
    public bool Block = true, Show = true, IsForceRestarting;
    private double progress = -1;
    public virtual double Progress {
        get => IsState(State, Host.Waiting) ? 0 : IsState(State, Host.Loading) ? progress == -1 ? 0 : progress : 1;
        set {
            if (progress == value) return;
            double previous = progress; progress = value;
            RaiseProgress(value, previous);
        }
    }
    public ProgressProvider CreateSyncProgressProvider(double FromPercentage = 0, double ToPercentage = 1) {
        ProgressProvider provider = new();
        provider.ProgressChanged += _ => {
            var raw = provider.GetRaw();
            Progress = FromPercentage + (raw.actual + raw.skiped) * (ToPercentage - FromPercentage);
        };
        return provider;
    }
    public double ProgressWeight { get; set; } = 1;
    public abstract void Start(object? Input = null, bool IsForceRestart = false);
    public abstract void Cancel();
    public abstract void Failed(Exception Ex);
    /// <summary>Actual workload lifetime where supported; unknown must never be interpreted as completed cleanup.</summary>
    public virtual bool? HasActiveWorkload => null;
    private CancellationTokenSource? cancellationTokenSource;
    public CancellationToken CreateCancellationToken() {
        lock (LockState) {
            if (cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested) {
                cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.Token.Register(Cancel);
            }
            if (IsState(State, Host.Canceled)) cancellationTokenSource.Cancel();
            return cancellationTokenSource.Token;
        }
    }
    public const string WaitForExitTimeoutMessage = "等待加载器执行超时。";
    public void WaitForExit(object? Input = null, TLoader? LoaderToSyncProgress = null, bool IsForceRestart = false,
        CancellationToken c = default, ProgressProvider? p = null, bool StartIfNeeded = true) {
        if (StartIfNeeded) Start(Input, IsForceRestart);
        while (IsState(State, Host.Loading)) {
            if (LoaderToSyncProgress is not null) LoaderToSyncProgress.Progress = Progress;
            p?.Set(Progress); c.ThrowIfCancellationRequested(); Thread.Sleep(10);
        }
        if (IsState(State, Host.Finished)) return;
        if (IsState(State, Host.Canceled)) throw new OperationCanceledException("加载器执行已中断");
        if (Error is null) throw new Exception("未知错误！");
        throw new Exception(Error.Message, Error);
    }
    public void WaitForExitTime(int Timeout, object? Input = null, string TimeoutMessage = WaitForExitTimeoutMessage,
        object? LoaderToSyncProgress = null, bool IsForceRestart = false) {
        Start(Input, IsForceRestart);
        while (IsState(State, Host.Loading)) {
            if (LoaderToSyncProgress is not null) NewLateBinding.LateSet(LoaderToSyncProgress, null, "Progress", new object?[] { Progress }, null, null);
            Thread.Sleep(10); Timeout = checked(Timeout - 10);
            if (Timeout < 0) throw new TimeoutException(TimeoutMessage);
        }
        if (IsState(State, Host.Finished)) return;
        if (IsState(State, Host.Canceled)) throw new OperationCanceledException("加载器执行已中断");
        if (Error is null) throw new Exception("未知错误！");
        throw Error;
    }
    public override bool Equals(object? obj) => obj is TLoader other && Uuid == other.Uuid;
    // LF overrides Equals but keeps Object's identity hash; do not invent UUID hashing.
    public override int GetHashCode() => base.GetHashCode();
}
