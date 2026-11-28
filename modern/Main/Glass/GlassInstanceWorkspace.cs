// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using MeloongCore;
using PCLCS;
using PCLLF.Presentation.Windowing;
using PCLLF.Wpf.Glass;
using McInstance = PCL.ModMinecraft.McInstance;
using Route = PCL.FormMain.PageSubType;
using Page = PCL.FormMain.PageType;

namespace PCL;

internal sealed class GlassInstanceWorkspace
{
    private readonly FormMain _window;
    private readonly Dictionary<Route, FrameworkElement> _pages = new();
    private McInstance? _owner;
    private string? _path;
    private object? _stamp;
    private InstanceSidebarState _context = new(InstanceSidebarFamily.Legacy, null);
    private Route _selected = Route.InstanceOverall;

    internal GlassInstanceWorkspace(FormMain window)
    {
        _window = window;
        InstanceClonerUi.BeforeMinecraftChangeHandler = (owner, oldVersion, newVersion, token) =>
            WorldBackups.BeforeMinecraftChange(owner, oldVersion, newVersion, token);
    }

    internal InstanceSidebarState Context
    {
        get { EnsureContext(); return _context; }
    }

    internal Route SelectedRoute { get { EnsureContext(); return _selected; } }
    internal void Select(Route route) => _selected = Normalize(route);

    internal Route Normalize(Route route)
    {
        EnsureContext();
        return NormalizeKnownContext(route);
    }

    private Route NormalizeKnownContext(Route route)
    {
        route = ModModpackMode.NormalizeInstancePage(route);
        route = (Route)InstanceSidebarContext.NormalizeRoute((int)route, _context.IsMmcFamily);
        if (route is Route.InstanceMod or Route.InstanceModDisabled)
            return _owner?.Modable == true ? Route.InstanceMod : Route.InstanceModDisabled;
        return route;
    }

    private bool EnsureContext(bool force = false)
    {
        McInstance? owner = LauncherInstanceContext.Instance;
        string? path = owner?.PathVersion;
        object? stamp = owner?.Config.ContextStamp;
        bool sameOwner = ReferenceEquals(_owner, owner) && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        if (!force && sameOwner && Equals(_stamp, stamp)) return false;
        var next = LauncherInstanceContext.ObserveInstanceContext(owner, sameOwner && _context.IsMmcFamily);
        bool changed = !sameOwner || _context.Family != next.Family || _context.InstanceId != next.InstanceId ||
            _context.Generation != next.Generation;
        _owner = owner;
        _path = path;
        _stamp = stamp;
        _context = next;
        if (changed)
        {
            _pages.Clear();
            ModMain.FrmDownloadModify = null;
            ModMain.FrmInstanceExport = null;
            ModMain.FrmInstanceMod = null;
        }
        _selected = NormalizeKnownContext(_selected);
        return changed;
    }

    internal void ContextChanged()
    {
        bool changed = EnsureContext(force: true);
        if (_window.PageCurrent.Page != Page.InstanceSetup || _owner is null) return;
        if (changed)
        {
            _window.PageNameRefresh();
            _window.RefreshInstanceWorkspacePage(_selected);
        }
        _window.RefreshGlassShell();
    }

    internal FrameworkElement GetPage(Route route)
    {
        route = Normalize(route);
        if (_pages.TryGetValue(route, out var page)) return page;
        if (_context.IsMmcFamily && _owner is { } managed && InstanceSidebarContext.IsMmcToolRoute((int)route))
        {
            Guid identity = managed.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed
                ? managed.Config.ContextStamp.InstanceId : _context.InstanceId;
            page = route switch {
                Route.InstanceModify => new PageInstanceMmcComponents(managed, identity),
                Route.InstanceExport => new PageInstanceExport(managed, identity),
                Route.InstanceRestorePoints => new PageInstanceRestorePoints(managed, identity),
                Route.InstancePackUpdate => new PageInstancePackUpdate(managed, identity),
                _ => new PageInstanceMmcTool(route, managed)
            };
        }
        else page = route switch {
            Route.InstanceOverall => ModMain.FrmInstanceOverall ??= new PageInstanceOverall(),
            Route.InstanceSetup => new GlassLaunchOptionsPage(_window, _owner ?? throw new InvalidOperationException("尚未选择实例")),
            Route.InstanceModify => ModMain.FrmDownloadModify ??= new PageDownloadModify(_owner),
            Route.InstanceMod => ModMain.FrmInstanceMod ??= new PageInstanceMod(),
            Route.InstanceModDisabled => ModMain.FrmInstanceModDisabled ??= new PageInstanceModDisabled(),
            Route.InstanceExport => ModMain.FrmInstanceExport ??= new PageInstanceExport(),
            Route.InstanceKeybindings => new PageInstanceKeybindings(),
            Route.InstanceRestorePoints => new PageInstanceRestorePoints(),
            Route.InstanceClone => new PageInstanceClone(),
            Route.InstancePackUpdate => new PageInstancePackUpdate(),
            Route.InstancePersonal => new PageSetupPersonal { IsInstanceContext = true },
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        _pages.Add(route, page);
        return page;
    }

    internal void RefreshPackUpdate()
    {
        EnsureContext();
        if (_pages.TryGetValue(Route.InstancePackUpdate, out var page) && page is PageInstancePackUpdate { IsLoaded: true } update) update.Refresh();
    }

    internal IReadOnlyList<GlassSidebarItem> Sidebar()
    {
        EnsureContext();
        bool mmc = _context.IsMmcFamily, pack = ModModpackMode.Enabled;
        bool hideModify = !LauncherAppearanceSettings.HiddenForceShow && Settings.Get<bool>("UiHiddenFunctionInstanceModify");
        var items = new List<GlassSidebarItem> {
            Item("概览", Route.InstanceOverall, "M2,7 L12,2 22,7 V17 L12,22 2,17 Z M2,7 L12,12 22,7 M12,12 V22"),
            Header(_context.ConfigSectionTitle),
            Item("设置", Route.InstanceSetup, ModBase.Logo.IconButtonSetup, reset: !pack && !mmc),
            Item("Java", Route.InstanceJava, GlassJavaGraphics.Icon)
        };
        if (mmc || !hideModify) items.Add(Item(_context.ModifyTitle, Route.InstanceModify, mmc ? ModBase.Logo.IconButtonList : ModBase.Logo.IconButtonEdit, refresh: true));
        items.Add(Item("按键管理", Route.InstanceKeybindings, InstanceSidebarIcons.Keyboard, refresh: true));
        items.Add(Item("基础设置同步", Route.InstancePersonal, ModBase.Logo.IconButtonPersonalSettings));
        items.Add(Header(_context.ToolsSectionTitle));
        items.Add(Item("整合包更新", Route.InstancePackUpdate, ModBase.Logo.IconButtonRefresh));
        if (!pack || mmc) items.Add(Item("导出", Route.InstanceExport, InstanceSidebarIcons.Export, refresh: true));
        items.Add(Item(_context.HistoryTitle, Route.InstanceRestorePoints, InstanceSidebarIcons.History, refresh: true));
        items.Add(Item("克隆", Route.InstanceClone, InstanceSidebarIcons.Copy, refresh: true));
        if (mmc) items.Add(Item("移动实例", Route.InstanceMmcMove, ModBase.Logo.IconButtonOpen));
        items.Add(Header("游戏资源"));
        items.Add(Item("存档", Route.InstanceSaves, ResourceBrowserCatalog.Find(Route.DownloadMap)!.Icon, refresh: true));
        items.Add(Item("截图", Route.InstanceScreenshots, ResourceBrowserCatalog.Find(Route.DownloadResourcePack)!.Icon, refresh: true));
        items.Add(Item("模组", Normalize(Route.InstanceMod), ResourceBrowserCatalog.Find(Route.DownloadMod)!.Icon));
        items.Add(Item("资源包", Route.InstanceResourcePacks, ResourceBrowserCatalog.Find(Route.DownloadResourcePack)!.Icon, refresh: true));
        items.Add(Item("光影包", Route.InstanceShaderPacks, ResourceBrowserCatalog.Find(Route.DownloadShader)!.Icon, refresh: true));
        items.Add(Item("投影/原理图", Route.InstanceSchematics, ResourceBrowserCatalog.Find(Route.DownloadDataPack)!.Icon, refresh: true));
        return items;

        static GlassSidebarItem Header(string label) => new() { Label = label, IsHeader = true };
    }

    private GlassSidebarItem Item(string label, Route route, string icon, bool refresh = false, bool reset = false)
    {
        var actions = new List<GlassSidebarAction>();
        if (refresh) actions.Add(new GlassSidebarAction {
            ToolTip = "刷新", IconData = ModBase.Logo.IconButtonRefresh, Invoked = () => Refresh(route)
        });
        if (reset) actions.Add(new GlassSidebarAction {
            ToolTip = "初始化本页设置", IconData = ModBase.Logo.IconButtonRefresh, Invoked = ResetSettings
        });
        return new GlassSidebarItem {
            Label = label, IconData = icon, IsSelected = route == _selected, Actions = actions,
            Invoked = () => _window.PageChange(Page.InstanceSetup, route)
        };
    }

    private void Refresh(Route route)
    {
        if (_owner is not { } owner || ModMinecraft.McInstanceLock.BlocksNavigation(owner)) return;
        _window.PageChange(Page.InstanceSetup, route);
        if (_window.PageCurrent.Page != Page.InstanceSetup || _window.PageCurrentSub != route) return;
        if (route is >= Route.InstanceSaves and <= Route.InstanceSchematics)
        {
            _window.Dispatcher.InvokeAsync(() => _window.RefreshGlassLocalResources());
            return;
        }
        if (GetPage(route) is IRefreshable page) page.Refresh();
    }

    private void ResetSettings()
    {
        if (_owner is null || ModMinecraft.McInstanceLock.IsLocked(_owner) ||
            ModMain.MyMsgBox("是否要初始化该版本的启动设置？该操作不可撤销。", "初始化确认", "初始化", "取消", IsWarn: true) != 1) return;
        ((GlassLaunchOptionsPage)GetPage(Route.InstanceSetup)).Reset();
        _window.PageChange(Page.InstanceSetup, Route.InstanceSetup);
    }
}
