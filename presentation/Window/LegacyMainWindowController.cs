using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using PCLLF.Presentation.Animation;
using PCLLF.Presentation.Controls;

using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>(LegacyMainWindowView view, LegacyMainWindowHost<TStack, TInstance, TAnimation> host)
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    private Window Window => view.Window;
    private LegacyMainAnimations<TAnimation> Animations => host.Animations;
    private TStack PageCurrent { get => host.CurrentPage(); set => host.WriteCurrentPage(value); }
    private TStack PageLast { get => host.LastPage(); set => host.WriteLastPage(value); }
    private List<TStack> PageStack => host.PageStack();
    private MyPageLeftView PageLeft { get => host.CurrentLeft(); set => host.WriteLeft(value); }
    private MyPageRightView PageRight { get => host.CurrentRight(); set => host.WriteRight(value); }
    private int AniControlEnabled { get => host.AnimationSuppression(); set => host.WriteAnimationSuppression(value); }
    private LegacyMainLeftPage EnsureLeft(int page) { host.EnsureLeftPage(page); return host.LeftPage(page); }
    private static TStack Stack(int page) => new() { PageCode = page };
    private static bool Same(TStack? left, TStack? right) => EqualityComparer<TStack>.Default.Equals(left!, right!);
    private static bool IsPage(TStack stack, LegacyMainPage page) => Same(stack, Stack((int)page));
    private void Hint(string text, LegacyMainHint kind = LegacyMainHint.Default) => host.Hint(text, kind);
    private int Message(string text, string title = "提示", string first = "确定", string second = "", string third = "", Action? thirdAction = null) =>
        host.Message(new LegacyMainMessage { Text = text, Title = title, Button1 = first, Button2 = second, Button3 = third, ThirdAction = thirdAction });
    private void RunThread(Action work, string name, ThreadPriority priority = ThreadPriority.Normal) => host.NewThread(work, name, priority);
    private void AniStart(TAnimation[] frames, string name, bool refreshTime = false) => Animations.Start(frames, name, refreshTime);
    private TAnimation AaOpacity(object target, double value, int duration = 400, int delay = 0, AnimationEase? ease = null, bool after = false) => Animations.Opacity(target, value, duration, delay, ease, after);
    private TAnimation AaX(object target, double value, int duration = 400, int delay = 0, AnimationEase? ease = null, bool after = false) => Animations.X(target, value, duration, delay, ease, after);
    private TAnimation AaWidth(object target, double value, int duration = 400, int delay = 0, AnimationEase? ease = null, bool after = false) => Animations.Width(target, value, duration, delay, ease, after);
    private TAnimation AaDouble(Action<double> work, double value, int duration = 400, int delay = 0, AnimationEase? ease = null, bool after = false) => Animations.Double(work, value, duration, delay, ease, after);
    private TAnimation AaCode(Action work, int delay = 0, bool after = false) => Animations.Code(work, delay, after);
    private AnimationEase Out(AnimationEasePower power = AnimationEasePower.Middle) => Animations.OutFluent(power);
    private AnimationEase In(AnimationEasePower power = AnimationEasePower.Middle) => Animations.InFluent(power);
    private AnimationEase Back(AnimationEasePower power = AnimationEasePower.Middle) => Animations.OutBack(power);
    private AnimationEase Inout(AnimationEasePower power = AnimationEasePower.Middle) => Animations.InoutFluent(power);
    // Additional is a public legacy Object. This is the original VB default-index primitive,
    // preserving existing array/list/default-index semantics without dynamic field discovery.
    private static object AdditionalAt(TStack stack, int index) => NewLateBinding.LateIndexGet(stack.Additional!, new object[] { index }, null)!;
    public static bool StackEquals(TStack self, object? other) => LegacyMainPageIdentity.Equals(self, other);
    public string? PageNameGet(TStack stack) => (LegacyMainPage)stack.PageCode switch {
        LegacyMainPage.InstanceSelect => "版本选择",
        LegacyMainPage.TaskManager => "任务管理",
        LegacyMainPage.InstanceSetup => (host.SettingsInstance() is not { } settingsInstance
            ? "版本设置 - 未知版本"
            : (host.InstanceSettingsTitlePrefix?.Invoke(settingsInstance) ?? "版本设置") + " - " + host.InstanceName(settingsInstance)),
        LegacyMainPage.ResourceDetail => ((ResourceProjectModelData)AdditionalAt(stack, 0)).TranslatedName,
        LegacyMainPage.HelpDetail => host.HelpTitle(AdditionalAt(stack, 0)),
        LegacyMainPage.Schematics => "投影管理",
        _ => ""
    };
    public void PageNameRefresh(TStack stack) => view.LabTitleInner().Text = PageNameGet(stack)!;
    public void PageNameRefresh() => PageNameRefresh(PageCurrent);
    public int PageCurrentSub {
        get {
            int page = PageCurrent.PageCode;
            return (LegacyMainPage)page switch {
                LegacyMainPage.Download or LegacyMainPage.Setup or LegacyMainPage.Other or LegacyMainPage.InstanceSetup or LegacyMainPage.Favorites => EnsureLeft(page).PageId(),
                _ => 0
            };
        }
    }
}
