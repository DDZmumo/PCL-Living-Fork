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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using global::System.Windows;
using System.Windows;
using global::System.Windows.Controls;
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
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using ModeCore = global::PCLCS.ModpackModeController<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance>;
using ThrottleDebounce;

namespace PCL
{

    // LF 原路由/控件/设置/Loader 适配。模式判断、界面决策与异步作用域流程在 C# 中。
    internal static class ModModpackMode
    {
        internal const global::System.String SettingKey = "LivingModpackMode";
        internal const global::System.String ExportMarkerKey = "LivingModpackExported";
        private readonly static global::PCLCS.ModpackModeController<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance> Service = new global::PCLCS.ModpackModeController<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance>(global::PCL.ModModpackMode.CreateHost());

        private static global::PCLCS.ModpackModeHost<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            global::PCLCS.ModpackModeHost<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance> @init = new global::PCLCS.ModpackModeHost<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance>();
            return (@init.Routes = new global::PCLCS.ModpackModeRoutes()
            {
                SetupLaunch = (global::System.Int32)global::PCL.FormMain.PageSubType.SetupLaunch,
                SetupSystem = (global::System.Int32)global::PCL.FormMain.PageSubType.SetupSystem,
                Download = (global::System.Int32)global::PCL.FormMain.PageType.Download,
                Other = (global::System.Int32)global::PCL.FormMain.PageType.Other,
                Favorites = (global::System.Int32)global::PCL.FormMain.PageType.Favorites,
                Link = (global::System.Int32)global::PCL.FormMain.PageType.Link,
                SetupPage = (global::System.Int32)global::PCL.FormMain.PageType.Setup,
                InstanceBackups = (global::System.Int32)global::PCL.FormMain.PageSubType.InstanceBackups,
                InstanceSaves = (global::System.Int32)global::PCL.FormMain.PageSubType.InstanceSaves,
                InstanceExport = (global::System.Int32)global::PCL.FormMain.PageSubType.InstanceExport,
                InstanceOverall = (global::System.Int32)global::PCL.FormMain.PageSubType.InstanceOverall
            }, @init.Setting = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)), @init.SetSetting = new Action<global::System.String, global::System.Boolean>((Key, Value) => global::PCL.Settings.Set(Key, Value)), @init.ForceShow = new Func<global::System.Boolean>(() => global::PCL.PageSetupUI.HiddenForceShow), @init.CurrentPageIsSetup = new Func<global::System.Boolean>(() => (global::PCL.ModMain.FrmMain is not null && ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Setup))), @init.BaseFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.Base), @init.MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected), @init.LauncherDisplayName = new Func<global::System.String>(() => global::PCL.ModBase.LauncherDisplayName), @init.SystemPage = new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmSetupSystem), @init.SetSystemPage = new Action<global::PCL.MyPageRight>(Page => global::PCL.ModMain.FrmSetupSystem = (global::PCL.PageSetupSystem)Page), @init.CreateSystemPage = new Func<global::PCL.MyPageRight>(() => new global::PCL.PageSetupSystem()), @init.CreateCompactPage = new Func<global::PCL.MyPageRight>(() => new global::PCL.PageSetupModpack()), @init.MainWindow = new Func<global::System.Windows.Window>(() => global::PCL.ModMain.FrmMain), @init.Header = global::PCL.ModModpackMode.HeaderView, @init.Setup = global::PCL.ModModpackMode.SetupView, @init.Launch = global::PCL.ModModpackMode.LaunchView, @init.Instance = global::PCL.ModModpackMode.InstanceView, @init.Message = new Func<global::PCLCS.ModpackMessage, global::System.Int32>(Message => global::PCL.ModMain.MyMsgBox(Message.Text, Message.Title, Message.Button1, Message.Button2, IsWarn: Message.IsWarn, ForceWait: Message.ForceWait)), @init.RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)), @init.RunInNewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)), @init.RefreshHidden = new Action(() => global::PCL.PageSetupUI.HiddenRefresh()), @init.UpdateTitle = new Action(() => global::PCL.ModMain.FrmMain?.UpdateTitleBar()), @init.RefreshLaunchButtons = new Action(() => global::PCL.ModMain.FrmLaunchLeft?.RefreshButtonsUI()), @init.ChangeToSystemPage = new Action(() => global::PCL.ModMain.FrmSetupLeft?.PageChange(global::PCL.FormMain.PageSubType.SetupSystem, true)), @init.PackFolder = global::PCL.MinecraftFolderPolicy.PackFolder, @init.IsPackVersion = global::PCL.MinecraftFolderPolicy.IsPackVersion, @init.CreateFolder = new Func<global::System.String, global::System.String, global::PCL.ModMinecraft.McFolder>((Name, Folder) => new global::PCL.ModMinecraft.McFolder() { Name = Name, Location = Folder, Type = global::PCL.ModMinecraft.McFolder.Types.Vanilla }), @init.ReadSelectedName = new Func<global::System.String, global::System.String>(Folder => global::PCL.ModBase.ReadIni(Folder + "PCL.ini", "Version")), @init.ForceInstanceRefresh = new Action(() => global::PCL.ModMinecraft.McInstanceListForceRefresh = true), @init.StartFolderList = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.Start(IsForceRestart: true)), @init.RefreshSelect = new Action(() => global::PCL.ModMain.FrmSelectLeft?.RefreshForModpackMode()), @init.WaitFolderList = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.WaitForExit()), @init.RunInstanceList = new Action<global::System.String>(Folder => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, Folder, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\", WaitForExit: true)), @init.Instances = new Func<global::System.Collections.Generic.IEnumerable<global::PCL.ModMinecraft.McInstance>>(() => global::PCL.ModMinecraft.McInstanceList.Values.SelectMany(List => List)), @init.InstanceError = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => (Instance.State == global::PCL.ModMinecraft.McInstance.McInstanceState.Error)), @init.InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name), @init.SelectInstance = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.ModMinecraft.McInstanceSelected = Instance), @init.RefreshLaunchPage = new Action<global::System.Boolean, global::System.Boolean>((KeepInput, Anim) => global::PCL.ModMain.FrmLaunchLeft?.RefreshPage(KeepInput, Anim)), @init.WriteIni = new Action<global::System.String, global::System.String, global::System.Object>((Name, Key, Value) => global::PCL.ModBase.WriteIni(Name, Key, Conversions.ToString(Value))), @init).@init;
        }
        private static global::PCLCS.ModpackModeHeaderView HeaderView(global::System.Windows.Window Main)
        {
            global::PCL.FormMain View = (global::PCL.FormMain)Main;
            return new global::PCLCS.ModpackModeHeaderView()
            {
                Panel = View.PanTitleSelect,
                Download = View.BtnTitleSelect1,
                Link = View.BtnTitleSelect2,
                Setup = View.BtnTitleSelect3,
                Other = View.BtnTitleSelect4,
                Favorites = View.BtnTitleSelect5,
                SetupText = new Action<global::System.String>(Value => View.BtnTitleSelect3.Text = Value)
            };
        }
        private static global::PCLCS.ModpackModeSetupView SetupView()
        {
            var View = global::PCL.ModMain.FrmSetupLeft;
            if (View is null)
                return (global::PCLCS.ModpackModeSetupView)null;
            return new global::PCLCS.ModpackModeSetupView()
            {
                Launch = View.ItemLaunch,
                System = View.ItemSystem,
                UI = View.ItemUI,
                Link = View.ItemLink,
                ItemsPanel = View.PanItem,
                SystemTitle = new Action<global::System.String>(Value => View.ItemSystem.Title = Value),
                SystemButtons = new Func<global::System.Collections.Generic.IEnumerable<global::System.Windows.UIElement>>(() => View.ItemSystem.Buttons)
            };
        }
        private static global::PCLCS.ModpackModeLaunchView LaunchView()
        {
            var View = global::PCL.ModMain.FrmLaunchLeft;
            if (View is null)
                return (global::PCLCS.ModpackModeLaunchView)null;
            return new global::PCLCS.ModpackModeLaunchView() { Version = View.BtnVersion, More = View.BtnMore };
        }
        private static global::PCLCS.ModpackModeInstanceView InstanceView()
        {
            global::PCLCS.ModpackModeInstanceView @init = new global::PCLCS.ModpackModeInstanceView();
            return (@init.Export = global::PCL.ModMain.FrmInstanceLeft?.ItemExport, @init.ModDownload = global::PCL.ModMain.FrmInstanceMod?.BtnManageDownload, @init.ModHintDownload = global::PCL.ModMain.FrmInstanceMod?.BtnHintDownload, @init.DisabledVersion = global::PCL.ModMain.FrmInstanceModDisabled?.BtnVersion, @init.RefreshOverview = new Action(() => global::PCL.ModMain.FrmInstanceOverall?.RefreshModpackVisibility()), @init.RefreshModify = new Action(() => global::PCL.ModMain.FrmDownloadModify?.RefreshModpackRestrictions()), @init.ModEmptyHint = new Action<global::System.String>(Text => global::PCL.ModMain.FrmInstanceMod.LabEmptyHint.Text = Text), @init.DisabledHint = new Action<global::System.String>(Text => global::PCL.ModMain.FrmInstanceModDisabled.LabModHint.Text = Text), @init.RefreshDisabledDownload = new Action(() => global::PCL.ModMain.FrmInstanceModDisabled.BtnDownload_Loaded()), @init).@init;
        }

        private static global::PCL.PageSetupModpack CompactSettings
        {
            get
            {
                return (global::PCL.PageSetupModpack)global::PCL.ModModpackMode.Service.CompactSettings;
            }
            set
            {
                global::PCL.ModModpackMode.Service.CompactSettings = value;
            }
        }
        private static int FolderScopeRevision
        {
            get
            {
                return global::PCL.ModModpackMode.Service.FolderScopeRevision;
            }
            set
            {
                global::PCL.ModModpackMode.Service.FolderScopeRevision = value;
            }
        }
        internal static bool CanEnable
        {
            get
            {
                return global::PCL.ModModpackMode.Service.CanEnable;
            }
        }
        internal static bool Enabled
        {
            get
            {
                return global::PCL.ModModpackMode.Service.Enabled;
            }
        }
        internal static global::PCL.MyPageRight LauncherSettingsPage()
        {
            return global::PCL.ModModpackMode.Service.LauncherSettingsPage();
        }
        internal static global::PCL.FormMain.PageSubType NormalizeSetupPage(global::PCL.FormMain.PageSubType Page)
        {
            if (Page == FormMain.PageSubType.SetupUpdate || Page == FormMain.PageSubType.SetupPersonal) return Page;
            return ((global::PCL.FormMain.PageSubType)(global::PCL.ModModpackMode.Service.NormalizeSetupPage((global::System.Int32)Page)));
        }
        internal static global::System.Boolean BlocksPage(global::PCL.FormMain.PageType Page)
        {
            return global::PCL.ModModpackMode.Service.BlocksPage((global::System.Int32)Page);
        }
        internal static global::System.Boolean ConfirmChange(global::System.Boolean Current, global::System.Boolean Desired, global::System.Func<global::System.Boolean, global::System.Boolean> Confirm)
        {
            return global::PCLCS.ModpackModeController<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance>.ConfirmChange(Current, Desired, Confirm);
        }
        internal static global::System.Boolean ShowWarning(global::System.Boolean Enable)
        {
            return global::PCL.ModModpackMode.Service.ShowWarning(Enable);
        }
        internal static void Toggle(global::PCL.MyCheckBox Check, global::System.Boolean User)
        {
            global::PCL.ModModpackMode.Service.Toggle(() => Check.Checked, Value => Check.Checked = Value, User);
        }
        internal static void OnChanged(global::System.Object Value)
        {
            global::PCL.ModModpackMode.Service.OnChanged(Value);
        }
        internal static global::System.String DisplayTitle(global::System.String Title)
        {
            return global::PCL.ModModpackMode.Service.DisplayTitle(Title);
        }
        internal static void ApplyVisibility(global::PCL.FormMain Main = null)
        {
            global::PCL.ModModpackMode.Service.ApplyVisibility(Main);
        }
        internal static global::PCL.FormMain.PageSubType NormalizeInstancePage(global::PCL.FormMain.PageSubType Page)
        {
            return ((global::PCL.FormMain.PageSubType)(global::PCL.ModModpackMode.Service.NormalizeInstancePage((global::System.Int32)Page)));
        }
        internal static global::System.Boolean CanChangeMinecraft(global::System.String Current, global::System.String Requested)
        {
            return global::PCL.ModModpackMode.Service.CanChangeMinecraft(Current, Requested);
        }
        internal static global::System.Boolean ConfirmModAction(global::System.String ActionName, global::System.Func<global::System.String, global::System.Boolean> Confirm = null)
        {
            return global::PCL.ModModpackMode.Service.ConfirmModAction(ActionName, Confirm);
        }
        internal static void ApplyInstanceVisibility()
        {
            global::PCL.ModModpackMode.Service.ApplyInstanceVisibility();
        }
        private static void HideButtonColumn(global::System.Windows.FrameworkElement Button)
        {
            global::PCL.ModModpackMode.Service.HideButtonColumn(Button);
        }
        internal static void ApplyHeader(global::System.Windows.Controls.Panel Panel, global::System.Windows.UIElement Download, global::System.Windows.UIElement Link, global::PCL.MyRadioButton Setup, global::System.Windows.UIElement Other)
        {
            global::PCL.ModModpackMode.Service.ApplyHeader(new global::PCLCS.ModpackModeHeaderView()
            {
                Panel = Panel,
                Download = Download,
                Link = Link,
                Setup = Setup,
                Other = Other,
                SetupText = new Action<global::System.String>(Value => Setup.Text = Value)
            });
        }
        internal static void ApplyLaunchLayout(global::PCL.PageLaunchLeft Target)
        {
            global::PCLCS.ModpackModeController<global::PCL.MyPageRight, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McInstance>.ApplyLaunchLayout(Target.BtnMore);
        }
        internal static global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder> CreatePackFolderList()
        {
            return global::PCL.ModModpackMode.Service.CreatePackFolderList();
        }
        internal static global::System.Boolean AllowsInstance(global::System.String Folder)
        {
            return global::PCL.ModModpackMode.Service.AllowsInstance(Folder);
        }
        private static void RefreshFolderScope()
        {
            global::PCL.ModModpackMode.Service.RefreshFolderScope();
        }
        internal static void ConfigureExport(global::System.String ExportRoot, global::System.Boolean IncludeLauncher, global::System.Boolean PackMode)
        {
            global::PCL.ModModpackMode.Service.ConfigureExport(ExportRoot, IncludeLauncher, PackMode);
        }
    }
}
