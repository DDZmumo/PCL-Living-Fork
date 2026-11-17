using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>Cooperative cross-process admission. A lease is synchronous and must stay on its owning thread.</summary>
public static class MmcAdmissionGate {
    public static MmcAdmissionLease Acquire(string versionRoot, Guid instanceId, TimeSpan timeout, CancellationToken cancellationToken = default) {
        string root = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(versionRoot));
        return AcquireCanonical(root, instanceId, timeout, cancellationToken);
    }
    internal static MmcAdmissionLease AcquireConfigurationRoot(string trustedRoot, Guid instanceId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        AcquireCanonical(MmcCanonicalPaths.ForTrustedRoot(trustedRoot), instanceId, timeout, cancellationToken);
    private static MmcAdmissionLease AcquireCanonical(string root, Guid instanceId, TimeSpan timeout, CancellationToken cancellationToken) {
        if (instanceId == Guid.Empty) throw new ArgumentException("实例身份不能为空。", nameof(instanceId));
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        // Path is the exclusion key: changing an untrusted ID must not bypass an existing path lease.
        string key = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        string name = "PCLLF.Mmc.Admission." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        Mutex mutex = new(false, (OperatingSystem.IsWindows() ? "Global\\" : "") + name);
        bool acquired = false, abandoned = false;
        try {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                if (OperatingSystem.IsWindows()) {
                    int selected = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], timeout);
                    if (selected == 1) cancellationToken.ThrowIfCancellationRequested();
                    acquired = selected == 0;
                } else acquired = WaitNamedMutex(mutex, timeout, cancellationToken);
            } catch (AbandonedMutexException) { acquired = true; abandoned = true; }
            if (!acquired) throw new IOException("MMC 实例正忙，请稍后重试。");
            cancellationToken.ThrowIfCancellationRequested();
            return new(mutex, root, instanceId, abandoned);
        } catch { if (acquired) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private static bool WaitNamedMutex(Mutex mutex, TimeSpan timeout, CancellationToken cancellationToken) {
        // Unix supports a real named Mutex via WaitOne, but not WaitAny with that
        // mutex. Keep ownership on this thread and use one monotonic total budget.
        long milliseconds = (long)timeout.TotalMilliseconds;
        if (milliseconds < Timeout.Infinite || milliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        long started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        if (mutex.WaitOne(0)) return true;
        if (milliseconds == 0) return false;
        for (;;) {
            cancellationToken.ThrowIfCancellationRequested();
            int slice = 20;
            if (milliseconds != Timeout.Infinite) {
                double remaining = milliseconds - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) return false;
                slice = (int)Math.Min(slice, Math.Ceiling(remaining));
            }
            // Return immediately after acquisition. The shared post-acquire token
            // check/catch above must see acquired=true before it can release it.
            if (mutex.WaitOne(slice)) return true;
        }
    }
}

public sealed class MmcAdmissionLease : IDisposable {
    private Mutex? mutex;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    public string VersionRoot { get; }
    public Guid InstanceId { get; }
    public bool WasAbandoned { get; }
    internal MmcAdmissionLease(Mutex mutex, string root, Guid instanceId, bool abandoned) {
        this.mutex = mutex; VersionRoot = root; InstanceId = instanceId; WasAbandoned = abandoned;
    }
    public void AssertHeld() {
        if (mutex is null) throw new ObjectDisposedException(nameof(MmcAdmissionLease));
        if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("MMC 准入租约不能跨线程或 await 迁移。");
    }
    public void Dispose() {
        if (mutex is null) return;
        AssertHeld(); Mutex owned = mutex; mutex = null; owned.ReleaseMutex(); owned.Dispose();
    }
}
