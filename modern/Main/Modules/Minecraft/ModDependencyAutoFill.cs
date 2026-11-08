// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using Inspector = PCL.ModDependencyInspector;
using PCLCS;
using AutoFillCore = PCLCS.ModDependencyAutoFillEngine<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ResourceVersion, PCL.ModNet.NetFile, PCL.ModDependencyAutoFill.Item, PCL.ModDependencyAutoFill.Plan, PCL.ModDependencyActivation.Item, PCL.ModDependencyOnline.LookupOptions, PCL.ModDependencyOnline.LookupCache>;

namespace PCL
{

    // 原 LF 对话框、版本模型、NetFile 与 Loader 的适配；规划、复查和事务在 C# 中执行。
    internal sealed class ModDependencyAutoFill
    {
        internal class Item : ModDependencyAutoFillItemData<PCL.ResourceVersion, Item>
        {
        }
        internal class Plan : ModDependencyAutoFillPlanData<Inspector.InspectionReport, Item, PCL.ModDependencyActivation.Item>
        {
        }
        internal static readonly AutoFillCore Service = new AutoFillCore(CreateHost());

        private class CandidateMismatchException : DependencyCandidateMismatchException
        {
            internal CandidateMismatchException(int Index, string Message) : base(Index, Message)
            {
            }
        }

        private static ModDependencyAutoFillHost<Inspector.InspectionReport, PCL.ResourceVersion, PCL.ModNet.NetFile, Plan, ModDependencyOnline.LookupOptions> CreateHost()
        {
            ModDependencyAutoFillHost<Inspector.InspectionReport, PCL.ResourceVersion, PCL.ModNet.NetFile, Plan, ModDependencyOnline.LookupOptions> @init = new ModDependencyAutoFillHost<Inspector.InspectionReport, PCL.ResourceVersion, PCL.ModNet.NetFile, Plan, ModDependencyOnline.LookupOptions>();
            @init.ApplyTransaction = (gameRoot, versionFolder, operations, token) =>
                ModpackUpdate.ApplyTransaction(gameRoot, versionFolder, operations, token);
            return (@init.ParseVersion = new Func<JObject, PCL.ResourceVersion>(Raw => PCL.ResourceVersion.FromPlatformJson(Raw, ResourceTypes.Mod)), @init.ResourceType = new Func<PCL.ResourceVersion, ResourceTypes>(Value => Value.ResourceType), @init.DownloadAvailable = new Func<PCL.ResourceVersion, bool>(Value => Value.DownloadAvailable), @init.Size = new Func<PCL.ResourceVersion, long>(Value => Value.Size), @init.GameVersions = new Func<PCL.ResourceVersion, IEnumerable<string>>(Value => Value.GameVersions), @init.ModLoaders = new Func<PCL.ResourceVersion, ModLoaders>(Value => Value.ModLoaders), @init.SetModLoaders = new Action<PCL.ResourceVersion, ModLoaders>((Value, Loaders) => Value.ModLoaders = Loaders), @init.Id = new Func<PCL.ResourceVersion, string>(Value => Value.Id), @init.FileName = new Func<PCL.ResourceVersion, string>(Value => Value.FileName), @init.Hash = new Func<PCL.ResourceVersion, string>(Value => Value.Hash), @init.Display = new Func<PCL.ResourceVersion, string>(Value => Value.Display), @init.DownloadUrls = new Func<PCL.ResourceVersion, List<string>>(Value => Value.DownloadUrls), @init.SetDownloadUrls = new Action<PCL.ResourceVersion, List<string>>((Value, Urls) => Value.DownloadUrls = Urls), @init.ReleaseType = new Func<PCL.ResourceVersion, int>(Value => (int)Value.ReleaseType), @init.ReleaseDate = new Func<PCL.ResourceVersion, DateTime>(Value => Value.ReleaseDate), @init.ToNetFile = new Func<PCL.ResourceVersion, string, string, ModLoaders, PCL.ModNet.NetFile>((Value, FileName, GameVersion, Loaders) => Value.ToNetFile(FileName, PCL.ResourceVersion.DownloadReason.Dependency, GameVersion, Loaders)), @init.Enrich = new Action<Inspector.InspectionReport, string, CancellationToken, ModDependencyOnline.LookupOptions>((Report, Root, Token, Options) => ModDependencyOnline.Enrich(Report, Root, Token, Options)), @init.DefaultRequest = ModDependencyOnline.DefaultRequest, @init.SafeName = new Func<string, string>(Name => PCL.InstanceResourceStore.SafeName(Name)), @init.EnsureWithin = new Action<string, string>((Root, Name) => PCL.InstanceResourceStore.EnsureWithin(Root, Name)), @init.DeleteStage = new Action<string, string>((Root, Stage) => PCL.InstanceResourceStore.DeleteStage(Root, Stage)), @init.FileHash = new Func<string, bool, string>((Name, Md5) => PCL.InstanceResourceUpdates.FileHash(Name, Md5)), @init.Download = DownloadFiles, @init.Info = new Action<string>(Message => Logger.Info(Message)), @init.Warn = new Action<Exception, string>((ErrorValue, Message) => Logger.Warn(ErrorValue, Message)), @init.RefreshOptions = new Action(() => PCL.ModBase.RunInUi(() => PCL.ModMain.FrmLaunchRight?.RefreshDependencyOptions())), @init.CandidateMismatch = new Func<int, string, DependencyCandidateMismatchException>((Index, Message) => new CandidateMismatchException(Index, Message)), @init).@init;
        }

        internal static Plan BuildPlan(string Root, Inspector.InspectionReport Report, CancellationToken Token, ModDependencyOnline.LookupOptions Options = null)
        {
            return Service.BuildPlan(Root, Report, Token, Options);
        }
        internal static int Install(Plan Plan, CancellationToken Token, Action<List<PCL.ModNet.NetFile>, CancellationToken> Download = null)
        {
            return Service.Install(Plan, Token, Download);
        }
        internal static PCL.ModLoader.LoaderCombo<int> CreateDownloadTask(Plan Plan, List<PCL.ModNet.NetFile> Files)
        {
            var Batch = Service.CreateDownloadTask(Plan, Files);
            var Steps = new List<PCL.ModLoader.LoaderBase>();
            foreach (var Entry in Batch.Steps)
                Steps.Add(new PCL.ModNet.LoaderDownload(Entry.Name, new[] { Entry.File }) { Block = Entry.Block, ProgressWeight = Entry.ProgressWeight });
            return new PCL.ModLoader.LoaderCombo<int>(Batch.Name, (IEnumerable<PCL.ModLoader.LoaderBase>)Steps) { OnStateChanged = PCL.ModDownloadLib.LoaderStateChangedHintOnly };
        }
        private static void DownloadFiles(Plan Plan, List<PCL.ModNet.NetFile> Files, CancellationToken Token)
        {
            if (PCL.ModMain.FrmMain is null)
            {
                var Work = new PCL.ModNet.LoaderDownload("下载必需前置", (IEnumerable<PCL.ModNet.NetFile>)Files);
                using (var Registration = Token.Register(Work.Cancel))
                {
                    Work.WaitForExit(c: Token);
                }
            }
            else
            {
                var Work = CreateDownloadTask(Plan, Files);
                PCL.ModBase.RunInUiWait(() =>
                    {
                        Work.Start(1);
                        PCL.ModLoader.LoaderTaskbarAdd(Work);
                        PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh();
                        PCL.ModMain.FrmMain.BtnExtraDownload.Ribble();
                    });
                using (var Registration = Token.Register(Work.Cancel))
                {
                    Work.WaitForExit(c: Token, StartIfNeeded: false);
                }
            }
        }
        internal static void ResetIndexLookups()
        {
            Service.ResetIndexLookups();
        }
    }
}
