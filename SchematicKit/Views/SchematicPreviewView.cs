using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SchematicKit;

/// <summary>
///     原理图预览面板：3D 视图、右上角所需方块列表、底部分层控制与详情浮层。
///     可以直接嵌进页面或弹窗，也可以铺满一个容器当作全屏预览。
/// </summary>
public sealed class SchematicPreviewView : UserControl
{
    private readonly Action? _onBack;
    private readonly int _layerCount;
    private readonly string? _jarPath;
    private readonly TextBlock _labLayer;
    private readonly Border _detailPanel;
    private readonly CheckBox _checkLayer;
    private readonly Slider _sliderLayer;
    private readonly SchematicViewer3D? _viewer;
    private bool _syncingLayer;

    /// <param name="title">标题（一般传文件名）。</param>
    /// <param name="info">详情浮层里的多行文本，可传 <see cref="SchematicInfo.BuildPreviewInfo" /> 的结果。</param>
    /// <param name="data">解析结果。</param>
    /// <param name="textures">贴图集；为 null 时不渲染 3D，只显示详情与方块列表。</param>
    /// <param name="url">来源链接，非空时显示「打开网页」。</param>
    /// <param name="onBack">返回回调；为 null 时不显示返回按钮。</param>
    /// <param name="jarPath">客户端 JAR，仅用于把方块 ID 翻译成名称。</param>
    public SchematicPreviewView(string title, string info, SchematicData data, TextureAtlas? textures, string? url,
        Action? onBack, string? jarPath = null)
    {
        _onBack = onBack;
        _jarPath = jarPath;
        var strings = SchematicStrings.Current;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        #region 顶部栏

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_onBack is not null)
        {
            var backButton = Ui.Button(strings.Back, false, 32d, 96d);
            backButton.Click += (_, _) => _onBack();
            header.Children.Add(backButton);
        }

        var titleText = Ui.Text(title, 16d, true);
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);

        if (!string.IsNullOrEmpty(url))
        {
            var openButton = Ui.Button(strings.OpenWebsite, true, 32d, 104d);
            openButton.Click += (_, _) => SchematicOptions.OpenUrl?.Invoke(url);
            Grid.SetColumn(openButton, 2);
            header.Children.Add(openButton);
        }

        var detailButton = Ui.Button(strings.Detail, false, 32d, 72d);
        detailButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(detailButton, 3);
        header.Children.Add(detailButton);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        #endregion

        #region 预览区

        var body = new Border
        {
            Background = SchematicTheme.PreviewBackground,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = SchematicTheme.Border
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var bodyGrid = new Grid();
        body.Child = bodyGrid;

        if (textures is not null)
            try
            {
                _viewer = new SchematicViewer3D(data, textures);
                bodyGrid.Children.Add(_viewer);
            }
            catch (Exception ex)
            {
                SchematicOptions.Log(ex, "构建原理图 3D 预览失败");
            }

        if (_viewer is null)
        {
            var notice = Ui.WrappedText(
                textures is null
                    ? string.Format(CultureInfo.CurrentCulture, strings.TooLarge, data.Blocks.Count,
                        SchematicViewer3D.MaxRenderBlocks)
                    : strings.BuildFailed,
                13d, SchematicTheme.TextSecondary);
            notice.HorizontalAlignment = HorizontalAlignment.Center;
            notice.VerticalAlignment = VerticalAlignment.Center;
            notice.TextAlignment = TextAlignment.Center;
            notice.Margin = new Thickness(40);
            bodyGrid.Children.Add(notice);
        }

        // 右上角所需方块列表
        var listPanel = BuildBlockList(data, textures);
        bodyGrid.Children.Add(listPanel);

        // 详情浮层，默认收起
        _detailPanel = BuildDetailPanel(info);
        _detailPanel.Visibility = Visibility.Collapsed;
        bodyGrid.Children.Add(_detailPanel);
        detailButton.Click += (_, _) =>
            _detailPanel.Visibility = _detailPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

        #endregion

        #region 底部分层控制

        // 没有 3D 视图时（例如方块数超限）也要算出层数，避免显示成只有一层
        _layerCount = _viewer is not null
            ? _viewer.MaxLayer - _viewer.MinLayer + 1
            : data.Blocks.Count == 0
                ? 0
                : data.Blocks.Max(block => block.Y) - data.Blocks.Min(block => block.Y) + 1;

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _checkLayer = Ui.CheckBox(strings.LayerOnly);
        footer.Children.Add(_checkLayer);

        _sliderLayer = new Slider
        {
            Width = 180,
            Margin = new Thickness(16, 0, 0, 0),
            IsEnabled = false,
            Minimum = 0,
            Maximum = Math.Max(1, _layerCount - 1),
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = _layerCount > 1 ? Visibility.Visible : Visibility.Collapsed
        };
        // 默认停在最高层，打开分层显示时从顶层往下看最自然
        _sliderLayer.Value = _layerCount > 1 ? _layerCount - 1 : 0;
        Grid.SetColumn(_sliderLayer, 1);
        footer.Children.Add(_sliderLayer);

        _labLayer = Ui.Text(_layerCount > 1
            ? strings.LayerAll
            : string.Format(CultureInfo.CurrentCulture, strings.LayerTotal, _layerCount), 12d, false,
            SchematicTheme.TextSecondary);
        _labLayer.Margin = new Thickness(12, 0, 0, 0);
        if (_viewer is null || _layerCount <= 1)
        {
            _checkLayer.Visibility = Visibility.Collapsed;
            _labLayer.Visibility = Visibility.Collapsed;
        }

        Grid.SetColumn(_labLayer, 2);
        footer.Children.Add(_labLayer);

        if (_viewer is not null)
        {
            var hint = Ui.Text(strings.PreviewHint, 12d, false, SchematicTheme.TextMuted);
            hint.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(hint, 4);
            footer.Children.Add(hint);
        }

        var footerBar = new Border
        {
            Child = footer,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Background = SchematicTheme.Surface,
            BorderBrush = SchematicTheme.Border,
            Visibility = _viewer is null ? Visibility.Collapsed : Visibility.Visible
        };
        Grid.SetRow(footerBar, 2);
        root.Children.Add(footerBar);

        _checkLayer.Checked += (_, _) => SyncLayer();
        _checkLayer.Unchecked += (_, _) => SyncLayer();
        _sliderLayer.ValueChanged += (_, _) =>
        {
            if (_syncingLayer) return;
            SyncLayer();
        };

        #endregion

        Content = root;
    }

    /// <summary>内部的 3D 查看器；没有构建成功时为 null。</summary>
    public SchematicViewer3D? Viewer => _viewer;

    /// <summary>层数。</summary>
    public int LayerCount => _layerCount;

    /// <summary>同步分层状态；滑块变化与复选框切换都走这里。</summary>
    private void SyncLayer()
    {
        if (_viewer is null) return;
        var show = _checkLayer.IsChecked == true;
        _sliderLayer.IsEnabled = show;
        var strings = SchematicStrings.Current;
        if (!show)
        {
            if (_viewer.VisibleTopLayer is not null)
            {
                _viewer.VisibleTopLayer = null;
                _labLayer.Text = strings.LayerAll;
            }

            return;
        }

        var count = Math.Max(1, _layerCount);
        var index = Math.Clamp((int)Math.Round(_sliderLayer.Value), 0, count - 1);
        var top = _viewer.MinLayer + index;
        if (_viewer.VisibleTopLayer == top) return;

        _syncingLayer = true;
        try
        {
            if (Math.Abs(_sliderLayer.Value - index) > 0.01d) _sliderLayer.Value = index;
        }
        finally
        {
            _syncingLayer = false;
        }

        _viewer.VisibleTopLayer = top;
        _labLayer.Text = string.Format(CultureInfo.CurrentCulture, strings.LayerInfo, index + 1, count, top);
    }

    private Border BuildBlockList(SchematicData data, TextureAtlas? textures)
    {
        var strings = SchematicStrings.Current;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in data.Blocks)
        {
            var id = TrimId(block.Name);
            counts[id] = counts.TryGetValue(id, out var count) ? count + 1 : 1;
        }

        var panel = new Border
        {
            Width = 224,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = SchematicTheme.Surface,
            BorderBrush = SchematicTheme.Border
        };

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });

        var header = Ui.Text(string.Format(CultureInfo.CurrentCulture, strings.BlockList, counts.Count,
            data.Blocks.Count), 12.5d, true, SchematicTheme.TextPrimary);
        header.TextWrapping = TextWrapping.Wrap;
        header.Margin = new Thickness(12, 10, 12, 8);
        stack.Children.Add(header);

        var line = new Rectangle { Height = 1, Fill = SchematicTheme.Border };
        Grid.SetRow(line, 1);
        stack.Children.Add(line);

        var list = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        foreach (var pair in counts.OrderByDescending(p => p.Value)
                     .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Take(600))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (textures is not null)
            {
                var image = new Image
                {
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    Source = textures.GetBlockIcon(pair.Key),
                    Margin = new Thickness(0, 0, 8, 0),
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                row.Children.Add(image);
            }

            var name = Ui.Text(BlockNames.Get(pair.Key, _jarPath), 12d, false, SchematicTheme.TextSecondary);
            name.ToolTip = pair.Key;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var count = Ui.Text("×" + pair.Value, 11d, false, SchematicTheme.TextMuted);
            count.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(count, 2);
            row.Children.Add(count);

            list.Children.Add(row);
        }

        var scroll = Ui.Scroll(list);
        scroll.MaxHeight = 320;
        Grid.SetRow(scroll, 2);
        stack.Children.Add(scroll);
        panel.Child = stack;
        return panel;
    }

    private static Border BuildDetailPanel(string info)
    {
        var text = Ui.WrappedText(info, 12d, SchematicTheme.TextSecondary);
        var panel = new Border
        {
            Child = Ui.Scroll(text),
            Width = 380,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 12),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            Background = SchematicTheme.Surface,
            BorderBrush = SchematicTheme.Border,
            MaxHeight = 260
        };
        return panel;
    }

    private static string TrimId(string name)
    {
        var id = name.Trim();
        if (id.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) id = id[10..];
        var cut = id.IndexOfAny(['[', '{']);
        if (cut >= 0) id = id[..cut];
        return id.ToLowerInvariant();
    }
}
