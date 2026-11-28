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
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using McInstance = PCL.ModMinecraft.McInstance;
using CardType = PCL.ModMinecraft.McInstanceCardType;
using SelectionHost = PCLCS.InstanceSelectionPageHost<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstanceCardType>;
using Selection = PCLCS.InstanceSelectionPageController<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState,
    PCL.ModMinecraft.McInstanceCardType, PCL.InstanceCloneGroupNode, PCL.InstanceCloneGroupSet>;
using CloneSelection = PCLCS.InstanceCloneSelectionPresenter<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstanceCardType,
    PCL.InstanceCloneGroupNode, PCL.InstanceCloneGroupSet>;
using Path = System.Windows.Shapes.Path;

namespace PCL;

internal sealed class GlassInstanceSelectionPage : ContentControl, IDisposable
{
    private readonly FormMain _window;
    private readonly GlassPageResources _resources;
    private readonly Selection _controller;
    private readonly StackPanel _groups = new(), _empty = new(), _status = new();
    private readonly TextBlock _emptyTitle, _emptyText, _statusText;
    private readonly BlockCurveLoader _loader = new() { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly GlassStatusGlyph _failed = new(GlassStatusGlyph.Kind.Failed) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _download, _retry;
    private readonly ModLoader.LoaderBase _instanceLoader = ModMinecraft.McInstanceListLoader;
    private DispatcherOperation? _render;
    private bool _active, _disposed, _loadData;
    internal bool ShowHidden { get; set; }
    internal ScrollViewer Scroll { get; }

    internal GlassInstanceSelectionPage(FormMain window)
    {
        _window = window;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        var root = new Grid();
        _groups.Margin = new Thickness(0, 12, 0, 10);
        Scroll = new ScrollViewer { Content = _groups, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        root.Children.Add(new ScrollEdgeFade(Scroll));
        _emptyTitle = _resources.Text("");
        _emptyTitle.FontWeight = FontWeights.SemiBold;
        _emptyTitle.FontSize = 18;
        _emptyText = _resources.Note("", 14);
        _emptyText.Margin = new Thickness(0, 8, 0, 12);
        _emptyText.MaxWidth = 480;
        _download = Button("下载游戏", () => window.PageChange(FormMain.PageType.Download), "GlassPresentationAccentButton");
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyText);
        _empty.Children.Add(_download);
        root.Children.Add(GlassPageResources.Plate(_empty, new Thickness(0, 8, 0, 0)));
        ((FrameworkElement)_empty.Parent).VerticalAlignment = VerticalAlignment.Top;

        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _statusText = _resources.Note("", 14);
        _statusText.Margin = new Thickness(0, 10, 0, 10);
        _statusText.TextAlignment = TextAlignment.Center;
        _statusText.MaxWidth = 450;
        _retry = Button("重试", () => Reload(force: true));
        _loader.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        foreach (var element in new UIElement[] { _loader, _failed, _statusText, _retry }) _status.Children.Add(element);
        root.Children.Add(_status);
        Content = root;

        var host = CreateHost();
        var cloneHost = new InstanceCloneSelectionHost<McInstance, CardType, InstanceCloneGroupNode> {
            HiddenCategory = CardType.Hidden, StarCategory = CardType.Star, SwappedHeight = 6,
            IsCollapsedCategory = card => card is CardType.Rubbish or CardType.Error or CardType.Fool,
            IsLocked = instance => ModMinecraft.McInstanceLock.BlocksNavigation(instance),
            AllowsInstance = instance => ModModpackMode.AllowsInstance(instance.PathVersion),
            VersionPath = instance => instance.PathVersion, Name = instance => instance.Name, Info = instance => instance.Info,
            SelectedPath = () => ModMinecraft.McInstanceSelected?.PathVersion,
            CreateRow = (instance, initial) => {
                var row = new InstanceRow(instance, _resources);
                initial(row, EventArgs.Empty);
                return row;
            },
            InitializeRow = (item, _) => Selection.RowContent((FrameworkElement)item, host),
            SetRowInfo = (item, info) => ((InstanceRow)item).Info.Text = info,
            CreateCard = (title, _) => new ChoiceCard(title, _resources),
            AdaptCard = item => (ChoiceCard)item
        };
        _controller = new Selection(host, new CloneSelection(InstanceCloneGroups.Service, cloneHost));
        _instanceLoader.OnStateChangedUi += OnLoaderChanged;
    }

    internal void Open(bool loadData)
    {
        _active = true;
        _loadData = loadData;
        _resources.ApplyPalette();
        if (loadData) _controller.Loaded();
        Regenerate();
    }

    internal void Reload(bool force)
    {
        if (!_active || !_loadData) return;
        LauncherGameFolders.RefreshInstances(force);
        Regenerate();
    }

    internal void Leave()
    {
        _active = false;
        _loader.IsActive = false;
        _render?.Abort();
        _render = null;
    }

    internal void RefreshPalette() => _resources.ApplyPalette();

    private void OnLoaderChanged(ModLoader.LoaderBase _, ModBase.LoadState state, ModBase.LoadState former)
    {
        if (_instanceLoader.State == state) Regenerate();
    }

    private void Regenerate()
    {
        if (!_active || _disposed) return;
        bool busy = _loadData && _instanceLoader.State == ModBase.LoadState.Loading;
        bool error = _loadData && _instanceLoader.State == ModBase.LoadState.Failed;
        _loader.IsActive = busy;
        _loader.Visibility = Display(busy);
        _failed.Visibility = _retry.Visibility = Display(error);
        _statusText.Text = error ? _instanceLoader.Error?.Message ?? "加载实例列表失败" : "正在读取本地游戏实例";
        _status.Visibility = Display(busy || error);
        if (busy || error || !_loadData)
        {
            ((FrameworkElement)_empty.Parent).Visibility = Scroll.Visibility = Visibility.Collapsed;
            return;
        }
        if (_render?.Status == DispatcherOperationStatus.Pending) return;
        _render = Dispatcher.InvokeAsync(() => {
            _render = null;
            if (!_active || _disposed || _instanceLoader.State != ModBase.LoadState.Finished) return;
            _controller.Render();
        }, DispatcherPriority.Background);
    }

    private SelectionHost CreateHost() => new() {
        View = () => new InstanceSelectionPageView {
            Main = _groups, Back = Scroll, Empty = (FrameworkElement)_empty.Parent, Download = _download,
            EmptyTitle = _emptyTitle, EmptyContent = _emptyText, ScrollHome = Scroll.ScrollToTop, ForceResize = () => { }
        },
        ShowHidden = () => ShowHidden,
        ModpackEnabled = () => ModModpackMode.Enabled,
        HiddenDownload = () => Settings.Get<bool>("UiHiddenPageDownload"),
        HiddenForceShow = () => LauncherAppearanceSettings.HiddenForceShow,
        LoaderFailed = () => _instanceLoader.State == ModBase.LoadState.Failed,
        Shift = () => (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
        RefreshInstances = LauncherGameFolders.RefreshInstances,
        Instances = () => ModMinecraft.McInstanceList,
        SetInstances = instances => ModMinecraft.McInstanceList = instances,
        SelectedInstance = () => ModMinecraft.McInstanceSelected,
        SetSelectedInstance = instance => ModMinecraft.McInstanceSelected = instance,
        InstancePage = () => LauncherInstanceContext.Instance,
        SetInstancePage = instance => LauncherInstanceContext.Instance = instance,
        MinecraftFolder = () => ModMinecraft.McFolderSelected,
        NewInstance = path => new McInstance(path),
        Locked = ModMinecraft.McInstanceLock.IsLocked,
        BlocksNavigation = ModMinecraft.McInstanceLock.BlocksNavigation,
        AllowsInstance = ModModpackMode.AllowsInstance,
        ForceRefresh = () => ModMinecraft.McInstanceListForceRefresh = true,
        PageBack = _window.PageBack,
        OpenDownload = () => _window.PageChange(FormMain.PageType.Download),
        OpenInstanceFolder = PageInstanceOverall.OpenInstanceFolder,
        OpenInstanceSetup = instance => {
            LauncherInstanceContext.Instance = instance;
            _window.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceOverall);
        },
        Hint = LauncherGameFolders.Hint,
        Message = LauncherGameFolders.Ask,
        Button = IconButton,
        SetButtons = (item, buttons) => { foreach (var button in buttons) ((InstanceRow)item).Actions.Children.Add(button); },
        BindClick = (item, action) => ((InstanceRow)item).Select.Click += (_, e) => action(item, e),
        BindRightClick = (item, action) => item.MouseRightButtonUp += (_, _) => action(),
        WriteIni = (file, key, value) => ModBase.WriteIni(file, key, Conversions.ToString(value)),
        DeleteConfirmed = LauncherInstanceDeletion.Start,
        BeginManagedDelete = MmcDeleteUi.PromptDelete
    };

    private FrameworkElement IconButton(string kind, string label, double scale, Visibility visibility, Action invoke)
    {
        string geometry = kind switch {
            "like-fill" => ModBase.Logo.IconButtonLikeFill, "like-line" => ModBase.Logo.IconButtonLikeLine,
            "delete" => ModBase.Logo.IconButtonDelete, "setup" => ModBase.Logo.IconButtonSetup,
            "open" => ModBase.Logo.IconButtonOpen, _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var icon = new Path { Data = Geometry.Parse(geometry), Stretch = Stretch.Uniform, Width = 14 * scale, Height = 14 * scale };
        icon.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        var button = Button("", invoke, "GlassPresentationRowButton");
        button.Content = icon;
        button.ToolTip = label;
        button.Visibility = visibility;
        button.Width = 36;
        button.Height = 36;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Button Button(string text, Action invoke, string style = "GlassPresentationButton")
    {
        var button = new Button { Content = text, Style = _resources.Style(style), HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => invoke();
        return button;
    }

    private static Visibility Display(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Leave();
        _instanceLoader.OnStateChangedUi -= OnLoaderChanged;
    }

    private sealed class ChoiceCard : ContentControl, IInstanceCloneSelectionCard<InstanceCloneGroupNode>
    {
        private readonly Expander _expander;
        private Action? _build;
        public FrameworkElement Element => this;
        public bool IsSwapped { get => !_expander.IsExpanded; set { if (!value) BuildContent(); else _expander.IsExpanded = false; } }
        public ChoiceCard(string title, GlassPageResources resources)
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Focusable = false;
            var caption = resources.Text(title);
            caption.FontSize = 15;
            caption.FontWeight = FontWeights.SemiBold;
            caption.TextWrapping = TextWrapping.NoWrap;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _expander = new Expander { Header = caption, Style = resources.Style("GlassPresentationExpander") };
            _expander.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, _expander)) BuildContent(); };
            // A collapsed group is a single 40 px header row, like the other collapsible cards.
            var plate = GlassPageResources.Card(_expander, GlassPageLayout.CardRole.Expander);
            Content = plate;
        }
        public void SetContent(Panel content)
        {
            content.Margin = new Thickness();
            _expander.Content = content;
        }
        public void SetContentBuilder(IEnumerable<InstanceCloneGroupNode> items, Func<InstanceCloneGroupNode, FrameworkElement> builder)
        {
            _build = () => {
                if (_expander.Content is not Panel panel) return;
                foreach (var item in items) panel.Children.Add(builder(item));
            };
        }
        public void BuildContent()
        {
            Action? build = _build;
            _build = null;
            build?.Invoke();
            _expander.IsExpanded = true;
        }
    }

    private sealed class InstanceRow : Grid
    {
        private readonly StackPanel _labels;
        private readonly RectangleGeometry _labelClip = new();
        internal TextBlock Info { get; }
        internal Button Select { get; }
        internal StackPanel Actions { get; } = new() {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden
        };
        internal InstanceRow(McInstance instance, GlassPageResources resources)
        {
            Tag = instance;
            var description = new DockPanel();
            var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Stretch = Stretch.Uniform,
                Source = GlassShellRuntime.LoadInstanceIcon(instance.Logo, instance.PathVersion) };
            DockPanel.SetDock(icon, Dock.Left);
            description.Children.Add(icon);
            var title = resources.Text(instance.Name);
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            Info = resources.Note(instance.Info, 12);
            Info.Margin = new Thickness(0, 2, 0, 0);
            Info.TextWrapping = TextWrapping.NoWrap;
            Info.TextTrimming = TextTrimming.CharacterEllipsis;
            _labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, Info } };
            description.Children.Add(_labels);
            Select = new Button { Content = description, Height = 52, Style = resources.Style("GlassPresentationBandRow") };
            AutomationProperties.SetName(Select, instance.Name);
            Select.ToolTip = instance.PathVersion;
            Children.Add(Select);
            Children.Add(Actions);
            MouseEnter += (_, _) => ShowActions(true);
            MouseLeave += (_, _) => ShowActions(IsKeyboardFocusWithin);
            GotKeyboardFocus += (_, _) => ShowActions(true);
            LostKeyboardFocus += (_, args) => ShowActions(IsMouseOver || args.NewFocus is Visual target && IsAncestorOf(target));
            Actions.SizeChanged += (_, _) => ClipLabels();
            _labels.SizeChanged += (_, _) => ClipLabels();
        }

        private void ShowActions(bool show)
        {
            Actions.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            ClipLabels();
        }

        private void ClipLabels()
        {
            // The overlay clears long labels without reserving an idle action column or remeasuring the row.
            if (Actions.Visibility != Visibility.Visible) { _labels.Clip = null; return; }
            _labelClip.Rect = new Rect(0, 0, Math.Max(0, _labels.ActualWidth - Actions.ActualWidth), _labels.ActualHeight);
            _labels.Clip = _labelClip;
        }
    }
}
