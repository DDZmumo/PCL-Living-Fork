// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using MeloongCore;
using MeloongCore.Wpf;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>Connects a new presentation tree to existing page commands and application state.</summary>
internal sealed class GlassShellIntegration : IDisposable {
    private readonly FormMain _owner;
    private readonly Grid _root = new();
    private readonly Grid _contentLayer = new();
    private readonly Border _modalScrim = new() { Background = Brushes.Black, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Canvas _resizeLayer = new();
    private readonly FrameworkElement[] _resizeParts = new FrameworkElement[8];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, ImageSource> _wallpaperCache = new(StringComparer.OrdinalIgnoreCase);
    private GlassShellRuntime? _runtime;
    private UIElement? _legacyRoot;
    private ImageSource? _wallpaperSource;
    private long _nextWallpaperRefresh;
    private string _wallpaperOptions = "";
    private string _navigationSignature = "";
    private bool _attached, _disposed, _refreshing;
    private PageLaunchLeft? _accountSource;
    private FrameworkElement? _accountPortal;
    private GlassAccountWorkspace? _accountWorkspace;
    private readonly List<GlassPresentationSession> _presentations = new();
    private readonly List<GlassPresentationSession> _retiredPresentations = new();
    private GlassPresentationSession? _homePresentation;
    private GlassPresentationSession? _pagePresentation;
    private object? _projectedPage;
    private readonly List<GlassExtraButtonAdapter> _extraButtons = new();
    private GlassWindowRegionClip? _windowRegion;
    private GlassToastHost? _toasts;
    private StackPanel? _extraActions;
    private GlassWindowBackdrop? _backdrop;
    private GlassModalEffects? _modalEffects;
    private GlassPresentationSession? _modalPresentation;
    private FrameworkElement? _modalView;
    private object? _lastActiveModal;
    private (Rect Body, Rect Controls, bool HasControls)? _lastModalClipKey;
    private bool _refreshingModal;
    private bool _windowResizing;
    private bool _startupPreload;
    private double _lastOpacityMultiplier = double.NaN;
    private Color? _lastFallbackColor;
    private bool? _lastDarkTheme;
    private long _lastPresentationRefresh;
    private (int Radius, bool Video)? _appliedWallpaperBlur;
    private int _wallpaperOpacity = -1;
    internal const double LeftOverhangAllowance = GlassLauncherView.RailExpandDelta + GlassLauncherView.RailOvershootHeadroom;
    private bool _leftOverhangApplied;
    internal bool HasLeftExpansionOverhang => _leftOverhangApplied;
    internal GlassLauncherView View { get; } = new();

    internal GlassShellIntegration(FormMain owner) {
        _owner = owner;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += OnRefreshTick;
    }

    internal void Attach() {
        if (_attached) return;
        _attached = true;
        _legacyRoot = (UIElement)_owner.Content;
        _owner.Content = null;
        _legacyRoot.Visibility = Visibility.Hidden;
        _root.Children.Add(_legacyRoot);
        _contentLayer.Children.Add(View);
        _root.Children.Add(_contentLayer);
        Panel.SetZIndex(_modalScrim, 25); _root.Children.Add(_modalScrim);
        View.SetSurfaceMaskHost(_root);
        ModernTooltipService.SetUseModernTooltip(_root, false);
        _owner.Content = _root;
        _windowRegion = GlassWindowRegionClip.Attach(_owner);
        GlassMaterial.SetIsEnabled(_root, true);
        _pagePresentation = CreatePresentation(_owner.PanMainRight);
        _projectedPage = DisplayedPage;
        View.SetPageContent(_pagePresentation.View);
        // Hints keep the original queue/timing; the shell mirrors them as toast cards in the body's bottom-right corner.
        _toasts = new GlassToastHost(_owner.PanHint);
        Panel.SetZIndex(_toasts, 20); _contentLayer.Children.Add(_toasts);
        MoveOverlay(_owner.PanMsg);
        if (_owner.BtnExtraDownload.Parent is FrameworkElement extras) {
            var actions = _extraActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 52, 38) };
            foreach (MyExtraButton source in new[] { _owner.BtnExtraBack, _owner.BtnExtraDownload,
                _owner.BtnExtraApril, _owner.BtnExtraShutdown, _owner.BtnExtraMusic }) {
                var adapter = new GlassExtraButtonAdapter(source, !ModTheme.IsDarkTheme);
                // The task button's ring shows the overall progress of all visible tasks (same as 任务管理's 总进度).
                if (ReferenceEquals(source, _owner.BtnExtraDownload))
                    adapter.TotalProgress = () => {
                        var tasks = ModLoader.LoaderTaskbar.ToList().Where(task => task.Show).ToList();
                        return tasks.Count == 0 ? null : Math.Clamp(tasks.Average(task => task.Progress), 0, 1);
                    };
                _extraButtons.Add(adapter); actions.Children.Add(adapter.View);
            }
            Panel.SetZIndex(actions, 12); _contentLayer.Children.Add(actions);
        }
        Panel.SetZIndex(_resizeLayer, 15); _root.Children.Add(_resizeLayer);
        for (int index = 0; index < _resizeParts.Length; index++) {
            FrameworkElement grip = _owner.GlassResizer(index);
            Detach(grip); grip.SetValue(Grid.RowProperty, 0);
            grip.Margin = new Thickness(); grip.HorizontalAlignment = HorizontalAlignment.Left; grip.VerticalAlignment = VerticalAlignment.Top;
            if (grip is System.Windows.Shapes.Shape shape) shape.Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            _resizeParts[index] = grip; _resizeLayer.Children.Add(grip);
        }
        _runtime = new GlassShellRuntime(_owner, View);
        _runtime.AccountRequested += OnAccountRequested;
        View.MinimizeRequested += OnMinimize;
        View.CloseRequested += OnClose;
        View.DragRequested += OnDrag;
        View.RegionsChanged += OnRegionsChanged;
        View.SplashFinished += (_, _) => OnStartupInterfaceShown();
        View.FarewellOverlayFade += OnFarewellOverlayFade;
        // From the splash's first frame the blur is shown through its shapes (each carries its own fade).
        View.SplashStarted += (_, _) => _backdropPresence?.Snap(1);
        _owner.StateChanged += OnWindowStateChanged;
        // Startup splash for the real product only (test hosts and design previews open straight into the interface).
        _startupPreload = System.Reflection.Assembly.GetEntryAssembly() == typeof(GlassShellIntegration).Assembly && _owner.GlassPreviewState is null;
        if (_startupPreload)
        {
            GlassStartupPreload.Begin();
            View.BeginSplash();
        }
        _owner.Loaded += OnLoaded;
        _owner.SizeChanged += OnSizeChanged;
        _owner.Closed += OnClosed;
        _owner.Activated += OnActivated;
        _owner.PanMsg.LayoutUpdated += OnModalLayoutUpdated;
        _owner.PanMainRight.LayoutUpdated += OnRightLayoutUpdated;
        // Loaded chooses desktop Acrylic or launcher wallpaper after the real HWND and layout exist.
        if (_owner.IsLoaded) OnLoaded(_owner, new RoutedEventArgs());
    }

    private void MoveOverlay(FrameworkElement element) {
        GlassPresentationSession session = CreatePresentation(element);
        FrameworkElement presentation = session.View;
        presentation.SetValue(Grid.RowProperty, 0);
        presentation.SetValue(Grid.RowSpanProperty, 1);
        Panel.SetZIndex(presentation, element == _owner.PanMsg ? 30 : 20);
        if (element == _owner.PanMsg) {
            _modalPresentation = session;
            _modalView = presentation;
            _root.Children.Add(presentation);
            // Blur and dim only the main body: the icon rail stays crisp, while input to it is still withheld.
            _modalEffects = new GlassModalEffects(_root, _contentLayer, View.FindName("BodyFrame") as FrameworkElement ?? _contentLayer, _modalScrim, presentation);
            presentation.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnModalAction), true);
        } else _contentLayer.Children.Add(presentation);
    }
    private GlassPresentationSession CreatePresentation(FrameworkElement source) {
        Func<MyCard, UIElement>? supplement = null;
        if (ReferenceEquals(source, _owner.PanMainRight)) {
            // Settings → Personalization hosts the shell wallpaper; one row per page session, never shared between trees.
            GlassWallpaperSettingRow? wallpaperRow = null;
            supplement = card => card.Name == "CardBackground" && card is { Parent: not null } && FindAncestor<PageSetupUI>(card) is not null
                ? (wallpaperRow ??= new GlassWallpaperSettingRow(ChooseWallpaper, ClearWallpaper, ReadWallpaperStatus)).Refresh()
                : null!;
        }
        GlassPresentationSession session = GlassPresentation.Create(source, supplement);
        _presentations.Add(session);
        return session;
    }
    internal static void EnsureContentPortal(UIElement content, ContentControl target) {
        if (ReferenceEquals(target.Content, content)) return;
        Detach(content);
        target.Content = content;
    }
    private static void Detach(UIElement element) {
        // ContentControls/Viewboxes can insert a template/container visual. Remove from the real logical owner first.
        if (element is FrameworkElement logicalElement) {
            switch (logicalElement.Parent) {
                case Panel logicalPanel: logicalPanel.Children.Remove(element); return;
                case Decorator logicalDecorator when logicalDecorator.Child == element: logicalDecorator.Child = null; return;
                case ContentControl logicalControl when logicalControl.Content == element: logicalControl.Content = null; return;
                case ContentPresenter logicalPresenter when logicalPresenter.Content == element: logicalPresenter.Content = null; return;
            }
        }
        DependencyObject? parent = VisualTreeHelper.GetParent(element);
        switch (parent) {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator when decorator.Child == element: decorator.Child = null; break;
            case ContentPresenter presenter when presenter.Content == element: presenter.Content = null; break;
            case ContentControl control when control.Content == element: control.Content = null; break;
            default:
                if (element is FrameworkElement { Parent: ContentControl logical } && logical.Content == element) logical.Content = null;
                break;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs args) {
        // Window-wide opacity would tint the native backing HWND and recreate layered-window artifacts.
        _owner.Opacity = 1;
        if (!_leftOverhangApplied && _owner.GlassPreviewState is null) {
            _leftOverhangApplied = true;
            bool wasSaveable = _owner.IsSizeSaveable;
            _owner.IsSizeSaveable = false;
            try {
                View.SetLeftExpansionAllowance(LeftOverhangAllowance);
                _owner.MinWidth += LeftOverhangAllowance;
                _owner.Width += LeftOverhangAllowance;
                _owner.Left -= LeftOverhangAllowance;
            } finally {
                _owner.IsSizeSaveable = wasSaveable;
            }
        }
        if (_backdrop is null && _owner.GlassPreviewState is null) {
            GlassMaterial.SetDesktopBackdropEnabled(_owner, true);
            _backdrop = GlassWindowBackdrop.Attach(_owner, new GlassBackdropOptions {
                UseDarkTheme = ModTheme.IsDarkTheme,
                ManageWindowRegion = false,
                DarkFallbackColor = Color.FromRgb(24, 24, 24),
                RegionsForClientSize = View.GetDesktopBackdropRegions
            });
            _backdrop.StatusChanged += OnBackdropChanged;
            // Keep the native host hidden before the first splash shape has been positioned.
            if (View.HoldsBackdrop) _backdrop.SetPresence(0);
            _backdropPresence = new PCLLF.Wpf.Motion.SpringMotion(View, _backdrop.Presence, value => _backdrop?.SetPresence(value),
                new PCLLF.Wpf.Motion.SpringParameters(.45, 1));
            OnBackdropChanged(_backdrop, EventArgs.Empty);
        }
        bool minimized = _owner.WindowState == WindowState.Minimized;
        GlassVideoWallpaper.SetPaused(minimized);
        Refresh();
        if (_startupPreload && !View.IsSplashing && !View.HoldsBackdrop) OnStartupInterfaceShown();
        if (!minimized) _refreshTimer.Start();
    }

    private void OnStartupInterfaceShown() {
        RevealBackdrop();
        if (!_startupPreload) return;
        // Render the unobstructed workspace before the background threads enqueue their dialogs.
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
            if (!_disposed) GlassStartupPreload.MarkInterfaceShown();
        }));
    }
    private void OnRefreshTick(object? sender, EventArgs args) => Refresh();
    internal void Refresh(bool forcePreferences = false) {
        if (_disposed || !_attached || !_owner.IsLoaded || _refreshing || (_windowResizing && !forcePreferences)) return;
        _refreshing = true;
        try {
            _owner.Opacity = 1;
            double opacityMultiplier = Math.Clamp(Settings.Get<int>("UiPanelOpacity") / 1000d, 0.4, 1);
            if (_lastOpacityMultiplier != opacityMultiplier) {
                _lastOpacityMultiplier = opacityMultiplier;
                GlassMaterial.SetOpacityMultiplier(_root, opacityMultiplier);
            }
            bool darkTheme = ModTheme.IsDarkTheme;
            Color fallbackColor = darkTheme ? Color.FromRgb(24, 24, 24) : Color.FromRgb(242, 245, 248);
            if (_lastFallbackColor != fallbackColor) {
                _lastFallbackColor = fallbackColor;
                GlassMaterial.SetFallbackColor(_root, fallbackColor);
            }
            if (_lastDarkTheme != darkTheme) {
                _lastDarkTheme = darkTheme;
                _backdrop?.SetDarkTheme(darkTheme);
            }
            // Every plain tooltip in the shell and its pages is a frosted pane matching the theme.
            if (_toolTipDark != darkTheme) {
                _toolTipDark = darkTheme;
                // Tooltips opened by ToolTipService have no logical parent, so they resolve implicit styles from the
                // application, not from this shell: replace the application-wide tooltip style while the shell is active.
                Style tipStyle = GlassCommandChrome.ToolTipStyle(!darkTheme);
                _root.Resources[typeof(ToolTip)] = tipStyle;
                if (Application.Current is { } app) {
                    if (!_savedAppToolTip) {
                        _savedAppToolTip = true;
                        _hadOriginalAppToolTip = app.Resources.Contains(typeof(ToolTip));
                        if (_hadOriginalAppToolTip) _originalAppToolTip = app.Resources[typeof(ToolTip)];
                    }
                    app.Resources[typeof(ToolTip)] = tipStyle;
                }
            }
            if (_owner.GlassPreviewState is not null) View.SetState(_owner.GlassPreviewState());
            else if (_runtime is not null) { _runtime.UseDarkText = !darkTheme; _runtime.Refresh(); }
            RefreshWallpaper(forcePreferences);
            RefreshNavigation();
            bool pageChanged = RefreshPagePresentation();
            RefreshHeaderVisibility();
            // Re-projecting every page is the heaviest part of a tick; defer it while a transition or smooth scroll plays so frames stay smooth.
            // While a new section is on its way, keep the outgoing page as displayed (the original page empties itself
            // during its exit), so the cross-fade starts from real content rather than a blank frame.
            // A sub-page swap is pending while PageRight already names the next page but PanMainRight still shows the old one.
            bool pagePending = View.IsAwaitingSection || !ReferenceEquals(_owner.PageRight, _owner.PanMainRight.Child);
            // Never freeze longer than a swap takes (pages whose container child differs by design keep refreshing).
            if (!pagePending) _pagePendingSince = 0;
            else if (_pagePendingSince == 0) _pagePendingSince = Environment.TickCount64;
            else if (Environment.TickCount64 - _pagePendingSince > 600) pagePending = false;
            long nowMs = Environment.TickCount64;
            bool motionBusy = View.IsTransitioning || PCLLF.Wpf.Motion.SmoothScroll.IsScrolling;
            if (!View.IsTransitioning && (forcePreferences || (!motionBusy && nowMs - _lastPresentationRefresh >= 500))) {
                _lastPresentationRefresh = nowMs;
                foreach (GlassPresentationSession presentation in _presentations) {
                    if (pageChanged && ReferenceEquals(presentation, _pagePresentation)) continue;
                    if (!(pagePending && ReferenceEquals(presentation, _pagePresentation))) presentation.Refresh();
                }
            }
            RefreshModalState();
            (_accountPortal as GlassAccountPanel)?.Refresh();
            if (View.IsAccountWorkspaceOpen) _accountWorkspace?.Refresh();
            foreach (GlassExtraButtonAdapter action in _extraButtons) { action.RefreshAppearance(!darkTheme, false); action.RefreshProgress(); }
            RefreshToasts();
            // While the loader shows, preload one step per refresh (dialog bodies, next pages). The splash may leave once
            // the instance list has loaded, the first page has been built and the preloads are done (or out of time).
            if (View.IsSplashLoading) GlassStartupPreload.Step();
            if (View.IsSplashing) {
                // Four stages, a quarter of the water each; a stage that is not needed counts as done at once.
                bool listed = ModMinecraft.McInstanceListLoader.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting);
                bool notes = GlassStartupPreload.NotesDone, update = GlassStartupPreload.UpdateDone;
                bool homepage = HomepageLoaded();
                double resources = _pagePresentation is null ? Math.Min(.5, GlassStartupPreload.ResourcesDone) : GlassStartupPreload.ResourcesDone;
                double progress = ((notes ? 1 : 0) + (update ? 1 : 0) + (homepage ? 1 : 0) + resources) / 4;
                // The instance list starts only after Java discovery; while everything else is ready the splash says so
                // and holds just short of full instead of sitting at a misleading figure.
                bool waitingForList = !listed && progress >= .999;
                View.SplashProgress = listed ? progress : Math.Min(progress, .95);
                View.SplashStageText = !notes ? "正在加载更新公告" : !update ? "正在检查更新" : !homepage ? "正在加载主页" :
                    waitingForList ? "正在加载版本列表" : "正在加载其他资源";
            }
            if (View.IsSplashing && _pagePresentation is not null && GlassStartupPreload.Settled &&
                ModMinecraft.McInstanceListLoader.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting))
                View.CompleteSplash();
            RefreshRegions();
        } catch (Exception error) {
            // Keep the shell alive during a page's transient replacement; original operation failures retain their own feedback.
            Logger.Warn(error, "刷新玻璃界面状态失败");
        } finally { _refreshing = false; }
    }
    // The page actually displayed: sub-page switches (e.g. 模组 → 资源包) assign PageRight first and place the page
    // into PanMainRight ~130 ms later, so following PageRight alone would animate the old content and then pop.
    private object? DisplayedPage => (object?)_owner.PanMainRight.Child ?? _owner.PageRight;
    private bool RefreshPagePresentation() {
        object? next = DisplayedPage;
        if (ReferenceEquals(_projectedPage, next)) return false;
        // When returning to Home, keep the outgoing page visual intact until Home's cross-fade finishes.
        if (_owner.GlassPageCode == (int)FormMain.PageType.Launch && _owner.GlassPreviewState is null && View.IsTransitioning)
            return false;
        GlassPresentationSession? previous = _pagePresentation;
        bool leavingHome = _projectedPage is PageLaunchRight;
        _projectedPage = next;
        // The home page's projection (its homepage cards) is kept while other pages show and reused on return, so the
        // cards are not rebuilt (and an online homepage not re-laid out) every time Home opens.
        if (next is PageLaunchRight && _homePresentation is { } home) {
            _homePresentation = null;
            _pagePresentation = home;
            _presentations.Add(home);
            home.Refresh();
        } else _pagePresentation = CreatePresentation(_owner.PanMainRight);
        View.SetPageContent(_pagePresentation.View);
        if (previous is not null) {
            _presentations.Remove(previous);
            if (leavingHome && _homePresentation is null) { _homePresentation = previous; return true; }
            _retiredPresentations.Add(previous);
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                if (_retiredPresentations.Remove(previous)) previous.Dispose();
            }));
        }
        return true;
    }
    private void RefreshNavigation() {
        // A detail page (resource/help detail) keeps the sidebar of the page it was opened from.
        FormMain.PageType? origin = _owner.GlassContextOrigin;
        MyPageLeft? left = origin is { } source ? FormMain.GlassContextLeft(source) : _owner.PageLeft;
        bool hasContext = origin is not null || _owner.GlassPageCode != (int)FormMain.PageType.Launch && _owner.PageLeft is { Width: not 0 };
        // The task manager's left page is a column of live metrics, not navigation: show them as a progress panel.
        bool taskManager = left is PageSpeedLeft && hasContext;
        if (taskManager) { _taskSidebar ??= new GlassTaskSidebar(); _taskSidebar.Refresh(); }
        View.SetSidebarCustom(taskManager ? _taskSidebar : null);
        IReadOnlyList<GlassSidebarItem> items = hasContext && left is not null && !taskManager ? GlassContextNavigation.Build(left) : Array.Empty<GlassSidebarItem>();
        if (origin is { } back) items = items.Select(item => ReturnFromDetail(item, back)).ToArray();
        string signature = (left?.GetType().FullName ?? "") + ":" + hasContext + ":" + origin + ":" +
            string.Join("|", items.Select(item => item.Label + ":" + item.IsSelected + ":" + item.IsEnabled + ":" + item.IsHeader + ":" + item.Actions.Count));
        if (signature == _navigationSignature) return;
        _navigationSignature = signature;
        View.SetSidebar(items);
    }
    // On a detail page the original rows may already be checked, so a click returns to that sub-page explicitly.
    private GlassSidebarItem ReturnFromDetail(GlassSidebarItem item, FormMain.PageType origin) {
        if (item.IsHeader || item.IsCommand || item.Source is null || FormMain.GlassContextSubPage(origin, item.Source) is not { } sub) return item;
        return new GlassSidebarItem {
            Label = item.Label, ToolTip = item.ToolTip, IconData = item.IconData, Image = item.Image, IsSelected = item.IsSelected,
            IsEnabled = item.IsEnabled, Actions = item.Actions, ContextRequested = item.ContextRequested, Source = item.Source,
            Invoked = () => _owner.PageChange(origin, sub),
        };
    }
    private void RefreshHeaderVisibility() {
        foreach (var pair in new[] {
            ("NavDownload", _owner.BtnTitleSelect1.Visibility), ("NavFavorites", _owner.BtnTitleSelect5.Visibility),
            ("NavSettings", _owner.BtnTitleSelect3.Visibility), ("NavOther", _owner.BtnTitleSelect4.Visibility)
        }) if (View.FindName(pair.Item1) is FrameworkElement button) {
            button.Visibility = pair.Item2;
            if (button.Parent is Grid grid && grid.RowDefinitions.Count > Grid.GetRow(button))
                grid.RowDefinitions[Grid.GetRow(button)].Height = new GridLength(pair.Item2 == Visibility.Visible ?
                    (View.TryFindResource("WorkspaceNavRowHeight") is double height ? height : 52) : 0);
        }
    }
    private void OnAccountRequested(object? sender, EventArgs args) {
        if (ModMain.FrmLaunchLeft is not PageLaunchLeft source) return;
        if (!ReferenceEquals(source, _accountSource)) {
            (_accountPortal as IDisposable)?.Dispose();
            _accountWorkspace?.Dispose();
            _accountSource = source;
            _accountPortal = CreateAccountPortal(source);
            // The avatar opens the full account page; the compact portal stays available as the popover content.
            _accountWorkspace = new GlassAccountWorkspace(source, View);
            View.SetAccountWorkspace(_accountWorkspace.Side, _accountWorkspace.Scene);
        }
        View.SetAccountContent(_accountPortal!);
        // Clicking the avatar while the account workspace is already open closes it; avoid rebuilding the deck mid-exit.
        if (View.IsAccountWorkspaceOpen) return;
        source.RefreshPage(true, false);
        _accountWorkspace?.Open();
    }
    internal static FrameworkElement CreateAccountPortal(PageLaunchLeft source) => new GlassAccountPanel(source);

    private void OnMinimize(object? sender, EventArgs args) => _owner.WindowState = WindowState.Minimized;
    // Fades every layer the shell put above the interface (dialog, scrim, toasts, floating buttons) while closing.
    private void OnFarewellOverlayFade(double fade) {
        double opacity = 1 - Math.Clamp(fade, 0, 1);
        Brush? mask = opacity >= .999 ? null : new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), 0, 0, 0));
        foreach (UIElement layer in _root.Children.OfType<UIElement>().Concat(_contentLayer.Children.OfType<UIElement>())) {
            if (ReferenceEquals(layer, View) || ReferenceEquals(layer, _contentLayer)) continue;
            if (mask is null) layer.ClearValue(UIElement.OpacityMaskProperty); else layer.OpacityMask = mask;
        }
    }

    // The close button plays the closing animation first; the program ends once it has finished.
    private void OnClose(object? sender, EventArgs args) {
        if (View.IsClosing) return;
        // With downloads running the program first asks whether to quit: ask over the intact interface.
        if (ModNet.HasDownloadingTask() || !View.BeginFarewell(() => {
                _owner.EndProgram(true);
                // Still usable afterwards: closing was refused (a file operation is running); bring the interface back.
                if (_owner.IsHitTestVisible) View.CancelFarewell();
            })) _owner.EndProgram(true);
    }
    private void OnDrag(object? sender, EventArgs args) {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { _owner.DragMove(); }
        catch (InvalidOperationException) { }
    }
    internal static string ReadSelectedWallpaper() => Settings.Get<int>("UiBackgroundMode") == 0
        ? Settings.Get<string>("UiGlassWallpaperPath") ?? ""
        : "";

    private void SetWallpaperSelection(string path) {
        Settings.Set("UiGlassWallpaperPath", path);
        if (path.Length > 0) Settings.Set("UiBackgroundMode", 0);
        PageSetupUI.BackgroundRefresh(false, false);
        RefreshWallpaper(true);
    }

    private void ClearWallpaper() => SetWallpaperSelection("");

    private GlassWallpaperStatus ReadWallpaperStatus() {
        string path = ReadSelectedWallpaper();
        if (string.IsNullOrWhiteSpace(path)) return new("未设置，使用默认背景", false);
        string file = Path.GetFileName(path);
        return new(File.Exists(path) ? file : "找不到文件 · " + file, true);
    }
    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject {
        for (DependencyObject? item = element; item is not null; item = LogicalTreeHelper.GetParent(item) ?? (item is Visual ? VisualTreeHelper.GetParent(item) : null))
            if (item is T match) return match;
        return null;
    }
    private void ChooseWallpaper() {
        var picker = new OpenFileDialog { Title = "选择启动器壁纸", Filter = "图片或视频|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.mp4;*.webm;*.wmv;*.mov;*.m4v;*.avi;*.mkv|图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|视频|*.mp4;*.webm;*.wmv;*.mov;*.m4v;*.avi;*.mkv|所有文件|*.*", CheckFileExists = true };
        if (picker.ShowDialog(_owner) != true) return;
        try {
            _wallpaperCache.Remove(picker.FileName);
            _ = ReadWallpaper(picker.FileName);
            SetWallpaperSelection(picker.FileName);
        } catch (Exception error) { Logger.Error(error, "无法读取选择的壁纸", LogBehavior.Toast); }
    }
    private ImageSource ReadWallpaper(string path) {
        if (GlassVideoWallpaper.IsVideo(path)) return GlassVideoWallpaper.Create(path, useSynchronizedFrames: true).ImageSource;
        if (_wallpaperCache.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? image = SetupPageRuntime.TryLoadBoundedBitmap(path, 1920) ?? InstanceResourceStore.LoadImage(path, 1920);
        if (image is null) throw new IOException("图片内容无法读取。");
        _wallpaperCache.Clear(); _wallpaperCache[path] = image;
        return image;
    }
    internal void RefreshWallpaper(bool force = false) {
        if (_disposed || !_attached || !_owner.IsLoaded) return;
        if (!force && Environment.TickCount64 < _nextWallpaperRefresh) return;
        _nextWallpaperRefresh = Environment.TickCount64 + 1000;
        string path = ReadSelectedWallpaper();
        bool exists = File.Exists(path);
        if (!exists || !GlassVideoWallpaper.IsVideo(path)) GlassVideoWallpaper.Stop();
        var wallpaper = View.FindName("WallpaperImage") as Image;
        var wallpaperViewport = View.FindName("WallpaperViewport") as FrameworkElement;
        int fit = Settings.Get<int>("UiBackgroundSuit");
        int opacity = Settings.Get<int>("UiBackgroundOpacity");
        int blur = Settings.Get<int>("UiBackgroundBlur");
        double width = Math.Max(1, wallpaperViewport?.ActualWidth > 0 ? wallpaperViewport.ActualWidth : wallpaper?.ActualWidth > 0 ? wallpaper.ActualWidth : View.ActualWidth);
        double height = Math.Max(1, wallpaperViewport?.ActualHeight > 0 ? wallpaperViewport.ActualHeight : wallpaper?.ActualHeight > 0 ? wallpaper.ActualHeight : View.ActualHeight);
        ImageSource? source = null;
        if (exists) {
            try { source = ReadWallpaper(path); }
            catch (Exception error) { Logger.Warn(error, "读取启动器壁纸失败，使用默认背景"); }
        }
        bool video = source is not null && ReferenceEquals(source, GlassVideoWallpaper.Source);
        if (video) GlassVideoWallpaper.SetFrostViewport(width, height, fit == 3, Math.Clamp(blur, 0, 40));
        // Video always fills (cover) directly: composing it into a drawing would freeze it on one frame.
        bool directStretch = source is not null && (video || fit is 2 or 3 || fit == 0 && !(source.Width < width / 2 && source.Height < height / 2));
        string options = directStretch
            ? FormattableString.Invariant($"{fit}:{blur}:direct")
            : FormattableString.Invariant($"{fit}:{blur}:{width:0.###}:{height:0.###}");
        bool sameSource = ReferenceEquals(source, _wallpaperSource);
        if (sameSource && options == _wallpaperOptions) {
            if (_wallpaperOpacity != opacity) {
                _wallpaperOpacity = opacity;
                if (wallpaper is not null) wallpaper.Opacity = Math.Clamp(opacity / 1000d, 0, 1);
            }
            return;
        }
        _wallpaperSource = source;
        _wallpaperOptions = options;
        _wallpaperOpacity = opacity;
        ImageSource? displayed = source is null ? null : directStretch ? source : ComposeWallpaper(source, new Size(width, height), fit);
        if (wallpaper is not null) {
            wallpaper.Stretch = directStretch ? (fit == 3 ? Stretch.Fill : Stretch.UniformToFill) : Stretch.Fill;
            wallpaper.Opacity = Math.Clamp(opacity / 1000d, 0, 1);
            int clampedBlur = Math.Clamp(blur, 0, 40);
            if (_appliedWallpaperBlur != (clampedBlur, video)) {
                _appliedWallpaperBlur = (clampedBlur, video);
                wallpaper.Effect = clampedBlur <= 0 ? null : new BlurEffect { Radius = clampedBlur, RenderingBias = RenderingBias.Performance };
                wallpaper.CacheMode = clampedBlur <= 0 ? null : new BitmapCache { EnableClearType = false, RenderAtScale = video ? 0.35 : clampedBlur >= 12 ? 0.5 : 1 };
            }
        }
        // Fit and blur edits take effect on the next frame without clearing the existing video material.
        if (!video || !sameSource) {
            ImageSource? material = video ? GlassVideoWallpaper.FrostedSource : displayed;
            View.SetWallpaperDisplay(displayed!, material);
            GlassMaterial.SetWallpaperSource(_root, material);
            SetVideoFrost(video && material is null ? wallpaper : null);
        }
        GlassMaterial.SetWallpaperHost(_root, wallpaper ?? (FrameworkElement)View);
        // Wallpaper owns only the main panel; the detached rail keeps desktop Acrylic.
        UpdateBackdropState();
        RefreshRegions();
    }
    private void OnBackdropChanged(object? sender, EventArgs args) {
        if (_disposed) return;
        UpdateBackdropState();
        if (_backdrop is not null) Logger.Info("背景材质：" + _backdrop.Status + "; " + _backdrop.DiagnosticMessage);
    }
    private void UpdateBackdropState() {
        bool active = _backdrop?.HasDesktopBackdrop == true;
        GlassMaterial.SetDesktopBackdropEnabled(_root, active);
        View.SetDesktopBackdropActive(active);
    }
    private static DrawingImage ComposeWallpaper(ImageSource source, Size viewport, int fit) {
        if (fit == 0) fit = source.Width < viewport.Width / 2 && source.Height < viewport.Height / 2 ? 4 : 2;
        var image = new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        if (fit == 1) image.Stretch = Stretch.None;
        else if (fit == 3) image.Stretch = Stretch.Fill;
        else if (fit == 4) {
            image.Stretch = Stretch.None; image.TileMode = TileMode.Tile;
            image.ViewportUnits = BrushMappingMode.Absolute;
            image.Viewport = new Rect(0, 0, Math.Max(1, source.Width), Math.Max(1, source.Height));
        }
        image.AlignmentX = fit is 5 or 7 or 9 ? AlignmentX.Left : fit is 6 or 8 or 10 ? AlignmentX.Right : AlignmentX.Center;
        image.AlignmentY = fit is 5 or 6 or 11 ? AlignmentY.Top : fit is 7 or 8 or 12 ? AlignmentY.Bottom : AlignmentY.Center;
        var group = new DrawingGroup();
        using (DrawingContext context = group.Open()) context.DrawRectangle(image, null, new Rect(viewport));
        var result = new DrawingImage(group);
        if (result.CanFreeze) result.Freeze();
        return result;
    }
    private void OnRegionsChanged(object? sender, EventArgs args) => RefreshRegions();

    // The homepage counts as loaded once its content is in place (or when no homepage is configured).
    private static bool HomepageLoaded() =>
        Settings.Get<int>("UiCustomType") == 0 || ModMain.FrmLaunchRight?.PanCustom.Children.Count > 0;

    private VideoWallpaperSampler? _videoFrost;
    private void SetVideoFrost(Image? source) {
        if (source is null) { _videoFrost?.Dispose(); _videoFrost = null; return; }
        _videoFrost ??= new VideoWallpaperSampler(_owner.Dispatcher, frame => {
            GlassMaterial.SetWallpaperSource(_root, frame);
            View.SetWallpaperMaterialSource(frame);
        });
        _videoFrost.SetHost(source);
        _videoFrost.SetPaused(_windowResizing || _owner.WindowState == WindowState.Minimized);
    }

    private PCLLF.Wpf.Motion.SpringMotion? _backdropPresence;
    private bool _revealQueued;

    // Minimizing hides the native blur at once; restoring brings the interface back first and fades the blur in after
    // it (Windows would otherwise show the separate backdrop window before WPF has redrawn the content).
    private void OnWindowStateChanged(object? sender, EventArgs args) {
        bool minimized = _owner.WindowState == WindowState.Minimized;
        GlassVideoWallpaper.SetPaused(minimized);
        _videoFrost?.SetPaused(minimized || _windowResizing);
        if (minimized) {
            _refreshTimer.Stop();
            _backdropPresence?.Snap(0);
            return;
        }
        if (_owner.IsLoaded && !_refreshTimer.IsEnabled) { Refresh(); _refreshTimer.Start(); }
        if (!View.HoldsBackdrop) RevealBackdrop();
    }

    // Waits for two rendered frames of the interface, then fades the native blur in.
    private void RevealBackdrop() {
        if (_backdrop is null || _backdropPresence is null || _revealQueued) return;
        _revealQueued = true;
        int frames = 0;
        EventHandler? onFrame = null;
        onFrame = (_, _) => {
            if (++frames < 2) return;
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
            _revealQueued = false;
            if (_disposed || View.HoldsBackdrop || _owner.WindowState == WindowState.Minimized) return;
            _backdropPresence.Retarget(1, !PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation);
        };
        System.Windows.Media.CompositionTarget.Rendering += onFrame;
    }
    private void RefreshRegions() {
        if (View.ActualWidth <= 0 || View.ActualHeight <= 0) return;
        var surfaces = View.GetSurfaceRegions();
        // Exclude the wallpaper panel from native sampling while retaining rail Acrylic.
        _backdrop?.SetRegions(View.GetDesktopBackdropRegions());
        // GDI regions have one-bit edges. Keep only the gap out of the native hit envelope;
        // the layered window's antialiased alpha mask owns its rounded outer edge.
        _windowRegion?.SetRegions(View.GetWindowHitRegions().Select(surface => surface with { CornerRadius = 0 }));
        if (surfaces.Count < 2) return;
        Rect rail = surfaces[0].Bounds;
        Rect body = surfaces[1].Bounds;
        Rect controls = surfaces.Count > 2 ? surfaces[2].Bounds : new Rect(body.Right, body.Top, 0, 0);
        UpdateResizeGrips(rail, body, controls);
    }
    private void UpdateResizeGrips(Rect rail, Rect body, Rect controls) {
        if (_resizeParts[0] is null) return;
        const double corner = 12, edge = 5, controlsCorner = 6;
        double rightEdgeTop = Math.Max(body.Top + corner, controls.Bottom);
        double topEdgeRight = controls.Width > 0 ? Math.Min(body.Right - corner, controls.Left - 4) : body.Right - corner;
        Rect[] bounds = {
            new(rail.Left + 36, body.Bottom - edge, Math.Max(0, body.Right - rail.Left - 72), edge),
            new(rail.Left, rail.Top + 36, edge, Math.Max(0, rail.Height - 72)),
            new(rail.Left, rail.Bottom - corner, corner, corner),
            new(rail.Left, rail.Top, corner, corner),
            new(body.Right - edge, rightEdgeTop, edge, Math.Max(0, body.Bottom - corner - rightEdgeTop)),
            new(body.Right - corner, body.Bottom - corner, corner, corner),
            controls.Width > 0
                ? new(controls.Right - controlsCorner, controls.Top, controlsCorner, controlsCorner)
                : new(body.Right - corner, body.Top, corner, corner),
            new(body.Left + corner, body.Top, Math.Max(0, topEdgeRight - (body.Left + corner)), edge)
        };
        for (int index = 0; index < bounds.Length; index++) {
            Rect box = bounds[index]; FrameworkElement grip = _resizeParts[index];
            Canvas.SetLeft(grip, box.Left); Canvas.SetTop(grip, box.Top); grip.Width = box.Width; grip.Height = box.Height;
        }
    }
    private void RefreshToasts() {
        if (_toasts is null) return;
        _toasts.SetTheme(ModTheme.IsDarkTheme);
        _toasts.Refresh();
        // Sit above the floating task/back-to-top buttons whenever they are shown.
        double raised = _extraActions is { IsVisible: true, ActualHeight: > 1 } actions ? actions.ActualHeight + actions.Margin.Bottom + 12 : 24;
        var margin = new Thickness(0, 0, 34, raised);
        if (_toasts.Margin != margin) _toasts.Margin = margin;
    }
    internal void SetMaterialDensity(double opacity) {
        GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(opacity, 0.4, 1));
    }
    internal void SetWindowResizing(bool resizing) {
        if (_disposed || _windowResizing == resizing) return;
        _windowResizing = resizing;
        _videoFrost?.SetPaused(resizing || _owner.WindowState == WindowState.Minimized);
        GlassMaterial.SetDeferWallpaperBlur(_root, resizing);
        if (!resizing) {
            _nextWallpaperRefresh = 0;
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (_disposed || _windowResizing) return;
                Refresh();
                _modalEffects?.RefreshSnapshot();
            }));
        }
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) {
        RefreshRegions();
        if (_windowResizing) return;
        _nextWallpaperRefresh = 0;
        _modalEffects?.RefreshSnapshot();
    }
    private void OnActivated(object? sender, EventArgs args) { RefreshRegions(); }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private void OnModalLayoutUpdated(object? sender, EventArgs args) => RefreshModalState();
    // React to the displayed page changing right away instead of waiting for the next 200 ms tick.
    private bool _pageRefreshQueued;
    private long _pagePendingSince;
    private GlassTaskSidebar? _taskSidebar;
    private bool? _toolTipDark;
    private bool _savedAppToolTip;
    private bool _hadOriginalAppToolTip;
    private object? _originalAppToolTip;
    private void OnRightLayoutUpdated(object? sender, EventArgs args) {
        if (_pageRefreshQueued || _disposed || ReferenceEquals(_projectedPage, DisplayedPage)) return;
        _pageRefreshQueued = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { _pageRefreshQueued = false; Refresh(); }));
    }
    private void OnModalAction(object sender, RoutedEventArgs args) => RefreshModalState();
    private void RefreshModalState() {
        if (_disposed || _refreshingModal || _modalEffects is null) return;
        if (_owner.PanMsg.Children.Count == 0 && !_modalEffects.HasActiveMessage) return;
        _refreshingModal = true;
        try {
            object? active = GlassModalState.FindActive(_owner.PanMsg);
            _resizeLayer.IsHitTestVisible = active is null;
            bool modalChanged = !ReferenceEquals(active, _lastActiveModal);
            _lastActiveModal = active;
            if (active is not null) {
                AlignModalToBody();
                if (modalChanged) _modalPresentation?.Refresh();
            } else {
                _lastModalClipKey = null;
            }
            _modalEffects.SetMessage(active);
        } finally { _refreshingModal = false; }
    }
    // Dialog and scrim occupy the main body only, so neither crosses the transparent gap beside the icon rail.
    private void AlignModalToBody() {
        if (View.FindName("BodyFrame") is not FrameworkElement body || !body.IsLoaded || _modalView is null) return;
        Rect bounds;
        try { bounds = body.TransformToAncestor(_root).TransformBounds(new Rect(body.RenderSize)); }
        catch (InvalidOperationException) { return; }
        var margin = new Thickness(bounds.Left, bounds.Top, Math.Max(0, _root.ActualWidth - bounds.Right), Math.Max(0, _root.ActualHeight - bounds.Bottom));
        if (_modalScrim.Margin != margin) _modalScrim.Margin = margin;
        if (_modalView.Margin != margin) _modalView.Margin = margin;
        if (_modalScrim.CornerRadius != new CornerRadius(12)) _modalScrim.CornerRadius = new CornerRadius(12);
        if (View.FindName("WindowControlsSurface") is FrameworkElement controls && controls.ActualWidth > 0 && controls.ActualHeight > 0) {
            Rect localBody = new(0, 0, Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));
            Rect controlsInBody = controls.TransformToAncestor(_root).TransformBounds(new Rect(controls.RenderSize));
            controlsInBody.Offset(-bounds.Left, -bounds.Top);
            if (_lastModalClipKey is { HasControls: true } cached && SameRect(cached.Body, localBody) && SameRect(cached.Controls, controlsInBody)) return;
            _lastModalClipKey = (localBody, controlsInBody, true);
            var cut = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(localBody, 12, 12),
                new RectangleGeometry(controlsInBody, 12, 12));
            cut.Freeze();
            _modalScrim.Clip = cut;
        } else {
            if (_lastModalClipKey is { HasControls: false }) return;
            _lastModalClipKey = (Rect.Empty, Rect.Empty, false);
            _modalScrim.Clip = null;
        }
    }
    private static bool SameRect(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.05 && Math.Abs(a.Y - b.Y) < 0.05 &&
        Math.Abs(a.Width - b.Width) < 0.05 && Math.Abs(a.Height - b.Height) < 0.05;
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _owner.PanMsg.LayoutUpdated -= OnModalLayoutUpdated;
        _owner.PanMainRight.LayoutUpdated -= OnRightLayoutUpdated;
        _modalView?.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnModalAction));
        _modalEffects?.Dispose();
        if (_backdrop is not null) { _backdrop.StatusChanged -= OnBackdropChanged; _backdrop.Dispose(); }
        _windowRegion?.Dispose();
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnRefreshTick;
        if (_savedAppToolTip && Application.Current is { } app) {
            if (_hadOriginalAppToolTip) app.Resources[typeof(ToolTip)] = _originalAppToolTip;
            else app.Resources.Remove(typeof(ToolTip));
        }
        foreach (GlassPresentationSession presentation in _presentations) presentation.Dispose();
        _presentations.Clear();
        foreach (GlassPresentationSession retired in _retiredPresentations) retired.Dispose();
        _homePresentation?.Dispose(); _homePresentation = null;
        _retiredPresentations.Clear();
        (_accountPortal as IDisposable)?.Dispose();
        _accountWorkspace?.Dispose();
        foreach (GlassExtraButtonAdapter action in _extraButtons) action.Dispose();
        _extraButtons.Clear();
        _toasts?.Dispose();
        _runtime?.Dispose();
        if (_runtime is not null) _runtime.AccountRequested -= OnAccountRequested;
        View.MinimizeRequested -= OnMinimize; View.CloseRequested -= OnClose;
        View.DragRequested -= OnDrag;
        View.RegionsChanged -= OnRegionsChanged;
        _owner.StateChanged -= OnWindowStateChanged;
        _videoFrost?.Dispose();
        GlassVideoWallpaper.Stop();
        if (_startupPreload) GlassStartupPreload.Cancel();
        _backdropPresence?.Dispose();
        View.Dispose();
        _owner.Loaded -= OnLoaded; _owner.SizeChanged -= OnSizeChanged;
        _owner.Closed -= OnClosed; _owner.Activated -= OnActivated;
    }
}
