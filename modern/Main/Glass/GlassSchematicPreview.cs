// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MeloongCore;
using MeloongCore.Extensions;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using SchematicKit;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// A schematic opened from the resource list: the 3D model fills a large stage with its controls floating on it,
/// and a side panel leads with the figures that matter (size, block count, kinds) above the per-block statistics.
/// </summary>
internal sealed class GlassSchematicPreview : Grid, IDisposable
{
    private const double StageRadius = 16;
    private const double StageRim = .7;
    private const double LayerBarRadius = 12;
    private const double StatisticsBreakpoint = 620;
    private const double SideStatisticsWidth = 260;
    private const double OverlayInset = 14;

    private readonly GlassPageResources _resources;
    private readonly Grid _viewport = new() { ClipToBounds = true };
    private readonly Grid _scene = new();
    private readonly Grid _stage = new();
    private readonly Grid _overlays = new();
    private readonly DockPanel _toolbar = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(OverlayInset) };
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _commandBar;
    private readonly Grid _body = new();
    private readonly BlockCurveLoader _loading = new() { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private readonly Button _details;
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) };
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<(Grid Host, Border Tint, GlassSceneFrost Frost)> _sceneOverlays = new();
    private SpringMotion? _statisticsMotion;
    private SchematicSession? _session;
    private bool _started, _disposed;

    internal GlassSchematicPreview(ModMinecraft.McInstance instance, InstanceResourceEntry entry, Action close)
    {
        Margin = new Thickness(0, 10, 0, 14);
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        // Header: a round back button, the file name, its format and size as chips, and details on the right.
        var header = new DockPanel();
        var back = RoundButton("M15,4 L7,12 L15,20", "返回投影列表", close);
        back.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        _details = Command("文件详情");
        _details.Click += (_, _) => {
            if (_session is { } session) ModMain.MyMsgBoxContent("", "投影详情", () => new GlassSchematicDetails(session, entry));
        };
        _details.IsEnabled = false;
        DockPanel.SetDock(_details, Dock.Right);
        header.Children.Add(_details);
        var title = _resources.Text(System.IO.Path.GetFileNameWithoutExtension(entry.Name));
        title.FontSize = 20;
        title.FontWeight = FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.ToolTip = entry.Name;
        var heading = new DockPanel { Children = { title } };
        DockPanel.SetDock(_chips, Dock.Right);
        heading.Children.Insert(0, _chips);
        string extension = System.IO.Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
        if (extension.Length > 0) _chips.Children.Add(GlassHeroCard.Chip(extension, accent: true));
        header.Children.Add(heading);
        // The header sits on its own frosted strip so the name and chips stay readable over the wallpaper.
        var strip = new GlassSurface { Child = header, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(.7),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, Margin = new Thickness(0, 0, 0, 12) };
        strip.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        strip.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        Children.Add(strip);

        // Stage: the model on a soft vignette, with the hint and layer controls floating over it.
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        var vignette = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(StageRadius - StageRim) };
        vignette.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        _scene.Children.Add(vignette);
        _scene.Children.Add(_viewport);
        // Clip the scene inside the surface's rim so zooming never covers its rounded corners.
        var clip = new RectangleGeometry { RadiusX = StageRadius - StageRim, RadiusY = StageRadius - StageRim };
        _stage.Clip = clip;
        _stage.SizeChanged += (_, args) => clip.Rect = new Rect(args.NewSize);
        _stage.Children.Add(_scene);
        _commandBar = SceneOverlay(_commands, new Thickness(2));
        _commandBar.HorizontalAlignment = HorizontalAlignment.Right;
        _commandBar.VerticalAlignment = VerticalAlignment.Top;
        _commandBar.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_commandBar, Dock.Right);
        _toolbar.Children.Add(_commandBar);
        _overlays.Children.Add(_toolbar);
        _stage.Children.Add(_overlays);
        var stageCard = new GlassSurface { Child = _stage, CornerRadius = new CornerRadius(StageRadius), BorderThickness = new Thickness(StageRim), BlurRadius = 24,
            TintOpacity = 1, HighlightOpacity = 0, Padding = new Thickness(0), MinHeight = 380 };
        stageCard.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        stageCard.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        _body.Children.Add(stageCard);
        SetRow(_body, 1);
        Children.Add(_body);

        _status = _resources.Note("正在读取投影……", 14);
        _status.TextAlignment = TextAlignment.Center;
        _status.Margin = new Thickness(12, 10, 12, 12);
        var progress = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _loading, _status } };
        _viewport.Children.Add(progress);
        _loading.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");

        Loaded += async (_, _) => {
            LayoutUpdated -= OnOverlayLayout;
            LayoutUpdated += OnOverlayLayout;
            if (_started || _disposed) return;
            _started = true;
            await LoadAsync(instance, entry);
        };
        Unloaded += (_, _) => {
            LayoutUpdated -= OnOverlayLayout;
            foreach (var overlay in _sceneOverlays) {
                overlay.Frost.ClearBackdrop();
                overlay.Tint.SetResourceReference(Border.BackgroundProperty, "GlassPresentationPopup");
            }
        };
    }

    internal void PaletteChanged()
    {
        _resources.ApplyPalette();
        RefreshSceneOverlays();
    }

    private Button Command(string text) => new() {
        Content = text, Style = _resources.Style("GlassPresentationButton"), MinWidth = 76
    };

    private Button RoundButton(string glyph, string name, Action click)
    {
        var path = new Path { Data = Geometry.Parse(glyph), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(Shape.StrokeProperty, "GlassPresentationText");
        var button = new Button { Content = path, Style = _resources.Style("GlassPresentationButton"), Width = 36, Height = 36, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), ToolTip = name };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private async System.Threading.Tasks.Task LoadAsync(ModMinecraft.McInstance instance, InstanceResourceEntry entry)
    {
        var token = _cancel.Token;
        SchematicSession? staged = null;
        _loading.IsActive = true;
        try {
            staged = await LauncherSchematicApplication.PreviewAsync(instance, entry, token);
            token.ThrowIfCancellationRequested();
            if (staged.Metadata.EnclosingSize is { } size) _chips.Children.Add(GlassHeroCard.Chip(size));
            if (staged.Textures is { } textures) {
                _status.Text = "正在生成 3D 预览……";
                var viewer = new SchematicViewer3D(textures, MotionPreferences.AllowsAnimation);
                await viewer.InitializeAsync(staged.Data, token);
                token.ThrowIfCancellationRequested();
                _viewport.Children.Clear();
                if (staged.BlockCounts.All(count => count.IsMissing)) {
                    // Nothing left to draw: say why instead of showing an empty stage.
                    _status.Text = staged.BlockCounts.Any(count => count.IsModAbsent)
                        ? "这个投影的方块都来自当前实例没有安装的模组，无法显示 3D 预览。\n请把投影放到装有对应模组的实例中。"
                        : "这个投影的方块都没有可显示的模型或贴图，无法显示 3D 预览。";
                    if (_status.Parent is Panel loading) loading.Children.Remove(_status);
                    var stage = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _status } };
                    _viewport.Children.Add(stage);
                    AddBlockCounts(staged);
                    _session = staged;
                    staged = null;
                    _details.IsEnabled = true;
                    return;
                }
                _viewport.Children.Add(viewer);
                AddViewControls(viewer);
                AddLayerControls(viewer);
                AddBlockCounts(staged);
                RevealOverlays();
            } else {
                _status.Text = staged.Data.Error ?? (staged.Data.IsEmpty ? "没有可预览的方块。" : "此投影方块较多，可查看文件详情。");
            }
            _session = staged;
            staged = null;
            _details.IsEnabled = true;
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) {
            if (!token.IsCancellationRequested) {
                _status.Text = error.GetDisplay(false);
                Logger.Warn(error, "读取投影预览失败");
            }
        } finally {
            staged?.Dispose();
            _loading.IsActive = false;
            _loading.Visibility = Visibility.Collapsed;
            _cancel.Dispose();
        }
    }

    private void AddViewControls(SchematicViewer3D viewer)
    {
        var hint = _resources.Note("左键旋转 · 右键平移 · 滚轮缩放", 12);
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        hint.ToolTip = "左键拖动旋转，右键拖动平移，滚轮缩放\nW/S 前后移动，A/D 横移，Q/E 升降，Shift 加速，R 复位";
        var hintPlate = SceneOverlay(hint, new Thickness(12, 7, 12, 7));
        hintPlate.HorizontalAlignment = HorizontalAlignment.Left;
        hintPlate.VerticalAlignment = VerticalAlignment.Center;
        hintPlate.Margin = new Thickness(0, 0, 8, 0);
        _toolbar.Children.Add(hintPlate);
        AddStageCommand(RoundButton("M20,4 V10 H14 M19.5,10 A7,7 0 1 0 20,15", "重置视角（R）", () => {
            viewer.ResetView();
            viewer.Focus();
        }));
    }

    private void AddStageCommand(Button button)
    {
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        _commands.Children.Add(button);
        UpdateCommandBar();
    }

    private void UpdateCommandBar() => _commandBar.Visibility = _commands.Children.OfType<UIElement>()
        .Any(command => command.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    // Keep the controls' entrance in step with the model while the backdrop samples their final position.
    private void RevealOverlays()
    {
        if (!MotionPreferences.AllowsAnimation) return;
        _overlays.Opacity = 0;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = TimeSpan.FromMilliseconds(420),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        fade.Completed += (_, _) => _overlays.Opacity = 1;
        _overlays.BeginAnimation(OpacityProperty, fade);
    }

    // Layer slicing floats at the bottom of the stage: a switch, the top layer slider and the current Y.
    private void AddLayerControls(SchematicViewer3D viewer)
    {
        var panel = new DockPanel();
        var level = _resources.Text("全部层");
        level.FontSize = 13;
        level.FontWeight = FontWeights.SemiBold;
        level.MinWidth = 58;
        level.TextAlignment = TextAlignment.Right;
        level.Margin = new Thickness(12, 0, 0, 0);
        level.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(level, Dock.Right);
        panel.Children.Add(level);
        var slice = new CheckBox { Style = _resources.Style("GlassPresentationSwitch"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slice, "分层查看");
        var caption = _resources.Text("分层");
        caption.FontSize = 13;
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.Margin = new Thickness(8, 0, 14, 0);
        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Children = { slice, caption } };
        DockPanel.SetDock(toggle, Dock.Left);
        panel.Children.Add(toggle);
        var layers = new Slider { Style = _resources.Style("GlassPresentationSlider"), Minimum = viewer.MinLayer,
            Maximum = viewer.MaxLayer, Value = viewer.MaxLayer, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(layers, "投影最高可见层");
        panel.Children.Add(layers);
        slice.IsEnabled = viewer.MinLayer < viewer.MaxLayer;
        slice.Checked += (_, _) => ApplyLayer();
        slice.Unchecked += (_, _) => ApplyLayer();
        layers.ValueChanged += (_, _) => ApplyLayer();
        var bar = SceneOverlay(panel, new Thickness(12, 7, 12, 7));
        bar.MaxWidth = 404;
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        bar.Margin = new Thickness(OverlayInset);
        _overlays.Children.Add(bar);

        void ApplyLayer() {
            layers.IsEnabled = slice.IsChecked == true;
            viewer.VisibleTopLayer = layers.IsEnabled ? (int)layers.Value : null;
            level.Text = layers.IsEnabled ? $"Y = {(int)layers.Value}" : "全部层";
        }
    }

    private Grid SceneOverlay(UIElement content, Thickness padding)
    {
        var surface = new Border { Child = content, CornerRadius = new CornerRadius(LayerBarRadius), Padding = padding,
            BorderThickness = new Thickness(StageRim) };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationPopup");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationDockEdge");
        var frost = new GlassSceneFrost();
        var bar = new Grid { Children = { frost, surface } };
        _sceneOverlays.Add((bar, surface, frost));
        return bar;
    }

    private void OnOverlayLayout(object? sender, EventArgs args) => RefreshSceneOverlays();

    private void RefreshSceneOverlays()
    {
        if (_disposed) return;
        PresentationSource? source = PresentationSource.FromVisual(_scene);
        foreach (var (bar, surface, frost) in _sceneOverlays) {
            bool wasFrosted = frost.Visibility == Visibility.Visible;
            if (source is not null && bar.IsVisible && GlassMaterial.AllowsTransparency(bar) &&
                ReferenceEquals(PresentationSource.FromVisual(bar), source))
                frost.UpdateBackdrop(bar, _scene, _scene, LayerBarRadius);
            else
                frost.ClearBackdrop();
            bool frosted = frost.Visibility == Visibility.Visible;
            if (wasFrosted != frosted)
                surface.SetResourceReference(Border.BackgroundProperty, frosted ? "GlassPresentationDockFill" : "GlassPresentationPopup");
        }
    }

    private void AddBlockCounts(SchematicSession session)
    {
        var side = StatisticsCard(BuildStatistics(session, null), new Thickness(12, 0, 0, 0), "GlassPresentationFill");
        var column = new ColumnDefinition();
        _body.ColumnDefinitions.Add(column);
        SetColumn(side, 1);
        _body.Children.Add(side);

        GlassSurface? flyout = null;
        bool flyoutOpen = false;
        var toggle = Command("方块统计");
        toggle.Click += (_, _) => ToggleStatistics();
        toggle.Margin = new Thickness(3, 0, 0, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        AddStageCommand(toggle);
        _body.SizeChanged += (_, _) => UpdateStatisticsLayout();
        _stage.SizeChanged += (_, _) => FitFlyout();
        _stage.PreviewMouseDown += (_, _) => {
            if (flyoutOpen && flyout is { IsMouseOver: false } && !toggle.IsMouseOver)
                SetFlyoutOpen(false);
        };
        PreviewKeyDown += (_, args) => {
            if (args.Key != Key.Escape || !flyoutOpen) return;
            CloseFlyout();
            args.Handled = true;
        };
        UpdateStatisticsLayout();

        void UpdateStatisticsLayout()
        {
            bool compact = _body.ActualWidth is > 0 and < StatisticsBreakpoint;
            column.Width = new GridLength(compact ? 0 : SideStatisticsWidth);
            side.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            toggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            UpdateCommandBar();
            if (!compact) SetFlyoutOpen(false, immediate: true);
        }

        void FitFlyout()
        {
            if (flyout is not null)
                flyout.Width = Math.Clamp(_stage.ActualWidth - 2 * OverlayInset, 0, 540);
        }

        void CloseFlyout()
        {
            SetFlyoutOpen(false);
            toggle.Focus();
        }

        void SetFlyoutOpen(bool open, bool immediate = false)
        {
            if (flyout is null || _statisticsMotion is null) return;
            flyoutOpen = open;
            flyout.IsHitTestVisible = open;
            if (open) flyout.Visibility = Visibility.Visible;
            _statisticsMotion.Retarget(open ? 1 : 0, immediate);
        }

        void ToggleStatistics()
        {
            if (flyoutOpen)
            {
                CloseFlyout();
                return;
            }
            if (flyout is null)
            {
                flyout = StatisticsCard(BuildStatistics(session, CloseFlyout), new Thickness(OverlayInset, 60, OverlayInset, OverlayInset),
                    "GlassPresentationPopup");
                flyout.HorizontalAlignment = HorizontalAlignment.Right;
                var scale = new ScaleTransform();
                var offset = new TranslateTransform();
                flyout.RenderTransformOrigin = new Point(1, 0);
                flyout.RenderTransform = new TransformGroup { Children = { scale, offset } };
                _overlays.Children.Add(flyout);
                _statisticsMotion = new SpringMotion(this, 0, value => {
                    double progress = Math.Clamp(value, 0, 1);
                    flyout.Opacity = progress;
                    scale.ScaleX = scale.ScaleY = MotionTokens.RevealScale + (1 - MotionTokens.RevealScale) * progress;
                    offset.Y = -MotionTokens.PopoverTravel * (1 - progress);
                    if (!flyoutOpen && progress <= MotionTokens.VisibilityThreshold) flyout.Visibility = Visibility.Collapsed;
                }, MotionTokens.Reveal);
            }
            FitFlyout();
            SetFlyoutOpen(true);
        }
    }

    private DockPanel BuildStatistics(SchematicSession session, Action? close)
    {
        bool compact = close is not null;
        var rows = new ListBox { Background = Brushes.Transparent,
            ItemsSource = compact ? session.BlockCounts.Chunk(2).Select(pair => new BlockCountPair(pair[0], pair.Length > 1 ? pair[1] : null)).ToArray()
                : session.BlockCounts,
            Style = _resources.Style("GlassPresentationPickerList"),
            ItemContainerStyle = _resources.Style("GlassInstallVersionListItem"),
            ItemTemplate = (DataTemplate)FindResource(compact ? "GlassSchematicBlockPair" : "GlassSchematicBlockRow") };
        AutomationProperties.SetName(rows, "方块统计");
        VirtualizingPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(rows, ScrollUnit.Pixel);
        var box = new DockPanel();
        long total = session.Metadata.TotalBlocks ?? session.BlockCounts.Sum(item => (long)item.Count);
        var heading = _resources.Text("方块统计");
        heading.FontWeight = FontWeights.SemiBold;
        if (close is not null)
        {
            var header = new DockPanel { Margin = new Thickness(4, 0, 0, 8) };
            var dismiss = RoundButton("M6,6 L18,18 M18,6 L6,18", "关闭方块统计", close);
            dismiss.Width = dismiss.Height = 30;
            DockPanel.SetDock(dismiss, Dock.Right);
            header.Children.Add(dismiss);
            var summary = _resources.Note($"{total:N0} 个方块 · {session.BlockCounts.Count:N0} 种", 12);
            header.Children.Add(new StackPanel { Children = { heading, summary } });
            AddHeader(header);
        }
        else
        {
            var figures = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -8, 12) };
            figures.Children.Add(Figure(total.ToString("N0"), "方块"));
            figures.Children.Add(Figure(session.BlockCounts.Count.ToString("N0"), "种类"));
            AddHeader(figures);
            if (session.Metadata.Author is { Length: > 0 } author)
            {
                var by = _resources.Note("作者：" + author, 12);
                by.Margin = new Thickness(2, 0, 2, 10);
                by.TextTrimming = TextTrimming.CharacterEllipsis;
                by.TextWrapping = TextWrapping.NoWrap;
                AddHeader(by);
            }
            heading.Margin = new Thickness(4, 0, 4, 7);
            AddHeader(heading);
        }
        if (MissingNotice(session) is { } notice) AddHeader(notice);
        box.Children.Add(rows);
        return box;

        void AddHeader(UIElement child)
        {
            DockPanel.SetDock(child, Dock.Top);
            box.Children.Add(child);
        }
    }

    private static GlassSurface StatisticsCard(DockPanel content, Thickness margin, string fill)
    {
        var stats = new GlassSurface { Child = content, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 14, 10, 10), BorderThickness = new Thickness(.7),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, Margin = margin };
        stats.SetResourceReference(Border.BackgroundProperty, fill);
        stats.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return stats;
    }

    private sealed record BlockCountPair(SchematicBlockCount First, SchematicBlockCount? Second);

    // Blocks without resources (their mod is not in this instance) are left out of the model; name the mods to look for.
    private Border? MissingNotice(SchematicSession session)
    {
        var absent = session.BlockCounts.Where(count => count.IsModAbsent).ToArray();
        int shapeless = session.BlockCounts.Count(count => count.IsMissing && !count.IsModAbsent);
        if (absent.Length == 0 && shapeless == 0) return null;
        var lines = new System.Collections.Generic.List<string>();
        if (absent.Length > 0) {
            string[] mods = absent.Select(count => count.BlockId.Split(':')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string named = string.Join("、", mods.Take(5)) + (mods.Length > 5 ? $" 等 {mods.Length} 个" : "");
            lines.Add($"{absent.Length} 种方块所属的模组（{named}）当前实例没有安装，未在预览中显示。请把投影放到装有这些模组的实例中。");
        }
        if (shapeless > 0) lines.Add($"{shapeless} 种方块没有可显示的模型或贴图（如伪装类方块），未在预览中显示。");
        var text = new TextBlock {
            Text = string.Join("\n", lines),
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDangerText");
        var notice = new Border { Child = text, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(.7),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8) };
        notice.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDangerFill");
        notice.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationDangerEdge");
        return notice;
    }

    private Border Figure(string value, string caption)
    {
        var number = _resources.Text(value);
        number.FontSize = 20;
        number.FontWeight = FontWeights.SemiBold;
        number.TextWrapping = TextWrapping.NoWrap;
        var label = _resources.Note(caption, 12);
        var tile = new Border { Child = new StackPanel { Children = { number, label } }, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        return tile;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_started) _cancel.Dispose();
        else if (_session is null && _loading.IsActive) _cancel.Cancel();
        _statisticsMotion?.Dispose();
        LayoutUpdated -= OnOverlayLayout;
        foreach (var overlay in _sceneOverlays) overlay.Frost.ClearBackdrop();
        _overlays.Children.Clear();
        _viewport.Children.Clear();
        _session?.Dispose();
        _session = null;
    }
}
