using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;

namespace PCLCS;

public static class InstanceNavigationRules {
    public static bool IsEditingPage(int id) => id is 1 or 2 or 11;
    public static void ReloadJava<TInstance>(Func<int> animation, Func<TInstance?> instance, RedoableWorker<Java> worker) where TInstance : class {
        if (animation() != 0) return; if (instance() is null) return; worker.Start();
    }
}
public sealed class InstanceNavigationView<TItem> where TItem : FrameworkElement {
    public StackPanel Items = null!;
    public TItem Overall = null!, Setup = null!, Modify = null!, Export = null!, Mod = null!, ModDisabled = null!, Keybindings = null!, RestorePoints = null!, Clone = null!;
    public TItem[] Resources = null!, Refreshables = null!;
}
public sealed class InstanceNavigationHost<TInstance, TId, TItem, TButton, TPage, TResource>
    where TInstance : class where TId : struct, Enum where TItem : FrameworkElement where TButton : FrameworkElement where TPage : FrameworkElement where TResource : TPage {
    public Func<TInstance?> Instance = null!;
    public Func<TInstance, bool> Locked = null!, BlocksNavigation = null!, Modable = null!;
    public Func<bool> IsSyncingSelection = null!;
    public Action<bool> SetSyncingSelection = null!;
    public Func<TId> PageId = null!, CurrentSubPage = null!;
    public Action<TId> SetPageId = null!;
    public Func<TId, TId> Normalize = null!;
    public Dictionary<int, Func<TPage?>> ReadPage = new();
    public Dictionary<int, Action<TPage>> WritePage = new();
    public Dictionary<int, Func<TPage>> CreatePage = new();
    public Func<int, TResource> CreateResource = null!;
    public Dictionary<TId, TResource> ResourcePages = null!;
    public Dictionary<int, Action<TPage>> RefreshSpecial = new();
    public Action<TPage> ResetSetup = null!;
    public Action RefreshMods = null!, RefreshExport = null!, RestartModify = null!;
    public Action<string> HintRed = null!;
    public Func<string, string, string, bool, int> Confirm = null!;
    public Action<Exception, string> Error = null!;
    public Action<int> AdjustAnimation = null!;
    public InstanceNavigationTransitionHost<TPage> Transitions = null!;
    public Action HiddenRefresh = null!, BindBeforeMinecraftChange = null!;
    public Func<TItem, string> Logo = null!;
    public Action<TItem, string> SetLogo = null!;
    public Func<TItem, double> LogoScale = null!;
    public Action<TItem, double> SetLogoScale = null!;
    public Func<TItem, IEnumerable<TButton>> Buttons = null!;
    public Action<TItem, IEnumerable<TButton>> SetButtons = null!;
    public Func<TButton> CreateButton = null!;
    public Func<TButton, string> ButtonLogo = null!;
    public Action<TButton, string> SetButtonLogo = null!;
    public Action<TButton, double> SetButtonScale = null!;
    public Action<TButton, Action> OnClick = null!;
    public Action<TItem, bool> SetChecked = null!;
    public Action<TItem, bool, bool, bool> SetCheckedState = null!;
    public Func<string> RestoreLogo = null!;
    public Func<bool> ModpackEnabled = null!;
}

public sealed class InstanceNavigationTransitionHost<TPage> where TPage : FrameworkElement {
    public Action<string> StopAnimation = null!;
    public Func<TPage> PageRight = null!, PanelChild = null!;
    public Action<TPage> SetPageRight = null!, SetPanelChild = null!, PageExit = null!, PageForceExit = null!, PageEnter = null!;
    public Action<Action, int, Action, int, bool, string> AnimatePage = null!;
}
public static class InstanceNavigationTransitions {
    public static void Run<TPage>(TPage target, InstanceNavigationTransitionHost<TPage> host) where TPage : FrameworkElement {
        host.StopAnimation("FrmMain PageChangeRight");
        if (target.Parent is not null) target.SetValue(ContentPresenter.ContentProperty, null);
        host.SetPageRight(target); host.PageExit(host.PanelChild());
        host.AnimatePage(() => { host.PageForceExit(host.PanelChild()); host.SetPanelChild(host.PageRight()); host.PageRight().Opacity = 0; }, 130,
            () => { host.PageRight().Opacity = 1; host.PageEnter(host.PageRight()); }, 30, true, "PageLeft PageChange");
    }
}
