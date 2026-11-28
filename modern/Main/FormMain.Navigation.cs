// SPDX-License-Identifier: MPL-2.0
using System;
using System.Linq;
using MeloongCore;
using PCLCS;

namespace PCL;

public partial class FormMain
{
    public PageSubType PageCurrentSub => SelectedSubPage(PageCurrent.Page);

    public void PageChange(PageStackData target, PageSubType subPage = PageSubType.Default)
    {
        if (ModModpackMode.BlocksPage(target.Page)) {
            ModMain.Hint("整合包模式已隐藏此功能，可在 设置 → 启动器 中关闭整合包模式。");
            return;
        }
        Navigate(target, subPage);
    }

    public void PageBack()
    {
        if (glassShell?.CloseSchematicPreview() == true) return;
        if (PageStack.FirstOrDefault() is { } previous && !ModModpackMode.BlocksPage(previous.Page))
            Navigate(previous, SelectedSubPage(previous.Page));
        else Navigate(PageType.Launch, PageSubType.Default);
    }

    private void Navigate(PageStackData target, PageSubType subPage)
    {
        if (target.Page == PageType.InstanceSetup) {
            var instance = LauncherInstanceContext.Instance;
            if (instance is null || ModMinecraft.McInstanceLock.BlocksNavigation(instance)) {
                ModMain.Hint("该版本暂时无法进入版本设置。", ModMain.HintType.Red);
                return;
            }
            subPage = GlassInstanceNavigation.Normalize(subPage);
            if (ModMinecraft.McInstanceLock.IsLocked(instance) && InstanceNavigationRules.IsEditingPage((int)subPage)) {
                if (PageCurrent.Page == PageType.InstanceSetup) {
                    ModMain.Hint("该版本正在处理文件，暂时无法打开此页面。", ModMain.HintType.Red);
                    return;
                }
                subPage = PageSubType.InstanceOverall;
            }
        }
        else if (target.Page == PageType.Setup)
            subPage = ModModpackMode.NormalizeSetupPage(subPage == PageSubType.SetupLink ? PageSubType.SetupLaunch : subPage);
        else if (target.Page == PageType.Other) {
            if (subPage is not (PageSubType.OtherHelp or PageSubType.OtherAbout or PageSubType.OtherTest or PageSubType.OtherSchematics)) subPage = PageSubType.OtherAbout;
            if (!ShowMorePage(subPage))
                subPage = ShowMorePage(PageSubType.OtherHelp) ? PageSubType.OtherHelp :
                    ShowMorePage(PageSubType.OtherAbout) ? PageSubType.OtherAbout : PageSubType.OtherTest;
        }

        if (glassShell is null || !glassShell.OwnsPage(target, subPage)) {
            ModMain.Hint("此页面无法打开。", ModMain.HintType.Red);
            return;
        }
        if (PageCurrent == target && PageCurrentSub == subPage) {
            glassShell.RequestPageRefresh();
            return;
        }

        bool nested = target.Page is PageType.InstanceSelect or PageType.InstanceSetup or PageType.TaskManager or
            PageType.ResourceDetail or PageType.HelpDetail;
        if (!nested) PageStack.Clear();
        else if (PageCurrent != target) {
            if (PageStack.IndexOf(target) is int index and >= 0) PageStack.RemoveRange(0, index + 1);
            else PageStack.Insert(0, PageCurrent);
        }

        PageLast = PageCurrent;
        PageCurrent = target;
        switch (target.Page) {
            case PageType.Download: GlassDownloadsSubPage = subPage; break;
            case PageType.Favorites: GlassFavoritesSubPage = subPage; break;
            case PageType.Setup: GlassSettingsSubPage = subPage; break;
            case PageType.Other: GlassMoreSubPage = subPage; break;
            case PageType.InstanceSetup: GlassInstanceNavigation.Select(subPage); break;
        }
        glassShell.BeginNavigation();
        Logger.Info($"切换主要页面：{target.Page}, {subPage}");
    }

    private PageSubType SelectedSubPage(PageType page) => page switch {
        PageType.Download => GlassDownloadsSubPage,
        PageType.Favorites => GlassFavoritesSubPage,
        PageType.Setup => GlassSettingsSubPage,
        PageType.Other => GlassMoreSubPage,
        PageType.InstanceSetup => GlassInstanceNavigation.SelectedRoute,
        _ => PageSubType.Default
    };
}
