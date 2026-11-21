// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCLLF.Wpf.Motion;
using ThemeTransition = PCL.ThemeTransition;
using GlassMusicSurface = PCL.GlassMusicSurface;

namespace PCLLF.Wpf.Glass;

/// <summary>Native visual shell; all launcher operations are supplied by its owner.</summary>
public partial class GlassLauncherView : UserControl, IDisposable
{
    internal static readonly DependencyProperty PreventWindowDragProperty = DependencyProperty.RegisterAttached(
        "PreventWindowDrag", typeof(bool), typeof(GlassLauncherView), new PropertyMetadata(false));

    public const double RailCollapsedWidth = 64;
    public const double RailExpandedWidth = 168;
    public const double RailExpandDelta = RailExpandedWidth - RailCollapsedWidth;
    public const double RailOvershootHeadroom = 12;

    private readonly SpringMotion _expansionMotion;
    private readonly SpringMotion _railMotion;
    private readonly SpringMotion _railExpandMotion;
    private readonly SpringMotion _navigationMotion;
    private readonly SpringMotion _sidebarSelectionMotion;
    private bool _sidebarSelectionVisible;
    // Sidebar selection: a slightly underdamped spring so the overlay settles softly on the new row.
    private static readonly SpringParameters SelectionSpring = new(.3, .82);
    // Floating left rail expansion: fast iOS-style underdamped spring (response 0.25 s, damping 0.84).
    // iOS-like sheet spring: a 0.4 s response with damping 0.78 gives the rail weight and a small, soft overshoot.
    private static readonly SpringParameters RailExpandSpring = new(.4, .78);
    // Page changes: a soft, slightly underdamped spring so the incoming page rises and settles rather than snapping.
    private static readonly SpringParameters PageSpring = new(.46, .86);
    // Pages enter from the direction of travel through the navigation order (down the list: from below).
    private const double PageRise = 22;
    private const double PageExitTravel = 16;
    private const double PageExitScale = .94;
    private const double PageExitEnd = .68;
    private const double PageEntryDelay = .34;
    private const double PageEntryFadeEnd = .86;
    private const double TransitionFrameCap = 1 / 40d;
    // When the section changes, the original pages swap a moment later; titles wait for them so a new title never
    // sits over the previous section's content. The fallback releases them if nothing arrives.
    private bool _titlesPending;
    private DispatcherTimer _titleFallback;
    private double _pageDirection = 1;
    private string _sidebarSignature = string.Empty;
    private int _sidebarSelectedIndex = -1;
    private static readonly int[] NavigationOrder = { 0, 1, 10, 3, 4 };
    private readonly Dictionary<Button, SpringMotion> _navigationHoverMotions = new();
    private readonly SpringMotion _accountMotion;
    private readonly SpringMotion _homeMotion;
    private readonly SpringMotion _pageMotion;
    private readonly SpringMotion _titleMotion;
    private readonly SpringMotion _optionsMotion;
    private GlassLauncherViewState _state = new();
    private bool _updatingState;
    private bool _expanded;
    private bool _railExpanded;
    private bool _lastRailAnimating;
    private double _leftExpansionAllowance;
    private bool _railExpansionAvailable = true;
    private bool _hasSidebar;
    private bool _accountOpen;
    private bool _layoutPending;
    private bool _disposed;
    private bool _keyboardInteraction;
    private double _homeProgress = 1;
    private PageSlot _currentPage, _alternatePage;
    private PagePose _entryStart, _exitStart;
    private double _entryDelay, _transitionDirection;
    private ImageSource _fallbackInstanceIcon;
    private bool _homeTextShadowEnabled;
    private IReadOnlyList<GlassWindowRegion> _lastRegions = Array.Empty<GlassWindowRegion>();
    private FrameworkElement _surfaceMaskHost;
    private SolidColorBrush _wallpaperBaseBrush, _materialFallbackBrush;
    private bool _blendPalette;
    private readonly RectangleGeometry _bodyContentClip = new();
    private readonly RectangleGeometry _controlsWallpaperClip = new();
    private (Rect Body, Rect Cutout, double BodyRadius, double CutoutRadius, Rect Dock)? _lastBodyClipKey;
    // The body's notch under the window buttons (BodyFrame coordinates); the splash cuts it too, so the buttons' own
    // frosted slice looks the same during the splash and after it (no jump when the normal clip takes over).
    private (Rect Rect, double Radius)? _controlsCutout;
    private (bool DarkText, bool Wallpaper, bool Desktop, double Density, bool HighContrast, bool IsHome, Color? Hue)? _lastPalette;

    public event EventHandler StartRequested;
    public event EventHandler CancelRequested;
    public event EventHandler SelectInstanceRequested;
    public event EventHandler InstanceSettingsRequested;
    public event EventHandler AccountRequested;
    public event EventHandler ThemeToggleRequested;
    public event EventHandler MinimizeRequested;
    public event EventHandler CloseRequested;
    public event EventHandler RecentWorldRequested;
    public event EventHandler BackRequested;
    public event EventHandler DragRequested;
    public event EventHandler RegionsChanged;
    public event Action<bool> DependencyCheckChanged;
    public event Action<bool> AutoFillChanged;
    public event Action<int> NavigationRequested;
    internal event Action<UIElement> PageContentReleased;

    public ContentControl PageContentHost => _currentPage.Host;
    public bool IsRailExpanded => _railExpanded;
    public bool IsRailAnimating => _railExpandMotion.IsRunning;
    public double LeftExpansionAllowance => _leftExpansionAllowance;
    private bool IsCompactViewport => ActualWidth - _leftExpansionAllowance < 960;
    /// <summary>True while a page transition, rail expansion or entrance cascade is playing; owners can defer heavy refreshes.</summary>
    public bool IsTransitioning => _pageMotion.IsRunning || _homeMotion.IsRunning || _railExpandMotion.IsRunning || (_workspaceMotion?.IsRunning == true) || StaggeredEntrance.IsPlaying;
    /// <summary>True between a section change and the arrival of that section's page; the current page should stay as displayed.</summary>
    public bool IsAwaitingSection => _titlesPending;
    public Geometry SurfaceClip => FrameClip;
    public Brush SurfaceOpacityMask => FrameMask;

    public void SetLeftExpansionAllowance(double allowance)
    {
        double clamped = Math.Max(0, allowance);
        if (Math.Abs(_leftExpansionAllowance - clamped) < .01) return;
        _leftExpansionAllowance = clamped;
        NavigationColumn.Width = clamped > 0 ? new GridLength(RailCollapsedWidth + clamped) : GridLength.Auto;
        AccountPopover.Margin = new Thickness(84 + clamped, 0, 0, 16);
        SyncSurfaceRegions();
        ScheduleLayout();
    }

    internal void SetRailExpansionAvailable(bool available)
    {
        if (_railExpansionAvailable == available) return;
        _railExpansionAvailable = available;
        RailToggleButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) SetRailExpanded(false, immediate: true);
    }

    public void SetRailExpanded(bool expanded, bool immediate = false)
    {
        if (expanded && !_railExpansionAvailable) return;
        if (_railExpanded == expanded && !immediate) return;
        _railExpanded = expanded;
        string actionText = expanded ? "收起导航栏" : "展开导航栏";
        RailToggleButton.ToolTip = actionText;
        AutomationProperties.SetName(RailToggleButton, actionText);
        UpdateRailToolTips();
        _railExpandMotion.Retarget(expanded ? 1 : 0, immediate || ShouldSnapMotion);
        NotifyRegionsChanged();
    }

    private void UpdateRailToolTips()
    {
        bool showTips = !_railExpanded;
        foreach (Button button in new[] { NavLaunch, NavDownload, NavFavorites, NavSettings, NavOther, RailToggleButton, AccountButton })
            ToolTipService.SetIsEnabled(button, showTips);
    }

    public void SetSurfaceMaskHost(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_surfaceMaskHost != null && !ReferenceEquals(_surfaceMaskHost, host))
        {
            _surfaceMaskHost.OpacityMask = null;
            if (_materialFallbackBrush is not null)
            {
                _surfaceMaskHost.ClearValue(GlassMaterial.FallbackBrushProperty);
                _surfaceMaskHost.ClearValue(GlassMaterial.FallbackColorProperty);
            }
        }
        _surfaceMaskHost = host;
        host.OpacityMask = FrameMask;
        if (ThemeTransition.PaletteTarget(_materialFallbackBrush) is Color color) SetMaterialFallback(color);
        UpdateWallpaperClip();
    }

    public GlassLauncherView()
    {
        InitializeComponent();
        SidebarScroller.Style = global::PCL.GlassPresentationSession.ShellScrollStyle;
        SidebarScroller.SetResourceReference(Control.ForegroundProperty, "GlassSecondaryTextBrush");
        _surfaceMaskHost = MainSurface;
        _expansionMotion = new SpringMotion(this, 0, ApplyExpansion, MotionTokens.Spatial);
        _railMotion = new SpringMotion(this, 0, ApplyRail, MotionTokens.Spatial);
        _railExpandMotion = new SpringMotion(this, 0, ApplyRailExpansion, RailExpandSpring);
        _navigationMotion = new SpringMotion(this, 0, value => NavigationTranslation.Y = value, MotionTokens.Reveal);
        _sidebarSelectionMotion = new SpringMotion(this, 0, value => SidebarSelectionTranslation.Y = value, SelectionSpring);
        // The sidebar list fades out softly at its top and bottom edges while it can scroll, like the page content.
        SideRail.Child = null;
        SideRail.Child = new ScrollEdgeFade(SidebarScroller, 36, 36);
        // Transitions pause rather than skip when the UI thread stalls (page construction, list loading).
        _sidebarSelectionMotion.MaxFrameSeconds = TransitionFrameCap;
        _accountMotion = new SpringMotion(this, 0, ApplyAccountPopover, MotionTokens.Reveal);
        _homeMotion = new SpringMotion(this, 1, ApplyHomeTransition, MotionTokens.Reveal);
        _currentPage = new PageSlot(PageContent);
        _alternatePage = new PageSlot(AlternatePageContent);
        _pageMotion = new SpringMotion(this, 1, ApplyPageTransition, PageSpring) { MaxFrameSeconds = TransitionFrameCap };
        _titleMotion = new SpringMotion(this, 0, ApplyTitleTransition, MotionTokens.Reveal);
        _optionsMotion = new SpringMotion(this, 1, ApplyOptionsAvailability, MotionTokens.Reveal);
        Loaded += OnLoaded;
        // Start the splash in the same pass the view loads (after OnLoaded has the logo), so the first frame the window
        // shows is already the splash; if layout is not ready yet, try again right after it.
        Loaded += (_, _) =>
        {
            StartSplash();
            if (_splashRequested) Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
                StartSplash();
                if (_splashRequested) AbortSplash();
            }));
        };
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnViewIsVisibleChanged;
        SizeChanged += OnSizeChanged;
        MainSurface.SizeChanged += OnSurfaceGeometryChanged;
        NavigationSurface.SizeChanged += OnSurfaceGeometryChanged;
        BodyFrame.SizeChanged += OnSurfaceGeometryChanged;
        BodyContentRoot.SizeChanged += OnSurfaceGeometryChanged;
        WindowControlsSurface.SizeChanged += OnSurfaceGeometryChanged;
        WindowControlsWallpaperFrost.SizeChanged += OnSurfaceGeometryChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        UpdateRailToolTips();
        SetState(_state);
    }

    public void SetState(GlassLauncherViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool optionsAvailabilityChanged = _state.LaunchOptionsAvailable != state.LaunchOptionsAvailable;
        bool homeChanged = _state.IsHome != state.IsHome;
        bool navigationChanged = _state.SelectedNavigationId != state.SelectedNavigationId;
        bool titleChanged = _state.CanGoBack != state.CanGoBack;
        bool layoutDirty = homeChanged || navigationChanged ||
            _state.IsLaunching != state.IsLaunching ||
            _state.HasInstance != state.HasInstance ||
            _state.LaunchOptionsAvailable != state.LaunchOptionsAvailable ||
            _state.RecentWorldAvailable != state.RecentWorldAvailable ||
            _state.ReduceMotion != state.ReduceMotion ||
            string.IsNullOrWhiteSpace(_state.LaunchStatus) != string.IsNullOrWhiteSpace(state.LaunchStatus) ||
            string.IsNullOrWhiteSpace(_state.InstanceDescription) != string.IsNullOrWhiteSpace(state.InstanceDescription);
        if (navigationChanged && _workspaceOpen)
            SetAccountWorkspaceOpen(false);
        if (navigationChanged && !state.IsHome && !_state.IsHome && !ShouldSnapMotion)
            HoldTitles();
        if (navigationChanged)
            _pageDirection = Array.IndexOf(NavigationOrder, state.SelectedNavigationId) >= Array.IndexOf(NavigationOrder, _state.SelectedNavigationId) ? 1 : -1;
        _state = state;
        if (homeChanged && ActualWidth > 0)
        {
            bool compact = IsCompactViewport;
            double columnWidth = SecondaryWidth(state.IsHome || _workspaceOpen, compact);
            // Page motion already runs on render transforms. Resizing this column throughout it remeasures both pages.
            SetSecondaryColumnWidth(columnWidth, animate: false);
        }
        _updatingState = true;
        try
        {
            if (homeChanged)
            {
                ApplyRail(_railMotion.Value);
                SideRail.IsHitTestVisible = _hasSidebar && !state.IsHome;
                _homeMotion.Retarget(state.IsHome ? 1 : 0, ShouldSnapMotion);
            }
            // The panel flips the moment navigation changes, in the same frame as the rail selection. A non-home page's
            // rows arrive a little later; until then the old rows are hidden so the panel never shows stale content.
            if (navigationChanged)
            {
                StartSidebarFlip();
                if (!state.IsHome) HideSidebarRowsUntilSection();
            }
            if (titleChanged) _titleMotion.Retarget(state.CanGoBack ? 1 : 0, ShouldSnapMotion);
            ApplyHomeTransition(_homeMotion.Value);
            ApplyTitleTransition(_titleMotion.Value);
            if (!_titlesPending || state.IsHome) ApplyTitles();
            AccountNameText.Text = string.IsNullOrWhiteSpace(state.AccountName) ? "未登录" : state.AccountName;
            AccountButton.ToolTip = AccountNameText.Text;
            AutomationProperties.SetName(AccountButton, "当前账户：" + AccountNameText.Text);
            ImageSource avatar = state.AccountAvatar;
            AccountAvatarImage.Source = avatar;
            AccountFallbackGlyph.Visibility = avatar is null ? Visibility.Visible : Visibility.Collapsed;

            InstanceImage.Source = state.InstanceIcon ?? GetFallbackInstanceIcon();
            InstanceImage.Visibility = Visibility.Visible;
            InstanceNameText.Text = string.IsNullOrWhiteSpace(state.InstanceName) ? "选择实例" : state.InstanceName;
            InstanceNameText.ToolTip = InstanceNameText.Text;
            InstanceDescriptionText.Text = state.InstanceDescription ?? string.Empty;
            InstanceDescriptionText.Visibility = state.IsLaunching || string.IsNullOrWhiteSpace(state.InstanceDescription) ? Visibility.Collapsed : Visibility.Visible;
            InstanceSettingsButton.IsEnabled = state.HasInstance && !state.IsLaunching;
            SelectInstanceButton.IsEnabled = !state.IsLaunching;
            SelectInstanceButton.Visibility = state.IsHome ? Visibility.Visible : Visibility.Hidden;
            InstanceSettingsButton.Visibility = state.IsHome ? Visibility.Visible : Visibility.Hidden;
            // The footer row only carries the home actions; elsewhere the sidebar list reaches down to the panel edge.
            if (PrimaryActions.Parent is Grid secondaryGrid && secondaryGrid.RowDefinitions.Count > 2)
                secondaryGrid.RowDefinitions[2].Height = new GridLength(state.IsHome ? 52 : 12);
            ExpandButtonContainer.Visibility = state.IsHome ? Visibility.Visible : Visibility.Hidden;
            LaunchButton.IsEnabled = state.IsLaunching || state.CanLaunch;
            LaunchButtonText.Text = state.IsLaunching ? "取消启动" : string.IsNullOrWhiteSpace(state.LaunchText) ? "启动游戏" : state.LaunchText;
            LaunchGlyph.Data = state.IsLaunching ? StopGlyph : (Geometry)Resources["IconPlay"];
            AutomationProperties.SetName(LaunchButton, LaunchButtonText.Text);
            LaunchStatusText.Text = state.LaunchStatus ?? string.Empty;
            // Launch progress uses the same tide as signing in, on the launch card; the caption is the step in progress.
            EnsureLaunchTide().Update(state.IsHome && state.IsLaunching, state.LaunchProgress,
                string.IsNullOrWhiteSpace(state.LaunchStatus) ? "正在启动游戏" : state.LaunchStatus);
            LaunchStatusText.Visibility = state.IsLaunching && !string.IsNullOrWhiteSpace(state.LaunchStatus) ? Visibility.Visible : Visibility.Collapsed;
            // While launching, the button itself fills with progress (a hard stop between full and dimmed accent).
            LaunchProgressBar.Visibility = Visibility.Collapsed;
            LaunchProgressBar.Value = Math.Clamp(state.LaunchProgress ?? 0, 0, 1);
            if (state.IsLaunching && state.LaunchProgress is double done && TryFindResource("GlassAccentBrush") is SolidColorBrush accent)
            {
                double p = Math.Clamp(done, 0, 1);
                Color dim = Color.FromArgb((byte)(accent.Color.A * .55), accent.Color.R, accent.Color.G, accent.Color.B);
                LaunchButton.Background = new LinearGradientBrush(new GradientStopCollection {
                    new(accent.Color, 0), new(accent.Color, p), new(dim, p), new(dim, 1) }, new Point(0, .5), new Point(1, .5));
            }
            else LaunchButton.ClearValue(BackgroundProperty);
            ExpandButton.IsEnabled = state.LaunchOptionsAvailable;
            // Recomputed on every state change: it used to update only when option availability animated, so leaving and
            // re-entering the home page could keep the plus non-interactive although it was shown as available.
            ExpandButtonContainer.IsHitTestVisible = state.IsHome && state.LaunchOptionsAvailable;
            if (optionsAvailabilityChanged)
            {
                _expansionMotion.Retarget(_expanded && state.LaunchOptionsAvailable ? 1 : 0, ShouldSnapMotion);
                _optionsMotion.Retarget(state.LaunchOptionsAvailable ? 1 : 0, ShouldSnapMotion);
            }

            ToggleFeedback.SetReduceMotion(DependencyCheckBox, state.ReduceMotion);
            ToggleFeedback.SetReduceMotion(AutoFillCheckBox, state.ReduceMotion);
            DependencyCheckBox.IsChecked = state.DependencyCheckEnabled;
            AutoFillCheckBox.IsChecked = state.AutoFillEnabled;
            RecentWorldButton.Visibility = state.RecentWorldAvailable ? Visibility.Visible : Visibility.Collapsed;
            RecentWorldButton.IsEnabled = state.RecentWorldAvailable && state.CanLaunch && !state.IsLaunching;
            // Without a recent world the row simply disappears instead of showing an empty placeholder under the launch button.
            NoRecentWorldText.Visibility = Visibility.Collapsed;
            RecentWorldCard.Visibility = state.RecentWorldAvailable ? Visibility.Visible : Visibility.Collapsed;
            RecentWorldButton.ToolTip = string.IsNullOrWhiteSpace(state.RecentWorldDetail) ? "继续游玩" : state.RecentWorldDetail;
            RecentWorldNameText.Text = state.RecentWorldName ?? string.Empty;
            RecentWorldDetailText.Text = state.RecentWorldDetail ?? string.Empty;
            RecentWorldImage.Source = state.RecentWorldIcon;
            WorldFallbackGlyph.Visibility = state.RecentWorldIcon is null ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(RecentWorldButton, "继续游玩：" + (state.RecentWorldName ?? string.Empty));
            ApplyPalette(state.UseDarkText);
            WorkspaceHint.Text = "页面选项";
            WorkspaceHint.Visibility = state.IsHome || _hasSidebar ? Visibility.Hidden : Visibility.Visible;
            SetSelectedNavigation(ShouldSnapMotion);
            if (ShouldSnapMotion)
            {
                _expansionMotion.Snap(_expanded && state.LaunchOptionsAvailable ? 1 : 0);
                _railMotion.Snap(_hasSidebar ? 1 : 0);
                _accountMotion.Snap(_accountOpen ? 1 : 0);
                _homeMotion.Snap(state.IsHome ? 1 : 0);
                _titleMotion.Snap(state.CanGoBack ? 1 : 0);
                _optionsMotion.Snap(state.LaunchOptionsAvailable ? 1 : 0);
                _pageMotion.Snap(1);
            }
        }
        finally
        {
            _updatingState = false;
        }
        if (layoutDirty) ScheduleLayout();
    }

    private void ApplyTitles()
    {
        string title = _state.IsHome ? string.Empty : (string.IsNullOrWhiteSpace(_state.PageTitle) ? NavigationTitle(_state.SelectedNavigationId) : _state.PageTitle);
        if (PageTitleText.Text != title) ChangePageTitle(title);
        UpdateHeaderLayout();
        SecondaryTitleText.Text = _state.IsHome ? "主页" : NavigationTitle(_state.SelectedNavigationId);
        SecondaryTitleText.FontSize = _state.IsHome ? 24 : 20;
    }

    // The outgoing title rises and fades out on a ghost copy while the new one rises in from below.
    private TextBlock _titleGhost;
    private readonly TranslateTransform _titleIn = new(), _titleOut = new();
    private SpringMotion _titleSwapMotion;
    private void ChangePageTitle(string title)
    {
        string previous = PageTitleText.Text;
        PageTitleText.Text = title;
        if (ShouldSnapMotion || string.IsNullOrEmpty(previous) && string.IsNullOrEmpty(title)) { _titleSwapMotion?.Snap(1); return; }
        if (_titleGhost is null)
        {
            _titleGhost = new TextBlock { Style = PageTitleText.Style, FontFamily = PageTitleText.FontFamily, FontSize = PageTitleText.FontSize, FontWeight = PageTitleText.FontWeight,
                VerticalAlignment = PageTitleText.VerticalAlignment, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false, RenderTransform = _titleOut };
            Grid.SetColumn(_titleGhost, Grid.GetColumn(PageTitleText));
            ((Panel)PageTitleText.Parent).Children.Add(_titleGhost);
            PageTitleText.RenderTransform = _titleIn;
            _titleSwapMotion = new SpringMotion(this, 1, ApplyTitleSwap, new SpringParameters(.32, .9)) { MaxFrameSeconds = TransitionFrameCap };
        }
        _titleGhost.Text = previous;
        _titleSwapMotion.Snap(0);
        _titleSwapMotion.Retarget(1);
    }
    private void ApplyTitleSwap(double value)
    {
        double outgoing = Math.Clamp(value / .45, 0, 1), incoming = Math.Clamp(value, 0, 1);
        if (_titleGhost != null)
        {
            _titleGhost.Opacity = 1 - outgoing;
            _titleOut.Y = -10 * outgoing;
            // Collapsed (not Hidden) once gone: the ghost shares the title's auto-sized cell, and a hidden ghost would
            // keep the frosted title backing at the previous, longer title's width.
            _titleGhost.Visibility = outgoing >= 1 ? Visibility.Collapsed : Visibility.Visible;
        }
        PageTitleText.Opacity = incoming * incoming * (3 - 2 * incoming);
        _titleIn.Y = 12 * (1 - value);
    }

    private void HoldTitles()
    {
        _titlesPending = true;
        _titleFallback ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (_, _) => ReleaseTitles(), Dispatcher) { IsEnabled = false };
        _titleFallback.Stop();
        _titleFallback.Start();
    }

    private void ReleaseTitles()
    {
        _titleFallback?.Stop();
        if (!_titlesPending) return;
        _titlesPending = false;
        ApplyTitles();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Geometry> FrozenGlyphCache = new(StringComparer.Ordinal);
    private static readonly Geometry StopGlyph = GetCachedGlyph("M5,5 H19 V19 H5 Z");

    private static Geometry GetCachedGlyph(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        return FrozenGlyphCache.GetOrAdd(data, static raw =>
        {
            try
            {
                Geometry parsed = Geometry.Parse(raw);
                if (parsed.Bounds.IsEmpty) return null;
                if (parsed.CanFreeze) parsed.Freeze();
                return parsed;
            }
            catch (FormatException)
            {
                return null;
            }
        });
    }

    public void SetPageContent(UIElement content)
    {
        if (ReferenceEquals(PageContentHost.Content, content)) return;
        ReleaseTitles();
        bool animate = !ShouldSnapMotion && content is not null;
        bool returning = content is not null && ReferenceEquals(_alternatePage.Host.Content, content);
        // If the replacement arrives mid-transition, keep whichever old page the user can still see.
        if (returning || animate && _currentPage.Host.Content is not null &&
            (_alternatePage.Host.Content is null || _currentPage.Pose.Opacity >= _alternatePage.Pose.Opacity))
            (_currentPage, _alternatePage) = (_alternatePage, _currentPage);

        if (!returning)
        {
            ReleasePage(_currentPage);
            _currentPage.Host.Content = content;
            _currentPage.Apply(animate ? new PagePose(0, MotionTokens.RevealScale, PageRise * _pageDirection) : PagePose.Rest);
        }
        Panel.SetZIndex(_currentPage.Host, 1);
        Panel.SetZIndex(_alternatePage.Host, 0);
        _currentPage.Host.Visibility = content is null ? Visibility.Hidden : Visibility.Visible;
        _currentPage.SetInputEnabled(content is not null);
        _alternatePage.SetInputEnabled(false);
        if (_alternatePage.Host.IsKeyboardFocusWithin && content is not null) _currentPage.Host.Focus();

        _entryStart = _currentPage.Pose;
        _exitStart = _alternatePage.Pose;
        _transitionDirection = _pageDirection;
        _entryDelay = !returning && _exitStart.Opacity > MotionTokens.VisibilityThreshold && _alternatePage.Host.Content is not null ? PageEntryDelay : 0;
        if (!animate) { _pageMotion.Snap(1); return; }
        _pageMotion.Snap(0);
        _pageMotion.Retarget(1);
    }

    private void ReleasePage(PageSlot page)
    {
        if (page.Host.Content is not UIElement content) return;
        page.Host.Content = null;
        page.Host.Visibility = Visibility.Hidden;
        page.SetInputEnabled(false);
        PageContentReleased?.Invoke(content);
    }

    private readonly record struct PagePose(double Opacity, double Scale, double Offset)
    {
        internal static readonly PagePose Rest = new(1, 1, 0);
    }

    private sealed class PageSlot
    {
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _offset = new();
        internal ContentControl Host { get; }
        internal PagePose Pose { get; private set; } = PagePose.Rest;

        internal PageSlot(ContentControl host)
        {
            Host = host;
            host.RenderTransformOrigin = new Point(.5, .42);
            host.RenderTransform = new TransformGroup { Children = { _scale, _offset } };
        }

        internal void Apply(PagePose pose)
        {
            Pose = pose;
            Host.Opacity = pose.Opacity;
            _scale.ScaleX = _scale.ScaleY = pose.Scale;
            _offset.Y = pose.Offset;
        }

        internal void SetInputEnabled(bool enabled)
        {
            Host.IsHitTestVisible = enabled;
            KeyboardNavigation.SetTabNavigation(Host, enabled ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
            KeyboardNavigation.SetDirectionalNavigation(Host, enabled ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        }
    }

    private UIElement _sidebarCustom;
    private global::PCL.GlassTideProgress _sidebarTide;

    internal void SetSidebarProgress(double? progress)
    {
        if (_sidebarTide is null)
        {
            if (!progress.HasValue || SecondarySurface.Child is not Grid card) return;
            _sidebarTide = new global::PCL.GlassTideProgress(SecondarySurface.CornerRadius.TopLeft)
            {
                BackgroundOnly = true,
                Margin = new Thickness(-card.Margin.Left, 0, -card.Margin.Right, 0)
            };
            Grid.SetRowSpan(_sidebarTide, card.RowDefinitions.Count);
            card.Children.Insert(0, _sidebarTide);
        }
        _sidebarTide.Update(progress.HasValue, progress, string.Empty);
    }

    /// <summary>
    /// Page-specific content shown in the sidebar below its rows (for example the task manager's progress and rates).
    /// Pass null to remove it.
    /// </summary>
    public void SetSidebarCustom(UIElement custom)
    {
        if (ReferenceEquals(_sidebarCustom, custom)) return;
        if (_sidebarCustom is not null) SidebarPanel.Children.Remove(_sidebarCustom);
        _sidebarCustom = custom;
        // Custom content is sized to the visible sidebar, so the sidebar itself must not scroll (it would only nudge it).
        SidebarScroller.VerticalScrollBarVisibility = custom is null ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (custom is null) return;
        // Custom content may fill the sidebar: it is at least as tall as the visible area.
        if (custom is FrameworkElement fill && fill.ReadLocalValue(MinHeightProperty) == DependencyProperty.UnsetValue)
            fill.SetBinding(MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = SidebarScroller });
        SidebarPanel.Children.Add(custom);
        // A page whose sidebar is only custom content has no rows to wait for.
        RevealSidebarRows();
    }

    public void SetSidebar(IEnumerable<GlassSidebarItem> items)
    {
        GlassSidebarItem[] list = (items ?? Enumerable.Empty<GlassSidebarItem>()).ToArray();
        // A new set of rows (another section) enters with a staggered cascade; a selection change within the same
        // rows only moves the overlay and decides which way the next page enters.
        string signature = string.Join("\u001f", list.Select(item => item.Label));
        int selected = Array.FindIndex(list, item => item.IsSelected);
        bool newSection = signature != _sidebarSignature;
        if (!newSection && selected >= 0 && _sidebarSelectedIndex >= 0 && selected != _sidebarSelectedIndex)
            _pageDirection = selected > _sidebarSelectedIndex ? 1 : -1;
        _sidebarSignature = signature;
        _sidebarSelectedIndex = selected;

        // An empty interim list (the original left page swapping) is not the new section arriving.
        if (newSection && list.Length > 0) ReleaseTitles();
        SidebarPanel.Children.Clear();
        bool first = true;
        foreach (GlassSidebarItem item in list)
        {
            SidebarPanel.Children.Add(item.IsHeader ? SidebarHeader(item, first) : SidebarRow(item));
            first = false;
        }
        if (_sidebarCustom is not null) SidebarPanel.Children.Add(_sidebarCustom);
        UpdateSidebarAvailability();
        ScheduleLayout();
        // New rows start hidden in the same pass they are added, so they never flash before the cascade.
        // The flip already started on navigation; the new rows just fade in on the moving panel.
        if (newSection && list.Length > 0) RevealSidebarRows();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdateSidebarSelection(ShouldSnapMotion)));
    }

    // iOS-style spring for the sidebar flip: response 0.42 s, damping 0.8 (a short, soft settle with a hint of overshoot).
    private static readonly SpringParameters SidebarFlipSpring = new(.42, .8);
    private const double SidebarFlipTravel = 56;
    private readonly TranslateTransform _sidebarFlipOffset = new();
    private SpringMotion? _sidebarFlipMotion;
    private double _sidebarFlipDirection = 1;

    private void StartSidebarFlip()
    {
        // The whole sidebar panel (surface, title, rows and actions) moves as one card.
        // Do not attach a BitmapCache to SecondarySurface: WallpaperBlurCache already holds the pre-blurred texture,
        // whereas BitmapCache would freeze a displaced wallpaper slice onto the card instead of sampling live coordinates.
        SecondarySurface.RenderTransform = new TransformGroup { Children = { _sidebarFlipOffset, _secondaryShift } }; // flip offset + account-page drift
        _sidebarFlipMotion ??= new SpringMotion(this, 1, ApplySidebarFlip, SidebarFlipSpring) { MaxFrameSeconds = TransitionFrameCap };
        _sidebarFlipDirection = _pageDirection;
        if (ShouldSnapMotion) { _sidebarFlipMotion.Snap(1); return; }
        // Start from the offset position (keeping any in-flight velocity) and spring to rest.
        _sidebarFlipMotion.Snap(0);
        _sidebarFlipMotion.Retarget(1);
    }

    private DispatcherTimer? _sidebarRowsFallback;

    private void HideSidebarRowsUntilSection()
    {
        if (ShouldSnapMotion) return;
        SidebarHost.BeginAnimation(OpacityProperty, null);
        SidebarHost.Opacity = 0;
        _sidebarRowsFallback ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (_, _) => RevealSidebarRows(), Dispatcher) { IsEnabled = false };
        _sidebarRowsFallback.Stop(); _sidebarRowsFallback.Start(); // a section that never changes must not stay hidden
    }

    private void RevealSidebarRows()
    {
        _sidebarRowsFallback?.Stop();
        if (SidebarHost.Opacity >= 1 && SidebarHost.HasAnimatedProperties == false) return;
        if (ShouldSnapMotion) { SidebarHost.BeginAnimation(OpacityProperty, null); SidebarHost.Opacity = 1; return; }
        SidebarHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplySidebarFlip(double value)
    {
        // Position follows the spring including its overshoot; the panel never fully vanishes (it is the same card
        // flipping to new content), it dips to 40 % and recovers over the first ~60 % of the travel.
        _sidebarFlipOffset.Y = SidebarFlipTravel * _sidebarFlipDirection * (1 - value);
        double fade = Math.Clamp(value / .6, 0, 1);
        SecondarySurface.Opacity = .4 + .6 * (1 - Math.Pow(1 - fade, 2));
    }

    private const string SelectedRowTag = "SidebarSelectedRow";

    // Category caption: small secondary text that groups the rows below it (Fluent NavigationView header style).
    private FrameworkElement SidebarHeader(GlassSidebarItem item, bool first)
    {
        var caption = new TextBlock
        {
            Text = item.Label ?? string.Empty, FontSize = 12, Margin = new Thickness(12, first ? 2 : 10, 12, 4),
            TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["GlassText"],
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush");
        AutomationProperties.SetHeadingLevel(caption, AutomationHeadingLevel.Level3);
        return caption;
    }

    private FrameworkElement SidebarRow(GlassSidebarItem item)
    {
        var button = new Button
        {
            Style = (Style)Resources["GlassButton"],
            Height = 33,
            MinHeight = 33,
            Padding = new Thickness(11, 4, 11, 4),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = item.IsEnabled,
            ToolTip = string.IsNullOrWhiteSpace(item.ToolTip) || item.ToolTip == item.Label ? null : item.ToolTip,
        };
        AutomationProperties.SetName(button, item.Label ?? string.Empty);
        AutomationProperties.SetHelpText(button, item.ToolTip ?? string.Empty);
        var contents = new StackPanel { Orientation = Orientation.Horizontal };
        if (item.Image != null)
        {
            contents.Children.Add(new Image { Source = item.Image, Width = 17, Height = 17, Margin = new Thickness(0, 0, 10, 0), Stretch = Stretch.Uniform });
        }
        else if (SidebarGlyph(item.IconData, 16) is Path path)
        {
            path.Margin = new Thickness(0, 0, 10, 0);
            contents.Children.Add(path);
        }
        var label = new TextBlock { Text = item.Label ?? string.Empty, FontSize = item.IsCommand ? 13.5 : 14.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["GlassText"] };
        if (item.IsCommand) label.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush");
        else if (item.IsSelected) label.SetResourceReference(TextBlock.ForegroundProperty, "GlassAccentBrush");
        contents.Children.Add(label);
        button.Content = contents;
        button.Click += (_, _) => item.Invoked?.Invoke();
        if (item.ContextRequested is { } context)
            button.MouseRightButtonUp += (_, args) => { args.Handled = true; context(button); };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 2), Tag = item.IsSelected ? SelectedRowTag : null };
        row.Children.Add(button);
        if (item.Actions.Count > 0)
        {
            // Accessories (refresh, configure) appear while the row is hovered or keyboard-focused, like the original list.
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0), Opacity = 0 };
            foreach (GlassSidebarAction action in item.Actions)
            {
                var accessory = new Button { Style = (Style)Resources["GlassCircleButton"], Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(6), ToolTip = action.ToolTip, Content = SidebarGlyph(action.IconData, 14) };
                AutomationProperties.SetName(accessory, $"{item.Label}：{action.ToolTip}");
                accessory.Click += (_, _) => action.Invoked?.Invoke();
                actions.Children.Add(accessory);
            }
            row.Children.Add(actions);
            void Reveal() => actions.Opacity = row.IsMouseOver || row.IsKeyboardFocusWithin ? 1 : 0;
            row.MouseEnter += (_, _) => Reveal();
            row.MouseLeave += (_, _) => Reveal();
            row.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        }
        return row;
    }

    private static Path SidebarGlyph(string data, double size)
    {
        Geometry geometry = GetCachedGlyph(data);
        if (geometry == null) return null;
        var path = new Path { Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
        return path;
    }

    private void UpdateSidebarAvailability()
    {
        _hasSidebar = SidebarPanel.Children.Count > 0;
        WorkspaceHint.Visibility = _hasSidebar || _state.IsHome ? Visibility.Hidden : Visibility.Visible;
        if (_hasSidebar && !_state.IsHome) SideRail.Visibility = Visibility.Visible;
        SideRail.IsHitTestVisible = _hasSidebar && !_state.IsHome;
        _railMotion.Retarget(_hasSidebar ? 1 : 0, ShouldSnapMotion);
    }

    public void SetAccountContent(UIElement content)
    {
        if (!ReferenceEquals(AccountContent.Content, content)) AccountContent.Content = content;
        if (content is null) SetAccountOpen(false);
    }

    public void SetAccountOpen(bool isOpen)
        => SetAccountOpenCore(isOpen, ShouldSnapMotion);

    private void SetAccountOpenCore(bool isOpen, bool immediate)
    {
        _accountOpen = isOpen && AccountContent.Content != null;
        if (_accountOpen) AccountPopover.Visibility = Visibility.Visible;
        AccountPopover.IsHitTestVisible = _accountOpen;
        _accountMotion.Retarget(_accountOpen ? 1 : 0, immediate);
        AutomationProperties.SetHelpText(AccountButton, _accountOpen ? "账户面板已展开" : "打开账户面板");
        if (_accountOpen)
            AccountPopover.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        ScheduleLayout();
    }


    private Color? _wallpaperHue;

    // Dominant colour of the wallpaper: render it tiny and average pixels weighted by saturation, so vivid areas
    // define the hue rather than large grey regions.
    private static Color? SampleWallpaperColor(ImageSource source)
    {
        try
        {
            if (source is BitmapSource bitmapSource && TrySampleBitmapRows(bitmapSource, out Color sampled))
                return sampled;

            const int width = 32, height = 20;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
            using (DrawingContext context = visual.RenderOpen()) context.DrawImage(source, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 16) continue;
                double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double weight = .15 + (max - min) / 255d; // saturation-weighted
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total <= 0) return null;
            return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool TrySampleBitmapRows(BitmapSource bitmap, out Color color)
    {
        color = default;
        int pw = bitmap.PixelWidth, ph = bitmap.PixelHeight;
        if (pw <= 0 || ph <= 0) return false;
        PixelFormat fmt = bitmap.Format;
        bool is32 = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Bgr32;
        bool is24 = fmt == PixelFormats.Bgr24;
        if (!is32 && !is24) return false;

        int bpp = is32 ? 4 : 3;
        bool hasAlpha = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32;
        bool premultiplied = fmt == PixelFormats.Pbgra32;
        int stride = pw * bpp;
        byte[] row = new byte[stride];
        int rows = Math.Min(20, ph);
        int cols = Math.Min(32, pw);
        double r = 0, g = 0, b = 0, total = 0;

        for (int ry = 0; ry < rows; ry++)
        {
            int sy = (int)(((ry + 0.5) * ph) / rows);
            if (sy >= ph) sy = ph - 1;
            bitmap.CopyPixels(new Int32Rect(0, sy, pw, 1), row, stride, 0);
            for (int rx = 0; rx < cols; rx++)
            {
                int sx = (int)(((rx + 0.5) * pw) / cols);
                if (sx >= pw) sx = pw - 1;
                int offset = sx * bpp;
                byte alpha = hasAlpha ? row[offset + 3] : (byte)255;
                if (alpha < 16) continue;
                double pb = row[offset], pg = row[offset + 1], pr = row[offset + 2];
                if (premultiplied && alpha < 255)
                {
                    double invA = 255d / alpha;
                    pb = Math.Min(255, pb * invA);
                    pg = Math.Min(255, pg * invA);
                    pr = Math.Min(255, pr * invA);
                }
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double weight = .15 + (max - min) / 255d;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
        }
        if (total <= 0) return false;
        byte cr = (byte)Math.Clamp((int)Math.Round(r / total), 0, 255);
        byte cg = (byte)Math.Clamp((int)Math.Round(g / total), 0, 255);
        byte cb = (byte)Math.Clamp((int)Math.Round(b / total), 0, 255);
        color = Color.FromRgb(cr, cg, cb);
        return true;
    }

    // Tonal tint: keep the hue, soften saturation, and set lightness for the theme (dark: deep tone, light: pale tone).
    private static Color TonalTint(Color color, bool darkText)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min, hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = (g - b) / delta % 6; else if (max == g) hue = (b - r) / delta + 2; else hue = (r - g) / delta + 4;
            hue *= 60; if (hue < 0) hue += 360;
        }
        double lightness = (max + min) / 2, saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        double s = Math.Min(saturation, .42), l = darkText ? .88 : .2;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = l - c / 2;
        (double rr, double gg, double bb) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        // Translucent: the rail only tints what lies beneath it.
        return Color.FromArgb(darkText ? (byte)120 : (byte)110, (byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
    }

    public void SetWallpaper(ImageSource source) => SetWallpaperDisplay(source, source);

    internal void SetWallpaperDisplay(ImageSource source, ImageSource? materialSource)
    {
        WallpaperImage.Source = source;
        WallpaperImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        _wallpaperHue = source is not null && ReferenceEquals(source, materialSource) ? SampleWallpaperColor(source) : null;
        GlassMaterial.SetIsEnabled(ShellRoot, true);
        SetWallpaperMaterialSource(materialSource);
        GlassMaterial.SetWallpaperHost(ShellRoot, WallpaperImage);
        // The rail has its own desktop material. A shared wallpaper must never leak
        // through inheritance or be sampled again by the rail's material layer.
        GlassMaterial.SetWallpaperSource(NavigationSurface, null);
        GlassMaterial.SetWallpaperHost(NavigationSurface, null);
        GlassMaterial.SetDesktopBackdropEnabled(BodyFrame, source is null && GlassMaterial.GetDesktopBackdropEnabled(ShellRoot));
        ApplyPalette(_state.UseDarkText);
        UpdateWallpaperClip();
    }

    internal void SetWallpaperMaterialSource(ImageSource? source) => GlassMaterial.SetWallpaperSource(ShellRoot, source);

    public void SetDesktopBackdropActive(bool active)
    {
        GlassMaterial.SetDesktopBackdropEnabled(ShellRoot, active);
        GlassMaterial.SetDesktopBackdropEnabled(BodyFrame, active && WallpaperImage.Source is null);
        ApplyPalette(_state.UseDarkText);
    }

    /// <summary>The painted rounded surfaces; transparent gaps and corners stay outside the window shape.</summary>
    public IReadOnlyList<GlassWindowRegion> GetSurfaceRegions()
    {
        if (NavigationSurface.ActualWidth <= 0 || BodyFrame.ActualWidth <= 0 || WindowControlsSurface.ActualWidth <= 0 || ActualHeight <= 0)
            return Array.Empty<GlassWindowRegion>();
        if (_splashActive || _splashRequested) return new[] { new GlassWindowRegion(new Rect(RenderSize), 0) };

        var surfaces = new List<GlassWindowRegion>(5) {
            new GlassWindowRegion(NavigationSurface.TransformToAncestor(this).TransformBounds(new Rect(NavigationSurface.RenderSize)), NavigationSurface.CornerRadius.TopLeft),
            new GlassWindowRegion(BodyFrame.TransformToAncestor(this).TransformBounds(new Rect(BodyFrame.RenderSize)), BodyFrame.CornerRadius.TopLeft),
            new GlassWindowRegion(WindowControlsSurface.TransformToAncestor(this).TransformBounds(new Rect(WindowControlsSurface.RenderSize)), WindowControlsChrome.CornerRadius.TopLeft)
        };
        // The bottom-right dock protrudes like the window controls: its own region after the controls.
        if (MusicDockShown) surfaces.Add(MusicRegion(MusicDockSurface));
        if (_musicDockCompanion is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Opacity: > .01 } playlist)
            surfaces.Add(MusicRegion(playlist));
        return surfaces;

        GlassWindowRegion MusicRegion(GlassMusicSurface surface) {
            Rect bounds = surface.TransformToAncestor(this).TransformBounds(new Rect(surface.RenderSize));
            double scale = Math.Min(bounds.Width / surface.ActualWidth, bounds.Height / surface.ActualHeight);
            return new(bounds, surface.Chrome.CornerRadius.TopLeft * scale, surface.Opacity);
        }
    }

    public IReadOnlyList<GlassWindowRegion> GetDesktopBackdropRegions() => GetDesktopBackdropRegions(RenderSize);

    private IReadOnlyList<GlassWindowRegion> SplashBackdropRegions()
    {
        var regions = new List<GlassWindowRegion>(2);
        double fade = _splashFade is null ? 0 : Math.Clamp(_splashFade.Value, 0, 1);
        if (fade > .001 && NavigationSurface.Clip is RectangleGeometry rail && !rail.Rect.IsEmpty)
            regions.Add(new GlassWindowRegion(NavigationSurface.TransformToAncestor(this).TransformBounds(rail.Rect), NavigationSurface.CornerRadius.TopLeft, fade));
        Rect body = SplashBodyRect();
        if (!body.IsEmpty && body.Width > 1 && WallpaperImage.Source is null)
            regions.Add(new GlassWindowRegion(BodyFrame.TransformToAncestor(this).TransformBounds(body), BodyFrame.CornerRadius.TopLeft));
        // Window buttons: their blur fades in with them, together with the loading screen.
        double buttons = _splashLogo is null ? 0 : Math.Clamp(_splashLogo.Value, 0, 1);
        if (buttons > .001 && WindowControlsSurface.ActualWidth > 0)
            regions.Add(new GlassWindowRegion(WindowControlsSurface.TransformToAncestor(this).TransformBounds(new Rect(WindowControlsSurface.RenderSize)),
                WindowControlsChrome.CornerRadius.TopLeft, buttons));
        return regions;
    }

    private IReadOnlyList<GlassWindowRegion> _lastSplashBackdrop = Array.Empty<GlassWindowRegion>();

    public IReadOnlyList<GlassWindowRegion> GetDesktopBackdropRegions(Size clientSize)
    {
        // The backdrop host also asks on every move/resize: keep the splash shapes (or nothing) while it runs.
        if (_splashActive) return SplashBackdropRegions();
        if (_farewellActive) return FarewellBackdropRegions();
        if (_splashRequested) return Array.Empty<GlassWindowRegion>();
        IReadOnlyList<GlassWindowRegion> surfaces = GetSurfaceRegions();
        if (surfaces.Count < 3) return surfaces;
        GlassWindowRegion rail = surfaces[0], body = surfaces[1], controls = surfaces[2];
        double widthDelta = clientSize.Width - RenderSize.Width;
        double heightDelta = clientSize.Height - RenderSize.Height;
        // The rail and body stretch vertically. The window controls keep their size and follow the right edge.
        if (Math.Abs(widthDelta) > 0.5) {
            Rect bounds = controls.Bounds;
            bounds.Offset(widthDelta, 0);
            controls = controls with { Bounds = bounds };
            body = body with { Bounds = ResizeBounds(body.Bounds, widthDelta, 0) };
        }
        if (Math.Abs(heightDelta) > 0.5) {
            rail = rail with { Bounds = ResizeBounds(rail.Bounds, 0, heightDelta) };
            body = body with { Bounds = ResizeBounds(body.Bounds, 0, heightDelta) };
        }
        var backdrop = new List<GlassWindowRegion>(surfaces.Count) { rail };
        if (WallpaperImage.Source is null) backdrop.Add(body);
        backdrop.Add(controls);
        for (int index = 3; index < surfaces.Count; index++)
        {
            GlassWindowRegion surface = surfaces[index];
            Rect moved = surface.Bounds;
            if (Math.Abs(widthDelta) > .5 || Math.Abs(heightDelta) > .5) moved.Offset(widthDelta, heightDelta);
            backdrop.Add(surface with { Bounds = moved });
        }
        return backdrop;

        static Rect ResizeBounds(Rect bounds, double widthDelta, double heightDelta) =>
            new(bounds.Location, new Size(Math.Max(1, bounds.Width + widthDelta), Math.Max(1, bounds.Height + heightDelta)));
    }

    public IReadOnlyList<GlassWindowRegion> GetWindowHitRegions()
    {
        IReadOnlyList<GlassWindowRegion> surfaces = GetSurfaceRegions();
        if (surfaces.Count < 3 || !IsRailAnimating || _leftExpansionAllowance <= 0) return surfaces;
        Rect rail = surfaces[0].Bounds;
        GlassWindowRegion[] expanded = surfaces.ToArray();
        expanded[0] = surfaces[0] with { Bounds = new Rect(0, rail.Top, rail.Right, rail.Height) };
        return expanded;
    }

    private static string NavigationTitle(int page) => page switch
    {
        0 => "主页", 1 => "资源中心", 2 => "联机", 3 => "设置", 4 => "更多",
        5 => "选择实例", 6 => "任务管理", 7 => "实例管理", 8 => "资源详情", 9 => "帮助详情", 10 => "收藏",
        _ => "页面选项",
    };
    private double Radius(string resource) => ((CornerRadius)Resources[resource]).TopLeft;
    private bool ShouldSnapMotion => _state.ReduceMotion || !IsLoaded || _keyboardInteraction;

    private void ApplyPalette(bool darkText)
    {
        bool highContrast = SystemParameters.HighContrast;
        bool wallpaper = WallpaperImage.Source != null;
        bool desktop = GlassMaterial.GetDesktopBackdropEnabled(ShellRoot);
        bool translucent = wallpaper || desktop;
        // Colourless blur only (no tint), and never on the launch page, which has no page title.
        PageTitleBacking.Visibility = wallpaper && !_state.IsHome ? Visibility.Visible : Visibility.Collapsed;
        bool controlsFrost = translucent && !highContrast && WindowControlsChrome.IsTransparencyActive;
        WindowControlsWallpaperFrost.Visibility = controlsFrost ? Visibility.Visible : Visibility.Collapsed;
        double density = translucent ? Math.Clamp(GlassMaterial.GetOpacityMultiplier(this), 0, 1) : 1;
        var paletteKey = (darkText, wallpaper, desktop, density, highContrast, _state.IsHome, _wallpaperHue);
        if (_lastPalette == paletteKey)
        {
            UpdateWallpaperClip();
            return;
        }
        _blendPalette = _lastPalette is { HighContrast: false } old && old.DarkText != darkText && !highContrast &&
            !ShouldSnapMotion && IsVisible && !IsSplashing && !HoldsBackdrop && !IsClosing && ThemeTransition.CanBlendPalette;
        _lastPalette = paletteKey;
        ThemeToggleGlyph.Data = (Geometry)Resources[darkText ? "IconThemeMoon" : "IconThemeSun"];
        string themeAction = darkText ? "切换到深色主题" : "切换到浅色主题";
        ThemeToggleButton.ToolTip = themeAction;
        AutomationProperties.SetName(ThemeToggleButton, themeAction);

        if (highContrast)
        {
            foreach (string name in new[] { "GlassWindowBrush", "GlassMainBrush", "GlassCardBrush", "GlassChromeBrush" }) SetBrush(name, SystemColors.WindowColor);
            foreach (string name in new[] { "GlassTextBrush", "GlassSecondaryTextBrush", "HomeTextBrush", "HomeSecondaryTextBrush" }) SetBrush(name, SystemColors.WindowTextColor);
            SetBrush("GlassControlBrush", SystemColors.ControlColor);
            SetBrush("GlassHoverBrush", SystemColors.ControlDarkColor);
            SetBrush("GlassSelectedBrush", SystemColors.HighlightColor);
            SetBrush("GlassListSelectedBrush", SystemColors.HighlightColor);
            SetBrush("GlassOutlineBrush", SystemColors.WindowTextColor);
            SetBrush("GlassNavHoverBrush", SystemColors.ControlColor);
            SetBrush("GlassNavHoverOutlineBrush", SystemColors.WindowTextColor);
            SetBrush("GlassAccentBrush", SystemColors.HighlightColor);
            SetBrush("GlassAccentTextBrush", SystemColors.HighlightTextColor);
            SetMaterialFallback(SystemColors.WindowColor);
            BodyFrame.Background = Brushes.Transparent;
            WindowControlsBodyFrostBase.Background = Brushes.Transparent;
            WindowControlsBodyFrostBase.BorderBrush = Brushes.Transparent;
            WallpaperImage.Visibility = Visibility.Hidden;
            WindowControlsWallpaperFrost.Visibility = Visibility.Hidden;
            WorkspaceSurface.Background = SystemColors.WindowBrush;
            ContextStrip.Background = SystemColors.WindowBrush;
            SetHomeTextShadow(false);
            UpdateWallpaperClip();
            return;
        }
        NavigationSurface.TintOpacity = wallpaper ? 1 : .48;
        WindowControlsChrome.TintOpacity = wallpaper ? .66 : .52;
        WallpaperImage.Visibility = wallpaper ? Visibility.Visible : Visibility.Collapsed;
        SetBrush("GlassWindowBrush", desktop ? Colors.Transparent : darkText ? Color.FromRgb(245, 246, 248) : Color.FromRgb(24, 24, 24));
        SetBrush("GlassTextBrush", darkText ? Color.FromRgb(32, 43, 58) : Color.FromRgb(244, 244, 244));
        SetBrush("GlassSecondaryTextBrush", darkText ? Color.FromRgb(101, 114, 132) : Color.FromRgb(184, 184, 184));
        Color mainBase = darkText ? Color.FromRgb(241, 243, 246) : Color.FromRgb(24, 24, 24);
        SetBrush("GlassMainBrush", wallpaper ? Colors.Transparent : mainBase);
        // Keep an opaque dark/theme base behind the wallpaper so lowering wallpaper opacity fades to it rather than the desktop.
        if (wallpaper) _wallpaperBaseBrush = ThemeTransition.BlendPaletteBrush(_wallpaperBaseBrush, mainBase, _blendPalette);
        BodyFrame.Background = wallpaper ? _wallpaperBaseBrush : Brushes.Transparent;
        Color card = darkText ? Color.FromRgb(255, 255, 255) : Color.FromRgb(32, 32, 32);
        card.A = translucent ? (byte)Math.Round(224 * density) : (byte)255;
        SetBrush("GlassCardBrush", card);
        // With a wallpaper the floating rail is tinted from the wallpaper's own colour (Monet-style tonal palette)
        // and stays translucent over its frosted backdrop instead of painting a neutral panel.
        Color chrome = wallpaper && _wallpaperHue is Color hue
            ? TonalTint(hue, darkText)
            : darkText ? Color.FromArgb(247, 248, 249, 251) : Color.FromArgb(245, 32, 32, 32);
        SetBrush("GlassChromeBrush", chrome);
        SetBrush("GlassControlBrush", darkText ? Color.FromArgb(231, 232, 237, 243) : Color.FromRgb(51, 51, 51));
        SetBrush("GlassHoverBrush", darkText ? Color.FromRgb(220, 230, 236) : Color.FromRgb(65, 65, 65));
        SetBrush("GlassNavHoverBrush", darkText ? Color.FromArgb(16, 32, 43, 58) : Color.FromArgb(24, 255, 255, 255));
        SetBrush("GlassNavHoverOutlineBrush", darkText ? Color.FromArgb(40, 32, 43, 58) : Color.FromArgb(48, 255, 255, 255));
        // Selections are a same-tone overlay of the panel beneath (lighter in dark mode, darker in light mode).
        SetBrush("GlassSelectedBrush", darkText ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255));
        SetBrush("GlassListSelectedBrush", darkText ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
        SetBrush("GlassOutlineBrush", darkText ? Color.FromArgb(32, 128, 148, 168) : Color.FromArgb(32, 255, 255, 255));
        // Neutral accent in the interface's own grey family: near-white on dark, charcoal on light.
        // Labels on the accent use the opposite tone (contrast > 12:1 in both themes).
        SetBrush("GlassAccentBrush", darkText ? Color.FromRgb(43, 43, 43) : Color.FromRgb(230, 230, 230));
        SetBrush("GlassAccentTextBrush", darkText ? Colors.White : Color.FromRgb(26, 26, 26));
        SetBrush("HomeTextBrush", darkText ? Color.FromRgb(32, 43, 58) : Color.FromRgb(248, 250, 253));
        SetBrush("HomeSecondaryTextBrush", darkText ? Color.FromRgb(83, 101, 122) : Color.FromRgb(221, 221, 221));
        SetMaterialFallback(darkText ? Color.FromRgb(245, 246, 248) : Color.FromRgb(24, 24, 24));
        SetHomeTextShadow(false);
        WorkspaceSurface.Background = Brushes.Transparent;
        ContextStrip.Background = Brushes.Transparent;
        ContextStrip.BorderBrush = _state.IsHome ? Brushes.Transparent : (Brush)Resources["GlassOutlineBrush"];
        WindowControlsBodyFrostBase.Background = BodyFrame.Background;
        WindowControlsBodyFrostBase.BorderBrush = (Brush)Resources["GlassOutlineBrush"];
        WindowControlsBodyFrostBase.BorderThickness = new Thickness(0, 1, 1, 0);
        UpdateWallpaperClip();
    }

    private void SetHomeTextShadow(bool enabled)
    {
        if (_homeTextShadowEnabled == enabled) return;
        _homeTextShadowEnabled = enabled;
        System.Windows.Media.Effects.DropShadowEffect effect = null;
        if (enabled)
        {
            effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .4, Color = Colors.Black };
            effect.Freeze();
        }
        InstanceNameText.Effect = effect;
        InstanceDescriptionText.Effect = effect;
        LaunchStatusText.Effect = effect;
    }

    private void SetMaterialFallback(Color color)
    {
        _materialFallbackBrush = ThemeTransition.BlendPaletteBrush(_materialFallbackBrush, color, _blendPalette);
        if (GlassMaterial.GetFallbackColor(_surfaceMaskHost) != color)
            GlassMaterial.SetFallbackColor(_surfaceMaskHost, color);
        GlassMaterial.SetFallbackBrush(_surfaceMaskHost, _materialFallbackBrush);
    }

    private void SetBrush(string key, Color newColor)
    {
        var old = Resources[key] as SolidColorBrush;
        var next = ThemeTransition.BlendPaletteBrush(old, newColor, _blendPalette);
        if (!ReferenceEquals(old, next)) Resources[key] = next;
    }

    private ImageSource GetFallbackInstanceIcon()
    {
        if (_fallbackInstanceIcon != null) return _fallbackInstanceIcon;
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/LivingPCL;component/Images/Blocks/Grass.png", UriKind.Absolute));
            if (image.CanFreeze) image.Freeze();
            _fallbackInstanceIcon = image;
        }
        catch (Exception exception) when (exception is System.IO.IOException or InvalidOperationException or NotSupportedException)
        {
            // An isolated preview can omit the main app resources. The adapter supplies real icons at runtime.
        }
        return _fallbackInstanceIcon;
    }

    private void ApplyExpansion(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        PrimaryTranslation.X = 0;
        ExpandTranslation.X = 0;
        // Drops down from the panel header.
        AuxiliaryTranslation.X = 0;
        AuxiliaryTranslation.Y = -MotionTokens.AuxiliaryTravel * (1 - p);
        // The options live inside the home panel, so they leave with the home content when another page is opened.
        double home = PrimaryCard.Opacity;
        AuxiliaryCards.Opacity = p * home;
        ExpandRotation.Angle = p * 45;
        AuxiliaryCards.IsHitTestVisible = _state.IsHome && _expanded && _state.LaunchOptionsAvailable && p > MotionTokens.VisibilityThreshold;
        AuxiliaryCards.Visibility = (p < MotionTokens.VisibilityThreshold && (!_expanded || !_state.LaunchOptionsAvailable)) || home < MotionTokens.VisibilityThreshold ? Visibility.Hidden : Visibility.Visible;
        MakeRoomForOptions(p);
    }

    /// <summary>
    /// The startup options drop down over the top of the centred instance block. As they open, the block moves down
    /// just enough to clear them; if the panel has too little height below, the rest is taken by scaling the block down
    /// (anchored at its bottom), so the two never overlap.
    /// </summary>
    private void MakeRoomForOptions(double p)
    {
        PrimaryScale.ScaleX = PrimaryScale.ScaleY = 1;
        PrimaryTranslation.Y = 0;
        double height = PrimaryCard.ActualHeight, options = AuxiliaryCards.ActualHeight;
        if (p <= 0 || height <= 0 || options <= 0) return;
        Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(PrimaryCard);
        if (slot.Height <= 0) return;
        const double gap = 14;
        double top = slot.Top + (slot.Height - height) / 2;
        double needed = slot.Top + AuxiliaryCards.Margin.Top + options + gap - top;
        if (needed <= 0) return;
        double below = Math.Max(0, slot.Bottom - (top + height));
        double shift = Math.Min(needed, below);
        double scale = Math.Clamp((height - (needed - shift)) / height, .55, 1);
        PrimaryCard.RenderTransformOrigin = new Point(.5, 1);
        PrimaryScale.ScaleX = PrimaryScale.ScaleY = 1 - (1 - scale) * p;
        PrimaryTranslation.Y = shift * p;
    }

    private void ApplyRail(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        RailTranslation.X = 0;
        RailTranslation.Y = -MotionTokens.RailTravel * (1 - p);
        SideRail.Opacity = p;
        SideRail.Visibility = _state.IsHome || p < MotionTokens.VisibilityThreshold && !_hasSidebar ? Visibility.Hidden : Visibility.Visible;
    }

    private void ApplyRailExpansion(double value)
    {
        double maxExtra = _leftExpansionAllowance > 0 ? _leftExpansionAllowance : RailExpandDelta + RailOvershootHeadroom;
        double width = Math.Clamp(RailCollapsedWidth + RailExpandDelta * value, RailCollapsedWidth - 4, RailCollapsedWidth + maxExtra);
        NavigationSurface.Width = width;
        RailToggleRotation.Angle = 180 * value;
        // Labels: expanding, they follow the growing width one after another from top to bottom (each slides in and
        // settles); collapsing, they all leave quickly in the first part of the travel so the rail shrinks clean.
        TextBlock[] labels = { RailBrandText, NavLaunchText, NavDownloadText, NavFavoritesText, NavSettingsText, NavOtherText, RailToggleText, AccountNameText };
        if (Resources["RailLabelTranslation"] is TranslateTransform shared) shared.X = 0;
        for (int index = 0; index < labels.Length; index++)
        {
            TextBlock label = labels[index];
            double progress = _railExpanded
                ? Math.Clamp((value - .14 - .045 * index) / .5, 0, 1)
                : Math.Clamp((value - .5) / .4, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            if (label.RenderTransform is not TranslateTransform own || ReferenceEquals(own, Resources["RailLabelTranslation"]))
                label.RenderTransform = own = new TranslateTransform();
            own.X = 14 * (1 - eased);
            label.Opacity = eased;
            Visibility visibility = progress > MotionTokens.VisibilityThreshold || _railExpanded && value > .08 ? Visibility.Visible : Visibility.Collapsed;
            if (label.Visibility != visibility) label.Visibility = visibility;
        }
        if (IsLoaded) SyncSurfaceRegions();
    }

    private void ApplyAccountPopover(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        AccountPopover.Opacity = p;
        AccountPopoverScale.ScaleX = AccountPopoverScale.ScaleY = 1;
        AccountPopoverTranslation.Y = MotionTokens.PopoverTravel * (1 - p);
        AccountPopover.Visibility = _accountOpen || p > MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Collapsed;
        AccountPopover.IsHitTestVisible = _accountOpen;
    }

    private void ApplyHomeTransition(double value)
    {
        _homeProgress = Math.Clamp(value, 0, 1);
        // The sidebar panel already flips vertically, so home content only cross-fades (no sideways slide of its own).
        // Leaving home it clears in the first half, before the new section fades in; entering it arrives early.
        double homeOpacity = _state.IsHome ? Math.Clamp(_homeProgress / .6, 0, 1) : Math.Clamp((_homeProgress - .5) / .5, 0, 1);
        HomePanel.Opacity = homeOpacity;
        HomeTranslation.X = 0;
        HomePanel.Visibility = _state.IsHome || _homeProgress > MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Hidden;
        HomePanel.IsHitTestVisible = _state.IsHome;
        PrimaryCard.Opacity = homeOpacity;
        ApplyExpansion(_expansionMotion?.Value ?? 0);
        PrimaryCard.Visibility = _state.IsHome || _homeProgress > MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Hidden;
        PrimaryCard.IsHitTestVisible = _state.IsHome;
    }

    private void ApplyPageTransition(double value)
    {
        if (_currentPage.Host.Content is not null)
        {
            double travel = Math.Clamp((value - _entryDelay) / (1 - _entryDelay), 0, 1.10);
            double fade = Math.Clamp((value - _entryDelay) / (PageEntryFadeEnd - _entryDelay), 0, 1);
            _currentPage.Apply(new PagePose(
                _entryStart.Opacity + (1 - _entryStart.Opacity) * SmoothPageFade(fade),
                _entryStart.Scale + (1 - _entryStart.Scale) * travel,
                _entryStart.Offset * (1 - travel)));
        }
        if (_alternatePage.Host.Content is null) return;
        double exit = Math.Clamp(value / PageExitEnd, 0, 1);
        double recede = 1 - (1 - exit) * (1 - exit);
        _alternatePage.Apply(new PagePose(
            _exitStart.Opacity * (1 - SmoothPageFade(exit)),
            _exitStart.Scale + (PageExitScale - _exitStart.Scale) * recede,
            _exitStart.Offset + (-PageExitTravel * _transitionDirection - _exitStart.Offset) * recede));
        if (exit >= 1) ReleasePage(_alternatePage);
    }

    private static double SmoothPageFade(double value) => value * value * (3 - 2 * value);

    private void ApplyTitleTransition(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        NavigationButtons.Opacity = 1;
        NavigationButtons.Visibility = Visibility.Visible;
        NavigationButtons.IsHitTestVisible = true;
        BackTitlePanel.Opacity = p;
        BackTitleTranslation.X = -MotionTokens.LayerTravel * (1 - p);
        BackTitlePanel.Visibility = _state.CanGoBack || p > MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Collapsed;
        // The back button's slot grows and shrinks with it, so the title glides instead of jumping 40 px at the end.
        double slot = Math.Clamp(value, 0, 1.1);
        BackTitlePanel.Width = 36 * slot;
        BackTitlePanel.Margin = new Thickness(0, 0, 4 * slot, 0);
        BackTitlePanel.IsHitTestVisible = _state.CanGoBack;
    }

    private void ApplyOptionsAvailability(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        ExpandButtonContainer.Opacity = p;
        ExpandAvailabilityScale.ScaleX = ExpandAvailabilityScale.ScaleY = 1;
        ExpandButton.Visibility = _state.LaunchOptionsAvailable || p > MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Hidden;
        ExpandButtonContainer.IsHitTestVisible = _state.IsHome && _state.LaunchOptionsAvailable;
    }

    private void ArrangeHomeDeck()
    {
        if (HomeDeck.ActualWidth <= 0) return;
        bool compact = IsCompactViewport;
        PrimaryCard.Width = double.NaN;
        PrimaryCard.Height = double.NaN;
        InstanceImage.Width = InstanceImage.Height = 64;
        InstanceNameText.FontSize = 18;
        InstanceCenter.Margin = new Thickness(0);
        LaunchButton.Width = compact ? 180 : 190;
        LaunchButton.Height = 48;
        LaunchButton.Margin = new Thickness(0, 22, 0, 0);
        AuxiliaryCards.Width = compact ? 244 : 272;
        ApplyExpansion(_expansionMotion.Value);
    }

    private void UpdateSidebarSelection(bool immediate)
    {
        FrameworkElement selected = SidebarPanel.Children.OfType<FrameworkElement>().FirstOrDefault(row => Equals(row.Tag, SelectedRowTag));
        if (selected is null || selected.ActualHeight <= 0)
        {
            _sidebarSelectionVisible = false;
            SidebarSelection.Opacity = 0;
            return;
        }
        // Layout offset, not TranslatePoint: rows may still carry their entrance rise (a render transform).
        double target = VisualTreeHelper.GetOffset(selected).Y + VisualTreeHelper.GetOffset(SidebarPanel).Y;
        SidebarSelection.Height = selected.ActualHeight;
        // The first appearance lands in place; later changes slide on the spring.
        _sidebarSelectionMotion.Retarget(target, immediate || !_sidebarSelectionVisible);
        _sidebarSelectionVisible = true;
        SidebarSelection.Opacity = 1;
    }

    private void SetSelectedNavigation(bool immediate)
    {
        int index = _state.SelectedNavigationId switch { 1 => 1, 10 => 2, 3 => 3, 4 => 4, _ => 0 };
        Button[] buttons = [NavLaunch, NavDownload, NavFavorites, NavSettings, NavOther];
        TextBlock[] labels = [NavLaunchText, NavDownloadText, NavFavoritesText, NavSettingsText, NavOtherText];
        Button selected = buttons[index];
        // The account page owns the selection while it is open: the highlight slides down onto the avatar.
        if (_workspaceOpen && AccountButton.ActualHeight > 0 && NavigationButtons.ActualHeight > 0)
        {
            index = -1;
            NavSelection.Height = AccountButton.ActualHeight;
            _navigationMotion.Retarget(AccountButton.TranslatePoint(new Point(0, 0), NavigationButtons).Y, immediate);
        }
        else if (selected.ActualHeight > 0 && NavigationButtons.ActualHeight > 0)
        {
            NavSelection.Height = selected.ActualHeight;
            Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(selected);
            _navigationMotion.Retarget(slot.Top + (slot.Height - selected.ActualHeight) / 2, immediate);
        }
        for (int i = 0; i < buttons.Length; i++)
        {
            FontWeight weight = i == index ? FontWeights.Medium : FontWeights.Normal;
            buttons[i].FontWeight = weight;
            labels[i].FontWeight = weight;
            AutomationProperties.SetItemStatus(buttons[i], i == index ? "当前页面" : string.Empty);
            buttons[i].SetResourceReference(Control.ForegroundProperty, i == index ? "GlassAccentBrush" : "GlassTextBrush");
            UpdateNavigationHover(buttons[i], immediate);
        }
    }

    private void NavigationButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _navigationHoverMotions.ContainsKey(button)) return;
        button.ApplyTemplate();
        _navigationHoverMotions.Add(button, new SpringMotion(button, 0,
            value => ApplyNavigationHover(button, value), MotionTokens.Reveal));
        button.IsEnabledChanged += NavigationButton_IsEnabledChanged;
        UpdateNavigationHover(button, ShouldSnapMotion);
    }

    private void NavigationButton_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabledChanged -= NavigationButton_IsEnabledChanged;
        if (_navigationHoverMotions.Remove(button, out SpringMotion motion)) motion.Dispose();
    }

    private void NavigationButton_HoverChanged(object sender, MouseEventArgs e)
    {
        if (sender is Button button) UpdateNavigationHover(button);
    }

    private void NavigationButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button button) UpdateNavigationHover(button, true);
    }

    private void UpdateNavigationHover(Button button, bool immediate = false)
    {
        if (_navigationHoverMotions.TryGetValue(button, out SpringMotion motion))
        {
            bool selected = AutomationProperties.GetItemStatus(button) == "当前页面";
            double target = button.IsEnabled && button.IsMouseOver ? (selected ? .45 : 1) : 0;
            motion.Retarget(target, immediate || _state.ReduceMotion || !button.IsLoaded);
        }
    }

    private static void ApplyNavigationHover(Button button, double value)
    {
        double progress = Math.Clamp(value, 0, 1);
        if (button.Template?.FindName("NavHoverLayer", button) is Border layer)
            layer.Opacity = progress;
        if (button.Template?.FindName("NavHoverTranslation", button) is TranslateTransform translation)
            translation.X = progress * 1.5;
    }

    private void ScheduleLayout()
    {
        if (_layoutPending || _disposed) return;
        _layoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _layoutPending = false;
            if (_disposed) return;
            bool compact = IsCompactViewport;
            double columnWidth = SecondaryWidth(_state.IsHome || _workspaceOpen, compact);
            SetSecondaryColumnWidth(columnWidth, animate: false);
            ArrangeHomeDeck();
            SetSelectedNavigation(ShouldSnapMotion);
            SyncSurfaceRegions();
        }));
    }

    // ---------- Startup splash ----------
    //
    // The window already has its final size; everything but the logo is transparent at first.
    // 1. The logo tile fades in at the window's bottom-right corner.
    // 2. It travels up the right edge to the top-right corner.
    // 3. The rail extends down from it along the right edge, then the whole rail glides left to its place; the path it
    //    sweeps becomes the body (solid frosted glass in the theme's tone).
    // 4. The large logo pops in, "PCL Living Fork" rises under it, then the loader; once the shell reports everything
    //    loaded (CompleteSplash), the splash fades into the real interface.
    // Every stage is an iOS-style spring and starts when the previous one is mostly settled; the window's visible
    // region follows the animated shapes frame by frame.

    private static readonly SpringParameters SplashRevealSpring = new(.55, .8);
    private static readonly SpringParameters SplashSlideSpring = new(.62, .84);
    private static readonly SpringParameters SplashLogoSpring = new(.5, .68);
    private static readonly SpringParameters SplashTextSpring = new(.45, .82);
    private static readonly SpringParameters SplashExitSpring = new(.42, 1);
    private const double SplashTile = 62;           // the rail header that holds the logo
    private const double SplashMaxSeconds = 6;      // never hold the interface back longer than this

    private bool _splashRequested, _splashActive, _splashReady, _splashExiting;
    private System.Diagnostics.Stopwatch _splashClock;
    private double _splashTime, _splashLastFrame = double.NaN;
    private SpringMotion _splashFade, _splashRise, _splashReveal, _splashSlide, _splashLogo, _splashText, _splashLoader, _splashExit;
    private readonly TranslateTransform _splashRailShift = new();
    private double _splashStartX, _splashStartY;
    private SolidColorBrush _splashRailTone;

    /// <summary>True while the startup splash is showing.</summary>
    public bool IsSplashing => _splashActive;
    /// <summary>The splash has finished its entrance and is showing its loader.</summary>
    public bool IsSplashLoading => _splashActive && !_splashExiting && _splashLoader is { Value: > .85 };
    /// <summary>True from the splash request until it has finished (the native backdrop is held back meanwhile).</summary>
    public bool HoldsBackdrop => _splashRequested && !_splashActive;
    /// <summary>Raised when the splash has gone and the interface is fully shown.</summary>
    public event EventHandler SplashFinished;
    /// <summary>Raised when the splash starts drawing (its shapes now carry the native blur).</summary>
    public event EventHandler SplashStarted;

    /// <summary>Requests the startup splash; it starts when the view is loaded. Ignored with reduced motion.</summary>
    public void BeginSplash()
    {
        if (_state.ReduceMotion || !MotionPreferences.AllowsAnimation) return;
        _splashRequested = true;
        // The splash uses a rectangular envelope for its moving surfaces. Only the
        // individual splash panels may paint a fallback inside that envelope.
        ShellRoot.Background = Brushes.Transparent;
        Opacity = 0; // revealed by StartSplash with the splash's own first frame
        if (IsLoaded) StartSplash();
    }

    /// <summary>Everything is loaded: the splash may leave (after its entrance has played).</summary>
    public void CompleteSplash() => _splashReady = true;

    // Closing: the window buttons fade away, the body folds leftwards into the rail and vanishes, then the rail fades
    // out; the window closes once everything has gone. Each stage starts while the previous one is finishing.
    private bool _farewellActive;
    private DispatcherOperation _farewellDraw;
    private SpringMotion _byeControls, _byeBody, _byeRail;
    private Action _farewellDone;
    private IReadOnlyList<GlassWindowRegion> _lastFarewellBackdrop = Array.Empty<GlassWindowRegion>();
    public bool IsClosing => _farewellActive;
    /// <summary>Raised each closing frame with how far the overlays above the interface should have faded (0..1).</summary>
    public event Action<double> FarewellOverlayFade;

    /// <summary>Plays the closing animation and then runs <paramref name="done"/>; false when it cannot play.</summary>
    public bool BeginFarewell(Action done)
    {
        if (_farewellActive) return true;
        if (_splashActive || !MotionPreferences.AllowsAnimation || !IsLoaded || BodyFrame.ActualWidth <= 0) return false;
        _farewellActive = true;
        _farewellDone = done;
        IsHitTestVisible = false;
        _byeControls = new SpringMotion(this, 0, _ => QueueFarewellDraw(), new SpringParameters(.28, 1));
        _byeBody = new SpringMotion(this, 0, _ => QueueFarewellDraw(), new SpringParameters(.46, 1));
        _byeRail = new SpringMotion(this, 0, _ => QueueFarewellDraw(), new SpringParameters(.3, 1));
        _byeControls.Retarget(1);
        return true;
    }

    private void QueueFarewellDraw()
    {
        if (!_farewellActive || _farewellDraw?.Status == DispatcherOperationStatus.Pending) return;
        _farewellDraw = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _farewellDraw = null;
            ApplyFarewell();
        }));
    }

    private void ApplyFarewell()
    {
        if (!_farewellActive || _byeControls is null || _byeBody is null || _byeRail is null) return;
        double controls = Math.Clamp(_byeControls.Value, 0, 1), body = Math.Clamp(_byeBody.Value, 0, 1), rail = Math.Clamp(_byeRail.Value, 0, 1);
        if (controls > .6 && _byeBody.Target < 1) _byeBody.Retarget(1);
        if (body > .85 && _byeRail.Target < 1) _byeRail.Retarget(1);
        // 1. The button capsule fades out (shrinking a little, like a bubble).
        WindowControlsSurface.Opacity = 1 - controls;
        if (WindowControlsSurface.RenderTransform is not ScaleTransform bubble)
        {
            WindowControlsSurface.RenderTransformOrigin = new Point(.5, .5);
            WindowControlsSurface.RenderTransform = bubble = new ScaleTransform();
        }
        bubble.ScaleX = bubble.ScaleY = 1 - .22 * controls;
        // The music dock and its playlist go the same way, at the same time.
        ApplyMusicFarewell(controls);
        // 2. The body folds towards the rail: its right edge travels left, its content fading near the end.
        Rect visible = FarewellBodyRect();
        BodyFrame.Clip = visible.Width <= .5 ? Geometry.Empty : new RectangleGeometry(visible, BodyFrame.CornerRadius.TopLeft, BodyFrame.CornerRadius.TopLeft);
        BodyContentRoot.Opacity = 1 - Math.Clamp((body - .45) / .5, 0, 1);
        // Dialogs, toasts and floating buttons sit above the body: they leave with the button capsule and the body.
        FarewellOverlayFade?.Invoke(Math.Max(controls, body));
        // 3. The rail fades out last.
        NavigationSurface.Opacity = 1 - rail;
        IReadOnlyList<GlassWindowRegion> blur = FarewellBackdropRegions();
        if (!blur.SequenceEqual(_lastFarewellBackdrop)) { _lastFarewellBackdrop = blur; RegionsChanged?.Invoke(this, EventArgs.Empty); }
        if (rail > .985 && _farewellDone is { } done)
        {
            _farewellDone = null;
            Dispatcher.BeginInvoke(done);
        }
    }

    /// <summary>Brings the interface back when closing was refused after the animation (e.g. a file operation still running).</summary>
    public void CancelFarewell()
    {
        if (!_farewellActive) return;
        _farewellActive = false;
        if (_farewellDraw?.Status == DispatcherOperationStatus.Pending) _farewellDraw.Abort();
        _farewellDraw = null;
        _farewellDone = null;
        foreach (SpringMotion motion in new[] { _byeControls, _byeBody, _byeRail }) motion?.Dispose();
        _byeControls = _byeBody = _byeRail = null;
        WindowControlsSurface.Opacity = 1;
        WindowControlsSurface.RenderTransform = null;
        ApplyMusicFarewell(0);
        BodyContentRoot.Opacity = 1;
        NavigationSurface.Opacity = 1;
        FarewellOverlayFade?.Invoke(0);
        BodyFrame.Clip = null;
        _lastBodyClipKey = null;
        _lastFarewellBackdrop = Array.Empty<GlassWindowRegion>();
        IsHitTestVisible = true;
        SyncSurfaceRegions();
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The part of the body still shown, in BodyFrame coordinates: from its left edge to a right edge moving left.
    private Rect FarewellBodyRect()
    {
        double body = _byeBody is null ? 0 : Math.Clamp(_byeBody.Value, 0, 1);
        double width = BodyFrame.ActualWidth * (1 - body);
        return width <= .5 ? Rect.Empty : new Rect(0, 0, width, BodyFrame.ActualHeight);
    }

    private IReadOnlyList<GlassWindowRegion> FarewellBackdropRegions()
    {
        IReadOnlyList<GlassWindowRegion> surfaces = GetSurfaceRegions();
        if (surfaces.Count < 3) return surfaces;
        double controls = Math.Clamp(_byeControls?.Value ?? 0, 0, 1), rail = Math.Clamp(_byeRail?.Value ?? 0, 0, 1);
        var regions = new List<GlassWindowRegion>(3);
        if (rail < .999) regions.Add(surfaces[0] with { Opacity = surfaces[0].Opacity * (1 - rail) });
        Rect body = FarewellBodyRect();
        if (!body.IsEmpty && body.Width > 1 && WallpaperImage.Source is null)
            regions.Add(surfaces[1] with { Bounds = BodyFrame.TransformToAncestor(this).TransformBounds(body) });
        if (controls < .999) regions.Add(surfaces[2] with { Opacity = surfaces[2].Opacity * (1 - controls) });
        // The music dock and playlist (their regions already carry the fading opacity and shrinking bounds).
        for (int index = 3; index < surfaces.Count; index++)
            if (surfaces[index].Opacity > .001) regions.Add(surfaces[index]);
        return regions;
    }

    private void StartSplash()
    {
        try { StartSplashCore(); }
        catch (Exception) { AbortSplash(); }
    }

    // Whatever goes wrong, the interface comes up as if there had been no splash.
    private void AbortSplash()
    {
        Opacity = 1;
        try { FinishSplash(); }
        catch (Exception error) { Logger.Warn(error, "恢复启动界面失败"); }
    }

    private void StartSplashCore()
    {
        if (!_splashRequested || _splashActive) return;
        UpdateLayout();
        if (NavigationSurface.ActualHeight <= 0 || BodyFrame.ActualWidth <= 0) return;
        _splashRequested = false; // only once it can actually start (layout may not be ready on the first call)
        _splashActive = true;
        SetSplashUnderlay(0);
        // Theme tone: solid dark glass in a dark theme, solid light glass in a light theme.
        SplashGlass.Background = new SolidColorBrush(_state.UseDarkText ? Color.FromArgb(0xC8, 0xF4, 0xF5, 0xF8) : Color.FromArgb(0xB8, 0x14, 0x14, 0x16));
        if (AppLogoImage.Source is null)
        {
            try { AppLogoImage.Source = (ImageSource)Application.LoadComponent(new Uri("/LivingPCL;component/Images/Branding/PCLLF.Logo.xaml", UriKind.Relative)); }
            catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or NotSupportedException) { }
        }
        // A separate instance: the logo's colour is a DynamicResource, which resolves through the one element a drawing is
        // attached to — sharing the rail's instance left the large logo without a brush (invisible).
        try { SplashLogo.Source = (ImageSource)Application.LoadComponent(new Uri("/LivingPCL;component/Images/Branding/PCLLF.Logo.xaml", UriKind.Relative)); }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or NotSupportedException) { SplashLogo.Source = AppLogoImage.Source; }
        SplashLayer.Visibility = Visibility.Visible;
        SplashLayer.Opacity = 1;
        SplashGlass.Opacity = 1;
        SplashStatusContent.Opacity = 1;
        // The travelling rail carries only the logo; its page icons, account and toggle arrive with the interface.
        foreach (UIElement item in RailItems) item.Opacity = 0;
        // Start: the rail stands against the body's right edge, its tile at the bottom.
        Point railAt = NavigationSurface.TranslatePoint(new Point(0, 0), this);
        Point bodyRight = BodyFrame.TranslatePoint(new Point(BodyFrame.ActualWidth, 0), this);
        _splashStartX = bodyRight.X - NavigationSurface.ActualWidth - railAt.X;
        _splashStartY = NavigationSurface.ActualHeight - SplashTile;
        NavigationSurface.RenderTransform = _splashRailShift;
        NavigationSurface.IsHitTestVisible = false;
        // Cache the travelling rail's tint while the native blur follows its moving region underneath.
        NavigationSurface.Background = SplashGlass.Background;
        NavigationSurface.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
        RegionsChanged?.Invoke(this, EventArgs.Empty);
        WindowControlsSurface.Opacity = 0;
        // Every spring advances at most one 40 fps step per rendered frame, so a busy startup pauses the splash
        // instead of letting its first stages run out unseen.
        const double frameCap = 1 / 40d;
        // SplashFrame commits visuals once. Each spring only advances its own state.
        _splashFade = new SpringMotion(this, 0, static _ => { }, SplashTextSpring) { MaxFrameSeconds = frameCap };
        _splashRise = new SpringMotion(this, 0, static _ => { }, SplashSlideSpring) { MaxFrameSeconds = frameCap };
        _splashReveal = new SpringMotion(this, 0, static _ => { }, SplashRevealSpring) { MaxFrameSeconds = frameCap };
        _splashSlide = new SpringMotion(this, 0, static _ => { }, SplashSlideSpring) { MaxFrameSeconds = frameCap };
        _splashLogo = new SpringMotion(this, 0, static _ => { }, SplashLogoSpring) { MaxFrameSeconds = frameCap };
        _splashText = new SpringMotion(this, 0, static _ => { }, SplashTextSpring) { MaxFrameSeconds = frameCap };
        _splashLoader = new SpringMotion(this, 0, static _ => { }, SplashTextSpring) { MaxFrameSeconds = frameCap };
        _splashExit = new SpringMotion(this, 0, static _ => { }, SplashExitSpring) { MaxFrameSeconds = frameCap };
        _splashClock = System.Diagnostics.Stopwatch.StartNew();
        _splashTime = 0; _splashLastFrame = double.NaN;
        ApplySplash();
        Opacity = 1;
        SplashStarted?.Invoke(this, EventArgs.Empty);
        CompositionTarget.Rendering += OnSplashFrame;
    }

    // Starts each stage once the previous one is mostly in place, and the exit once loading has finished.
    private void OnSplashFrame(object sender, EventArgs args)
    {
        try { SplashFrame(); }
        catch (Exception) { AbortSplash(); }
    }

    private void SplashFrame()
    {
        // Splash time counts rendered frames (each at most 1/40 s), not wall-clock time.
        double now = _splashClock.Elapsed.TotalSeconds;
        if (!double.IsNaN(_splashLastFrame)) _splashTime += Math.Min(now - _splashLastFrame, 1 / 40d);
        _splashLastFrame = now;
        double t = _splashTime;
        if (t > .12 && _splashFade.Target < 1) _splashFade.Retarget(1);
        if (_splashFade.Value > .8 && _splashRise.Target < 1) _splashRise.Retarget(1);
        if (_splashRise.Value > .85 && _splashReveal.Target < 1) _splashReveal.Retarget(1);
        if (_splashReveal.Value > .85 && _splashSlide.Target < 1) _splashSlide.Retarget(1);
        if (_splashSlide.Value > .78 && _splashLogo.Target < 1) _splashLogo.Retarget(1);
        if (_splashLogo.Value > .55 && _splashText.Target < 1) _splashText.Retarget(1);
        if (_splashText.Value > .6 && _splashLoader.Target < 1) { _splashLoader.Retarget(1); SplashLoader.IsActive = true; }
        // While loading, the resting rail fills with water up to the load progress, its percentage written across it.
        if (_splashLoader.Value > .5 && !_splashExiting)
        {
            if (_splashTide is null)
            {
                NavigationSurface.CacheMode = null; // the rail is at rest now; the water animates inside it
                // The water fills the whole rail (behind the logo); the figure stands on its side in the rail's middle.
                _splashTide = new global::PCL.GlassTideProgress(NavigationSurface.CornerRadius.TopLeft) { IsHitTestVisible = false, PosterFigure = true };
                Grid.SetRow(_splashTide, 0); Grid.SetRowSpan(_splashTide, 3);
                NavigationRailContent.Children.Insert(0, _splashTide);
                _splashTideStart = t;
                _splashTide.Update(true, 0, string.Empty); // the water starts empty
            }
            // Rises at a steady pace (full in about 1.6 s) but never above the real progress, so it always reads as a fill.
            _splashTideShown = Math.Min(Math.Clamp(SplashProgress, 0, 1), (t - _splashTideStart) / 1.6);
            _splashTide.Update(true, _splashTideShown, string.Empty);
        }
        bool entered = _splashLoader.Value > .85;
        bool filled = _splashTideShown >= .995; // the rail's water shows the interface is ready
        if (!_splashExiting && (entered && _splashReady && t > 2.2 && filled || now > SplashMaxSeconds + 4))
        {
            _splashExiting = true;
            _splashTide?.Update(true, 1, string.Empty);
            // The rail is at rest from here on: render it live again before the tone starts changing.
            NavigationSurface.CacheMode = null;
            if (SplashGlass.Background is SolidColorBrush tone)
                NavigationSurface.Background = _splashRailTone = new SolidColorBrush(tone.Color);
            _splashExit.Retarget(1);
        }
        if (_splashExiting && _splashExit.Value > .995) { FinishSplash(); return; }
        ApplySplash();
    }

    private void ApplySplash()
    {
        if (!_splashActive || _splashFade is null || _splashRise is null || _splashReveal is null || _splashSlide is null || _splashLogo is null ||
            _splashText is null || _splashLoader is null || _splashExit is null) return;
        double reveal = _splashReveal.Value, slide = _splashSlide.Value;
        // Stages 1-2: the tile fades in at the bottom-right and travels up the right edge.
        NavigationSurface.Opacity = Math.Clamp(_splashFade.Value, 0, 1);
        _splashRailShift.Y = _splashStartY * (1 - _splashRise.Value);
        // Stage 3: the rail extends down from the tile, then glides left.
        double height = NavigationSurface.ActualHeight;
        double visible = SplashTile + (height - SplashTile) * Math.Clamp(reveal, 0, 1.04);
        _splashRailShift.X = _splashStartX * (1 - slide);
        NavigationSurface.Clip = new RectangleGeometry(new Rect(0, 0, NavigationSurface.ActualWidth, Math.Min(height, Math.Max(SplashTile, visible))),
            NavigationSurface.CornerRadius.TopLeft, NavigationSurface.CornerRadius.TopLeft);
        // Stage 2: the body unfolds to the right of the moving rail, growing to its full width as the rail arrives.
        Rect body = SplashBodyRect();
        if (body.Width <= 0) BodyFrame.Clip = Geometry.Empty;
        else
        {
            Geometry revealed = new RectangleGeometry(body, BodyFrame.CornerRadius.TopLeft, BodyFrame.CornerRadius.TopLeft);
            if (_controlsCutout is { } notch && body.IntersectsWith(notch.Rect))
                revealed = new CombinedGeometry(GeometryCombineMode.Exclude, revealed, new RectangleGeometry(notch.Rect, notch.Radius, notch.Radius));
            BodyFrame.Clip = revealed;
        }
        // The existing exit spring drives both the logo's depth pose and the staggered workspace reveal.
        double exit = Math.Clamp(_splashExit.Value, 0, 1);
        double logoDepth = SplashSegment(exit, 0, .72);
        double workspace = SplashSegment(exit, .50, 1);
        double logo = _splashLogo.Value;
        SplashLogo.Opacity = Math.Clamp(logo * 1.4, 0, 1) * (1 - SplashSegment(exit, .10, .72));
        SplashLogoScale.ScaleX = SplashLogoScale.ScaleY = (.6 + .4 * logo) * (1 + .68 * logoDepth);
        SplashStatusContent.Opacity = 1 - SplashSegment(exit, 0, .48);
        double text = _splashText.Value;
        SplashTitle.Opacity = Math.Clamp(text, 0, 1);
        SplashTitleShift.Y = 14 * (1 - text);
        SplashLoader.Opacity = Math.Clamp(_splashLoader.Value, 0, 1);
        if (SplashStage.HasAnimatedProperties == false) SplashStage.Opacity = Math.Clamp(_splashLoader.Value, 0, 1);
        // The loader gives way first. As the enlarged logo disappears, reveal the workspace in place.
        SplashGlass.Opacity = 1 - workspace;
        SetSplashUnderlay(workspace);
        // The rail's tone glides from the splash glass to its own chrome while the splash leaves, so restoring the
        // chrome resource at the end changes nothing visible (an abrupt swap read as a flash of the blur).
        if (_splashRailTone is not null && SplashGlass.Background is SolidColorBrush from && TryFindResource("GlassChromeBrush") is SolidColorBrush to)
            _splashRailTone.Color = LerpColor(from.Color, to.Color, workspace);
        WindowControlsSurface.Opacity = Math.Clamp(logo, 0, 1);
        IReadOnlyList<GlassWindowRegion> blur = SplashBackdropRegions();
        if (!blur.SequenceEqual(_lastSplashBackdrop)) { _lastSplashBackdrop = blur; RegionsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static double SplashSegment(double progress, double start, double end) => Math.Clamp((progress - start) / (end - start), 0, 1);

    private void SetSplashUnderlay(double opacity)
    {
        ApplicationBackdrop.Opacity = opacity;
        WorkspaceContentLayer.Opacity = opacity;
        WorkspaceContentLayer.IsHitTestVisible = opacity >= 1;
    }

    // The body's revealed part, in BodyFrame coordinates: the path the rail has swept, from its right edge to the
    // body's right edge.
    private Rect SplashBodyRect()
    {
        if (_splashSlide is null || _splashSlide.Value <= .001) return Rect.Empty;
        double railRight = NavigationSurface.TranslatePoint(new Point(NavigationSurface.ActualWidth, 0), BodyFrame).X;
        double left = Math.Clamp(railRight, 0, BodyFrame.ActualWidth);
        double width = BodyFrame.ActualWidth - left;
        return width <= 1 ? Rect.Empty : new Rect(left, 0, width, BodyFrame.ActualHeight);
    }

    private static Color LerpColor(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private IReadOnlyList<GlassWindowRegion> SplashRegions()
    {
        var regions = new List<GlassWindowRegion>(2);
        if (NavigationSurface.Clip is RectangleGeometry rail && !rail.Rect.IsEmpty)
            regions.Add(new GlassWindowRegion(NavigationSurface.TransformToAncestor(this).TransformBounds(rail.Rect), NavigationSurface.CornerRadius.TopLeft));
        Rect body = SplashBodyRect();
        if (!body.IsEmpty && body.Width > 1)
            regions.Add(new GlassWindowRegion(BodyFrame.TransformToAncestor(this).TransformBounds(body), BodyFrame.CornerRadius.TopLeft));
        if (_splashExiting)
            regions.Add(new GlassWindowRegion(WindowControlsSurface.TransformToAncestor(this).TransformBounds(new Rect(WindowControlsSurface.RenderSize)), WindowControlsChrome.CornerRadius.TopLeft));
        return regions;
    }

    private void FinishSplash()
    {
        CompositionTarget.Rendering -= OnSplashFrame;
        if (!_splashRequested && !_splashActive && SplashLayer.Visibility != Visibility.Visible) return;
        _splashRequested = _splashActive = _splashExiting = false;
        _splashRailTone = null;
        SplashLayer.Visibility = Visibility.Collapsed;
        SetSplashUnderlay(1);
        try {
            foreach (SpringMotion motion in new[] { _splashFade, _splashRise, _splashReveal, _splashSlide, _splashLogo, _splashText, _splashLoader, _splashExit }) motion?.Dispose();
            NavigationSurface.Opacity = 1;
            SplashLoader.IsActive = false;
            NavigationSurface.ClearValue(UIElement.ClipProperty);
            NavigationSurface.CacheMode = null;
            NavigationSurface.SetResourceReference(Border.BackgroundProperty, "GlassChromeBrush");
            NavigationSurface.RenderTransform = Transform.Identity;
            NavigationSurface.IsHitTestVisible = true;
            WindowControlsSurface.Opacity = 1;
            if (_splashTide is global::PCL.GlassTideProgress tide)
            {
                _splashTide = null;
                tide.Update(false, 1, string.Empty);
                var removal = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                removal.Tick += (_, _) => { removal.Stop(); NavigationRailContent.Children.Remove(tide); };
                removal.Start();
            }
            RevealRailItems();
            BodyFrame.Clip = null;
            _lastBodyClipKey = null; // let the window-controls cutout rebuild the body clip
            SyncSurfaceRegions();
            ShellRoot.SetResourceReference(Panel.BackgroundProperty, "GlassWindowBrush");
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        } finally {
            SplashFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Loading progress shown by the splash (instance list, first page, preloads); set by the shell.</summary>
    public double SplashProgress { get; set; }

    /// <summary>The loading stage shown under the splash loader (crossfades when it changes).</summary>
    public string SplashStageText
    {
        get => _splashStageTarget;
        set
        {
            if (value == _splashStageTarget) return;
            _splashStageTarget = value ?? string.Empty;
            if (!_splashActive || !MotionPreferences.AllowsAnimation) { SplashStage.Text = _splashStageTarget; return; }
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) =>
            {
                SplashStage.Text = _splashStageTarget;
                SplashStage.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            };
            SplashStage.BeginAnimation(OpacityProperty, fadeOut);
        }
    }
    private string _splashStageTarget = string.Empty;
    private global::PCL.GlassTideProgress _splashTide;
    private double _splashTideStart, _splashTideShown;

    // The rail's own items, top to bottom: page icons, the selection box, the collapse toggle and the account.
    private FrameworkElement[] RailItems => new FrameworkElement[] { NavLaunch, NavDownload, NavFavorites, NavSettings, NavOther, NavSelection, RailToggleButton, AccountSurface };

    // One after another from the top, each settles from slightly in front of the rail (larger, transparent) to its
    // place on an iOS-like spring; elements that already carry their own transform only fade.
    private void RevealRailItems()
    {
        FrameworkElement[] items = RailItems;
        if (!MotionPreferences.AllowsAnimation) { foreach (UIElement item in items) item.Opacity = 1; return; }
        for (int index = 0; index < items.Length; index++)
        {
            FrameworkElement item = items[index];
            bool scalable = item.RenderTransform is null || item.RenderTransform == Transform.Identity;
            ScaleTransform? depth = null;
            if (scalable)
            {
                depth = new ScaleTransform(1.25, 1.25);
                item.RenderTransformOrigin = new Point(.5, .5);
                item.RenderTransform = depth;
            }
            SpringMotion? motion = null;
            motion = new SpringMotion(item, 0, value =>
            {
                item.Opacity = Math.Clamp(value * 1.2, 0, 1);
                if (depth is not null) depth.ScaleX = depth.ScaleY = 1.25 - .25 * value;
                if (Math.Abs(value - 1) < .002 && motion is { IsRunning: false })
                {
                    if (depth is not null && ReferenceEquals(item.RenderTransform, depth)) item.RenderTransform = Transform.Identity;
                    motion.Dispose();
                }
            }, new SpringParameters(.46, .8));
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120 + 55 * index) };
            delay.Tick += (_, _) => { delay.Stop(); motion.Retarget(1); };
            delay.Start();
        }
    }

    private void SyncSurfaceRegions()
    {
        UpdateWallpaperClip();
        NotifyRegionsChanged();
    }

    private void NotifyRegionsChanged()
    {
        if (!IsLoaded) return;
        IReadOnlyList<GlassWindowRegion> regions = GetSurfaceRegions();
        bool animating = IsRailAnimating;
        if (_lastRailAnimating == animating && _lastRegions.Count == regions.Count && _lastRegions.SequenceEqual(regions)) return;
        _lastRailAnimating = animating;
        _lastRegions = regions;
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppLogoImage.Source is null)
        {
            try { AppLogoImage.Source = (ImageSource)Application.LoadComponent(new Uri("/LivingPCL;component/Images/Branding/PCLLF.Logo.xaml", UriKind.Relative)); }
            catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or NotSupportedException) { }
        }
        SyncSurfaceRegions();
        ScheduleLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => WindowControlsBodyBrush.Visual = null;

    private void OnViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) UpdateWallpaperClip();
        else WindowControlsBodyBrush.Visual = null;
    }

    private static double SecondaryWidth(bool wide, bool compact) => wide ? (compact ? 284 : 304) : (compact ? 230 : 254);

    private SpringMotion _columnMotion;
    private double _columnTarget = double.NaN;

    /// <summary>
    /// Sidebar column width (wider on the launch page). Entering or leaving home glides on the same spring family as the
    /// sidebar flip instead of snapping the content area sideways; resizes and layout passes only retarget it.
    /// </summary>
    private void SetSecondaryColumnWidth(double width, bool animate)
    {
        if (!double.IsNaN(_columnTarget) && Math.Abs(_columnTarget - width) < .01 &&
            (animate || _columnMotion?.IsRunning != true)) return;
        _columnTarget = width;
        if (!animate)
        {
            if (_columnMotion is not null) _columnMotion.Snap(width);
            else SecondaryColumn.Width = new GridLength(width);
            return;
        }
        double current = SecondaryColumn.Width.IsAbsolute ? SecondaryColumn.Width.Value : width;
        _columnMotion ??= new SpringMotion(this, current, value => SecondaryColumn.Width = new GridLength(Math.Max(0, value)), SidebarFlipSpring) { MaxFrameSeconds = TransitionFrameCap };
        _columnMotion.Retarget(width);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = IsCompactViewport;
        MainContentGrid.Margin = new Thickness(compact ? 16 : 24, 0, compact ? 16 : 24, 24);
        UpdateHeaderLayout();
        double columnWidth = SecondaryWidth(_state.IsHome || _workspaceOpen, compact);
        SetSecondaryColumnWidth(columnWidth, animate: false);
        AccountPopover.HorizontalAlignment = HorizontalAlignment.Left;
        AccountPopover.VerticalAlignment = VerticalAlignment.Bottom;
        AccountPopover.Margin = new Thickness(84 + _leftExpansionAllowance, 0, 0, 16);
        AccountPopover.MaxHeight = Math.Max(160, ActualHeight - 40);
        if (_workspaceOpen && _workspaceMotion is { IsRunning: false } && !_navigationMotion.IsRunning &&
            AccountButton.ActualHeight > 0 && NavigationButtons.ActualHeight > 0)
            _navigationMotion.Snap(AccountButton.TranslatePoint(new Point(0, 0), NavigationButtons).Y);
        SyncSurfaceRegions();
        ScheduleLayout();
    }

    private void UpdateHeaderLayout()
    {
        bool hasPageHeading = !_state.IsHome;
        double inset = IsCompactViewport ? 16 : 24;
        PageHeaderRow.Height = new GridLength(hasPageHeading ? 88 : 60);
        PageHeading.VerticalAlignment = hasPageHeading ? VerticalAlignment.Top : VerticalAlignment.Center;
        // Page headings share one baseline below the caption while retaining its click area.
        PageHeading.Margin = new Thickness(inset, hasPageHeading ? 40 : 0, 100, 0);
    }

    private void UpdateWallpaperClip()
    {
        if (MainSurface.ActualWidth <= 0 || MainSurface.ActualHeight <= 0) return;
        IReadOnlyList<GlassWindowRegion> regions = GetSurfaceRegions();
        GeneralTransform transform = this.TransformToVisual(_surfaceMaskHost);
        UpdateClip(FrameClip, regions, transform);
        Rect viewport = transform.TransformBounds(new Rect(RenderSize));
        if (FrameMask.Viewbox != viewport) FrameMask.Viewbox = viewport;
        if (FrameMask.Viewport != viewport) FrameMask.Viewport = viewport;
        if (BodyContentRoot.ActualWidth > 0 && BodyContentRoot.ActualHeight > 0)
        {
            Rect innerBounds = new(BodyContentRoot.RenderSize);
            double innerRadius = Math.Max(0, BodyFrame.CornerRadius.TopRight - Math.Max(BodyFrame.BorderThickness.Right, BodyFrame.BorderThickness.Top));
            if (_bodyContentClip.Rect != innerBounds) _bodyContentClip.Rect = innerBounds;
            if (_bodyContentClip.RadiusX != innerRadius) _bodyContentClip.RadiusX = _bodyContentClip.RadiusY = innerRadius;
            if (!ReferenceEquals(BodyContentRoot.Clip, _bodyContentClip))
                BodyContentRoot.Clip = _bodyContentClip;
        }
        if (IsVisible && WindowControlsWallpaperFrost.Visibility == Visibility.Visible &&
            BodyFrame.ActualWidth > 0 && BodyFrame.ActualHeight > 0 &&
            BodyContentRoot.ActualWidth > 0 && BodyContentRoot.ActualHeight > 0 &&
            WindowControlsSurface.ActualWidth > 0 && WindowControlsSurface.ActualHeight > 0)
        {
            Rect capsuleBounds = new(WindowControlsSurface.RenderSize);
            double capsuleRadius = WindowControlsChrome.CornerRadius.TopLeft;
            if (_controlsWallpaperClip.Rect != capsuleBounds) _controlsWallpaperClip.Rect = capsuleBounds;
            if (_controlsWallpaperClip.RadiusX != capsuleRadius) _controlsWallpaperClip.RadiusX = _controlsWallpaperClip.RadiusY = capsuleRadius;
            if (!ReferenceEquals(WindowControlsWallpaperFrost.Clip, _controlsWallpaperClip))
                WindowControlsWallpaperFrost.Clip = _controlsWallpaperClip;

            const double blurPad = 36;
            MusicDockSurface.UpdateBackdrop(BodyFrame, BodyBackdropLayer);
            // The playlist overlaps the workspace. Its frosted slice needs the painted page,
            // including the frame's background underneath translucent cards and wallpaper.
            _musicDockCompanion?.UpdateBackdrop(BodyFrame, BodyFrame);
            UpdatePlaylistCutout(_musicDockCompanion);
            Rect bodyInControls = BodyFrame.TransformToVisual(WindowControlsSurface).TransformBounds(new Rect(BodyFrame.RenderSize));
            double sliceLeft = Math.Max(bodyInControls.Left, -blurPad);
            double sliceTop = bodyInControls.Top;
            double sliceRight = bodyInControls.Right;
            double sliceBottom = Math.Min(bodyInControls.Bottom, capsuleBounds.Height + blurPad);
            double sliceWidth = Math.Max(0, sliceRight - sliceLeft);
            double sliceHeight = Math.Max(0, sliceBottom - sliceTop);
            Canvas.SetLeft(WindowControlsBodyFrost, sliceLeft);
            Canvas.SetTop(WindowControlsBodyFrost, sliceTop);
            WindowControlsBodyFrost.Width = sliceWidth;
            WindowControlsBodyFrost.Height = sliceHeight;
            var bodyCorner = new CornerRadius(0, BodyFrame.CornerRadius.TopRight, 0, 0);
            WindowControlsBodyFrostBase.CornerRadius = bodyCorner;
            WindowControlsBodyFrostVisual.CornerRadius = bodyCorner;

            Rect contentSlice = WindowControlsSurface.TransformToVisual(BodyBackdropLayer).TransformBounds(new Rect(sliceLeft, sliceTop, sliceWidth, sliceHeight));
            if (!ReferenceEquals(WindowControlsBodyBrush.Visual, BodyBackdropLayer))
                WindowControlsBodyBrush.Visual = BodyBackdropLayer;
            if (WindowControlsBodyBrush.Viewbox != contentSlice)
                WindowControlsBodyBrush.Viewbox = contentSlice;

            Rect fullBody = new(BodyFrame.RenderSize);
            Rect controlsInBody = WindowControlsSurface.TransformToVisual(BodyFrame).TransformBounds(capsuleBounds);
            var insetCutout = new Rect(
                controlsInBody.Left + 1,
                controlsInBody.Top - 2,
                Math.Max(0, controlsInBody.Width + 2),
                Math.Max(0, controlsInBody.Height + 1));
            double cutoutRadius = Math.Max(0, capsuleRadius - 1);
            _controlsCutout = (insetCutout, cutoutRadius);
            Rect dockCutout = MusicDockShown ? MusicDockCutout() : Rect.Empty;
            double dockRadius = cutoutRadius;
            // The dock notch is part of the key: it follows the dock while it grows out of / folds into the corner.
            var bodyClipKey = (fullBody, insetCutout, BodyFrame.CornerRadius.TopRight, cutoutRadius, dockCutout);
            if (_lastBodyClipKey != bodyClipKey)
            {
                _lastBodyClipKey = bodyClipKey;
                Geometry cutouts = new RectangleGeometry(insetCutout, cutoutRadius, cutoutRadius);
                if (!dockCutout.IsEmpty)
                    cutouts = new CombinedGeometry(GeometryCombineMode.Union, cutouts, new RectangleGeometry(dockCutout, dockRadius, dockRadius));
                var bodyCut = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new RectangleGeometry(fullBody, BodyFrame.CornerRadius.TopLeft, BodyFrame.CornerRadius.TopLeft),
                    cutouts);
                bodyCut.Freeze();
                if (!_splashActive && !_farewellActive) BodyFrame.Clip = bodyCut; // the splash / farewell own the body clip while they run
            }
        }
        else
        {
            _lastBodyClipKey = null;
            _controlsCutout = null;
            MusicDockSurface.ClearBackdrop();
            _musicDockCompanion?.ClearBackdrop();
            UpdatePlaylistCutout(null);
            if (WindowControlsBodyBrush.Visual is not null) WindowControlsBodyBrush.Visual = null;
            if (BodyFrame.Clip is not null && !_splashActive && !_farewellActive) BodyFrame.Clip = null;
        }
    }

    private readonly RectangleGeometry _playlistBody = new(), _playlistNotch = new();
    private readonly SolidColorBrush _pageBehindPlaylist = new(Colors.White);
    private CombinedGeometry _outsidePlaylist;
    private DrawingBrush _playlistFadeMask;

    private void UpdatePlaylistCutout(GlassMusicSurface playlist)
    {
        if (playlist is not { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Opacity: > .01 } ||
            !playlist.Chrome.IsTransparencyActive || _splashActive || _farewellActive)
        {
            BodyPresentation.Clip = null;
            BodyPresentation.OpacityMask = null;
            return;
        }
        // Cut the sharp page out of its presentation only. The playlist's brush still sees
        // the intact BodyFrame, including its border, with room for the Gaussian halo.
        Rect body = new(BodyPresentation.RenderSize);
        Rect notch = playlist.TransformToVisual(BodyPresentation).TransformBounds(new Rect(playlist.RenderSize));
        double radius = playlist.Chrome.CornerRadius.TopLeft * notch.Width / playlist.ActualWidth;
        notch.Inflate(-1, -1);
        if (notch.IsEmpty) return;
        if (_playlistBody.Rect != body) _playlistBody.Rect = body;
        if (_playlistNotch.Rect != notch) _playlistNotch.Rect = notch;
        radius = Math.Max(0, radius - 1);
        if (_playlistNotch.RadiusX != radius) _playlistNotch.RadiusX = _playlistNotch.RadiusY = radius;
        if (_outsidePlaylist is null)
        {
            _outsidePlaylist = new CombinedGeometry(GeometryCombineMode.Exclude, _playlistBody, _playlistNotch);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, _outsidePlaylist));
            drawing.Children.Add(new GeometryDrawing(_pageBehindPlaylist, null, _playlistNotch));
            _playlistFadeMask = new DrawingBrush(drawing) { ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute };
        }
        if (playlist.Opacity >= .999)
        {
            BodyPresentation.Clip = _outsidePlaylist;
            BodyPresentation.OpacityMask = null;
        }
        else
        {
            _pageBehindPlaylist.Opacity = 1 - playlist.Opacity;
            if (_playlistFadeMask.Viewbox != body) _playlistFadeMask.Viewbox = _playlistFadeMask.Viewport = body;
            BodyPresentation.OpacityMask = _playlistFadeMask;
            BodyPresentation.Clip = null;
        }
    }

    private Rect MusicDockCutout()
    {
        Rect capsuleBounds = new(MusicDockSurface.RenderSize);
        Rect dockInBody = MusicDockSurface.TransformToVisual(BodyFrame).TransformBounds(capsuleBounds);
        return new Rect(dockInBody.Left + 1, dockInBody.Top + 1, Math.Max(0, dockInBody.Width + 2), Math.Max(0, dockInBody.Height + 1));
    }

    private static void UpdateClip(GeometryGroup clip, IReadOnlyList<GlassWindowRegion> regions, GeneralTransform transform)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            GlassWindowRegion region = regions[index];
            Rect bounds = transform.TransformBounds(region.Bounds);
            if (index >= clip.Children.Count) clip.Children.Add(new RectangleGeometry());
            var rectangle = (RectangleGeometry)clip.Children[index];
            if (rectangle.Rect != bounds) rectangle.Rect = bounds;
            if (rectangle.RadiusX != region.CornerRadius) rectangle.RadiusX = rectangle.RadiusY = region.CornerRadius;
        }
        while (clip.Children.Count > regions.Count) clip.Children.RemoveAt(clip.Children.Count - 1);
    }

    private void OnSurfaceGeometryChanged(object sender, SizeChangedEventArgs e) => SyncSurfaceRegions();
    private void HomeDeck_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeHomeDeck();
    private void NavigationButtons_SizeChanged(object sender, SizeChangedEventArgs e) => SetSelectedNavigation(ShouldSnapMotion);

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.LaunchOptionsAvailable) return;
        _expanded = !_expanded;
        if (_expanded) AuxiliaryCards.Visibility = Visibility.Visible;
        ExpandButton.ToolTip = _expanded ? "收起启动选项" : "更多启动选项";
        AutomationProperties.SetName(ExpandButton, _expanded ? "收起启动选项" : "展开启动选项");
        _expansionMotion.Retarget(_expanded ? 1 : 0, ShouldSnapMotion);
    }

    private global::PCL.GlassTideProgress _launchTide;

    private global::PCL.GlassTideProgress EnsureLaunchTide()
    {
        if (_launchTide is not null) return _launchTide;
        _launchTide = new global::PCL.GlassTideProgress(11, "终止启动") { Margin = new Thickness(-12, 0, -12, 0), Covers = InstanceCenter };
        _launchTide.StopRequested += (_, _) => { if (_state.IsLaunching) CancelRequested?.Invoke(this, EventArgs.Empty); };
        if (SecondarySurface.Child is Grid card)
        {
            Grid.SetRowSpan(_launchTide, Math.Max(1, card.RowDefinitions.Count));
            Panel.SetZIndex(_launchTide, 3);
            card.Children.Add(_launchTide);
        }
        return _launchTide;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLaunching) CancelRequested?.Invoke(this, EventArgs.Empty);
        else StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetAccountOpen(false);
        SetAccountWorkspaceOpen(false);
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int page)) NavigationRequested?.Invoke(page);
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        AccountRequested?.Invoke(this, EventArgs.Empty);
        // With a full account workspace supplied by the shell, the avatar opens that page instead of the small popover.
        if (AccountSideHost.Content is not null && AccountSceneHost.Content is not null)
        {
            SetAccountWorkspaceOpen(!_workspaceOpen);
            return;
        }
        SetAccountOpen(!_accountOpen);
    }

    private bool _workspaceOpen;
    private readonly TranslateTransform _secondaryShift = new();
    private SpringMotion _workspaceMotion;

    /// <summary>The signed-in account's display name, as shown on the rail avatar.</summary>
    public string AccountNameLabel => _state.AccountName ?? string.Empty;
    /// <summary>The signed-in account's avatar image, as shown on the rail.</summary>
    public ImageSource AccountAvatarSource => AccountAvatarImage.Source;

    /// <summary>True while the account workspace is shown over the secondary panel and main column.</summary>
    public bool IsAccountWorkspaceOpen => _workspaceOpen;

    /// <summary>Supplies the account workspace: a side card (replaces the secondary panel) and a main scene.</summary>
    // ---------- Bottom-right dock (music) ----------

    /// <summary>Content of the bottom-right dock's chrome (the music bar); null empties the dock.</summary>
    public void SetMusicDock(FrameworkElement content)
    {
        MusicDockSurface.Host.Content = content;
        MusicDockSurface.SizeChanged -= OnMusicDockSizeChanged;
        MusicDockSurface.SizeChanged += OnMusicDockSizeChanged;
    }

    /// <summary>Shows or hides the dock (it protrudes from the corner only while shown) and refreshes the window shape.</summary>
    public void SetMusicDockVisible(bool visible)
    {
        if (_musicDockVisible == visible) return;
        _musicDockVisible = visible;
        if (visible) MusicDockSurface.Visibility = Visibility.Visible;
        _musicDockMotion ??= new SpringMotion(this, 0, ApplyMusicDock, new SpringParameters(.42, .78));
        _musicDockMotion.Retarget(visible ? 1 : 0, ShouldSnapMotion);
        _lastBodyClipKey = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncSurfaceRegions));
    }

    private bool _musicDockVisible;
    private double _musicDockContentOpacity = 1, _musicDockVeilOpacity = 1;
    private SpringMotion _musicDockMotion;
    private readonly ScaleTransform _musicDockScale = new(1, 1);
    // Closing: the music dock and its playlist leave with the window controls (a bubble shrinking about its centre).
    private readonly ScaleTransform _musicDockBye = new(1, 1), _musicPlaylistBye = new(1, 1);
    private TransformGroup _musicDockTransform;
    private double _musicDockByeOpacity = 1;
    private readonly System.Windows.Media.Effects.BlurEffect _musicDockVeil = new() { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };

    // The dock grows out of (and shrinks back into) the window's bottom-right corner. The layout never moves for it:
    // the body is only notched around the dock, as around the window controls.
    private void ApplyMusicDock(double value)
    {
        double v = Math.Clamp(value, 0, 1);
        // The chrome (and the frost behind it) stays opaque and grows out of the corner, so the notch never shows the
        // window colour through a half-faded dock; only the controls fade.
        MusicDockSurface.Opacity = _musicDockByeOpacity;
        _musicDockContentOpacity = Math.Clamp(value * 1.4 - .3, 0, 1);
        MusicDockSurface.Host.Opacity = _musicDockContentOpacity * _musicDockVeilOpacity;
        MusicDockSurface.RenderTransformOrigin = new Point(1, 1);
        _musicDockTransform ??= new TransformGroup { Children = { _musicDockScale, _musicDockBye } };
        MusicDockSurface.RenderTransform = _musicDockTransform;
        _musicDockScale.ScaleX = _musicDockScale.ScaleY = Math.Max(.001, Math.Clamp(value, 0, 1.06));
        // The notch and window shape follow the scaled dock on every frame (they read its transformed bounds).
        if (MusicDockSurface.Visibility == Visibility.Visible) SyncSurfaceRegions();
        if (v < .01 && !_musicDockVisible) { MusicDockSurface.Visibility = Visibility.Collapsed; _lastBodyClipKey = null; SyncSurfaceRegions(); }
    }

    /// <summary>Veils the dock with a dialog's blur (0 = clear, 1 = fully under the veil).</summary>
    public void SetMusicDockVeil(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        _musicDockVeil.Radius = 6 * p;
        MusicDockSurface.Chrome.Effect = p > .001 ? _musicDockVeil : null;
        _musicDockVeilOpacity = 1 - .35 * p;
        MusicDockSurface.Host.Opacity = _musicDockContentOpacity * _musicDockVeilOpacity;
        MusicDockSurface.IsHitTestVisible = p < .5;
    }

    internal const double MusicPlaylistGap = 8;
    private GlassMusicSurface _musicDockCompanion;

    // Fades and shrinks the dock and the playlist about their centres (0 = in place, 1 = gone).
    private void ApplyMusicFarewell(double progress)
    {
        double scale = 1 - .22 * progress;
        _musicDockByeOpacity = 1 - progress;
        if (MusicDockSurface.Visibility == Visibility.Visible)
        {
            _musicDockTransform ??= new TransformGroup { Children = { _musicDockScale, _musicDockBye } };
            if (!ReferenceEquals(MusicDockSurface.RenderTransform, _musicDockTransform)) MusicDockSurface.RenderTransform = _musicDockTransform;
            // RenderTransformOrigin is the bottom-right corner: the centre lies half the size up and to the left of it.
            _musicDockBye.CenterX = -MusicDockSurface.ActualWidth / 2;
            _musicDockBye.CenterY = -MusicDockSurface.ActualHeight / 2;
            MusicDockSurface.Opacity = _musicDockByeOpacity;
        }
        _musicDockBye.ScaleX = _musicDockBye.ScaleY = scale;
        if (_musicDockCompanion is { } playlist)
        {
            if (playlist.RenderTransform is TransformGroup group && !group.IsFrozen && !group.Children.Contains(_musicPlaylistBye)) group.Children.Add(_musicPlaylistBye);
            _musicPlaylistBye.CenterX = -playlist.ActualWidth / 2;
            _musicPlaylistBye.CenterY = -playlist.ActualHeight / 2;
            _musicPlaylistBye.ScaleX = _musicPlaylistBye.ScaleY = scale;
            // A shown playlist fades with the dock; when closing is cancelled, an open one (still hit-testable) comes back.
            if (playlist.Visibility == Visibility.Visible)
            {
                if (progress > 0) playlist.Opacity = Math.Min(playlist.Opacity, 1 - progress);
                else if (playlist.IsHitTestVisible) playlist.Opacity = 1;
            }
        }
    }

    internal void AddMusicDockCompanion(GlassMusicSurface companion)
    {
        if (MusicDockSurface.Parent is not Panel layer) return;
        _musicDockCompanion = companion;
        Panel.SetZIndex(companion, Panel.GetZIndex(MusicDockSurface) + 1);
        layer.Children.Add(companion);
        companion.SizeChanged += OnSurfaceGeometryChanged;
        UpdateMusicPlaylistPlacement();
    }

    private void UpdateMusicPlaylistPlacement()
    {
        if (_musicDockCompanion is not { } playlist) return;
        var margin = new Thickness(0, 0, 0, MusicDockSurface.ActualHeight + MusicPlaylistGap);
        if (playlist.Margin != margin) playlist.Margin = margin;
        if (MusicDockSurface.ActualWidth > 0 && playlist.Width != MusicDockSurface.ActualWidth)
            playlist.Width = MusicDockSurface.ActualWidth;
    }

    private void OnMusicDockSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateMusicPlaylistPlacement();
        _lastBodyClipKey = null;
        SyncSurfaceRegions();
    }

    internal void RefreshMusicDockRegions() => SyncSurfaceRegions();

    /// <summary>The dock itself (for placing things above it).</summary>
    public FrameworkElement MusicDock => MusicDockSurface;

    internal Rect MusicOverlayBounds(Visual relativeTo)
    {
        Rect bounds = MusicDockShown ? Bounds(MusicDockSurface) : Rect.Empty;
        if (_musicDockCompanion is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Opacity: > .01 } playlist)
            bounds.Union(Bounds(playlist));
        return bounds;

        Rect Bounds(FrameworkElement surface) => surface.TransformToVisual(relativeTo).TransformBounds(new Rect(surface.RenderSize));
    }

    private bool MusicDockShown => MusicDockSurface.IsVisible && MusicDockSurface.ActualWidth > 0 && MusicDockSurface.ActualHeight > 0;

    public void SetAccountWorkspace(FrameworkElement side, FrameworkElement scene)
    {
        AccountSideHost.Content = side;
        AccountSceneHost.Content = scene;
        if (side is null || scene is null) SetAccountWorkspaceOpen(false, immediate: true);
    }

    /// <summary>
    /// Opens or closes the account workspace. The current page recedes and fades while the account side card and
    /// scene rise into place on the sidebar spring; any navigation closes it again.
    /// </summary>
    public void SetAccountWorkspaceOpen(bool open, bool immediate = false)
    {
        open &= AccountSideHost.Content is not null && AccountSceneHost.Content is not null;
        if (_workspaceOpen == open && !immediate) return;
        _workspaceOpen = open;
        if (open) { SetAccountOpen(false); AccountSideSurface.Visibility = AccountSceneHost.Visibility = Visibility.Visible; }
        _workspaceMotion ??= new SpringMotion(this, 0, ApplyAccountWorkspace, SidebarFlipSpring) { MaxFrameSeconds = TransitionFrameCap };
        _workspaceMotion.Retarget(open ? 1 : 0, immediate || ShouldSnapMotion);
        AutomationProperties.SetHelpText(AccountButton, open ? "账号页面已打开" : "打开账号页面");
        AccountSideSurface.IsHitTestVisible = AccountSceneHost.IsHitTestVisible = open;
        // The page underneath is not just hidden but inert: no clicks, focus or keyboard activation reach it.
        MainColumn.IsHitTestVisible = !open; MainColumn.IsEnabled = !open;
        if (SecondarySurface.Child is UIElement secondary) { secondary.IsHitTestVisible = !open; secondary.IsEnabled = !open; }
        SetSelectedNavigation(ShouldSnapMotion || immediate);
        SetSecondaryColumnWidth(SecondaryWidth(_state.IsHome || open, IsCompactViewport), animate: !(ShouldSnapMotion || immediate));
    }

    // Same rhythm as switching pages: the current page leaves first (fading and receding), then the account page
    // arrives — the side card flips up from below like the sidebar, the scene rises — so the two never overlap.
    private void ApplyAccountWorkspace(double value)
    {
        double outgoing = Math.Clamp(value / .38, 0, 1);
        double incoming = Math.Clamp((value - .3) / .55, 0, 1);
        double eased = 1 - Math.Pow(1 - incoming, 2);
        double travel = Math.Clamp((value - .3) / .7, 0, 1.12); // keeps the spring's overshoot for the rise
        AccountSideSurface.Opacity = eased;
        AccountSideTranslation.Y = SidebarFlipTravel * (1 - travel);
        AccountSceneHost.Opacity = eased;
        AccountSceneTranslation.Y = PageRise * (1 - travel);
        AccountSceneScale.ScaleX = AccountSceneScale.ScaleY = .97 + .03 * Math.Min(travel, 1);
        double leave = 1 - outgoing * outgoing * (3 - 2 * outgoing);
        MainColumn.Opacity = leave;
        if (SecondarySurface.RenderTransform is not TransformGroup)
            SecondarySurface.RenderTransform = new TransformGroup { Children = { _sidebarFlipOffset, _secondaryShift } };
        // The whole previous sidebar card leaves (and returns) with the page, so nothing stays layered underneath.
        SecondarySurface.Opacity = leave;
        SecondarySurface.Visibility = leave <= MotionTokens.VisibilityThreshold ? Visibility.Hidden : Visibility.Visible;
        _secondaryShift.Y = 24 * outgoing;
        if (!_workspaceOpen && value <= MotionTokens.VisibilityThreshold)
            AccountSideSurface.Visibility = AccountSceneHost.Visibility = Visibility.Hidden;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _keyboardInteraction = true;
        if (e.Key == Key.Escape && _workspaceOpen)
        {
            SetAccountWorkspaceOpen(false);
            AccountButton.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _accountOpen)
        {
            SetAccountOpenCore(false, true);
            AccountButton.Focus();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _keyboardInteraction = false;
        if (e.OriginalSource is not DependencyObject source) return;
        if (_accountOpen && !IsWithin(source, AccountPopover) && !IsWithin(source, AccountButton)) SetAccountOpen(false);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject item = source; item != null; item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (ReferenceEquals(item, ancestor)) return true;
        return false;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestWindowDrag((DependencyObject)sender, e);
    }

    private void AccountWorkspace_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_workspaceOpen) RequestWindowDrag((DependencyObject)sender, e);
    }

    private void RequestWindowDrag(DependencyObject boundary, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        for (DependencyObject item = source; item != null && !ReferenceEquals(item, boundary);
             item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
        {
            if (item is ButtonBase or TextBoxBase or PasswordBox or Selector or RangeBase or Thumb or System.Windows.Documents.Hyperlink ||
                (bool)item.GetValue(PreventWindowDragProperty)) return;
        }
        DragRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void DependencyCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_updatingState) DependencyCheckChanged?.Invoke(DependencyCheckBox.IsChecked == true); }
    private void AutoFillCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_updatingState) AutoFillChanged?.Invoke(AutoFillCheckBox.IsChecked == true); }
    private void SelectInstanceButton_Click(object sender, RoutedEventArgs e) => SelectInstanceRequested?.Invoke(this, EventArgs.Empty);
    private void InstanceSettingsButton_Click(object sender, RoutedEventArgs e) => InstanceSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e) => ThemeToggleRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void RecentWorldButton_Click(object sender, RoutedEventArgs e) => RecentWorldRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void RailToggleButton_Click(object sender, RoutedEventArgs e) => SetRailExpanded(!_railExpanded);
    private void CloseAccountButton_Click(object sender, RoutedEventArgs e) { SetAccountOpen(false); AccountButton.Focus(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _titleFallback?.Stop();
        _sidebarRowsFallback?.Stop();
        WindowControlsBodyBrush.Visual = null;
        _expansionMotion.Dispose();
        _railMotion.Dispose();
        _railExpandMotion.Dispose();
        _navigationMotion.Dispose();
        _sidebarSelectionMotion.Dispose();
        _accountMotion.Dispose();
        _homeMotion.Dispose();
        _pageMotion.Dispose();
        ReleasePage(_currentPage);
        ReleasePage(_alternatePage);
        _titleMotion.Dispose();
        _optionsMotion.Dispose();
        _sidebarFlipMotion?.Dispose();
        _titleSwapMotion?.Dispose();
        _columnMotion?.Dispose();
        _workspaceMotion?.Dispose();
        foreach (SpringMotion hover in _navigationHoverMotions.Values) hover.Dispose();
        _navigationHoverMotions.Clear();
    }
}
