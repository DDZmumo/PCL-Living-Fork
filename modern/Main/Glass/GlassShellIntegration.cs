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

/// <summary>Hosts the launcher workspace and connects it to application services.</summary>
internal sealed partial class GlassShellIntegration : IDisposable {
    private readonly FormMain _owner;
    private readonly Grid _root = new();
    private readonly Grid _contentLayer = new();
    private readonly Border _modalScrim = new() { Background = Brushes.Black, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Canvas _resizeLayer = new();
    private readonly FrameworkElement[] _resizeParts = new FrameworkElement[8];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, ImageSource> _wallpaperCache = new(StringComparer.OrdinalIgnoreCase);
    private GlassShellRuntime? _runtime;
    private ImageSource? _wallpaperSource;
    private long _nextWallpaperRefresh;
    private string _wallpaperOptions = "";
    private string _navigationSignature = "";
    private ModMinecraft.McInstance? _sidebarInstance;
    private bool _attached, _disposed, _refreshing;
    private GlassAccountWorkspace? _accountWorkspace;
    private readonly List<GlassFloatingAction> _extraButtons = new();
    private GlassFloatingAction? _taskButton, _backButton, _shutdownButton, _giveUpButton;
    private GlassWindowRegionClip? _windowRegion;
    private GlassToastHost? _toasts;
    private StackPanel? _extraActions;
    private GlassWindowBackdrop? _backdrop;
    private GlassModalEffects? _modalEffects;
    private FrameworkElement? _modalView;
    private Grid? _modalLayer;
    private GlassMessageDialog? _directMessage;
    private object? _lastActiveModal;
    private (Rect Body, Rect Controls, bool HasControls)? _lastModalClipKey;
    private bool _refreshingModal;
    private bool _windowResizing;
    private bool _startupPreload;
    private double _lastOpacityMultiplier = double.NaN;
    private bool? _lastDarkTheme;
    private (int Radius, bool Video)? _appliedWallpaperBlur;
    private int _wallpaperOpacity = -1;
    internal const double LeftOverhangAllowance = GlassLauncherView.RailExpandDelta + GlassLauncherView.RailOvershootHeadroom;
    private Rect _lastWorkArea = Rect.Empty;
    private Size _preferredMinimum;
    private DispatcherOperation? _workAreaCheck;
    private bool CanRenderWindow => _owner.IsVisible && _owner.WindowState != WindowState.Minimized;
    internal bool HasLeftExpansionOverhang => View.LeftExpansionAllowance > 0;
    internal GlassLauncherView View { get; } = new();
    internal bool OwnsPage(FormMain.PageStackData target, FormMain.PageSubType subPage) {
        var page = target.Page;
        return page is FormMain.PageType.Launch or FormMain.PageType.TaskManager or FormMain.PageType.Link or FormMain.PageType.ResourceDetail or FormMain.PageType.InstanceSelect ||
            IsBrowserRoute(page, subPage) || IsLocalResourceRoute(page, subPage) || IsJavaRoute(page, subPage, target.Additional) || IsWorkspaceRoute(page, subPage) ||
            page == FormMain.PageType.HelpDetail && target.Additional is object[] { Length: > 0 } args && args[0] is ModMain.HelpEntry ||
            page == FormMain.PageType.Other && subPage is FormMain.PageSubType.OtherHelp or FormMain.PageSubType.OtherAbout or FormMain.PageSubType.OtherSchematics ||
            page == FormMain.PageType.Setup && subPage is FormMain.PageSubType.SetupUpdate or FormMain.PageSubType.SetupUI;
    }
    internal void RequestPageRefresh() => QueuePageRefresh();
    internal void CloseMultiplayer() => _terracottaPage?.Dispose();
    internal void CheckLauncherUpdates() => UpdatePage.CheckNow();
    internal void ResetPersonalization() => PersonalizationPage.Reset();
    internal void InvalidateHomepageSettings() => _homePage?.Loader.InvalidateSettings();
    internal void RefreshHomepage() => HomePage.Loader.ForceRefresh();
    internal void ClearLaunchLog() => HomePage.ClearLaunchLog();
    internal void AppendLaunchLog(string text) => HomePage.AppendLaunchLog(text);
    internal void RefreshRecentWorlds() => _runtime?.RequestRecentWorldRefresh();
    internal bool HandleHomepageException(Exception error) =>
        _directPage?.Page == FormMain.PageType.Launch && _homePage?.Loader.HandleDispatcherException(error) == true;
    internal bool CanRefreshDirectPage => _owner.IsDirectHomepage ||
        _directPage?.Page == _owner.PageCurrent.Page &&
        (_activeResourceBrowser is not null || _activeResourceDetail is not null || _activeLocalResources is not null ||
            _helpDetail is not null || _directPage?.Page == FormMain.PageType.InstanceSelect ||
            _directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherHelp or FormMain.PageSubType.OtherSchematics) ||
            _activeJava is GlassJavaManagementPage or GlassInstanceJavaPage || _activeWorkspacePage is IRefreshable);
    internal bool TryRefreshDirectPage() {
        if (!CanRefreshDirectPage) return false;
        if (_owner.IsDirectHomepage) RefreshHomepage();
        else if (_directPage?.Page == FormMain.PageType.InstanceSelect) _instanceSelection?.Reload(force: true);
        else if (_activeLocalResources is { } local) local.Refresh();
        else if (_directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherSchematics)) _schematicManager?.Refresh();
        else if (_directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherHelp)) _help?.Reload();
        else if (_helpDetail is not null) _helpDetail.Reload();
        else if (_activeJava is GlassJavaManagementPage) ModJava.ManuallySearchJava();
        else if (_activeJava is GlassInstanceJavaPage instance) instance.Reload();
        else if (_activeWorkspacePage is IRefreshable workspace) workspace.Refresh();
        else if (_activeResourceBrowser is { } browser) browser.Reload();
        else _activeResourceDetail?.Reload();
        return true;
    }
    internal bool CloseSchematicPreview() => _owner.PageCurrent.Page == _directPage?.Page &&
        (_activeLocalResources?.CloseSchematicPreview() == true ||
         _directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherSchematics) && _schematicManager?.ClosePreview() == true);
    internal bool HasDirectMessage => _directMessage is not null;
    internal bool CanPresentMessage => _modalLayer is not null && !_disposed;

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
        // Only cold product startup prepares the selected video before the window is shown.
        _startupPreload = System.Reflection.Assembly.GetEntryAssembly() == typeof(GlassShellIntegration).Assembly && _owner.GlassPreviewState is null;
        if (_startupPreload && !_owner.IsLoaded) PreloadSelectedVideoWallpaper();
        _contentLayer.Children.Add(View);
        _root.Children.Add(_contentLayer);
        Panel.SetZIndex(_modalScrim, 25); _root.Children.Add(_modalScrim);
        View.SetSurfaceMaskHost(_root);
        GlassCommandChrome.SetToolTipScrollTracking(_root, true);
        _owner.Content = _root;
        _windowRegion = GlassWindowRegionClip.Attach(_owner);
        GlassMaterial.SetIsEnabled(_root, true);
        _toasts = new GlassToastHost();
        _toasts.SizeChanged += OnToastObstacleSizeChanged;
        Panel.SetZIndex(_toasts, 20); _contentLayer.Children.Add(_toasts);
        InitializeModalLayer();
        {
            var actions = _extraActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, ExtraRight, ExtraBaseLift),
                RenderTransform = _extraLiftOffset };
            actions.SizeChanged += OnToastObstacleSizeChanged;
            GlassFloatingAction Add(string name, string title, string icon, Func<bool> show, Action click, Action? alternate = null, bool stacked = true) {
                var action = new GlassFloatingAction(name, title, icon, !ModTheme.IsDarkTheme, show, click, alternate);
                action.SetBackdrop(View.BodyFrame);
                _extraButtons.Add(action);
                if (stacked) actions.Children.Add(action.View);
                return action;
            }
            _backButton = Add("BackToTop", "返回顶部", GlassFloatingAction.BackToTopIcon,
                () => ProjectedBackShowCheck() == true, _owner.BackToTop);
            _taskButton = Add("TasksButton", "任务管理", ModBase.Logo.IconButtonList,
                () => _owner.PageCurrent.Page != FormMain.PageType.TaskManager && ModNet.HasDownloadingTask(),
                () => _owner.PageChange(FormMain.PageType.TaskManager));
            _taskButton.TotalProgress = () => {
                var tasks = ModLoader.LoaderTaskbar.ToList().Where(task => task.Show).ToList();
                return tasks.Count == 0 ? null : Math.Clamp(tasks.Average(task => task.Progress), 0, 1);
            };
            _giveUpButton = Add("GiveUpButton", "投降", "M3,2 H5 V23 H3 Z M7,3 L21,5 V16 L7,13 Z",
                _owner.BtnExtraApril_ShowCheck, _owner.AprilGiveup);
            _shutdownButton = Add("ShutdownGameButton", "关闭 Minecraft", "M10.6,0 H13.4 V12 H10.6 Z M6,3 A11,11 0 1 0 18,3 L16.5,5.5 A8,8 0 1 1 7.5,5.5 Z",
                _owner.BtnExtraShutdown_ShowCheck, _owner.BtnExtraShutdown_Click);
            _musicButton = Add("MusicButton", "展开播放器", ModBase.Logo.IconMusic,
                () => _music is { IsMinimised: true, IsReady: true }, () => _music?.SetMinimised(false), ModMusic.MusicControlNext, stacked: false);
            var round = _musicButton.View;
            round.HorizontalAlignment = HorizontalAlignment.Right;
            round.VerticalAlignment = VerticalAlignment.Bottom;
            round.Margin = new Thickness(0, round.Margin.Top, ExtraRight, ExtraBaseLift + round.Margin.Bottom);
            round.SizeChanged += OnToastObstacleSizeChanged;
            Panel.SetZIndex(round, 12);
            _contentLayer.Children.Add(round);
            // Overlay the page without changing its width as the floating buttons come and go.
            Panel.SetZIndex(actions, 12); _contentLayer.Children.Add(actions);
            // The bar docks in the window's bottom-right corner (the window controls' twin); its track list card opens
            // right above it, matching its width.
            // Spectrum in the body's backdrop: over the wallpaper, under the panes (their frosted glass blurs it).
            if (View.FindName("BodyBackdropLayer") is Panel backdropLayer) { _visualizer = new GlassAudioVisualizer(); backdropLayer.Children.Add(_visualizer); }
            _music = new GlassMusicPlayer(View);
            View.SetMusicDock(_music.Bar);
            View.MusicDock.SizeChanged += OnMusicOverlaySizeChanged;
            _music.Card.SizeChanged += OnMusicOverlaySizeChanged;
            _music.MinimisedChanged += (_, _) => {
                RefreshMusicButtonProgress();
                _musicButton.RefreshVisibility();
                QueueExtraLift();
            };
            _music.CardChanged += (_, _) => QueueExtraLift();
            // A click anywhere outside the player closes its one-shot parts (track list; the dock away from home).
            _owner.PreviewMouseDown += OnMusicOutsideClick;
        }
        Panel.SetZIndex(_resizeLayer, 15); _root.Children.Add(_resizeLayer);
        Cursor[] cursors = [Cursors.SizeNS, Cursors.SizeWE, Cursors.SizeNESW, Cursors.SizeNWSE,
            Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNS];
        for (int index = 0; index < _resizeParts.Length; index++) {
            var grip = new System.Windows.Shapes.Rectangle {
                Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), Cursor = cursors[index]
            };
            _resizeParts[index] = grip;
            _resizeLayer.Children.Add(grip);
        }
        _runtime = new GlassShellRuntime(_owner, View, QueuePageRefresh);
        _runtime.AccountRequested += OnAccountRequested;
        View.ThemeToggleRequested += OnThemeToggle;
        View.MinimizeRequested += OnMinimize;
        View.CloseRequested += OnClose;
        View.DragRequested += OnDrag;
        View.RegionsChanged += OnRegionsChanged;
        View.SplashFinished += (_, _) => OnStartupInterfaceShown();
        View.FarewellOverlayFade += OnFarewellOverlayFade;
        // From the splash's first frame the blur is shown through its shapes (each carries its own fade).
        View.SplashStarted += (_, _) => _backdropPresence?.Snap(1);
        _preferredMinimum = new Size(_owner.MinWidth, _owner.MinHeight);
        _owner.StateChanged += OnWindowStateChanged;
        _owner.IsVisibleChanged += OnWindowVisibilityChanged;
        _owner.LocationChanged += OnWindowLocationChanged;
        _owner.DpiChanged += OnWindowDpiChanged;
        if (_startupPreload)
        {
            GlassStartupPreload.Begin();
            View.BeginSplash();
        }
        _owner.Loaded += OnLoaded;
        _owner.SizeChanged += OnSizeChanged;
        _owner.Closed += OnClosed;
        _owner.Activated += OnActivated;
        // Loaded chooses desktop Acrylic or launcher wallpaper after the real HWND and layout exist.
        if (_owner.IsLoaded) OnLoaded(_owner, new RoutedEventArgs());
    }

    private void InitializeModalLayer() {
        var layer = _modalLayer = new Grid();
        _modalView = layer;
        Panel.SetZIndex(layer, 30);
        _root.Children.Add(layer);
        _modalEffects = new GlassModalEffects(_root, _contentLayer, View.FindName("BodyFrame") as FrameworkElement ?? _contentLayer,
            _modalScrim, layer, View.FindName("ModalVideoFrost") as Border, View.FindName("CrashBodySnapshot") as Image);
        _modalEffects.Companion = (progress, blurRadius) => {
            View.SetMusicDockVeil(progress, blurRadius);
            if (progress > .01) _music?.CloseCard();
        };
        layer.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnModalAction), true);
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
            double opacityMultiplier = Math.Clamp(Settings.Get<int>("UiPanelOpacity") / 1000d, 0, 1);
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
            bool pageChanged = ShowDirectPage((_owner.PageCurrent.Page, _owner.PageCurrentSub));
            RefreshHeaderVisibility();
            bool pagePending = View.IsAwaitingSection;
            bool motionBusy = View.IsTransitioning || PCLLF.Wpf.Motion.SmoothScroll.IsScrolling;
            bool crashReportOpen = GlassModalEffects.IsCrashReport(_lastActiveModal);
            GlassVideoWallpaper.SetNavigationActive(pagePending || View.IsTransitioning);
            if (paletteChanged) {
                _activeResourceBrowser?.Refresh();
                _activeResourceDetail?.Refresh();
                if (_directPage?.Page == FormMain.PageType.InstanceSelect) _instanceSelection?.RefreshPalette();
                _activeLocalResources?.PaletteChanged();
                _schematicManager?.RefreshPalette();
                _about?.RefreshPalette();
                _help?.RefreshPalette();
                _helpDetail?.RefreshPalette();
                _activeJava?.ApplyPalette();
                RefreshWorkspacePalette();
                if (_directPage?.Page == FormMain.PageType.Launch) _homePage?.RefreshAppearance();
                if (_directPage?.Page == FormMain.PageType.TaskManager) _taskManager?.RefreshPalette();
                if (_directPage?.Page == FormMain.PageType.Link) _terracottaPage?.RefreshPalette();
                if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate)) _updatePage?.RefreshPalette();
                if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUI)) _personalizationPage?.RefreshPalette();
                _directMessage?.RefreshPalette();
            }
            RefreshModalState();
            if (View.IsAccountWorkspaceOpen) _accountWorkspace?.Refresh();
            foreach (var action in _extraButtons) {
                action.SetBackdropSuspended(pageChanged || pagePending || motionBusy);
                action.RefreshAppearance(!darkTheme, false);
                action.RefreshVisibility();
                action.RefreshProgress();
            }
            if (_music is not null) {
                // The player arrives once loading is over (never over the splash); so does the music.
                bool ready = !View.IsSplashing && !View.HoldsBackdrop;
                if (ready && !_music.Ready) ModMusic.MusicReleaseStart();
                _music.Ready = ready;
                _music.OnHome = _owner.GlassPageCode == (int)FormMain.PageType.Launch;
                _music.Refresh(updateDetails: !crashReportOpen && !pageChanged && !motionBusy);
                _musicButton?.SetIcon(ModMusic.MusicState == ModMusic.MusicStates.Pause ? ModBase.Logo.IconPlay : ModBase.Logo.IconMusic);
                RefreshMusicButtonProgress();
                RefreshAudioVisualization();
                if (!motionBusy) UpdateExtraLift();
            }
            if (!pageChanged && !crashReportOpen && !motionBusy) {
                RefreshJava();
                _activeResourceBrowser?.Refresh();
                _activeResourceDetail?.Refresh();
                if (_directPage?.Page == FormMain.PageType.Launch) _homePage?.RefreshAppearance();
                if (_directPage?.Page == FormMain.PageType.TaskManager) RefreshTaskManager();
                if (_directPage?.Page == FormMain.PageType.Link) _terracottaPage?.Refresh();
                if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate)) _updatePage?.Refresh();
                if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUI)) _personalizationPage?.Refresh();
            }
            RefreshToasts();
            if (View.IsSplashing) {
                // Restoring a valid selection skips the full instance list scan. Wait for the launch page's
                // initial selection and its page; updates and custom homepage content can arrive later.
                bool selectionReady = _owner.GlassInitialSelectionComplete;
                bool workspaceReady = _directPage is not null;
                bool foldersReady = ModMinecraft.McFolderListLoader.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting);
                bool ready = selectionReady && workspaceReady;
                View.SplashProgress = ready ? 1 : (foldersReady ? .25 : 0) + (selectionReady ? .50 : 0) + (workspaceReady ? .25 : 0);
                View.SplashStageText = !selectionReady
                    ? foldersReady ? "正在读取本地游戏实例" : "正在查找游戏文件夹"
                    : GlassStartupPreload.CheckingForUpdate ? "正在检查启动器更新" : "正在进入主界面";
                if (ready) View.CompleteSplash();
                if (View.IsSplashLoading) GlassStartupPreload.Step();
            }
            RefreshRegions();
        } catch (Exception error) {
            // Keep the shell alive during a page's transient replacement; original operation failures retain their own feedback.
            Logger.Warn(error, "刷新玻璃界面状态失败");
        } finally { _refreshing = false; }
    }
    // The page actually displayed: sub-page switches (e.g. 模组 → 资源包) assign PageRight first and place the page
    // into PanMainRight ~130 ms later, so following PageRight alone would animate the old content and then pop.
    private GlassHomePage HomePage => _homePage ??= new GlassHomePage();
    private GlassHomePage? _homePage;
    private GlassTaskManagerPage TaskManager => _taskManager ??= new GlassTaskManagerPage(LauncherApplicationServices.TaskManager);
    private GlassTaskManagerPage? _taskManager;
    private GlassTerracottaPage TerracottaPage => _terracottaPage ??= new GlassTerracottaPage(_owner, QueuePageRefresh);
    private GlassTerracottaPage? _terracottaPage;
    private GlassLauncherUpdatePage UpdatePage => _updatePage ??= new GlassLauncherUpdatePage();
    private GlassLauncherUpdatePage? _updatePage;
    private GlassPersonalizationPage PersonalizationPage => _personalizationPage ??= new GlassPersonalizationPage(ChooseWallpaper, ClearWallpaper, ReadWallpaperStatus);
    private GlassPersonalizationPage? _personalizationPage;
    private (FormMain.PageType Page, FormMain.PageSubType SubPage)? _directPage;

    private bool ShowDirectPage((FormMain.PageType Page, FormMain.PageSubType SubPage) route) {
        GlassWorkspacePage? workspacePage = IsWorkspaceRoute(route.Page, route.SubPage) ? GetWorkspacePage(route.Page, route.SubPage) : null;
        bool sameTarget = route.Page switch {
            FormMain.PageType.ResourceDetail => ReferenceEquals(_resourceDetailRoute, _owner.PageCurrent),
            FormMain.PageType.HelpDetail => ReferenceEquals(_helpDetailRoute, _owner.PageCurrent) || _activeJava is GlassJavaDownloadPage &&
                IsJavaRoute(route.Page, route.SubPage),
            _ => workspacePage is null || ReferenceEquals(_activeWorkspacePage, workspacePage)
        };
        if (_directPage == route && sameTarget) {
            if (route.Page == FormMain.PageType.Download && _owner.TakeResourceSearchTarget(route.SubPage) is { } target)
                _activeResourceBrowser?.Open(ResourceBrowserCatalog.Find(route.SubPage), target);
            if (route.Page == FormMain.PageType.Download && _activeWorkspacePage is PageDownloadInstall install)
                install.TrySelectWaitingVersion();
            if (View.IsAwaitingPageContent && View.PageContentHost.Content is UIElement current)
                View.SetPageContent(current);
            return false;
        }
        LeaveDirectPage();
        PruneResourceDetails();
        _directPage = route;
        if (route.Page == FormMain.PageType.Launch) {
            if (_owner.GlassPreviewState is null) HomePage.Open();
            View.SetPageContent(HomePage);
            _runtime?.RequestRecentWorldRefresh();
        } else if (route.Page == FormMain.PageType.TaskManager) {
            TaskManager.Open();
            View.SetPageContent(TaskManager);
            View.SetSidebarProgress(TaskManager.Metrics.HasActiveTasks ? TaskManager.Metrics.Progress : null);
        } else if (route.Page == FormMain.PageType.Link) {
            TerracottaPage.Open();
            View.SetPageContent(TerracottaPage);
        } else if (IsBrowserRoute(route.Page, route.SubPage)) {
            OpenResourceBrowser(route.Page, route.SubPage);
        } else if (route.Page == FormMain.PageType.ResourceDetail) {
            OpenResourceDetail();
        } else if (route.Page == FormMain.PageType.InstanceSelect) {
            OpenInstanceSelection();
        } else if (IsLocalResourceRoute(route.Page, route.SubPage)) {
            OpenLocalResources(route.SubPage);
        } else if (IsJavaRoute(route.Page, route.SubPage)) {
            OpenJava(route.Page);
        } else if (workspacePage is not null) {
            _activeWorkspacePage = workspacePage;
            RefreshWorkspacePalette();
            View.SetPageContent(workspacePage);
        } else if (route.Page is FormMain.PageType.Other or FormMain.PageType.HelpDetail) {
            OpenMorePage(route.Page, route.SubPage);
        } else if (route.SubPage == FormMain.PageSubType.SetupUpdate) {
            UpdatePage.Open();
            View.SetPageContent(UpdatePage);
        } else {
            PersonalizationPage.Open();
            View.SetPageContent(PersonalizationPage);
        }
        _owner.RefreshBackToTop();
        return true;
    }

    private void LeaveDirectPage() {
        if (_directPage?.Page == FormMain.PageType.InstanceSelect) _instanceSelection?.Leave();
        _activeLocalResources?.Leave();
        _activeLocalResources = null;
        _activeWorkspacePage = null;
        if (_directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherHelp)) _help?.Leave();
        if (_directPage is (FormMain.PageType.Other, FormMain.PageSubType.OtherSchematics)) _schematicManager?.Leave();
        _helpDetail = null;
        _helpDetailRoute = null;
        LeaveJava();
        _activeResourceBrowser?.Leave();
        _activeResourceBrowser = null;
        _activeResourceDetail?.Leave();
        _activeResourceDetail = null;
        _resourceDetailRoute = null;
        if (_directPage?.Page == FormMain.PageType.Link) _terracottaPage?.Leave();
        if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate)) _updatePage?.Leave();
        if (_directPage is (FormMain.PageType.Setup, FormMain.PageSubType.SetupUI)) _personalizationPage?.Leave();
        _directPage = null;
    }

    private void RefreshTaskManager() {
        if (_owner.GlassPageCode != (int)FormMain.PageType.TaskManager) return;
        TaskManager.Refresh();
        View.SetSidebarProgress(TaskManager.Metrics.HasActiveTasks ? TaskManager.Metrics.Progress : null);
        if (!TaskManager.HasItems) _owner.PageBack();
    }

    private ScrollViewer? ActiveBackToTopScroll => _activeJava?.Scroll ?? ActiveWorkspaceScroll ?? (_directPage switch {
        (FormMain.PageType.Launch, _) => _homePage?.Scroll,
        (FormMain.PageType.TaskManager, _) => _taskManager?.Scroll,
        (FormMain.PageType.Link, _) => _terracottaPage?.Scroll,
        (FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate) => _updatePage?.Scroll,
        (FormMain.PageType.Setup, FormMain.PageSubType.SetupUI) => _personalizationPage?.Scroll,
        (FormMain.PageType.Download or FormMain.PageType.Favorites, _) => _activeResourceBrowser?.Scroll,
        (FormMain.PageType.ResourceDetail, _) => _activeResourceDetail?.Scroll,
        (FormMain.PageType.InstanceSelect, _) => _instanceSelection?.Scroll,
        (FormMain.PageType.InstanceSetup, _) => _activeLocalResources?.ActiveScroll,
        (FormMain.PageType.Other, FormMain.PageSubType.OtherHelp) => _help?.Scroll,
        (FormMain.PageType.Other, FormMain.PageSubType.OtherAbout) => _about?.Scroll,
        (FormMain.PageType.Other, FormMain.PageSubType.OtherSchematics) => _schematicManager?.Scroll,
        (FormMain.PageType.HelpDetail, _) => _helpDetail?.Scroll,
        _ => null
    });

    internal bool? ProjectedBackShowCheck() =>
        ActiveBackToTopScroll is { } scroll ? scroll.IsVisible && scroll.VerticalOffset > 1 : null;

    internal void TryScrollProjectedPageToTop() => ActiveBackToTopScroll?.ScrollToTop();

    internal UIElement ResizeGrip(int index) => _resizeParts[index];

    internal void BeginNavigation() {
        if (!View.IsSplashing && !View.HoldsBackdrop) View.BeginPageExit();
        QueuePageRefresh();
    }

    private void RefreshNavigation() {
        // A detail page (resource/help detail) keeps the sidebar of the page it was opened from.
        FormMain.PageType? origin = _owner.GlassContextOrigin;
        FormMain.PageType route = origin ?? _owner.PageCurrent.Page;
        bool settings = route == FormMain.PageType.Setup;
        bool resources = route is FormMain.PageType.Download or FormMain.PageType.Favorites;
        bool folders = route == FormMain.PageType.InstanceSelect;
        bool instance = route == FormMain.PageType.InstanceSetup;
        bool more = route == FormMain.PageType.Other;
        bool taskPage = _owner.PageCurrent.Page == FormMain.PageType.TaskManager;
        View.SetSidebarCustom(taskPage ? TaskManager.Sidebar : null);
        if (!taskPage) View.SetSidebarProgress(null);
        IReadOnlyList<GlassSidebarItem> items = settings ? GlassSettingsSidebar.Build(_owner) :
            resources ? ResourceSidebar(route) :
            folders ? GameFolderSidebar() :
            instance ? _owner.GlassInstanceNavigation.Sidebar() :
            more ? MoreSidebar() :
            _owner.GlassPageCode == (int)FormMain.PageType.Link ? TerracottaPage.SidebarItems() :
            Array.Empty<GlassSidebarItem>();
        var sidebarInstance = instance ? LauncherInstanceContext.Instance : null;
        string signature = route + ":" + string.Join("|", items.Select(item =>
            item.Label + ":" + item.ToolTip + ":" + item.IsSelected + ":" + item.IsEnabled + ":" + item.IsHeader + ":" + item.Actions.Count));
        if (ReferenceEquals(_sidebarInstance, sidebarInstance) && signature == _navigationSignature) return;
        _sidebarInstance = sidebarInstance;
        _navigationSignature = signature;
        View.SetSidebar(items);
    }

    private void RefreshHeaderVisibility() {
        foreach (var (name, route) in new[] {
            ("NavDownload", FormMain.PageType.Download), ("NavFavorites", FormMain.PageType.Favorites),
            ("NavSettings", FormMain.PageType.Setup), ("NavOther", FormMain.PageType.Other), ("NavLink", FormMain.PageType.Link)
        }) if (View.FindName(name) is FrameworkElement button)
            button.Visibility = ModModpackMode.BlocksPage(route) ? Visibility.Collapsed : Visibility.Visible;
        View.UpdateNavigationRows();
    }
    private void OnAccountRequested(object? sender, EventArgs args) {
        if (_accountWorkspace is null) {
            _accountWorkspace = new GlassAccountWorkspace(View);
            View.SetAccountWorkspace(_accountWorkspace.Side, _accountWorkspace.Scene);
        }
        if (View.IsAccountWorkspaceOpen) return;
        _accountWorkspace.Open();
    }
    internal void StartFromHome() {
        if (_owner.GlassInitialSelectionComplete) _runtime?.StartFromHome();
    }

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

    private void OnClose(object? sender, EventArgs args) {
        if (!View.IsClosing) _owner.EndProgram(true);
    }
    private void OnDrag(object? sender, EventArgs args) {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { _owner.DragMove(); }
        catch (InvalidOperationException) { }
    }
    internal static string ReadSelectedWallpaper() => Settings.Get<int>("UiBackgroundMode") == 0
        ? Settings.Get<string>("UiGlassWallpaperPath") ?? ""
        : "";

    private void PreloadSelectedVideoWallpaper() {
        try {
            string path = ReadSelectedWallpaper();
            if (!GlassVideoWallpaper.IsVideo(path)) return;
            GlassVideoWallpaper.SetPaused(true);
            _ = ReadWallpaper(path);
        }
        catch (Exception error) { Logger.Warn(error, "预读启动器视频壁纸失败"); }
    }

    private void SetWallpaperSelection(string path) {
        Settings.Set("UiGlassWallpaperPath", path);
        if (path.Length > 0) Settings.Set("UiBackgroundMode", 0);
        LauncherAppearanceSettings.BackgroundRefresh(false, false);
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
        bool alphaFallback = _backdrop?.Status is GlassBackdropStatus.UnsupportedPlatform or
            GlassBackdropStatus.LayeredWindowUnsupported or GlassBackdropStatus.Failed;
        GlassMaterial.SetAlphaFallback(_root, alphaFallback);
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

    private void UpdateVideoWallpaperActivity() {
        GlassVideoWallpaper.SetPaused(!CanRenderWindow);
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
        _backdrop?.SetRegions(View.GetDesktopBackdropRegions(),
            reserveLeftInset: View.IsRailAnimating && View.LeftExpansionAllowance > 0);
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
    internal bool ShowHint(string text, ModMain.HintType type) {
        if (_toasts is null || _disposed) return false;
        _toasts.SetTheme(ModTheme.IsDarkTheme);
        _toasts.Show(text, type switch {
            ModMain.HintType.Green => GlassToastKind.Success,
            ModMain.HintType.Red => GlassToastKind.Error,
            _ => GlassToastKind.Info
        });
        UpdateToastPlacement();
        return true;
    }

    internal bool DismissHints() {
        if (_toasts is null || _disposed) return false;
        _toasts.DismissAll();
        return true;
    }
    private void OnToastObstacleSizeChanged(object sender, SizeChangedEventArgs args) => UpdateToastPlacement();
    private void UpdateToastPlacement() {
        if (_disposed || _toasts is null || _toasts.Children.Count == 0 || !_contentLayer.IsLoaded || !View.IsLoaded ||
            _contentLayer.ActualWidth <= 0 || _contentLayer.ActualHeight <= 0) return;
        Rect obstacles = View.MusicOverlayBounds(_contentLayer);
        Include(_extraActions);
        Include(_musicButton?.View);
        _toasts.AvoidOverlays(_contentLayer.RenderSize, obstacles);

        void Include(FrameworkElement? overlay) {
            if (overlay is { IsVisible: true, ActualWidth: > 1, ActualHeight: > 1 } && _contentLayer.IsAncestorOf(overlay))
                obstacles.Union(overlay.TransformToAncestor(_contentLayer).TransformBounds(new Rect(overlay.RenderSize)));
        }
    }
    internal void SetMaterialDensity(double opacity) {
        GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(opacity, 0, 1));
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
    // React to the displayed page changing right away instead of waiting for the next 200 ms tick.
    private bool _pageRefreshQueued;
    private bool? _toolTipDark;
    private bool _savedAppToolTip;
    private bool _hadOriginalAppToolTip;
    private object? _originalAppToolTip;
    private void QueuePageRefresh() {
        if (_pageRefreshQueued || _disposed) return;
        _pageRefreshQueued = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { _pageRefreshQueued = false; Refresh(); }));
    }
    private void OnModalAction(object sender, RoutedEventArgs args) => RefreshModalState();
    internal bool TryPresentMessage(ModMain.MyMsgBoxConverter request) {
        GlassMessageDialog? dialog = null;
        try {
            if (_modalLayer is null || _disposed) throw new OperationCanceledException("窗口已关闭。");
            if (request.Type == ModMain.MyMsgBoxType.Select && request.Content is not IReadOnlyList<DialogChoice>)
                throw new InvalidOperationException("选择弹窗需要使用选项数据。");
            dialog = new GlassMessageDialog(request, () => {
                if (Mouse.LeftButton == MouseButtonState.Pressed) _owner.DragMove();
            }, RemoveDirectMessage);
            _directMessage = dialog;
            _modalLayer.Children.Add(dialog);
            RefreshModalState();
            dialog.LogPresented();
        } catch (Exception error) {
            request.Dismiss(request.Type == ModMain.MyMsgBoxType.Login ? error : null);
            if (dialog is not null) {
                if (ReferenceEquals(_directMessage, dialog)) _directMessage = null;
                dialog.Dispose();
                _modalLayer?.Children.Remove(dialog);
            }
            Logger.Error(error, "打开弹窗失败", LogBehavior.Toast);
        }
        return true;
    }

    private void RemoveDirectMessage(GlassMessageDialog dialog) {
        if (!ReferenceEquals(_directMessage, dialog)) return;
        _directMessage = null;
        dialog.Dispose();
        _modalLayer?.Children.Remove(dialog);
        RefreshModalState();
        if (dialog.IsDeviceLogin) _owner.ShowWindowToTop();
    }

    internal bool HandleDirectMessageKey(Key key) {
        if (_directMessage is not { } dialog) return false;
        dialog.HandleKey(key);
        return true;
    }

    private void RefreshModalState() {
        if (_disposed || _refreshingModal || _modalEffects is null) return;
        if (_directMessage is null && !_modalEffects.HasActiveMessage) return;
        _refreshingModal = true;
        try {
            object? active = GlassModalState.IsOpen(_directMessage) ? _directMessage : null;
            bool crash = GlassModalEffects.IsCrashReport(active);
            _resizeLayer.IsHitTestVisible = active is null;
            _lastActiveModal = active;
            if (active is not null) {
                AlignModalToBody();
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
    private GlassFloatingAction? _musicButton;
    private string _musicProgressTrack = "";

    private void RefreshMusicButtonProgress()
    {
        if (_musicButton is not { } button) return;
        string track = _music is { IsMinimised: true, IsReady: true } ? ModMusic.MusicCurrentPath : "";
        double position = ModMusic.MusicProgress;
        double? progress = track.Length > 0 && ModMusic.MusicDuration > TimeSpan.Zero && double.IsFinite(position)
            ? Math.Clamp(position, 0, 1) : null;
        bool playing = progress is not null && ModMusic.MusicState == ModMusic.MusicStates.Play;
        bool reset = !StringComparer.OrdinalIgnoreCase.Equals(track, _musicProgressTrack) || !playing;
        _musicProgressTrack = track;
        button.SetProgress(progress, reset, playing);
    }

    internal void RefreshTaskAction(bool pulse) {
        if (_taskButton is not { } action) return;
        action.RefreshVisibility();
        action.RefreshProgress();
        if (pulse && action.IsShown) action.Ripple();
    }
    internal void RefreshBackToTop() => _backButton?.RefreshVisibility();
    internal void RefreshShutdownAction() => _shutdownButton?.RefreshVisibility();
    internal bool CanGiveUp => _giveUpButton?.IsShown == true;
    internal void PulseGiveUp() => _giveUpButton?.Ripple();
    private GlassAudioVisualizer? _visualizer;
    private PCLLF.Wpf.Motion.SpringMotion? _extraLift;
    private readonly TranslateTransform _extraLiftOffset = new();
    private double _extraLiftTarget = ExtraBaseLift;
    private bool _extraLiftQueued;
    private const double ExtraBaseLift = 38, ExtraGap = 12, ExtraRight = 52;

    private void OnMusicOverlaySizeChanged(object sender, SizeChangedEventArgs args) => QueueExtraLift();

    private void QueueExtraLift()
    {
        if (_extraLiftQueued || _disposed) return;
        _extraLiftQueued = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            _extraLiftQueued = false;
            UpdateExtraLift();
        }));
    }

    /// <summary>
    /// Keeps the action stack above the player or its track list. The round music button has a fixed slot so
    /// its own appearance animation cannot change the stack's layout.
    /// </summary>
    private void UpdateExtraLift()
    {
        if (_disposed || _extraActions is not { Parent: FrameworkElement layer } actions || _music is null) return;
        double target = ExtraBaseLift;
        if (_musicButton is { IsShown: true } musicButton)
            target = musicButton.View.Height + musicButton.View.Margin.Top + musicButton.View.Margin.Bottom;
        else if (_music.IsDocked && layer.ActualHeight > 0 && View.ActualHeight > 0)
        {
            try
            {
                double obstacleTop = View.TransformToVisual(layer).Transform(new Point(0, View.ActualHeight - View.MusicDock.ActualHeight)).Y;
                if (_music.IsCardOpen && _music.Card.ActualHeight > 0) obstacleTop -= GlassLauncherView.MusicPlaylistGap + _music.Card.ActualHeight;
                target = Math.Max(ExtraBaseLift, layer.ActualHeight - obstacleTop + ExtraGap);
            }
            catch (InvalidOperationException) { }
        }
        if (Math.Abs(target - _extraLiftTarget) < .5) return;
        _extraLiftTarget = target;
        _extraLift ??= new PCLLF.Wpf.Motion.SpringMotion(actions, ExtraBaseLift, value =>
        {
            _extraLiftOffset.Y = ExtraBaseLift - value;
            UpdateToastPlacement();
        }, new PCLLF.Wpf.Motion.SpringParameters(.42, .84)) { MaxFrameSeconds = 1 / 30d };
        _extraLift.Retarget(target);
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
        GlassCommandChrome.SetToolTipScrollTracking(_root, false);
        _visualizer?.Update(false, false, GlassAudioVisualizer.SpectrumStyle.Bars);
        _music?.CloseCard(immediate: true);
        _owner.PreviewMouseDown -= OnMusicOutsideClick;
        _modalView?.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnModalAction));
        _modalEffects?.Dispose();
        if (_backdrop is not null) { _backdrop.StatusChanged -= OnBackdropChanged; _backdrop.Dispose(); }
        _windowRegion?.Dispose();
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnRefreshTick;
        if (_savedAppToolTip && Application.Current is { } app) {
            if (_hadOriginalAppToolTip) app.Resources[typeof(ToolTip)] = _originalAppToolTip;
            else app.Resources.Remove(typeof(ToolTip));
        }
        _homePage?.Dispose();
        foreach (GlassResourceBrowserPage browser in _downloadBrowsers.Values) browser.Dispose();
        _favoritesBrowser?.Dispose();
        foreach (GlassResourceDetailPage details in _resourceDetails.Values) details.Dispose();
        _instanceSelection?.Dispose();
        foreach (GlassLocalResourcesPage page in _localResources.Values) page.Dispose();
        _help?.Dispose();
        _schematicManager?.Dispose();
        _javaCatalogue?.Dispose();
        _instanceJava?.Leave();
        CloseMultiplayer();
        _updatePage?.Dispose();
        _personalizationPage?.Dispose();
        _directMessage?.Dispose();
        _directMessage = null;
        _accountWorkspace?.Dispose();
        foreach (var action in _extraButtons) action.Dispose();
        _extraButtons.Clear();
        _extraLift?.Dispose();
        if (_extraActions is { } actions) actions.SizeChanged -= OnToastObstacleSizeChanged;
        if (_musicButton is { } musicButton) musicButton.View.SizeChanged -= OnToastObstacleSizeChanged;
        if (_music is { } music) music.Card.SizeChanged -= OnMusicOverlaySizeChanged;
        View.MusicDock.SizeChanged -= OnMusicOverlaySizeChanged;
        if (_toasts is { } toasts) { toasts.SizeChanged -= OnToastObstacleSizeChanged; toasts.Dispose(); }
        _runtime?.Dispose();
        if (_runtime is not null) _runtime.AccountRequested -= OnAccountRequested;
        View.ThemeToggleRequested -= OnThemeToggle;
        View.MinimizeRequested -= OnMinimize; View.CloseRequested -= OnClose;
        View.DragRequested -= OnDrag;
        View.RegionsChanged -= OnRegionsChanged;
        _owner.StateChanged -= OnWindowStateChanged;
        _owner.IsVisibleChanged -= OnWindowVisibilityChanged;
        _owner.LocationChanged -= OnWindowLocationChanged;
        _owner.DpiChanged -= OnWindowDpiChanged;
        if (_workAreaCheck?.Status == DispatcherOperationStatus.Pending) _workAreaCheck.Abort();
        _workAreaCheck = null;
        GlassVideoWallpaper.Stop();
        if (_startupPreload) GlassStartupPreload.Cancel();
        _backdropPresence?.Dispose();
        View.Dispose();
        _owner.Loaded -= OnLoaded; _owner.SizeChanged -= OnSizeChanged;
        _owner.Closed -= OnClosed; _owner.Activated -= OnActivated;
    }
}
