// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Wpf.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{

    // 保留 LF 的共享调用入口和事件类型；备份格式、事务及工作流由 C# 实现维护。
    internal sealed class WorldBackups
    {
        internal static readonly WorldBackupEngine<ModMinecraft.McInstance> Service = new WorldBackupEngine<ModMinecraft.McInstance>(CreateHost());
        // 与引擎使用同一把锁和同一份状态，供原消费者及测试入口使用。
        private static readonly object Gate = Service.Gate;
        private static int Busy
        {
            get
            {
                return Service.BusyCount;
            }
        }
        internal static string TestRoot
        {
            get
            {
                return Service.TestRoot;
            }
            set
            {
                Service.TestRoot = value;
            }
        }
        internal static event ChangedEventHandler Changed;

        internal delegate void ChangedEventHandler();
        internal static bool IsBusy
        {
            get
            {
                return Service.IsBusy;
            }
        }

        private static WorldBackupHost<ModMinecraft.McInstance> CreateHost()
        {
            WorldBackupHost<ModMinecraft.McInstance> @init = new WorldBackupHost<ModMinecraft.McInstance>();
            return (@init.RemoveExtendedPrefix = PathUtils.RemoveExtendedPrefix, @init.ProfileAddress = new Func<string, string>(Name => LocalGameData.Address(Name)), @init.GameFolder = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathIndie), @init.VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion), @init.Retention = new Func<ModMinecraft.McInstance, int>(Instance => Settings.Get<int>("VersionWorldBackupRetention", Instance: Instance)), @init.AutoEnabled = new Func<ModMinecraft.McInstance, bool>(Instance => Settings.Get<bool>("VersionWorldBackupAutoEnabled", Instance: Instance)), @init.Watchers = SnapshotWatchers, @init.WorldLabel = new Func<string, string>(Folder => WorldMetadata.ReadDetails(Folder)?.Name), @init.FormatSize = new Func<long, string>(Length => StringUtils.FormatByteSize(Length)), @init.Warn = new Action<Exception, string>((Ex, Message) => Logger.Warn(Ex, Message)), @init.UiBusy = new Func<bool>(() => WorldBackupsUi.IsBusy), @init.Dispatch = new Action<Action>(Work => ModBase.RunInUi(Work)), @init.NotifyChanged = PublishChanged, @init.OpenFolder = new Action<string>(Folder => ModBase.OpenExplorer(Folder)), @init.Hint = PublishHint, @init).@init;
        }

        private static IReadOnlyList<WorldBackupWatcher<ModMinecraft.McInstance>> SnapshotWatchers()
        {
            var Result = new List<WorldBackupWatcher<ModMinecraft.McInstance>>();
            foreach (var Item in ModWatcher.McWatcherList.ToArray())
            {
                if (Item is null)
                {
                    Result.Add(null);
                }
                else
                {
                    Result.Add(new WorldBackupWatcher<ModMinecraft.McInstance>()
                    {
                        Process = Item.GameProcess,
                        Instance = Item.Instance,
                        State = (WorldBackupWatcherState)(int)Item.State
                    });
                }
            }
            return Result;
        }

        private static void PublishChanged()
        {
            if (Changed is not null)
                ModBase.RunInUi(() => Changed?.Invoke());
        }
        internal static void PublishHint(string Text, bool IsError)
        {
            if (IsError)
            {
                ModMain.Hint(Text, ModMain.HintType.Red);
            }
            else
            {
                ModMain.Hint(Text);
            }
        }

        internal static string Store(string GameFolder)
        {
            return Service.Store(GameFolder);
        }
        internal static bool HasActiveGame(string GameFolder, Process ExitedProcess = null)
        {
            return Service.HasActiveGame(GameFolder, ExitedProcess);
        }
        private static JObject Metadata(string GameFolder, string Id)
        {
            return Service.Metadata(GameFolder, Id);
        }
        internal static List<JObject> Points(string GameFolder)
        {
            return Service.Points(GameFolder);
        }
        internal static List<JObject> Summaries(string GameFolder) => Service.Summaries(GameFolder);
        internal static void ClearSummaryCache() => Service.ClearSummaryCache();
        private static void ReadArchive(string Folder, JObject Point, CancellationToken Token, string Destination = null)
        {
            Service.ReadArchive(Folder, Point, Token, Destination);
        }
        internal static bool IsManualPoint(JObject Point)
        {
            return Service.IsManualPoint(Point);
        }
        internal static string CaptureWorld(string GameFolder, string Identifier, string Reason, int Retention, CancellationToken Token, Process ExitedProcess = null)
        {
            return Service.CaptureWorld(GameFolder, Identifier, Reason, Retention, Token, ExitedProcess);
        }
        internal static List<string> CaptureAll(ModMinecraft.McInstance Instance, string Reason, CancellationToken Token, Process ExitedProcess = null)
        {
            return Service.CaptureAll(Instance, Reason, Token, ExitedProcess);
        }
        internal static List<string> CaptureFolder(string GameFolder, string Reason, int Retention, CancellationToken Token, Process ExitedProcess = null)
        {
            return Service.CaptureFolder(GameFolder, Reason, Retention, Token, ExitedProcess);
        }
        internal static void RecoverPending(string GameFolder, CancellationToken Token)
        {
            Service.RecoverPending(GameFolder, Token);
        }
        internal static void RecoverBeforeLaunch(string GameFolder, CancellationToken Token)
        {
            Service.RecoverBeforeLaunch(GameFolder, Token);
        }
        internal static string Restore(string GameFolder, string Id, int Retention, CancellationToken Token, Action<int> BeforeCommit = null)
        {
            return Service.Restore(GameFolder, Id, Retention, Token, BeforeCommit);
        }
        internal static string CaptureWorldWithProgress(string GameFolder, string Identifier, string Reason, int Retention, CancellationToken Token, Action<double> Progress, Process ExitedProcess = null)
        {
            return Service.CaptureWorldWithProgress(GameFolder, Identifier, Reason, Retention, Token, Progress, ExitedProcess);
        }
        internal static List<string> CaptureAllWithProgress(ModMinecraft.McInstance Instance, string Reason, CancellationToken Token, Action<double> Progress)
        {
            return Service.CaptureAllWithProgress(Instance, Reason, Token, Progress);
        }
        internal static List<string> CaptureFolderWithProgress(string GameFolder, string Reason, int Retention, CancellationToken Token, Action<double> Progress, Process ExitedProcess = null)
        {
            return Service.CaptureFolderWithProgress(GameFolder, Reason, Retention, Token, Progress, ExitedProcess);
        }
        internal static string RestoreWithProgress(string GameFolder, string Id, int Retention, CancellationToken Token, Action<double> Progress, Action<int> BeforeCommit = null)
        {
            return Service.RestoreWithProgress(GameFolder, Id, Retention, Token, Progress, BeforeCommit);
        }
        internal static bool HasPendingRecovery(string GameFolder)
        {
            return Service.HasPendingRecovery(GameFolder);
        }
        internal static string DeletePoints(string GameFolder, IEnumerable<string> Ids)
        {
            return Service.DeletePoints(GameFolder, Ids);
        }
        internal static void OpenFolder(string GameFolder)
        {
            Service.OpenFolder(GameFolder);
        }
        internal static void NotifyChanged()
        {
            Service.NotifyChanged();
        }
        internal static void Track(ModMinecraft.McInstance Instance, Process Game)
        {
            Service.Track(Instance, Game);
        }
        internal static void OnProcessStopped(ModMinecraft.McInstance Instance, Process Game, bool NormalExit)
        {
            Service.OnProcessStopped(Instance, Game, NormalExit);
        }
        internal static void OnNormalExit(ModMinecraft.McInstance Instance, Process Game)
        {
            Service.OnNormalExit(Instance, Game);
        }
        internal static List<string> BeforeMinecraftChange(ModMinecraft.McInstance Instance, string OldMinecraft, string NewMinecraft, CancellationToken Token)
        {
            return Service.BeforeMinecraftChange(Instance, OldMinecraft, NewMinecraft, Token);
        }
    }

    internal sealed class WorldBackupsUi
    {
        internal static readonly WorldBackupWorkflow<ModMinecraft.McInstance> Service = new WorldBackupWorkflow<ModMinecraft.McInstance>(WorldBackups.Service, CreateHost());
        private static int Busy
        {
            get
            {
                return Service.BusyCount;
            }
        }
        internal static bool IsBusy
        {
            get
            {
                return Service.IsBusy;
            }
        }

        private static WorldBackupWorkflowHost<ModMinecraft.McInstance> CreateHost()
        {
            return new WorldBackupWorkflowHost<ModMinecraft.McInstance>()
            {
                GameFolder = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathIndie),
                VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                InstanceName = new Func<ModMinecraft.McInstance, string>(Instance => Instance.Name),
                Retention = new Func<ModMinecraft.McInstance, int>(Instance => Settings.Get<int>("VersionWorldBackupRetention", Instance: Instance)),
                LauncherBusy = new Func<bool>(() => ModLaunch.McLaunchLoader.State == ModBase.LoadState.Loading || ModpackUpdateFlow.IsUpdating || EnvironmentHistoryUi.IsBusy || ModMinecraft.McInstanceLock.IsAnyLocked || PageInstanceMod.UpdatingInstanceModFolders.Any()),
                SamePath = new Func<string, string, bool>((Left, Right) => PathUtils.IsSame(Left, Right)),
                Confirm = new Func<string, string, string, int>((Text, Title, Accept) => ModMain.MyMsgBox(Text, Title, Accept, "取消", IsWarn: true)),
                Hint = WorldBackups.PublishHint,
                Dispatch = new Action<Action>(Work => ModBase.RunInUi(Work)),
                StartWork = StartWork
            };
        }

        internal static void Start(ModMinecraft.McInstance Instance, string RestoreId = null, IEnumerable<string> DeleteIds = null, string WorldIdentifier = null)
        {
            Service.Start(Instance, RestoreId, DeleteIds, WorldIdentifier);
        }

        // 原 Loader/Worker、版本锁、停止事件和任务栏生命周期仍由 LF 平台管理。
        private static bool StartWork(ModMinecraft.McInstance Instance, WorldBackupWork Work)
        {
            var StepLoader = new ModLoader.LoaderTask<string, string>(Work.StepName, Task => {
                var Token = Task.CreateCancellationToken();
                Work.Progress = global::PCLCS.FileTaskProgress.ForTask(() => Task.Progress, Value => Task.Progress = Value);
                Work.Execute(Token, Output => Task.Output = Output);
            });
            var Loader = new ModLoader.LoaderCombo<string>(Work.Title, new[] { StepLoader }) { OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly };
            var Worker = new RedoableWorker(LoaderFileOperation.CreateWorkload(Loader, Instance.PathVersion));
            bool BrowseOnly = Work.SnapshotReservation();
            if (!(BrowseOnly ? ModMinecraft.McInstanceLock.TryAcquireSnapshot(Worker, Instance.PathVersion) : ModMinecraft.McInstanceLock.TryAcquire(Worker, Instance.PathVersion)))
                return false;
            Worker.Stopped += () => Work.Stopped();
            Worker.Start();
            ModLoader.LoaderTaskbarAdd(Loader);
            ModMain.FrmMain.BtnExtraDownload.ShowRefresh();
            return true;
        }
    }
}
