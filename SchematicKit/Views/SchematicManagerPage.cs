using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SchematicKit;

/// <summary>
///     投影管理页：实例选择 + 搜索 + 排序 + 列表，点击条目在页面内全屏预览。
///     不依赖任何启动器框架，宿主把实例列表通过 <see cref="ISchematicInstanceProvider" /> 接进来即可。
/// </summary>
public sealed class SchematicManagerPage : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _emptyHint;
    private readonly TextBlock _listCount;
    private readonly TextBlock _titleCount;
    private readonly TextBlock _instanceName;
    private readonly TextBlock _instancePath;
    private readonly Border _instanceLogoHost;
    private readonly ComboBox _instanceBox;
    private readonly TextBox _searchBox;
    private readonly Button _sortButton;
    private readonly Grid _overlay = new();
    private readonly SchematicPreviewHost _previewHost;
    private readonly SchematicService _service;
    private readonly List<(SchematicInstance Instance, int Count)> _orderedInstances = [];
    private SchematicSortOrder _order = SchematicSortOrder.ModifiedTime;
    private bool _syncing;

    public SchematicManagerPage(SchematicService service)
    {
        _service = service;
        Background = SchematicTheme.WindowBackground;
        var strings = SchematicStrings.Current;

        #region 页面骨架

        var root = new Grid();
        Content = root;

        var page = new StackPanel { Margin = new Thickness(25, 24, 25, 24) };
        var scroll = Ui.Scroll(page);
        scroll.Padding = new Thickness(0);
        root.Children.Add(scroll);

        // 标题
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(strings.Title, 20d, true);
        titleRow.Children.Add(title);
        _titleCount = Ui.Text("", 12d, false, SchematicTheme.TextMuted);
        _titleCount.VerticalAlignment = VerticalAlignment.Bottom;
        _titleCount.Margin = new Thickness(0, 0, 0, 3);
        Grid.SetColumn(_titleCount, 1);
        titleRow.Children.Add(_titleCount);
        page.Children.Add(titleRow);

        // 实例选择卡片
        page.Children.Add(Ui.Text(strings.InstancePick, 12d, false, SchematicTheme.TextMuted));
        page.Children.Add(new Border { Height = 6 });

        _instanceLogoHost = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(7),
            Background = SchematicTheme.SurfaceHover,
            VerticalAlignment = VerticalAlignment.Center
        };
        _instanceName = Ui.Text("", 14.5d, true);
        _instanceName.TextTrimming = TextTrimming.CharacterEllipsis;
        _instancePath = Ui.Text("", 11d, false, SchematicTheme.TextMuted);
        _instancePath.TextTrimming = TextTrimming.CharacterEllipsis;
        _instancePath.Margin = new Thickness(0, 2, 0, 0);

        var instanceText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        instanceText.Children.Add(_instanceName);
        instanceText.Children.Add(_instancePath);

        _instanceBox = new ComboBox
        {
            Width = 280,
            Height = 32,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = SchematicTheme.TextPrimary,
            Background = SchematicTheme.Surface
        };
        _instanceBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var index = _instanceBox.SelectedIndex;
            if (index < 0 || index >= _orderedInstances.Count) return;
            _service.Select(_orderedInstances[index].Instance);
            UpdateInstanceCard();
            ReloadFiles();
        };

        var card = Ui.Card(padding: new Thickness(14, 12, 14, 12));
        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardGrid.Children.Add(_instanceLogoHost);
        instanceText.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(instanceText, 1);
        cardGrid.Children.Add(instanceText);
        Grid.SetColumn(_instanceBox, 2);
        cardGrid.Children.Add(_instanceBox);
        card.Child = cardGrid;
        page.Children.Add(card);

        page.Children.Add(new Border { Height = 14 });

        // 工具栏
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchBox = Ui.SearchBox(strings.SearchHint, out var searchHost);
        searchBox.Width = 300;
        searchBox.HorizontalAlignment = HorizontalAlignment.Left;
        _searchBox = searchBox;
        _searchBox.TextChanged += (_, _) => ReloadFiles();
        toolbar.Children.Add(searchHost);

        var actions = Ui.Row();
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        _sortButton = Ui.Button("", false, 32d, 120d);
        _sortButton.Margin = new Thickness(0, 0, 8, 0);
        _sortButton.Click += (_, _) =>
        {
            _order = _order == SchematicSortOrder.ModifiedTime
                ? SchematicSortOrder.FileName
                : SchematicSortOrder.ModifiedTime;
            UpdateSortButton();
            ReloadFiles();
        };
        actions.Children.Add(_sortButton);

        var refresh = Ui.Button(strings.Refresh, false, 32d);
        refresh.Margin = new Thickness(0, 0, 8, 0);
        refresh.Click += (_, _) => ReloadAll();
        actions.Children.Add(refresh);

        var openFolder = Ui.Button(strings.OpenFolder, false, 32d);
        openFolder.Margin = new Thickness(0, 0, 8, 0);
        openFolder.Click += (_, _) => OpenSchematicFolder();
        actions.Children.Add(openFolder);

        var import = Ui.Button(strings.Import, true, 32d);
        import.Click += (_, _) => ImportFiles();
        actions.Children.Add(import);

        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        // 列表
        var listCard = Ui.Card();
        var listStack = new StackPanel();

        var listHeader = new Grid { Margin = new Thickness(16, 13, 16, 10) };
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        listHeader.Children.Add(Ui.Text(strings.ListTitle, 13.5d, true, SchematicTheme.TextSecondary));
        _listCount = Ui.Text("", 11.5d, false, SchematicTheme.TextMuted);
        Grid.SetColumn(_listCount, 1);
        listHeader.Children.Add(_listCount);
        listStack.Children.Add(listHeader);
        listStack.Children.Add(Ui.Divider());

        _rows.Margin = new Thickness(8, 4, 8, 8);
        listStack.Children.Add(_rows);

        _emptyHint = Ui.WrappedText(strings.Empty, 12d, SchematicTheme.TextMuted);
        _emptyHint.Margin = new Thickness(16, 14, 16, 18);
        _emptyHint.Visibility = Visibility.Collapsed;
        listStack.Children.Add(_emptyHint);

        listCard.Child = listStack;
        page.Children.Add(listCard);

        // 预览浮层
        _overlay.Background = SchematicTheme.WindowBackground;
        _overlay.Visibility = Visibility.Collapsed;
        Grid.SetZIndex(_overlay, 10);
        root.Children.Add(_overlay);

        var overlayGrid = new Grid { Margin = new Thickness(25, 20, 25, 20) };
        _previewHost = new SchematicPreviewHost(_service);
        _previewHost.Closed += (_, _) => _overlay.Visibility = Visibility.Collapsed;
        overlayGrid.Children.Add(_previewHost);
        _overlay.Children.Add(overlayGrid);

        #endregion

        Loaded += (_, _) => Initialize();
        Unloaded += (_, _) => _previewHost.Close();
    }

    /// <summary>投影业务入口，宿主可以据此实现自己的列表或刷新逻辑。</summary>
    public SchematicService Service => _service;

    /// <summary>当前是否正在显示预览。</summary>
    public bool IsPreviewOpen => _overlay.Visibility == Visibility.Visible;

    private void Initialize()
    {
        if (_orderedInstances.Count > 0) return;
        ReloadAll();
    }

    /// <summary>重新扫描实例并刷新列表。</summary>
    public void ReloadAll()
    {
        _service.Reload();
        RebuildInstances();
        UpdateInstanceCard();
        ReloadFiles();
    }

    private void RebuildInstances()
    {
        var strings = SchematicStrings.Current;
        var ordered = _service.Instances
            .Select(instance => (Instance: instance, Count: instance.CountSchematics()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Instance.Name, StringComparer.Ordinal)
            .ToList();

        _orderedInstances.Clear();
        _orderedInstances.AddRange(ordered);

        _syncing = true;
        try
        {
            _instanceBox.Items.Clear();
            foreach (var (instance, count) in ordered)
                _instanceBox.Items.Add($"{instance.Name}  ·  " +
                                       string.Format(CultureInfo.CurrentCulture, strings.VersionInfo, count));

            var index = _service.Current is null
                ? -1
                : ordered.FindIndex(entry => entry.Instance.Name == _service.Current.Name);
            _instanceBox.SelectedIndex = index >= 0 ? index : ordered.Count > 0 ? 0 : -1;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateInstanceCard()
    {
        var strings = SchematicStrings.Current;
        var instance = _service.Current;
        _instanceName.Text = instance?.Name ?? strings.NoInstance;
        _instancePath.Text = instance?.SchematicFolder ?? "";
        _instanceLogoHost.Child = null;

        if (instance?.LogoPath is { Length: > 0 } logo && File.Exists(logo))
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(logo, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                _instanceLogoHost.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                return;
            }
            catch (Exception ex)
            {
                SchematicOptions.Log(ex, "读取实例图标失败");
            }

        _instanceLogoHost.Child = new TextBlock
        {
            Text = "▦",
            FontSize = 16,
            Foreground = SchematicTheme.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void UpdateSortButton()
    {
        var strings = SchematicStrings.Current;
        _sortButton.Content = string.Format(CultureInfo.CurrentCulture, strings.SortBy,
            _order == SchematicSortOrder.ModifiedTime ? strings.SortModifyTime : strings.SortFileName);
    }

    /// <summary>只按当前实例、排序与搜索条件重建列表。</summary>
    public void ReloadFiles()
    {
        var strings = SchematicStrings.Current;
        UpdateSortButton();
        var files = _service.Scan(_order, _searchBox.Text);
        _rows.Children.Clear();
        foreach (var entry in files) _rows.Children.Add(BuildRow(entry));

        _titleCount.Text = string.Format(CultureInfo.CurrentCulture, strings.Count, files.Count);
        _listCount.Text = string.Format(CultureInfo.CurrentCulture, strings.VersionInfo, files.Count);
        _emptyHint.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildRow(SchematicFileEntry entry)
    {
        var strings = SchematicStrings.Current;
        var border = new Border
        {
            Height = 50,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 0, 8, 0)
        };
        border.MouseEnter += (_, _) => border.Background = SchematicTheme.SurfaceHover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => OpenPreview(entry);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = SchematicTheme.SurfaceHover,
            Margin = new Thickness(4, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "▦",
                FontSize = 14,
                Foreground = SchematicTheme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(entry.DisplayName, 13d, true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var info = Ui.Text(
            $"{SchematicInfo.FormatSize(entry.Size)} · {entry.Modified:g} · {entry.RelativePath}", 10.5d, false,
            SchematicTheme.TextMuted);
        info.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(name);
        text.Children.Add(info);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var previewButton = Ui.Button(strings.Preview, true, 26d, 64d, 11.5d);
        previewButton.Padding = new Thickness(10, 0, 10, 0);
        previewButton.Margin = new Thickness(10, 0, 0, 0);
        previewButton.VerticalAlignment = VerticalAlignment.Center;
        previewButton.Click += (_, _) => OpenPreview(entry);
        Grid.SetColumn(previewButton, 2);
        grid.Children.Add(previewButton);

        border.Child = grid;
        return border;
    }

    private async void OpenPreview(SchematicFileEntry entry)
    {
        await OpenFileAsync(entry.Path, entry.DisplayName).ConfigureAwait(true);
    }

    /// <summary>
    ///     直接打开某个文件的预览（实例资源页可以调用它，把预览嵌在自己的页面里）。
    ///     返回是否显示成功。
    /// </summary>
    public async Task<bool> OpenFileAsync(string path, string? title = null)
    {
        if (!File.Exists(path)) return false;
        var jarPath = _service.ResolveJarPath(path);
        _overlay.Visibility = Visibility.Visible;
        var shown = await _previewHost.LoadAsync(path, jarPath, title).ConfigureAwait(true);
        if (!shown) _overlay.Visibility = Visibility.Collapsed;
        return shown;
    }

    /// <summary>关闭预览并释放贴图集。</summary>
    public void ClosePreview() => _previewHost.Close();

    private void OpenSchematicFolder()
    {
        var instance = _service.Current;
        if (instance is null) return;
        try
        {
            Directory.CreateDirectory(instance.SchematicFolder);
            SchematicOptions.OpenFolder?.Invoke(instance.SchematicFolder);
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, "打开投影文件夹失败");
        }
    }

    private void ImportFiles()
    {
        var instance = _service.Current;
        if (instance is null) return;
        var strings = SchematicStrings.Current;

        var dialog = new OpenFileDialog
        {
            Title = strings.ImportDialogTitle,
            Filter = strings.ImportDialogFilter,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var written = SchematicLibrary.Import(instance, dialog.FileNames);
            ReloadFiles();
            _listCount.Text = string.Format(CultureInfo.CurrentCulture, strings.ImportDone, written.Count);
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, strings.ImportFailed);
        }
    }
}
