using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS;

namespace PCLLF.Presentation.Launch;

public sealed class LegacyLaunchRightView {
    public Func<FrameworkElement> Root = null!, RecentCard = null!, DependencyCard = null!, DependencyAnchor = null!, AutoFillCheck = null!, LogPanel = null!;
    public Func<FrameworkElement?> DependencyPanel = null!;
    public Func<TranslateTransform> DependencyTransform = null!;
    public Func<Panel> CustomPanel = null!;
    public Func<bool> DependencyCheck = null!;
    public Func<string> PageId = null!;
    public Action ScrollToHome = null!, ResizeDependencyCard = null!;
}
public sealed class LegacyLaunchRightHost {
    public Func<string, string> ReadString = null!;
    public Func<string, int> ReadInteger = null!;
    public Action<string, object> WriteSetting = null!;
    public Func<bool> Debug = null!, IsLaunchPage = null!;
    public Func<string> BasePath = null!, TempPath = null!;
    public Action<Action> RunInThread = null!, Dispatch = null!, DispatchWait = null!, DispatchWhenLoaded = null!;
    public Action<int> ChangeAnimationControl = null!;
    public Action<FrameworkElement> DisablePageAnimation = null!, RefreshSettings = null!;
    public Action<string> Hint = null!;
    public Action ChangeToLaunchPage = null!;
    public Func<string, string> HtmlReplace = null!, XmlReplace = null!, Request = null!;
    public Func<string, UIElement> ParseXaml = null!;
    public Func<string, string, int> RetryMessage = null!;
    public Action<(string Address, bool ShouldRefresh)> StartOnline = null!, ResetOnlineInput = null!;
}
public sealed partial class LegacyLaunchRight<TInstance, TWorld, TAnimation>
    where TInstance : class where TWorld : RecentWorldData where TAnimation : struct {
    private readonly LegacyLaunchRightView view;
    private readonly LegacyLaunchRightHost host;
    private readonly LegacyLaunchAnimations<TAnimation> animation;
    private readonly Func<RecentWorldPageController<TInstance, TWorld, TAnimation>> recent;
    private bool dependencyVisible, dependencyRefreshing;
    private readonly object refreshLock = new(), contentLock = new();
    private ulong? loadedHash;
    public LegacyLaunchRight(LegacyLaunchRightView view, LegacyLaunchRightHost host, LegacyLaunchAnimations<TAnimation> animation,
        Func<RecentWorldPageController<TInstance, TWorld, TAnimation>> recent) { this.view = view; this.host = host; this.animation = animation; this.recent = recent; }
    public void InitializeRecentCard() {
        host.DisablePageAnimation(view.RecentCard()); host.DisablePageAnimation(view.DependencyCard()); recent().InitializeCard(); RefreshDependencyOptions();
    }
    public void PageEntered() { RefreshDependencyOptions(); host.DispatchWhenLoaded(UpdateRecentWorldVisibility); }
    public void Loaded() { RefreshDependencyOptions(); view.ScrollToHome(); view.LogPanel().Visibility = host.Debug() ? Visibility.Visible : Visibility.Collapsed; RefreshRecentWorlds(); }
    public void RefreshRecentWorlds() => recent().RefreshRecentWorlds();
    public void ShowRecentWorld(Tuple<TWorld, ImageSource?>? Item) => recent().ShowRecentWorld(Item);
    public void UpdateRecentWorldVisibility() => recent().UpdateRecentWorldVisibility();
    public void ApplyRecentWorldState(bool GameActive, bool Animate) => recent().ApplyRecentWorldState(GameActive, Animate);
    public void BeginRecentWorldLaunch(Func<bool> Start, bool Animate = true) => recent().BeginRecentWorldLaunch(Start, Animate);
    public void RecentWorldClick(object Sender, System.Windows.Input.MouseButtonEventArgs e) => recent().RecentWorldClick(Sender, e);
    public void RefreshDependencyOptions() {
        if (dependencyRefreshing || view.DependencyPanel() is null) return;
        dependencyRefreshing = true; host.ChangeAnimationControl(1);
        try { host.RefreshSettings(view.DependencyPanel()!); }
        finally { host.ChangeAnimationControl(-1); dependencyRefreshing = false; }
        UpdateDependencyExpansion();
    }
    public void DependencyChanged() { if (!dependencyRefreshing) UpdateDependencyExpansion(); }
    private void UpdateDependencyExpansion() {
        Visibility target = view.DependencyCheck() ? Visibility.Visible : Visibility.Collapsed;
        if (view.AutoFillCheck().Visibility == target) return;
        view.AutoFillCheck().Visibility = target; view.ResizeDependencyCard();
    }
    public void ApplyDependencyOptionsState(bool GameActive, bool Animate, bool RecentVisible) {
        view.DependencyAnchor().Margin = new Thickness(20, 0, 0, 108);
        // More Settings owns this card's visibility. A running game only disables
        // Quick Play; changing the next launch's dependency preferences remains valid.
        string name = "Dependency Options " + view.PageId();
        if (dependencyVisible == true && view.DependencyCard().Visibility == Visibility.Visible) return;
        dependencyVisible = true; animation.Stop(name); view.DependencyCard().IsHitTestVisible = true;
        view.DependencyCard().Visibility = Visibility.Visible;
        if (!Animate) { view.DependencyCard().Opacity = 1; view.DependencyTransform().X = 0; }
        else {
            animation.Start(new[] { animation.Opacity(view.DependencyCard(), 1 - view.DependencyCard().Opacity, 180, 0, LegacyLaunchEase.Linear, false), animation.TranslateX(view.DependencyCard(), -view.DependencyTransform().X, 280, 0, LegacyLaunchEase.OutBackWeak, false) }, name, false);
        }
    }
    public void Unloaded() {
        animation.Stop("Dependency Options " + view.PageId()); dependencyVisible = false; view.DependencyCard().Visibility = Visibility.Collapsed; view.DependencyCard().Opacity = 0; view.DependencyTransform().X = -24; recent().ReleaseRecentWorlds();
    }
    public void Refresh() => host.RunInThread(() => {
        try { lock (refreshLock) RefreshReal(); }
        catch (Exception error) { Logger.Error(error, "加载 PCL 主页自定义信息失败", host.Debug() ? LogBehavior.Alert : LogBehavior.Toast); }
    });
    private void RefreshReal() {
        string? content = null, url = null;
        switch (host.ReadInteger("UiCustomType")) {
            case 1: Logger.Info("主页自定义数据来源：本地文件"); content = FileUtils.TryReadAsString(host.BasePath() + @"PCL\Custom.xaml"); break;
            case 2: url = host.ReadString("UiCustomNet"); break;
            case 3: (content, url) = Preset(host.ReadInteger("UiCustomPreset")); break;
        }
        if (!string.IsNullOrWhiteSpace(url)) {
            url = host.HtmlReplace(url!);
            if (string.Equals(url ?? "", host.ReadString("CacheSavedPageUrl") ?? "", StringComparison.Ordinal) && FileUtils.Exists(host.TempPath() + @"Cache\Custom.xaml")) {
                Logger.Info("主页自定义数据来源：联网缓存文件"); content = FileUtils.TryReadAsString(host.TempPath() + @"Cache\Custom.xaml"); host.StartOnline((url!, false));
            } else {
                Logger.Info("主页自定义数据来源：联网全新下载"); host.Hint("正在加载主页……"); host.DispatchWait(() => LoadContent(null)); host.WriteSetting("CacheSavedPageVersion", ""); host.StartOnline((url!, true)); return;
            }
        }
        host.Dispatch(() => LoadContent(content));
    }
    public void OnlineLoad(Func<(string Address, bool ShouldRefresh)> Input) {
        string address = Input().Address; bool refresh = Input().ShouldRefresh;
        try {
            string versionAddress;
            if (address.Contains(".xaml")) versionAddress = address.Replace(".xaml", ".xaml.ini");
            else { versionAddress = address.BeforeFirst("?"); if (!versionAddress.EndsWithF("/")) versionAddress += "/"; versionAddress += "version"; if (address.Contains("?")) versionAddress += "?" + address.AfterFirst("?"); }
            string version = "";
            try {
                version = host.Request(versionAddress);
                if (version.Length > 1000) throw new Exception($"获取的主页版本过长（{version.Length} 字符）");
                string current = host.ReadString("CacheSavedPageVersion");
                if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(current) && version == current) { Logger.Info($"当前缓存的主页已为最新，当前版本：{version}，检查源：{versionAddress}"); return; }
                Logger.Info($"需要下载联网主页，当前版本：{version}，检查源：{versionAddress}");
            } catch (Exception error) { Logger.Warn(error, "联网获取主页版本失败"); Logger.Info($"无法检查联网主页版本，将直接下载，检查源：{versionAddress}"); }
            string content = host.Request(address); Logger.Info($"已联网下载主页，内容长度：{content.Length}，来源：{address}");
            host.WriteSetting("CacheSavedPageUrl", address); host.WriteSetting("CacheSavedPageVersion", version); FileUtils.Write(host.TempPath() + @"Cache\Custom.xaml", content);
            // VB lifted <> is Nothing when loadedHash is Nothing, so it does not trigger refresh.
            if (loadedHash.HasValue && loadedHash.Value != content.GetStableHashCode() && refresh) Refresh();
        } catch (Exception error) { Logger.Error(error, $"下载主页失败（{address}）", host.Debug() ? LogBehavior.Alert : LogBehavior.Toast); }
    }
    public void ForceRefresh() {
        Logger.Info("要求强制刷新主页"); ClearCache();
        if (host.IsLaunchPage()) { view.ScrollToHome(); Refresh(); } else host.ChangeToLaunchPage();
    }
    private void ClearCache() { loadedHash = null; host.ResetOnlineInput(("", true)); host.WriteSetting("CacheSavedPageUrl", ""); host.WriteSetting("CacheSavedPageVersion", ""); Logger.Info("已清空主页缓存"); }
    private void LoadContent(string? Content) {
        try {
            lock (contentLock) {
                ulong hash = (Content ?? "").GetStableHashCode(); if (loadedHash.HasValue && hash == loadedHash.Value) return;
                loadedHash = hash; view.CustomPanel().Children.Clear();
                if (string.IsNullOrWhiteSpace(Content)) { Logger.Info("实例化：清空主页 UI，来源为空"); return; }
                DateTime start = DateTime.Now;
                Content = host.XmlReplace(Content!);
                while (Content.Contains("xmlns")) Content = Content.RegexReplace("xmlns[^\"']*(\"|')[^\"']*(\"|')", "").Replace("xmlns", "");
                Content = "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" xmlns:core=\"clr-namespace:MeloongCore;assembly=MeloongCore\" xmlns:corewpf=\"clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf\">" + Content + "</StackPanel>";
                Logger.Info($"实例化：加载主页 UI 开始，最终内容长度：{Content.Length}"); view.CustomPanel().Children.Add(host.ParseXaml(Content));
                int cost = (DateTime.Now - start).Milliseconds; Logger.Info($"实例化：加载主页 UI 完成，耗时 {cost}ms");
                if (cost > 3000) host.Hint($"主页加载过于缓慢（花费了 {Math.Round(cost / 1000d, 1)} 秒），请向主页作者反馈此问题，或暂时停止使用该主页");
            }
        } catch (Exception error) { Logger.Warn(error, "加载失败的主页内容：\r\n" + Content); OnLoadContentFailed(error); }
    }
    private void OnLoadContentFailed(Exception error) {
        if (host.Debug() || host.ReadInteger("UiCustomType") == 1) {
            Logger.Warn(error, "加载主页失败");
            if (host.RetryMessage(error is UnauthorizedAccessException ? error.Message : "主页内容编写有误，请根据下列错误信息进行检查：\r\n" + error.GetDisplay(false), "加载主页失败") == 1) ForceRefresh();
        } else Logger.Error(error, "加载主页失败", LogBehavior.Toast);
    }
    public void DispatcherUnhandledException(DispatcherUnhandledExceptionEventArgs e) {
        if (e.Exception is not XamlParseException) return;
        e.Handled = true; LoadContent(null); OnLoadContentFailed(e.Exception);
    }
}
