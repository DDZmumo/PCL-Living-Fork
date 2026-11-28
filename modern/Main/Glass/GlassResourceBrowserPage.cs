// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using SearchRequest = PCL.ResourceSearcher.SearchRequest;
using McInstance = PCL.ModMinecraft.McInstance;
using Path = System.Windows.Shapes.Path;
using ItemRules = PCLCS.ResourceItemController<PCL.ResourceProject, PCL.ResourceSearcher.SearchResult, PCL.ResourceSearcher.SearchRequest,
    PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.ModAnimation.AniData>;

namespace PCL;

internal sealed class GlassResourceBrowserPage : ContentControl, IDisposable
{
    private const int FavoritesPerPage = 40;
    private const double ExitThreshold = .018;
    private readonly FormMain _window;
    private readonly LauncherResourceBrowser? _search;
    private readonly GlassPageResources _resources;
    private readonly Grid _layout = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _query;
    private readonly ComboBox _version, _source, _tags, _loader, _pageSize;
    private readonly StackPanel _loaderField, _filterContent, _rows = new();
    private readonly GlassSurface _filterPlate;
    private readonly Grid _statusHost;
    private readonly ScrollViewer _filterScroll;
    private readonly Button _filter, _first, _previous, _next, _retry;
    private readonly TextBlock _page, _status, _warning;
    private readonly FrameworkElement _warningPlate;
    private readonly BlockCurveLoader _loading;
    private readonly ProgressBar _progress;
    private readonly GlassStatusGlyph _failed, _empty;
    private readonly SpringMotion _filterMotion, _rowMotion;
    private readonly TranslateTransform _rowY = new();
    private readonly ScaleTransform _rowDepth = new();
    private readonly List<ProjectSlot> _displayedRows = new();
    // Favorites: notes under their rows (issue #62), folded to one line unless expanded; one toolbar button folds them all.
    private Button? _notesToggle;
    private FrameworkElement? _notesSlot;
    private bool _notesExpanded = true;
    private const double NoteIndent = 52; // the row's padding + icon + gap, so a note lines up with the project name
    private readonly DispatcherTimer _viewportTimer;
    private static readonly Geometry Placeholder = Geometry.Parse(
        "M4,0 H28 Q32,0 32,4 V28 Q32,32 28,32 H4 Q0,32 0,28 V4 Q0,0 4,0 Z M44,7 H162 V16 H44 Z M44,21 H124 V27 H44 Z");
    private double _filterHeight;
    private bool _active, _disposed, _filterOpen, _synchronizingChoices, _readingFavorites;
    private ResourceTypes? _category;
    private int _favoritePage = 0;
    private ResourceFavoriteFilter _favoriteFilter = new();
    private IReadOnlyList<ResourceFavorite>? _favorites;
    private Exception? _favoriteError;
    private ResourceFavorite[]? _filteredFavorites;
    private ResourceProject[]? _favoriteProjects;
    private int _favoriteDisplayPage = -1;
    private Rows? _pending, _displayed;
    private DispatcherOperation? _swap;
    internal ScrollViewer Scroll { get; }
    private bool CanAnimate => _active && IsLoaded && IsVisible && MotionPreferences.AllowsAnimation;

    private sealed record Rows(object Key, int Page, IReadOnlyList<ResourceProject> Projects, SearchRequest? Request, bool ShowVersion, bool ShowLoader)
    {
        internal bool SamePage(Rows? other) => other is not null && ReferenceEquals(Key, other.Key) && Page == other.Page;
    }

    // Hold the placeholder, the live row and its note in the same animation and layout slot.
    private sealed class ProjectSlot : Grid
    {
        internal ResourceProject Project { get; }
        internal UIElement Row => Children[0];
        internal GlassFavoriteNote? Note => Children.Count > 1 ? (GlassFavoriteNote)Children[1] : null;

        internal ProjectSlot(ResourceProject project, UIElement placeholder)
        {
            Project = project;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(GlassResourceProjectRow.RowHeight) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(placeholder);
        }

        internal void Realize(GlassResourceProjectRow row)
        {
            Children.RemoveAt(0);
            Children.Insert(0, row);
            if (Children.Count > 1) Children[1].Visibility = Visibility.Visible;
        }

        internal void SetNote(string? text, bool expanded)
        {
            GlassFavoriteNote? current = Note;
            if (text == current?.Note) return;
            if (current is not null) Children.RemoveAt(1);
            if (text is null) return;
            var note = new GlassFavoriteNote(text, current?.IsExpanded ?? expanded) { Margin = new Thickness(NoteIndent, 2, 16, 8) };
            // Preserve the note's space so realizing a nearby row never moves the rest of the list.
            note.Visibility = Row is GlassResourceProjectRow ? Visibility.Visible : Visibility.Hidden;
            SetRow(note, 1);
            Children.Add(note);
        }
    }

    internal GlassResourceBrowserPage(FormMain window, ResourceBrowserCategory? category)
    {
        _window = window;
        _category = category?.Type;
        if (category is not null)
        {
            _search = new LauncherResourceBrowser(category.Type, work => Dispatcher.InvokeAsync(work, DispatcherPriority.Background));
            _search.Changed += Refresh;
        }
        else ResourceFavorites.Observe(this, (_, _) => UpdateFavorites());
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        Content = _layout;
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition());

        var toolbar = new Grid { Height = 32, Margin = new Thickness(0, 0, 0, 18) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.Children.Add(toolbar);
        var searchBox = new Grid();
        searchBox.Children.Add(Frost(null, 8));
        _query = new TextBox { Name = "ResourceSearchInput", Style = _resources.Style("GlassPresentationSearchBox") };
        AutomationProperties.SetName(_query, "搜索资源");
        searchBox.Children.Add(_query);
        searchBox.Children.Add(new Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        ((Path)searchBox.Children[2]).SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        var placeholder = _resources.Note("搜索", 14);
        placeholder.Margin = new Thickness(35, 0, 12, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        searchBox.Children.Add(placeholder);
        GlassPageResources.BindTextPlaceholder(_query, placeholder);
        toolbar.Children.Add(searchBox);
        var filterSlot = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        filterSlot.Children.Add(Frost(null, 8));
        _filter = ActionButton("筛选设置", (_, _) => ToggleFilters(!_filterOpen));
        _filter.Name = "ResourceFilterButton";
        filterSlot.Children.Add(_filter);
        Grid.SetColumn(filterSlot, 1);
        toolbar.Children.Add(filterSlot);
        if (_search is null)
        {
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var notesSlot = new Grid { Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
            notesSlot.Children.Add(Frost(null, 8));
            _notesToggle = ActionButton("收起备注", (_, _) => {
                _notesExpanded = !_notesExpanded;
                foreach (ProjectSlot slot in _displayedRows) slot.Note?.SetExpanded(_notesExpanded);
                UpdateNotesCaption();
            });
            _notesToggle.ToolTip = "展开或收起本页所有收藏的备注";
            notesSlot.Children.Add(_notesToggle);
            Grid.SetColumn(notesSlot, 2);
            toolbar.Children.Add(notesSlot);
            _notesSlot = notesSlot;
            ResourceNotes.Changed += OnNotesChanged;
        }

        _filterContent = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var fields = new UniformGrid { Columns = 2 };
        _version = Choice(editable: true);
        _source = Choice();
        _tags = Choice();
        _loader = Choice();
        fields.Children.Add(Field("版本", _version));
        fields.Children.Add(Field("来源", _source));
        fields.Children.Add(Field("类型", _tags));
        _loaderField = Field("加载器", _loader);
        fields.Children.Add(_loaderField);
        _filterContent.Children.Add(fields);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (_category == ResourceTypes.ModPack && _search is not null)
            actions.Children.Add(ActionButton("安装已有整合包", (_, _) => ModModpack.ModpackInstall()));
        actions.Children.Add(ActionButton("重置条件", (_, _) => {
            ResetFilters();
            if (_search is null) Search(closeFilters: false);
            MeasureFilter();
        }));
        actions.Children.Add(ActionButton("应用筛选", (_, _) => Search(), "GlassPresentationAccentButton"));
        foreach (FrameworkElement action in actions.Children) action.Margin = new Thickness(8, 0, 0, 0);
        _filterContent.Children.Add(actions);
        _filterScroll = new ScrollViewer { Content = _filterContent, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _filterPlate = Frost(_filterScroll);
        _filterPlate.Name = "ResourceFilterPanel";
        _filterPlate.ClipToBounds = true;
        Grid.SetRow(_filterPlate, 1);
        _layout.Children.Add(_filterPlate);
        _filterMotion = new SpringMotion(_filterPlate, 0, ApplyFilterPose, new SpringParameters(.28, 1));
        ApplyFilterPose(0);

        var results = new StackPanel();
        _warning = _resources.Text("");
        _warning.TextWrapping = TextWrapping.Wrap;
        _warningPlate = CreateWarning(_warning);
        results.Children.Add(_warningPlate);
        _rows.Name = "ResourceFlatResults";
        _rows.RenderTransformOrigin = new Point(.5, .1);
        _rows.RenderTransform = new TransformGroup { Children = { _rowDepth, _rowY } };
        results.Children.Add(_rows);
        _rowMotion = new SpringMotion(_rows, 1, ApplyRowsPose, MotionTokens.Reveal);
        var status = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 16, 18, 12) };
        _loading = new BlockCurveLoader { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center };
        _loading.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        _failed = new GlassStatusGlyph(GlassStatusGlyph.Kind.Failed, 48) { HorizontalAlignment = HorizontalAlignment.Center };
        _empty = new GlassStatusGlyph(GlassStatusGlyph.Kind.Empty, 48) { HorizontalAlignment = HorizontalAlignment.Center };
        _progress = new ProgressBar { Width = 138, Height = 3, Margin = new Thickness(0, 10, 0, 0), Maximum = 1, BorderThickness = new Thickness() };
        _progress.SetResourceReference(Control.ForegroundProperty, "GlassPresentationAccent");
        _progress.SetResourceReference(Control.BackgroundProperty, "GlassPresentationTrack");
        _status = _resources.Note("", 14);
        _status.TextAlignment = TextAlignment.Center;
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.MaxWidth = 475;
        _retry = ActionButton("重试", (_, _) => Reload());
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.Margin = new Thickness(0, 12, 0, 0);
        foreach (UIElement element in new UIElement[] { _loading, _failed, _empty, _progress, _status, _retry }) status.Children.Add(element);
        // With no rows, the status sits in the middle of the results card instead of at its top edge.
        _statusHost = new Grid { Children = { status } };
        results.Children.Add(_statusHost);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 4, 8) };
        _pageSize = Choice();
        _pageSize.Width = 106;
        _pageSize.Margin = new Thickness(0, 0, 10, 0);
        AutomationProperties.SetName(_pageSize, "每页条数");
        _pageSize.Visibility = _search is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (int size in LauncherResourceBrowser.PageSizes) _pageSize.Items.Add(Item($"{size} 条/页", size));
        _pageSize.SelectionChanged += (_, _) =>
        {
            if (_synchronizingChoices || _search is null || _pageSize.SelectedItem is not ComboBoxItem { Tag: int size }) return;
            Settings.Set(LauncherResourceBrowser.PageSizeSetting, size);
            _search.Resize(size);
        };
        footer.Children.Add(_pageSize);
        _first = Pager("M8,2 L3,8 8,14 M14,2 L9,8 14,14", "第一页", () => ChangePage(0));
        _previous = Pager("M10,2 L4,8 10,14", "上一页", () => ChangePage(PageNumber - 1));
        _page = _resources.Text("1");
        _page.MinWidth = 34;
        _page.TextAlignment = TextAlignment.Center;
        _page.VerticalAlignment = VerticalAlignment.Center;
        _next = Pager("M5,2 L11,8 5,14", "下一页", () => ChangePage(PageNumber + 1));
        foreach (UIElement element in new UIElement[] { _first, _previous, _page, _next }) footer.Children.Add(element);
        results.Children.Add(footer);
        Scroll = new ScrollViewer { Name = "ResourceResultsScroll", Content = results, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        _viewportTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _viewportTimer.Tick += (_, _) => PrepareViewport();
        Scroll.ScrollChanged += (_, _) => ScheduleViewport();
        Scroll.SizeChanged += (_, _) => ScheduleViewport();
        Scroll.SizeChanged += (_, args) => { if (args.HeightChanged) CenterStatus(); };
        // Feather the material by itself. Rows, hit targets and the scrollbar stay on the sharp foreground layer.
        var background = new GlassSurface {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(), Margin = new Thickness(4),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false,
            Effect = new BlurEffect { Radius = 14, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
        };
        background.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        var resultsPlate = new Grid { Name = "ResourceResultsPlate", ClipToBounds = true };
        resultsPlate.Children.Add(background);
        resultsPlate.Children.Add(new ScrollEdgeFade(Scroll) { Margin = new Thickness(6, 4, 2, 4) });
        Grid.SetRow(resultsPlate, 2);
        _layout.Children.Add(resultsPlate);

        _query.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Search(); } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_filterOpen) return;
            e.Handled = true;
            ToggleFilters(false);
            _filter.Focus();
        };
        _version.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => {
            if (!_version.IsDropDownOpen) UpdateLoaderVisibility();
        }));
        _version.DropDownClosed += (_, _) => UpdateLoaderVisibility();
        SizeChanged += (_, _) => { if (_filterOpen) MeasureFilter(); };
        Loaded += (_, _) => { Refresh(); ScheduleViewport(); };
        Unloaded += (_, _) => _viewportTimer.Stop();
        InitializeChoices();
    }

    internal void Open(ResourceBrowserCategory? category, (McInstance Instance, string? Name)? target, bool loadData = true)
    {
        _active = true;
        _pending = null;
        if (!loadData) return;
        if (_search is { } search)
        {
            SelectPageSize();
            if (target is { } selected)
            {
                ResetFilters();
                search.DownloadTarget = selected.Instance;
                _version.Text = selected.Instance.Version.VanillaName ?? ResourceBrowserCatalog.AnyVersion;
                UpdateLoaderVisibility();
                var available = selected.Instance.Version.ModLoaders;
                var type = new[] { ModLoaders.Forge, ModLoaders.Fabric, ModLoaders.NeoForge }.FirstOrDefault(loader => available.HasFlag(loader));
                Select(_loader, type);
                _query.Text = selected.Name ?? "";
                Search();
            }
            else if (search.Snapshot.Request is null) Search();
            else search.Resize(LauncherResourceBrowser.PageSize);
        }
        else
        {
            if (_category != category?.Type)
            {
                _category = category?.Type;
                _favoritePage = 0;
                _filteredFavorites = null;
                UpdateFavoriteChoices();
                _favoriteFilter = ReadFavoriteFilter();
                Scroll.ScrollToTop();
            }
            ReloadFavorites();
        }
        Refresh();
        ScheduleViewport();
    }

    internal void Leave()
    {
        _active = false;
        _loading.IsActive = false;
        ToggleFilters(false);
        _viewportTimer.Stop();
        _swap?.Abort();
        _swap = null;
    }

    internal void Reload()
    {
        if (_search is { } search) search.Refresh();
        else ReloadFavorites();
    }

    internal void Refresh()
    {
        if (!_active || _disposed) return;
        _resources.ApplyPalette();
        if (_search is not { } search) { PresentFavorites(); return; }
        ResourceSearchSnapshot snapshot = search.Snapshot;
        SetWarning(snapshot.Warning);
        SetStatus(snapshot.Loading && snapshot.Results.Count < LauncherResourceBrowser.PageSize, snapshot.Error,
            snapshot.Request is not null && !snapshot.Loading && snapshot.Results.Count == 0 && snapshot.Error is null ? "没有符合条件的结果" : null,
            snapshot.Results.Count == 0 ? "正在获取列表" : "正在补充本页", search.Progress);
        UpdatePager(snapshot.Page, snapshot.CanPrevious, snapshot.CanNext, true);
        if (snapshot.Request is { } request)
            Present(new(request, snapshot.Page, snapshot.Results, request, request.GameVersion is null,
                request.ModLoaders == ModLoaders.None && request.Type is ResourceTypes.Mod or ResourceTypes.ModPack));
    }

    private int PageNumber => _search?.Snapshot.Page ?? _favoritePage;

    private void ChangePage(int number)
    {
        if (_search is { } search) search.Navigate(number);
        else if (number >= 0 && number != _favoritePage)
        {
            _favoritePage = number;
            PresentFavorites();
        }
    }

    private void Search(bool closeFilters = true)
    {
        if (!_active) return;
        if (closeFilters) ToggleFilters(false);
        if (_search is { } search)
        {
            var source = _category == ResourceTypes.Map ? ResourcePlatforms.CurseForge : Value(_source, ResourcePlatforms.Any);
            search.Start(new(_query.Text, GameVersion, Value(_tags, ""), _loaderField.Visibility == Visibility.Visible
                ? Value(_loader, ModLoaders.None) : ModLoaders.None, source), LauncherResourceBrowser.PageSize);
        }
        else
        {
            _favoritePage = 0;
            _favoriteFilter = ReadFavoriteFilter();
            _filteredFavorites = null;
            if (_favoriteError is not null) ReloadFavorites();
            else PresentFavorites();
        }
    }

    private async void ReloadFavorites()
    {
        if (_readingFavorites || _disposed) return;
        _readingFavorites = true;
        PresentFavorites();
        await Task.Run(ResourceFavorites.Store.Reload);
        _readingFavorites = false;
        UpdateFavorites(force: true);
    }

    private void UpdateFavorites(bool force = false)
    {
        if (!_active || _readingFavorites) return;
        var store = ResourceFavorites.Store;
        var list = store.Snapshot;
        Exception? error = store.LoadError ?? store.LastOperationError;
        if (!force && ReferenceEquals(list, _favorites) && ReferenceEquals(error, _favoriteError)) return;
        _favorites = list;
        _favoriteError = error;
        _filteredFavorites = null;
        UpdateFavoriteChoices();
        _favoriteFilter = ReadFavoriteFilter(_favoriteFilter.Name);
        PresentFavorites();
    }

    private void PresentFavorites()
    {
        if (!_active) return;
        var entries = _favorites ?? Array.Empty<ResourceFavorite>();
        if (_filteredFavorites is null)
        {
            _filteredFavorites = entries.Where(entry => _favoriteFilter.Matches(entry)).ToArray();
            _favoriteDisplayPage = -1;
        }
        int pages = Math.Max(1, (_filteredFavorites.Length + FavoritesPerPage - 1) / FavoritesPerPage);
        _favoritePage = Math.Clamp(_favoritePage, 0, pages - 1);
        if (_favoriteDisplayPage != _favoritePage)
        {
            _favoriteDisplayPage = _favoritePage;
            _favoriteProjects = _filteredFavorites.Skip(_favoritePage * FavoritesPerPage).Take(FavoritesPerPage)
                .Select(entry => new ResourceProject(entry.ProjectJson)).ToArray();
        }
        SetWarning(_favoriteError is null ? null : "无法读取或保存收藏，原文件已保留。点击搜索可重试。\n" + _favoriteError.Message);
        SetStatus(_readingFavorites, null, _filteredFavorites.Length > 0 || _readingFavorites ? null :
            _favoriteError is not null && entries.Count == 0 ? "收藏暂时不可用。修复收藏文件后点击搜索重试。" :
            entries.Count == 0 ? "还没有收藏。点击资源项目旁的收藏按钮即可添加。" : "没有符合当前筛选条件的收藏。",
            "正在读取收藏", null);
        UpdatePager(_favoritePage, !_readingFavorites && _favoritePage > 0, !_readingFavorites && _favoritePage + 1 < pages, pages > 1);
        Present(new(_filteredFavorites, _favoritePage, _favoriteProjects!,
            null, _favoriteFilter.GameVersion is null, _favoriteFilter.Loader == ModLoaders.None));
    }

    private ResourceFavoriteFilter ReadFavoriteFilter(string? name = null) => new() {
        Category = _category, Name = name ?? _query.Text, GameVersion = GameVersion, Tag = Value(_tags, ""),
        Source = Value(_source, ResourcePlatforms.Any), Loader = _loaderField.Visibility == Visibility.Visible ? Value(_loader, ModLoaders.None) : ModLoaders.None
    };

    private void Present(Rows next)
    {
        if (_pending is { } pending && next.SamePage(pending) && next.Projects.SequenceEqual(pending.Projects, ReferenceEqualityComparer.Instance)) return;
        _pending = next;
        if (!next.SamePage(_displayed) && _displayedRows.Count > 0 && CanAnimate && _rowMotion.Value > ExitThreshold)
        {
            _rows.IsHitTestVisible = false;
            if (_rowMotion.Target != 0) _rowMotion.Retarget(0);
            return;
        }
        CommitRows();
    }

    private void ApplyRowsPose(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        _rows.Opacity = p;
        _rowY.Y = MotionTokens.PageTravel * (1 - p);
        _rowDepth.ScaleX = _rowDepth.ScaleY = MotionTokens.RevealScale + (1 - MotionTokens.RevealScale) * p;
        if (p > ExitThreshold || _rowMotion.Target != 0 || _swap?.Status == DispatcherOperationStatus.Pending) return;
        _swap = Dispatcher.InvokeAsync(CommitRows, DispatcherPriority.Background);
    }

    private void CommitRows()
    {
        _swap = null;
        if (!_active || _pending is not { } next) return;
        bool replace = !next.SamePage(_displayed);
        bool changed = replace || !next.Projects.SequenceEqual(_displayed!.Projects, ReferenceEqualityComparer.Instance);
        if (!changed && _displayedRows.Count == next.Projects.Count) return;
        int common = 0;
        if (!replace)
            while (common < _displayedRows.Count && common < next.Projects.Count &&
                ReferenceEquals(_displayedRows[common].Project, next.Projects[common])) common++;
        for (int index = _displayedRows.Count - 1; index >= common; index--)
        {
            _rows.Children.RemoveAt(index);
            _displayedRows.RemoveAt(index);
        }
        _displayed = next;
        _rows.IsHitTestVisible = true;
        if (replace) Scroll.ScrollToTop();
        if (next.Projects.Count > 0 && (replace || _rowMotion.Target != 1))
        {
            if (replace && CanAnimate) _rowMotion.Snap(0);
            _rowMotion.Retarget(1, !CanAnimate);
        }
        for (int index = common; index < next.Projects.Count; index++)
        {
            var skeleton = new Path { Data = Placeholder, Stretch = Stretch.None, Opacity = .18,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            skeleton.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
            var slot = new ProjectSlot(next.Projects[index], skeleton);
            _displayedRows.Add(slot);
            _rows.Children.Add(slot);
        }
        SyncNotes();
        ScheduleViewport();
    }

    private void ScheduleViewport()
    {
        if (_active && _displayedRows.Count > 0 && IsVisible) _viewportTimer.Start();
    }

    private void PrepareViewport()
    {
        if (!_active || !IsVisible || !IsLoaded || Scroll.ViewportHeight <= 0 ||
            _displayed is not { } displayed || !displayed.SamePage(_pending))
        {
            _viewportTimer.Stop();
            return;
        }
        var viewport = Scroll.Template?.FindName("PART_ScrollContentPresenter", Scroll) as FrameworkElement ?? Scroll;
        double top = -_rows.TranslatePoint(new Point(), viewport).Y;
        double bottom = top + Scroll.ViewportHeight;
        double margin = Math.Min(208, Scroll.ViewportHeight / 2);
        var entering = new List<UIElement>();
        int prepared = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Prepare(double from, double to, bool visible)
        {
            double offset = 0;
            foreach (ProjectSlot entry in _displayedRows)
            {
                // Rows may carry a note under them, so walk the real heights instead of assuming one row height.
                double height = entry.ActualHeight > 0 ? entry.ActualHeight : GlassResourceProjectRow.RowHeight;
                double entryTop = offset;
                offset += height;
                if (entryTop + height <= from) continue;
                if (entryTop >= to) break;
                if (entry.Row is GlassResourceProjectRow existing)
                {
                    if (visible) existing.LoadIcon();
                    continue;
                }
                if (prepared >= 4 || prepared > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) return true;
                var project = entry.Project;
                var row = new GlassResourceProjectRow(project, _resources, displayed.ShowVersion, displayed.ShowLoader,
                    () => OpenDetail(project, displayed.Request));
                entry.Realize(row);
                if (visible) { row.LoadIcon(); entering.Add(entry); }
                prepared++;
            }
            return false;
        }
        if (!Prepare(top, bottom, true) && !Prepare(top - margin, bottom + margin, false)) _viewportTimer.Stop();
        if (CanAnimate && entering.Count > 0) StaggeredEntrance.Play(entering);
    }

    private void OnNotesChanged(object? sender, EventArgs args)
    {
        if (!_disposed) SyncNotes();
    }

    // Update the note without moving its row to a different visual parent during an entrance.
    private void SyncNotes()
    {
        if (_search is not null || _notesToggle is null) return;
        bool any = false;
        foreach (ProjectSlot entry in _displayedRows)
        {
            string? note = ResourceNotes.Get(entry.Project);
            entry.SetNote(note, _notesExpanded);
            any |= note is not null;
        }
        _notesSlot!.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesCaption();
        if (any) ScheduleViewport();
    }

    private void UpdateNotesCaption() => _notesToggle!.Content = _notesExpanded ? "收起备注" : "展开备注";

    private void OpenDetail(ResourceProject project, SearchRequest? request)
    {
        var page = _search is null ? FormMain.PageType.Favorites : FormMain.PageType.Download;
        object[] args = ItemRules.BuildDetailArguments(project, (int)page, request, null, _search?.DownloadTarget,
            (int)FormMain.PageType.Download, (int)FormMain.PageType.ResourceDetail, (int)FormMain.PageType.InstanceSetup);
        if (_search is null)
        {
            if (_category is { } type && project.Types.HasFlag(type)) args[4] = type;
            else if ((ResourceTypes)args[4] == ResourceTypes.ModOrDataPack) args[4] = ResourceTypes.Mod;
        }
        Logger.Info("按下资源工程列表项：" + project.TranslatedName);
        _window.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.ResourceDetail, Additional = args });
    }

    private void InitializeChoices()
    {
        _source.Items.Add(Item("全部", ResourcePlatforms.Any));
        _source.Items.Add(Item("CurseForge", ResourcePlatforms.CurseForge));
        _source.Items.Add(Item("Modrinth", ResourcePlatforms.Modrinth));
        foreach (var (label, loader) in new[] { ("任意 Mod 加载器", ModLoaders.None), ("Forge", ModLoaders.Forge),
            ("NeoForge", ModLoaders.NeoForge), ("Fabric", ModLoaders.Fabric), ("LiteLoader", ModLoaders.LiteLoader) })
            _loader.Items.Add(Item(label, loader));
        if (_search is null) UpdateFavoriteChoices();
        else
        {
            var versions = ResourceBrowserCatalog.GameVersions.ToList();
            if (ModDownload.AllDrops?.FirstOrDefault() > 250 && McVersion.DropToVersion(ModDownload.AllDrops[0]) is { } highest &&
                !versions.Contains(highest)) versions.Insert(1, highest);
            foreach (string version in versions) _version.Items.Add(Item(version));
            foreach (var tag in ResourceBrowserCatalog.Tags(_category!.Value))
            {
                var item = Item(tag.Label, tag.Value);
                item.IsEnabled = !tag.Heading;
                item.Padding = new Thickness(tag.Indent + 10, 5, 10, 5);
                _tags.Items.Add(item);
            }
        }
        ResetFilters();
    }

    private void UpdateFavoriteChoices()
    {
        string version = _version.Text;
        string tag = Value(_tags, "");
        var scope = (_favorites ?? Array.Empty<ResourceFavorite>()).Where(entry => entry.Matches(_category, null)).ToArray();
        _version.Items.Clear();
        _version.Items.Add(Item(ResourceBrowserCatalog.AnyVersion));
        foreach (var value in scope.SelectMany(entry => entry.KnownGameVersions).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value, Comparer<string>.Create(McUtils.CompareVersion)))
            _version.Items.Add(Item(value));
        _version.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(version)) _version.Text = version;
        _tags.Items.Clear();
        _tags.Items.Add(Item("全部", ""));
        foreach (var value in scope.SelectMany(entry => entry.Tags).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            _tags.Items.Add(Item(value, value));
        Select(_tags, tag);
        UpdateLoaderVisibility();
    }

    private void ResetFilters()
    {
        if (_search is not null) _search.DownloadTarget = null;
        _query.Text = "";
        _version.SelectedIndex = 0;
        _version.Text = ResourceBrowserCatalog.AnyVersion;
        _source.SelectedIndex = _category == ResourceTypes.Map && _search is not null ? 1 : 0;
        _source.IsEnabled = _category != ResourceTypes.Map || _search is null;
        _tags.SelectedIndex = 0;
        _loader.SelectedIndex = 0;
        UpdateLoaderVisibility();
    }

    private string? GameVersion
    {
        get
        {
            string value = _version.Text.Trim();
            if (value.Length == 0 || value == ResourceBrowserCatalog.AnyVersion) return null;
            return _search is null || value.Contains('.') || value.Contains('w') ? value : null;
        }
    }

    private void UpdateLoaderVisibility()
    {
        bool available = _category == ResourceTypes.Mod && GameVersion is { } value && (value.Contains('.') || value.Contains('w'));
        _loaderField.Visibility = VisibilityFor(available);
        if (!available) _loader.SelectedIndex = 0;
    }

    private void SelectPageSize()
    {
        _synchronizingChoices = true;
        try { Select(_pageSize, LauncherResourceBrowser.PageSize); }
        finally { _synchronizingChoices = false; }
    }

    private void SetStatus(bool loading, Exception? error, string? empty, string title, double? progress)
    {
        _loading.IsActive = loading;
        _loading.Visibility = VisibilityFor(loading);
        _failed.Visibility = VisibilityFor(error is not null);
        _empty.Visibility = VisibilityFor(empty is not null);
        _progress.Visibility = VisibilityFor(loading && progress.HasValue);
        if (progress is { } value && double.IsFinite(value)) _progress.Value = Math.Clamp(value, 0, 1);
        _status.Text = loading ? title : error?.Message ?? empty ?? "";
        _status.Visibility = VisibilityFor(_status.Text.Length > 0);
        _retry.Visibility = VisibilityFor(error is not null);
        if (_status.Parent is FrameworkElement host) host.Visibility = _status.Visibility;
        CenterStatus();
    }

    private void CenterStatus()
    {
        bool alone = _rows.Children.Count == 0 && _status.Visibility == Visibility.Visible;
        _statusHost.MinHeight = alone && Scroll.ActualHeight > 0 ? Math.Max(0, Scroll.ActualHeight - 24) : 0;
    }

    private void SetWarning(string? warning)
    {
        _warning.Text = warning ?? "";
        _warningPlate.Visibility = VisibilityFor(!string.IsNullOrWhiteSpace(warning));
    }

    private void UpdatePager(int index, bool canPrevious, bool canNext, bool visible)
    {
        _page.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _first.IsEnabled = canPrevious && index > 1;
        _previous.IsEnabled = canPrevious;
        _next.IsEnabled = canNext;
        _page.Visibility = _first.Visibility = _previous.Visibility = _next.Visibility = VisibilityFor(visible);
    }

    private GlassSurface Frost(UIElement? content, double radius = 10)
    {
        var surface = new GlassSurface { Child = content, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(.7),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = content is not null };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return surface;
    }

    private FrameworkElement CreateWarning(UIElement content)
    {
        var surface = Frost(content);
        surface.Padding = new Thickness(12, 9, 12, 9);
        surface.Margin = new Thickness(0, 4, 8, 12);
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationWarnEdge");
        return surface;
    }

    private ComboBox Choice(bool editable = false) => new() { Style = _resources.Style("GlassPresentationComboBox"),
        IsEditable = editable, IsTextSearchEnabled = false };
    private static ComboBoxItem Item(string label, object? tag = null) => new() { Content = label, Tag = tag };
    private static T Value<T>(ComboBox combo, T fallback) => combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;
    private static void Select<T>(ComboBox combo, T value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
        if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private StackPanel Field(string title, FrameworkElement editor)
    {
        AutomationProperties.SetName(editor, title);
        var label = _resources.Text(title);
        label.FontSize = 13;
        label.Margin = new Thickness(0, 0, 0, 6);
        return new StackPanel { Margin = new Thickness(0, 0, 12, 12), Children = { label, editor } };
    }

    private Button ActionButton(string text, RoutedEventHandler click, string style = "GlassPresentationButton")
    {
        var button = new Button { Content = text, Style = _resources.Style(style), Height = 32, MinHeight = 32, Padding = new Thickness(14, 0, 14, 0) };
        button.Click += click;
        return button;
    }

    private Button Pager(string geometry, string label, Action navigate)
    {
        var icon = new Path { Data = Geometry.Parse(geometry), Height = 14, Width = 14, Stretch = Stretch.Uniform,
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        icon.SetResourceReference(Shape.StrokeProperty, "GlassPresentationText");
        var button = new Button { Content = icon, ToolTip = label, Width = 38, Height = 34, Style = _resources.Style("GlassPresentationRowButton") };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => navigate();
        return button;
    }

    private void ToggleFilters(bool open)
    {
        _filterOpen = open;
        if (open) MeasureFilter();
        _filterMotion.Retarget(open ? 1 : 0, !CanAnimate);
    }

    private void MeasureFilter()
    {
        _filterContent.Measure(new Size(Math.Max(160, ActualWidth - 2), double.PositiveInfinity));
        double available = ActualHeight > 0 ? Math.Max(72, ActualHeight - 156) : 240;
        _filterHeight = Math.Min(_filterContent.DesiredSize.Height + 2, available);
        ApplyFilterPose(_filterMotion.Value);
    }

    private void ApplyFilterPose(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        _filterPlate.Visibility = VisibilityFor(_filterOpen || p > .001);
        _filterPlate.Height = p * _filterHeight;
        _filterPlate.Opacity = p;
        _filterPlate.Margin = new Thickness(0, 0, 0, 18 * p);
        _filterPlate.IsHitTestVisible = _filterOpen;
        _filterScroll.VerticalScrollBarVisibility = _filterOpen && p > .999 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private static Visibility VisibilityFor(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        Leave();
        _disposed = true;
        _search?.Dispose();
        if (_notesToggle is not null) ResourceNotes.Changed -= OnNotesChanged;
        _rowMotion.Dispose();
        _filterMotion.Dispose();
    }
}
