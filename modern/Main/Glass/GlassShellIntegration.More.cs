// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using PCLLF.Wpf.Glass;
using Page = PCL.FormMain.PageType;
using Sub = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed partial class GlassShellIntegration
{
    private GlassAboutPage? _about;
    private GlassHelpPage? _help;
    private GlassHelpDetailPage? _helpDetail;
    private FormMain.PageStackData? _helpDetailRoute;
    private GlassSchematicManagerPage? _schematicManager;

    private void OpenMorePage(Page route, Sub sub)
    {
        if (route == Page.HelpDetail)
        {
            _helpDetailRoute = _owner.PageCurrent;
            _helpDetail = new GlassHelpDetailPage((ModMain.HelpEntry)((object[])_helpDetailRoute.Additional)[0]);
            _helpDetail.Open();
            View.SetPageContent(_helpDetail);
        }
        else if (sub == Sub.OtherAbout)
        {
            _about ??= new GlassAboutPage();
            _about.Open();
            View.SetPageContent(_about);
        }
        else if (sub == Sub.OtherSchematics)
        {
            _schematicManager ??= new GlassSchematicManagerPage(_owner);
            _schematicManager.Open(_owner.GlassPreviewState is null, _owner.PageCurrent.Additional as ModMinecraft.McInstance);
            View.SetPageContent(_schematicManager);
        }
        else
        {
            _help ??= new GlassHelpPage();
            _help.Open(_owner.GlassPreviewState is null);
            View.SetPageContent(_help);
        }
    }

    internal void ReloadHelp()
    {
        _owner.PageChange(Page.Other, Sub.OtherHelp);
        _owner.Dispatcher.InvokeAsync(() => _help?.Reload());
    }

    private IReadOnlyList<GlassSidebarItem> MoreSidebar()
    {
        var items = new List<GlassSidebarItem>();
        if (FormMain.ShowMorePage(Sub.OtherHelp)) items.Add(Item("帮助", Sub.OtherHelp, GlassSidebarIcons.Help, refresh: true));
        items.Add(Item("投影管理", Sub.OtherSchematics, ResourceBrowserCatalog.Find(Sub.DownloadDataPack)!.Icon));
        if (FormMain.ShowMorePage(Sub.OtherAbout)) items.Add(Item("关于与鸣谢", Sub.OtherAbout, GlassSidebarIcons.About));
        if (FormMain.ShowMorePage(Sub.OtherTest)) items.Add(Item("工具百宝箱", Sub.OtherTest, GlassSidebarIcons.Toolbox));
        return items;

        GlassSidebarItem Item(string title, Sub sub, string icon, bool refresh = false) => new() {
            Label = title, IconData = icon, IsSelected = sub == _owner.GlassMoreSubPage,
            Invoked = () => _owner.PageChange(Page.Other, sub),
            Actions = refresh ? [new GlassSidebarAction { ToolTip = "刷新", IconData = ModBase.Logo.IconButtonRefresh, Invoked = ReloadHelp }]
                : Array.Empty<GlassSidebarAction>()
        };
    }
}
