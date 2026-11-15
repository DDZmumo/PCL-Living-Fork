using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed class InstanceSelectionPageView {
    public Panel Main = null!;
    public FrameworkElement Back = null!, Empty = null!, Download = null!;
    public TextBlock EmptyTitle = null!, EmptyContent = null!;
    public Action ScrollHome = null!, ForceResize = null!;
}
public sealed class InstanceSelectionPageHost<TInstance, TCard> where TInstance : class where TCard : struct, Enum {
    public Func<InstanceSelectionPageView> View = null!;
    public Func<bool> ShowHidden = null!, ModpackEnabled = null!, HiddenDownload = null!, HiddenForceShow = null!, LoaderFailed = null!, Shift = null!;
    public Action<Action> InitializeLoader = null!;
    public Action<bool> RefreshInstances = null!;
    public Func<IDictionary<TCard, List<TInstance>>> Instances = null!;
    public Action<Dictionary<TCard, List<TInstance>>> SetInstances = null!;
    public Func<TInstance?> SelectedInstance = null!, InstancePage = null!;
    public Action<TInstance?> SetSelectedInstance = null!, SetInstancePage = null!;
    public Func<string> MinecraftFolder = null!;
    public Func<string, TInstance> NewInstance = null!;
    public Func<TInstance, bool> Locked = null!, BlocksNavigation = null!;
    public Func<string, bool> AllowsInstance = null!;
    public Action ForceRefresh = null!, PageBack = null!, OpenDownload = null!;
    public Action<TInstance> OpenInstanceFolder = null!, OpenInstanceSetup = null!;
    public Action<string, SelectionPageHint> Hint = null!;
    public Func<SelectionPageMessage, int> Message = null!;
    public Func<string, string, double, Visibility, Action, FrameworkElement> Button = null!;
    public Action<FrameworkElement, IEnumerable<FrameworkElement>> SetButtons = null!;
    public Action<FrameworkElement, Action<FrameworkElement, EventArgs>> BindClick = null!;
    public Action<FrameworkElement, Action> BindRightClick = null!;
    public Action<string, string, object> WriteIni = null!;
    public Action<TInstance, bool, Action> DeleteConfirmed = null!;
}
// Complete LF PageSelectRight workflow, including clone groups and guarded deletion.
// LF source and LICENCE; C012 clone presenter is reused directly, not reconstructed or copied.
public sealed class InstanceSelectionPageController<TInstance, TState, TCard, TNode, TSet>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum
    where TNode : InstanceCloneGroupNodeData<TNode> where TSet : InstanceCloneGroupSetData<TNode> {
    private readonly InstanceSelectionPageHost<TInstance, TCard> host;
    private readonly InstanceCloneSelectionPresenter<TInstance, TCard, TNode, TSet> clones;
    public InstanceSelectionPageController(InstanceSelectionPageHost<TInstance, TCard> host, InstanceCloneSelectionPresenter<TInstance, TCard, TNode, TSet> clones) { this.host = host; this.clones = clones; }
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    public void Loaded() { host.RefreshInstances(false); host.View().ScrollHome(); }
    public void Initialize() => host.InitializeLoader(Render);
    public void LoadClick() { if (host.LoaderFailed()) host.RefreshInstances(true); }
    public void Render() {
        try {
            InstanceSelectionPageView view = host.View(); view.Main.Children.Clear();
            var grouping = clones.Prepare(host.Instances(), host.ShowHidden);
            foreach (var card in grouping.VisibleCards) {
                List<TNode> roots = grouping.Roots(card.Key, card.Value); if (roots.Count == 0) continue;
                List<TInstance> instances = grouping.Instances(roots); string name;
                switch (Convert.ToInt32(card.Key)) {
                    case 3: name = "常规版本"; break;
                    case 2:
                        bool forge = false, neo = false, fabric = false, lite = false;
                        foreach (TInstance instance in instances) {
                            if (instance.Version.ModLoaders.HasFlagF(ModLoaders.Fabric)) fabric = true;
                            if (instance.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) lite = true;
                            if (instance.Version.ModLoaders.HasFlagF(ModLoaders.Forge)) forge = true;
                            if (instance.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)) neo = true;
                        }
                        name = (lite ? 1 : 0) + (forge ? 1 : 0) + (fabric ? 1 : 0) + (neo ? 1 : 0) > 1 ? "可安装 Mod" : forge ? "Forge 版本" : neo ? "NeoForge 版本" : lite ? "LiteLoader 版本" : "Fabric 版本"; break;
                    case 6: name = "错误的版本"; break; case 1: name = "隐藏的版本"; break; case 4: name = "不常用版本"; break; case -1: name = "收藏夹"; break; case 5: name = "愚人节版本"; break;
                    default: throw new ArgumentException("未知的卡片种类（" + card.Key + "）");
                }
                clones.RenderCategory(view.Main, card.Key, name, roots, instances.Count, grouping);
            }
            clones.ExpandSingle(view.Main);
            if (view.Main.Children.Count == 0) {
                view.Empty.Visibility = Visibility.Visible; view.Back.Visibility = Visibility.Collapsed;
                if (host.ShowHidden()) { view.EmptyTitle.Text = "无隐藏版本"; view.EmptyContent.Text = "没有版本被隐藏，你可以在版本设置的版本分类选项中隐藏版本。\r\n再次按下 F11 即可退出隐藏版本查看模式。"; view.Download.Visibility = Visibility.Collapsed; }
                else {
                    view.EmptyTitle.Text = "无可用版本"; view.EmptyContent.Text = "未找到任何版本的游戏，请先下载任意版本的游戏。\r\n若有已存在的游戏，请在左边的列表中选择添加文件夹，选择 .minecraft 文件夹将其导入。";
                    if (host.ModpackEnabled()) view.EmptyContent.Text = "当前整合包目录中没有可用的游戏版本。请确认整合包已完整解压，或重新运行附带安装文件的启动器完成安装。";
                    view.Download.Visibility = host.ModpackEnabled() || (host.HiddenDownload() && !host.HiddenForceShow()) ? Visibility.Collapsed : Visibility.Visible;
                }
            } else { view.Back.Visibility = Visibility.Visible; view.Empty.Visibility = Visibility.Collapsed; }
        } catch (Exception error) { Logger.Error(error, "将版本列表转换显示时失败"); }
    }
    public static void RowContent(FrameworkElement row, InstanceSelectionPageHost<TInstance, TCard> host) {
        TInstance instance = (TInstance)row.Tag;
        host.BindClick(row, (item, args) => ItemClick(item, host));
        FrameworkElement star = host.Button(instance.IsStar ? "like-fill" : "like-line", instance.IsStar ? "取消收藏" : "收藏", 1.1, host.ModpackEnabled() ? Visibility.Collapsed : Visibility.Visible, () => {
            host.WriteIni(instance.PathVersion + "PCL\\Setup.ini", "IsStar", !instance.IsStar); host.ForceRefresh(); host.RefreshInstances(true);
        });
        FrameworkElement delete = host.Button("delete", "删除", 1.1, host.ModpackEnabled() ? Visibility.Collapsed : Visibility.Visible, () => Delete(row, instance, host));
        FrameworkElement content;
        if (Convert.ToInt32(instance.State) != 0) {
            content = host.Button("setup", "设置", 1.1, Visibility.Visible, () => host.OpenInstanceSetup(instance));
            host.BindRightClick(row, () => host.OpenInstanceSetup(instance));
        } else {
            content = host.Button("open", "打开文件夹", 1.15, Visibility.Visible, () => host.OpenInstanceFolder(instance));
            host.BindRightClick(row, () => host.OpenInstanceFolder(instance));
        }
        host.SetButtons(row, new[] { star, delete, content });
    }
    public static void ItemClick(FrameworkElement row, InstanceSelectionPageHost<TInstance, TCard> host) {
        TInstance instance = (TInstance)row.Tag;
        if (!host.AllowsInstance(instance.PathVersion)) return;
        if (host.BlocksNavigation(instance)) { host.Hint("该版本正在被修改，请稍后再选择！", SelectionPageHint.Red); return; }
        if (host.NewInstance(instance.PathVersion).Check()) { host.SetSelectedInstance(instance); host.PageBack(); }
        else host.OpenInstanceFolder(instance);
    }
    public static void Delete(FrameworkElement? item, TInstance? instance, InstanceSelectionPageHost<TInstance, TCard> host) {
        if (host.ModpackEnabled()) return;
        try {
            instance = ResolveDeleteTarget(item, instance);
            if (host.Locked(instance)) { host.Hint("该版本正在被修改，请稍后再删除！", SelectionPageHint.Red); return; }
            bool shift = host.Shift(), hintIndie = Convert.ToInt32(instance.State) != 0 && !Same(instance.PathIndie, host.MinecraftFolder());
            DirectoryInfo savesFolder = DirectoryUtils.GetInfo(instance.PathIndie + "saves\\");
            List<string> saves = hintIndie && savesFolder.Exists ? savesFolder.EnumerateDirectories().OrderByDescending(save => save.LastWriteTime).Select(save => $"{save.Name}（上次修改：{StringUtils.FormatTimeSpan(save.LastWriteTime - DateTime.Now, false)}）").ToList() : new();
            int choice = host.Message(new SelectionPageMessage { Text = $"你确定要{(shift ? "永久" : "")}删除版本 {instance.Name} 吗？" + (hintIndie ? "\r\n该版本对应的存档、资源包、Mod 等文件也将被一并删除！" : "") + (saves.Any() ? "\r\n\r\n这会删除以下存档：\r\n· " + string.Join("\r\n· ", saves) : ""), Title = "版本删除确认", Button2 = "取消", IsWarn = true });
            if (choice != 1) return;
            TInstance target = instance;
            target.ResetSettingsCache();
            host.DeleteConfirmed(target, !shift, () => {
                host.Hint("版本 " + target.Name + " 已删除！", SelectionPageHint.Green);
                host.SetInstances(WithoutDeletedInstance(host.Instances(), target.PathVersion));
                TInstance? current = host.SelectedInstance();
                TInstance? next = SelectionAfterDelete(current, target.PathVersion);
                if (!ReferenceEquals(next, current)) host.SetSelectedInstance(next);
                if (host.InstancePage() is TInstance shown && PathUtils.IsSame(shown.PathVersion, target.PathVersion)) host.SetInstancePage(null);
                host.ForceRefresh(); host.RefreshInstances(true);
            });
        } catch (OperationCanceledException error) { Logger.Warn(error, "删除版本 " + (instance?.Name ?? "未知版本") + " 被主动取消"); }
        catch (Exception error) { Logger.Error(error, "删除版本 " + (instance?.Name ?? "未知版本") + " 失败", LogBehavior.Alert); }
    }
    public static TInstance ResolveDeleteTarget(FrameworkElement? item, TInstance? expected) {
        TInstance? actual = item?.Tag as TInstance;
        if (actual is null || expected is null || !PathUtils.IsSame(actual.PathVersion, expected.PathVersion)) throw new IOException("版本列表已变化，请刷新后重新选择要删除的版本。");
        return actual;
    }
    public static Dictionary<TCard, List<TInstance>> WithoutDeletedInstance(IDictionary<TCard, List<TInstance>> cards, string deletedFolder) {
        Dictionary<TCard, List<TInstance>> result = new();
        foreach (var card in cards) { List<TInstance> remaining = card.Value.Where(instance => !PathUtils.IsSame(instance.PathVersion, deletedFolder)).ToList(); if (remaining.Count > 0) result.Add(card.Key, remaining); }
        return result;
    }
    public static TInstance? SelectionAfterDelete(TInstance? current, string deletedFolder) => current is not null && PathUtils.IsSame(current.PathVersion, deletedFolder) ? null : current;
    public void OpenDownload() => host.OpenDownload();
    public void EmptyDownloadLoaded() {
        Visibility visibility = (host.HiddenDownload() && !host.HiddenForceShow()) || host.ShowHidden() ? Visibility.Collapsed : Visibility.Visible;
        InstanceSelectionPageView view = host.View(); if (view.Download.Visibility != visibility) { view.Download.Visibility = visibility; view.ForceResize(); }
    }
}
