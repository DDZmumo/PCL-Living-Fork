// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MeloongCore;
using MeloongCore.Wpf;

namespace PCL;

public partial class PageSetupSystem {
    internal Func<string> SelectBackupFolder = () => Dialogs.SelectFolder("选择备份与还原点存储位置（空文件夹）", false, BackupStorage.Root).FirstOrDefault();
    internal Func<string, string, bool> ConfirmBackupMove = (oldRoot, newRoot) => ModMain.MyMsgBox(
        "将世界备份和环境还原点从：\r\n" + oldRoot + "\r\n\r\n迁移到：\r\n" + newRoot +
        "\r\n\r\n数据复制并校验成功后才切换位置，随后清理旧目录。请选择空文件夹，并等待当前游戏和文件任务结束。",
        "更改备份存储位置", "取消", "迁移", IsWarn: true, HighLight: false) == 2;

    private void RefreshBackupStorage() {
        try { TextBackupStoragePath.Text = BackupStorage.Root; }
        catch (Exception error) { TextBackupStoragePath.Text = "无法读取存储位置：" + error.Message; }
        BtnBackupStorageChoose.IsEnabled = !BackupStorage.IsChanging;
        LabBackupStorageStatus.Text = BackupStorage.IsChanging ? "正在迁移，请在任务列表中查看进度。" : "世界备份和环境还原点共同使用此目录。";
    }
    private void BtnBackupStorageChoose_Click(object sender, MouseButtonEventArgs e) {
        try {
            if (BackupStorage.IsChanging) { ModMain.Hint("备份数据正在迁移，请稍候。"); return; }
            string selected = SelectBackupFolder();
            if (string.IsNullOrWhiteSpace(selected)) return;
            string target = Path.GetFullPath(selected), current = BackupStorage.Root;
            if (string.Equals(target.TrimEnd('\\','/'), current.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase)) return;
            if (!ConfirmBackupMove(current, target)) return;
            if (!BackupStorage.StartMove(target, (result, failure, canceled) => {
                RefreshBackupStorage();
                if (failure is not null) { Logger.Warn(failure, "迁移备份存储位置失败"); ModMain.MyMsgBox(failure.Message, "备份迁移未完成", IsWarn: true); }
                else if (result is not null) {
                    ModMain.Hint(result.CleanupComplete ? "备份与还原点已迁移，新位置已生效。" : "新位置已生效，旧目录部分文件未清理，请保留并检查：" + result.PreviousRoot,
                        result.CleanupComplete ? ModMain.HintType.Green : ModMain.HintType.Red);
                } else if (canceled) ModMain.Hint("迁移已取消，原备份位置保持有效。");
            })) ModMain.Hint("备份数据正在迁移，请稍候。");
            RefreshBackupStorage();
        } catch (Exception error) { Logger.Warn(error, "更改备份存储位置失败"); ModMain.MyMsgBox(error.Message, "备份迁移未完成", IsWarn: true); RefreshBackupStorage(); }
    }
    private void BtnBackupStorageOpen_Click(object sender, MouseButtonEventArgs e) {
        try {
            string root = BackupStorage.Root;
            if (!Directory.Exists(root)) { ModMain.Hint("当前备份目录尚未创建。"); return; }
            ModBase.OpenExplorer(root);
        } catch (Exception error) { ModMain.MyMsgBox(error.Message, "无法打开备份目录", IsWarn: true); }
    }
}
