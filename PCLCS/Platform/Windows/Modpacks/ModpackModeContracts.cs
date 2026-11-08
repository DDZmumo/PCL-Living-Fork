// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

public sealed class ModpackModeRoutes {
    public int SetupLaunch, SetupSystem, Download, Other, Link, SetupPage, InstanceBackups, InstanceSaves, InstanceExport, InstanceOverall;
    public int Favorites = 10;
}
public sealed class ModpackModeHeaderView {
    public Panel Panel = null!;
    public UIElement Download = null!, Link = null!, Setup = null!, Other = null!;
    public UIElement? Favorites;
    public Action<string> SetupText = null!;
}
public sealed class ModpackModeSetupView {
    public FrameworkElement Launch = null!, System = null!, UI = null!, Link = null!, ItemsPanel = null!;
    public Action<string> SystemTitle = null!;
    public Func<IEnumerable<UIElement>?> SystemButtons = null!;
}
public sealed class ModpackModeLaunchView {
    public FrameworkElement Version = null!, More = null!;
}
public sealed class ModpackModeInstanceView {
    public FrameworkElement? Export, ModDownload, ModHintDownload, DisabledVersion;
    public Action<string>? ModEmptyHint, DisabledHint;
    public Action RefreshOverview = null!, RefreshModify = null!, RefreshDisabledDownload = null!;
}
/// <summary>The real pages, controls, settings and folder loaders. Visibility/state decisions stay in C#.</summary>
public sealed class ModpackModeHost<TPage, TFolder, TInstance>
    where TPage : class where TInstance : class {
    public ModpackModeRoutes Routes = null!;
    public Func<string, bool> Setting = null!;
    public Action<string, bool> SetSetting = null!;
    public Func<bool> ForceShow = null!, CurrentPageIsSetup = null!;
    public Func<string> BaseFolder = null!, MinecraftFolder = null!, LauncherDisplayName = null!;
    public Func<TPage?> SystemPage = null!;
    public Action<TPage> SetSystemPage = null!;
    public Func<TPage> CreateSystemPage = null!, CreateCompactPage = null!;
    public Func<Window?> MainWindow = null!;
    public Func<Window, ModpackModeHeaderView> Header = null!;
    public Func<ModpackModeSetupView?> Setup = null!;
    public Func<ModpackModeLaunchView?> Launch = null!;
    public Func<ModpackModeInstanceView> Instance = null!;
    public Func<ModpackMessage, int> Message = null!;
    public Action<Action> RunInUi = null!;
    public Action<Action, string> RunInNewThread = null!;
    public Action RefreshHidden = null!, UpdateTitle = null!, RefreshLaunchButtons = null!, ChangeToSystemPage = null!;
    public Func<string, string> PackFolder = null!;
    public Func<string, string, bool> IsPackVersion = null!;
    public Func<string, string, TFolder> CreateFolder = null!;
    public Func<string, string?> ReadSelectedName = null!;
    public Action ForceInstanceRefresh = null!, StartFolderList = null!, RefreshSelect = null!, WaitFolderList = null!;
    public Action<string> RunInstanceList = null!;
    public Func<IEnumerable<TInstance>> Instances = null!;
    public Func<TInstance, bool> InstanceError = null!;
    public Func<TInstance, string> InstanceName = null!;
    public Action<TInstance?> SelectInstance = null!;
    public Action<bool, bool> RefreshLaunchPage = null!;
    public Action<string, string, object> WriteIni = null!;
}
