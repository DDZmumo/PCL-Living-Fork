using System;
using System.Diagnostics;
using MeloongCore;

namespace PCLCS.Applications;

// Existing cancellation ordering extracted from presentation/Launch/LegacyLaunchLeft.cs
// at 74031b6da49d6efa0cfe453f41285ee1b2b3b333. Retains the inherited root LICENCE.

public sealed class LaunchCancellationHost {
    public Func<bool> HasOperation = null!, HasWatcher = null!;
    public Action CancelOperation = null!, LogCancellation = null!, KillWatcher = null!;
    public Func<Process?> ReadProcess = null!;
}

public static class LaunchCancellationActions {
    public static void Cancel(LaunchCancellationHost host) {
        if (!host.HasOperation()) return;
        host.CancelOperation(); host.LogCancellation();
        try {
            if (host.HasWatcher()) host.KillWatcher();
            else {
                Process? process = host.ReadProcess();
                if (process is not null && !process.HasExited) process.Kill();
            }
        } catch (Exception error) { Logger.Error(error, "取消启动结束进程失败", LogBehavior.Toast); }
    }
}
