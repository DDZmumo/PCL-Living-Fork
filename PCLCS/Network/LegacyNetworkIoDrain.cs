using System;
using System.Threading;
using System.Threading.Tasks;
using PCLCS.Loaders;

namespace PCLCS.Network;

// LF-owned lifecycle observation. Does not create threads, change download priority or retry policy.
internal sealed class LegacyNetworkIoCounter {
    private readonly object gate = new();
    private int active;
    private bool sealedForNewWork;
    private TaskCompletionSource? idle;
    internal int Active { get { lock (gate) return active; } }
    internal bool IsSealed { get { lock (gate) return sealedForNewWork; } }
    internal IDisposable? Enter() {
        lock (gate) {
            if (sealedForNewWork) return null;
            if (active++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this);
        }
    }
    internal void Seal() { lock (gate) sealedForNewWork = true; }
    internal Task WhenIdle() { lock (gate) return active == 0 ? Task.CompletedTask : idle!.Task; }
    private void Exit() {
        TaskCompletionSource? complete = null;
        lock (gate) { if (--active == 0) complete = idle; }
        complete?.TrySetResult();
    }
    private sealed class Lease(LegacyNetworkIoCounter owner) : IDisposable {
        private LegacyNetworkIoCounter? held = owner;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Exit();
    }
}

public sealed partial class LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TDownload : TLoader, ILegacyNetworkDownload<TFile>
    where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState>
    where TNetState : struct, Enum where TLoadState : struct, Enum {
    private void RunDownloadWorker(TDownload owner, Action work, string name, ThreadPriority priority, IDisposable? registeredLease = null) {
        IDisposable lease = registeredLease ?? owner.CoreRecord.IoWork.Enter() ?? throw new InvalidOperationException("Download operation cannot register new work.");
        try { Host.RunNewThread(() => { try { work(); } finally { lease.Dispose(); } }, name, priority); }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>
    /// Call after terminal state, and do not restart this loader until this barrier returns.
    /// Includes initialization, existing-file copies, transfer cleanup and merge. Shared files are
    /// not canceled here: their remaining owners may continue, and this barrier waits for those
    /// writers too before declaring that the target is quiescent. No instance/UI lock is taken.
    /// </summary>
    public static async Task WaitForDownloadQuiescenceAsync(TDownload owner) {
        lock (owner.LockState) {
            if (Convert.ToInt32(owner.State) < 2) throw new InvalidOperationException("Download must reach a terminal state before draining.");
        }
        await owner.CoreRecord.IoWork.WhenIdle().ConfigureAwait(false);
        foreach (TFile file in owner.CoreFiles) {
            while (true) {
                Task wait;
                lock (file.LockState) {
                    if (Convert.ToInt32(file.State) >= 5 && file.IoWork.IsSealed && file.IoWork.Active == 0) break;
                    wait = file.IoWork.WhenIdle();
                }
                if (!wait.IsCompleted) await wait.ConfigureAwait(false);
                else await Task.Delay(10).ConfigureAwait(false); // shared owner may schedule its next transfer
            }
        }
    }
}
