// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MeloongCore;
using PCLCS;

namespace PCL
{

    internal class QuickPlaySession : QuickPlaySessionData
    {
    }

    internal class RecentWorld : RecentWorldData
    {
    }

    internal static class QuickPlayWorlds
    {
        private readonly static QuickPlayHistory HistoryService = new QuickPlayHistory(LocalGameData.Service, LocalGameData.Gate, () => Settings.Get<bool>("LaunchRecentWorlds"), (Root, FileName) => InstanceResourceStore.EnsureWithin(Root, FileName), (First, Second) => PathUtils.IsSame(First, Second), VersionPath => ModModpackMode.AllowsInstance(VersionPath), (ErrorValue, Message) => Logger.Warn(ErrorValue, Message), () => new RecentWorld());
        private readonly static QuickPlayFlow FlowService = new QuickPlayFlow(HistoryService, LocalGameData.Service, LocalGameData.Gate, () => Settings.Get<bool>("LaunchRecentWorlds"), (Root, FileName) => InstanceResourceStore.EnsureWithin(Root, FileName), (ErrorValue, Message) => Logger.Warn(ErrorValue, Message), () => new QuickPlaySession(), () => new RecentWorld());
        private readonly static RecentWorldQuickPlay<ModMinecraft.McInstance> LaunchService = new RecentWorldQuickPlay<ModMinecraft.McInstance>(new RecentWorldQuickPlayHost<ModMinecraft.McInstance>()
        {
            VersionPath = new Func<ModMinecraft.McInstance, string>(Value => Value.PathVersion),
            GameFolder = new Func<ModMinecraft.McInstance, string>(Value => Value.PathIndie),
            AllowsInstance = new Func<string, bool>(Value => ModModpackMode.AllowsInstance(Value)),
            Load = new Action<ModMinecraft.McInstance>(Value => Value.Load()),
            Supported = Supported,
            WorldPath = WorldPath,
            HasRunningGame = new Func<bool>(() => ModWatcher.HasRunningMinecraft),
            NavigateLaunch = new Action(() => ModMain.FrmMain.PageChange((FormMain.PageStackData)FormMain.PageType.Launch)),
            Launch = new Func<ModMinecraft.McInstance, string, bool>((Value, Identifier) => ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions() { Instance = Value, ServerIp = "", QuickPlayWorld = Identifier })),
            HintError = new Action<string>(Message => ModMain.Hint(Message, ModMain.HintType.Red))
        });
        internal static bool Supported(ModMinecraft.McInstance Instance)
        {
            return FlowService.Supported(Adapt(Instance));
        }
        internal static string WorldPath(string GameFolder, string Identifier)
        {
            return QuickPlayWorldRules.WorldPath(GameFolder, Identifier, (Root, World) => InstanceResourceStore.EnsureWithin(Root, World));
        }
        internal static bool Start(ModMinecraft.McInstance Instance, string Identifier)
        {
            return LaunchService.Start(Instance, Identifier);
        }
        internal static QuickPlaySession Prepare(ModMinecraft.McInstance Instance, string Identifier, List<string> Arguments, bool SaveBatch)
        {
            return (QuickPlaySession)FlowService.Prepare(Adapt(Instance), Identifier, Arguments, SaveBatch);
        }
        private static void RemoveArguments(List<string> Arguments, IEnumerable<string> Names)
        {
            QuickPlayWorldRules.RemoveArguments(Arguments, Names);
        }
        internal static void Track(QuickPlaySession Session, Process Game)
        {
            FlowService.Track(Session, Game);
        }
        internal static void Finish(QuickPlaySession Session)
        {
            FlowService.Finish(Session);
        }
        private static void ReadSessionLog(QuickPlaySession Session)
        {
            HistoryService.ReadSessionLog(Session);
        }
        internal static void Remember(RecentWorld World)
        {
            HistoryService.Remember(World);
        }
        internal static void DiscoverExistingWorlds(ModMinecraft.McInstance Instance)
        {
            FlowService.DiscoverExistingWorlds(Adapt(Instance));
        }
        private static List<RecentWorld> ReadEntries()
        {
            return HistoryService.ReadEntries().Cast<RecentWorld>().ToList();
        }
        internal static List<RecentWorld> Recent()
        {
            return HistoryService.Recent().Cast<RecentWorld>().ToList();
        }
        internal static void RecoverExitedSessions()
        {
            FlowService.RecoverExitedSessions();
        }
        internal static void OnEnabledChanged(object Value)
        {
            ModBase.RunInUi(() => ModMain.FrmLaunchRight?.RefreshRecentWorlds());
        }
        internal static bool HasRunningSessions()
        {
            return FlowService.HasRunningSessions();
        }
        private static IQuickPlayInstance Adapt(ModMinecraft.McInstance Instance)
        {
            return Instance is null ? null : new QuickPlayInstance(Instance);
        }
        private sealed class QuickPlayInstance : IQuickPlayInstance
        {
            private readonly ModMinecraft.McInstance Owner;
            internal QuickPlayInstance(ModMinecraft.McInstance Instance)
            {
                Owner = Instance;
            }
            public string VersionPath
            {
                get
                {
                    return Owner.PathVersion;
                }
            }
            public string GameFolder
            {
                get
                {
                    return Owner.PathIndie;
                }
            }
            public bool VersionValid
            {
                get
                {
                    return Owner.Version.Valid;
                }
            }
            public int VanillaMajor
            {
                get
                {
                    return Owner.Version.Vanilla.Major;
                }
            }
            public bool IsSnapshot
            {
                get
                {
                    return Owner.Version.IsSnapshot;
                }
            }
            public DateTime ReleaseTime
            {
                get
                {
                    return Owner.ReleaseTime;
                }
            }
            public bool HasSavedIsolation
            {
                get
                {
                    return Settings.HasSaved("VersionArgumentIndieV2", Instance: Owner);
                }
            }
            public bool IsolationEnabled
            {
                get
                {
                    return Settings.Get<bool>("VersionArgumentIndieV2", Instance: Owner);
                }
            }
        }
    }
}