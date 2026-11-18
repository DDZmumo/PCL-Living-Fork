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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCLLF.Wpf.Motion;

namespace PCLLF.Wpf.Glass;

/// <summary>Native visual shell; all launcher operations are supplied by its owner.</summary>
public partial class GlassLauncherView : UserControl
{
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
    private static readonly SpringParameters RailExpandSpring = new(.25, .84);
    // Page changes: a soft, slightly underdamped spring so the incoming page rises and settles rather than snapping.
    private static readonly SpringParameters PageSpring = new(.46, .86);
    // Pages enter from the direction of travel through the navigation order (down the list: from below).
    private const double PageRise = 22;
    private const double TransitionFrameCap = 1 / 40d;
    // When the section changes, the original pages swap a moment later; titles wait for them so a new title never
    // sits over the previous section's content. The fallback releases them if nothing arrives.
    private bool _titlesPending;
    private DispatcherTimer _titleFallback;
    private double _pageDirection = 1;
    private string _sidebarSignature = string.Empty;
    private int _sidebarSelectedIndex = -1;
    private static readonly int[] NavigationOrder = { 0, 1, 10, 3, 4 };
    private readonly TranslateTransform _snapshotTranslation = new();
    private readonly ScaleTransform _snapshotScale = new(1, 1);
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
    private bool _hasSidebar;
    private bool _accountOpen;
    private bool _layoutPending;
    private bool _keyboardInteraction;
    private bool _hasPageSnapshot;
    private double _homeProgress = 1;
    private double _pageProgress = 1;
    private ImageSource _fallbackInstanceIcon;
    private ImageSource _fallbackAccountAvatar;
    private bool _homeTextShadowEnabled;
    private IReadOnlyList<GlassWindowRegion> _lastRegions = Array.Empty<GlassWindowRegion>();
    private FrameworkElement _surfaceMaskHost;
    private SolidColorBrush _wallpaperBaseBrush;
    private readonly RectangleGeometry _bodyContentClip = new();
    private readonly RectangleGeometry _controlsWallpaperClip = new();
    private (bool DarkText, bool Wallpaper, bool Desktop, double Density, bool HighContrast, bool IsHome, Color? Hue)? _lastPalette;

    public event EventHandler StartRequested;
    public event EventHandler CancelRequested;
    public event EventHandler SelectInstanceRequested;
    public event EventHandler InstanceSettingsRequested;
    public event EventHandler AccountRequested;
    public event EventHandler MinimizeRequested;
    public event EventHandler CloseRequested;
    public event EventHandler RecentWorldRequested;
    public event EventHandler BackRequested;
    public event EventHandler DragRequested;
    public event EventHandler RegionsChanged;
    public event Action<bool> DependencyCheckChanged;
    public event Action<bool> AutoFillChanged;
    public event Action<int> NavigationRequested;

    public ContentControl PageContentHost => PageContent;
    public bool IsRailExpanded => _railExpanded;
    public bool IsRailAnimating => _railExpandMotion.IsRunning;
    public double LeftExpansionAllowance => _leftExpansionAllowance;
    private bool IsCompactViewport => ActualWidth - _leftExpansionAllowance < 960;
    /// <summary>True while a page transition, rail expansion or entrance cascade is playing; owners can defer heavy refreshes.</summary>
    public bool IsTransitioning => _pageMotion.IsRunning || _railExpandMotion.IsRunning || StaggeredEntrance.IsPlaying;
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

    public void SetRailExpanded(bool expanded, bool immediate = false)
    {
        if (_railExpanded == expanded && !immediate) return;
        _railExpanded = expanded;
        string actionText = expanded ? "收起导航栏" : "展开导航栏";
        RailToggleButton.ToolTip = actionText;
        AutomationProperties.SetName(RailToggleButton, actionText);
        _railExpandMotion.Retarget(expanded ? 1 : 0, immediate || ShouldSnapMotion);
        NotifyRegionsChanged();
    }

    public void SetSurfaceMaskHost(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_surfaceMaskHost != null && !ReferenceEquals(_surfaceMaskHost, host)) _surfaceMaskHost.OpacityMask = null;
        _surfaceMaskHost = host;
        host.OpacityMask = FrameMask;
        UpdateWallpaperClip();
    }

    public GlassLauncherView()
    {
        InitializeComponent();
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
        _pageMotion = new SpringMotion(this, 1, ApplyPageTransition, PageSpring) { MaxFrameSeconds = TransitionFrameCap };
        // The outgoing page recedes into depth: it shrinks about its centre while drifting and fading.
        PageSnapshot.RenderTransformOrigin = new Point(.5, .42);
        PageSnapshot.RenderTransform = new TransformGroup { Children = { _snapshotScale, _snapshotTranslation } };
        _titleMotion = new SpringMotion(this, 0, ApplyTitleTransition, MotionTokens.Reveal);
        _optionsMotion = new SpringMotion(this, 1, ApplyOptionsAvailability, MotionTokens.Reveal);
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        MainSurface.SizeChanged += OnSurfaceGeometryChanged;
        NavigationSurface.SizeChanged += OnSurfaceGeometryChanged;
        BodyFrame.SizeChanged += OnSurfaceGeometryChanged;
        BodyContentRoot.SizeChanged += OnSurfaceGeometryChanged;
        WindowControlsSurface.SizeChanged += OnSurfaceGeometryChanged;
        WindowControlsWallpaperFrost.SizeChanged += OnSurfaceGeometryChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
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
        if (navigationChanged && !state.IsHome && !_state.IsHome && !ShouldSnapMotion)
            HoldTitles();
        if (navigationChanged)
            _pageDirection = Array.IndexOf(NavigationOrder, state.SelectedNavigationId) >= Array.IndexOf(NavigationOrder, _state.SelectedNavigationId) ? 1 : -1;
        _state = state;
        _updatingState = true;
        try
        {
            if (homeChanged) _homeMotion.Retarget(state.IsHome ? 1 : 0, ShouldSnapMotion);
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
            ImageSource avatar = state.AccountAvatar ?? GetFallbackAccountAvatar();
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
            LaunchGlyph.Data = state.IsLaunching ? Geometry.Parse("M5,5 H19 V19 H5 Z") : (Geometry)Resources["IconPlay"];
            AutomationProperties.SetName(LaunchButton, LaunchButtonText.Text);
            LaunchStatusText.Text = state.LaunchStatus ?? string.Empty;
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

    public void SetPageContent(UIElement content)
    {
        if (ReferenceEquals(PageContent.Content, content)) return;
        ReleaseTitles();
        bool animate = !ShouldSnapMotion && !_state.IsHome && _homeProgress < .999 && content != null;
        BitmapSource previous = animate ? CapturePageLayer() : null;
        PageContent.Content = content;
        _hasPageSnapshot = previous != null;
        PageSnapshot.Source = previous;
        PageSnapshot.Visibility = _hasPageSnapshot ? Visibility.Visible : Visibility.Hidden;
        if (animate)
        {
            // The page's cards and rows cascade in with the page. Build the tree now so their start state is applied
            // before the first frame (no flash of finished content).
            PageContent.UpdateLayout();
            // Only the first screenful cascades; animating every row of a long page costs frames for items nobody sees.
            if (StaggeredEntrance.FindItemsPanel(PageContent) is Panel itemsPanel)
                StaggeredEntrance.Play(itemsPanel.Children.OfType<UIElement>().Where(item => item.Visibility == Visibility.Visible).Take(14));
            // A different page has different content. Capture the current composite on rapid navigation
            // so the replacement starts from exactly the displayed frame, with no stale completion callback.
            _pageMotion.Snap(0);
            _pageMotion.Retarget(1);
        }
        else _pageMotion.Snap(1);
    }

    private UIElement _sidebarCustom;

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
        SecondarySurface.RenderTransform = _sidebarFlipOffset;
        _sidebarFlipMotion ??= new SpringMotion(this, 1, ApplySidebarFlip, SidebarFlipSpring) { MaxFrameSeconds = TransitionFrameCap };
        _sidebarFlipDirection = _pageDirection;
        if (ShouldSnapMotion) { _sidebarFlipMotion.Snap(1); return; }
        // Start from the offset position (keeping any in-flight velocity) and spring to rest.
        _sidebarFlipMotion.Snap(0);
        _sidebarFlipMotion.Retarget(1);
        // Moving a frosted surface re-composes its blur every frame; cache it as a bitmap while it travels.
        if (_sidebarFlipMotion.IsRunning) SecondarySurface.CacheMode ??= new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
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
        if (_sidebarFlipMotion is { IsRunning: false } && SecondarySurface.CacheMode is not null) SecondarySurface.CacheMode = null;
    }

    private const string SelectedRowTag = "SidebarSelectedRow";

    // Category caption: small secondary text that groups the rows below it (Fluent NavigationView header style).
    private FrameworkElement SidebarHeader(GlassSidebarItem item, bool first)
    {
        var caption = new TextBlock
        {
            Text = item.Label ?? string.Empty, FontSize = 12, Margin = new Thickness(12, first ? 2 : 14, 12, 6),
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
            Height = 36,
            MinHeight = 36,
            Padding = new Thickness(11, 5, 11, 5),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = item.IsEnabled,
            ToolTip = string.IsNullOrWhiteSpace(item.ToolTip) ? item.Label : item.ToolTip,
        };
        AutomationProperties.SetName(button, item.Label ?? string.Empty);
        AutomationProperties.SetHelpText(button, item.ToolTip ?? string.Empty);
        var contents = new StackPanel { Orientation = Orientation.Horizontal };
        if (item.Image != null)
        {
            contents.Children.Add(new Image { Source = item.Image, Width = 18, Height = 18, Margin = new Thickness(0, 0, 9, 0), Stretch = Stretch.Uniform });
        }
        else if (SidebarGlyph(item.IconData, 18) is Path path)
        {
            path.Margin = new Thickness(0, 0, 9, 0);
            contents.Children.Add(path);
        }
        var label = new TextBlock { Text = item.Label ?? string.Empty, FontSize = item.IsCommand ? 14 : 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["GlassText"] };
        if (item.IsCommand) label.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush");
        else if (item.IsSelected) label.SetResourceReference(TextBlock.ForegroundProperty, "GlassAccentBrush");
        contents.Children.Add(label);
        button.Content = contents;
        button.Click += (_, _) => item.Invoked?.Invoke();
        if (item.ContextRequested is { } context)
            button.MouseRightButtonUp += (_, args) => { args.Handled = true; context(button); };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 3), Tag = item.IsSelected ? SelectedRowTag : null };
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
        Geometry geometry = null;
        try { if (!string.IsNullOrWhiteSpace(data)) geometry = Geometry.Parse(data); }
        catch (FormatException) { }
        if (geometry == null || geometry.Bounds.IsEmpty) return null;
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

    public void SetWallpaper(ImageSource source)
    {
        WallpaperImage.Source = source;
        WallpaperImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        _wallpaperHue = source is null ? null : SampleWallpaperColor(source);
        GlassMaterial.SetIsEnabled(ShellRoot, true);
        GlassMaterial.SetWallpaperSource(ShellRoot, source);
        GlassMaterial.SetWallpaperHost(ShellRoot, WallpaperImage);
        // The rail has its own desktop material. A shared wallpaper must never leak
        // through inheritance or be sampled again by the rail's material layer.
        GlassMaterial.SetWallpaperSource(NavigationSurface, null);
        GlassMaterial.SetWallpaperHost(NavigationSurface, null);
        GlassMaterial.SetDesktopBackdropEnabled(BodyFrame, source is null && GlassMaterial.GetDesktopBackdropEnabled(ShellRoot));
        ApplyPalette(_state.UseDarkText);
        UpdateWallpaperClip();
    }

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
        return new[] {
            new GlassWindowRegion(NavigationSurface.TransformToAncestor(this).TransformBounds(new Rect(NavigationSurface.RenderSize)), NavigationSurface.CornerRadius.TopLeft),
            new GlassWindowRegion(BodyFrame.TransformToAncestor(this).TransformBounds(new Rect(BodyFrame.RenderSize)), BodyFrame.CornerRadius.TopLeft),
            new GlassWindowRegion(WindowControlsSurface.TransformToAncestor(this).TransformBounds(new Rect(WindowControlsSurface.RenderSize)), WindowControlsChrome.CornerRadius.TopLeft)
        };
    }

    public IReadOnlyList<GlassWindowRegion> GetDesktopBackdropRegions()
    {
        IReadOnlyList<GlassWindowRegion> surfaces = GetSurfaceRegions();
        if (surfaces.Count < 3 || WallpaperImage.Source is null) return surfaces;
        return new[] { surfaces[0], surfaces[2] };
    }

    public IReadOnlyList<GlassWindowRegion> GetWindowHitRegions()
    {
        IReadOnlyList<GlassWindowRegion> surfaces = GetSurfaceRegions();
        if (surfaces.Count < 3 || !IsRailAnimating || _leftExpansionAllowance <= 0) return surfaces;
        Rect rail = surfaces[0].Bounds;
        return new[] {
            new GlassWindowRegion(new Rect(0, rail.Top, rail.Right, rail.Height), surfaces[0].CornerRadius),
            surfaces[1],
            surfaces[2]
        };
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
        WindowControlsWallpaperFrost.Visibility = wallpaper && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        double density = translucent ? Math.Clamp(GlassMaterial.GetOpacityMultiplier(this), 0, 1) : 1;
        var paletteKey = (darkText, wallpaper, desktop, density, highContrast, _state.IsHome, _wallpaperHue);
        if (_lastPalette == paletteKey) return;
        _lastPalette = paletteKey;

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
            GlassMaterial.SetFallbackColor(ShellRoot, SystemColors.WindowColor);
            BodyFrame.Background = Brushes.Transparent;
            WallpaperImage.Visibility = Visibility.Hidden;
            WindowControlsWallpaperFrost.Visibility = Visibility.Hidden;
            WorkspaceSurface.Background = SystemColors.WindowBrush;
            ContextStrip.Background = SystemColors.WindowBrush;
            SetHomeTextShadow(false);
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
        BodyFrame.Background = wallpaper ? FreezeSolidBrush(ref _wallpaperBaseBrush, mainBase) : Brushes.Transparent;
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
        GlassMaterial.SetFallbackColor(ShellRoot, darkText ? Color.FromRgb(245, 246, 248) : Color.FromRgb(24, 24, 24));
        SetHomeTextShadow(false);
        WorkspaceSurface.Background = Brushes.Transparent;
        ContextStrip.Background = Brushes.Transparent;
        ContextStrip.BorderBrush = _state.IsHome ? Brushes.Transparent : (Brush)Resources["GlassOutlineBrush"];
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

    private static SolidColorBrush FreezeSolidBrush(ref SolidColorBrush cache, Color color)
    {
        if (cache is not null && cache.Color == color) return cache;
        cache = new SolidColorBrush(color);
        cache.Freeze();
        return cache;
    }

    private void SetBrush(string key, Color newColor)
    {
        if (Resources[key] is SolidColorBrush existing && existing.Color == newColor) return;
        var brush = new SolidColorBrush(newColor);
        brush.Freeze();
        Resources[key] = brush;
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

    private ImageSource GetFallbackAccountAvatar()
    {
        if (_fallbackAccountAvatar != null) return _fallbackAccountAvatar;
        try
        {
            var resource = Application.GetResourceStream(new Uri("/LivingPCL;component/Images/Skins/Steve.png", UriKind.Relative));
            if (resource is null) return null;
            BitmapSource skin;
            using (resource.Stream) skin = BitmapFrame.Create(resource.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int unit = Math.Max(1, skin.PixelWidth / 64);
            var head = new CroppedBitmap(skin, new Int32Rect(8 * unit, 8 * unit, 8 * unit, 8 * unit));
            head.Freeze(); _fallbackAccountAvatar = head;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or ArgumentException or NotSupportedException) { }
        return _fallbackAccountAvatar;
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
        double textProgress = Math.Clamp((value - .12) / .68, 0, 1);
        bool showText = textProgress > MotionTokens.VisibilityThreshold || _railExpanded && value > .08;
        Visibility labelVisibility = showText ? Visibility.Visible : Visibility.Collapsed;
        if (Resources["RailLabelTranslation"] is TranslateTransform slide)
            slide.X = 8 * (1 - textProgress);
        foreach (TextBlock label in new[] { RailBrandText, NavLaunchText, NavDownloadText, NavFavoritesText, NavSettingsText, NavOtherText, RailToggleText, AccountNameText })
        {
            label.Opacity = textProgress;
            if (label.Visibility != labelVisibility) label.Visibility = labelVisibility;
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
        PageScene.Opacity = 1 - _homeProgress;
        PageSceneTranslation.X = 0; // the page itself rises/falls with the page transition
        PageScene.Visibility = !_state.IsHome || _homeProgress < 1 - MotionTokens.VisibilityThreshold ? Visibility.Visible : Visibility.Hidden;
        PageContent.Visibility = PageScene.Visibility;
        PageContent.IsHitTestVisible = !_state.IsHome;
    }

    private void ApplyPageTransition(double value)
    {
        _pageProgress = Math.Clamp(value, 0, 1);
        // Cross-fade on eased curves: the outgoing frame leaves early and drifts up a little, the incoming page
        // rises from below and may overshoot by a pixel or two with the spring before it settles.
        // Mostly sequential: the old page eases out over the first half (a visible exit, not a blink) and the new page
        // starts at 30 %, when the old one is already faint, so two pages' text never overlap at readable strength.
        // Strictly sequential: the new page only starts once the old one has fully gone, so their text never overlaps.
        double fadeOut = Math.Clamp(value / .38, 0, 1), fadeIn = Math.Clamp((value - .38) / .45, 0, 1);
        PageContent.Opacity = fadeIn * fadeIn * (3 - 2 * fadeIn);
        PageEntryTranslation.X = 0;
        PageEntryTranslation.Y = PageRise * _pageDirection * (1 - value);
        PageSnapshot.Opacity = 1 - fadeOut * fadeOut * (3 - 2 * fadeOut);
        _snapshotTranslation.Y = -16 * _pageDirection * fadeOut;
        double recede = 1 - Math.Pow(1 - fadeOut, 2);
        _snapshotScale.ScaleX = _snapshotScale.ScaleY = 1 - .06 * recede;
        if (_hasPageSnapshot && _pageProgress >= 1 - MotionTokens.VisibilityThreshold)
        {
            _hasPageSnapshot = false;
            PageSnapshot.Source = null;
        }
        PageSnapshot.Visibility = _hasPageSnapshot ? Visibility.Visible : Visibility.Hidden;
    }

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

    private BitmapSource CapturePageLayer()
    {
        if (PageLayer.ActualWidth <= 0 || PageLayer.ActualHeight <= 0 || !PageLayer.IsVisible) return null;
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(PageLayer);
            int width = (int)Math.Ceiling(PageLayer.ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(PageLayer.ActualHeight * dpi.DpiScaleY);
            if (width < 1 || height < 1 || width > 8192 || height > 8192) return null;
            var snapshot = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            snapshot.Render(PageLayer);
            snapshot.Freeze();
            return snapshot;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A disappearing/unrenderable visual must never delay actual page navigation.
            return null;
        }
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
        if (selected.ActualHeight > 0 && NavigationButtons.ActualHeight > 0)
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
        if (_layoutPending) return;
        _layoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _layoutPending = false;
            bool compact = IsCompactViewport;
            double columnWidth = _state.IsHome ? (compact ? 284 : 304) : (compact ? 230 : 254);
            if (!SecondaryColumn.Width.IsAbsolute || Math.Abs(SecondaryColumn.Width.Value - columnWidth) > .01)
                SecondaryColumn.Width = new GridLength(columnWidth);
            ArrangeHomeDeck();
            SetSelectedNavigation(ShouldSnapMotion);
            SyncSurfaceRegions();
        }));
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

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = IsCompactViewport;
        MainContentGrid.Margin = new Thickness(compact ? 16 : 24, 0, compact ? 16 : 24, 24);
        UpdateHeaderLayout();
        double columnWidth = _state.IsHome ? (compact ? 284 : 304) : (compact ? 230 : 254);
        if (!SecondaryColumn.Width.IsAbsolute || Math.Abs(SecondaryColumn.Width.Value - columnWidth) > .01)
            SecondaryColumn.Width = new GridLength(columnWidth);
        AccountPopover.HorizontalAlignment = HorizontalAlignment.Left;
        AccountPopover.VerticalAlignment = VerticalAlignment.Bottom;
        AccountPopover.Margin = new Thickness(84 + _leftExpansionAllowance, 0, 0, 16);
        AccountPopover.MaxHeight = Math.Max(160, ActualHeight - 40);
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
        if (BodyFrame.ActualWidth > 0 && BodyFrame.ActualHeight > 0 &&
            WindowControlsWallpaperFrost.ActualWidth > 0 && WindowControlsWallpaperFrost.ActualHeight > 0)
        {
            Rect bodyBounds = BodyFrame.TransformToVisual(WindowControlsWallpaperFrost).TransformBounds(new Rect(BodyFrame.RenderSize));
            double radius = BodyFrame.CornerRadius.TopRight;
            if (_controlsWallpaperClip.Rect != bodyBounds) _controlsWallpaperClip.Rect = bodyBounds;
            if (_controlsWallpaperClip.RadiusX != radius) _controlsWallpaperClip.RadiusX = _controlsWallpaperClip.RadiusY = radius;
            if (!ReferenceEquals(WindowControlsWallpaperFrost.Clip, _controlsWallpaperClip))
                WindowControlsWallpaperFrost.Clip = _controlsWallpaperClip;
        }
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

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLaunching) CancelRequested?.Invoke(this, EventArgs.Empty);
        else StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetAccountOpen(false);
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int page)) NavigationRequested?.Invoke(page);
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        AccountRequested?.Invoke(this, EventArgs.Empty);
        SetAccountOpen(!_accountOpen);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _keyboardInteraction = true;
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
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source)
        {
            for (DependencyObject item = source; item != null && !ReferenceEquals(item, sender); item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
                if (item is System.Windows.Controls.Primitives.ButtonBase) return;
        }
        DragRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ShellRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (IsWithin(source, PageContent) || IsWithin(source, AccountPopover) || IsWithin(source, SideRail)) return;
        for (DependencyObject item = source; item != null && !ReferenceEquals(item, ShellRoot); item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is System.Windows.Controls.Primitives.ButtonBase) return;
        DragRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void DependencyCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_updatingState) DependencyCheckChanged?.Invoke(DependencyCheckBox.IsChecked == true); }
    private void AutoFillCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_updatingState) AutoFillChanged?.Invoke(AutoFillCheckBox.IsChecked == true); }
    private void SelectInstanceButton_Click(object sender, RoutedEventArgs e) => SelectInstanceRequested?.Invoke(this, EventArgs.Empty);
    private void InstanceSettingsButton_Click(object sender, RoutedEventArgs e) => InstanceSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void RecentWorldButton_Click(object sender, RoutedEventArgs e) => RecentWorldRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void RailToggleButton_Click(object sender, RoutedEventArgs e) => SetRailExpanded(!_railExpanded);
    private void CloseAccountButton_Click(object sender, RoutedEventArgs e) { SetAccountOpen(false); AccountButton.Focus(); }
}
