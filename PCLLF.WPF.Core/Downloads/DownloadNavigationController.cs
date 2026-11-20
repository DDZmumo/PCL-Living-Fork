using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;

namespace PCLCS;

public sealed class DownloadNavigationHost<TPage, TSub> where TPage : FrameworkElement where TSub : struct, Enum {
    public Func<TSub> PageId = null!, CurrentSub = null!;
    public Action<TSub> SetPageId = null!;
    public Func<int, TPage?> CachedPage = null!;
    public Func<int, TPage> NewPage = null!;
    public Action<int, TPage> StorePage = null!;
    public Func<TPage> PageRight = null!, VisibleRight = null!;
    public Action<TPage> SetPageRight = null!, SetVisibleRight = null!, OnExit = null!, OnForceExit = null!, OnEnter = null!;
    public Action ClearProjectCache = null!, ClearVersionCache = null!;
    public Action<int> ForceLoader = null!;
    public Action<TPage> ResetStorage = null!, Restart = null!;
    public Action<TPage, int> SetResultPage = null!;
    public Action<int, bool> CheckItem = null!;
    public Func<bool> InstallChecked = null!;
    public Action ExitInstallSelection = null!, RefreshHint = null!;
    public Func<object, object?> SenderTag = null!;
    public Action<Action<object>> AddMapRefreshButton = null!;
}
// LF PageDownloadLeft.xaml.vb (including LF map page), LF LICENCE. Original page identities and caches are retained.
public sealed class DownloadNavigationController<TPage, TSub, TAnimation>
    where TPage : FrameworkElement where TSub : struct, Enum {
    private readonly DownloadNavigationHost<TPage, TSub> host;
    private readonly DownloadPageAnimationHost<TAnimation> animation;
    public DownloadNavigationController(DownloadNavigationHost<TPage, TSub> host, DownloadPageAnimationHost<TAnimation> animation) { this.host = host; this.animation = animation; }
    private static TSub Sub(int value) => (TSub)Enum.ToObject(typeof(TSub), value);
    private static int Number(TSub value) => Convert.ToInt32(value);
    public void Check(FrameworkElement sender) { if (sender.Tag is not null) Change(Sub(Conversions.ToInteger(LegacyBaseOperations.Val(sender.Tag)))); }
    public void InitializeMapButton() => host.AddMapRefreshButton(RefreshClick);
    public TPage Get(TSub id) {
        int code = Number(id); if (code == -1) code = Number(host.PageId());
        if (code != 7 && code != 0 && code != 2 && code != 3 && code != 5 && code != 6 && code != 4) throw new Exception("未知的下载子页面种类：" + Sub(code));
        TPage? result = host.CachedPage(code);
        if (result is null) { result = host.NewPage(code); host.StorePage(code, result); }
        return result;
    }
    public void Change(TSub id) {
        if (EqualityComparer<TSub>.Default.Equals(host.PageId(), id)) return;
        animation.SetDisabled(checked(animation.Disabled() + 1));
        try { ChangeRun(Get(id)); host.SetPageId(id); }
        catch (Exception error) { Logger.Error(error, $"切换分页面失败（ID {id}）"); }
        finally { animation.SetDisabled(checked(animation.Disabled() - 1)); }
    }
    public void ChangeRun(TPage target) => Run(target, host, animation);
    public static void Run(TPage target, DownloadNavigationHost<TPage, TSub> host, DownloadPageAnimationHost<TAnimation> animation) {
        animation.Stop("FrmMain PageChangeRight");
        if (target.Parent is not null) target.SetValue(ContentPresenter.ContentProperty, null);
        host.SetPageRight(target); host.OnExit(host.VisibleRight());
        animation.Start(new[] {
            animation.Code(() => { host.OnForceExit(host.VisibleRight()); host.SetVisibleRight(host.PageRight()); host.PageRight().Opacity = 0; }, 130, false),
            animation.Code(() => { host.PageRight().Opacity = 1; host.OnEnter(host.PageRight()); }, 30, true)
        }, "PageLeft PageChange", false);
    }
    public void RefreshClick(object sender) => Refresh(Sub(Conversions.ToInteger(LegacyBaseOperations.Val(host.SenderTag(sender)))));
    public void Refresh() => Refresh(host.CurrentSub());
    public void Refresh(TSub subType) {
        host.ClearProjectCache(); host.ClearVersionCache(); int code = Number(subType);
        switch (code) {
            case 0: for (int i = 0; i < 8; i++) host.ForceLoader(i); host.CheckItem(0, true); break;
            case 7: case 2: case 3: case 5: case 6: case 4:
                TPage? page = host.CachedPage(code);
                if (page is not null) { host.ResetStorage(page); host.SetResultPage(page, 0); host.Restart(page); }
                host.CheckItem(code, true); break;
        }
        host.RefreshHint();
    }
    public void InstallClick() { if (!host.InstallChecked()) return; host.ExitInstallSelection(); }
}
