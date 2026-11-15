// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MeloongCore;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>Portals the real legacy page containers into the new shell; no engine or account state is duplicated.</summary>
internal sealed class GlassShellIntegration : IDisposable {
    private readonly FormMain _owner;
    private readonly Grid _root = new();
    private readonly Canvas _resizeLayer = new();
    private readonly FrameworkElement[] _resizeParts = new FrameworkElement[8];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, ImageSource> _wallpaperCache = new(StringComparer.OrdinalIgnoreCase);
    private GlassShellRuntime? _runtime;
    private GlassWindowBackdrop? _backdrop;
    private UIElement? _legacyRoot;
    private string? _wallpaperPath;
    private ImageSource? _legacyWallpaper;
    private long _nextWallpaperRefresh;
    private readonly DependencyPropertyDescriptor _backgroundDescriptor = DependencyPropertyDescriptor.FromProperty(Panel.BackgroundProperty, typeof(Canvas));
    private string _navigationSignature = "";
    private bool _attached, _disposed, _refreshing;
    private PageLaunchLeft? _accountSource;
    private FrameworkElement? _accountPortal;
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
        _root.Children.Add(View);
        _owner.Content = _root;
        GlassMaterial.SetIsEnabled(_root, true);
        EnsureContentPortal(_owner.PanMainRight, View.PageContentHost);
        MoveOverlay(_owner.PanHint);
        MoveOverlay(_owner.PanMsg);
        if (_owner.BtnExtraDownload.Parent is FrameworkElement extras) {
            Detach(extras);
            extras.SetValue(Grid.RowProperty, 0);
            extras.HorizontalAlignment = HorizontalAlignment.Right;
            extras.VerticalAlignment = VerticalAlignment.Bottom;
            extras.Margin = new Thickness(0, 0, 42, 38);
            Panel.SetZIndex(extras, 12); _root.Children.Add(extras);
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
        View.NavigationMenuRequested += OnNavigationRequested;
        View.MinimizeRequested += OnMinimize;
        View.CloseRequested += OnClose;
        View.DragRequested += OnDrag;
        View.WallpaperRequested += OnWallpaper;
        View.RegionsChanged += OnRegionsChanged;
        _backgroundDescriptor.AddValueChanged(_owner.ImgBack, OnLegacyBackgroundChanged);
        _owner.Loaded += OnLoaded;
        _owner.SizeChanged += OnSizeChanged;
        _owner.Closed += OnClosed;
        _owner.Activated += OnActivated;
        _backdrop = GlassWindowBackdrop.Attach(_owner, new GlassBackdropOptions { UseDarkTheme = ModTheme.IsDarkTheme });
        if (_owner.IsLoaded) OnLoaded(_owner, new RoutedEventArgs());
    }

    private void MoveOverlay(FrameworkElement element) {
        Detach(element);
        element.SetValue(Grid.RowProperty, 0);
        element.SetValue(Grid.RowSpanProperty, 1);
        Panel.SetZIndex(element, element == _owner.PanMsg ? 30 : 20);
        _root.Children.Add(element);
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
        Refresh();
        SetMaterialDensity(Settings.Get<int>("UiLauncherTransparent") / 1000d + 0.4d);
        _refreshTimer.Start();
    }
    private void OnRefreshTick(object? sender, EventArgs args) => Refresh();
    internal void Refresh(bool forcePreferences = false) {
        if (_disposed || !_attached || !_owner.IsLoaded || _refreshing) return;
        _refreshing = true;
        try {
            _owner.Opacity = 1;
            if (_owner.GlassPreviewState is not null) View.SetState(_owner.GlassPreviewState());
            else if (_runtime is not null) { _runtime.UseDarkText = !ModTheme.IsDarkTheme; _runtime.Refresh(); }
            RefreshNavigation();
            RefreshHeaderVisibility();
            RefreshWallpaper(forcePreferences);
            _backdrop?.SetDarkTheme(ModTheme.IsDarkTheme);
            RefreshRegions();
        } catch (Exception error) {
            // Keep the shell alive during a page's transient replacement; original operation failures retain their own feedback.
            Logger.Warn(error, "刷新玻璃界面状态失败");
        } finally { _refreshing = false; }
    }
    private void RefreshNavigation() {
        bool hasContext = _owner.GlassPageCode != (int)FormMain.PageType.Launch && _owner.PageLeft is { Width: not 0 };
        IReadOnlyList<GlassSidebarItem> items = hasContext ? GlassContextNavigation.Build(_owner.PageLeft) : Array.Empty<GlassSidebarItem>();
        string signature = (_owner.PageLeft?.GetType().FullName ?? "") + ":" + hasContext + ":" +
            string.Join("|", items.Select(item => item.Label + ":" + item.IsSelected + ":" + item.IsEnabled));
        if (signature == _navigationSignature) return;
        _navigationSignature = signature;
        View.SetSidebar(items);
        if (hasContext) {
            EnsureNavigationPortal();
        } else {
            View.SetNavigationContent(null!);
            Detach(_owner.PanMainLeft);
            _owner.SidebarCard.Child = _owner.PanMainLeft;
        }
    }
    private void OnNavigationRequested(object? sender, EventArgs args) {
        if (_owner.PageLeft is null) return;
        EnsureNavigationPortal();
    }
    private void EnsureNavigationPortal() {
        var target = (ContentControl)View.FindName("NavigationContent");
        EnsureContentPortal(_owner.PanMainLeft, target);
        View.SetNavigationContent(_owner.PanMainLeft);
    }
    private void RefreshHeaderVisibility() {
        foreach (var pair in new[] {
            ("NavDownload", _owner.BtnTitleSelect1.Visibility), ("NavFavorites", _owner.BtnTitleSelect5.Visibility),
            ("NavSettings", _owner.BtnTitleSelect3.Visibility), ("NavOther", _owner.BtnTitleSelect4.Visibility)
        }) if (View.FindName(pair.Item1) is UIElement button) button.Visibility = pair.Item2;
    }
    private void OnAccountRequested(object? sender, EventArgs args) {
        if (ModMain.FrmLaunchLeft is not PageLaunchLeft source) return;
        if (!ReferenceEquals(source, _accountSource)) {
            _accountSource = source;
            _accountPortal = CreateAccountPortal(source);
        }
        View.SetAccountContent(_accountPortal!);
        source.RefreshPage(true, false);
    }
    internal static FrameworkElement CreateAccountPortal(PageLaunchLeft source) {
        var portal = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        foreach (FrameworkElement part in new FrameworkElement[] { source.PanTypeOne, source.PanType, source.PanLogin }) {
            Detach(part); part.Margin = new Thickness(0, 0, 0, part == source.PanLogin ? 0 : 14);
            part.SetValue(Grid.RowProperty, 0); part.SetValue(Grid.ColumnProperty, 0); part.SetValue(Grid.ColumnSpanProperty, 1);
            portal.Children.Add(part);
        }
        return portal;
    }

    private void OnMinimize(object? sender, EventArgs args) => _owner.WindowState = WindowState.Minimized;
    private void OnClose(object? sender, EventArgs args) => _owner.EndProgram(true);
    private void OnDrag(object? sender, EventArgs args) {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { _owner.DragMove(); }
        catch (InvalidOperationException) { }
    }
    private void OnWallpaper(object? sender, EventArgs args) {
        var menu = new ContextMenu();
        var choose = new MenuItem { Header = "设置启动器壁纸…" };
        choose.Click += (_, _) => ChooseWallpaper();
        var desktop = new MenuItem { Header = "使用桌面磨砂", IsEnabled = Settings.Get<int>("UiBackgroundMode") == 0 };
        desktop.Click += (_, _) => { Settings.Set("UiBackgroundMode", 1); RefreshWallpaper(true); };
        var appearance = new MenuItem { Header = "外观设置" };
        appearance.Click += (_, _) => _owner.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI);
        menu.Items.Add(choose); menu.Items.Add(desktop); menu.Items.Add(new Separator()); menu.Items.Add(appearance);
        var theme = new MenuItem { Header = ModTheme.IsDarkTheme ? "切换到浅色主题" : "切换到深色主题" };
        theme.Click += (_, _) => Settings.Set("UiLauncherColorMode", ModTheme.IsDarkTheme ? 0 : 1);
        var updates = new MenuItem { Header = "启动器更新" };
        updates.Click += (_, _) => _owner.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
        var tasks = new MenuItem { Header = "任务管理" };
        tasks.Click += (_, _) => _owner.PageChange(FormMain.PageType.TaskManager);
        menu.Items.Add(theme); menu.Items.Add(updates); menu.Items.Add(tasks);
        var homeContent = new MenuItem { Header = "主页内容与启动日志" };
        homeContent.Click += (_, _) => {
            _owner.PageChange(FormMain.PageType.Launch);
            if (_runtime is not null) _runtime.ShowLegacyHome = true;
            Refresh();
        };
        menu.Items.Add(homeContent);
        menu.PlacementTarget = View; menu.IsOpen = true;
    }
    private void ChooseWallpaper() {
        var picker = new OpenFileDialog { Title = "选择启动器壁纸", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*", CheckFileExists = true };
        if (picker.ShowDialog(_owner) != true) return;
        try {
            _ = ReadWallpaper(picker.FileName);
            Settings.Set("UiGlassWallpaperPath", picker.FileName);
            Settings.Set("UiBackgroundMode", 0);
            RefreshWallpaper(true);
        } catch (Exception error) { Logger.Error(error, "无法读取选择的壁纸", LogBehavior.Toast); }
    }
    private ImageSource ReadWallpaper(string path) {
        if (_wallpaperCache.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource image = InstanceResourceStore.LoadImage(path, 2560);
        if (image is null) throw new IOException("图片内容无法读取。");
        _wallpaperCache.Clear(); _wallpaperCache[path] = image;
        return image;
    }
    private void OnLegacyBackgroundChanged(object? sender, EventArgs args) => RefreshWallpaper(true);
    private void RefreshWallpaper(bool force = false) {
        if (!force && Environment.TickCount64 < _nextWallpaperRefresh) return;
        _nextWallpaperRefresh = Environment.TickCount64 + 1000;
        bool pictureMode = Settings.Get<int>("UiBackgroundMode") == 0;
        string path = pictureMode ? Settings.Get<string>("UiGlassWallpaperPath") : "";
        ImageSource? legacy = null;
        string pictureFolder = Path.Combine(MeloongCore.Paths.Base, "PCL", "Pictures");
        if (string.IsNullOrWhiteSpace(path) && pictureMode && Directory.Exists(pictureFolder) &&
            Directory.EnumerateFiles(pictureFolder).Any(file => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            legacy = (_owner.ImgBack.Background as ImageBrush)?.ImageSource;
        if (string.Equals(path, _wallpaperPath, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(legacy, _legacyWallpaper)) return;
        _wallpaperPath = path;
        _legacyWallpaper = legacy;
        ImageSource? source = legacy;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) {
            try { source = ReadWallpaper(path); }
            catch (Exception error) { Logger.Warn(error, "读取启动器壁纸失败，保留桌面材质"); }
        }
        View.SetWallpaper(source!);
        GlassMaterial.SetWallpaperSource(_root, source);
        GlassMaterial.SetWallpaperHost(_root, View);
        _backdrop?.SetWallpaperMode(source is not null);
        RefreshRegions();
    }
    private void OnRegionsChanged(object? sender, EventArgs args) => RefreshRegions();
    private void RefreshRegions() {
        if (_backdrop is null || View.ActualWidth <= 0 || View.ActualHeight <= 0) return;
        var surfaces = View.GetSurfaceRegions();
        if (surfaces.Count > 0) UpdateResizeGrips(surfaces[0].Bounds);
        if (_owner.PanMsg.Visibility == Visibility.Visible)
            _backdrop.SetRegions(new[] { new GlassWindowRegion(new Rect(0, 0, View.ActualWidth, View.ActualHeight), 16) });
        else _backdrop.SetRegions(surfaces);
    }
    private void UpdateResizeGrips(Rect area) {
        if (_resizeParts[0] is null) return;
        const double corner = 16, edge = 5;
        Rect[] bounds = {
            new(area.Left + 36, area.Bottom - edge, Math.Max(0, area.Width - 72), edge),
            new(area.Left, area.Top + 36, edge, Math.Max(0, area.Height - 72)),
            new(area.Left + 9, area.Bottom - corner - 9, corner, corner),
            new(area.Left + 9, area.Top + 9, corner, corner),
            new(area.Right - edge, area.Top + 36, edge, Math.Max(0, area.Height - 72)),
            new(area.Right - corner - 9, area.Bottom - corner - 9, corner, corner),
            new(area.Right - corner - 9, area.Top + 9, corner, corner),
            new(area.Left + 36, area.Top, Math.Max(0, area.Width - 72), edge)
        };
        for (int index = 0; index < bounds.Length; index++) {
            Rect box = bounds[index]; FrameworkElement grip = _resizeParts[index];
            Canvas.SetLeft(grip, box.Left); Canvas.SetTop(grip, box.Top); grip.Width = box.Width; grip.Height = box.Height;
        }
        _owner.PanHint.Margin = new Thickness(area.Left + 24, 0, 0, Math.Max(0, View.ActualHeight - area.Bottom) + 24);
        _owner.PanHint.MaxWidth = Math.Max(100, area.Width - 48);
    }
    internal void SetMaterialDensity(double opacity) {
        GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(opacity, 0.4, 1));
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { RefreshRegions(); }
    private void OnActivated(object? sender, EventArgs args) { _backdrop?.Refresh(); RefreshRegions(); }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnRefreshTick;
        _runtime?.Dispose(); _backdrop?.Dispose();
        if (_runtime is not null) _runtime.AccountRequested -= OnAccountRequested;
        View.NavigationMenuRequested -= OnNavigationRequested;
        View.MinimizeRequested -= OnMinimize; View.CloseRequested -= OnClose;
        View.DragRequested -= OnDrag; View.WallpaperRequested -= OnWallpaper;
        View.RegionsChanged -= OnRegionsChanged;
        _backgroundDescriptor.RemoveValueChanged(_owner.ImgBack, OnLegacyBackgroundChanged);
        _owner.Loaded -= OnLoaded; _owner.SizeChanged -= OnSizeChanged;
        _owner.Closed -= OnClosed; _owner.Activated -= OnActivated;
    }
}
