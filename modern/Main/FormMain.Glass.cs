// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using MeloongCore;
using PCLLF.Presentation.Windowing;
using PCLLF.Wpf.Glass;

namespace PCL;

public partial class FormMain {
    private GlassShellIntegration glassShell;
    private bool _glassResizing, _saveGlassResize;
    private bool _glassLaunchStarted;
    internal bool GlassInitialSelectionComplete { get; private set; }
    internal static bool GlassShellPreferred => true;
    internal bool GlassShellActive => glassShell is not null;
    /// <summary>The glass account page is open over the launch page.</summary>
    internal bool GlassAccountPageOpen => glassShell?.View.IsAccountWorkspaceOpen == true;
    internal PageSubType GlassSettingsSubPage { get; private set; } = PageSubType.SetupLaunch;
    internal PageSubType GlassDownloadsSubPage { get; private set; } = PageSubType.DownloadInstall;
    internal PageSubType GlassFavoritesSubPage { get; private set; } = PageSubType.Default;
    internal PageSubType GlassMoreSubPage { get; private set; } = PageSubType.OtherHelp;
    internal int GlassPageCode => (int)PageCurrent.Page;
    internal string GlassPageTitle => PageNameGet(PageCurrent);
    internal Func<GlassLauncherViewState> GlassPreviewState { get; set; }
    internal GlassLauncherView GlassView => glassShell?.View;
    private void StartGlassLaunch() {
        if (_glassLaunchStarted) return;
        _glassLaunchStarted = true;
        try { LauncherAccountSession.Initialize(); }
        catch (Exception error) { Logger.Error(error, "读取上次登录方式失败", LogBehavior.Toast); }
        ModBase.RunInNewThread(() => {
            try {
                var selection = LauncherStartupSelection.Resolve();
                ModBase.RunInUi(() => {
                    if (!IsLoaded) return;
                    try {
                        ModMinecraft.McInstanceSelected = selection;
                        LauncherAccountSession.LoadActiveSkin();
                        if (string.IsNullOrEmpty(ModLaunch.McLoginAble())) ModLaunch.McLoginLoader.Start();
                        if (Environment.GetCommandLineArgs().Contains("--test", StringComparer.Ordinal))
                            StartGlassLaunchSmokeNavigation();
                    } catch (Exception error) {
                        Logger.Error(error, "初始化游戏选择或登录会话失败", LogBehavior.Toast);
                    } finally { CompleteGlassLaunchSelection(); }
                });
            } catch (Exception error) {
                Logger.Error(error, "读取本地游戏实例失败", LogBehavior.Toast);
                ModBase.RunInUi(CompleteGlassLaunchSelection);
            }
        }, "Version Check", ThreadPriority.AboveNormal);
    }

    private void CompleteGlassLaunchSelection() {
        GlassInitialSelectionComplete = true;
        glassShell.RequestPageRefresh();
    }

    private void StartGlassLaunchSmokeNavigation() => ModBase.RunInThread(() => {
        foreach (var (page, subPage) in new[] {
            (PageType.Download, PageSubType.Default), (PageType.Download, PageSubType.DownloadMod),
            (PageType.Setup, PageSubType.Default), (PageType.Other, PageSubType.Default), (PageType.InstanceSelect, PageSubType.Default)
        }) {
            Thread.Sleep(500);
            ModBase.RunInUiWait(() => PageChange(page, subPage));
        }
        Thread.Sleep(500);
        ModBase.RunInUiWait(() => {
            if (ModMinecraft.McInstanceSelected is not { } instance || ModMinecraft.McInstanceLock.BlocksNavigation(instance)) return;
            instance.Load();
            LauncherInstanceContext.Instance = instance;
            PageChange(PageType.InstanceSetup);
        });
        Thread.Sleep(500);
        EndProgramForce((ModBase.ProcessReturnValues)123);
    });

    internal static void RefreshLaunchAccount(bool keepInput, bool animate) {
        if (ModMain.FrmMain is { GlassShellActive: true } window) window.glassShell.RequestPageRefresh();
    }
    internal static void RefreshHomepage() {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.RefreshHomepage();
    }
    internal static void InvalidateHomepageSettings() => ModMain.FrmMain?.glassShell?.InvalidateHomepageSettings();
    internal bool IsDirectHomepage => GlassShellActive && PageCurrent.Page == PageType.Launch;
    internal bool CanRefreshDirectPage => glassShell?.CanRefreshDirectPage == true;
    internal bool TryRefreshDirectPage() => glassShell?.TryRefreshDirectPage() == true;
    internal bool TryHandleHomepageException(Exception error) => glassShell?.HandleHomepageException(error) == true;
    internal static void ClearLaunchLog() {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.ClearLaunchLog();
    }
    internal static void AppendLaunchLog(string text) {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.AppendLaunchLog(text);
    }
    internal static void RefreshRecentWorlds() {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.RefreshRecentWorlds();
    }
    internal static void RefreshRecentWorldVisibility() {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.RequestPageRefresh();
    }
    internal static void RefreshLaunchDependencyOptions() {
        if (ModMain.FrmMain?.glassShell is { } shell) shell.RequestPageRefresh();
    }
    internal static void ApplyConfiguredOpacity(object value) {
        double opacity = Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(value) / 1000d + 0.4d;
        if (ModMain.FrmMain is not FormMain window) return;
        if (window.glassShell is not null) window.glassShell.SetMaterialDensity(opacity);
        else window.Opacity = opacity;
    }

    /// <summary>Detail pages keep the sidebar of the page they were opened from.</summary>
    internal static bool IsGlassDetailPage(PageType page) => page is PageType.ResourceDetail or PageType.HelpDetail;
    private static bool IsGlassSidebar(PageType page) => page is PageType.Setup or PageType.Download or PageType.Favorites or PageType.InstanceSetup or PageType.Other;

    /// <summary>
    /// While a detail page is shown, the most recent stacked page that owns a contextual sidebar
    /// (for example 资源中心 → 模组 → 资源详情 keeps 资源中心). Null on ordinary pages.
    /// </summary>
    internal PageType? GlassContextOrigin {
        get {
            if (!IsGlassDetailPage(PageCurrent.Page)) return null;
            foreach (PageStackData stacked in PageStack)
                if (!IsGlassDetailPage(stacked.Page) && IsGlassSidebar(stacked.Page)) return stacked.Page;
            return null;
        }
    }

    private void PrepareGlassShell() {
        // Alpha follows the separated rounded surfaces and their gap; panel bases are painted by the launcher.
        AllowsTransparency = true;
        glassShell = new GlassShellIntegration(this);
    }
    private void AttachGlassShell() => glassShell?.Attach();
    internal void RefreshGlassShell() => glassShell?.Refresh(true);
    internal void PrepareWorkspacePage(PageType page) => glassShell?.PrepareWorkspacePage(page);
    internal void RefreshJavaPages() => glassShell?.RefreshJavaPages();
    internal bool TryShowGlassHint(string text, ModMain.HintType type) => glassShell?.ShowHint(text, type) == true;
    internal bool TryDismissGlassHints() => glassShell?.DismissHints() == true;
    internal void CheckGlassLauncherUpdates() => glassShell?.CheckLauncherUpdates();
    internal void ResetGlassPersonalization() => glassShell?.ResetPersonalization();
    internal void ResetGlassLaunchOptions() => glassShell?.ResetGlobalLaunchOptions();
    internal void RefreshTaskButton(bool pulse = false) => ModBase.RunInUi(() => glassShell?.RefreshTaskAction(pulse));
    internal void RefreshBackToTop() => glassShell?.RefreshBackToTop();
    internal void RefreshShutdownButton() => ModBase.RunInUi(() => glassShell?.RefreshShutdownAction());
    internal static void RefreshLauncherSettingsForModpackMode() {
        if (ModMain.FrmMain is { GlassShellActive: true } main) {
            if (main.PageCurrent != PageType.Setup) return;
            if (main.GlassSettingsSubPage != PageSubType.SetupSystem) main.PageChange(PageType.Setup, PageSubType.SetupSystem);
            main.glassShell?.RequestPageRefresh();
        }
    }
    private (PageSubType Route, ModMinecraft.McInstance Instance, string Name)? _pendingResourceSearch;
    internal void BrowseResources(PageSubType route, ModMinecraft.McInstance instance, string name) {
        _pendingResourceSearch = (route, instance, name);
        PageChange(PageType.Download, route);
        if (PageCurrent.Page != PageType.Download) _pendingResourceSearch = null;
        glassShell.RequestPageRefresh();
    }

    internal (ModMinecraft.McInstance Instance, string Name)? TakeResourceSearchTarget(PageSubType route) {
        if (_pendingResourceSearch is not { } pending || pending.Route != route) return null;
        _pendingResourceSearch = null;
        return (pending.Instance, pending.Name);
    }

    private GlassInstanceWorkspace _instanceWorkspace;
    internal GlassInstanceWorkspace GlassInstanceNavigation => _instanceWorkspace ??= new GlassInstanceWorkspace(this);
    internal void SyncGlassInstanceContext() => _instanceWorkspace?.ContextChanged();
    internal void RefreshGlassPackUpdate() => _instanceWorkspace?.RefreshPackUpdate();
    internal bool RefreshGlassLocalResources() => glassShell?.RefreshLocalResources() == true;
    internal bool IsCurrentGlassPage(FrameworkElement page) => glassShell?.IsCurrentWorkspacePage(page) == true;
    internal bool HasOpenGlassDialog => glassShell?.HasOpenNativeDialog == true;
    internal void RefreshInstanceKeybindings() => (glassShell?.ActiveKeybindings ?? PageRight as PageInstanceKeybindings)?.RefreshBindings();
    internal void RefreshInstanceWorkspacePage(PageSubType route) => glassShell?.RequestPageRefresh();
    internal static bool ShowMorePage(PageSubType sub) => sub == PageSubType.OtherSchematics || LauncherAppearanceSettings.HiddenForceShow || !Settings.Get<bool>(sub switch {
        PageSubType.OtherHelp => "UiHiddenOtherHelp",
        PageSubType.OtherAbout => "UiHiddenOtherAbout",
        _ => "UiHiddenOtherTest"
    });
    internal void ReloadGlassHelp() => glassShell?.ReloadHelp();
    internal void OpenGlassJavaCatalogue() => glassShell?.OpenJavaCatalogue();
    internal bool GlassMessageActive => glassShell?.HasDirectMessage == true;
    internal bool CanPresentGlassMessage => glassShell?.CanPresentMessage == true;
    internal bool ShowHiddenInstances => glassShell?.ShowHiddenInstances == true;
    internal bool TryPresentGlassMessage(ModMain.MyMsgBoxConverter message) => glassShell?.TryPresentMessage(message) == true;
    internal bool TryRefreshGlassTheme(bool dark, Action apply) => glassShell?.TryRefreshTheme(dark, apply) == true;

    internal void SetGlassResizing(bool resizing) {
        if (_glassResizing == resizing) return;
        _glassResizing = resizing;
        if (resizing) {
            _saveGlassResize = IsSizeSaveable;
            IsSizeSaveable = false;
        } else {
            IsSizeSaveable = _saveGlassResize;
            if (_saveGlassResize) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
                if (IsLoaded && !_glassResizing) FormMain_SizeChanged();
            }));
        }
        glassShell?.SetWindowResizing(resizing);
    }
}
