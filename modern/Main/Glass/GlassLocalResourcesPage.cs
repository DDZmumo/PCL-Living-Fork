// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Controller = PCLCS.ResourcePageController<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using View = PCLCS.ResourcePageView<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using Path = System.Windows.Shapes.Path;

namespace PCL;

internal sealed class GlassLocalResourcesPage : ContentControl, IDisposable
{
    private readonly FormMain _window;
    private readonly InstanceResourceKind _kind;
    private readonly GlassPageResources _resources;
    private readonly Controller _controller;
    private readonly ModLoader.LoaderTask<Controller.Request, Controller.Result?> _load;
    private readonly InstanceResourceScanCache<InstanceResourceEntry> _scan = new();
    private readonly GlassWorldBackupSettings? _backupSettings;
    private readonly BlockCurveLoader _loading = new() { Width = 43, Height = 43, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _loadText;
    private readonly Button _retry;
    private readonly GlassSurface _loadStatus;
    private readonly Grid _body;
    private readonly GlassSelectionDock _selection = new();
    private bool _active, _opened, _disposed;
    private GlassSchematicPreview? _schematicPreview;
    private double _previewReturnOffset;
    private DispatcherOperation? _pendingOpen;
    private WorldBackups.ChangedEventHandler? _backupChanged;
    private KeyEventHandler? _keyHandler;
    internal ScrollViewer Scroll { get; }
    internal ScrollViewer? ActiveScroll => _schematicPreview is null ? Scroll : null;

    internal GlassLocalResourcesPage(FormMain window, InstanceResourceKind kind) : this(window, kind, null) { }

    internal GlassLocalResourcesPage(FormMain window, InstanceResourceKind kind, Func<ModMinecraft.McInstance?>? instanceSource)
    {
        _window = window;
        _kind = kind;
        Focusable = false;
        AllowDrop = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        _body = new Grid();
        Content = _body;
        var sections = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        Scroll = new ScrollViewer { Content = sections, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly, Focusable = true, Padding = new Thickness(0, 0, 0, 28) };
        SmoothScroll.SetIsEnabled(Scroll, true);
        _body.Children.Add(new ScrollEdgeFade(Scroll));

        var view = new View {
            Root = this, Viewport = Scroll,
            LabEmpty = _resources.Note("", 14), LabEmptyHint = _resources.Note("", 13), LabEmptyTitle = _resources.Text(""),
            LabOperation = _resources.Note("", 13), LabBackupStatus = _resources.Note("", 13), LabSelect = _selection.Status,
            PanList = new StackPanel(), PanWorlds = new StackPanel(), PanScreenshots = new UniformGrid { Columns = 2 },
            TransSelect = _selection.Position, SaveLogo = ModBase.Logo.IconButtonSave, RestoreLogo = ModBase.Logo.IconButtonRefresh,
            FocusViewport = () => Scroll.Focus(), ScrollHome = Scroll.ScrollToTop
        };
        if (kind == InstanceResourceKind.Saves)
        {
            _backupSettings = new GlassWorldBackupSettings(_resources) { Margin = new Thickness(0, 15, 0, 0) };
            sections.Children.Add(_backupSettings);
        }
        view.BackupSettings = _backupSettings as FrameworkElement ?? new Border();
        view.BindBackupOwner = owner => _backupSettings?.Bind(owner);
        view.CommitBackupSettings = () => _backupSettings?.Commit() ?? true;

        var commands = new StackPanel();
        var controls = new WrapPanel();
        view.PanManageButtons = new Grid { Children = { controls } };
        view.ToolbarWrapsContent = true;
        view.BtnOpenFolder = Action("打开文件夹", () => _controller.OpenFolder());
        view.BtnImportFile = Action("从文件安装", () => _controller.ImportFiles());
        view.BtnDownload = Action("下载新资源", () => _controller.DownloadNew());
        view.BtnSelectAll = Action("全选", () => _controller.SelectAllClick());
        view.BtnBackupAll = Action("立即备份", () => _controller.BackupAll());
        if (kind != InstanceResourceKind.Saves) view.BtnBackupAll.Visibility = Visibility.Collapsed;
        // Opening the folder is the toolbar's primary action, as on the mod page.
        if (view.BtnOpenFolder.Element is Button openFolder) openFolder.Style = _resources.Style("GlassPresentationAccentButton");
        foreach (var button in new[] { view.BtnOpenFolder, view.BtnImportFile, view.BtnDownload, view.BtnSelectAll, view.BtnBackupAll })
        {
            if (button.Element is Button command) GlassPageLayout.SizeToolbarAction(command);
            controls.Children.Add(button.Element);
        }
        if (kind == InstanceResourceKind.Schematics && instanceSource is null) {
            var manager = (Button)Action("投影管理", () => _window.PageChange(new FormMain.PageStackData {
                Page = FormMain.PageType.Other, Additional = _controller.Owner
            }, FormMain.PageSubType.OtherSchematics)).Element;
            GlassPageLayout.SizeToolbarAction(manager);
            controls.Children.Add(manager);
        }
        // One toolbar card: the search field on top, its commands in a row underneath.
        controls.Margin = new Thickness(0, 10, 0, 0);
        var search = GlassPageLayout.SearchField();
        var query = new TextBox { Style = _resources.Style("GlassPresentationSearchBox") };
        var placeholder = _resources.Note("", 14);
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextWrapping = TextWrapping.NoWrap;
        placeholder.Margin = new Thickness(35, 0, 32, 0);
        var magnifier = new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        magnifier.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "GlassPresentationSecondary");
        search.Children.Add(query);
        search.Children.Add(magnifier);
        search.Children.Add(placeholder);
        GlassPageResources.BindTextPlaceholder(query, placeholder);
        view.GetSearch = () => query.Text;
        view.SetSearch = value => query.Text = value;
        view.SetSearchHint = value => { placeholder.Text = value; AutomationProperties.SetName(query, value); };
        view.SearchHasFocus = () => query.IsKeyboardFocusWithin;
        query.TextChanged += (_, _) => _controller?.SearchChanged();
        query.PreviewKeyDown += (sender, e) => _controller?.SearchKeyDown(sender, e);
        commands.Children.Add(search);
        commands.Children.Add(view.PanManageButtons);
        GlassPageLayout.PreserveRowSize(view.PanManageButtons);
        var operation = new WrapPanel { Margin = new Thickness(0, 9, 0, 0), Visibility = Visibility.Collapsed };
        view.PanOperations = operation;
        view.LabOperation.Visibility = Visibility.Collapsed;
        view.LabOperation.VerticalAlignment = VerticalAlignment.Center;
        view.LabOperation.Margin = new Thickness(8, 0, 8, 0);
        view.BtnUp = Action("上一级", () => _controller.UpClick());
        view.BtnUp.Visibility = Visibility.Collapsed;
        view.BtnCancel = Action("取消", () => _controller.CancelClick());
        operation.Children.Add(view.BtnUp.Element);
        operation.Children.Add(view.LabOperation);
        operation.Children.Add(view.BtnCancel.Element);
        commands.Children.Add(operation);
        var toolbar = GlassPageResources.Card(commands, GlassPageLayout.CardRole.Toolbar);
        sections.Children.Add(toolbar);

        var listBody = new StackPanel();
        var header = new Grid { Margin = new Thickness(4, 0, 4, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filters = new WrapPanel();
        view.PanFilter = filters;
        view.FilterAll = Filter("全部", 0);
        view.FilterUpdates = Filter("可更新", 1);
        view.FilterFiles = Filter("文件", 2);
        view.FilterFolders = Filter("文件夹", 3);
        view.Filters = new[] { view.FilterAll, view.FilterUpdates, view.FilterFiles, view.FilterFolders };
        foreach (var filter in view.Filters) filters.Children.Add(filter.Element);
        header.Children.Add(filters);
        ResourcePageButton? sortHandle = null;
        var sort = Action("", () =>
        {
            if (sortHandle?.Element is not Button button) return;
            LocalResourceUi.ShowSortMenu(button, _controller.SortMode, kind == InstanceResourceKind.Saves, mode =>
            {
                _controller.SortChanged(mode);
                SortText(view);
            });
        });
        sortHandle = sort;
        view.SortHeader = header;
        view.SortControl = sort.Element;
        Grid.SetColumn(sort.Element, 1);
        header.Children.Add(sort.Element);
        listBody.Children.Add(header);
        view.LabBackupStatus.Margin = new Thickness(16, 6, 16, 6);
        listBody.Children.Add(view.LabBackupStatus);
        var lists = new StackPanel { Children = { view.PanList, view.PanWorlds, view.PanScreenshots, view.LabEmpty } };
        view.PanResourceList = lists;
        view.LabEmpty.Name = "LabEmpty";
        view.LabEmpty.Margin = new Thickness(5, 2, 5, 8);
        view.BtnHintDownload = Action("下载新资源", () => _controller.DownloadNew());
        view.PanEmpty = new StackPanel { Visibility = Visibility.Collapsed, Children = { view.LabEmptyTitle, view.LabEmptyHint, view.BtnHintDownload.Element } };
        listBody.Children.Add(lists);
        sections.Children.Add(view.PanEmpty);
        var card = new LocalCard(listBody, _resources, expanded: true, showHeader: false);
        view.CardList = card;
        sections.Children.Add(card.Element);
        var selectionTail = new Border { Height = 0 };
        sections.Children.Add(selectionTail);

        void RefreshSelectionTail() => selectionTail.Height = _selection.ScrollClearance(Scroll);
        _selection.SizeChanged += (_, _) => RefreshSelectionTail();
        Scroll.SizeChanged += (_, _) => RefreshSelectionTail();

        view.BtnUpdate = Action("更新", () => _controller.UpdateSelected());
        view.BtnBackupRestore = Action("恢复", () => _controller.RestoreWorldBackup());
        view.BtnExport = Action("导出", () => _controller.ExportSelected());
        view.BtnDelete = Action("删除", () => _controller.DeleteSelected());
        view.BtnDeselect = Action("取消选择", () => _controller.DeselectClick());
        _selection.AddCommand((Button)view.BtnUpdate.Element, GlassSelectionDock.UpdateIcon);
        _selection.AddCommand((Button)view.BtnBackupRestore.Element, ModBase.Logo.IconButtonRefresh);
        _selection.AddCommand((Button)view.BtnExport.Element, ModBase.Logo.IconButtonSave);
        _selection.AddCommand((Button)view.BtnDelete.Element, GlassSelectionDock.DeleteIcon, danger: true);
        _selection.AddCommand((Button)view.BtnDeselect.Element, GlassSelectionDock.CancelIcon, dismiss: true);
        view.CardSelect = _selection;
        view.RefreshSelectionBar = (count, previous, animate) => {
            if (count != previous) _selection.ShowSelection(count, animate);
            RefreshSelectionTail();
            return count;
        };
        view.StopSelectionBar = () => {
            _selection.ShowSelection(0, false);
            RefreshSelectionTail();
        };
        _selection.SetBackdropSource(Scroll);
        _body.Children.Add(_selection);

        _loadText = _resources.Note("", 14);
        _loadText.Margin = new Thickness(0, 10, 0, 0);
        _loadText.TextAlignment = TextAlignment.Center;
        _loadText.MaxWidth = 480;
        _retry = (Button)Action("重试", () => _controller.RetryLoad()).Element;
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.Margin = new Thickness(0, 12, 0, 0);
        _loading.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        _loadStatus = GlassPageResources.Plate(new StackPanel { Children = { _loading, _loadText, _retry } }, new Thickness(0, 0, 0, 18));
        _loadStatus.HorizontalAlignment = HorizontalAlignment.Center;
        _loadStatus.VerticalAlignment = VerticalAlignment.Center;
        _loadStatus.Visibility = Visibility.Collapsed;
        _body.Children.Add(_loadStatus);

        bool artworkQueued = false;
        void QueueArtwork()
        {
            if (artworkQueued || kind is not (InstanceResourceKind.ResourcePacks or InstanceResourceKind.ShaderPacks)) return;
            artworkQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                artworkQueued = false;
                LoadVisibleArtwork(view.PanList);
            }));
        }
        view.CreateRow = actions => new GlassLocalResourceRow(kind, _resources, actions, QueueArtwork,
            kind == InstanceResourceKind.Schematics ? ShowSchematicPreview : null);
        view.PanList.Loaded += (_, _) => QueueArtwork();
        view.CreateThumbnail = (changed, click) => new GlassResourceScreenshot(_resources, changed, click);
        view.CreateBackupGroup = (changed, before) => new GlassWorldBackupGroup(_resources, changed, before);
        view.CreateBackupCard = content => new LocalCard(content, _resources, expanded: false, showHeader: true);
        var host = InstanceResourceApplication.CreateHost(kind, _scan, () => _active, () => MotionPreferences.AllowsAnimation,
            request => _load!.Start(request, IsForceRestart: true), () => _load!.Cancel(),
            () => _load!.State == ModBase.LoadState.Failed, BindExternalEvents);
        if (instanceSource is not null) host.CurrentInstance = instanceSource;
        _controller = new Controller((int)kind, view, host, new InstanceResourceTransactions(PathUtils.RemoveExtendedPrefix));
        _load = new("读取游戏资源", task =>
        {
            Thread worker = Thread.CurrentThread;
            bool Canceled() => task.IsCanceledWithThread(worker);
            var result = _controller.Read(task.Input, task.CreateCancellationToken(), Canceled);
            if (!Canceled()) task.Output = result;
        });
        _load.OnStateChangedUi += OnLoadedResources;
        SortText(view);
        Scroll.ScrollChanged += (_, _) => { _controller.RefreshScreenshotViewport(); QueueArtwork(); };
        DragOver += (_, e) => { if (_schematicPreview is null) _controller.ResourceDragOver(this, e); };
        Drop += (_, e) => { if (_schematicPreview is null) _controller.ResourceDrop(this, e); };
    }

    internal void Open(bool loadData)
    {
        _active = true;
        _resources.ApplyPalette();
        Scroll.ScrollToTop();
        if (!loadData) return;
        _pendingOpen = Dispatcher.InvokeAsync(() => {
            if (!_active || _disposed) return;
            _opened = true;
            _controller.PageLoaded();
        }, DispatcherPriority.Background);
    }

    internal void Refresh()
    {
        if (_active && _opened)
        {
            _controller.Refresh();
            UpdateStatus();
        }
    }

    internal void PaletteChanged() {
        _resources.ApplyPalette();
        _schematicPreview?.PaletteChanged();
    }

    private void ShowSchematicPreview(InstanceResourceEntry entry)
    {
        if (!_active || _controller.Owner is not { } owner || entry.IsDirectory) return;
        CloseSchematicPreview();
        _previewReturnOffset = Scroll.VerticalOffset;
        _schematicPreview = new GlassSchematicPreview(owner, entry, () => CloseSchematicPreview());
        Content = _schematicPreview;
        // The preview rises in and grows slightly into place; its model then plays its own entrance.
        PlayEnter(_schematicPreview, MotionTokens.PageTravel * 1.5, MotionTokens.RevealScale, 300);
        _window.RefreshBackToTop();
    }

    internal bool CloseSchematicPreview()
    {
        if (_schematicPreview is not { } preview) return false;
        _schematicPreview = null;
        Content = _body;
        Scroll.ScrollToVerticalOffset(_previewReturnOffset);
        preview.Dispose();
        // Back to the list: it settles down from slightly above.
        if (_active) PlayEnter(_body, -MotionTokens.PageTravel, 1, 220);
        _window.RefreshBackToTop();
        return true;
    }

    // Fades an element in while it travels `travel` px into place and grows from `scale`; nothing is left behind afterwards.
    private static void PlayEnter(FrameworkElement element, double travel, double scale, int milliseconds)
    {
        if (!MotionPreferences.AllowsAnimation) return;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var move = new TranslateTransform(0, travel);
        var grow = new ScaleTransform(scale, scale);
        Transform former = element.RenderTransform;
        bool hadTransform = element.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue;
        element.RenderTransformOrigin = new Point(.5, .35);
        element.RenderTransform = new TransformGroup { Children = { grow, move } };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        fade.Completed += (_, _) => {
            if (hadTransform) element.RenderTransform = former; else element.ClearValue(UIElement.RenderTransformProperty);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(travel, 0, duration) { EasingFunction = ease });
        if (scale != 1) {
            var growth = new System.Windows.Media.Animation.DoubleAnimation(scale, 1, duration) { EasingFunction = ease };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, growth);
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, growth);
        }
    }

    internal void Leave()
    {
        _active = false;
        CloseSchematicPreview();
        _pendingOpen?.Abort();
        _pendingOpen = null;
        _loading.IsActive = false;
        if (!_opened) return;
        _opened = false;
        _controller.PageUnloaded();
    }

    private void OnLoadedResources(ModLoader.LoaderBase _, ModBase.LoadState state, ModBase.LoadState former)
    {
        if (!_active || _disposed || _load.State != state) return;
        if (state == ModBase.LoadState.Finished) _controller.Apply(_load.Output);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        bool loading = _load.State == ModBase.LoadState.Loading;
        bool failed = _load.State == ModBase.LoadState.Failed;
        _loading.IsActive = loading;
        _loading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        _loadText.Text = failed ? _load.Error?.Message ?? "读取游戏资源失败" : "正在读取游戏资源";
        _loadStatus.Visibility = loading || failed ? Visibility.Visible : Visibility.Collapsed;
        Scroll.IsHitTestVisible = _loadStatus.Visibility != Visibility.Visible;
    }

    private void BindExternalEvents(bool add, Action refresh, KeyEventHandler keys)
    {
        if (add)
        {
            _keyHandler = (sender, args) => { if (!_window.GlassMessageActive && _schematicPreview is null) keys(sender, args); };
            _window.PreviewKeyDown += _keyHandler;
            if (_kind == InstanceResourceKind.Saves)
            {
                _backupChanged = () => ModBase.RunInUi(refresh);
                WorldBackups.Changed += _backupChanged;
            }
        }
        else
        {
            if (_keyHandler is not null) _window.PreviewKeyDown -= _keyHandler;
            if (_backupChanged is not null) WorldBackups.Changed -= _backupChanged;
            _keyHandler = null;
            _backupChanged = null;
        }
    }

    private void SortText(View view)
    {
        if (view.SortControl is Button button) LocalResourceUi.SetSortButton(button, _controller.SortMode);
    }

    private void LoadVisibleArtwork(Panel rows)
    {
        if (!rows.IsVisible || !rows.IsArrangeValid || rows.Children.Count == 0 || !Scroll.IsAncestorOf(rows)) return;
        double top = -rows.TranslatePoint(new Point(), Scroll).Y - 52;
        double bottom = top + Scroll.ViewportHeight + 104;
        int first = 0, last = rows.Children.Count;
        while (first < last)
        {
            int mid = first + (last - first) / 2;
            var child = rows.Children[mid];
            if (VisualTreeHelper.GetOffset(child).Y + child.RenderSize.Height < top) first = mid + 1;
            else last = mid;
        }
        for (int i = first; i < rows.Children.Count; i++)
        {
            var child = rows.Children[i];
            if (VisualTreeHelper.GetOffset(child).Y > bottom) break;
            if (child is GlassLocalResourceRow row) row.LoadArtwork();
        }
    }

    private ResourcePageButton Action(string text, Action handler)
    {
        // Compact toolbar commands, the same size as the mod page's.
        var button = new Button { Content = text, Style = _resources.Style("GlassPresentationButton"), MinWidth = 88, Height = 34,
            Padding = new Thickness(14, 0, 14, 0), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => handler();
        return new ResourcePageButton(button, () => button.Content?.ToString() ?? "", value => button.Content = value, logo => GlassSelectionDock.SetIcon(button, logo));
    }

    private ResourcePageFilter Filter(string text, int index)
    {
        var button = new RadioButton { Content = text, Style = _resources.Style("GlassResourceFilter") };
        button.Click += (_, _) => _controller.FilterChanged(index);
        return new ResourcePageFilter(button, index, () => button.IsChecked == true, value => button.IsChecked = value,
            value => button.Content = value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Leave();
        _disposed = true;
        _load.OnStateChangedUi -= OnLoadedResources;
        _selection.Dispose();
    }

    private sealed class LocalCard : IResourcePageCard
    {
        private readonly TextBlock _title;
        public FrameworkElement Element { get; }
        public Panel Children { get; }
        public string Title { get => _title.Text; set => _title.Text = value; }
        internal LocalCard(Panel content, GlassPageResources resources, bool expanded, bool showHeader)
        {
            _title = resources.Text("");
            _title.FontWeight = FontWeights.SemiBold;
            Children = content;
            UIElement body = showHeader ? new Expander { Header = _title, Content = content, IsExpanded = expanded,
                Style = resources.Style("GlassPresentationExpander") } : content;
            // Same list card as the mod page.
            var plate = GlassPageResources.Card(body, showHeader ? GlassPageLayout.CardRole.Expander : GlassPageLayout.CardRole.SettingsSection,
                GlassPageLayout.Mode.Settings);
            Element = plate;
        }
        public void InitializeHeader() { }
    }
}
