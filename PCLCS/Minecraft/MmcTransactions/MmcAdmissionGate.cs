using System;
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
                int selected = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], timeout);
                if (selected == 1) cancellationToken.ThrowIfCancellationRequested();
                acquired = selected == 0;
            } catch (AbandonedMutexException) { acquired = true; abandoned = true; }
            if (!acquired) throw new IOException("MMC 实例正忙，请稍后重试。");
            cancellationToken.ThrowIfCancellationRequested();
            return new(mutex, root, instanceId, abandoned);
        } catch { if (acquired) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
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
