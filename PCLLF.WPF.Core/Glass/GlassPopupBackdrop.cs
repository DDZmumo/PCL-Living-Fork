// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>Wallpaper sampling with an explicit desktop backdrop option for translucent popups.</summary>
public static class GlassPopupBackdrop {
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(GlassPopupBackdrop), new PropertyMetadata(false, OnEnableChanged));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(double), typeof(GlassPopupBackdrop), new PropertyMetadata(24d));
    private static readonly DependencyProperty LeaseProperty = DependencyProperty.RegisterAttached(
        "Lease", typeof(Lease), typeof(GlassPopupBackdrop), new PropertyMetadata(null));
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static double GetCornerRadius(DependencyObject element) => (double)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, double value) => element.SetValue(CornerRadiusProperty, value);

    private static void OnEnableChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) {
        if (element is not FrameworkElement root) return;
        if ((bool)args.NewValue) {
            root.Loaded += OnLoaded; root.Unloaded += OnUnloaded;
            if (root.IsLoaded) OnLoaded(root, new RoutedEventArgs());
        } else {
            root.Loaded -= OnLoaded; root.Unloaded -= OnUnloaded;
            OnUnloaded(root, new RoutedEventArgs());
        }
    }
    private static void OnLoaded(object sender, RoutedEventArgs args) {
        var root = (FrameworkElement)sender;
        if (root.GetValue(LeaseProperty) is not null || PresentationSource.FromVisual(root) is not HwndSource source) return;
        root.SetValue(LeaseProperty, new Lease(root, source));
    }
    private static void OnUnloaded(object sender, RoutedEventArgs args) {
        var root = (FrameworkElement)sender;
        (root.GetValue(LeaseProperty) as Lease)?.Dispose(); root.ClearValue(LeaseProperty);
    }

    private sealed class Lease : IDisposable {
        private readonly FrameworkElement _root;
        private readonly HwndSource _source;
        private readonly HwndSourceHook _hook;
        private readonly Canvas? _wallpaperLayer;
        private readonly Border? _wallpaperHost;
        private GlassCompositionHost? _host;
        private bool _disposed, _queued, _failed;
        internal Lease(FrameworkElement root, HwndSource source) {
            _root = root; _source = source; _hook = Hook;
            if (root is Panel panel) {
                _wallpaperLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Hidden };
                _wallpaperHost = new Border(); _wallpaperLayer.Children.Add(_wallpaperHost);
                panel.Children.Insert(0, _wallpaperLayer);
            }
            source.AddHook(_hook);
            root.LayoutUpdated += OnLayoutUpdated;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
            QueueRefresh();
        }
        private void OnLayoutUpdated(object? sender, EventArgs args) => QueueRefresh();
        private void OnSystemChanged(object? sender, PropertyChangedEventArgs args) => QueueRefresh();
        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) {
            if (message is 0x0047 or 0x0018 or 0x02E0 or 0x031E or 0x001A) QueueRefresh();
            if (message == 0x0082) Dispose();
            return 0;
        }
        private void QueueRefresh() {
            if (_disposed || _queued || _root.Dispatcher.HasShutdownStarted) return;
            _queued = true;
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _queued = false; if (!_disposed) Refresh(); }));
        }
        private void Refresh() {
            if (_source.CompositionTarget is not { } target || _root.RenderSize.Width <= 0 || _root.RenderSize.Height <= 0) return;
            var context = _root.TemplatedParent ?? _root.Parent ?? _root;
            bool requested = GlassMaterial.GetIsEnabled(context);
            bool wallpaper = GlassMaterial.GetWallpaperSource(context) is not null;
            bool native = GlassMaterial.GetDesktopBackdropEnabled(context) && requested && !wallpaper && !_failed && !SystemParameters.HighContrast && GlassSystemPreferences.TransparencyEnabled &&
                          OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
            GlassMaterial.SetDesktopBackdropEnabled(_root, native);
            GlassMaterial.SetIsEnabled(_root, requested && (native || wallpaper));
            if (wallpaper) AlignWallpaper(context);
            else _root.ClearValue(GlassMaterial.WallpaperHostProperty);
            try {
                if (native) {
                    _host ??= new GlassCompositionHost(_source.Handle);
                    Rect bounds = _root.TransformToVisual(_source.RootVisual).TransformBounds(new Rect(_root.RenderSize));
                    _host.SetRegions([new GlassWindowRegion(bounds, GetCornerRadius(_root), _root.Opacity)], target.TransformToDevice);
                }
                _host?.SyncBounds(() => !_disposed && native && _root.IsVisible);
            } catch (Exception error) when (error is COMException or Win32Exception or InvalidOperationException) {
                _host?.Dispose(); _host = null; _failed = true;
                GlassMaterial.SetDesktopBackdropEnabled(_root, false);
                GlassMaterial.SetIsEnabled(_root, wallpaper && requested);
            }
        }
        private void AlignWallpaper(DependencyObject context) {
            if (_wallpaperHost is null) return;
            var original = GlassMaterial.GetWallpaperHost(context);
            if (original is null || ReferenceEquals(original, _wallpaperHost)) return;
            try {
                Point origin = _root.PointFromScreen(original.PointToScreen(new Point()));
                Point end = _root.PointFromScreen(original.PointToScreen(new Point(original.RenderSize.Width, original.RenderSize.Height)));
                _wallpaperHost.Width = Math.Max(1, end.X - origin.X); _wallpaperHost.Height = Math.Max(1, end.Y - origin.Y);
                _wallpaperHost.Opacity = original.Opacity;
                GlassMaterial.SetWallpaperStretch(_wallpaperHost, original is Image image ? image.Stretch : GlassMaterial.GetWallpaperStretch(original));
                if (original.Effect is BlurEffect blur) {
                    if (_wallpaperHost.Effect is not BlurEffect current || current.Radius != blur.Radius || current.KernelType != blur.KernelType)
                        _wallpaperHost.Effect = blur.CloneCurrentValue();
                } else if (_wallpaperHost.Effect is not null) _wallpaperHost.Effect = null;
                Canvas.SetLeft(_wallpaperHost, origin.X); Canvas.SetTop(_wallpaperHost, origin.Y);
                GlassMaterial.SetWallpaperHost(_root, _wallpaperHost);
            } catch (InvalidOperationException) { }
        }
        public void Dispose() {
            if (_disposed) return; _disposed = true;
            _source.RemoveHook(_hook);
            _root.LayoutUpdated -= OnLayoutUpdated;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
            _host?.Dispose(); _host = null;
            if (_root is Panel panel && _wallpaperLayer is not null) panel.Children.Remove(_wallpaperLayer);
            _root.ClearValue(GlassMaterial.IsEnabledProperty); _root.ClearValue(GlassMaterial.WallpaperHostProperty);
            _root.ClearValue(GlassMaterial.DesktopBackdropEnabledProperty);
        }
    }
}
