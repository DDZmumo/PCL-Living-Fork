// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL;

internal static class LegacyResourceRecycleCleanup {
    private static readonly object gate = new();
    private static int backupCleanupScheduled;
    internal static void ScheduleBackupCleanup() {
        if (Interlocked.CompareExchange(ref backupCleanupScheduled, 1, 0) != 0) return;
        ModBase.RunInNewThread(() => {
            try {
                // The storage-location move takes these same gates in this order.
                lock (WorldBackups.Service.Gate) lock (EnvironmentHistory.Service.Gate) {
                    WorldBackups.Service.CleanupDeletedStores(CancellationToken.None);
                    EnvironmentHistory.Service.CleanupDeletedStores(CancellationToken.None);
                }
            } catch (Exception error) { Logger.Warn(error, "旧备份与还原点删除目录暂未清理，下次启动重试"); }
        }, "清理旧备份删除目录");
    }
    internal static Thread? Schedule(IEnumerable<string> minecraftFolders, IEnumerable<string>? sharedCaches = null) {
        string[] folders = minecraftFolders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] caches = (sharedCaches ?? Array.Empty<string>()).Where(folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (folders.Length == 0 && caches.Length == 0) return null;
        return ModBase.RunInNewThread(() => {
            lock (gate) {
                foreach (string folder in folders) {
                    try {
                        if (PermanentResourceDeletion.CleanupLegacy(folder, CancellationToken.None))
                            Logger.Info("已清理停用的资源回收站：" + folder);
                    } catch (Exception error) {
                        // An unavailable drive or sharing lock must not hide other
                        // game folders. The next folder-list load retries it.
                        Logger.Warn(error, "旧资源回收站暂未清理，下次加载游戏目录时重试：" + folder);
                    }
                }
                foreach (string cache in caches) {
                    try {
                        int removed = PermanentResourceDeletion.CleanupLegacyCache(cache, CancellationToken.None,
                            (error, folder) => Logger.Warn(error, "旧资源回收站暂未清理，下次加载时重试：" + folder));
                        if (removed > 0) Logger.Info("已清理共享缓存中的旧资源回收站：" + removed + " 个");
                    } catch (Exception error) {
                        Logger.Warn(error, "共享缓存中的旧资源回收站暂未清理，下次加载时重试：" + cache);
                    }
                }
            }
        }, "清理旧资源回收站");
    }
}
