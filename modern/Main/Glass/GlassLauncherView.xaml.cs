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
using System.Windows.Threading;
using PCLLF.Wpf.Motion;

namespace PCLLF.Wpf.Glass;

/// <summary>Native visual shell; all launcher operations are supplied by its owner.</summary>
public partial class GlassLauncherView : UserControl
{
    private readonly SpringMotion _expansionMotion;
    private readonly SpringMotion _railMotion;
    private readonly SpringMotion _navigationMotion;
    private GlassLauncherViewState _state = new();
    private bool _updatingState;
    private bool _expanded;
    private bool _hasSidebar;
    private bool _accountOpen;
    private bool _navigationOpen;
    private bool _layoutPending;
    private double _primaryWidth = 326;
    private double _auxiliaryWidth = 286;
    private double _cardHeight = 348;
    private double _deckLeft;
    private double _deckTop;
    private ImageSource _fallbackInstanceIcon;
    private IReadOnlyList<GlassWindowRegion> _lastRegions = Array.Empty<GlassWindowRegion>();

    public event EventHandler StartRequested;
    public event EventHandler CancelRequested;
    public event EventHandler SelectInstanceRequested;
    public event EventHandler InstanceSettingsRequested;
    public event EventHandler AccountRequested;
    public event EventHandler NavigationMenuRequested;
    public event EventHandler MinimizeRequested;
    public event EventHandler CloseRequested;
    public event EventHandler RecentWorldRequested;
    public event EventHandler WallpaperRequested;
    public event EventHandler BackRequested;
    public event EventHandler DragRequested;
    public event EventHandler RegionsChanged;
    public event Action<bool> DependencyCheckChanged;
    public event Action<bool> AutoFillChanged;
    public event Action<int> NavigationRequested;

    public ContentControl PageContentHost => PageContent;

    public GlassLauncherView()
    {
        InitializeComponent();
        _expansionMotion = new SpringMotion(this, 0, ApplyExpansion, MotionTokens.Spatial);
        _railMotion = new SpringMotion(this, 0, ApplyRail, MotionTokens.Spatial);
        _navigationMotion = new SpringMotion(this, 0, value => NavigationTranslation.X = value, MotionTokens.Spatial);
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        SetState(_state);
    }

    public void SetState(GlassLauncherViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool optionsAvailabilityChanged = _state.LaunchOptionsAvailable != state.LaunchOptionsAvailable;
        _state = state;
        _updatingState = true;
        try
        {
            HomePanel.Visibility = state.IsHome ? Visibility.Visible : Visibility.Collapsed;
            PageContent.Visibility = state.IsHome ? Visibility.Hidden : Visibility.Visible;
            PageContent.IsHitTestVisible = !state.IsHome;
            bool titleMode = state.CanGoBack;
            NavigationButtons.Visibility = titleMode ? Visibility.Collapsed : Visibility.Visible;
            BackTitlePanel.Visibility = titleMode ? Visibility.Visible : Visibility.Collapsed;
            PageTitleText.Text = state.PageTitle ?? string.Empty;
            AccountNameText.Text = string.IsNullOrWhiteSpace(state.AccountName) ? "未登录" : state.AccountName;
            AccountButton.ToolTip = AccountNameText.Text;
            AutomationProperties.SetName(AccountButton, "当前账户：" + AccountNameText.Text);
            AccountAvatarImage.Source = state.AccountAvatar;
            AccountFallbackGlyph.Visibility = state.AccountAvatar is null ? Visibility.Visible : Visibility.Collapsed;

            InstanceImage.Source = state.InstanceIcon ?? GetFallbackInstanceIcon();
            InstanceNameText.Text = string.IsNullOrWhiteSpace(state.InstanceName) ? "选择实例" : state.InstanceName;
            InstanceNameText.ToolTip = InstanceNameText.Text;
            InstanceDescriptionText.Text = state.InstanceDescription ?? string.Empty;
            InstanceDescriptionText.Visibility = string.IsNullOrWhiteSpace(state.InstanceDescription) ? Visibility.Collapsed : Visibility.Visible;
            InstanceSettingsButton.IsEnabled = state.HasInstance;
            LaunchButton.IsEnabled = state.IsLaunching || state.CanLaunch;
            LaunchButtonText.Text = state.IsLaunching ? "取消启动" : string.IsNullOrWhiteSpace(state.LaunchText) ? "启动游戏" : state.LaunchText;
            LaunchGlyph.Data = state.IsLaunching ? Geometry.Parse("M5,5 H19 V19 H5 Z") : (Geometry)Resources["IconPlay"];
            AutomationProperties.SetName(LaunchButton, LaunchButtonText.Text);
            LaunchStatusText.Text = state.LaunchStatus ?? string.Empty;
            LaunchStatusText.Visibility = state.IsLaunching && !string.IsNullOrWhiteSpace(state.LaunchStatus) ? Visibility.Visible : Visibility.Collapsed;
            LaunchProgressBar.Visibility = state.IsLaunching && state.LaunchProgress.HasValue ? Visibility.Visible : Visibility.Collapsed;
            LaunchProgressBar.Value = Math.Clamp(state.LaunchProgress ?? 0, 0, 1);
            ExpandButton.Visibility = state.LaunchOptionsAvailable ? Visibility.Visible : Visibility.Hidden;
            ExpandButton.IsEnabled = state.LaunchOptionsAvailable;
            if (optionsAvailabilityChanged)
                _expansionMotion.Retarget(_expanded && state.LaunchOptionsAvailable ? 1 : 0, state.ReduceMotion || !IsLoaded);

            DependencyCheckBox.IsChecked = state.DependencyCheckEnabled;
            AutoFillCheckBox.IsChecked = state.AutoFillEnabled;
            RecentWorldButton.Visibility = state.RecentWorldAvailable ? Visibility.Visible : Visibility.Collapsed;
            RecentWorldButton.IsEnabled = state.RecentWorldAvailable && state.CanLaunch && !state.IsLaunching;
            NoRecentWorldText.Visibility = state.RecentWorldAvailable ? Visibility.Collapsed : Visibility.Visible;
            RecentWorldNameText.Text = state.RecentWorldName ?? string.Empty;
            RecentWorldDetailText.Text = state.RecentWorldDetail ?? string.Empty;
            RecentWorldImage.Source = state.RecentWorldIcon;
            WorldFallbackGlyph.Visibility = state.RecentWorldIcon is null ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(RecentWorldButton, "继续游玩：" + (state.RecentWorldName ?? string.Empty));
            ApplyPalette(state.UseDarkText);
            SetSelectedNavigation(state.ReduceMotion || !IsLoaded);
            if (state.ReduceMotion)
            {
                _expansionMotion.Snap(_expanded && state.LaunchOptionsAvailable ? 1 : 0);
                _railMotion.Snap(_hasSidebar ? 1 : 0);
            }
        }
        finally
        {
            _updatingState = false;
        }
        ScheduleLayout();
    }

    public void SetPageContent(UIElement content)
    {
        if (ReferenceEquals(PageContent.Content, content)) return;
        PageContent.Content = content;
    }

    public void SetSidebar(IEnumerable<GlassSidebarItem> items)
    {
        SidebarPanel.Children.Clear();
        foreach (GlassSidebarItem item in items ?? Enumerable.Empty<GlassSidebarItem>())
        {
            var button = new Button
            {
                Style = (Style)Resources["GlassCircleButton"],
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(13),
                BorderThickness = item.IsSelected ? new Thickness(0.7) : new Thickness(0),
                Background = (Brush)Resources[item.IsSelected ? "GlassSelectedBrush" : "GlassControlBrush"],
                IsEnabled = item.IsEnabled,
                ToolTip = string.IsNullOrWhiteSpace(item.ToolTip) ? item.Label : item.ToolTip,
            };
            AutomationProperties.SetName(button, item.Label ?? string.Empty);
            AutomationProperties.SetHelpText(button, item.ToolTip ?? string.Empty);
            if (item.Image != null)
            {
                button.Content = new Image { Source = item.Image, Width = 18, Height = 18, Stretch = Stretch.Uniform };
            }
            else
            {
                Geometry geometry = null;
                try { if (!string.IsNullOrWhiteSpace(item.IconData)) geometry = Geometry.Parse(item.IconData); }
                catch (FormatException) { }
                if (geometry != null && !geometry.Bounds.IsEmpty)
                {
                    var path = new Path { Data = geometry, Width = 18, Height = 18, Stretch = Stretch.Uniform };
                    path.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
                    button.Content = path;
                }
                else
                {
                    button.Content = new TextBlock { Style = (Style)Resources["GlassText"], Text = string.IsNullOrWhiteSpace(item.Label) ? "·" : item.Label.Substring(0, 1), FontSize = 14, FontWeight = FontWeights.Medium };
                }
            }
            button.Click += (_, _) => { SetNavigationOpen(false); item.Invoked?.Invoke(); };
            SidebarPanel.Children.Add(button);
        }
        UpdateSidebarAvailability();
        ScheduleLayout();
    }

    private void UpdateSidebarAvailability()
    {
        _hasSidebar = SidebarPanel.Children.Count > 0 || NavigationContent.Content != null;
        if (_hasSidebar) SideRail.Visibility = Visibility.Visible;
        SideRail.IsHitTestVisible = _hasSidebar;
        _railMotion.Retarget(_hasSidebar ? 1 : 0, _state.ReduceMotion || !IsLoaded);
    }

    public void SetAccountContent(UIElement content)
    {
        if (!ReferenceEquals(AccountContent.Content, content)) AccountContent.Content = content;
        if (content is null) SetAccountOpen(false);
    }

    public void SetAccountOpen(bool isOpen)
    {
        _accountOpen = isOpen && AccountContent.Content != null;
        if (_accountOpen) SetNavigationOpen(false);
        AccountPopover.Visibility = _accountOpen ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(AccountButton, _accountOpen ? "账户面板已展开" : "打开账户面板");
        if (_accountOpen)
            AccountPopover.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        ScheduleLayout();
    }

    public void SetNavigationContent(UIElement content)
    {
        if (!ReferenceEquals(NavigationContent.Content, content)) NavigationContent.Content = content;
        NavigationMenuButton.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        if (content is null) SetNavigationOpen(false);
        UpdateSidebarAvailability();
        ScheduleLayout();
    }

    public void SetNavigationOpen(bool isOpen)
    {
        _navigationOpen = isOpen && NavigationContent.Content != null;
        if (_navigationOpen) SetAccountOpen(false);
        NavigationPopover.Visibility = _navigationOpen ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NavigationMenuButton, _navigationOpen ? "收起全部页面选项" : "展开全部页面选项");
        if (_navigationOpen) NavigationPopover.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        ScheduleLayout();
    }

    public void SetWallpaper(ImageSource source)
    {
        WallpaperImage.Source = source;
        WallpaperImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        GlassMaterial.SetWallpaperHost(ShellRoot, WallpaperImage);
        GlassMaterial.SetWallpaperSource(ShellRoot, source);
        UpdateWallpaperClip();
    }

    /// <summary>Rounded outer islands in view coordinates, including their current animated positions.</summary>
    public IReadOnlyList<GlassWindowRegion> GetSurfaceRegions()
    {
        var result = new List<GlassWindowRegion>(7);
        AddSurfaceRegion(result, MainSurface, 36);
        AddSurfaceRegion(result, AccountSurface, 28);
        AddSurfaceRegion(result, NavigationSurface, 28);
        AddSurfaceRegion(result, WindowControlsSurface, 28);
        AddSurfaceRegion(result, SideRail, 28);
        AddSurfaceRegion(result, AccountPopover, 24);
        AddSurfaceRegion(result, NavigationPopover, 24);
        return result;
    }

    private void AddSurfaceRegion(List<GlassWindowRegion> result, FrameworkElement element, double radius)
    {
        if (element.Visibility != Visibility.Visible || element.Opacity < 0.005 || element.ActualWidth <= 0 || element.ActualHeight <= 0 || !element.IsDescendantOf(this)) return;
        Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
        if (!bounds.IsEmpty) result.Add(new GlassWindowRegion(bounds, radius));
    }

    private void ApplyPalette(bool darkText)
    {
        SetBrush("GlassTextBrush", darkText ? "#ED263339" : "#F4FFFFFF");
        SetBrush("GlassSecondaryTextBrush", darkText ? "#AF354249" : "#BFFFFFFF");
        SetBrush("GlassMainBrush", darkText ? "#61E4E6E0" : "#54343B3C");
        SetBrush("GlassCardBrush", darkText ? "#62FFFFFF" : "#66444847");
        SetBrush("GlassChromeBrush", darkText ? "#66D5D9D4" : "#78575F5D");
        SetBrush("GlassAccentBrush", darkText ? "#EC334549" : "#F4FFFFFF");
        SetBrush("GlassAccentTextBrush", darkText ? "#FFF7FAF8" : "#FF303B3C");
        SetBrush("GlassCheckOutlineBrush", darkText ? "#885C6B70" : "#80FFFFFF");
    }

    private void SetBrush(string key, string color)
    {
        Color newColor = (Color)ColorConverter.ConvertFromString(color);
        if (Resources[key] is SolidColorBrush existing && existing.Color == newColor) return;
        Resources[key] = new SolidColorBrush(newColor);
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
        AuxiliaryTranslation.X = -22 * (1 - p);
        AuxiliaryCards.Opacity = p;
        ExpandRotation.Angle = p * 45;
        AuxiliaryCards.IsHitTestVisible = _expanded && _state.LaunchOptionsAvailable && p > 0.5;
        AuxiliaryCards.Visibility = p < 0.001 && (!_expanded || !_state.LaunchOptionsAvailable) ? Visibility.Hidden : Visibility.Visible;
    }

    private void ApplyRail(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        RailTranslation.X = 60 * (1 - p);
        SideRail.Opacity = p;
        SideRail.Visibility = p < 0.001 && !_hasSidebar ? Visibility.Hidden : Visibility.Visible;
        NotifyRegionsChanged();
    }

    private void ArrangeHomeDeck()
    {
        if (HomeDeck.ActualWidth <= 0 || HomeDeck.ActualHeight <= 0) return;
        double width = HomeDeck.ActualWidth;
        double height = HomeDeck.ActualHeight;
        _primaryWidth = Math.Clamp(width * 0.345, 270, 330);
        _auxiliaryWidth = Math.Clamp(width * 0.3, 242, 290);
        if (_primaryWidth + _auxiliaryWidth + 16 > width)
        {
            _primaryWidth = Math.Max(244, width - _auxiliaryWidth - 16);
        }
        _cardHeight = Math.Max(272, height);
        _deckLeft = 0;
        _deckTop = 0;
        PrimaryCard.Width = _primaryWidth;
        PrimaryCard.Height = _cardHeight;
        AuxiliaryCards.Width = _auxiliaryWidth;
        Canvas.SetLeft(PrimaryCard, _deckLeft);
        Canvas.SetTop(PrimaryCard, _deckTop);
        Canvas.SetLeft(AuxiliaryCards, _deckLeft + _primaryWidth + 16);
        Canvas.SetTop(AuxiliaryCards, _deckTop);
        Canvas.SetLeft(ExpandButtonContainer, _deckLeft + _primaryWidth - 47);
        Canvas.SetTop(ExpandButtonContainer, _deckTop + 13);
        bool compact = _cardHeight < 360;
        InstanceImage.Width = InstanceImage.Height = compact ? 48 : 80;
        InstanceNameText.FontSize = compact ? 21 : 23;
        InstanceNameText.MaxWidth = Math.Max(180, _primaryWidth - 60);
        InstanceDescriptionText.MaxWidth = Math.Max(180, _primaryWidth - 60);
        InstanceNameText.Margin = new Thickness(0, compact ? 8 : 22, 0, 0);
        InstanceCenter.Margin = new Thickness(12, compact ? 12 : 28, 12, compact ? 8 : 22);
        LaunchButton.Margin = new Thickness(0, compact ? 14 : 32, 0, 0);
        LaunchButton.Height = compact ? 42 : 44;
        LaunchStatusText.MaxWidth = Math.Max(160, _primaryWidth - 64);
        ApplyExpansion(_expansionMotion.Value);
    }

    private void SetSelectedNavigation(bool immediate)
    {
        int index = _state.SelectedNavigationId switch { 1 => 1, 10 => 2, 3 => 3, 4 => 4, _ => 0 };
        double cellWidth = NavigationButtons.ActualWidth / 5;
        if (cellWidth <= 0) return;
        NavSelection.Width = cellWidth;
        _navigationMotion.Retarget(cellWidth * index, immediate);
        Button[] buttons = [NavLaunch, NavDownload, NavFavorites, NavSettings, NavOther];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetItemStatus(buttons[i], i == index ? "当前页面" : string.Empty);
        }
    }

    private void ScheduleLayout()
    {
        if (_layoutPending) return;
        _layoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _layoutPending = false;
            ArrangeHomeDeck();
            SetSelectedNavigation(_state.ReduceMotion || !IsLoaded);
            NotifyRegionsChanged();
        }));
    }

    private void NotifyRegionsChanged()
    {
        if (!IsLoaded) return;
        IReadOnlyList<GlassWindowRegion> regions = GetSurfaceRegions();
        if (_lastRegions.Count == regions.Count && _lastRegions.SequenceEqual(regions)) return;
        _lastRegions = regions;
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GlassMaterial.SetWallpaperHost(ShellRoot, WallpaperImage);
        ScheduleLayout();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = ActualWidth < 930;
        double mainLeft = compact ? 92 : 96;
        double mainRight = compact ? 18 : 26;
        double headerInset = compact ? 14 : 70;
        double mainTop = compact ? 91 : 104;
        MainSurface.Margin = new Thickness(mainLeft, mainTop, mainRight, compact ? 18 : 26);
        MainSurface.Padding = new Thickness(26);
        Header.Margin = new Thickness(mainLeft + headerInset, compact ? 18 : 24, mainRight + headerInset, 0);
        AccountColumn.Width = new GridLength(compact ? 148 : 168);
        SideRail.Margin = new Thickness(compact ? 14 : 16, mainTop, 0, compact ? 18 : 26);
        SideRail.MaxHeight = Math.Max(96, ActualHeight - mainTop - 42);
        AccountPopover.Margin = new Thickness(mainLeft + headerInset, compact ? 82 : 91, 0, 22);
        AccountPopover.MaxHeight = Math.Max(180, ActualHeight - 108);
        NavigationPopover.Margin = new Thickness(mainLeft, mainTop, 0, compact ? 18 : 26);
        NavigationPopover.MaxHeight = Math.Max(160, ActualHeight - mainTop - 38);
        ScheduleLayout();
    }

    private void UpdateWallpaperClip()
    {
        if (MainSurface.ActualWidth <= 0 || MainSurface.ActualHeight <= 0) return;
        Rect bounds = MainSurface.TransformToVisual(WallpaperImage).TransformBounds(new Rect(MainSurface.RenderSize));
        if (WallpaperClip.Rect != bounds) WallpaperClip.Rect = bounds;
    }

    private void OnLayoutUpdated(object sender, EventArgs e)
    {
        UpdateWallpaperClip();
        NotifyRegionsChanged();
    }
    private void HomeDeck_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeHomeDeck();
    private void NavigationButtons_SizeChanged(object sender, SizeChangedEventArgs e) => SetSelectedNavigation(true);

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.LaunchOptionsAvailable) return;
        _expanded = !_expanded;
        if (_expanded) AuxiliaryCards.Visibility = Visibility.Visible;
        ExpandButton.ToolTip = _expanded ? "收起启动选项" : "更多启动选项";
        AutomationProperties.SetName(ExpandButton, _expanded ? "收起启动选项" : "展开启动选项");
        _expansionMotion.Retarget(_expanded ? 1 : 0, _state.ReduceMotion);
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLaunching) CancelRequested?.Invoke(this, EventArgs.Empty);
        else StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetAccountOpen(false);
        SetNavigationOpen(false);
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int page)) NavigationRequested?.Invoke(page);
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        AccountRequested?.Invoke(this, EventArgs.Empty);
        SetAccountOpen(!_accountOpen);
    }

    private void NavigationMenuButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationMenuRequested?.Invoke(this, EventArgs.Empty);
        SetNavigationOpen(!_navigationOpen);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _accountOpen)
        {
            SetAccountOpen(false);
            AccountButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _navigationOpen)
        {
            SetNavigationOpen(false);
            NavigationMenuButton.Focus();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (_accountOpen && !IsWithin(source, AccountPopover) && !IsWithin(source, AccountButton)) SetAccountOpen(false);
        if (_navigationOpen && !IsWithin(source, NavigationPopover) && !IsWithin(source, NavigationMenuButton)) SetNavigationOpen(false);
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
        if (IsWithin(source, PageContent) || IsWithin(source, AccountPopover) || IsWithin(source, NavigationPopover) || IsWithin(source, SideRail)) return;
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
    private void WallpaperButton_Click(object sender, RoutedEventArgs e) => WallpaperRequested?.Invoke(this, EventArgs.Empty);
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void CloseAccountButton_Click(object sender, RoutedEventArgs e) { SetAccountOpen(false); AccountButton.Focus(); }
    private void CloseNavigationButton_Click(object sender, RoutedEventArgs e) { SetNavigationOpen(false); NavigationMenuButton.Focus(); }
}
