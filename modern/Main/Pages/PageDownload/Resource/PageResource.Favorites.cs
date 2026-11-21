using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCLCS;

namespace PCL;

// Local project data source for the original PageResource controls and native handlers.
// The online controller, loader and typed field shape remain unchanged.
public partial class PageResource {
    private ResourceFavoriteStore FavoriteStore;
    private Action FavoriteReload;
    private ResourceTypes? FavoriteCategory;
    private IReadOnlyList<ResourceFavorite> FavoriteEntries;
    private Exception FavoriteError;
    private ResourceFavoriteFilter FavoriteQuery = new();
    private TextBlock FavoriteStatus;
    private bool FavoriteLoading, FavoriteRebuilding;

    internal void UseFavorites(ResourceFavoriteStore store, Action reload) {
        if (IsLoaded) throw new InvalidOperationException("收藏数据源必须在页面加载前设置。");
        FavoriteStore = store; FavoriteReload = reload;
        FavoriteStatus = new TextBlock { Name = "FavoriteStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(15) };
        FavoriteStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3");
        BtnSearchInstallModPack.Visibility = Visibility.Collapsed;
        TextSearchVersion.ToolTip = "按已缓存的项目兼容信息筛选；未知信息不匹配，具体文件以详情为准。";
        ComboSearchLoader.ToolTip = TextSearchVersion.ToolTip;
        Load.AutoRun = false; Load.ShowProgress = false;
        Load.State = new MyLoadingStateSimulator { LoadingState = MyLoading.MyLoadingState.Stop };
        PanLoad.Visibility = Visibility.Collapsed; PanContent.Visibility = Visibility.Visible;
        FavoritesChanged(true);
    }
    internal void ChooseFavoriteCategory(ResourceTypes? category) {
        if (FavoriteCategory == category) return;
        FavoriteCategory = category; Page = 0;
        RebuildFavoriteChoices(); ApplyFavoriteSearch(); ScrollToHome();
    }
    internal void SetFavoritesLoading(bool value) {
        FavoriteLoading = value; BtnSearchRun.IsEnabled = BtnSearchReset.IsEnabled = !value;
        Load.Text = "正在读取收藏"; Load.State.LoadingState = value ? MyLoading.MyLoadingState.Run : MyLoading.MyLoadingState.Stop;
        PanLoad.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        PanContent.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void FavoritesChanged(bool force = false) {
        if (FavoriteStore is null || FavoriteLoading) return;
        var entries = FavoriteStore.Snapshot; Exception error = FavoriteStore.LoadError ?? FavoriteStore.LastOperationError;
        if (!force && ReferenceEquals(entries, FavoriteEntries) && ReferenceEquals(error, FavoriteError)) return;
        FavoriteEntries = entries; FavoriteError = error;
        RebuildFavoriteChoices(); FavoriteQuery = ReadFavoriteQuery(); RenderFavorites();
    }
    private void RebuildFavoriteChoices() {
        string version = FavoriteVersion(), tag = FavoriteTag(); int loader = ComboSearchLoader.SelectedIndex;
        FavoriteRebuilding = true;
        try {
            string title = FavoriteCategory switch {
                ResourceTypes.Mod => "收藏的 Mod", ResourceTypes.ModPack => "收藏的整合包", ResourceTypes.Map => "收藏的地图",
                ResourceTypes.DataPack => "收藏的数据包", ResourceTypes.ResourcePack => "收藏的资源包", ResourceTypes.Shader => "收藏的光影包", _ => "全部收藏"
            };
            PanAlways.Title = "搜索" + title;
            var scope = (FavoriteEntries ?? FavoriteStore.Snapshot).Where(entry => entry.Matches(FavoriteCategory, null)).ToArray();
            ComboSearchTag.Items.Clear(); ComboSearchTag.Items.Add(new MyComboBoxItem { Content = "全部", Tag = "" });
            foreach (string value in scope.SelectMany(entry => entry.Tags).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                ComboSearchTag.Items.Add(new MyComboBoxItem { Content = value, Tag = value });
            ComboSearchTag.SelectedItem = ComboSearchTag.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag) ?? ComboSearchTag.Items[0];
            TextSearchVersion.Items.Clear(); TextSearchVersion.Items.Add(new MyComboBoxItem { Content = "全部 (也可自行输入)" });
            foreach (string value in scope.SelectMany(entry => entry.KnownGameVersions).Distinct(StringComparer.Ordinal).OrderByDescending(value => value, Comparer<string>.Create(McUtils.CompareVersion)))
                TextSearchVersion.Items.Add(new MyComboBoxItem { Content = value });
            TextSearchVersion.SelectedIndex = 0;
            if (version is not null) TextSearchVersion.Text = version;
            ComboSearchLoader.SelectedIndex = Math.Max(0, loader);
        } finally { FavoriteRebuilding = false; }
        FavoriteLoaderVisibility();
    }
    private string FavoriteVersion() {
        string value = TextSearchVersion.Text?.Trim();
        return string.IsNullOrEmpty(value) || value == "全部 (也可自行输入)" ? null : value;
    }
    private string FavoriteTag() => (ComboSearchTag.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private ResourceFavoriteFilter ReadFavoriteQuery() => new() {
        Category = FavoriteCategory, Name = TextSearchName.Text, GameVersion = FavoriteVersion(), Tag = FavoriteTag(),
        Source = (ResourcePlatforms)Convert.ToInt32((ComboSearchSource.SelectedItem as ComboBoxItem)?.Tag ?? (int)ResourcePlatforms.Any, CultureInfo.InvariantCulture),
        Loader = ComboSearchLoader.Visibility == Visibility.Visible ? (ModLoaders)Convert.ToInt32((ComboSearchLoader.SelectedItem as ComboBoxItem)?.Tag ?? 0, CultureInfo.InvariantCulture) : ModLoaders.None
    };
    private void ApplyFavoriteSearch() {
        if (FavoriteStore is null || FavoriteRebuilding) return;
        Page = 0; FavoriteQuery = ReadFavoriteQuery(); RenderFavorites(); ScrollToHome();
    }
    private void ResetFavoriteFilter() {
        FavoriteRebuilding = true;
        try {
            TextSearchName.Text = ""; TextSearchVersion.SelectedIndex = 0; TextSearchVersion.Text = "全部 (也可自行输入)"; ComboSearchSource.SelectedIndex = 0;
            ComboSearchTag.SelectedIndex = 0; ComboSearchLoader.SelectedIndex = 0;
        } finally { FavoriteRebuilding = false; }
        FavoriteLoaderVisibility(); ApplyFavoriteSearch();
    }
    private void FavoriteLoaderVisibility() {
        if (FavoriteRebuilding) return;
        bool show = FavoriteCategory == ResourceTypes.Mod && FavoriteVersion() is { } value && (value.Contains('.') || value.Contains('w'));
        ComboSearchLoader.Visibility = show ? Visibility.Visible : Visibility.Collapsed; Grid.SetColumnSpan(TextSearchVersion, show ? 1 : 2);
        if (!show) ComboSearchLoader.SelectedIndex = 0;
    }
    private void ChangeFavoritePage(int page) { Page = page; RenderFavorites(); ScrollToHome(); }
    private void RenderFavorites() {
        if (FavoriteLoading) return;
        var all = FavoriteEntries ?? FavoriteStore.Snapshot;
        var shown = all.Where(entry => FavoriteQuery.Matches(entry)).ToArray();
        int pages = Math.Max(1, (shown.Length + PAGE_SIZE - 1) / PAGE_SIZE); Page = Math.Clamp(Page, 0, pages - 1);
        PanProjects.Children.Clear();
        foreach (ResourceFavorite entry in shown.Skip(Page * PAGE_SIZE).Take(PAGE_SIZE)) {
            var project = new ResourceProject(entry.ProjectJson);
            PanProjects.Children.Add(project.ToResourceItem(FavoriteQuery.GameVersion is null, FavoriteQuery.Loader == ModLoaders.None, FavoriteCategory));
        }
        if (shown.Length == 0) {
            FavoriteStatus.Text = FavoriteError is not null && all.Count == 0 ? "收藏暂时不可用。修复收藏文件后点击搜索重试。" :
                all.Count == 0 ? "还没有收藏。点击资源项目旁的收藏按钮即可添加。" : "没有符合当前条件的收藏；未知版本信息不会计入版本筛选。";
            PanProjects.Children.Add(FavoriteStatus);
        }
        HintError.Visibility = FavoriteError is null ? Visibility.Collapsed : Visibility.Visible;
        HintError.Text = "无法读取或保存收藏，原文件已保留。点击搜索可重试。" + (FavoriteError is null ? "" : "\n" + FavoriteError.Message);
        CardPages.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        LabPage.Text = (Page + 1).ToString(CultureInfo.InvariantCulture);
        BtnPageFirst.IsEnabled = BtnPageLeft.IsEnabled = Page > 0; BtnPageFirst.Opacity = BtnPageLeft.Opacity = Page > 0 ? 1 : 0.2;
        BtnPageRight.IsEnabled = Page + 1 < pages; BtnPageRight.Opacity = BtnPageRight.IsEnabled ? 1 : 0.2;
        NotifyResultsChanged();
    }
}
