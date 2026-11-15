// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

// LF-only ModModpackMode.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// under MPL-2.0 for PCLLF-original code; absent from fixed PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// No equivalent CE stable 1a7653127d578efdb2a9388999a3fdc67d9e49d5 pack-mode
// implementation is reused. Original LF page identities and WPF views are hosted.
public sealed class ModpackModeController<TPage, TFolder, TInstance>
    where TPage : class where TInstance : class {
    public const string SettingKey = "LivingModpackMode", ExportMarkerKey = "LivingModpackExported";
    private readonly ModpackModeHost<TPage, TFolder, TInstance> host;
    public TPage? CompactSettings;
    public int FolderScopeRevision;
    public ModpackModeController(ModpackModeHost<TPage, TFolder, TInstance> host) { this.host = host; }
    public bool CanEnable => host.Setting(ExportMarkerKey);
    public bool Enabled => CanEnable && host.Setting(SettingKey);
    public TPage LauncherSettingsPage() {
        if (!Enabled) {
            TPage? page = host.SystemPage();
            if (page is null) { page = host.CreateSystemPage(); host.SetSystemPage(page); }
            return page;
        }
        return CompactSettings ??= host.CreateCompactPage();
    }
    public int NormalizeSetupPage(int page) => Enabled && page != host.Routes.SetupLaunch && page != host.Routes.SetupSystem ? host.Routes.SetupSystem : page;
    public bool BlocksPage(int page) => Enabled && (page == host.Routes.Download || page == host.Routes.Other || page == host.Routes.Link || page == host.Routes.Favorites);
    public static bool ConfirmChange(bool current, bool desired, Func<bool, bool> confirm) => current == desired ? current : confirm(desired) ? desired : current;
    private int Message(string text, string title, string button1 = "确定", string button2 = "", bool warn = false) =>
        host.Message(new ModpackMessage { Text = text, Title = title, Button1 = button1, Button2 = button2, IsWarn = warn });
    public bool ShowWarning(bool enable) {
        string message = enable ?
            "开启 PCL Living Fork 整合包模式后，将隐藏下载和更多，版本选择仅显示本目录的整合包游戏，不提供新建、添加或导入入口，设置也会精简。\r\n\r\n原有的其他游戏文件夹和收藏记录会保留。之后可在 设置 → 启动器 中关闭此模式。是否继续？" :
            "关闭 PCL Living Fork 整合包模式后，将恢复普通启动器界面，允许选择版本并使用下载、更多和完整设置。\r\n\r\n修改版本或组件可能影响整合包正常运行；关闭模式不会删除游戏文件。是否继续？";
        return Message(message, enable ? "开启整合包模式" : "关闭整合包模式", enable ? "开启" : "关闭", "取消", true) == 1;
    }
    public void Toggle(Func<bool> readChecked, Action<bool> writeChecked, bool user) {
        if (!user) return;
        if (readChecked() && !CanEnable) {
            writeChecked(false);
            Message("只有导出并附带 PCL Living Fork 启动器的整合包才能开启此模式。\r\n\r\n当前目录的 PCL\\Setup.ini 中没有整合包导出标记。请先导出整合包，再运行解压后的启动器。", "无法开启整合包模式");
            return;
        }
        bool current = Enabled, confirmed = ConfirmChange(current, readChecked(), ShowWarning);
        if (confirmed != current) host.SetSetting(SettingKey, confirmed);
        writeChecked(Enabled);
    }
    public void OnChanged(object? value) => host.RunInUi(() => {
        host.RefreshHidden(); ApplyVisibility(); host.UpdateTitle(); host.RefreshLaunchButtons(); RefreshFolderScope();
        if (host.CurrentPageIsSetup()) host.ChangeToSystemPage();
    });
    public string? DisplayTitle(string? title) => Enabled ? (string.IsNullOrWhiteSpace(title) ? host.LauncherDisplayName() : title) + " - 整合包模式" : title;
    public void ApplyVisibility(Window? main = null) {
        main ??= host.MainWindow();
        bool active = Enabled, force = host.ForceShow();
        if (main is not null) ApplyHeader(host.Header(main));
        ModpackModeSetupView? setup = host.Setup();
        if (setup is not null) {
            setup.SystemTitle(active ? "启动器" : "其他");
            setup.Launch.Visibility = active || force || !host.Setting("UiHiddenSetupLaunch") ? Visibility.Visible : Visibility.Collapsed;
            setup.System.Visibility = active || force || !host.Setting("UiHiddenSetupSystem") ? Visibility.Visible : Visibility.Collapsed;
            setup.UI.Visibility = active || (!force && host.Setting("UiHiddenSetupUi")) ? Visibility.Collapsed : Visibility.Visible;
            setup.Link.Visibility = Visibility.Collapsed;
            int visible = new[] { setup.Launch, setup.System, setup.UI }.Count(item => item.Visibility == Visibility.Visible);
            setup.ItemsPanel.Visibility = active || force || visible >= 2 ? Visibility.Visible : Visibility.Collapsed;
            IEnumerable<UIElement>? buttons = setup.SystemButtons();
            if (buttons is not null) foreach (UIElement button in buttons) button.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        }
        ModpackModeLaunchView? launch = host.Launch();
        if (launch is not null) {
            launch.Version.Visibility = active || force || !host.Setting("UiHiddenFunctionSelect") ? Visibility.Visible : Visibility.Collapsed;
            ApplyLaunchLayout(launch.More);
        }
        ApplyInstanceVisibility();
    }
    public int NormalizeInstancePage(int page) {
        if (page == host.Routes.InstanceBackups) return host.Routes.InstanceSaves;
        return Enabled && page == host.Routes.InstanceExport ? host.Routes.InstanceOverall : page;
    }
    public bool CanChangeMinecraft(string? current, string? requested) => !Enabled || (!string.IsNullOrWhiteSpace(current) && string.Equals(current, requested, StringComparison.Ordinal));
    public bool ConfirmModAction(string actionName, Func<string, bool>? confirm = null) {
        if (!Enabled) return true;
        if (confirm is not null) return confirm(actionName);
        string detail = "修改整合包的 Mod 可能导致依赖缺失、版本不兼容或整合包无法启动。操作前建议备份存档，并确认整合包允许这项改动。";
        if (actionName.Contains("删除")) detail += "\r\n按住 Shift 删除会永久移除文件。";
        return Message("即将" + actionName + "。\r\n\r\n" + detail, "整合包 Mod 操作警告", "继续", "取消", true) == 1;
    }
    public void ApplyInstanceVisibility() {
        Visibility hidden = Enabled ? Visibility.Collapsed : Visibility.Visible;
        ModpackModeInstanceView view = host.Instance();
        if (view.Export is not null) view.Export.Visibility = hidden;
        view.RefreshOverview(); view.RefreshModify();
        if (view.ModDownload is not null) {
            view.ModDownload.Visibility = hidden; view.ModHintDownload!.Visibility = hidden;
            HideButtonColumn(view.ModDownload); HideButtonColumn(view.ModHintDownload);
            view.ModEmptyHint!(Enabled ? "你可以从本地文件安装 Mod，或打开 Mod 文件夹。" :
                "你可以下载新的 Mod，或者从已经下载好的文件安装 Mod。\n如果你已经安装了 Mod，可能是版本隔离设置有误，请在设置中调整版本隔离选项。");
        }
        if (view.DisabledVersion is not null) {
            view.DisabledVersion.Visibility = hidden;
            view.DisabledHint!(Enabled ? "该版本未安装可用的 Mod 加载器，可以在「修改」页检查。修改组件可能影响整合包运行。" :
                "你需要先安装 Forge、Fabric 等 Mod 加载器才能使用 Mod，请在下载页面安装这些版本。\n如果你已经安装过了 Mod 加载器，那么你很可能选择了错误的版本，请点击版本选择按钮切换版本。");
            view.RefreshDisabledDownload();
        }
    }
    public void HideButtonColumn(FrameworkElement button) {
        if (button.Parent is not Grid parent) return;
        ColumnDefinition column = parent.ColumnDefinitions[Grid.GetColumn(button)];
        column.SharedSizeGroup = Enabled ? null : "Button";
        column.Width = Enabled ? new GridLength(0) : GridLength.Auto;
    }
    public void ApplyHeader(ModpackModeHeaderView view) {
        bool active = Enabled, force = host.ForceShow();
        view.Panel.Visibility = active || force || !(host.Setting("UiHiddenPageDownload") && host.Setting("UiHiddenPageLink") && host.Setting("UiHiddenPageSetup") && host.Setting("UiHiddenPageOther") && host.Setting("UiHiddenPageFavorites")) ? Visibility.Visible : Visibility.Collapsed;
        view.Download.Visibility = active || (!force && host.Setting("UiHiddenPageDownload")) ? Visibility.Collapsed : Visibility.Visible;
        view.Link.Visibility = Visibility.Collapsed;
        view.Other.Visibility = active || (!force && host.Setting("UiHiddenPageOther")) ? Visibility.Collapsed : Visibility.Visible;
        if (view.Favorites is not null) view.Favorites.Visibility = active || (!force && host.Setting("UiHiddenPageFavorites")) ? Visibility.Collapsed : Visibility.Visible;
        view.Setup.Visibility = active || force || !host.Setting("UiHiddenPageSetup") ? Visibility.Visible : Visibility.Collapsed;
        view.SetupText("设置");
    }
    public static void ApplyLaunchLayout(FrameworkElement more) { Grid.SetColumn(more, 3); Grid.SetColumnSpan(more, 1); }
    public List<TFolder> CreatePackFolderList() => new() { host.CreateFolder("整合包文件夹", host.PackFolder(host.BaseFolder())) };
    public bool AllowsInstance(string folder) => !Enabled || host.IsPackVersion(host.BaseFolder(), folder);
    public void RefreshFolderScope() {
        int revision = Interlocked.Increment(ref FolderScopeRevision);
        bool packMode = Enabled;
        string folder = host.MinecraftFolder();
        string? selectedName = host.ReadSelectedName(folder);
        host.ForceInstanceRefresh(); host.StartFolderList(); host.RefreshSelect();
        host.RunInNewThread(() => {
            try {
                host.WaitFolderList(); host.RunInstanceList(folder);
                List<TInstance> instances = host.Instances().Where(instance => !host.InstanceError(instance)).ToList();
                TInstance? selected = instances.FirstOrDefault(instance => string.Equals(host.InstanceName(instance) ?? "", selectedName ?? "", StringComparison.Ordinal)) ?? instances.FirstOrDefault();
                host.RunInUi(() => {
                    if (revision != FolderScopeRevision || packMode != Enabled || !PathUtils.IsSame(folder, host.MinecraftFolder())) return;
                    host.SelectInstance(selected); host.RefreshLaunchPage(true, false); host.RefreshLaunchButtons();
                });
            } catch (Exception error) { if (!error.IsCanceled()) Logger.Warn(error, "刷新整合包文件夹范围失败"); }
        }, "Refresh Modpack Folder Scope");
    }
    public void ConfigureExport(string exportRoot, bool includeLauncher, bool packMode) {
        if (!includeLauncher) return;
        host.WriteIni(Path.Combine(exportRoot, "PCL", "Setup.ini"), ExportMarkerKey, true);
        host.WriteIni(Path.Combine(exportRoot, "PCL", "Setup.ini"), SettingKey, packMode);
    }
}
