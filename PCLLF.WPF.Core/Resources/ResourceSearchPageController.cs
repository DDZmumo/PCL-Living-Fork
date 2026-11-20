using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

// LF Resource/PageResource.xaml.vb f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, LF LICENCE.
// Fixed PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 differs by LF Map scope.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Comp/PageComp.xaml.cs was
// reviewed (their Plain Craft Launcher 2/LICENCE); LF search storage, retries and scope remain authoritative.
public sealed class ResourceSearchPageController<TInstance, TState, TCard, TProject, TResult, TRequest>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum
    where TProject : ResourceProjectModelData
    where TResult : ResourceSearchResultData<TProject>, new()
    where TRequest : ResourceSearchRequestData<TProject, TResult> {
    public const int PageSize = 40;
    private readonly ResourceSearchPageHost<TInstance, TProject, TResult, TRequest> host;
    private string? typeName = "", typeNameSpaced = "";
    private ResourceTypes pageType = (ResourceTypes)(-1);
    private bool firstLoaded;
    public TInstance? DownloadTargetInstance { get; set; }
    public ResourceSearchPageController(ResourceSearchPageHost<TInstance, TProject, TResult, TRequest> host) { this.host = host; }
    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    private static object? Tag(ComboBox combo) => ((FrameworkElement)combo.SelectedItem).Tag;
    public string? TypeName { get => typeName; set { if (Same(typeName, value)) return; typeName = value; host.LoaderName("社区资源获取：" + value); } }
    public string? TypeNameSpaced { get => typeNameSpaced; set { if (Same(typeNameSpaced, value)) return; typeNameSpaced = value; ResourceSearchPageView view = host.View(); view.SearchTitle("搜索" + value); view.LoadingText("正在获取" + value + "列表"); } }
    public ResourceTypes PageType {
        get => pageType;
        set {
            if (pageType == value) return; pageType = value;
            ResourceSearchPageView view = host.View();
            view.InstallPack.Visibility = value == ResourceTypes.ModPack ? Visibility.Visible : Visibility.Collapsed;
            if (value == ResourceTypes.Map) { view.Source.Items.Clear(); view.Source.Items.Add(host.Item("CurseForge", (int)ResourcePlatforms.CurseForge, true)); }
        }
    }
    public void Loaded() {
        ResourceSearchPageView view = host.View();
        if (host.TargetInstance() is not null) {
            ResetFilter(); DownloadTargetInstance = host.TargetInstance(); view.Version.Text = host.TargetInstance()!.Version.VanillaName;
            ComboBoxItem Find(string name) => view.Loader.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Operators.ConditionalCompareObjectEqual(item.Content, name, false)) ?? (ComboBoxItem)view.Loader.Items[0];
            if (host.TargetInstance()!.Version.ModLoaders.HasFlagF(ModLoaders.Forge)) view.Loader.SelectedItem = Find("Forge");
            else if (host.TargetInstance()!.Version.ModLoaders.HasFlagF(ModLoaders.Fabric)) view.Loader.SelectedItem = Find("Fabric");
            else if (host.TargetInstance()!.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)) view.Loader.SelectedItem = Find("NeoForge");
            host.SetTargetInstance(null);
            if (host.TargetName() is not null) { view.Name.Text = host.TargetName(); host.SetTargetName(null); }
            if (firstLoaded) StartNewSearch(); view.ScrollHome();
        }
        if (firstLoaded) return; firstLoaded = true; host.InitializeLoader();
        if (host.AllDrops() is not null && host.AllDrops()!.First() > 250) {
            string highest = McVersion.DropToVersion(host.AllDrops()!.First());
            if (!Same(((ComboBoxItem)view.Version.Items[1]).Content.ToString(), highest)) view.Version.Items.Insert(1, host.Item(highest, null, false));
        }
    }
    public TRequest LoaderInput() {
        ResourceSearchPageView view = host.View();
        TRequest request = host.Request(PageType, host.Storage(), checked((host.Page() + 1) * PageSize));
        string? version = Same(view.Version.Text, "全部 (也可自行输入)") ? null : view.Version.Text.Contains(".") || view.Version.Text.Contains("w") ? view.Version.Text : null;
        request.SearchText = view.Name.Text; request.GameVersion = version; request.Tag = Conversions.ToString(Tag(view.Tag))!;
        request.ModLoaders = PageType == ResourceTypes.Mod ? (ModLoaders)Conversions.ToInteger(LegacyBaseOperations.Val(Tag(view.Loader)!)) : ModLoaders.None;
        request.Sources = PageType == ResourceTypes.Map ? ResourcePlatforms.CurseForge : (ResourcePlatforms)Conversions.ToInteger(LegacyBaseOperations.Val(Tag(view.Source)!));
        return request;
    }
    public void LoadOnFinish() {
        try {
            ResourceSearchPageView view = host.View(); TResult storage = host.Storage(); int page = host.Page();
            Logger.Info("开始可视化" + TypeNameSpaced + "列表，已储藏 " + storage.Results.Count + " 个结果，当前在第 " + (page + 1) + " 页");
            view.Projects.Children.Clear();
            int index = Math.Min(checked(page * PageSize), storage.Results.Count - 1);
            foreach (TProject result in storage.Results.GetRange(index, Math.Min(storage.Results.Count - index, PageSize))) view.Projects.Children.Add(host.ProjectItem(result, host.Input().GameVersion is null, host.Input().ModLoaders == ModLoaders.None && (PageType == ResourceTypes.Mod || PageType == ResourceTypes.ModPack)));
            view.Pages.Visibility = storage.Results.Count > 40 || storage.CurseForgeOffset < storage.CurseForgeTotal || storage.ModrinthOffset < storage.ModrinthTotal ? Visibility.Visible : Visibility.Collapsed;
            view.PageNumber.Text = Conversions.ToString(checked(page + 1));
            view.First.IsEnabled = page > 1; view.First.Opacity = page > 1 ? 1 : 0.2; view.Left.IsEnabled = page > 0; view.Left.Opacity = page > 0 ? 1 : 0.2;
            bool right = storage.Results.Count > checked(PageSize * (page + 1)) || storage.CurseForgeOffset < storage.CurseForgeTotal || storage.ModrinthOffset < storage.ModrinthTotal;
            view.Right.IsEnabled = right; view.Right.Opacity = right ? 1 : 0.2;
            if (storage.ErrorMessage is null) view.Error.Visibility = Visibility.Collapsed; else { view.Error.Visibility = Visibility.Visible; view.ErrorText(storage.ErrorMessage); }
            view.ScrollTop();
        } catch (Exception error) { Logger.Error(error, "可视化" + TypeNameSpaced + "列表出错"); }
    }
    public void LoadState() {
        if (!host.Failed()) return;
        string message = host.Error()?.Message ?? "";
        if (message.Contains("不是有效的 json 文件")) { Logger.Warn("下载的" + TypeNameSpaced + "列表 json 文件损坏，已自动重试"); host.Restart(); }
    }
    public void ChangePage(int page) {
        host.View().Pages.IsEnabled = false; host.SetPage(page); host.BackToTop(); Logger.Info(TypeName + "：切换到第 " + checked(host.Page() + 1) + " 页");
        host.RunInThread(() => { Thread.Sleep(100); host.RunInUi(() => host.View().Pages.IsEnabled = true); host.Start(); });
    }
    public void FirstPage() => ChangePage(0);
    public void PreviousPage() => ChangePage(checked(host.Page() - 1));
    public void NextPage() => ChangePage(checked(host.Page() + 1));
    public void KeyDown(Key key) { if (key == Key.Enter) StartNewSearch(); }
    public void StartNewSearch() { host.SetPage(0); if (host.ShouldStart(LoaderInput())) host.SetStorage(new TResult()); host.Start(); }
    public void ResetFilter() {
        DownloadTargetInstance = null;
        ResourceSearchPageView view = host.View(); view.Name.Text = ""; view.Version.Text = "全部 (也可自行输入)"; view.Version.SelectedIndex = 0;
        view.Source.SelectedIndex = 0; view.Tag.SelectedIndex = 0; view.Loader.SelectedIndex = 0; host.ResetFinishedTime();
    }
    public void VersionTextChanged() { if (!host.View().Version.IsDropDownOpen) UpdateSearchLoaderVisibility(); }
    public void UpdateSearchLoaderVisibility() {
        ResourceSearchPageView view = host.View();
        if (PageType == ResourceTypes.Mod && (view.Version.Text.Contains(".") || view.Version.Text.Contains("w"))) { view.Loader.Visibility = Visibility.Visible; Grid.SetColumnSpan(view.Version, 1); }
        else { view.Loader.Visibility = Visibility.Collapsed; Grid.SetColumnSpan(view.Version, 2); view.Loader.SelectedIndex = 0; }
    }
    public void InstallExistingPack() => host.InstallPack();
}
