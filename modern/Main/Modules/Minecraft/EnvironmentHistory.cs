// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using global::System.IO;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using global::System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using ThrottleDebounce;

namespace PCL
{

    // 内容寻址存储使多次还原点共用未变化的文件；还原点仅保存在本机，不随整合包导出。
    internal sealed class EnvironmentHistory
    {
        private static readonly global::System.Object Gate = new object();
        internal static readonly global::PCLCS.EnvironmentHistoryEngine<global::PCL.ModMinecraft.McInstance> Service = new global::PCLCS.EnvironmentHistoryEngine<global::PCL.ModMinecraft.McInstance>(global::PCL.EnvironmentHistory.Gate, global::PCL.EnvironmentHistory.CreateHost());
        internal static readonly global::System.String[] GameEnvironmentFolders = global::PCLCS.EnvironmentHistoryRules.GameEnvironmentFolders;
        internal static string TestRoot
        {
            get
            {
                return global::PCL.EnvironmentHistory.Service.TestRoot;
            }
            set
            {
                global::PCL.EnvironmentHistory.Service.TestRoot = value;
            }
        }
        private static int Restoring
        {
            get
            {
                return global::PCL.EnvironmentHistory.Service.RestoringCount;
            }
        }
        private static global::PCLCS.EnvironmentHistoryHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            return new global::PCLCS.EnvironmentHistoryHost<global::PCL.ModMinecraft.McInstance>()
            {
                DataRoot = new Func<global::System.String>(() => global::PCL.BackupStorage.Root),
                Hash = global::PCL.LocalGameData.Hash,
                AtomicWrite = new Action<global::System.String, global::System.Byte[]>((FileName, Bytes) => global::PCL.LocalGameData.AtomicWrite(FileName, Bytes, (global::System.String)null, false)),
                Target = global::PCL.ModpackUpdate.Target,
                RelativePath = global::PCL.ModpackUpdate.RelativePath,
                AddSlashSuffix = new Func<global::System.String, global::System.String>(Value => global::MeloongCore.PathUtils.AddSlashSuffix(Value)),
                RemoveExtendedPrefix = global::MeloongCore.PathUtils.RemoveExtendedPrefix,
                SafeName = global::PCL.InstanceResourceStore.SafeName,
                Digest = new Func<global::System.String, global::System.String>(FileName => global::PCL.ModpackUpdate.Digest(FileName)),
                EnsureNoLinks = global::PCL.InstanceCloner.EnsureNoLinks,
                EnsureWithin = new Action<global::System.String, global::System.String>((Root, FileName) => global::PCL.InstanceResourceStore.EnsureWithin(Root, FileName)),
                IsParentOf = new Func<global::System.String, global::System.String, global::System.Boolean>((Parent, Child) => global::MeloongCore.PathUtils.IsParentOf(Parent, Child)),
                SamePath = new Func<global::System.String, global::System.String, global::System.Boolean>((Left, Right) => global::MeloongCore.PathUtils.IsSame(Left, Right)),
                ApplyTransaction = new Action<global::System.String, global::System.String, global::System.Collections.Generic.List<global::System.Tuple<global::System.String, global::System.String, global::System.String>>, global::System.Threading.CancellationToken, global::System.Action<global::System.Int32>>((GameRoot, VersionFolder, Operations, Token, BeforeApply) => global::PCL.ModpackUpdate.ApplyTransaction(GameRoot, VersionFolder, Operations, Token, BeforeApply)),
                ApplyTransactionWithProgress = (GameRoot, VersionFolder, Operations, Token, Progress, BeforeApply) => global::PCL.ModpackUpdate.ApplyTransactionWithProgress(GameRoot, VersionFolder, Operations, Token, Progress, BeforeApply),
                VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathIndie),
                VanillaName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Version.VanillaName),
                LibraryPaths = new Func<global::PCL.ModMinecraft.McInstance, global::System.Collections.Generic.IEnumerable<global::System.String>>(Instance => global::PCL.ModMinecraft.McLibListGet(Instance, true).Select(Library => Library.LocalPath)),
                ModLoaderVersions = new Func<global::PCL.ModMinecraft.McInstance, global::System.Collections.Generic.IEnumerable<global::System.String>>(Instance => Instance.Version.ModLoaderVersions.Select(Pair => ((Pair.Key.ToString() + " ") + Pair.Value))),
                JsonObject = new Func<global::PCL.ModMinecraft.McInstance, global::Newtonsoft.Json.Linq.JObject>(Instance => Instance.JsonObject),
                AutoCleanup = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => global::PCL.Settings.Get<bool>("VersionEnvironmentHistoryAutoCleanup", Instance: Instance)),
                Retention = new Func<global::PCL.ModMinecraft.McInstance, global::System.Int32>(Instance => global::PCL.Settings.Get<int>("VersionEnvironmentHistoryRetention", Instance: Instance)),
                Info = new Action<global::System.String>(Message => global::MeloongCore.Logger.Info(Message)),
                Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message)),
                OpenFolder = new Action<global::System.String>(Folder => global::PCL.ModBase.OpenExplorer(Folder)),
                Hint = new Action<global::System.String>(Message => global::PCL.ModMain.Hint(Message))
            };
        }
        internal static global::System.Int32 CoverageVersion(global::Newtonsoft.Json.Linq.JObject Point)
        {
            return global::PCL.EnvironmentHistory.Service.CoverageVersion(Point);
        }
        internal static global::System.Collections.Generic.IEnumerable<global::System.String> FoldersForCoverage(global::System.Int32 Version)
        {
            return global::PCL.EnvironmentHistory.Service.FoldersForCoverage(Version);
        }
        private static global::System.String Store(global::System.String VersionFolder)
        {
            return global::PCL.EnvironmentHistory.Service.Store(VersionFolder);
        }
        internal static global::System.Boolean IsGameFile(global::System.String Relative)
        {
            return global::PCL.EnvironmentHistory.Service.IsGameFile(Relative);
        }
        internal static global::System.Boolean IsGameFileCovered(global::System.String Relative, global::System.Int32 Version)
        {
            return global::PCL.EnvironmentHistory.Service.IsGameFileCovered(Relative, Version);
        }
        internal static global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Files(global::System.String VersionFolder, global::System.String GameRoot)
        {
            return global::PCL.EnvironmentHistory.Service.Files(VersionFolder, GameRoot);
        }
        private static global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> FilesWithCoverage(global::System.String VersionFolder, global::System.String GameRoot, global::System.Int32 Version)
        {
            return global::PCL.EnvironmentHistory.Service.FilesWithCoverage(VersionFolder, GameRoot, Version);
        }
        internal static global::System.String Capture(global::System.String VersionFolder, global::System.String GameRoot, global::System.String Label, global::System.Threading.CancellationToken Token, global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Dependencies = null, global::System.String Description = "")
        {
            return global::PCL.EnvironmentHistory.Service.Capture(VersionFolder, GameRoot, Label, Token, Dependencies, Description);
        }
        internal static global::System.String CaptureInstance(global::PCL.ModMinecraft.McInstance Instance, global::System.String Label, global::System.Threading.CancellationToken Token)
        {
            return global::PCL.EnvironmentHistory.Service.CaptureInstance(Instance, Label, Token);
        }
        internal static string CaptureWithProgress(string VersionFolder, string GameRoot, string Label, CancellationToken Token, Action<double> Progress, Dictionary<string, string> Dependencies = null, string Description = "")
        {
            return Service.CaptureWithProgress(VersionFolder, GameRoot, Label, Token, Progress, Dependencies, Description);
        }
        internal static string CaptureInstanceWithProgress(ModMinecraft.McInstance Instance, string Label, CancellationToken Token, Action<double> Progress)
        {
            return Service.CaptureInstanceWithProgress(Instance, Label, Token, Progress);
        }
        internal static string RestoreWithProgress(string VersionFolder, string GameRoot, string McRoot, string Id, CancellationToken Token, Action<double> Progress, Action<int> BeforeApply = null, Func<string> CaptureBefore = null)
        {
            return Service.RestoreWithProgress(VersionFolder, GameRoot, McRoot, Id, Token, Progress, BeforeApply, CaptureBefore);
        }
        internal static global::System.Collections.Generic.List<global::Newtonsoft.Json.Linq.JObject> Points(global::System.String VersionFolder)
        {
            return global::PCL.EnvironmentHistory.Service.Points(VersionFolder);
        }
        internal static global::System.Boolean IsManualPoint(global::Newtonsoft.Json.Linq.JObject Point)
        {
            return global::PCL.EnvironmentHistory.Service.IsManualPoint(Point);
        }
        internal static void ApplyRetention(global::PCL.ModMinecraft.McInstance Instance, global::System.String NewestId, global::System.Threading.CancellationToken Token)
        {
            global::PCL.EnvironmentHistory.Service.ApplyRetention(Instance, NewestId, Token);
        }
        internal static global::System.Int32 PruneAutomatic(global::System.String VersionFolder, global::System.Int32 Count, global::System.String NewestId, global::System.Threading.CancellationToken Token)
        {
            return global::PCL.EnvironmentHistory.Service.PruneAutomatic(VersionFolder, Count, NewestId, Token);
        }
        internal static void MarkHealthy(global::System.String VersionFolder, global::System.String Id, global::System.Boolean Healthy)
        {
            global::PCL.EnvironmentHistory.Service.MarkHealthy(VersionFolder, Id, Healthy);
        }
        internal static global::System.String Restore(global::System.String VersionFolder, global::System.String GameRoot, global::System.String McRoot, global::System.String Id, global::System.Threading.CancellationToken Token, global::System.Action<global::System.Int32> BeforeApply = null, global::System.Func<global::System.String> CaptureBefore = null)
        {
            return global::PCL.EnvironmentHistory.Service.Restore(VersionFolder, GameRoot, McRoot, Id, Token, BeforeApply, CaptureBefore);
        }
        internal static void OpenFolder(global::System.String VersionFolder)
        {
            global::PCL.EnvironmentHistory.Service.OpenFolder(VersionFolder);
        }
        internal static global::System.String DeletePoints(global::System.String VersionFolder, global::System.Collections.Generic.IEnumerable<global::System.String> Ids)
        {
            return global::PCL.EnvironmentHistory.Service.DeletePoints(VersionFolder, Ids);
        }
    }

    internal sealed class EnvironmentHistoryUi
    {
        internal static event ChangedEventHandler Changed;

        internal delegate void ChangedEventHandler();
        internal static readonly global::PCLCS.EnvironmentHistoryWorkflow<global::PCL.ModMinecraft.McInstance> Service = new global::PCLCS.EnvironmentHistoryWorkflow<global::PCL.ModMinecraft.McInstance>(global::PCL.EnvironmentHistory.Service, global::PCL.EnvironmentHistoryUi.CreateHost());
        private static int Busy
        {
            get
            {
                return global::PCL.EnvironmentHistoryUi.Service.BusyCount;
            }
        }
        internal static bool IsBusy
        {
            get
            {
                return global::PCL.EnvironmentHistoryUi.Service.IsBusy;
            }
        }
        private static global::PCLCS.EnvironmentHistoryWorkflowHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            return new global::PCLCS.EnvironmentHistoryWorkflowHost<global::PCL.ModMinecraft.McInstance>()
            {
                HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft),
                HasRunningSessions = global::PCL.QuickPlayWorlds.HasRunningSessions,
                LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
                Updating = new Func<global::System.Boolean>(() => global::PCL.ModpackUpdateFlow.IsUpdating),
                AnyLocked = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceLock.IsAnyLocked),
                UpdatingModFolders = new Func<global::System.Boolean>(() => global::PCL.PageInstanceMod.UpdatingInstanceModFolders.Any()),
                VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathIndie),
                Name = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name),
                HasPending = new Func<global::System.String, global::System.Boolean>(VersionFolder => global::PCL.ModpackUpdate.Pending(VersionFolder).Any()),
                RecoverInstance = global::PCL.ModpackUpdateFlow.RecoverInstance,
                SamePath = new Func<global::System.String, global::System.String, global::System.Boolean>((Left, Right) => global::MeloongCore.PathUtils.IsSame(Left, Right)),
                Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warning)),
                SelectPoint = global::PCL.EnvironmentHistoryUi.SelectPoint,
                HintError = new Action<global::System.String>(Message => global::PCL.ModMain.Hint(Message, global::PCL.ModMain.HintType.Red)),
                Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                DispatchWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)),
                NotifyChanged = global::PCL.EnvironmentHistoryUi.PublishChanged,
                ResetSettingsCache = new Action<global::PCL.ModMinecraft.McInstance>(Instance => Instance.ResetSettingsCache()),
                CreateLoadedInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(VersionFolder => new global::PCL.ModMinecraft.McInstance(VersionFolder).Load()),
                ClearIniCache = new Action<global::System.String>(FileName => global::PCL.ModBase.IniClearCache(FileName)),
                PageInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                SetPageInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value),
                SetSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft.McInstanceSelected = Value),
                RefreshPageName = new Action(() => global::PCL.ModMain.FrmMain.PageNameRefresh()),
                StartWork = global::PCL.EnvironmentHistoryUi.StartWork
            };
        }
        private static global::System.Int32? SelectPoint(global::System.Collections.Generic.IEnumerable<global::PCLCS.EnvironmentHistoryChoice> Choices)
        {
            global::System.Int32? Choice = (global::System.Int32?)(default(global::System.Int32?));
            global::PCL.ModBase.RunInUiWait(() => Choice = global::PCL.ModMain.MyMsgBoxSelect(Choices.Select(Item => ((global::PCL.IMyRadio)(new global::PCL.MyRadioBox() { Text = Item.Text, ToolTip = Item.ToolTip }))), "选择环境还原点", "下一步", "取消"));
            return Choice;
        }
        private static void PublishChanged()
        {
            Changed?.Invoke();
        }
        internal static void Start(global::PCL.ModMinecraft.McInstance Instance, global::System.Boolean Restore, global::System.String PointId = null, global::System.Collections.Generic.IEnumerable<global::System.String> DeleteIds = null)
        {
            global::PCL.EnvironmentHistoryUi.Service.Start(Instance, Restore, PointId, DeleteIds);
        }
        private static global::System.Boolean StartWork(global::PCL.ModMinecraft.McInstance Instance, global::PCLCS.EnvironmentHistoryWork Work)
        {
            var StepLoader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.String>(Work.StepName, Task => {
                var Token = Task.CreateCancellationToken();
                Work.Progress = global::PCLCS.FileTaskProgress.ForTask(() => Task.Progress, Value => Task.Progress = Value);
                Work.Execute(Token, Output => Task.Output = Output);
            });
            var Loader = new global::PCL.ModLoader.LoaderCombo<global::System.String>(Work.Title, new[] { StepLoader }) { OnStateChanged = global::PCL.ModDownloadLib.LoaderStateChangedHintOnly };
            var Worker = new global::MeloongCore.RedoableWorker(global::PCL.LoaderFileOperation.CreateWorkload(Loader, Instance.PathVersion));
            bool BrowseOnly = Work.SnapshotReservation();
            if (!(BrowseOnly ? global::PCL.ModMinecraft.McInstanceLock.TryAcquireSnapshot(Worker, Instance.PathVersion) : global::PCL.ModMinecraft.McInstanceLock.TryAcquire(Worker, Instance.PathVersion)))
                return false;
            Worker.Stopped += () => Work.Stopped();
            Worker.Start();
            global::PCL.ModLoader.LoaderTaskbarAdd(Loader);
            global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh();
            return true;
        }
    }
}