// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL
{
    internal sealed class RecentEnvironmentChange : RecentEnvironmentChangeData
    {
    }

    internal sealed class RecentEnvironmentChangesReport : RecentEnvironmentChangesReportData<RecentEnvironmentChange>
    {
    }

    // 只对比最近正常运行的环境清单。存档、截图、账号、地图标记不属于崩溃变更对照范围。
    internal sealed class RecentEnvironmentChanges
    {
        private static readonly RecentEnvironmentChangesEngine<ModMinecraft.McInstance, RecentEnvironmentChange, RecentEnvironmentChangesReport> Service = new RecentEnvironmentChangesEngine<ModMinecraft.McInstance, RecentEnvironmentChange, RecentEnvironmentChangesReport>(EnvironmentHistory.Service, CreateHost(), () => new RecentEnvironmentChange(), () => new RecentEnvironmentChangesReport());
        private static RecentEnvironmentHost<ModMinecraft.McInstance> CreateHost()
        {
            return new RecentEnvironmentHost<ModMinecraft.McInstance>()
            {
                NormalizeRoot = InstanceCloner.NormalizeRoot,
                RelativePath = ModpackUpdate.RelativePath,
                EnsureNoLinks = InstanceCloner.EnsureNoLinks,
                EnsureWithin = new Action<string, string>((Root, Address) => InstanceResourceStore.EnsureWithin(Root, Address)),
                Target = ModpackUpdate.Target,
                Digest = InstanceCloner.Digest,
                SamePath = new Func<string, string, bool>((Left, Right) => PathUtils.IsSame(Left, Right)),
                VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                GameRootForInstance = InstanceCloner.GameRootForInstance
            };
        }
        internal static RecentEnvironmentChangesReport BuildInstance(ModMinecraft.McInstance Instance, CancellationToken Token)
        {
            return Service.BuildInstance(Instance, Token);
        }
        internal static RecentEnvironmentChangesReport Build(string VersionFolder, string GameRoot, CancellationToken Token)
        {
            return Service.Build(VersionFolder, GameRoot, Token);
        }
        internal static string AppendAnalysis(string Analysis, RecentEnvironmentChangesReport Report)
        {
            return Service.AppendAnalysis(Analysis, Report);
        }
    }
}