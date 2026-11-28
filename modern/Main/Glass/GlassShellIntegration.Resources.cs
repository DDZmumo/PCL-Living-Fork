// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using PCLLF.Wpf.Glass;
using Page = PCL.FormMain.PageType;
using SubPage = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed partial class GlassShellIntegration
{
    private readonly Dictionary<SubPage, GlassResourceBrowserPage> _downloadBrowsers = new();
    private readonly Dictionary<FormMain.PageStackData, GlassResourceDetailPage> _resourceDetails = new(ReferenceEqualityComparer.Instance);
    private GlassResourceBrowserPage? _favoritesBrowser, _activeResourceBrowser;
    private GlassResourceDetailPage? _activeResourceDetail;
    private FormMain.PageStackData? _resourceDetailRoute;

    private static bool IsBrowserRoute(Page page, SubPage sub) =>
        page == Page.Favorites || page == Page.Download && ResourceBrowserCatalog.Find(sub) is not null;

    private void OpenResourceBrowser(Page page, SubPage sub)
    {
        var category = ResourceBrowserCatalog.Find(sub);
        if (page == Page.Favorites) _activeResourceBrowser = _favoritesBrowser ??= NewBrowser(null);
        else
        {
            if (!_downloadBrowsers.TryGetValue(sub, out var browser))
                _downloadBrowsers.Add(sub, browser = NewBrowser(category));
            _activeResourceBrowser = browser;
        }
        var pending = page == Page.Download ? _owner.TakeResourceSearchTarget(sub) : null;
        _activeResourceBrowser.Open(category, pending, _owner.GlassPreviewState is null);
        View.SetPageContent(_activeResourceBrowser);
    }

    private GlassResourceBrowserPage NewBrowser(ResourceBrowserCategory? category)
    {
        var browser = new GlassResourceBrowserPage(_owner, category);
        browser.Scroll.ScrollChanged += (_, e) => {
            if (e.VerticalChange != 0) _owner.RefreshBackToTop();
        };
        return browser;
    }

    private void OpenResourceDetail()
    {
        var route = _owner.PageCurrent;
        if (!_resourceDetails.TryGetValue(route, out var page))
        {
            page = new GlassResourceDetailPage(_owner, (object[])route.Additional);
            page.Scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _owner.RefreshBackToTop(); };
            _resourceDetails.Add(route, page);
        }
        _activeResourceDetail = page;
        _resourceDetailRoute = route;
        page.Open(_owner.GlassPreviewState is null);
        View.SetPageContent(page);
    }

    private void PruneResourceDetails()
    {
        foreach (var route in _resourceDetails.Keys.ToArray())
        {
            if (ReferenceEquals(route, _owner.PageCurrent) || _owner.PageStack.Any(entry => ReferenceEquals(entry, route))) continue;
            if (_resourceDetails.Remove(route, out var page)) page.Dispose();
        }
    }

    private IReadOnlyList<GlassSidebarItem> ResourceSidebar(Page page)
    {
        bool favorites = page == Page.Favorites;
        SubPage selected = favorites ? _owner.GlassFavoritesSubPage : _owner.GlassDownloadsSubPage;
        var rows = new List<GlassSidebarItem>();
        rows.Add(Item(favorites ? "全部收藏" : "原版游戏", SubPage.Default,
            favorites ? ModBase.Logo.IconButtonLikeLine : ResourceBrowserCatalog.InstallIcon));
        rows.Add(new GlassSidebarItem { Label = favorites ? "收藏资源" : "社区资源", IsHeader = true });
        foreach (var category in ResourceBrowserCatalog.Categories)
            rows.Add(Item(category.Title, category.Route, category.Icon));
        return rows;

        GlassSidebarItem Item(string label, SubPage route, string icon) => new() {
            Label = label, IconData = icon, IsSelected = route == selected,
            Invoked = () => _owner.PageChange(page, route),
            Actions = favorites || route == SubPage.DownloadInstall ? Array.Empty<GlassSidebarAction>() : [
                new GlassSidebarAction { ToolTip = "刷新", IconData = ModBase.Logo.IconButtonRefresh,
                    Invoked = () =>
                    {
                        _owner.PageChange(page, route);
                        if (_downloadBrowsers.TryGetValue(route, out var browser)) browser.Reload();
                    }
                }
            ]
        };
    }
}
