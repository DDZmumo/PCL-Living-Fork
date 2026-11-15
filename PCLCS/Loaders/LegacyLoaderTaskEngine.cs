using System;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS.Loaders;

/// <summary>LF Thread-based task algorithm; actual legacy fields remain behind the constrained data interface.</summary>
public sealed class LegacyLoaderTaskEngine<TTask, TIn, TOut, TLoader, TState, TLoadingState>
    where TLoader : LegacyLoaderBase<TLoader, TState, TLoadingState>
    where TTask : TLoader, ILegacyLoaderTaskData<TTask, TIn, TOut> {
    private readonly TTask owner;
    private LegacyLoaderHost<TLoader, TState, TLoadingState> Host => owner.Host;
    public LegacyLoaderTaskEngine(TTask owner) => this.owner = owner;
    private int activeWorkloads;
    public bool HasActiveWorkload => owner.IsState(owner.State, Host.Loading) || Volatile.Read(ref activeWorkloads) != 0;
    private static TIn ConvertInput(object? value) => Conversions.ToGenericParameter<TIn>(value)!;
    public bool IsCanceled => IsCanceledWithThread(Thread.CurrentThread);
    public bool IsCanceledWithThread(Thread Thread) => owner.CoreLastRunningThread is null || !ReferenceEquals(Thread, owner.CoreLastRunningThread) || owner.IsState(owner.State, Host.Canceled);
    public TIn StartGetInput(TIn Input = default!, Func<TIn>? InputDelegate = null) {
        // VB converts the stored delegate itself (CLR variance), without a value-converting adapter.
        // An incompatible Func<object> must fail before invocation, even when its result is convertible.
        if (InputDelegate is null) InputDelegate = (Func<TIn>?)(object?)owner.CoreInputDelegate;
        TIn newInput = default!;
        if ((Input is null || (newInput is not null && Input!.Equals(newInput))) && InputDelegate is not null)
            Host.RunInUiWait(() => Input = InputDelegate());
        return Input;
    }
    public bool ShouldStart(ref TIn Input, bool IsForceRestart = false, bool IgnoreReloadTimeout = false) {
        try { Input = StartGetInput(Input); }
        catch (Exception error) {
            Logger.Error(error, $"加载输入获取失败（{owner.Name}）", LogBehavior.Toast);
            owner.Error = error;
            lock (owner.LockState) owner.State = Host.Failed;
        }
        if (IsForceRestart) return true;
        if ((Input is null) != (owner.CoreInput is null) || (Input is not null && !Input.Equals(owner.CoreInput))) return true;
        if ((owner.IsState(owner.State, Host.Loading) || owner.IsState(owner.State, Host.Finished)) &&
            (IgnoreReloadTimeout || owner.CoreReloadTimeout == -1 || owner.CoreLastFinishedTime == 0 || checked(Host.Clock() - owner.CoreLastFinishedTime) < owner.CoreReloadTimeout)) return false;
        return true;
    }
    public bool ShouldStartForCombo(object? Input, bool IgnoreReloadTimeout) {
        TIn typed = ConvertInput(Input);
        return ShouldStart(ref typed, IgnoreReloadTimeout: IgnoreReloadTimeout);
    }
    public void Start(object? Input = null, bool IsForceRestart = false) {
        TIn typed = ConvertInput(Input);
        if (ShouldStart(ref typed, IsForceRestart)) {
            if (owner.IsState(owner.State, Host.Loading)) TriggerThreadInterrupt();
            owner.CoreInput = typed;
            lock (owner.LockState) { owner.State = Host.Loading; owner.Progress = -1; }
        } else return;
        int registered = 1;
        void ReleaseWorkload() { if (Interlocked.Exchange(ref registered, 0) != 0) Interlocked.Decrement(ref activeWorkloads); }
        Thread thread = new(() => {
            try {
                owner.IsForceRestarting = IsForceRestart;
                Logger.Trace(() => $"加载线程 {owner.Name} 已{(owner.IsForceRestarting ? "强制" : "")}启动");
                owner.CoreLoadDelegate!(owner);
                if (IsCanceled) {
                    Logger.Warn($"加载线程 {owner.Name} 已中断但线程正常运行至结束，输出被弃用（最新线程：{(owner.CoreLastRunningThread is null ? -1 : owner.CoreLastRunningThread.ManagedThreadId)}）");
                    return;
                }
                Logger.Trace(() => $"加载线程 {owner.Name} 已完成");
                owner.NotifyPreviewFinish();
                owner.State = Host.Finished;
                owner.CoreLastFinishedTime = Host.Clock();
            } catch (Exception error) {
                if (error.IsCanceled()) {
                    Logger.Trace(error, $"加载线程 {owner.Name} 已取消，当前已完成 {Math.Round(owner.Progress * 100)}%");
                    if (!IsCanceled) owner.State = Host.Canceled;
                } else Failed(error);
            } finally { ReleaseWorkload(); }
        }) { Name = "L/" + owner.Name, Priority = owner.CoreThreadPriority };
        owner.CoreLastRunningThread = thread;
        Interlocked.Increment(ref activeWorkloads);
        try {
            try { thread.Start(); }
            catch (ThreadStateException) { Thread.Sleep(500); thread.Start(); }
        } catch { if (!thread.IsAlive) ReleaseWorkload(); throw; }
    }
    public void Failed(Exception ex) {
        owner.Error = ex;
        lock (owner.LockState) {
            if (IsCanceled || owner.AtOrAfterFinished) return;
            owner.State = Host.Failed;
        }
        Logger.Warn(ex, $"加载线程 {owner.Name} 出错，已完成 {Math.Round(owner.Progress * 100)}%");
        TriggerThreadInterrupt();
    }
    public void Cancel() {
        lock (owner.LockState) {
            if (!owner.IsState(owner.State, Host.Loading)) return;
            owner.State = Host.Canceled;
        }
        if (owner.CoreInterruptOnCancel) TriggerThreadInterrupt();
    }
    private void TriggerThreadInterrupt() {
        if (owner.CoreLastRunningThread is null) return;
        if (owner.CoreLastRunningThread.IsAlive) {
            owner.CoreLastRunningThread.Interrupt();
            Logger.Trace(() => $"加载线程 {owner.Name} 已中断");
        }
        owner.CoreLastRunningThread = null;
    }
    public void GuardFileWorkload(Func<Action, Action> Wrap) {
        Action<TTask>? work = owner.CoreLoadDelegate;
        owner.CoreInterruptOnCancel = false;
        owner.CoreLoadDelegate = current => {
            Action guarded = Wrap(() => {
                current.CreateCancellationToken().ThrowIfCancellationRequested();
                work!(current);
            });
            guarded();
        };
    }
}
