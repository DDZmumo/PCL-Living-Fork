// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>Aligns popup frost to the wallpaper viewport or to the windows behind the popup.</summary>
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
        private static readonly DependencyPropertyDescriptor? OpacityProperty =
            DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        private readonly FrameworkElement _root;
        private readonly FrameworkElement _surface;
        private readonly HwndSource _source;
        private readonly HwndSourceHook _hook;
        private readonly Canvas? _wallpaperLayer;
        private readonly Border? _wallpaperHost;
        private readonly GlassSurface.TransformWatch _transform;
        private GlassCompositionHost? _host;
        private Window? _wallpaperWindow;
        private bool _disposed, _queued, _failed, _resetHost;
        internal Lease(FrameworkElement root, HwndSource source) {
            _root = root; _source = source; _hook = Hook;
            _surface = (root as Panel)?.Children.OfType<GlassSurface>().FirstOrDefault() ?? root;
            if (root is Panel panel) {
                _wallpaperLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Hidden };
                _wallpaperHost = new Border(); _wallpaperLayer.Children.Add(_wallpaperHost);
                panel.Children.Insert(0, _wallpaperLayer);
            }
            source.AddHook(_hook);
            _transform = new GlassSurface.TransformWatch(_surface, QueueRefresh);
            OpacityProperty?.AddValueChanged(_surface, OnSurfaceOpacityChanged);
            root.LayoutUpdated += OnLayoutUpdated;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
            QueueRefresh();
        }
        private void OnLayoutUpdated(object? sender, EventArgs args) => QueueRefresh();
        private void OnWallpaperWindowMoved(object? sender, EventArgs args) => QueueRefresh();
        private void OnSurfaceOpacityChanged(object? sender, EventArgs args) => QueueRefresh();
        private void OnSystemChanged(object? sender, PropertyChangedEventArgs args) => QueueRefresh();
        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) {
            if (GlassCompositionHost.IsResetNotification(message, wParam)) {
                _resetHost = true;
                QueueRefresh();
            }
            else if (message is 0x0047 or 0x0018 or 0x02E0 or 0x001A) QueueRefresh();
            if (message == 0x0082) Dispose();
            return 0;
        }
        private void QueueRefresh() {
            if (_disposed || _queued || _root.Dispatcher.HasShutdownStarted) return;
            _queued = true;
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _queued = false; if (!_disposed) Refresh(); }));
        }
        private void Refresh() {
            if (_resetHost) {
                _resetHost = false;
                _failed = false;
                DisposeHost();
            }
            if (_source.CompositionTarget is not { } target || _source.RootVisual is not { } windowRoot ||
                _surface.RenderSize.Width <= 0 || _surface.RenderSize.Height <= 0) return;
            var context = MaterialContext();
            bool requested = GlassMaterial.GetIsEnabled(context);
            bool wallpaper = GlassMaterial.GetWallpaperSource(context) is not null;
            bool useWallpaper = false;
            try {
                var original = wallpaper ? GlassMaterial.GetWallpaperHost(context) : null;
                WatchWallpaperWindow(original is null ? null : Window.GetWindow(original));
                Rect surface = _surface.TransformToVisual(_root).TransformBounds(new Rect(_surface.RenderSize));
                bool crossesWallpaper = false;
                if (wallpaper && original is not null && TryWallpaperPlacement(original, out Rect texture, out Rect visible)) {
                    // Use one sample of the scene for the full popup when it straddles the window edge.
                    crossesWallpaper = !visible.Contains(surface);
                    useWallpaper = requested && !crossesWallpaper && AlignWallpaper(original, texture);
                }
                bool requestedNative = crossesWallpaper || (!wallpaper && GlassMaterial.GetDesktopBackdropEnabled(context));
                bool native = requested && requestedNative && !_failed && !SystemParameters.HighContrast &&
                              GlassSystemPreferences.TransparencyEnabled && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
                if (native) {
                    _host ??= new GlassCompositionHost(_source.Handle);
                    Rect bounds = _surface.TransformToVisual(windowRoot).TransformBounds(new Rect(_surface.RenderSize));
                    double radius = GetCornerRadius(_root) * Math.Min(bounds.Width / _surface.RenderSize.Width, bounds.Height / _surface.RenderSize.Height);
                    _host.SetRegions([new GlassWindowRegion(bounds, radius, _surface.Opacity)], target.TransformToDevice);
                }
                _host?.SyncBounds(() => !_disposed && native && _surface.IsVisible);
                ApplyMaterial(context, useWallpaper, native);
            } catch (Exception error) when (error is COMException or Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) {
                DisposeHost(); _failed = true;
                ApplyMaterial(context, useWallpaper, native: false);
            }
        }

        private DependencyObject MaterialContext() {
            // A submenu must see the command menu's owner's wallpaper, even if the parent popup uses desktop frost.
            var context = _root.TemplatedParent ?? _root.Parent ?? _root;
            while (context is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } owner)
                context = owner;
            return context;
        }

        private void WatchWallpaperWindow(Window? window) {
            if (ReferenceEquals(_wallpaperWindow, window)) return;
            if (_wallpaperWindow is not null) _wallpaperWindow.LocationChanged -= OnWallpaperWindowMoved;
            _wallpaperWindow = window;
            if (window is not null) window.LocationChanged += OnWallpaperWindowMoved;
        }

        private void ApplyMaterial(DependencyObject context, bool wallpaper, bool native) {
            GlassMaterial.SetDesktopBackdropEnabled(_root, native);
            GlassMaterial.SetIsEnabled(_root, wallpaper || native);
            GlassMaterial.SetWallpaperSource(_root, wallpaper ? GlassMaterial.GetWallpaperSource(context) : null);
            GlassMaterial.SetWallpaperHost(_root, wallpaper ? _wallpaperHost : null);
        }

        private bool TryWallpaperPlacement(FrameworkElement original, out Rect texture, out Rect visible) {
            texture = visible = Rect.Empty;
            if (ReferenceEquals(original, _wallpaperHost) || original.RenderSize.Width <= 0 || original.RenderSize.Height <= 0 ||
                PresentationSource.FromVisual(original)?.RootVisual is not FrameworkElement windowRoot) return false;
            texture = ToPopupBounds(original, new Rect(original.RenderSize));
            visible = texture;
            for (Visual? element = original; element is not null; element = VisualTreeHelper.GetParent(element) as Visual) {
                if (VisualTreeHelper.GetClip(element) is { } clip)
                    visible.Intersect(ToPopupBounds(element, clip.Bounds));
                if (ReferenceEquals(element, windowRoot)) break;
            }
            visible.Intersect(ToPopupBounds(windowRoot, new Rect(windowRoot.RenderSize)));
            return !texture.IsEmpty && texture.Width > 0 && texture.Height > 0;
        }

        private Rect ToPopupBounds(Visual original, Rect bounds) => bounds.IsEmpty ? Rect.Empty : new Rect(
            _root.PointFromScreen(original.PointToScreen(bounds.TopLeft)),
            _root.PointFromScreen(original.PointToScreen(bounds.BottomRight)));

        private bool AlignWallpaper(FrameworkElement original, Rect bounds) {
            if (_wallpaperHost is null) return false;
            _wallpaperHost.Width = bounds.Width; _wallpaperHost.Height = bounds.Height;
            _wallpaperHost.Opacity = original.Opacity;
            GlassMaterial.SetWallpaperStretch(_wallpaperHost, original is Image ? Stretch.Fill : GlassMaterial.GetWallpaperStretch(original));
            if (original.Effect is BlurEffect blur) {
                if (_wallpaperHost.Effect is not BlurEffect current || current.Radius != blur.Radius || current.KernelType != blur.KernelType)
                    _wallpaperHost.Effect = blur.CloneCurrentValue();
            } else if (_wallpaperHost.Effect is not null) _wallpaperHost.Effect = null;
            Canvas.SetLeft(_wallpaperHost, bounds.Left); Canvas.SetTop(_wallpaperHost, bounds.Top);
            return true;
        }
        private void DisposeHost() {
            var host = _host;
            _host = null;
            host?.Dispose();
        }
        public void Dispose() {
            if (_disposed) return; _disposed = true;
            _source.RemoveHook(_hook);
            _root.LayoutUpdated -= OnLayoutUpdated;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
            WatchWallpaperWindow(null);
            OpacityProperty?.RemoveValueChanged(_surface, OnSurfaceOpacityChanged);
            _transform.Dispose();
            DisposeHost();
            if (_root is Panel panel && _wallpaperLayer is not null) panel.Children.Remove(_wallpaperLayer);
            _root.ClearValue(GlassMaterial.IsEnabledProperty); _root.ClearValue(GlassMaterial.WallpaperHostProperty);
            _root.ClearValue(GlassMaterial.WallpaperSourceProperty);
            _root.ClearValue(GlassMaterial.DesktopBackdropEnabledProperty);
        }
    }
}
