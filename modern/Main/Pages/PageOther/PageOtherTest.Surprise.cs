// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Input;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

public partial class PageOtherTest {
    private static int SurpriseRunning;
    // Tests replace only this UI boundary; production uses the real native warning/workflow.
    internal static Action SurpriseAction = StartSurprise;

    private void BtnSurprise_Click(object sender, MouseButtonEventArgs e) => SurpriseAction();

    internal static ModLoader.LoaderCombo<int> CreateSurpriseDownloadTask(SymbioLauncherAsset asset, string target) {
        ModNet.LoaderDownload download = new("搬运神秘包裹", new[] {
            new ModNet.NetFile(new[] { asset.Url }, target, new FileChecker { Hash = asset.Sha256, ActualSize = asset.Size })
        }) { ProgressWeight = 1 };
        // The outer workflow supplies neutral hints, including failures; downloader
        // exception text can contain the release asset name and stays in diagnostics.
        return new ModLoader.LoaderCombo<int>("准备一份小惊喜", new List<ModLoader.LoaderBase> { download });
    }

    private static void StartSurprise() {
        if (Interlocked.CompareExchange(ref SurpriseRunning, 1, 0) != 0) { ModMain.Hint("小惊喜正在准备中，别着急呀～"); return; }
        bool started = false;
        try {
            if (ModMain.MyMsgBox(SymbioLauncherSwitch.Warning, "千万别点", "取消", "继续", IsWarn: true, HighLight: false) != 2) return;
            ModBase.RunInNewThread(RunSurprise, "Toolbox Surprise");
            started = true;
        } finally { if (!started) Interlocked.Exchange(ref SurpriseRunning, 0); }
    }

    private static void EnsureCanSwitch() {
        if (AuxiliaryPageRuntime.Tools.GameFilesBusy() || ReadCleanup() != 0 || InstanceDeletionOperation.HasActiveOperations)
            throw new IOException("请等待游戏和文件任务结束后再试。");
    }

    private static void RunSurprise() {
        IDisposable shutdown = null;
        try {
            EnsureCanSwitch();
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            var host = new SymbioLauncherSwitchHost {
                Release = () => JObject.Parse(ModNet.NetRequestByClient(SymbioLauncherSwitch.LatestApi, Accept: "application/json", Timeout: 10000, RequireJson: true)),
                Hint = text => ModMain.Hint(text),
                Download = (asset, target) => {
                    var task = CreateSurpriseDownloadTask(asset, target);
                    ModBase.RunInUiWait(() => {
                        task.Start(1); ModLoader.LoaderTaskbarAdd(task);
                        ModMain.FrmMain.BtnExtraDownload.ShowRefresh(); ModMain.FrmMain.BtnExtraDownload.Ribble();
                    });
                    task.WaitForExit(StartIfNeeded: false);
                },
                Start = target => {
                    EnsureCanSwitch();
                    shutdown = InstanceDeletionOperation.TryReserveShutdown() ?? throw new IOException("请等待文件任务结束后再试。");
                    using Process process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) });
                    if (process is null || (process.WaitForExit(750) && process.ExitCode != 0)) throw new IOException("彩蛋程序没能正常启动。");
                },
                Close = () => ModBase.RunInUiWait(() => ModMain.FrmMain.EndProgram(SendWarning: false))
            };
            new SymbioLauncherSwitch(host).Run(Path.GetDirectoryName(executable), executable);
        } catch (OperationCanceledException) { ModMain.Hint("神秘包裹先收起来，下次再拆吧～"); }
        catch (ThreadInterruptedException) { ModMain.Hint("神秘包裹先收起来，下次再拆吧～"); }
        catch (Exception error) {
            Logger.Warn(error, "工具百宝箱彩蛋未完成，保留当前启动器");
            ModMain.Hint("小惊喜暂时迷路了，等一会儿再试试吧～", ModMain.HintType.Red);
        } finally { shutdown?.Dispose(); Interlocked.Exchange(ref SurpriseRunning, 0); }
    }
}
