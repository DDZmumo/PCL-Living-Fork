// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL;

internal static class BackupStorage {
    private static readonly object resolverGate = new();
    private static BackupStorageLocation location;
    private static string profile;
    private static int changing;
    // Production uses the LF-only HKCU key. TestRoot keeps existing isolated
    // filesystem fixtures from accessing a real user's Registry namespace.
    internal static Func<IBackupStoragePreference> PreferenceFactory = () => LocalGameData.TestRoot is null ? new BackupStorageRegistryStore() : null;
    internal static bool IsChanging => Volatile.Read(ref changing) != 0;
    internal static BackupStorageLocation Location {
        get {
            lock (resolverGate) {
                string currentProfile = Path.GetFullPath(LocalGameData.Root);
                if (location is null || !string.Equals(profile, currentProfile, StringComparison.OrdinalIgnoreCase)) {
                    location = new BackupStorageLocation(currentProfile, PreferenceFactory()); profile = currentProfile;
                }
                return location;
            }
        }
    }
    internal static string Root => Location.Root;
    private static void EnsureIdle(ModLoader.LoaderBase ownTask = null) {
        if (ModWatcher.HasRunningMinecraft || ModLaunch.McLaunchLoader.State == ModBase.LoadState.Loading ||
            WorldBackups.IsBusy || EnvironmentHistoryUi.IsBusy || InstanceDeletionOperation.HasActiveOperations ||
            ModLoader.LoaderTaskbar.Any(task => task.Show && task.State == ModBase.LoadState.Loading && !ReferenceEquals(task, ownTask)))
            throw new IOException("请等待游戏和文件任务结束后，再更改备份存储位置。");
    }
    internal static BackupStorageMoveResult MoveTo(string target, CancellationToken token, Action<double> progress = null, ModLoader.LoaderBase ownTask = null) {
        // Both engines are serialized before the root changes. New work resolves
        // the committed root only after these gates have been released.
        if (!Monitor.TryEnter(WorldBackups.Service.Gate)) throw new IOException("世界备份正在处理，请稍后再试。");
        try {
            if (!Monitor.TryEnter(EnvironmentHistory.Service.Gate)) throw new IOException("环境还原点正在处理，请稍后再试。");
            try {
                EnsureIdle(ownTask);
                var result = Location.MoveTo(target, token, progress);
                WorldBackups.ClearSummaryCache();
                return result;
            } finally { Monitor.Exit(EnvironmentHistory.Service.Gate); }
        } finally { Monitor.Exit(WorldBackups.Service.Gate); }
    }
    internal static bool StartMove(string target, Action<BackupStorageMoveResult, Exception, bool> stopped) {
        if (Interlocked.CompareExchange(ref changing, 1, 0) != 0) return false;
        try {
            EnsureIdle(); BackupStorageMoveResult result = null; Exception failure = null; bool canceled = false;
            ModLoader.LoaderCombo<string> loader = null;
            var step = new ModLoader.LoaderTask<string, string>("复制并校验备份数据", task => {
                try {
                    result = MoveTo(target, task.CreateCancellationToken(), PCLCS.FileTaskProgress.ForTask(() => task.Progress, value => task.Progress = value), loader);
                    task.Output = result.Root;
                } catch (Exception error) when (!error.IsCanceled()) { failure = error; throw; }
            });
            loader = new ModLoader.LoaderCombo<string>("更改备份与还原点存储位置", new[] { step });
            var worker = new RedoableWorker(LoaderFileOperation.CreateWorkload(loader, target));
            worker.Failed += error => failure ??= error;
            worker.Canceled += () => canceled = true;
            worker.Stopped += () => {
                Interlocked.Exchange(ref changing, 0);
                ModBase.RunInUi(() => stopped(result, canceled ? null : failure, canceled));
            };
            worker.Start(); ModLoader.LoaderTaskbarAdd(loader);
            ModMain.FrmMain?.BtnExtraDownload.ShowRefresh();
            return true;
        } catch { Interlocked.Exchange(ref changing, 0); throw; }
    }
}
