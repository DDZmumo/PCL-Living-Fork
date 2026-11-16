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
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using MeloongCore;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>Connects a new presentation tree to existing page commands and application state.</summary>
internal sealed class GlassShellIntegration : IDisposable {
    private readonly FormMain _owner;
    private readonly Grid _root = new();
    private readonly Canvas _resizeLayer = new();
    private readonly FrameworkElement[] _resizeParts = new FrameworkElement[8];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, ImageSource> _wallpaperCache = new(StringComparer.OrdinalIgnoreCase);
    private GlassShellRuntime? _runtime;
    private UIElement? _legacyRoot;
    private string? _wallpaperPath;
    private ImageSource? _legacyWallpaper;
    private long _nextWallpaperRefresh;
    private string _wallpaperOptions = "";
    private readonly DependencyPropertyDescriptor _backgroundDescriptor = DependencyPropertyDescriptor.FromProperty(Panel.BackgroundProperty, typeof(Canvas));
    private string _navigationSignature = "";
    private bool _attached, _disposed, _refreshing;
    private PageLaunchLeft? _accountSource;
    private FrameworkElement? _accountPortal;
    private readonly List<GlassPresentationSession> _presentations = new();
    private GlassPresentationSession? _pagePresentation, _navigationPresentation;
    private object? _projectedPage;
    private readonly List<GlassExtraButtonAdapter> _extraButtons = new();
    private GlassActionMenu? _actionMenu;
    private GlassWindowRegionClip? _windowRegion;
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
        View.SetSurfaceMaskHost(_root);
        _owner.Content = _root;
        _windowRegion = GlassWindowRegionClip.Attach(_owner);
        GlassMaterial.SetIsEnabled(_root, true);
        _pagePresentation = CreatePresentation(_owner.PanMainRight);
        _projectedPage = _owner.PageRight;
        View.SetPageContent(_pagePresentation.View);
        MoveOverlay(_owner.PanHint);
        MoveOverlay(_owner.PanMsg);
        if (_owner.BtnExtraDownload.Parent is FrameworkElement extras) {
            var actions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 42, 38) };
            foreach (MyExtraButton source in new[] { _owner.BtnExtraBack, _owner.BtnExtraDownload,
                _owner.BtnExtraApril, _owner.BtnExtraShutdown, _owner.BtnExtraMusic }) {
                var adapter = new GlassExtraButtonAdapter(source, !ModTheme.IsDarkTheme);
                _extraButtons.Add(adapter); actions.Children.Add(adapter.View);
            }
            Panel.SetZIndex(actions, 12); _root.Children.Add(actions);
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
        // The launcher paints its own complete frame and wallpaper. No desktop composition window is created.
        if (_owner.IsLoaded) OnLoaded(_owner, new RoutedEventArgs());
    }

    private void MoveOverlay(FrameworkElement element) {
        FrameworkElement presentation = CreatePresentation(element).View;
        presentation.SetValue(Grid.RowProperty, 0);
        presentation.SetValue(Grid.RowSpanProperty, 1);
        Panel.SetZIndex(presentation, element == _owner.PanMsg ? 30 : 20);
        _root.Children.Add(presentation);
    }
    private GlassPresentationSession CreatePresentation(FrameworkElement source) {
        GlassPresentationSession session = GlassPresentation.Create(source);
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
        Refresh();
        _refreshTimer.Start();
    }
    private void OnRefreshTick(object? sender, EventArgs args) => Refresh();
    internal void Refresh(bool forcePreferences = false) {
        if (_disposed || !_attached || !_owner.IsLoaded || _refreshing) return;
        _refreshing = true;
        try {
            _owner.Opacity = 1;
            GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(Settings.Get<int>("UiPanelOpacity") / 1000d, 0.4, 1));
            GlassMaterial.SetFallbackColor(_root, ModTheme.IsDarkTheme ? Color.FromRgb(25, 34, 42) : Color.FromRgb(242, 245, 248));
            if (_owner.GlassPreviewState is not null) View.SetState(_owner.GlassPreviewState());
            else if (_runtime is not null) { _runtime.UseDarkText = !ModTheme.IsDarkTheme; _runtime.Refresh(); }
            RefreshNavigation();
            RefreshPagePresentation();
            RefreshHeaderVisibility();
            RefreshWallpaper(forcePreferences);
            foreach (GlassPresentationSession presentation in _presentations) presentation.Refresh();
            (_accountPortal as GlassAccountPanel)?.Refresh();
            foreach (GlassExtraButtonAdapter action in _extraButtons) action.RefreshAppearance(!ModTheme.IsDarkTheme, false);
            RefreshRegions();
        } catch (Exception error) {
            // Keep the shell alive during a page's transient replacement; original operation failures retain their own feedback.
            Logger.Warn(error, "刷新玻璃界面状态失败");
        } finally { _refreshing = false; }
    }
    private void RefreshPagePresentation() {
        if (ReferenceEquals(_projectedPage, _owner.PageRight)) return;
        GlassPresentationSession? previous = _pagePresentation;
        _projectedPage = _owner.PageRight;
        _pagePresentation = CreatePresentation(_owner.PanMainRight);
        View.SetPageContent(_pagePresentation.View);
        if (previous is not null) { _presentations.Remove(previous); previous.Dispose(); }
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
        }
    }
    private void OnNavigationRequested(object? sender, EventArgs args) {
        if (_owner.PageLeft is null) return;
        EnsureNavigationPortal();
    }
    private void EnsureNavigationPortal() {
        _navigationPresentation ??= CreatePresentation(_owner.PanMainLeft);
        _navigationPresentation.Refresh();
        View.SetNavigationContent(_navigationPresentation.View);
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
            _accountSource = source;
            _accountPortal = CreateAccountPortal(source);
        }
        View.SetAccountContent(_accountPortal!);
        source.RefreshPage(true, false);
    }
    internal static FrameworkElement CreateAccountPortal(PageLaunchLeft source) => new GlassAccountPanel(source);

    private void OnMinimize(object? sender, EventArgs args) => _owner.WindowState = WindowState.Minimized;
    private void OnClose(object? sender, EventArgs args) => _owner.EndProgram(true);
    private void OnDrag(object? sender, EventArgs args) {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { _owner.DragMove(); }
        catch (InvalidOperationException) { }
    }
    private void OnWallpaper(object? sender, EventArgs args) {
        if (_actionMenu is not null) _actionMenu.IsOpen = false;
        var menu = new GlassActionMenu(View.FindName("WallpaperButton") as FrameworkElement ?? View, !ModTheme.IsDarkTheme);
        _actionMenu = menu;
        menu.AddCommand("设置启动器壁纸…", ChooseWallpaper);
        menu.AddCommand("清除启动器壁纸", () => { Settings.Set("UiBackgroundMode", 1); RefreshWallpaper(true); },
            !string.IsNullOrEmpty(_wallpaperPath) || _legacyWallpaper is not null);
        menu.AddSeparator();
        menu.AddCommand("外观设置", () => _owner.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI));
        menu.AddCommand(ModTheme.IsDarkTheme ? "切换到浅色主题" : "切换到深色主题",
            () => Settings.Set("UiLauncherColorMode", ModTheme.IsDarkTheme ? 0 : 1));
        menu.AddCommand("启动器更新", () => _owner.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate));
        menu.AddCommand("任务管理", () => _owner.PageChange(FormMain.PageType.TaskManager));
        menu.AddCommand("主页内容与启动日志", () => {
            _owner.PageChange(FormMain.PageType.Launch);
            if (_runtime is not null) _runtime.ShowLegacyHome = true;
            Refresh();
        });
        menu.Show();
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
        var wallpaper = View.FindName("WallpaperImage") as Image;
        int fit = Settings.Get<int>("UiBackgroundSuit");
        int opacity = Settings.Get<int>("UiBackgroundOpacity");
        int blur = Settings.Get<int>("UiBackgroundBlur");
        double width = Math.Max(1, wallpaper?.ActualWidth > 0 ? wallpaper.ActualWidth : View.ActualWidth);
        double height = Math.Max(1, wallpaper?.ActualHeight > 0 ? wallpaper.ActualHeight : View.ActualHeight);
        string options = FormattableString.Invariant($"{fit}:{opacity}:{blur}:{width:0.###}:{height:0.###}");
        if (string.Equals(path, _wallpaperPath, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(legacy, _legacyWallpaper) && options == _wallpaperOptions) return;
        _wallpaperPath = path;
        _legacyWallpaper = legacy;
        _wallpaperOptions = options;
        ImageSource? source = legacy;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) {
            try { source = ReadWallpaper(path); }
            catch (Exception error) { Logger.Warn(error, "读取启动器壁纸失败，使用默认背景"); }
        }
        ImageSource? displayed = source is null ? null : ComposeWallpaper(source, new Size(width, height), fit);
        View.SetWallpaper(displayed!);
        if (wallpaper is not null) {
            wallpaper.Stretch = Stretch.Fill;
            wallpaper.Opacity = Math.Clamp(opacity / 1000d, 0, 1);
            wallpaper.Effect = blur <= 0 ? null : new BlurEffect { Radius = Math.Clamp(blur, 0, 40) };
        }
        GlassMaterial.SetWallpaperSource(_root, displayed);
        GlassMaterial.SetWallpaperHost(_root, wallpaper ?? (FrameworkElement)View);
        RefreshRegions();
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
    private void RefreshRegions() {
        if (View.ActualWidth <= 0 || View.ActualHeight <= 0) return;
        var surfaces = View.GetSurfaceRegions();
        // GDI regions have one-bit edges. Keep only the gap out of the native hit envelope;
        // the layered window's antialiased alpha mask owns its rounded outer edge.
        _windowRegion?.SetRegions(surfaces.Select(surface => surface with { CornerRadius = 0 }));
        if (surfaces.Count == 0) return;
        Rect envelope = surfaces[0].Bounds;
        foreach (var surface in surfaces.Skip(1)) envelope.Union(surface.Bounds);
        UpdateResizeGrips(envelope, surfaces[^1].Bounds);
    }
    private void UpdateResizeGrips(Rect area, Rect body) {
        if (_resizeParts[0] is null) return;
        const double corner = 12, edge = 5;
        Rect[] bounds = {
            new(area.Left + 36, area.Bottom - edge, Math.Max(0, area.Width - 72), edge),
            new(area.Left, area.Top + 36, edge, Math.Max(0, area.Height - 72)),
            new(area.Left, area.Bottom - corner, corner, corner),
            new(area.Left, area.Top, corner, corner),
            new(area.Right - edge, area.Top + 36, edge, Math.Max(0, area.Height - 72)),
            new(area.Right - corner, area.Bottom - corner, corner, corner),
            new(area.Right - corner, area.Top, corner, corner),
            new(area.Left + 36, area.Top, Math.Max(0, area.Width - 72), edge)
        };
        for (int index = 0; index < bounds.Length; index++) {
            Rect box = bounds[index]; FrameworkElement grip = _resizeParts[index];
            Canvas.SetLeft(grip, box.Left); Canvas.SetTop(grip, box.Top); grip.Width = box.Width; grip.Height = box.Height;
        }
        _owner.PanHint.Margin = new Thickness(body.Left + 24, 0, 0, Math.Max(0, View.ActualHeight - body.Bottom) + 24);
        _owner.PanHint.MaxWidth = Math.Max(100, body.Width - 48);
    }
    internal void SetMaterialDensity(double opacity) {
        GlassMaterial.SetOpacityMultiplier(_root, Math.Clamp(opacity, 0.4, 1));
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { _nextWallpaperRefresh = 0; RefreshRegions(); }
    private void OnActivated(object? sender, EventArgs args) { RefreshRegions(); }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _windowRegion?.Dispose();
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnRefreshTick;
        foreach (GlassPresentationSession presentation in _presentations) presentation.Dispose();
        _presentations.Clear();
        (_accountPortal as IDisposable)?.Dispose();
        foreach (GlassExtraButtonAdapter action in _extraButtons) action.Dispose();
        _extraButtons.Clear();
        if (_actionMenu is not null) _actionMenu.IsOpen = false;
        _runtime?.Dispose();
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
