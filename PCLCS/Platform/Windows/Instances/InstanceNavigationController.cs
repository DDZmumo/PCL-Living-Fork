using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

// LF navigation preserves its added resource/keybinding/history/clone pages and mode normalization.
// CE stable 1a7653127d578efdb2a9388999a3fdc67d9e49d5 PageInstanceLeft.xaml.cs is same-source
// reference under Plain Craft Launcher 2/LICENCE; its page set is not an LF replacement.
public sealed class InstanceNavigationController<TInstance, TId, TItem, TButton, TPage, TResource>
    where TInstance : class where TId : struct, Enum where TItem : FrameworkElement where TButton : FrameworkElement where TPage : FrameworkElement where TResource : TPage {
    private readonly InstanceNavigationView<TItem> view;
    private readonly InstanceNavigationHost<TInstance, TId, TItem, TButton, TPage, TResource> host;
    public InstanceNavigationController(InstanceNavigationView<TItem> view, InstanceNavigationHost<TInstance, TId, TItem, TButton, TPage, TResource> host) { this.view = view; this.host = host; }
    private static int Code(TId id) => Convert.ToInt32(id);
    private static TId Id(int code) => (TId)Enum.ToObject(typeof(TId), code);
    public void Loaded() => host.HiddenRefresh();
    public void InitializeButtons() {
        // Semantic sidebar glyphs belong to the actual page's BAML. Do not
        // replace its keyboard/history/copy icons with unrelated navigation art.
        host.BindBeforeMinecraftChange();
        foreach (TItem item in view.Refreshables) {
            TButton button = host.CreateButton(); button.Tag = item.Tag; button.ToolTip = "刷新"; host.SetButtonScale(button, 0.85); host.SetButtonLogo(button, host.ButtonLogo(host.Buttons(view.Modify).First()));
            host.OnClick(button, () => RefreshTag(button.Tag)); host.SetButtons(item, new[] { button });
        }
    }
    public void RefreshModDisabled() {
        bool modable = host.Instance() is not null && host.Modable(host.Instance()!);
        view.Mod.Visibility = modable ? Visibility.Visible : Visibility.Collapsed; view.ModDisabled.Visibility = modable ? Visibility.Collapsed : Visibility.Visible;
    }
    public void Check(FrameworkElement sender) { if (!host.IsSyncingSelection() && sender.Tag is not null) PageChange(Id(Conversions.ToInteger(Conversion.Val(sender.Tag)))); }
    public TItem NavigationItem(TId id) { id = host.Normalize(id); return view.Items.Children.OfType<TItem>().First(item => Conversions.ToInteger(item.Tag) == Code(id)); }
    public void SyncSelection(TId id) {
        id = host.Normalize(id);
        TItem item = NavigationItem(id);
        host.SetSyncingSelection(true);
        try { host.SetPageId(id); host.SetCheckedState(item, true, false, false); }
        finally { host.SetSyncingSelection(false); }
    }
    public TPage PageGet(TId id) {
        if (Code(id) == -1) id = host.PageId(); id = host.Normalize(id); int code = Code(id);
        if (code >= 6 && code <= 10) {
            if (!host.ResourcePages.ContainsKey(id)) host.ResourcePages[id] = host.CreateResource(code - 6);
            return host.ResourcePages[id];
        }
        if (!host.ReadPage.ContainsKey(code)) throw new Exception("未知的版本设置子页面种类：" + Conversions.ToString(code));
        if (host.ReadPage[code]() is null) host.WritePage[code](host.CreatePage[code]());
        return host.ReadPage[code]()!;
    }
    public void PageChange(TId id) {
        id = host.Normalize(id); if (Code(host.PageId()) == Code(id)) return;
        if (host.Instance() is not null && host.BlocksNavigation(host.Instance()!)) { SyncSelection(host.PageId()); host.HintRed("该版本正在被修改，请稍后再切换页面！"); return; }
        if (host.Locked(host.Instance()!) && InstanceNavigationRules.IsEditingPage(Code(id))) { SyncSelection(host.PageId()); host.HintRed("文件任务进行中，可浏览版本内容，请稍后再修改版本设置。"); return; }
        host.AdjustAnimation(1);
        try { PageChangeRun(PageGet(id)); SyncSelection(id); }
        catch (Exception ex) { host.Error(ex, $"切换分页面失败（ID {id}）"); }
        finally { host.AdjustAnimation(-1); }
    }
    public void PageChangeRun(TPage target) => InstanceNavigationTransitions.Run(target, host.Transitions);
    public void RefreshTag(object tag) => Refresh(Id(Conversions.ToInteger(Conversion.Val(tag))));
    public void Refresh() => Refresh(host.CurrentSubPage());
    public void Refresh(TId id) {
        int code = Code(id); if (host.ModpackEnabled() && code == 5) return;
        if (host.Instance() is not null && host.BlocksNavigation(host.Instance()!)) { host.HintRed("该版本正在被修改，请稍后再刷新！"); return; }
        switch (code) {
            case 3: host.RefreshMods(); host.SetChecked(view.Mod, true); break;
            case 5: host.RefreshExport(); host.SetChecked(view.Export, true); break;
            case 2: host.RestartModify(); host.SetChecked(view.Modify, true); break;
            case 12: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(view.RestorePoints, true); break;
            case 14: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(view.Clone, true); break;
            case 15: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(NavigationItem(id), true); break;
            case 16: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(NavigationItem(id), true); break;
            case 11: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(view.Keybindings, true); break;
            case >= 6 and <= 10: host.RefreshSpecial[code](PageGet(id)); host.SetChecked(NavigationItem(id), true); break;
        }
    }
    public void Reset() {
        if (host.Confirm("是否要初始化该版本的版本独立设置？该操作不可撤销。", "初始化确认", "取消", true) != 1) return;
        if (host.ReadPage[1]() is null) host.WritePage[1](host.CreatePage[1]()); host.ResetSetup(host.ReadPage[1]()!); host.SetChecked(view.Setup, true);
    }
}
