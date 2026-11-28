// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using McInstance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal static class LauncherStartupSelection {
    internal static McInstance? Resolve() {
        string mrpack = Path.Combine(Paths.Base, "modpack.mrpack");
        string zip = Path.Combine(Paths.Base, "modpack.zip");
        string? pack = FileUtils.Exists(mrpack) ? mrpack : FileUtils.Exists(zip) ? zip : null;
        if (pack is not null) {
            Logger.Warn($"需自动安装整合包：{pack}");
            LauncherGameFolders.CreateStartupFolder();
            ModMinecraft.McFolderListLoader.WaitForExit();
        }

        string folder = ModMinecraft.McFolderSelected;
        if (string.IsNullOrEmpty(folder) || !DirectoryUtils.Exists(folder)) {
            if (string.IsNullOrEmpty(folder)) Logger.Info("没有已储存的 Minecraft 文件夹");
            else Logger.Warn($"Minecraft 文件夹无效，该文件夹已不存在：{folder}");
            ModMinecraft.McFolderListLoader.WaitForExit(IsForceRestart: true);
            ModMinecraft.McFolderSelected = ModMinecraft.McFolderList.First().Location;
        }

        if (Settings.Get<bool>("SystemDebugDelay")) Thread.Sleep(ModBase.RandomInteger(500, 3000));
        if (pack is not null) InstallStartupPack(pack);

        folder = ModMinecraft.McFolderSelected;
        string stored = ModBase.ReadIni(Path.Combine(folder, "PCL.ini"), "Version");
        var instance = string.IsNullOrEmpty(stored) ? null : new McInstance(stored);
        if (instance is not null && PathUtils.IsParentOf(folder, instance.PathVersion) && instance.Check()) return instance;

        Logger.Info($"当前选择的 Minecraft 版本无效：{instance?.PathVersion ?? "null"}",
            instance is null ? LogBehavior.None : LogBehavior.ToastIfDebug);
        if (ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Finished) {
            ModLoader.LoaderFolderRun(ModMinecraft.McInstanceListLoader, folder, ModLoader.LoaderFolderRunType.ForceRun,
                MaxDepth: 1, ExtraPath: @"versions\", WaitForExit: true);
        }
        instance = ModMinecraft.McInstanceList.Values.FirstOrDefault()?.FirstOrDefault();
        if (instance is null || instance.Logo.Contains("RedstoneBlock")) {
            Logger.Info("无可用 Minecraft 版本");
            return null;
        }
        Logger.Info($"自动选择 Minecraft 版本：{instance.PathVersion}");
        return instance;
    }

    private static void InstallStartupPack(string pack) {
        try {
            var installer = ModModpack.ModpackInstall(pack);
            Logger.Info($"自动安装整合包已开始：{pack}");
            installer.WaitForExit();
            if (installer.State == ModBase.LoadState.Finished) {
                Logger.Info($"自动安装整合包成功，清理安装包：{pack}");
                FileUtils.Delete(pack);
            }
        } catch (Exception error) {
            if (error.IsCanceled()) Logger.Info($"自动安装整合包已取消：{pack}");
            else Logger.Error(error, $"自动安装整合包失败：{pack}", LogBehavior.Alert);
        }
    }
}
