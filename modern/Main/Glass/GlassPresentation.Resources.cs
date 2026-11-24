// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using PCLLF.Presentation.Controls;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private sealed record ResourceScrollPosition(ResourceSearcher.SearchRequest Input, int Page, double Offset);
    private static readonly ConditionalWeakTable<PageResource, ResourceScrollPosition> ResourceScrollPositions = new();
    private const double DownloadResourceRowHeight = 52;
    private static readonly Geometry DownloadPlaceholderShape = Geometry.Parse(
        "M4,0 H28 Q32,0 32,4 V28 Q32,32 28,32 H4 Q0,32 0,28 V4 Q0,0 4,0 Z M44,7 H162 V16 H44 Z M44,21 H124 V27 H44 Z");

    private Node ProjectDownloadResourceRows(Func<DependencyObject[]> readRows)
    {
        var stack = new StackPanel();
        var node = new Node(stack);
        Action schedule = BindResourceViewport(stack, node);
        DependencyObject[] previous = [];
        node.Pull = () =>
        {
            DependencyObject[] rows = readRows();
            Children(stack, rows.Select(row => row is IControlVirtualElement element
                ? Project(row, create: () => DownloadResourcePlaceholder(element)) : Project(row))
                .Where(row => row is not null).ToArray());
            if (rows.SequenceEqual(previous, ReferenceEqualityComparer.Instance)) return;
            previous = rows;
            schedule();
        };
        return node;
    }

    private Node DownloadResourcePlaceholder(IControlVirtualElement source)
    {
        var preview = new System.Windows.Shapes.Path { Data = DownloadPlaceholderShape, Stretch = Stretch.None, Opacity = .18,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        preview.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GlassPresentationSecondary");
        var row = new Border { Height = DownloadResourceRowHeight, Padding = new Thickness(8, 0, 0, 0),
            IsHitTestVisible = false, Child = preview };
        // The shared viewport pump admits only a few rows per slice. Refresh requests coalesce.
        return new Node(row) { PrepareResourceRow = () => {
            if (source is not FrameworkElement { Parent: Panel }) return;
            source.InitializeElement();
            QueueRefresh();
        } };
    }
    private sealed record InstanceResourceScrollKey(string Instance, string Query, object Filter, string Sort);
    private sealed record InstanceResourceScrollPosition(InstanceResourceScrollKey Key, double Offset);
    private static readonly ConditionalWeakTable<ScrollViewer, InstanceResourceScrollPosition> InstanceResourceScrollPositions = new();
    internal ScrollViewer InstanceResourceScroll { get; private set; }
    internal ScrollViewer BackToTopScroll { get; private set; }
    private Action _resetInstanceResourceScroll;

    internal void ResetInstanceResourceScroll() => _resetInstanceResourceScroll?.Invoke();

    internal void ScrollToTop()
    {
        if (BackToTopScroll is not { } scroll) return;
        if (ReferenceEquals(scroll, InstanceResourceScroll)) { ResetInstanceResourceScroll(); return; }
        SmoothScroll.Stop(scroll);
        scroll.ScrollToTop();
    }

    private void TrackBackToTop(ScrollViewer scroll, Node node)
    {
        BackToTopScroll = scroll;
        void RefreshButton() {
            if (_source.Name == "PanMainRight" && Live(scroll)) ModMain.FrmMain?.BtnExtraBack?.ShowRefresh();
        }
        ScrollChangedEventHandler changed = (_, args) => {
            if (ReferenceEquals(args.OriginalSource, scroll) && args.VerticalChange != 0) RefreshButton();
        };
        DependencyPropertyChangedEventHandler visible = (_, _) => RefreshButton();
        scroll.ScrollChanged += changed;
        scroll.IsVisibleChanged += visible;
        node.Cleanup.Add(() => {
            scroll.ScrollChanged -= changed;
            scroll.IsVisibleChanged -= visible;
            if (ReferenceEquals(BackToTopScroll, scroll)) BackToTopScroll = null;
        });
    }

    private void KeepInstanceResourceScroll(ScrollViewer source, ScrollViewer scroll, Node node)
    {
        var search = source.FindName("SearchBox") as MySearchBox;
        var filters = source.FindName("PanFilter") as Panel;
        var sort = source.FindName("BtnSort") as MyIconTextButton;
        InstanceResourceScrollKey key = null;
        double? pending = null;
        InstanceResourceScrollKey CurrentKey() => new(PageInstanceLeft.Instance?.PathVersion, search?.Text,
            filters?.Children.OfType<MyRadioButton>().FirstOrDefault(button => button.Checked)?.Tag, sort?.Text);

        void Restore()
        {
            if (!scroll.IsLoaded || pending is not double offset || (offset > 0 && scroll.ScrollableHeight <= 0)) return;
            pending = null;
            scroll.ScrollToVerticalOffset(offset);
        }

        InstanceResourceScroll = scroll;
        TrackBackToTop(scroll, node);
        _resetInstanceResourceScroll = () =>
        {
            InstanceResourceScrollPositions.Remove(source);
            SmoothScroll.Stop(scroll);
            pending = 0;
            Restore();
        };

        RoutedEventHandler loaded = (_, _) =>
        {
            if (pending is null && InstanceResourceScrollPositions.TryGetValue(source, out var saved) && saved.Key == CurrentKey())
                pending = saved.Offset;
            Restore();
        };
        ScrollChangedEventHandler changed = (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, scroll) || !Live(scroll) || !scroll.IsLoaded || !scroll.IsVisible) return;
            if (pending.HasValue) { Restore(); return; }
            if (args.VerticalChange != 0 && scroll.ScrollableHeight > 0 && key == CurrentKey())
                InstanceResourceScrollPositions.AddOrUpdate(source, new(key, args.VerticalOffset));
        };
        scroll.Loaded += loaded;
        scroll.ScrollChanged += changed;
        node.Cleanup.Add(() =>
        {
            scroll.Loaded -= loaded;
            scroll.ScrollChanged -= changed;
            if (!ReferenceEquals(InstanceResourceScroll, scroll)) return;
            InstanceResourceScroll = null;
            _resetInstanceResourceScroll = null;
        });

        var pull = node.Pull;
        node.Pull = () =>
        {
            pull();
            var next = CurrentKey();
            if (next == key) return;
            pending = key is null && InstanceResourceScrollPositions.TryGetValue(source, out var saved) && saved.Key == next
                ? saved.Offset : 0;
            key = next;
            Restore();
        };
    }

    private Node ProjectResourcePage(PageResource source)
    {
        var frame = new Grid();
        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        frame.Children.Add(layout);
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());

        var toolbar = new Grid { Height = 32, Margin = new Thickness(0, 0, 0, 18) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(toolbar);
        // The toolbar sits directly on the wallpaper (no card beneath it), so the search box and filter button each get
        // a frosted plate of their own behind them; their translucent fills then read over a blur, not raw wallpaper.
        GlassSurface ToolbarFrost(int column, Thickness margin)
        {
            var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), Margin = margin, BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
            frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
            Grid.SetColumn(frost, column); toolbar.Children.Add(frost);
            return frost;
        }
        ToolbarFrost(0, new Thickness());
        var filterFrost = ToolbarFrost(1, new Thickness(12, 0, 0, 0));
        var searchHost = new Grid(); toolbar.Children.Add(searchHost);
        var search = new TextBox { Name = "ResourceSearchInput", Style = Style("GlassPresentationSearchBox"), Height = 32, MinHeight = 32 };
        Bind(search, TextBox.TextProperty, source.TextSearchName, nameof(TextBox.Text), BindingMode.TwoWay);
        AutomationProperties.SetName(search, "搜索资源");
        searchHost.Children.Add(search);
        var magnifier = new System.Windows.Shapes.Path {
            Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        magnifier.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "GlassPresentationSecondary"); searchHost.Children.Add(magnifier);
        var placeholder = PlainText("搜索"); placeholder.Margin = new Thickness(35, 0, 12, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); searchHost.Children.Add(placeholder);
        BindTextPlaceholder(search, placeholder);

        Button ToolbarButton(string text) => new() {
            Content = text, Style = Style("GlassPresentationButton"), Height = 32, MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0), FontSize = 14, FontWeight = FontWeights.Normal
        };
        var filter = ToolbarButton("筛选设置"); filter.Name = "ResourceFilterButton";
        filter.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(filter, 1); toolbar.Children.Add(filter);
        Bind(filterFrost, UIElement.VisibilityProperty, filter, nameof(UIElement.Visibility));

        var filters = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var filterScroll = new ScrollViewer { Content = filters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        var fields = new UniformGrid { Columns = 2 };
        filters.Children.Add(fields);
        // Filters drop down over the wallpaper, so the panel is a frosted plate like the results, not a flat fill.
        var filterFrame = new GlassSurface {
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0,
            Name = "ResourceFilterPanel", CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(.7),
            Child = filterScroll, ClipToBounds = true, Height = 0, Opacity = 0, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 18), IsHitTestVisible = false
        };
        filterFrame.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        filterFrame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        Grid.SetRow(filterFrame, 1); layout.Children.Add(filterFrame);

        var editors = new List<(FrameworkElement Source, ContentControl Host)>();
        foreach (var (labelText, editorSource) in new (string, FrameworkElement)[] {
            ("版本", source.TextSearchVersion), ("来源", source.ComboSearchSource),
            ("类型", source.ComboSearchTag), ("加载器", source.ComboSearchLoader)
        }) {
            var field = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
            Bind(field, UIElement.VisibilityProperty, editorSource, nameof(UIElement.Visibility));
            var label = PlainText(labelText); label.FontSize = 13; label.Margin = new Thickness(0, 0, 0, 6); field.Children.Add(label);
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            field.Children.Add(host); fields.Children.Add(field); editors.Add((editorSource, host));
        }
        var filterActions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var installPack = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 10, 0) };
        filterActions.Children.Add(installPack);
        var reset = ToolbarButton("重置条件"); reset.Margin = new Thickness(0, 0, 10, 0);
        var apply = ToolbarButton("应用筛选"); apply.Style = Style("GlassPresentationAccentButton");
        filterActions.Children.Add(reset); filterActions.Children.Add(apply); filters.Children.Add(filterActions);

        var results = new StackPanel();
        var error = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch }; results.Children.Add(error);
        var items = new ContentControl { Name = "ResourceFlatResults", HorizontalContentAlignment = HorizontalAlignment.Stretch }; results.Children.Add(items);
        var loading = new ContentControl { Name = "ResourceSearchLoading", Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        results.Children.Add(loading);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pageSize = new ContentControl { Name = "ResourcePageSizeSlot", Width = 106, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 10, 0) };
        Bind(pageSize, UIElement.VisibilityProperty, source.ComboResultPageSize, nameof(UIElement.Visibility));
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 8), Children = { pageSize, pages } };
        var pageActions = new Border { Child = footer }; results.Children.Add(pageActions);
        Bind(pages, UIElement.VisibilityProperty, source.CardPages, nameof(UIElement.Visibility));
        Bind(pages, UIElement.IsEnabledProperty, source.CardPages, nameof(UIElement.IsEnabled));
        var content = new Grid { ClipToBounds = true };
        var scroller = new ScrollViewer { Name = "ResourceResultsScroll", Style = Style("GlassPresentationScrollViewer"), Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(scroller, true);
        var resultsHost = new ScrollEdgeFade(scroller);
        // Rows are not inside a card, so the list gets one frosted plate of its own; text stays readable over any wallpaper.
        // The plate has no outline: it is a separate layer behind the rows whose edges are feathered by a blur,
        // so it dissolves into the wallpaper instead of ending at a hard border.
        var plateBackground = new GlassSurface { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(0), Margin = new Thickness(4), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        plateBackground.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        var resultsPlate = new Grid { Name = "ResourceResultsPlate", Visibility = Visibility.Collapsed };
        resultsPlate.Children.Add(plateBackground);
        resultsHost.Margin = new Thickness(6, 4, 2, 4);
        resultsPlate.Children.Add(resultsHost);
        content.Children.Add(resultsPlate);
        Grid.SetRow(content, 2); layout.Children.Add(content);

        // Move the rows in depth while keeping the live wallpaper sample fixed to the viewport.
        var resultScale = new ScaleTransform();
        var resultOffset = new TranslateTransform();
        var resultTransform = new TransformGroup();
        resultTransform.Children.Add(resultScale);
        resultTransform.Children.Add(resultOffset);
        items.RenderTransform = resultTransform;
        items.RenderTransformOrigin = new Point(.5, .5);
        const double ExitThreshold = .04;
        bool leavingResults = false, showLoading = false;
        var resultsMotion = new SpringMotion(frame, 0, value => {
            double p = Math.Clamp(value, 0, 1);
            items.Opacity = p;
            resultScale.ScaleX = resultScale.ScaleY = MotionTokens.RevealScale + (1 - MotionTokens.RevealScale) * p;
            resultOffset.Y = MotionTokens.PageTravel * (1 - p);
            if (leavingResults && p <= ExitThreshold) QueueRefresh();
        }, MotionTokens.Reveal);
        var loadingMotion = new SpringMotion(frame, 0, value => {
            loading.Opacity = Math.Clamp(value, 0, 1);
            if (!showLoading && loading.Visibility == Visibility.Visible && value <= ExitThreshold) QueueRefresh();
        }, MotionTokens.Reveal);
        DependencyObject[] displayedRows = [];
        Func<Node> createRows = () => ProjectDownloadResourceRows(() => displayedRows);
        StackPanel displayedPanel = null;
        ResourceSearcher.SearchRequest displayedInput = null;
        int displayedRevision = -1;
        int displayedPage = source.Page;
        bool scrollResultsHome = false;
        double? restoreOffset = null;
        scroller.ScrollChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, scroller) || args.VerticalChange == 0 ||
                !scroller.IsLoaded || !scroller.IsVisible || leavingResults || restoreOffset.HasValue ||
                displayedPanel is null || displayedPage != source.Page || !ReferenceEquals(displayedInput, source.Loader.Input)) return;
            ResourceScrollPositions.AddOrUpdate(source, new(displayedInput, displayedPage, args.VerticalOffset));
        };

        void UpdateSearchStage()
        {
            StackPanel preview = source.SearchPreview;
            StackPanel next = preview ?? (source.PanContent.Visibility == Visibility.Visible ? source.PanProjects : null);
            int revision = preview is null ? source.ResultsRevision : -1;
            bool merge = displayedPanel is not null && !ReferenceEquals(displayedPanel, source.PanProjects) &&
                next is not null && ReferenceEquals(displayedInput, source.Loader.Input) && displayedPage == source.Page;
            bool changed = next is not null && (!ReferenceEquals(displayedPanel, next) || displayedRevision != revision);
            bool replace = changed && !merge;
            bool animate = frame.IsLoaded && frame.IsVisible && MotionPreferences.AllowsAnimation;

            // Keep the old row objects attached through the exit, even when a fast search already completed.
            if (animate && (leavingResults || replace || next is null) && resultsMotion.Value > ExitThreshold)
            {
                leavingResults = true;
                items.IsHitTestVisible = false;
                if (resultsMotion.Target != 0) resultsMotion.Retarget(0);
            }
            else
            {
                bool firstDisplay = displayedPanel is null;
                bool entering = leavingResults || replace || items.Visibility != Visibility.Visible;
                leavingResults = false;
                items.Visibility = next is not null ? Visibility.Visible : Visibility.Collapsed;
                items.IsHitTestVisible = next is not null;
                displayedPanel = next;
                displayedInput = source.Loader.Input;
                if (next is not null)
                {
                    // Viewport realization can replace an individual source row without changing the result batch.
                    displayedRows = next.Children.OfType<DependencyObject>().ToArray();
                    displayedRevision = revision;
                    if (replace || displayedPage != source.Page || scrollResultsHome)
                    {
                        if (firstDisplay && !scrollResultsHome &&
                            ResourceScrollPositions.TryGetValue(source, out var previous) &&
                            ReferenceEquals(previous.Input, displayedInput) && previous.Page == source.Page)
                            restoreOffset = previous.Offset;
                        else
                        {
                            ResourceScrollPositions.Remove(source);
                            scroller.ScrollToHome();
                        }
                    }
                    displayedPage = source.Page;
                    scrollResultsHome = false;
                    if (entering)
                    {
                        if (animate) resultsMotion.Snap(0);
                        resultsMotion.Retarget(1, immediate: !animate);
                    }
                }
                else
                {
                    displayedRows = [];
                    if (resultsMotion.Value != 0) resultsMotion.Snap(0);
                }
            }

            bool showingPreview = displayedPanel is not null && !ReferenceEquals(displayedPanel, source.PanProjects);
            bool loadingPage = source.IsOnlineSearch && source.Loader.State == ModBase.LoadState.Loading;
            pageActions.Visibility = source.IsOnlineSearch || displayedPanel is not null ? Visibility.Visible : Visibility.Collapsed;
            error.Visibility = showingPreview || displayedPanel is null ? Visibility.Collapsed : Visibility.Visible;
            showLoading = loadingPage && !source.IsPreviewPageFull ||
                next is null && source.PanLoad.Visibility == Visibility.Visible;
            if (showLoading) loading.Visibility = Visibility.Visible;
            loading.IsHitTestVisible = showLoading && !loadingPage;
            double loadingTarget = showLoading ? 1 : 0;
            if (loadingMotion.Target != loadingTarget) loadingMotion.Retarget(loadingTarget, immediate: !animate);
            if (!showLoading && loadingMotion.Value <= ExitThreshold) loading.Visibility = Visibility.Collapsed;
            resultsPlate.Visibility = source.IsOnlineSearch || displayedPanel is not null || loading.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        bool open = false;
        double filterHeight = 200;
        double progress = 0;
        var reveal = new SpringMotion(frame, 0, value => {
            double p = Math.Clamp(value, 0, 1);
            progress = p;
            filterFrame.Visibility = open || p > .001 ? Visibility.Visible : Visibility.Collapsed;
            filterFrame.Height = filterHeight * p; filterFrame.Opacity = p;
            // The gap below the panel shrinks with it; a fixed margin vanished at the end and made the results jump up.
            filterFrame.Margin = new Thickness(0, 0, 0, 18 * p);
            filterFrame.IsHitTestVisible = open;
            // No scrollbar while the panel grows or shrinks; it only appears at rest if the filters really overflow.
            filterScroll.VerticalScrollBarVisibility = open && p >= .999 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        }, new SpringParameters(.28, 1));
        void MeasureFilters() {
            double width = Math.Max(160, frame.ActualWidth - 2);
            filters.Measure(new Size(width, double.PositiveInfinity));
            // Reserve visible results even at the launcher's minimum height. All filters
            // remain reachable through their own scrolling viewport, including wrapped actions.
            double available = frame.ActualHeight > 0 ? Math.Max(72, frame.ActualHeight - toolbar.ActualHeight - 18 - 18 - 88) : 240;
            filterHeight = Math.Min(filters.DesiredSize.Height + 2, available);
            filterFrame.Height = filterHeight * progress;
        }
        void ToggleFilters(bool show) {
            if (!Live(frame)) return;
            open = show;
            MeasureFilters();
            AutomationProperties.SetHelpText(filter, show ? "收起筛选设置" : "展开筛选设置");
            reveal.Retarget(show ? 1 : 0);
        }
        void RunSearch() {
            if (!Live(frame)) return;
            // Binding owns committed text, including the value produced by an IME composition.
            scrollResultsHome = true;
            source.BtnSearchRun.PerformClick(); ToggleFilters(false); QueueRefresh();
        }
        filter.Click += (_, _) => ToggleFilters(!open);
        apply.Click += (_, _) => RunSearch();
        reset.Click += (_, _) => { if (!Live(frame)) return; scrollResultsHome = true; source.BtnSearchReset.PerformClick(); QueueRefresh(); MeasureFilters(); };
        search.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; RunSearch(); } };
        frame.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && open) { ToggleFilters(false); filter.Focus(); args.Handled = true; } };
        frame.SizeChanged += (_, _) => { if (Live(frame) && open) MeasureFilters(); };
        var node = new Node(frame);
        node.Cleanup.Add(reveal.Dispose);
        node.Cleanup.Add(resultsMotion.Dispose);
        node.Cleanup.Add(loadingMotion.Dispose);
        var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
        EventHandler panelChanged = (_, _) => QueueRefresh();
        RoutedEventHandler frameLoaded = (_, _) => QueueRefresh();
        visibility.AddValueChanged(source.PanContent, panelChanged);
        visibility.AddValueChanged(source.PanLoad, panelChanged);
        source.ResultsChanged += QueueRefresh;
        frame.Loaded += frameLoaded;
        node.Cleanup.Add(() => {
            visibility.RemoveValueChanged(source.PanContent, panelChanged);
            visibility.RemoveValueChanged(source.PanLoad, panelChanged);
            source.ResultsChanged -= QueueRefresh;
            frame.Loaded -= frameLoaded;
        });
        node.Pull = () => {
            UpdateSearchStage();
            foreach (var editor in editors) {
                UIElement next = Project(editor.Source);
                if (next is FrameworkElement element) { element.Margin = new Thickness(); element.Width = double.NaN; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
                if (!ReferenceEquals(editor.Host.Content, next)) editor.Host.Content = next;
            }
            UIElement packView = Project(source.BtnSearchInstallModPack);
            if (!ReferenceEquals(installPack.Content, packView)) installPack.Content = packView;
            UIElement errorView = Project(source.HintError);
            if (!ReferenceEquals(error.Content, errorView)) error.Content = errorView;
            UIElement itemView = Project(source.PanProjects, create: createRows);
            if (itemView is FrameworkElement itemElement) itemElement.Margin = new Thickness();
            if (!ReferenceEquals(items.Content, itemView)) items.Content = itemView;
            if (restoreOffset is double offset)
            {
                restoreOffset = null;
                scroller.ScrollToVerticalOffset(offset);
            }
            UIElement loadView = Project(source.Load);
            if (!ReferenceEquals(loading.Content, loadView)) loading.Content = loadView;
            if (source.ComboResultPageSize.Visibility == Visibility.Visible)
            {
                UIElement sizeControl = Project(source.ComboResultPageSize);
                if (sizeControl is FrameworkElement element) element.Margin = new Thickness();
                if (!ReferenceEquals(pageSize.Content, sizeControl)) pageSize.Content = sizeControl;
            }
            Children(pages, new[] { Project(source.BtnPageFirst), Project(source.BtnPageLeft), Project(source.LabPage), Project(source.BtnPageRight) }.Where(item => item != null).ToArray());
            if (open) MeasureFilters();
        };
        return node;
    }
}
