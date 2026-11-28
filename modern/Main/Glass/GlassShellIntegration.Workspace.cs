// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Windows;
using System.Windows.Controls;
using Page = PCL.FormMain.PageType;
using Route = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed partial class GlassShellIntegration {
    private GlassWorkspacePage? _activeWorkspacePage;
    private GlassLaunchOptionsPage? _globalLaunchOptions;
    internal bool HasOpenNativeDialog => _directMessage is IGlassModalState { IsModalOpen: true };

    internal bool IsCurrentWorkspacePage(FrameworkElement page) =>
        ReferenceEquals(_activeWorkspacePage, page) &&
        _directPage is { } route && route.Page == _owner.PageCurrent.Page && route.SubPage == _owner.PageCurrentSub;

    private bool IsWorkspaceRoute(Page page, Route route) => page switch {
        Page.Download => route == Route.DownloadInstall,
        Page.Setup => route is Route.SetupLaunch or Route.SetupSystem or Route.SetupPersonal,
        Page.Other => route == Route.OtherTest,
        Page.InstanceSetup => route is Route.InstanceOverall or Route.InstanceSetup or Route.InstanceModify or Route.InstancePersonal or Route.InstancePackUpdate or Route.InstanceKeybindings or Route.InstanceClone or Route.InstanceRestorePoints or Route.InstanceExport or Route.InstanceMod or Route.InstanceModDisabled ||
            route is Route.InstanceMmcMove or Route.InstanceMmcPlatformOrigin &&
            _owner.GlassInstanceNavigation.Context.IsMmcFamily,
        _ => false
    };

    private GlassWorkspacePage GetWorkspacePage(Page page, Route route) => (page, route) switch {
        (Page.Download, Route.DownloadInstall) => ModMain.FrmDownloadInstall ??= new PageDownloadInstall(),
        (Page.Setup, Route.SetupLaunch) => GlobalLaunchOptions,
        (Page.Setup, Route.SetupSystem) => (GlassWorkspacePage)ModModpackMode.LauncherSettingsPage(),
        (Page.Setup, Route.SetupPersonal) => ModMain.FrmSetupPersonal ??= new PageSetupPersonal(),
        (Page.Other, Route.OtherTest) => ModMain.FrmOtherTest ??= new PageOtherTest(),
        _ => (GlassWorkspacePage)_owner.GlassInstanceNavigation.GetPage(route)
    };

    private GlassLaunchOptionsPage GlobalLaunchOptions => _globalLaunchOptions ??= new GlassLaunchOptionsPage(_owner);
    internal void PrepareWorkspacePage(Page page)
    {
        if (page == Page.Download) GetWorkspacePage(page, Route.DownloadInstall);
        else if (page == Page.Setup) GetWorkspacePage(page, Route.SetupLaunch);
    }
    internal void ResetGlobalLaunchOptions() => GlobalLaunchOptions.Reset();

    private ScrollViewer? ActiveWorkspaceScroll => _activeWorkspacePage?.Scroll;

    private void RefreshWorkspacePalette() => _activeWorkspacePage?.RefreshPalette();

    internal PageInstanceKeybindings? ActiveKeybindings =>
        _activeWorkspacePage is PageInstanceKeybindings keys && IsCurrentWorkspacePage(keys) ? keys : null;
}
