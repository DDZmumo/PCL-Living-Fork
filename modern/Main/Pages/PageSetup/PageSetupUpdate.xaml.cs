using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCL;

public partial class PageSetupUpdate : MyPageRight
{
    private bool refreshing;
    private int revision;
    private int headerLayout = -1;
    private bool catalogLoaded;
    private List<JObject> catalog = new List<JObject>();
    private string catalogError;
    internal JObject LatestUpdateRelease { get; private set; }
    internal Func<ModLauncherUpdate.UpdateSource, ModLauncherUpdate.UpdateChannel, List<JObject>> ReadReleases =
        (source, channel) => ModLauncherUpdate.GetPageReleases(source, channel);
    internal Action<JObject> UpdateRelease = release => ModLauncherUpdate.RequestSelectedUpdate(release, ModLauncherUpdate.PreferredUpdateSource());
    internal Action ShowCurrentUpdateLog = () => ModMain.FrmMain?.ShowUpdateLog();

    public PageSetupUpdate()
    {
        InitializeComponent();
        CardLauncherUpdate.BorderChild = new LauncherUpdateCardBackground();
        using (Stream icon = System.Windows.Application.GetResourceStream(new Uri(ModBase.PathImage + "Branding/LivingPCL.ico")).Stream)
        {
            var decoder = new IconBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames.OrderByDescending(value => value.PixelWidth * value.PixelHeight).First();
            frame.Freeze(); ImgLauncher.Source = frame;
        }
        HeaderGrid.SizeChanged += (_, args) => RefreshHeaderLayout(args.NewSize.Width);
        RefreshSelection();
        ComboSource.SelectionChanged += (_, _) =>
        {
            if (refreshing || ComboSource.SelectedIndex < 0) return;
            Settings.Set("ToolLauncherUpdateSource", ComboSource.SelectedIndex);
            RefreshReleases();
        };
        ComboChannel.SelectionChanged += (_, _) =>
        {
            if (refreshing || ComboChannel.SelectedIndex < 0) return;
            if (!ModLauncherUpdate.ChangeUpdateChannel(ComboChannel.SelectedIndex, () =>
                ModMain.MyMsgBox("预发布版本可能包含尚未完成验证的改动。确定接收正式版和预发布版更新吗？", "启用预发布更新", "继续开启", "取消", IsWarn: true) == 1))
                RefreshSelection();
            ApplyFilters();
        };
        TextSearchVersion.TextChanged += (_, _) => ApplyFilters();
        BtnRefresh.Click += (_, _) => RefreshReleases();
        BtnReleasePage.Click += (_, _) => ModBase.OpenWebsite(ModLauncherUpdate.PreferredUpdateSource() == ModLauncherUpdate.UpdateSource.Cnb
            ? ModBase.LauncherCnbUrl + "/-/releases" : ModBase.LauncherReleasesUrl);
        Loaded += (_, _) =>
        {
            ModMain.FrmSetupLeft?.ItemUpdate.SetChecked(true, false, false);
            RefreshSelection(); RefreshReleases();
        };
        Unloaded += (_, _) => Interlocked.Increment(ref revision);
        ApplyFilters();
    }

    private void RefreshSelection()
    {
        refreshing = true;
        try
        {
            LabCurrentVersion.Text = ModBase.VersionBaseName;
            ComboSource.SelectedIndex = (int)ModLauncherUpdate.PreferredUpdateSource();
            ComboChannel.SelectedIndex = (int)ModLauncherUpdate.PreferredUpdateChannel();
        }
        finally { refreshing = false; }
    }

    private void RefreshHeaderLayout(double width)
    {
        if (width <= 0) return;
        int layout = width >= 700 ? 0 : width >= 600 ? 1 : 2;
        if (headerLayout == layout) return;
        headerLayout = layout;
        bool compact = layout != 0;
        bool verticalActions = layout == 2;
        Grid.SetRowSpan(PanIdentity, compact ? 1 : 2);
        Grid.SetColumn(PanUpdateOptions, compact ? 0 : 2);
        Grid.SetRow(PanUpdateOptions, compact ? 1 : 0);
        Grid.SetColumnSpan(PanUpdateOptions, compact ? 3 : 1);
        PanUpdateOptions.Width = compact ? double.NaN : 320;
        PanUpdateOptions.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(0);
        Grid.SetRow(PanUpdateActions, compact ? 0 : 1);
        PanUpdateActions.Margin = compact ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        PanUpdateActions.Orientation = verticalActions ? Orientation.Vertical : Orientation.Horizontal;
        BtnRefresh.Margin = verticalActions ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 16, 0);
    }

    internal void RefreshReleases()
    {
        int current = Interlocked.Increment(ref revision);
        var source = ModLauncherUpdate.PreferredUpdateSource();
        var dispatcher = Dispatcher;
        BtnRefresh.IsEnabled = false; Loading.Visibility = Visibility.Visible;
        catalogLoaded = false; catalog.Clear(); catalogError = null; ApplyFilters();
        Task.Run(() =>
        {
            List<JObject> releases = null; string error = null;
            // Browsing release types does not change the subscribed update channel.
            try { releases = ReadReleases(source, ModLauncherUpdate.UpdateChannel.StableAndPrerelease); }
            catch (Exception failure) { error = PclLogger.ModernizeRuntimeMessage(failure.GetDisplay(false)); }
            dispatcher.InvokeAsync(() =>
            {
                if (current != Volatile.Read(ref revision)) return;
                BtnRefresh.IsEnabled = true; Loading.Visibility = Visibility.Collapsed;
                RenderReleases(releases ?? new List<JObject>(), error);
            });
        });
    }

    internal void RenderReleases(List<JObject> releases, string error)
    {
        catalog = releases.Select(value => (JObject)value.DeepClone()).ToList();
        catalogError = error; catalogLoaded = true;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        bool loading = Loading.Visibility == Visibility.Visible;
        LatestUpdateRelease = null;
        PanVersions.Children.Clear();
        PanVersions.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        HintStatus.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LabResultCount.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LabNoResults.Visibility = Visibility.Collapsed;
        if (loading)
        {
            LabBuildStatus.Text = "正在检查更新……";
            return;
        }
        bool development = ModBase.VersionBaseName == "dev_edition";
        int currentType = development ? 3 : ModBase.VersionBaseName.Contains('-') ? 2 : 1;
        var visibleRows = new List<MyListItem>();
        if (MatchesSearch(ModBase.VersionBaseName, "PCL Living Fork"))
        {
            var current = new MyListItem { Height = 58, Title = ModBase.VersionBaseName,
                Info = development ? "当前构建 · 本机开发版" : "当前已安装版本", Type = MyListItem.CheckType.Clickable };
            current.Click += (_, _) => ShowCurrentUpdateLog();
            PrepareVersionRow(current, currentType);
            MarkCurrentVersion(current);
            visibleRows.Add(current);
        }
        var channel = ModLauncherUpdate.PreferredUpdateChannel();
        int total = 1;
        foreach (JObject release in catalog.OrderByDescending(value => value["tag_name"]?.ToString(), Comparer<string>.Create(ModLauncherUpdate.CompareReleaseVersions)))
        {
            string tag = release["tag_name"]?.ToString();
            int comparison = ModLauncherUpdate.CompareReleaseVersions(tag, ModBase.VersionBaseName);
            if (!development && comparison == 0) continue;
            total++;
            bool preview = (bool?)release["prerelease"] == true;
            bool outsideChannel = preview && channel == ModLauncherUpdate.UpdateChannel.Stable;
            bool installable = ModLauncherUpdate.CanInstallPageRelease(release);
            bool newer = installable && ModLauncherUpdate.NewerReleaseForChannel(tag, ModBase.VersionBaseName, channel);
            if (newer) LatestUpdateRelease ??= release;
            if (!MatchesSearch(tag, release["name"]?.ToString() ?? "")) continue;
            var row = new MyListItem { Height = 58, Title = tag,
                Info = newer ? "可更新 · 点击下载并更新" : outsideChannel ? "当前更新通道未启用 · 可查看更新说明" : comparison < 0 ? "历史版本 · 可查看更新说明" : "已发布 · 可查看更新说明",
                Type = MyListItem.CheckType.Clickable, Tag = release };
            if (!installable) row.Info = "仅可查看更新说明 · 无自动更新附件";
            PrepareVersionRow(row, preview ? 2 : 1);
            row.Click += (_, _) =>
            {
                if (newer) UpdateRelease(release);
                else ModMain.MyMsgBoxContent(
                    outsideChannel ? "当前仅接收正式版更新。可在上方更新通道中开启预发布版更新。" : !installable ? "此发布没有可用于自动更新的完整附件。" : "当前没有比此构建更新的版本。",
                    tag + " 更新说明", () => new ReleaseNotesView(release["body"]?.ToString()));
            };
            visibleRows.Add(row);
        }
        foreach (MyListItem row in visibleRows.OrderByDescending(value => value.Title, Comparer<string>.Create(ModLauncherUpdate.CompareReleaseVersions)))
            PanVersions.Children.Add(row);
        LabResultCount.Text = $"共 {total} 个版本，显示 {PanVersions.Children.Count} 个";
        LabNoResults.Visibility = PanVersions.Children.Count == 0 && catalogLoaded && catalogError is null ? Visibility.Visible : Visibility.Collapsed;
        bool anyNewer = LatestUpdateRelease is not null;
        HintStatus.Text = catalogError is not null ? "读取版本列表失败：" + catalogError : !catalogLoaded ? "点击“检查更新”获取完整版本列表。" : anyNewer ? "有可用更新，点击版本下载并更新。" : "当前更新通道没有更新的版本。";
        LabBuildStatus.Text = catalogError is not null ? "检查更新未完成" : development ? "本机开发构建" : anyNewer ? "有新版本可用" : !catalogLoaded ? "尚未检查更新" : "已是最新版本";
    }

    private bool MatchesSearch(string tag, string name)
    {
        string query = TextSearchVersion.Text.Trim();
        if (query.Length > 1 && (query[0] == 'v' || query[0] == 'V') && char.IsDigit(query[1])) query = query.Substring(1);
        return (tag + " " + name).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void MarkCurrentVersion(MyListItem row)
    {
        var green = new SolidColorBrush(Color.FromRgb(39, 139, 84));
        var frame = new Border { Tag = "current-version-frame", BorderBrush = green, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 1, 0, 1), IsHitTestVisible = false };
        Grid.SetColumnSpan(frame, 5); Grid.SetRowSpan(frame, 4); Panel.SetZIndex(frame, 1); row.Children.Add(frame);
        var badge = new Border { Tag = "current-version-badge", CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(229, 246, 236)), IsHitTestVisible = false,
            Child = new TextBlock { Text = "当前版本", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = green } };
        row.Inlines.InsertBefore(row.Inlines.LastInline, new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
    }

    private void PrepareVersionRow(MyListItem row, int type)
    {
        row.Logo = ModBase.PathImage + "Branding/LivingPCL.ico";
        // Reserve a 48 x 48 area inside the native row's logo margins.
        row.ColumnLogo.MinWidth = 55;
        if (row.PathLogo is Canvas canvas)
        {
            canvas.Background = new ImageBrush(ImgLauncher.Source) { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.HighQuality);
        }
        var label = new TextBlock { Text = type == 3 ? "开发版" : type == 2 ? "预发布版" : "正式版", FontSize = 11, FontWeight = FontWeights.SemiBold };
        var badge = new Border { Tag = "release-channel", Child = label, CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false };
        if (type == 1) { label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3"); badge.SetResourceReference(Border.BackgroundProperty, "ColorBrush8"); }
        else
        {
            label.Foreground = new SolidColorBrush(type == 2 ? Color.FromRgb(151, 89, 8) : Color.FromRgb(112, 68, 165));
            badge.Background = new SolidColorBrush(type == 2 ? Color.FromRgb(255, 241, 216) : Color.FromRgb(242, 232, 252));
        }
        row.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
    }
}
