using System;
using System.Collections.Generic;
using System.Threading;

namespace PCL;

internal static class MmcOperationLoaders {
    internal static int Finishing;
    internal static bool HasBlockingCommit => Volatile.Read(ref Finishing) != 0;
}
// Original LF host integration; repository LICENCE applies.
internal sealed class MmcOperationCancellationFence {
    private readonly object gate = new();
    private readonly CancellationTokenSource source = new();
    private readonly CancellationToken token;
    private Action released;
    private bool finishing, completed, workerScheduled;
    public MmcOperationCancellationFence(Action onReleased = null) { token=source.Token;released=onReleased; }
    public CancellationToken Token => token;
    internal bool IsCompleted { get { lock(gate)return completed; } }
    public void Cancel(Action standardCancel) {
        Action release=null;
        try { lock (gate) {
            if (finishing) return;
            // Native cancellation/state propagation may arrive after the real delegate returned.
            // Its root still needs its terminal state, without touching a disposed CTS twice.
            if(completed){standardCancel();return;}
            source.Cancel();standardCancel();
            if(!workerScheduled)release=CompleteUnderGate();
        }}finally{release?.Invoke();}
    }
    public void EnterDecision(CancellationToken token) {
        lock (gate) {
            token.ThrowIfCancellationRequested();
            if (!finishing) { finishing = true; Interlocked.Increment(ref MmcOperationLoaders.Finishing); }
        }
    }
    internal bool ScheduleWorker(Action start,Func<Thread> actualThread) {
        Action release=null;
        try {lock(gate){
            if(completed||workerScheduled||token.IsCancellationRequested)return false;
            workerScheduled=true;
            try {start();}
            finally {
                var thread=actualThread();
                // No real Thread.Start means no producer can later enter this operation. Once
                // a thread is scheduled, cancellation waits for ExecuteWorker's real finally.
                if(thread is null||(thread.ThreadState&System.Threading.ThreadState.Unstarted)!=0)release=CompleteUnderGate();
            }
            return true;
        }}finally{release?.Invoke();}
    }
    internal void ExecuteWorker(Action work) {
        try {token.ThrowIfCancellationRequested();work();}
        finally {Action release;lock(gate)release=CompleteUnderGate();release?.Invoke();}
    }
    public void AbandonUnstarted()=>Cancel(()=>{});
    private Action CompleteUnderGate(){
        if(completed)return null;
        completed=true;if(finishing)Interlocked.Decrement(ref MmcOperationLoaders.Finishing);
        source.Dispose();var callback=released;released=null;return callback;
    }
}

// The original scheduler reads concrete generic input types. Preserve that contract.
internal sealed class MmcOperationWorker<TIn, TOut> : ModLoader.LoaderTask<TIn, TOut> {
    private readonly MmcOperationCancellationFence fence;
    internal bool OperationCompleted=>fence.IsCompleted;
    internal MmcOperationWorker(MmcOperationCancellationFence fence,string name,Action<ModLoader.LoaderTask<TIn,TOut>> work)
        :base(name,task=>fence.ExecuteWorker(()=>work(task))){this.fence=fence;}
    public override void Start(object input=null,bool IsForceRestart=false){
        if(!fence.ScheduleWorker(()=>base.Start(input,IsForceRestart),()=>LastRunningThread)&&State==ModBase.LoadState.Waiting)State=ModBase.LoadState.Canceled;
    }
    public override void Cancel() => fence.Cancel(() => base.Cancel());
}
internal sealed class MmcOperationCombo<TIn>(MmcOperationCancellationFence fence, string name,
    IEnumerable<ModLoader.LoaderBase> children) : ModLoader.LoaderCombo<TIn>(name, children) {
    public override void Cancel() => fence.Cancel(() => base.Cancel());
}
