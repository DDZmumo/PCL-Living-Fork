// Behavior source: LF Pages/PageSetup and Pages/PageOther; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Controls;
using MeloongCore;

namespace PCLCS;
public sealed class LegacyAuxiliaryNavigationController
{
    private readonly LegacyAuxiliaryNavigationHost host;
    private readonly LegacyAuxiliaryNavigationView view;
    private readonly bool setup;
    private bool isLoad, isPageSwitched;
    private LegacySetupPageHost Common => host.Common;

    private bool Read(string key) => Conversions.ToBoolean(Common.Read(key));
    public LegacyAuxiliaryNavigationController(LegacyAuxiliaryNavigationHost host, LegacyAuxiliaryNavigationView view, bool setup)
    {
        this.host = host;
        this.view = view;
        this.setup = setup;
    }

    public void InitializePageId()
    {
        if (setup)
            host.SetPageId(host.ModpackEnabled() || !Read("UiHiddenSetupLaunch") ? 0 : !Read("UiHiddenSetupUi") ? 2 : !Read("UiHiddenSetupSystem") ? 3 : !Read("UiHiddenSetupLink") ? 1 : 0);
        else
            host.SetPageId(!Read("UiHiddenOtherHelp") ? 0 : !Read("UiHiddenOtherAbout") ? 1 : 2);
    }

    public void Loaded()
    {
        if (setup && host.ModpackEnabled())
        {
            isLoad = true;
            host.HiddenRefresh();
            host.ApplyModpackVisibility();
            if (!isPageSwitched)
                view.Launch().SetChecked(true, false, false);
            return;
        }

        bool hidden = false;
        if (setup)
        {
            if (view.Launch().Checked && Read("UiHiddenSetupLaunch"))
                hidden = true;
            if (view.UI().Checked && Read("UiHiddenSetupUi"))
                hidden = true;
            if (view.System().Checked && Read("UiHiddenSetupSystem"))
                hidden = true;
            if (view.Link().Checked && Read("UiHiddenSetupLink"))
                hidden = true;
        }
        else
        {
            if (view.Help().Checked && Read("UiHiddenOtherHelp"))
                hidden = true;
            if (view.About().Checked && Read("UiHiddenOtherAbout"))
                hidden = true;
            if (view.Test().Checked && Read("UiHiddenOtherTest"))
                hidden = true;
        }

        if (host.HiddenForceShow())
            hidden = false;
        if (isLoad && !hidden)
            return;
        isLoad = true;
        host.HiddenRefresh();
        if (isPageSwitched)
            return;
        if (setup)
        {
            if (!Read("UiHiddenSetupLaunch"))
                view.Launch().SetChecked(true, false, false);
            else if (!Read("UiHiddenSetupUi"))
                view.UI().SetChecked(true, false, false);
            else if (!Read("UiHiddenSetupSystem"))
                view.System().SetChecked(true, false, false);
            else if (!Read("UiHiddenSetupLink"))
                view.Link().SetChecked(true, false, false);
            else
                view.Launch().SetChecked(true, false, false);
        }
        else
        {
            if (!Read("UiHiddenOtherHelp"))
                view.Help().SetChecked(true, false, false);
            else if (!Read("UiHiddenOtherAbout"))
                view.About().SetChecked(true, false, false);
            else
                view.Test().SetChecked(true, false, false);
        }
    }

    public void Unloaded() => isPageSwitched = false;
    public void PageCheck(FrameworkElement sender)
    {
        if (sender.Tag is not null)
            PageChange(host.ParseTag(sender.Tag));
    }

    private int Normalize(int id)
    {
        if (setup)
        {
            id = host.NormalizeSetup(id);
            if (!host.LegacyLink() && id == 1)
                id = 0;
        }
        else if (id > 2)
            id = 1;
        return id;
    }

    public MyPageRightView PageGet(int id = -1)
    {
        if (id == -1)
            id = host.PageId();
        id = Normalize(id);
        if (setup)
        {
            switch (id)
            {
                case 0:
                    return host.Launch.Get();
                case 2:
                    return host.UI.Get();
                case 1:
                    if (host.LegacyLink())
                        return host.Link.Get();
                    break;
                case 3:
                    return host.LauncherSettingsPage();
                case 4:
                    return host.Update.Get();
                case 5:
                    return host.Personal.Get();
            }

            throw new Exception("未知的设置子页面种类：" + host.FormatNumber(id));
        }

        switch (id)
        {
            case 0:
                return host.Help.Get();
            case 1:
                return host.About.Get();
            case 2:
                return host.Test.Get();
        }

        throw new Exception("未知的更多子页面种类：" + host.FormatNumber(id));
    }

    public void PageChange(int id, bool force = false)
    {
        id = Normalize(id);
        if (host.PageId() == id && (!setup || !force))
            return;
        Common.AddAnimationSuppression(1);
        isPageSwitched = true;
        try
        {
            PageChangeRun(PageGet(id));
            host.SetPageId(id);
        }
        catch (Exception error)
        {
            Logger.Error(error, $"切换分页面失败（ID {host.FormatEnum(id)}）");
        }
        finally
        {
            Common.AddAnimationSuppression(-1);
        }
    }

    public void PageChangeRun(MyPageRightView target) => PageChangeRun(host, target);
    public static void PageChangeRun(LegacyAuxiliaryNavigationHost host, MyPageRightView target)
    {
        ControlAnimations animations = ControlServices.Current.Animations;
        animations.Stop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(ContentPresenter.ContentProperty, null);
        host.SetTarget(target);
        ((MyPageRightView)host.MainRight().Child).PageOnExit();
        animations.Start(new[] { animations.Code(() =>
        {
            ((MyPageRightView)host.MainRight().Child).PageOnForceExit();
            host.MainRight().Child = host.Target();
            host.Target().Opacity = 0;
        }, 130), animations.Code(() =>
        {
            host.Target().Opacity = 1;
            host.Target().PageOnEnter();
        }, 30, true) }, "PageLeft PageChange");
    }

    private int Tag(object sender) => host.ParseTag(NewLateBinding.LateGet(sender, null, "Tag", Array.Empty<object>(), null, null, null)!);
    public void Reset(object sender)
    {
        int id = Tag(sender);
        switch (id)
        {
            case 0:
                if (Common.Message("是否要初始化 启动 页面的所有设置？该操作不可撤销。", "初始化确认", button2: "取消", warning: true) == 1)
                {
                    host.Launch.Reset(host.Launch.Get());
                    view.Launch().Checked = true;
                }

                break;
            case 2:
                if (Common.Message("是否要初始化 个性化 页面的所有设置？该操作不可撤销。\r\n（背景图片与音乐、主页等外部文件不会被删除）", "初始化确认", button2: "取消", warning: true) == 1)
                {
                    host.UI.Reset(host.UI.Get());
                    view.UI().Checked = true;
                }

                break;
            case 3:
                if (Common.Message("是否要初始化 其他 页面的所有设置？该操作不可撤销。", "初始化确认", button2: "取消", warning: true) == 1)
                {
                    host.System.Reset(host.System.Get());
                    view.System().Checked = true;
                }

                break;
            case 1:
                if (host.LegacyLink() && Common.Message("是否要初始化 联机 页面的所有设置？该操作不可撤销。", "初始化确认", button2: "取消", warning: true) == 1)
                {
                    host.Link.Reset(host.Link.Get());
                    view.Link().Checked = true;
                }

                break;
        }
    }

    public void Refresh(object sender)
    {
        if (Tag(sender) == 0)
        {
            host.RefreshHelp();
            view.Help().Checked = true;
        }

        Common.Hint("正在刷新……", LegacySetupHint.Blue, false);
    }
}
