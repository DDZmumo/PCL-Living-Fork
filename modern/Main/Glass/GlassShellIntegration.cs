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
using PCLLF.Presentation.Controls;
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
    private MyPageLeft? _sidebarOwner;
    private bool _attached, _disposed, _refreshing;
    private PageLaunchLeft? _accountSource;
    private FrameworkElement? _accountPortal;
    private GlassAccountWorkspace? _accountWorkspace;
    private readonly List<GlassPresentationSession> _presentations = new();
    private readonly List<RetiredPage> _retiredPresentations = new();
    private GlassPresentationSession? _homePresentation;
    private GlassPresentationSession? _pagePresentation;
    private MyPageRightView? _sourceRightPage;
    private UIElement? _projectedPage;
    private InstancePageScope? _projectedInstance;
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
    private bool? _lastDarkTheme;
    private long _lastPresentationRefresh;
    private (int Radius, bool Video)? _appliedWallpaperBlur;
    private int _wallpaperOpacity = -1;
    internal const double LeftOverhangAllowance = GlassLauncherView.RailExpandDelta + GlassLauncherView.RailOvershootHeadroom;
    private Rect _lastWorkArea = Rect.Empty;
    private Size _preferredMinimum;
    private DispatcherOperation? _workAreaCheck;
    private bool CanRenderWindow => _owner.IsVisible && _owner.WindowState != WindowState.Minimized;
    internal bool HasLeftExpansionOverhang => View.LeftExpansionAllowance > 0;
    internal GlassLauncherView View { get; } = new();

    internal GlassShellIntegration(FormMain owner) {
        _owner = owner;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += OnRefreshTick;
        // Background music starts only once the startup splash is over (released in the refresh below).
        ModMusic.MusicHoldStart();
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
        ObserveRightPage(DisplayedPage);
        View.SetPageContent(_pagePresentation.View);
        // Hints keep the original queue/timing; the shell mirrors them as toast cards in the body's bottom-right corner.
        _toasts = new GlassToastHost(_owner.PanHint);
        _toasts.SizeChanged += OnToastObstacleSizeChanged;
        Panel.SetZIndex(_toasts, 20); _contentLayer.Children.Add(_toasts);
        MoveOverlay(_owner.PanMsg);
        if (_owner.BtnExtraDownload.Parent is FrameworkElement extras) {
            var actions = _extraActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 52, 38) };
            actions.SizeChanged += OnToastObstacleSizeChanged;
            // Background music: the original round button returns only while the music player is minimised.
            foreach (MyExtraButton source in new[] { _owner.BtnExtraBack, _owner.BtnExtraDownload,
                _owner.BtnExtraApril, _owner.BtnExtraShutdown, _owner.BtnExtraMusic }) {
                var adapter = new GlassExtraButtonAdapter(source, !ModTheme.IsDarkTheme);
                adapter.SetBackdrop(View.BodyFrame);
                // The task button's ring shows the overall progress of all visible tasks (same as 任务管理's 总进度).
                if (ReferenceEquals(source, _owner.BtnExtraDownload))
                    adapter.TotalProgress = () => {
                        var tasks = ModLoader.LoaderTaskbar.ToList().Where(task => task.Show).ToList();
                        return tasks.Count == 0 ? null : Math.Clamp(tasks.Average(task => task.Progress), 0, 1);
                    };
                if (ReferenceEquals(source, _owner.BtnExtraMusic)) _musicButton = adapter;
                _extraButtons.Add(adapter); actions.Children.Add(adapter.View);
            }
            Panel.SetZIndex(actions, 12); _contentLayer.Children.Add(actions);
            // The bar docks in the window's bottom-right corner (the window controls' twin); its track list card opens
            // right above it, matching its width.
            // Spectrum in the body's backdrop: over the wallpaper, under the panes (their frosted glass blurs it).
            if (View.FindName("BodyBackdropLayer") is Panel backdropLayer) { _visualizer = new GlassAudioVisualizer(); backdropLayer.Children.Add(_visualizer); }
            _music = new GlassMusicPlayer(View);
            View.SetMusicDock(_music.Bar);
            if (_musicButton is { } round) {
                round.ShowGate = () => _music.IsMinimised && _music.IsReady;
                round.ClickOverride = () => _music.SetMinimised(false);
                _music.MinimisedChanged += (_, _) => round.RefreshVisibility();
            }
            // The floating buttons glide clear of the dock and its card whenever either comes or goes.
            _music.MinimisedChanged += (_, _) => QueueExtraLift();
            _music.CardChanged += (_, _) => QueueExtraLift();
            // A click anywhere outside the player closes its one-shot parts (track list; the dock away from home).
            _owner.PreviewMouseDown += OnMusicOutsideClick;
        }
        Panel.SetZIndex(_resizeLayer, 15); _root.Children.Add(_resizeLayer);
        for (int index = 0; index < _resizeParts.Length; index++) {
            FrameworkElement grip = _owner.GlassResizer(index);
            Detach(grip); grip.SetValue(Grid.RowProperty, 0);
            grip.Margin = new Thickness(); grip.HorizontalAlignment = HorizontalAlignment.Left; grip.VerticalAlignment = VerticalAlignment.Top;
            if (grip is System.Windows.Shapes.Shape shape) shape.Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            _resizeParts[index] = grip; _resizeLayer.Children.Add(grip);
        }
        _runtime = new GlassShellRuntime(_owner, View, QueuePageRefresh);
        _runtime.AccountRequested += OnAccountRequested;
        View.ThemeToggleRequested += OnThemeToggle;
        View.MinimizeRequested += OnMinimize;
        View.CloseRequested += OnClose;
        View.DragRequested += OnDrag;
        View.RegionsChanged += OnRegionsChanged;
        View.PageContentReleased += OnPageContentReleased;
        View.SplashFinished += (_, _) => OnStartupInterfaceShown();
        View.FarewellOverlayFade += OnFarewellOverlayFade;
        // From the splash's first frame the blur is shown through its shapes (each carries its own fade).
        View.SplashStarted += (_, _) => _backdropPresence?.Snap(1);
        _preferredMinimum = new Size(_owner.MinWidth, _owner.MinHeight);
        _owner.StateChanged += OnWindowStateChanged;
        _owner.IsVisibleChanged += OnWindowVisibilityChanged;
        _owner.LocationChanged += OnWindowLocationChanged;
        _owner.DpiChanged += OnWindowDpiChanged;
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
            _modalEffects = new GlassModalEffects(_root, _contentLayer, View.FindName("BodyFrame") as FrameworkElement ?? _contentLayer,
                _modalScrim, presentation, View.FindName("ModalVideoFrost") as Border, View.FindName("CrashBodySnapshot") as Image);
            // The music dock lies outside the body, so it follows the dialog veil explicitly.
            _modalEffects.Companion = progress => { View.SetMusicDockVeil(progress); if (progress > .01) _music?.CloseCard(); };
            presentation.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnModalAction), true);
        } else _contentLayer.Children.Add(presentation);
    }
    private GlassPresentationSession CreatePresentation(FrameworkElement source) {
        Func<MyCard, UIElement>? supplement = null;
        bool rightPage = ReferenceEquals(source, _owner.PanMainRight);
        if (rightPage) {
            // Settings → Personalization hosts the shell wallpaper; one row per page session, never shared between trees.
            GlassWallpaperSettingRow? wallpaperRow = null;
            supplement = card => card.Name == "CardBackground" && card is { Parent: not null } && FindAncestor<PageSetupUI>(card) is not null
                ? (wallpaperRow ??= new GlassWallpaperSettingRow(ChooseWallpaper, ClearWallpaper, ReadWallpaperStatus)).Refresh()
                : null!;
        }
        GlassPresentationSession session = GlassPresentation.Create(source, supplement, trackPageAppearance: rightPage);
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
        UpdateWorkArea();
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
        UpdateVideoWallpaperActivity();
        Refresh();
        if (_startupPreload && !View.IsSplashing && !View.HoldsBackdrop) OnStartupInterfaceShown();
        if (CanRenderWindow) _refreshTimer.Start();
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
        if (_disposed || !_attached || !_owner.IsLoaded || _refreshing || ((!CanRenderWindow || _windowResizing) && !forcePreferences)) return;
        _refreshing = true;
        try {
            _owner.Opacity = 1;
            double opacityMultiplier = Math.Clamp(Settings.Get<int>("UiPanelOpacity") / 1000d, 0.4, 1);
            if (_lastOpacityMultiplier != opacityMultiplier) {
                _lastOpacityMultiplier = opacityMultiplier;
                GlassMaterial.SetOpacityMultiplier(_root, opacityMultiplier);
            }
            bool darkTheme = ModTheme.IsDarkTheme;
            bool paletteChanged = _lastDarkTheme.HasValue && _lastDarkTheme != darkTheme;
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
            bool crashReportOpen = GlassModalEffects.IsCrashReport(_lastActiveModal);
            GlassVideoWallpaper.SetNavigationActive(pagePending || View.IsTransitioning);
            if (paletteChanged)
                foreach (GlassPresentationSession presentation in _presentations) presentation.RefreshPalette();
            if (!crashReportOpen && !View.IsTransitioning && (forcePreferences || (!motionBusy && nowMs - _lastPresentationRefresh >= 500))) {
                _lastPresentationRefresh = nowMs;
                foreach (GlassPresentationSession presentation in _presentations) {
                    if (pageChanged && ReferenceEquals(presentation, _pagePresentation)) continue;
                    if (!(pagePending && ReferenceEquals(presentation, _pagePresentation))) presentation.Refresh();
                }
            }
            RefreshModalState();
            if (_accountPortal is GlassAccountPanel { IsVisible: true } portal) portal.Refresh();
            if (View.IsAccountWorkspaceOpen) _accountWorkspace?.Refresh();
            foreach (GlassExtraButtonAdapter action in _extraButtons) {
                action.SetBackdropSuspended(pageChanged || pagePending || motionBusy);
                action.RefreshAppearance(!darkTheme, false);
                action.RefreshProgress();
            }
            if (_music is not null) {
                // The player arrives once loading is over (never over the splash); so does the music.
                bool ready = !View.IsSplashing && !View.HoldsBackdrop;
                if (ready && !_music.Ready) ModMusic.MusicReleaseStart();
                _music.Ready = ready;
                _music.OnHome = _owner.GlassPageCode == (int)FormMain.PageType.Launch;
                _music.Refresh(updateDetails: !crashReportOpen && !pageChanged && !motionBusy);
                RefreshAudioVisualization();
                if (!motionBusy) UpdateExtraLift();
            }
            if (!motionBusy) UpdatePageGutter();
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
    private UIElement? DisplayedPage => _owner.PanMainRight.Child;

    private const int InstancePageCacheLimit = 3;

    private readonly record struct InstancePageScope(ModMinecraft.McInstance Instance, string Path) {
        internal bool IsCurrent => ReferenceEquals(Instance, PageInstanceLeft.Instance) &&
            string.Equals(Path, Instance.PathVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameInstancePageScope(InstancePageScope? first, InstancePageScope? second) =>
        first is { } left ? second is { } right && ReferenceEquals(left.Instance, right.Instance) &&
            string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) : second is null;

    private sealed class RetiredPage(UIElement? source, GlassPresentationSession session, InstancePageScope? instance) {
        internal UIElement? Source { get; } = source;
        internal GlassPresentationSession Session { get; } = session;
        internal InstancePageScope? Instance { get; } = instance;
        internal bool Released { get; set; }
    }

    private bool RefreshPagePresentation() {
        UIElement? next = DisplayedPage;
        if (next is null) return false;
        InstancePageScope? instance = _owner.GlassPageCode == (int)FormMain.PageType.InstanceSetup &&
            ReferenceEquals(_owner.PageRight, next) && PageInstanceLeft.Instance is { } selected
                ? new InstancePageScope(selected, selected.PathVersion) : null;
        if (ReferenceEquals(_projectedPage, next) && (instance is null || SameInstancePageScope(_projectedInstance, instance))) return false;
        if (instance is not null)
            foreach (RetiredPage stale in _retiredPresentations.Where(page => page.Released && !SameInstancePageScope(page.Instance, instance)).ToArray())
                DisposeReleasedPage(stale);
        GlassPresentationSession? previous = _pagePresentation;
        UIElement? previousSource = _projectedPage;
        InstancePageScope? previousInstance = _projectedInstance;
        GlassPresentationSession? reusable = null;
        // The home page's projection (its homepage cards) is kept while other pages show and reused on return, so the
        // cards are not rebuilt (and an online homepage not re-laid out) every time Home opens.
        if (next is PageLaunchRight && _homePresentation is { } home) {
            _homePresentation = null;
            reusable = home;
        } else if (_retiredPresentations.FirstOrDefault(page => (!page.Released || instance is not null) &&
            ReferenceEquals(page.Source, next) && SameInstancePageScope(page.Instance, instance)) is { } outgoing) {
            // Reuse an outgoing view for reversals, or a detached instance tab for a repeat visit.
            _retiredPresentations.Remove(outgoing);
            reusable = outgoing.Session;
        }
        if (reusable is not null) {
            _pagePresentation = reusable;
            _presentations.Add(reusable);
            reusable.ResumePageAppearance();
            // The cached home already has its visual tree. Let the regular refresh update its
            // contents after the page motion, instead of walking the whole homepage before the first frame.
            if (next is not PageLaunchRight) reusable.Refresh();
        } else _pagePresentation = CreatePresentation(_owner.PanMainRight);
        _projectedPage = next;
        _projectedInstance = instance;
        if (previous is not null) {
            previous.HoldPageAppearance();
            _presentations.Remove(previous);
            if (previousSource is PageLaunchRight && _homePresentation is null) _homePresentation = previous;
            else _retiredPresentations.Add(new(previousSource, previous, previousInstance));
        }
        ObserveRightPage(next);
        View.SetPageContent(_pagePresentation.View);
        return true;
    }

    private void ObserveRightPage(UIElement? page) {
        if (_sourceRightPage is not null) {
            _sourceRightPage.PageLeaving -= HoldRightPage;
            _sourceRightPage.PageEntering -= ResumeRightPage;
        }
        _sourceRightPage = page as MyPageRightView;
        if (_sourceRightPage is not null) {
            _sourceRightPage.PageLeaving += HoldRightPage;
            _sourceRightPage.PageEntering += ResumeRightPage;
        }
    }

    private void HoldRightPage() {
        _runtime?.CancelPendingInstanceSettings();
        GlassVideoWallpaper.SetNavigationActive(true);
        _pagePresentation?.HoldPageAppearance();
    }
    private void ResumeRightPage() => _pagePresentation?.ResumePageAppearance();

    private void OnPageContentReleased(UIElement content) {
        var retired = _retiredPresentations.FirstOrDefault(page => ReferenceEquals(page.Session.View, content));
        if (retired is null || retired.Released) return;
        retired.Released = true;
        if (retired.Instance is { IsCurrent: true }) {
            var cached = _retiredPresentations.Where(page => page.Released && page.Instance is { IsCurrent: true }).ToArray();
            if (cached.Length > InstancePageCacheLimit) DisposeReleasedPage(cached[0]);
        } else DisposeReleasedPage(retired);
    }

    private void DisposeReleasedPage(RetiredPage page) {
        if (_retiredPresentations.Remove(page))
            page.Session.DisposeReleasedPage();
    }
    private void RefreshNavigation() {
        // A detail page (resource/help detail) keeps the sidebar of the page it was opened from.
        FormMain.PageType? origin = _owner.GlassContextOrigin;
        MyPageLeft? left = origin is { } source ? FormMain.GlassContextLeft(source) : _owner.PageLeft;
        bool hasContext = origin is not null || _owner.GlassPageCode != (int)FormMain.PageType.Launch && _owner.PageLeft is { Width: not 0 };
        // The task manager's left page is a column of live metrics, not navigation: show them as a progress panel.
        var taskPage = hasContext ? left as PageSpeedLeft : null;
        if (taskPage is not null) { _taskSidebar ??= new GlassTaskSidebar(); _taskSidebar.Refresh(taskPage); }
        View.SetSidebarCustom(taskPage is not null ? _taskSidebar : null);
        View.SetSidebarProgress(taskPage?.HasActiveTasks == true ? taskPage.OverallProgress : null);
        IReadOnlyList<GlassSidebarItem> items = hasContext && left is not null && taskPage is null ? GlassContextNavigation.Build(left) : Array.Empty<GlassSidebarItem>();
        string signature = (left?.GetType().FullName ?? "") + ":" + hasContext + ":" + origin + ":" +
            string.Join("|", items.Select(item => item.Label + ":" + item.IsSelected + ":" + item.IsEnabled + ":" + item.IsHeader + ":" + item.Actions.Count));
        if (ReferenceEquals(_sidebarOwner, left) && signature == _navigationSignature) return;
        _sidebarOwner = left;
        _navigationSignature = signature;
        if (origin is { } back) items = items.Select(item => ReturnFromDetail(item, back)).ToArray();
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

    private void OnThemeToggle(object? sender, EventArgs args) => FormMain.ToggleLauncherColorMode();

    internal bool TryRefreshTheme(bool dark, Action apply)
    {
        if (!_attached || _disposed || ModTheme.IsDarkTheme == dark) return false;
        apply();
        Refresh(forcePreferences: true);
        return true;
    }

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
            _modalEffects?.SetVideoBackdrop(video ? wallpaper : null, video ? material : null);
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
    private void OnRegionsChanged(object? sender, EventArgs args) {
        RefreshRegions();
        UpdateToastPlacement();
    }

    // The homepage counts as loaded once its content is in place (or when no homepage is configured).
    private static bool HomepageLoaded() =>
        Settings.Get<int>("UiCustomType") == 0 || ModMain.FrmLaunchRight?.HasHomepageContent == true;

    private VideoWallpaperSampler? _videoFrost;

    private void UpdateVideoWallpaperActivity() {
        bool paused = !CanRenderWindow;
        GlassVideoWallpaper.SetPaused(paused);
        _videoFrost?.SetPaused(paused || _windowResizing);
    }

    private void RefreshAudioVisualization() {
        if (_visualizer is null || _music is null) return;
        bool enabled = Settings.Get<bool>("UiMusicVisualizerEnabled");
        var style = Settings.Get<int>("UiMusicVisualizerStyle") == (int)GlassAudioVisualizer.SpectrumStyle.Line
            ? GlassAudioVisualizer.SpectrumStyle.Line : GlassAudioVisualizer.SpectrumStyle.Bars;
        bool playing = CanRenderWindow && !GlassModalEffects.IsCrashReport(_lastActiveModal) &&
            _music.IsReady && _music.OnHome && ModMusic.MusicState == ModMusic.MusicStates.Play;
        _visualizer.Update(enabled && CanRenderWindow, playing, style);
    }

    private void SetVideoFrost(Image? source) {
        if (source is null) { _videoFrost?.Dispose(); _videoFrost = null; return; }
        _videoFrost ??= new VideoWallpaperSampler(_owner.Dispatcher, frame => {
            GlassMaterial.SetWallpaperSource(_root, frame);
            View.SetWallpaperMaterialSource(frame);
            _modalEffects?.SetVideoBackdrop(View.FindName("WallpaperImage") as Image, frame);
        });
        _videoFrost.SetHost(source);
        UpdateVideoWallpaperActivity();
    }

    private PCLLF.Wpf.Motion.SpringMotion? _backdropPresence;
    private bool _revealQueued;

    // Minimizing hides the native blur at once; restoring brings the interface back first and fades the blur in after
    // it (Windows would otherwise show the separate backdrop window before WPF has redrawn the content).
    private void OnWindowLocationChanged(object? sender, EventArgs args) => QueueWorkAreaCheck();
    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs args) => QueueWorkAreaCheck();

    private void QueueWorkAreaCheck()
    {
        if (_disposed || !_owner.IsLoaded || !CanRenderWindow || _owner.GlassPreviewState is not null ||
            _workAreaCheck?.Status == DispatcherOperationStatus.Pending) return;
        _workAreaCheck = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _workAreaCheck = null;
            UpdateWorkArea();
        }));
    }

    internal static (Size MinSize, double RailAllowance) FitWorkArea(Size preferred, Size available)
    {
        double allowance = available.Width >= preferred.Width + LeftOverhangAllowance ? LeftOverhangAllowance : 0;
        double minWidth = allowance > 0 ? preferred.Width + allowance : Math.Min(preferred.Width, 810);
        return (new Size(Math.Min(minWidth, available.Width), Math.Min(preferred.Height, available.Height)), allowance);
    }

    private void UpdateWorkArea()
    {
        if (_disposed || !CanRenderWindow || _owner.GlassPreviewState is not null ||
            !WindowResizer.TryGetMonitorLayout(_owner, out Rect work, out Rect bounds) || work == _lastWorkArea) return;
        _lastWorkArea = work;
        var (minimum, allowance) = FitWorkArea(_preferredMinimum, work.Size);
        double delta = allowance - View.LeftExpansionAllowance;
        bool wasSaveable = _owner.IsSizeSaveable;
        _owner.IsSizeSaveable = false;
        try
        {
            View.SetRailExpansionAvailable(allowance > 0);
            View.SetLeftExpansionAllowance(allowance);
            _owner.MinWidth = minimum.Width;
            _owner.MinHeight = minimum.Height;
            if (_owner.WindowState != WindowState.Normal) return;
            double width = Math.Clamp(bounds.Width + delta, minimum.Width, work.Width);
            double height = Math.Clamp(bounds.Height, minimum.Height, work.Height);
            double left = Math.Clamp(bounds.Left - delta, work.Left, Math.Max(work.Left, work.Right - width));
            double top = Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
            _owner.Width = width;
            _owner.Height = height;
            _owner.Left += left - bounds.Left;
            _owner.Top += top - bounds.Top;
        }
        finally { _owner.IsSizeSaveable = wasSaveable; }
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateWindowActivity();
    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateWindowActivity();

    private void UpdateWindowActivity()
    {
        if (_disposed) return;
        bool visible = CanRenderWindow;
        UpdateVideoWallpaperActivity();
        RefreshAudioVisualization();
        if (!visible)
        {
            _music?.CloseCard(immediate: true);
            _refreshTimer.Stop();
            _backdropPresence?.Snap(0);
            return;
        }
        QueueWorkAreaCheck();
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
        UpdateToastPlacement();
    }
    private void OnToastObstacleSizeChanged(object sender, SizeChangedEventArgs args) => UpdateToastPlacement();
    private void UpdateToastPlacement() {
        if (_disposed || _toasts is null || _toasts.Children.Count == 0 || !_contentLayer.IsLoaded || !View.IsLoaded ||
            _contentLayer.ActualWidth <= 0 || _contentLayer.ActualHeight <= 0) return;
        Rect obstacles = View.MusicOverlayBounds(_contentLayer);
        if (_extraActions is { IsVisible: true, ActualWidth: > 1, ActualHeight: > 1 } actions) {
            // Match the stack's animated bottom margin even before its next arrange pass.
            Point origin = new(_contentLayer.ActualWidth - actions.Margin.Right - actions.ActualWidth,
                               _contentLayer.ActualHeight - actions.Margin.Bottom - actions.ActualHeight);
            obstacles.Union(new Rect(origin, actions.RenderSize));
        }
        _toasts.AvoidOverlays(_contentLayer.RenderSize, obstacles);
    }
    internal void SetMaterialDensity(double opacity) {
        GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(opacity, 0.4, 1));
    }
    internal void SetWindowResizing(bool resizing) {
        if (_disposed || _windowResizing == resizing) return;
        _windowResizing = resizing;
        _modalEffects?.SetWindowResizing(resizing);
        UpdateVideoWallpaperActivity();
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
        if (DisplayedPage is { } page && !ReferenceEquals(_projectedPage, page)) QueuePageRefresh();
    }
    private void QueuePageRefresh() {
        if (_pageRefreshQueued || _disposed) return;
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
            bool crash = GlassModalEffects.IsCrashReport(active);
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
            RefreshCrashBanner(crash);
        } finally { _refreshingModal = false; }
    }

    // The game-crash dialog ("Minecraft 出现错误") gets the playful crash banner above it, between the scrim and the
    // dialog; the dialog moves down to make room. It leaves with the dialog.
    private const double CrashBannerRoom = 190;
    private GlassCrashBanner? _crashBanner;
    private GlassMusicPlayer? _music;
    private GlassExtraButtonAdapter? _musicButton;
    private GlassAudioVisualizer? _visualizer;
    private PCLLF.Wpf.Motion.SpringMotion? _extraLift;
    private double _extraLiftTarget = ExtraBaseLift;
    private const double ExtraBaseLift = 38, ExtraGap = 12;

    // After layout (the card's height is known once it is visible).
    private void QueueExtraLift() => _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateExtraLift));

    /// <summary>
    /// Keeps the floating buttons (back to top, tasks, the round music button) 12 px above whatever the music player
    /// shows at the bottom right: the dock while it is out, plus its track list while that is open. Measured from the
    /// real edges, and eased with a spring rather than jumping.
    /// </summary>
    private const double ExtraButtonWidth = 44, PageGutterGap = 12, PageBaseMargin = 24;

    /// <summary>
    /// Every page keeps a column free on its right for the floating round buttons (back to top, tasks, music): the main
    /// content stops 12 px short of the buttons' left edge, as the settings pages already did. The column is kept whether
    /// or not a button is showing, so pages never shift when one appears.
    /// </summary>
    private void UpdatePageGutter()
    {
        if (_extraActions is not { Parent: FrameworkElement layer } actions || layer.ActualWidth <= 0) return;
        if (View.FindName("MainContentGrid") is not FrameworkElement content || content.Parent is not FrameworkElement host || host.ActualWidth <= 0) return;
        try
        {
            double buttons = Math.Max(ExtraButtonWidth, actions.ActualWidth);
            double buttonsLeft = layer.ActualWidth - actions.Margin.Right - buttons;
            double hostRight = host.TransformToVisual(layer).Transform(new Point(host.ActualWidth, 0)).X;
            double right = Math.Max(PageBaseMargin, hostRight - buttonsLeft + PageGutterGap);
            Thickness margin = content.Margin;
            if (Math.Abs(margin.Right - right) > .5) content.Margin = new Thickness(margin.Left, margin.Top, right, margin.Bottom);
        }
        catch (InvalidOperationException) { }
    }

    private void UpdateExtraLift()
    {
        if (_extraActions is not { Parent: FrameworkElement layer } actions || _music is null) return;
        double target = ExtraBaseLift;
        if (_music.IsDocked && layer.ActualHeight > 0 && View.ActualHeight > 0)
        {
            try
            {
                double layerBottom = layer.TransformToAncestor(_owner).Transform(new Point(0, layer.ActualHeight)).Y;
                double obstacleTop = View.TransformToAncestor(_owner).Transform(new Point(0, View.ActualHeight)).Y - View.MusicDock.ActualHeight;
                if (_music.IsCardOpen && _music.Card.ActualHeight > 0) obstacleTop -= GlassLauncherView.MusicPlaylistGap + _music.Card.ActualHeight;
                target = Math.Max(ExtraBaseLift, layerBottom - obstacleTop + ExtraGap);
            }
            catch (InvalidOperationException) { }
        }
        if (Math.Abs(target - _extraLiftTarget) < .5) return;
        _extraLiftTarget = target;
        _extraLift ??= new PCLLF.Wpf.Motion.SpringMotion(actions, actions.Margin.Bottom, value =>
        {
            var margin = new Thickness(0, 0, 52, value);
            if (actions.Margin != margin) { actions.Margin = margin; UpdateToastPlacement(); }
        }, new PCLLF.Wpf.Motion.SpringParameters(.42, .84));
        // Minimised, the round music button joins the column in the corner itself: land at once so it appears in its
        // own place instead of sliding down from where the dock was.
        bool settle = _music.IsMinimised && target <= ExtraBaseLift;
        _extraLift.Retarget(target, settle || !PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation);
    }

    private void OnMusicOutsideClick(object sender, MouseButtonEventArgs args)
    {
        if (_music is null || args.OriginalSource is not DependencyObject source) return;
        if (Within(source, View.MusicDock) || Within(source, _music.Card) || _musicButton is { } round && Within(source, round.View)) return;
        _music.Dismiss();

        static bool Within(DependencyObject node, DependencyObject container)
        {
            for (DependencyObject? current = node; current is not null;
                 current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, container)) return true;
            return false;
        }
    }

    private void RefreshCrashBanner(bool crash) {
        if (crash && _crashBanner is null) {
            _crashBanner = new GlassCrashBanner { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 720, Height = CrashBannerRoom - 26 };
            Panel.SetZIndex(_crashBanner, 28);
            _root.Children.Add(_crashBanner);
            _lastModalClipKey = null;
            AlignModalToBody();
            _crashBanner.Play();
        } else if (!crash && _crashBanner is { } banner) {
            _crashBanner = null;
            banner.Dismiss(() => _root.Children.Remove(banner));
            AlignModalToBody();
        }
    }
    // Dialog and scrim occupy the main body only, so neither crosses the transparent gap beside the icon rail.
    private void AlignModalToBody() {
        if (View.FindName("BodyFrame") is not FrameworkElement body || !body.IsLoaded || _modalView is null) return;
        Rect bounds;
        try { bounds = body.TransformToAncestor(_root).TransformBounds(new Rect(body.RenderSize)); }
        catch (InvalidOperationException) { return; }
        var margin = new Thickness(bounds.Left, bounds.Top, Math.Max(0, _root.ActualWidth - bounds.Right), Math.Max(0, _root.ActualHeight - bounds.Bottom));
        if (_modalScrim.Margin != margin) _modalScrim.Margin = margin;
        Thickness dialogMargin = margin;
        if (_crashBanner is not null) {
            double bannerHeight = _crashBanner.Height, gap = 10;
            double bannerRoom = bannerHeight + gap + 12;
            // Reserve the banner's space independently of the card's measured size. A long report
            // otherwise alternates between filling the current inset and forcing a different one.
            dialogMargin = new Thickness(margin.Left, margin.Top + bannerRoom, margin.Right, margin.Bottom);
            double cardHeight = DialogCardHeight();
            double cardTop = bannerRoom + Math.Max(0, (bounds.Height - bannerRoom - cardHeight) / 2);
            double bannerTop = bounds.Top + cardTop - bannerHeight - gap;
            var bannerMargin = new Thickness(margin.Left + 32, bannerTop, margin.Right + 32, 0);
            if (_crashBanner.Margin != bannerMargin) _crashBanner.Margin = bannerMargin;
        }
        if (_modalView.Margin != dialogMargin) _modalView.Margin = dialogMargin;
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
    // Height of the dialog card inside the modal projection (its frosted sheet), 0 before it has been laid out.
    private double DialogCardHeight() {
        if (_modalView is null) return 0;
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(_modalView);
        while (pending.Count > 0) {
            DependencyObject item = pending.Dequeue();
            if (item is GlassSurface { ActualHeight: > 0 } card && !ReferenceEquals(item, _modalView)) return card.ActualHeight;
            int count = VisualTreeHelper.GetChildrenCount(item);
            for (int index = 0; index < count; index++) pending.Enqueue(VisualTreeHelper.GetChild(item, index));
        }
        return 0;
    }
    private static bool SameRect(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.05 && Math.Abs(a.Y - b.Y) < 0.05 &&
        Math.Abs(a.Width - b.Width) < 0.05 && Math.Abs(a.Height - b.Height) < 0.05;
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _visualizer?.Update(false, false, GlassAudioVisualizer.SpectrumStyle.Bars);
        _music?.CloseCard(immediate: true);
        _owner.PreviewMouseDown -= OnMusicOutsideClick;
        ObserveRightPage(null);
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
        foreach (RetiredPage retired in _retiredPresentations) retired.Session.Dispose();
        _homePresentation?.Dispose(); _homePresentation = null;
        _retiredPresentations.Clear();
        (_accountPortal as IDisposable)?.Dispose();
        _accountWorkspace?.Dispose();
        foreach (GlassExtraButtonAdapter action in _extraButtons) action.Dispose();
        _extraButtons.Clear();
        if (_extraActions is { } actions) actions.SizeChanged -= OnToastObstacleSizeChanged;
        if (_toasts is { } toasts) { toasts.SizeChanged -= OnToastObstacleSizeChanged; toasts.Dispose(); }
        _runtime?.Dispose();
        if (_runtime is not null) _runtime.AccountRequested -= OnAccountRequested;
        View.ThemeToggleRequested -= OnThemeToggle;
        View.MinimizeRequested -= OnMinimize; View.CloseRequested -= OnClose;
        View.DragRequested -= OnDrag;
        View.RegionsChanged -= OnRegionsChanged;
        View.PageContentReleased -= OnPageContentReleased;
        _owner.StateChanged -= OnWindowStateChanged;
        _owner.IsVisibleChanged -= OnWindowVisibilityChanged;
        _owner.LocationChanged -= OnWindowLocationChanged;
        _owner.DpiChanged -= OnWindowDpiChanged;
        if (_workAreaCheck?.Status == DispatcherOperationStatus.Pending) _workAreaCheck.Abort();
        _workAreaCheck = null;
        _videoFrost?.Dispose();
        GlassVideoWallpaper.Stop();
        if (_startupPreload) GlassStartupPreload.Cancel();
        _backdropPresence?.Dispose();
        View.Dispose();
        _owner.Loaded -= OnLoaded; _owner.SizeChanged -= OnSizeChanged;
        _owner.Closed -= OnClosed; _owner.Activated -= OnActivated;
    }
}
