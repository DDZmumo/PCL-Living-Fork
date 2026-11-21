using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using PCLLF.Presentation.Animation;
using PCLLF.Presentation.Controls;

using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    public void PageChange(TStack stack, int subType = 0) {
        if (!host.LegacyLink) {
            if (stack.PageCode == (int)LegacyMainPage.Link) { Hint("旧联机模块暂未参与编译。"); return; }
            if (stack.PageCode == (int)LegacyMainPage.Setup && subType == 1) subType = 0;
        }
        if (host.PackBlocksPage(stack.PageCode)) { Hint("整合包模式已隐藏此功能，可在 设置 → 启动器 中关闭整合包模式。"); return; }
        if (stack.PageCode == (int)LegacyMainPage.Setup) subType = host.NormalizeSetupPage(subType);
        if (stack.PageCode == (int)LegacyMainPage.InstanceSetup) subType = host.NormalizeInstancePage(subType);
        if (stack.PageCode == (int)LegacyMainPage.Other && subType > 2) subType = 1;
        if (stack.PageCode != (int)LegacyMainPage.InstanceSetup && string.IsNullOrEmpty(PageNameGet(stack))) {
            PageChangeExit(); host.WriteChangingPage(true);
            // Page codes are stable persisted identities, not title-panel indexes.
            MyRadioButtonView title = view.PanTitleSelect().Children.OfType<MyRadioButtonView>()
                .First(button => Conversions.ToInteger(LegacyBaseOperations.Val(button.Tag)) == stack.PageCode);
            title.SetChecked(true, true, string.IsNullOrEmpty(PageNameGet(PageCurrent)));
            host.WriteChangingPage(false);
            switch ((LegacyMainPage)stack.PageCode) {
                case LegacyMainPage.Download: case LegacyMainPage.Setup: case LegacyMainPage.Other: case LegacyMainPage.Favorites:
                    EnsureLeft(stack.PageCode).Item(subType).SetChecked(true, true, Same(stack, PageCurrent)); break;
            }
            PageChangeActual(stack, subType);
        } else {
            PageChangeActual(stack, subType);
        }
    }
    public void TitleSelect(MyRadioButtonView sender) {
        if (host.ChangingPage()) return;
        PageChangeActual(Stack(Conversions.ToInteger(LegacyBaseOperations.Val(sender.Tag))));
    }
    public void PageBack() { if (PageStack.Any()) PageChangeActual(PageStack[0]); else PageChange(Stack((int)LegacyMainPage.Launch)); }
    public void PageChangeActual(TStack stack, int subType = -1) {
        if (host.PackBlocksPage(stack.PageCode)) return;
        if (stack.PageCode == (int)LegacyMainPage.Setup && subType >= 0) subType = host.NormalizeSetupPage(subType);
        LegacyMainLeftPage? instanceLeft = null;
        if (stack.PageCode == (int)LegacyMainPage.InstanceSetup) {
            if (host.InstanceBlocksNavigation(host.SettingsInstance())) { Hint("该版本正在被修改，暂时无法进入版本设置！", LegacyMainHint.Red); return; }
            // EnsureLeft refreshes the instance's on-disk context. Reuse that inspection within this dispatch.
            instanceLeft = EnsureLeft(stack.PageCode);
            if (subType < 0) subType = instanceLeft.PageId();
            subType = host.NormalizeInstancePage(subType);
            if (host.InstanceLocked(host.SettingsInstance()!) && InstanceNavigationRules.IsEditingPage(subType)) subType = 0;
        }
        if (Same(PageCurrent, stack) && (PageCurrentSub == subType || subType == -1)) {
            if (instanceLeft is not null) instanceLeft.SyncSelection(subType);
            return;
        }
        AniControlEnabled = checked(AniControlEnabled + 1);
        try {
            string? name = PageNameGet(stack);
            if (string.IsNullOrEmpty(name)) PageChangeExit();
            else if (PageStack.Any()) {
                AniStart([AaOpacity(view.LabTitleInner(), -view.LabTitleInner().Opacity, 130), AaCode(() => view.LabTitleInner().Text = name!, after: true), AaOpacity(view.LabTitleInner(), 1, 150, 30)], "FrmMain Titlebar SubLayer");
                if (PageStack.Contains(stack)) { while (PageStack.Contains(stack)) PageStack.RemoveAt(0); }
                else PageStack.Insert(0, PageCurrent);
            } else {
                view.PanTitleInner().Visibility = Visibility.Visible; view.PanTitleMain().IsHitTestVisible = false; view.PanTitleInner().IsHitTestVisible = true;
                PageNameRefresh(stack);
                AniStart([
                    AaOpacity(view.PanTitleMain(), -view.PanTitleMain().Opacity, 150), AaX(view.PanTitleMain(), 12 - view.PanTitleMain().Margin.Left, 150, ease: In(AnimationEasePower.Weak)),
                    AaOpacity(view.PanTitleInner(), 1 - view.PanTitleInner().Opacity, 150, 200), AaX(view.PanTitleInner(), -view.PanTitleInner().Margin.Left, 350, 200, Back()),
                    AaCode(() => view.PanTitleMain().Visibility = Visibility.Collapsed, after: true)
                ], "FrmMain Titlebar FirstLayer");
                PageStack.Insert(0, PageCurrent);
            }
            PageLast = PageCurrent; PageCurrent = stack;
            switch ((LegacyMainPage)stack.PageCode) {
                case LegacyMainPage.Launch: PageChangeAnim(host.LeftPage(stack.PageCode).Element, host.RightPage(stack.PageCode)); break;
                case LegacyMainPage.Download: case LegacyMainPage.Setup: case LegacyMainPage.Other: case LegacyMainPage.Favorites: {
                    LegacyMainLeftPage left = EnsureLeft(stack.PageCode); PageChangeAnim(left.Element, left.PageGet(subType)); break;
                }
                case LegacyMainPage.InstanceSetup: {
                    MyPageRightView target = instanceLeft!.PageGet(subType); instanceLeft.SyncSelection(subType); PageChangeAnim(instanceLeft.Element, target); break;
                }
                case LegacyMainPage.Link:
                    if (host.LegacyLink) { host.EnsureRightPage(stack.PageCode); PageChangeAnim(host.NewBlankLeft(), host.RightPage(stack.PageCode)); } else return;
                    break;
                case LegacyMainPage.InstanceSelect: case LegacyMainPage.TaskManager: host.EnsureLeftPage(stack.PageCode); host.EnsureRightPage(stack.PageCode); PageChangeAnim(host.LeftPage(stack.PageCode).Element, host.RightPage(stack.PageCode)); break;
                case LegacyMainPage.ResourceDetail: host.EnsureRightPage(stack.PageCode); PageChangeAnim(host.NewBlankLeft(), host.RightPage(stack.PageCode)); break;
                case LegacyMainPage.HelpDetail: PageChangeAnim(host.NewBlankLeft(), (FrameworkElement)AdditionalAt(stack, 1)); break;
            }
            view.ExtraDownload().ShowRefresh(); view.ExtraApril().ShowRefresh();
            Logger.Info($"切换主要页面：{(LegacyMainPage)stack.PageCode}, {host.DescribeSubType(subType)}");
        } catch (Exception ex) { Logger.Error(ex, $"切换主要页面失败（ID {(LegacyMainPage)PageCurrent.PageCode}）"); }
        finally { AniControlEnabled = checked(AniControlEnabled - 1); }
    }
    public void PageChangeAnim(FrameworkElement targetLeft, FrameworkElement? targetRight) {
        Animations.Stop("FrmMain LeftChange"); Animations.Stop("PageLeft PageChange"); AniControlEnabled = checked(AniControlEnabled + 1);
        if (targetLeft.Parent is not null) targetLeft.SetValue(ContentPresenter.ContentProperty, null);
        if (targetRight is not null && targetRight.Parent is not null) targetRight.SetValue(ContentPresenter.ContentProperty, null);
        PageLeft = (MyPageLeftView)targetLeft; PageRight = (MyPageRightView)targetRight!;
        ((MyPageLeftView)view.PanMainLeft().Child).TriggerHideAnimation(); ((MyPageRightView)view.PanMainRight().Child).PageOnExit();
        AniControlEnabled = checked(AniControlEnabled - 1);
        if (view.UsesGlassShell?.Invoke() == true) {
            Animations.Stop("FrmMain PageChangeLeft"); Animations.Stop("FrmMain PageChangeRight");
            AniControlEnabled = checked(AniControlEnabled + 1);
            ((MyPageRightView)view.PanMainRight().Child).PageOnForceExit();
            view.PanMainLeft().Child = PageLeft; PageLeft.Opacity = 1; view.PanMainLeft().Background = null;
            view.PanMainRight().Child = PageRight; PageRight.Opacity = 1; view.PanMainRight().Background = null;
            AniControlEnabled = checked(AniControlEnabled - 1);
            PageLeft.TriggerShowAnimation();
            PageRight.PageOnEnter();
            host.UiPosted(() => { ResizeLeft(view.PanMainLeft().ActualWidth); view.ExtraBack().ShowRefresh(); }, true);
            return;
        }
        AniStart([
            AaCode(() => {
                AniControlEnabled = checked(AniControlEnabled + 1); view.PanMainLeft().Child = PageLeft; PageLeft.Opacity = 0; view.PanMainLeft().Background = null; AniControlEnabled = checked(AniControlEnabled - 1);
                host.UiPosted(() => ResizeLeft(view.PanMainLeft().ActualWidth), true);
            }, 110),
            AaCode(() => { PageLeft.Opacity = 1; PageLeft.TriggerShowAnimation(); }, 30, true)
        ], "FrmMain PageChangeLeft");
        AniStart([
            AaCode(() => {
                AniControlEnabled = checked(AniControlEnabled + 1); ((MyPageRightView)view.PanMainRight().Child).PageOnForceExit(); view.PanMainRight().Child = PageRight; PageRight.Opacity = 0; view.PanMainRight().Background = null; AniControlEnabled = checked(AniControlEnabled - 1);
                host.UiPosted(() => view.ExtraBack().ShowRefresh(), true);
            }, 110),
            AaCode(() => { PageRight.Opacity = 1; PageRight.PageOnEnter(); }, 30, true)
        ], "FrmMain PageChangeRight");
    }
    public void PageChangeExit() {
        if (!PageStack.Any()) return;
        view.PanTitleMain().Visibility = Visibility.Visible; view.PanTitleMain().IsHitTestVisible = true; view.PanTitleInner().IsHitTestVisible = false;
        AniStart([
            AaOpacity(view.PanTitleInner(), -view.PanTitleInner().Opacity, 150), AaX(view.PanTitleInner(), -18 - view.PanTitleInner().Margin.Left, 150, ease: In()),
            AaOpacity(view.PanTitleMain(), 1 - view.PanTitleMain().Opacity, 150, 200), AaX(view.PanTitleMain(), -view.PanTitleMain().Margin.Left, 350, 200, Back(AnimationEasePower.Weak)),
            AaCode(() => view.PanTitleInner().Visibility = Visibility.Collapsed, after: true)
        ], "FrmMain Titlebar FirstLayer"); PageStack.Clear();
    }
    public void LeftSizeChanged(SizeChangedEventArgs args) { if (args.WidthChanged) ResizeLeft(args.NewSize.Width); }
    public void ResizeLeft(double width) {
        double delta = width - view.RectLeftBackground().Width;
        if (Math.Abs(delta) > 0.1 && AniControlEnabled == 0) {
            if (view.PanMain().Opacity < 0.1) view.PanMainLeft().IsHitTestVisible = false;
            if (width > 0) AniStart([
                AaWidth(view.RectLeftBackground(), width - view.RectLeftBackground().Width, 180, ease: Out(AnimationEasePower.ExtraStrong)),
                AaOpacity(view.RectLeftShadow(), 1 - view.RectLeftShadow().Opacity, 180), AaCode(() => view.PanMainLeft().IsHitTestVisible = true, 150)
            ], "FrmMain LeftChange", true);
            else AniStart([
                AaWidth(view.RectLeftBackground(), -view.RectLeftBackground().Width, 180, ease: Out()), AaOpacity(view.RectLeftShadow(), -view.RectLeftShadow().Opacity, 180), AaCode(() => view.PanMainLeft().IsHitTestVisible = true, 150)
            ], "FrmMain LeftChange", true);
        } else { view.RectLeftBackground().Width = width; view.PanMainLeft().IsHitTestVisible = true; Animations.Stop("FrmMain LeftChange"); }
    }
}
