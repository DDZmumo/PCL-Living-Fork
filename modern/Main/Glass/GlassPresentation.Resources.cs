// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

public sealed partial class GlassPresentationSession
{
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
        var pages = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 8) };
        Bind(pages, UIElement.VisibilityProperty, source.CardPages, nameof(UIElement.Visibility)); results.Children.Add(pages);
        Bind(pages, UIElement.IsEnabledProperty, source.CardPages, nameof(UIElement.IsEnabled));
        var content = new Grid();
        var scroller = new ScrollViewer { Name = "ResourceResultsScroll", Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(scroller, true);
        var resultsHost = new ScrollEdgeFade(scroller);
        // Rows are not inside a card, so the list gets one frosted plate of its own; text stays readable over any wallpaper.
        // The plate has no outline: it is a separate layer behind the rows whose edges are feathered by a blur,
        // so it dissolves into the wallpaper instead of ending at a hard border.
        var plateBackground = new GlassSurface { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(0), Margin = new Thickness(4), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        plateBackground.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        var resultsPlate = new Grid();
        resultsPlate.Children.Add(plateBackground);
        resultsHost.Margin = new Thickness(6, 4, 2, 4);
        resultsPlate.Children.Add(resultsHost);
        Bind(resultsPlate, UIElement.VisibilityProperty, source.PanContent, nameof(UIElement.Visibility)); content.Children.Add(resultsPlate);
        // Results replace the loader on every search or page change: fade the plate in and cascade the first rows,
        // instead of the whole list appearing in one frame.
        bool cascadeArmed = false; DateTime cascadeDeadline = default;
        resultsPlate.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true || !Live(resultsPlate) || !MotionPreferences.AllowsAnimation) return;
            plateBackground.Opacity = 0;
            var plateFade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
            plateBackground.BeginAnimation(UIElement.OpacityProperty, plateFade);
            // Rows may be projected by a later refresh; play on the first layout pass that has them (or give up after a second).
            cascadeDeadline = DateTime.UtcNow.AddSeconds(1);
            if (!cascadeArmed) { cascadeArmed = true; resultsPlate.LayoutUpdated += PlayResultsCascade; }
        };
        void PlayResultsCascade(object? sender, EventArgs args)
        {
            bool expired = DateTime.UtcNow > cascadeDeadline || !Live(resultsPlate) || !resultsPlate.IsVisible;
            Panel? rows = expired ? null : StaggeredEntrance.FindItemsPanel(items);
            if (!expired && rows is null) return;
            cascadeArmed = false; resultsPlate.LayoutUpdated -= PlayResultsCascade;
            if (rows is not null) StaggeredEntrance.Play(rows.Children.OfType<UIElement>().Where(row => row.Visibility == Visibility.Visible).Take(16));
        }
        var loading = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 38, 0, 0) };
        Bind(loading, UIElement.VisibilityProperty, source.PanLoad, nameof(UIElement.Visibility)); content.Children.Add(loading);
        Grid.SetRow(content, 2); layout.Children.Add(content);

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
            source.BtnSearchRun.PerformClick(); scroller.ScrollToHome(); ToggleFilters(false); Refresh();
        }
        filter.Click += (_, _) => ToggleFilters(!open);
        apply.Click += (_, _) => RunSearch();
        reset.Click += (_, _) => { if (!Live(frame)) return; source.BtnSearchReset.PerformClick(); scroller.ScrollToHome(); Refresh(); MeasureFilters(); };
        search.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; RunSearch(); } };
        frame.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && open) { ToggleFilters(false); filter.Focus(); args.Handled = true; } };
        frame.SizeChanged += (_, _) => { if (Live(frame) && open) MeasureFilters(); };
        search.TextChanged += (_, _) => placeholder.Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
        var node = new Node(frame);
        node.Cleanup.Add(reveal.Dispose);
        int displayedPage = source.Page;
        node.Pull = () => {
            if (displayedPage != source.Page) { displayedPage = source.Page; scroller.ScrollToHome(); }
            search.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            placeholder.Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var editor in editors) {
                UIElement next = Project(editor.Source);
                if (next is FrameworkElement element) { element.Margin = new Thickness(); element.Width = double.NaN; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
                if (!ReferenceEquals(editor.Host.Content, next)) editor.Host.Content = next;
            }
            UIElement packView = Project(source.BtnSearchInstallModPack);
            if (!ReferenceEquals(installPack.Content, packView)) installPack.Content = packView;
            UIElement errorView = Project(source.HintError);
            if (!ReferenceEquals(error.Content, errorView)) error.Content = errorView;
            UIElement itemView = Project(source.PanProjects);
            if (itemView is FrameworkElement itemElement) itemElement.Margin = new Thickness();
            if (!ReferenceEquals(items.Content, itemView)) items.Content = itemView;
            UIElement loadView = Project(source.Load);
            if (!ReferenceEquals(loading.Content, loadView)) loading.Content = loadView;
            Children(pages, new[] { Project(source.BtnPageFirst), Project(source.BtnPageLeft), Project(source.LabPage), Project(source.BtnPageRight) }.Where(item => item != null).ToArray());
            if (open) MeasureFilters();
        };
        return node;
    }
}
