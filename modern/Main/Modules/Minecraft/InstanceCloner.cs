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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
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
using CloneEngine = global::PCLCS.InstanceCloneEngine<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan>;
using CloneWorkflow = global::PCLCS.InstanceCloneWorkflow<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan>;
using ThrottleDebounce;

namespace PCL
{

    internal sealed class InstanceCloneOptions : global::PCLCS.InstanceCloneOptionsData
    {
    }
    internal sealed class InstanceCloneResult : global::PCLCS.InstanceCloneResultData
    {
    }
    internal sealed class InstanceCloneMigrationChange : global::PCLCS.InstanceCloneMigrationChangeData
    {
    }
    internal sealed class InstanceCloneMigrationPlan : global::PCLCS.InstanceCloneMigrationPlanData<global::PCL.InstanceCloneMigrationChange>
    {
    }

    // 复制采用独立文件和同卷临时目录；提交前重新校验来源，绝不重命名或修改来源文件。
    internal sealed class InstanceCloner
    {
        internal const global::System.String OriginName = "PCL/CloneOrigin.json";
        private static readonly global::System.Object Gate = new object();
        internal static readonly global::PCLCS.InstanceCloneEngine<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan> Service = new global::PCLCS.InstanceCloneEngine<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan>(global::PCL.InstanceCloner.Gate, global::PCL.InstanceCloner.CreateHost(), () => new global::PCL.InstanceCloneOptions(), () => new global::PCL.InstanceCloneResult(), () => new global::PCL.InstanceCloneMigrationChange(), () => new global::PCL.InstanceCloneMigrationPlan());
        private static global::PCLCS.InstanceCloneHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            return new global::PCLCS.InstanceCloneHost<global::PCL.ModMinecraft.McInstance>()
            {
                RemoveExtendedPrefix = global::MeloongCore.PathUtils.RemoveExtendedPrefix,
                RelativePath = global::PCL.ModpackUpdate.RelativePath,
                SafeName = global::PCL.InstanceResourceStore.SafeName,
                AddSlashSuffix = new Func<global::System.String, global::System.String>(Value => global::MeloongCore.PathUtils.AddSlashSuffix(Value)),
                EnsureWithin = new Action<global::System.String, global::System.String>((Root, Address) => global::PCL.InstanceResourceStore.EnsureWithin(Root, Address)),
                SamePath = new Func<global::System.String, global::System.String, global::System.Boolean>((Left, Right) => global::MeloongCore.PathUtils.IsSame(Left, Right)),
                HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft),
                HasRunningSessions = global::PCL.QuickPlayWorlds.HasRunningSessions,
                LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
                WorldBackupBusy = new Func<global::System.Boolean>(() => global::PCL.WorldBackups.IsBusy),
                HasActiveGame = (GameFolder) => global::PCL.WorldBackups.HasActiveGame(GameFolder),
                HasPending = new Func<global::System.String, global::System.Boolean>(VersionFolder => global::PCL.ModpackUpdate.Pending(VersionFolder).Any()),
                VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                VanillaName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Version.VanillaName),
                IsFool = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Version.IsFool),
                IsSnapshot = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Version.IsSnapshot),
                HasModLoaders = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Version.ModLoaders.Flags().Any()),
                ApiType = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => (Instance.DisplayType == global::PCL.ModMinecraft.McInstanceCardType.API)),
                IsolationMode = new Func<global::System.Int32>(() => global::PCL.Settings.Get<int>("LaunchArgumentIndieV2")),
                CaptureEnvironment = new Func<global::System.String, global::System.String, global::System.String, global::System.Threading.CancellationToken, global::System.String>((VersionFolder, GameRoot, Label, Token) => global::PCL.EnvironmentHistory.Capture(VersionFolder, GameRoot, Label, Token)),
                ApplyTransaction = new Action<global::System.String, global::System.String, global::System.Collections.Generic.List<global::System.Tuple<global::System.String, global::System.String, global::System.String>>, global::System.Threading.CancellationToken, global::System.Action<global::System.Int32>>((GameRoot, VersionFolder, Operations, Token, BeforeApply) => global::PCL.ModpackUpdate.ApplyTransaction(GameRoot, VersionFolder, Operations, Token, BeforeApply))
            };
        }
        internal static global::System.String NormalizeRoot(global::System.String Value)
        {
            return global::PCL.InstanceCloner.Service.NormalizeRoot(Value);
        }
        internal static void EnsureNoLinks(global::System.String Value)
        {
            global::PCL.InstanceCloner.Service.EnsureNoLinks(Value);
        }
        internal static global::System.String GameRootForInstance(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.InstanceCloner.Service.GameRootForInstance(Instance);
        }
        internal static global::System.String Digest(global::System.String Address, global::System.Threading.CancellationToken Token)
        {
            return global::PCL.InstanceCloner.Service.Digest(Address, Token);
        }
        internal static global::PCL.InstanceCloneResult CloneInstance(global::PCL.ModMinecraft.McInstance Instance, global::System.String NewName, global::PCL.InstanceCloneOptions Options, global::System.Threading.CancellationToken Token)
        {
            return global::PCL.InstanceCloner.Service.CloneInstance(Instance, NewName, Options, Token);
        }
        internal static global::PCL.InstanceCloneResult Clone(global::System.String VersionFolder, global::System.String GameRoot, global::System.String NewName, global::PCL.InstanceCloneOptions Options, global::System.Threading.CancellationToken Token, global::System.Action<global::System.Int32> AfterFileCopied = null)
        {
            return global::PCL.InstanceCloner.Service.Clone(VersionFolder, GameRoot, NewName, Options, Token, AfterFileCopied);
        }
        internal static InstanceCloneResult CloneWithProgress(string VersionFolder, string GameRoot, string NewName, InstanceCloneOptions Options, CancellationToken Token, Action<double> Progress, Action<int> AfterFileCopied = null)
        {
            return Service.CloneWithProgress(VersionFolder, GameRoot, NewName, Options, Token, Progress, AfterFileCopied);
        }
        internal static InstanceCloneResult CloneInstanceWithProgress(ModMinecraft.McInstance Instance, string NewName, InstanceCloneOptions Options, CancellationToken Token, Action<double> Progress)
        {
            return Service.CloneInstanceWithProgress(Instance, NewName, Options, Token, Progress);
        }
        internal static global::System.String SourceVersionFolder(global::System.String CloneFolder)
        {
            return global::PCL.InstanceCloner.Service.SourceVersionFolder(CloneFolder);
        }
        internal static global::PCL.InstanceCloneMigrationPlan PreviewMigration(global::System.String CloneFolder, global::System.String SourceGameRoot, global::System.Threading.CancellationToken Token)
        {
            return global::PCL.InstanceCloner.Service.PreviewMigration(CloneFolder, SourceGameRoot, Token);
        }
        internal static global::System.String ApplyMigration(global::PCL.InstanceCloneMigrationPlan Plan, global::System.Threading.CancellationToken Token, global::System.Func<global::System.String> CaptureBefore = null, global::System.Action<global::System.String, global::System.String> BeforeMinecraftChange = null, global::System.Action<global::System.Int32> BeforeApply = null)
        {
            return global::PCL.InstanceCloner.Service.ApplyMigration(Plan, Token, CaptureBefore, BeforeMinecraftChange, BeforeApply);
        }
    }

    internal sealed class InstanceClonerUi
    {
        internal static event ChangedEventHandler Changed;

        internal delegate void ChangedEventHandler();
        internal static readonly global::PCLCS.InstanceCloneWorkflow<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan> Service = new global::PCLCS.InstanceCloneWorkflow<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceCloneOptions, global::PCL.InstanceCloneResult, global::PCL.InstanceCloneMigrationChange, global::PCL.InstanceCloneMigrationPlan>(global::PCL.InstanceCloner.Service, global::PCL.InstanceClonerUi.CreateHost());
        internal static global::System.Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.String, global::System.Threading.CancellationToken> BeforeMinecraftChangeHandler
        {
            get
            {
                return global::PCL.InstanceClonerUi.Service.BeforeMinecraftChangeHandler;
            }
            set
            {
                global::PCL.InstanceClonerUi.Service.BeforeMinecraftChangeHandler = value;
            }
        }
        private static int Busy
        {
            get
            {
                return global::PCL.InstanceClonerUi.Service.BusyCount;
            }
        }
        internal static bool IsBusy
        {
            get
            {
                return global::PCL.InstanceClonerUi.Service.IsBusy;
            }
        }
        private static global::PCLCS.InstanceCloneWorkflowHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            return new global::PCLCS.InstanceCloneWorkflowHost<global::PCL.ModMinecraft.McInstance>()
            {
                VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                Name = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name),
                CreateInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(VersionFolder => new global::PCL.ModMinecraft.McInstance(VersionFolder)),
                HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft),
                HasRunningSessions = global::PCL.QuickPlayWorlds.HasRunningSessions,
                LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
                WorldBackupBusy = new Func<global::System.Boolean>(() => global::PCL.WorldBackups.IsBusy),
                HasActiveGame = (GameFolder) => global::PCL.WorldBackups.HasActiveGame(GameFolder),
                EnvironmentBusy = new Func<global::System.Boolean>(() => global::PCL.EnvironmentHistoryUi.IsBusy),
                Updating = new Func<global::System.Boolean>(() => global::PCL.ModpackUpdateFlow.IsUpdating),
                AnyLocked = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceLock.IsAnyLocked),
                UpdatingModFolders = new Func<global::System.Boolean>(() => global::PCL.PageInstanceMod.UpdatingInstanceModFolders.Any()),
                Hint = new Action<global::System.String, global::System.Boolean>((Message, Success) => global::PCL.ModMain.Hint(Message, (Success) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Red))),
                Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warning)),
                Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                ResetSettingsCache = new Action<global::PCL.ModMinecraft.McInstance>(Instance => Instance.ResetSettingsCache()),
                CaptureInstance = global::PCL.EnvironmentHistory.CaptureInstance,
                RefreshInstances = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\")),
                NotifyChanged = global::PCL.InstanceClonerUi.PublishChanged,
                StartWork = global::PCL.InstanceClonerUi.Run
            };
        }
        private static void PublishChanged()
        {
            Changed?.Invoke();
        }
        private static global::System.Boolean CanStart(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.InstanceClonerUi.Service.CanStart(Instance);
        }
        internal static void Start(global::PCL.ModMinecraft.McInstance Instance, global::System.String NewName, global::PCL.InstanceCloneOptions Options)
        {
            global::PCL.InstanceClonerUi.Service.Start(Instance, NewName, Options);
        }
        internal static void Migrate(global::PCL.ModMinecraft.McInstance CloneInstance)
        {
            global::PCL.InstanceClonerUi.Service.Migrate(CloneInstance);
        }
        // 原 Loader、双实例锁、废弃 Worker 及停止／任务栏生命周期。
        private static void Run(global::PCL.ModMinecraft.McInstance Instance, global::PCLCS.InstanceCloneWork Work)
        {
            var StepLoader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.String>(Work.StepName, Task => {
                var Token = Task.CreateCancellationToken();
                Work.Progress = global::PCLCS.FileTaskProgress.ForTask(() => Task.Progress, Value => Task.Progress = Value);
                Work.Execute(Token, Output => Task.Output = Output);
            });
            var Loader = new global::PCL.ModLoader.LoaderCombo<global::System.String>(Work.Title, new[] { StepLoader }) { OnStateChanged = global::PCL.ModDownloadLib.LoaderStateChangedHintOnly };
            var Abandoned = default(global::System.Boolean);
            var Workload = global::PCL.LoaderFileOperation.CreateWorkload(Loader, Instance.PathVersion);
            var Worker = new global::MeloongCore.RedoableWorker(() => { if (!(Abandoned)) Workload(); });
            if (!(Work.LockSource ? global::PCL.ModMinecraft.McInstanceLock.TryAcquire(Worker, Instance.PathVersion) : global::PCL.ModMinecraft.McInstanceLock.TryAcquireSnapshot(Worker, Instance.PathVersion)))
            {
                Work.ReleaseBusy();
                return;
            }
            if (Work.LockSource)
            {
                try
                {
                    if (!(global::PCL.ModMinecraft.McInstanceLock.TryAcquire(Worker, global::PCL.InstanceCloner.SourceVersionFolder(Instance.PathVersion))))
                    {
                        Abandoned = true;
                        Worker.Start();
                        Work.ReleaseBusy();
                        return;
                    }
                }
                catch (global::System.Exception ex)
                {
                    Abandoned = true;
                    Worker.Start();
                    Work.ReleaseBusy();
                    Work.SourceError(ex);
                    return;
                }
            }
            Worker.Stopped += () => Work.Stopped();
            Worker.Start();
            global::PCL.ModLoader.LoaderTaskbarAdd(Loader);
            global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh();
        }
    }
}