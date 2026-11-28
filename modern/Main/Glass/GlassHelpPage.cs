// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Entry = PCL.ModMain.HelpEntry;
using Path = System.Windows.Shapes.Path;

namespace PCL;

internal sealed class GlassHelpPage : ContentControl, IDisposable
{
    private readonly GlassPageResources _resources;
    private readonly TextBox _query;
    private readonly StackPanel _categories = new(), _results = new();
    private readonly BlockCurveLoader _loading = new() { Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private readonly Button _retry;
    private readonly GlassSurface _loadCard;
    private readonly ModLoader.LoaderTask<int, List<Entry>> _loader = ModMain.HelpLoader;
    private List<Entry>? _displayed;
    private bool _active, _disposed, _loadData;
    private DispatcherOperation? _change;
    internal ScrollViewer Scroll { get; }

    internal GlassHelpPage()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        var frame = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition());
        var search = new Grid { Height = 32 };
        var frost = GlassPageResources.Plate(search, new Thickness(0, 0, 0, 15));
        frost.BorderThickness = new Thickness();
        frost.Padding = new Thickness();
        frost.CornerRadius = new CornerRadius(8);
        frame.Children.Add(frost);
        _query = new TextBox { Style = _resources.Style("GlassPresentationSearchBox") };
        AutomationProperties.SetName(_query, "搜索帮助");
        var prompt = _resources.Note("搜索帮助", 14);
        prompt.Margin = new Thickness(35, 0, 32, 0);
        var magnifier = new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        magnifier.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "GlassPresentationSecondary");
        prompt.VerticalAlignment = VerticalAlignment.Center;
        prompt.IsHitTestVisible = false;
        search.Children.Add(_query);
        search.Children.Add(magnifier);
        search.Children.Add(prompt);
        GlassPageResources.BindTextPlaceholder(_query, prompt);
        _query.TextChanged += (_, _) => ScheduleSearch();

        _status = _resources.Note("", 13);
        _status.Margin = new Thickness(0, 9, 0, 10);
        _status.TextAlignment = TextAlignment.Center;
        _status.MaxWidth = 476;
        _retry = new Button { Content = "重试", Style = _resources.Style("GlassPresentationButton"), HorizontalAlignment = HorizontalAlignment.Center };
        _retry.Click += (_, _) => Reload();
        _loading.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        _loadCard = GlassPageResources.Plate(new StackPanel { Children = { _loading, _status, _retry } }, new Thickness(0, 12, 0, 0));
        _loadCard.HorizontalAlignment = HorizontalAlignment.Center;
        _loadCard.Visibility = Visibility.Collapsed;
        var stack = new StackPanel { Children = { _categories, _results, _loadCard } };
        Scroll = new ScrollViewer { Content = stack, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        var scroll = new ScrollEdgeFade(Scroll);
        Grid.SetRow(scroll, 1);
        frame.Children.Add(scroll);
        Content = frame;
        _loader.OnStateChangedUi += LoaderChanged;
    }

    internal void Open(bool loadData)
    {
        _active = true;
        _loadData = loadData;
        RefreshPalette();
        Scroll.ScrollToTop();
        if (loadData && _loader.State is ModBase.LoadState.Waiting or ModBase.LoadState.Canceled) _loader.Start(ModBase.GetUuid());
        ReadLoader();
    }

    internal void Reload()
    {
        if (!_loadData) return;
        Settings.Set("SystemHelpVersion", 0);
        _query.Clear();
        _loader.Start(ModBase.GetUuid(), IsForceRestart: true);
        ReadLoader();
    }

    internal void Leave()
    {
        _active = false;
        _loading.IsActive = false;
        _change?.Abort();
        _change = null;
    }

    internal void RefreshPalette() => _resources.ApplyPalette();

    private void LoaderChanged(ModLoader.LoaderBase _, ModBase.LoadState state, ModBase.LoadState former)
    {
        if (_loader.State == state) ReadLoader();
    }

    private void ReadLoader()
    {
        if (!_active || !_loadData || _disposed) return;
        bool loading = _loader.State == ModBase.LoadState.Loading, failed = _loader.State == ModBase.LoadState.Failed;
        _loading.IsActive = loading;
        _loading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = loading ? "正在加载帮助列表" : _loader.Error?.Message ?? "加载帮助列表失败";
        _loadCard.Visibility = loading || failed ? Visibility.Visible : Visibility.Collapsed;
        if (_loader.State == ModBase.LoadState.Finished && _loader.Output is { } entries && !ReferenceEquals(entries, _displayed))
        {
            _displayed = entries;
            BuildCategories(entries);
        }
        ScheduleSearch();
    }

    private void BuildCategories(List<Entry> entries)
    {
        _categories.Children.Clear();
        Entry[] available = entries.Where(LauncherHelpCatalog.IsVisible).ToArray();
        foreach (string category in available.SelectMany(entry => entry.Types).Distinct().OrderBy(label => label != "指南"))
        {
            var label = _resources.Text(category);
            label.FontWeight = FontWeights.SemiBold;
            var expander = new Expander { Header = label, Style = _resources.Style("GlassPresentationExpander") };
            void Expand(object? sender, RoutedEventArgs e)
            {
                if (!ReferenceEquals(e.OriginalSource, expander)) return;
                expander.Expanded -= Expand;
                var rows = new StackPanel();
                foreach (var entry in available.Where(item => item.Types.Contains(category))) rows.Children.Add(Row(entry));
                expander.Content = rows;
            }
            expander.Expanded += Expand;
            expander.IsExpanded = category == "指南";
            _categories.Children.Add(GlassPageResources.Card(expander, GlassPageLayout.CardRole.Expander));
        }
    }

    private void ScheduleSearch()
    {
        if (!_active || _disposed || _change?.Status == DispatcherOperationStatus.Pending) return;
        _change = Dispatcher.InvokeAsync(() =>
        {
            _change = null;
            string query = _query.Text.Trim();
            if (!_active) return;
            _categories.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _results.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _results.Children.Clear();
            if (query.Length == 0 || _displayed is null) return;
            var matches = LauncherHelpCatalog.Find(_displayed, query);
            var stack = new StackPanel();
            if (matches.Count == 0) stack.Children.Add(_resources.Note("无搜索结果", 14));
            foreach (var item in matches)
            {
                string? note = ModBase.ModeDebug ? (item.AbsoluteRight ? "完全匹配，" : "") + $"相似度：{item.Similarity:0.###}，" + item.Item.Desc : item.Item.Desc;
                stack.Children.Add(Row(item.Item, note));
            }
            _results.Children.Add(GlassPageResources.Card(stack, GlassPageLayout.CardRole.Content));
            StaggeredEntrance.Play(stack.Children.OfType<UIElement>());
        }, DispatcherPriority.Background);
    }

    private Button Row(Entry entry, string? note = null)
    {
        var body = new DockPanel();
        string fallback = ModBase.PathImage + (entry.IsEvent ? entry.EventType == CustomEvent.EventType.弹出窗口 ?
            "Blocks/GrassPath.png" : "Blocks/CommandBlock.png" : "Blocks/Grass.png");
        FrameworkElement icon;
        try
        {
            if (entry.Logo is { Length: > 0 } geometry && (geometry.StartsWith("M", StringComparison.Ordinal) || geometry.StartsWith("F", StringComparison.Ordinal)))
            {
                var vector = new Path { Data = Geometry.Parse(geometry), Stretch = Stretch.Uniform };
                vector.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
                icon = vector;
            }
            else icon = new LauncherMarkupImage { Source = entry.Logo ?? fallback, FallbackSource = fallback };
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        { icon = new Image { Source = (ImageSource)new MyBitmap(fallback) }; }
        icon.Width = icon.Height = 28;
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        var title = _resources.Text(entry.Title);
        title.FontWeight = FontWeights.SemiBold;
        var description = _resources.Note(note ?? entry.Desc ?? "", 12);
        description.TextWrapping = TextWrapping.NoWrap;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, description } });
        var row = new Button { Style = _resources.Style("GlassPresentationBandRow"), Content = body };
        row.Click += (_, _) => LauncherHelpCatalog.Open(entry);
        AutomationProperties.SetName(row, entry.Title ?? "");
        return row;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Leave();
        _loader.OnStateChangedUi -= LoaderChanged;
    }
}

internal sealed class GlassHelpDetailPage : ContentControl
{
    private readonly GlassPageResources _resources;
    private readonly GlassHomepageViewport _content = new() { Margin = new Thickness(0, 12, 0, 0) };
    private Entry _entry;
    internal ScrollViewer Scroll { get; }

    internal GlassHelpDetailPage(Entry entry)
    {
        _entry = entry;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        Scroll = new ScrollViewer { Content = _content, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);
        _resources.ApplyPalette();
    }

    internal void Open()
    {
        _resources.ApplyPalette();
        if (_content.Children.Count == 0) Load(_entry);
    }
    internal void RefreshPalette() => _resources.ApplyPalette();
    internal void Reload()
    {
        try { Load(new Entry(_entry.RawPath)); }
        catch (Exception error) { Logger.Error(error, "重新读取帮助项目失败", LogBehavior.Toast); }
    }

    private void Load(Entry entry)
    {
        try
        {
            var view = LauncherMarkup.ParseFragment(entry.XamlContent ?? throw new InvalidOperationException("帮助内容为空。"));
            ApplyHelpCards(view);
            _content.Children.Clear();
            _content.Children.Add(view);
            _entry = entry;
        }
        catch (Exception error)
        {
            Logger.Error(error, "加载帮助内容失败", LogBehavior.Toast);
            if (_content.Children.Count == 0) _content.Children.Add(_resources.Note("加载帮助内容失败。", 14));
        }
    }

    private static void ApplyHelpCards(DependencyObject element)
    {
        if (element is LauncherMarkupCard card) card.ApplyHelpCardLayout();
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) ApplyHelpCards(child);
    }
}
