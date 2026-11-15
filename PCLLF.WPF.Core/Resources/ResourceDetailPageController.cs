using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF Resource/PageDownloadCompDetail.xaml.vb f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, LF LICENCE.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Comp/PageDownloadCompDetail.xaml.cs
// reviewed (their Plain Craft Launcher 2/LICENCE); LF Map installation/type fallback/scope, existing
// instance selection, confirmation and native lazy cards are retained, with no CE policy imports.
public sealed partial class ResourceDetailPageController<TProject, TVersion, TCard, TResourceItem, TInstance, TState, TInstanceCard, TLoader, TNetFile, TAnimation>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TCard : FrameworkElement where TResourceItem : FrameworkElement
    where TInstance : LegacyMcInstance<TInstance, TState, TInstanceCard>
    where TState : struct, Enum where TInstanceCard : struct, Enum where TLoader : class where TNetFile : class {
    private readonly ResourceDetailPageHost<TProject, TVersion, TCard, TResourceItem> host;
    private readonly ResourceDetailDownloadHost<TLoader, TNetFile, TVersion, TInstance> download;
    private readonly DownloadPageAnimationHost<TAnimation> animation;
    public TResourceItem? ResourceItem;
    public TInstance? DownloadTargetInstance;
    public ResourceTypes TargetResourceType;
    public TProject Project = null!;
    public string? TargetVersion, VersionFilter;
    public ModLoaders TargetLoaders;
    public bool GroupedDrop, GroupedOld;
    private bool firstInit = true;
    public ResourceDetailPageController(ResourceDetailPageHost<TProject, TVersion, TCard, TResourceItem> host, ResourceDetailDownloadHost<TLoader, TNetFile, TVersion, TInstance> download, DownloadPageAnimationHost<TAnimation> animation) { this.host = host; this.download = download; this.animation = animation; }
    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    public static string? ChooseModDownloadFolder(TInstance? target, TInstance? selected, string? cached, Func<TInstance,bool> suitable) {
        foreach (TInstance? instance in new[] { target, selected }) if (instance is not null && suitable(instance)) return System.IO.Path.Combine(instance.PathIndie,"mods") + System.IO.Path.DirectorySeparatorChar;
        return string.IsNullOrWhiteSpace(cached) ? null : cached;
    }
    public void LoadPageArguments() {
        Project = (TProject)host.Argument(0)!; TargetVersion = Conversions.ToString(host.Argument(2)); TargetLoaders = (ModLoaders)Conversions.ToInteger(host.Argument(3));
        if (TargetLoaders != ModLoaders.None && TargetLoaders.Flags().Count() != 1) TargetLoaders = TargetLoaders.Flags().First();
        TargetResourceType = (ResourceTypes)Conversions.ToInteger(host.Argument(4));
        object[]? arguments = host.Arguments();
        DownloadTargetInstance = arguments is not null && arguments.Length > 5 ? arguments[5] as TInstance : null;
    }
    public void LoadFiles(Func<bool> canceled, Action<List<TVersion>> output) { LoadPageArguments(); List<TVersion> result = host.ProjectFiles(Project.Id, Project.Platform); if (!canceled()) output(result); }
    public void Initialized() { LoadPageArguments(); host.Initialize(); }
    public void LoadState() {
        if (!host.Failed()) return;
        if ((host.Error()?.Message ?? "").Contains("不是有效的 json 文件")) { Logger.Warn("下载的文件 json 列表损坏，已自动重试"); host.Restart(false); }
    }
    public List<TVersion> GetResults() {
        List<TVersion> results = host.Files();
        ResourceTypes type = TargetResourceType == 0 ? Project is null ? ResourceTypes.Any : Project.Types : TargetResourceType;
        switch (type) {
            case ResourceTypes.Any: return results.Where(file => file.ResourceType != ResourceTypes.Plugin).ToList();
            case ResourceTypes.Shader: case ResourceTypes.ResourcePack: return results;
            default: return results.Where(file => file.ResourceType == type).ToList();
        }
    }
    public void LoadOnFinish() {
        List<TVersion> results = GetResults(); List<string> filters = null!;
        foreach (var grouping in new[] { (Drop: false, Old: false), (Drop: true, Old: false), (Drop: false, Old: true), (Drop: true, Old: true) }) {
            GroupedDrop = grouping.Drop; GroupedOld = grouping.Old;
            filters = results.SelectMany(file => file.GameVersions).Select(version => GetGroupedVersionName(version, GroupedDrop, GroupedOld)).Distinct().OrderByDescending(value => value, Comparer<string>.Create((x, y) => McUtils.CompareVersion(x, y))).ToList();
            if (filters.Count < 9) break;
        }
        ResourceDetailPageView view = host.View(); view.Filter.Children.Clear();
        if (filters.Count < 2) { view.FilterCard.Visibility = Visibility.Collapsed; VersionFilter = null; }
        else {
            view.FilterCard.Visibility = Visibility.Visible; filters.Insert(0, "全部");
            foreach (string version in filters) view.Filter.Children.Add(host.FilterButton(version, value => { VersionFilter = Same(value, "全部") ? null : value; UpdateFilterResult(); }));
            FrameworkElement? selected = null;
            if (!Same(TargetVersion, "")) {
                TVersion? file = results.FirstOrDefault(value => value.GameVersions.Contains(TargetVersion!));
                if (file is not null) {
                    string target = GetGroupedVersionName(TargetVersion, GroupedDrop, GroupedOld);
                    foreach (FrameworkElement button in view.Filter.Children) if (Same(host.FilterText(button), target)) { selected = button; break; }
                }
            }
            selected ??= (FrameworkElement)view.Filter.Children[0]; host.FilterChecked(selected, true);
        }
        UpdateFilterResult();
    }
    public void UpdateFilterResult() {
        List<TVersion> results = GetResults();
        string targetCard = !Same(TargetVersion, "") || TargetLoaders != ModLoaders.None ? (TargetLoaders != ModLoaders.None ? TargetLoaders + " " : "") + TargetVersion + "（所选版本）" : "";
        SortedDictionary<string, List<TVersion>> groups = new(new ResourceFileCardSorter(targetCard)) { ["其他"] = new() };
        foreach (TVersion version in results) foreach (string gameVersion in version.GameVersions) {
            if (VersionFilter is not null && !Same(GetGroupedVersionName(gameVersion, GroupedDrop, GroupedOld), VersionFilter)) continue;
            string name = GetGroupedVersionName(gameVersion, false, false);
            List<string> loaders = new();
            if (Project.ModLoaders.Flags().Count() > 1 && version.ResourceType == ResourceTypes.Mod && McVersion.IsStandardFormat(name)) loaders.AddRange(version.ModLoaders.Flags().Select(value => value + " "));
            if (!loaders.Any()) loaders.Add("");
            foreach (string loader in loaders) {
                string card = loader + name; if (!groups.ContainsKey(card)) groups.Add(card, new());
                if (!groups[card].Contains(version)) groups[card].Add(version);
            }
        }
        if (!Same(targetCard, "") && (VersionFilter is null || GetGroupedVersionName(TargetVersion, GroupedDrop, GroupedOld).StartsWithF(VersionFilter))) {
            groups.Add(targetCard, new());
            foreach (TVersion version in results) if (version.GameVersions.Contains(TargetVersion!) && (TargetLoaders == ModLoaders.None || TargetLoaders.Flags().Intersect(version.ModLoaders.Flags()).Any())) {
                if (VersionFilter is not null && !version.GameVersions.Any(value => Same(GetGroupedVersionName(value, GroupedDrop, GroupedOld), VersionFilter))) continue;
                if (!groups[targetCard].Contains(version)) groups[targetCard].Add(version);
            }
        }
        try {
            ResourceDetailPageView view = host.View(); view.Results.Children.Clear();
            foreach (var pair in groups) {
                if (!pair.Value.Any() || Same(pair.Key, targetCard.Replace("（所选版本）", ""))) continue;
                StackPanel stack = new() { Margin = new Thickness(20, host.SwappedHeight(), 18, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0, 0), Tag = pair.Value };
                TCard card = host.Card(pair.Key, stack); host.SetBuilder(card, InitializeStackResourceVersions); view.Results.Children.Add(card);
                if (Same(pair.Key, targetCard) || (host.HasArguments() && ((List<string>)host.Argument(1)!).Contains(host.CardTitle(card)))) host.BuildCard(card); else host.SwapCard(card, true);
                if (Same(pair.Key, "其他")) stack.Children.Add(CreateOldVersionHint());
            }
            if (view.Results.Children.Count == 1) host.SwapCard((TCard)view.Results.Children[0], false);
            if (Project.Types == ResourceTypes.ModOrDataPack && (TargetResourceType == ResourceTypes.Mod || TargetResourceType == ResourceTypes.DataPack)) {
                view.Alternative.Visibility = Visibility.Visible; view.AlternativeText(TargetResourceType == ResourceTypes.Mod ? "以下是该项目的 Mod 版本。点击这里查看其数据包版本。" : "以下是该项目的数据包版本。点击这里查看其 Mod 版本。");
            } else view.Alternative.Visibility = Visibility.Collapsed;
        } catch (Exception error) { Logger.Error(error, "可视化工程下载列表出错"); }
    }
    public void InitializeStackResourceVersions(StackPanel stack) {
        List<TVersion> versions = LegacyMinecraftListOrder.SortByComparison((List<TVersion>)stack.Tag, (a, b) => a.ReleaseDate > b.ReleaseDate);
        bool duplicates = versions.Select(file => file.Display).Distinct().Count() != versions.Count;
        List<string> dependencies = TargetResourceType == ResourceTypes.ModPack ? new() : versions.SelectMany(file => file.Dependencies).Distinct().Ordered().ToList();
        if (dependencies.Any()) {
            dependencies = dependencies.Where(id => { if (!host.HasProject(id)) Logger.Warn("未找到 ID " + id + " 的前置信息"); return host.HasProject(id); }).ToList();
            stack.Children.Add(new TextBlock { Text = "前置资源", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 2, 0, 5) });
            foreach (string id in dependencies) stack.Children.Add(host.ProjectRow(host.CachedProject(id)));
            stack.Children.Add(new TextBlock { Text = "版本列表", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 12, 0, 5) });
        }
        foreach (TVersion version in versions) {
            if (TargetResourceType == ResourceTypes.ModPack) stack.Children.Add(host.VersionRow(version, InstallClick, SaveClick, duplicates));
            else stack.Children.Add(host.VersionRow(version, (row, args) => SaveClick(row, args), null, duplicates));
        }
        stack.Children.Add(new FrameworkElement { Height = 18 }); stack.Tag = null;
    }
    private FrameworkElement CreateOldVersionHint() => host.YellowHint("由于版本信息更新缓慢，可能无法识别刚更新的 MC 版本。几天后即可正常识别。", new Thickness(5, 0, 0, 8));
    public static string GetGroupedVersionName(string? name, bool byDrop, bool foldOld) {
        int drop = McVersion.VersionToDrop(name);
        if (name is null) return "其他"; if (name.Contains("w") || drop == 209) return "快照版";
        if (!McVersion.IsStandardFormat(name) || (foldOld && drop < 120)) return "远古版";
        return byDrop ? McVersion.DropToVersion(drop) : name;
    }
    public void Init() {
        animation.SetDisabled(checked(animation.Disabled() + 1)); Project = (TProject)host.Argument(0)!; ResourceDetailPageView view = host.View(); view.ScrollHome();
        if (firstInit) firstInit = false; else host.Restart(true);
        if (ResourceItem is not null) view.Intro.Children.Remove(ResourceItem);
        ResourceItem = host.Header(Project); host.HeaderInteractive(ResourceItem, false); ResourceItem.Margin = new Thickness(-7, -7, 0, 8); view.Intro.Children.Insert(0, ResourceItem);
        view.WebText("转到 " + Project.Platform); view.Wiki.Visibility = Project.WikiId == 0 ? Visibility.Collapsed : Visibility.Visible;
        animation.SetDisabled(checked(animation.Disabled() - 1));
    }
    public void IntroWeb() => host.Website(Project.Website!);
    public void IntroWiki() => host.Website("https://www.mcmod.cn/class/" + Project.WikiId + ".html");
    public void IntroCopy() { var name = host.HeaderName(ResourceItem!); host.Clipboard(name.Title + name.Raw); }
    public void AlternativeClick() { TargetResourceType = TargetResourceType == ResourceTypes.Mod ? ResourceTypes.DataPack : ResourceTypes.Mod; host.SetArgument(4, TargetResourceType); host.Restart(true); }
}
public class ResourceFileCardSorter : IComparer<string> {
    public string Topmost = "";
    public ResourceFileCardSorter(string? topmost = "") { Topmost = topmost ?? ""; }
    public int Compare(string? x, string? y) {
        bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        if (Same(x, y)) return 0; if (Same(x, Topmost)) return -1; if (Same(y, Topmost)) return 1;
        bool xs = !x!.Contains("."), ys = !y!.Contains(".");
        if (xs && ys) return x.CompareTo(y); if (xs) return 1; if (ys) return -1;
        int sort = -McUtils.CompareVersion(x.Replace(x.BeforeFirst(" ") + " ", ""), y.Replace(y.BeforeFirst(" ") + " ", ""));
        return sort != 0 ? sort : -McUtils.CompareVersion(x, y);
    }
}
