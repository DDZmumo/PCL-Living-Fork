// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>Aligns only an application-owned image across popup visual trees; never creates a native backdrop.</summary>
public static class WallpaperPopupContext {
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(WallpaperPopupContext), new PropertyMetadata(false, OnEnableChanged));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(double), typeof(WallpaperPopupContext), new PropertyMetadata(14d));
    private static readonly DependencyProperty LeaseProperty = DependencyProperty.RegisterAttached(
        "Lease", typeof(Lease), typeof(WallpaperPopupContext), new PropertyMetadata(null));
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static double GetCornerRadius(DependencyObject element) => (double)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, double value) => element.SetValue(CornerRadiusProperty, value);
    private static void OnEnableChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) {
        if (element is not FrameworkElement root) return;
        if ((bool)args.NewValue) {
            root.Loaded += Loaded; root.Unloaded += Unloaded;
            if (root.IsLoaded) Loaded(root, new RoutedEventArgs());
        } else {
            root.Loaded -= Loaded; root.Unloaded -= Unloaded; Unloaded(root, new RoutedEventArgs());
        }
    }
    private static void Loaded(object sender, RoutedEventArgs args) {
        var root = (FrameworkElement)sender;
        if (root.GetValue(LeaseProperty) is null) root.SetValue(LeaseProperty, new Lease(root));
    }
    private static void Unloaded(object sender, RoutedEventArgs args) {
        var root = (FrameworkElement)sender;
        (root.GetValue(LeaseProperty) as Lease)?.Dispose(); root.ClearValue(LeaseProperty);
    }
    private sealed class Lease : IDisposable {
        private readonly FrameworkElement _root;
        private readonly Canvas? _layer;
        private readonly Border? _host;
        private bool _disposed, _queued;
        private Color? _paintedFallback;
        private Size _clippedSize;
        internal Lease(FrameworkElement root) {
            _root = root;
            if (root is Panel panel) {
                _layer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Hidden };
                _host = new Border(); _layer.Children.Add(_host); panel.Children.Insert(0, _layer);
            }
            root.LayoutUpdated += LayoutUpdated;
            Refresh(); QueueRefresh();
        }
        private void LayoutUpdated(object? sender, EventArgs args) => QueueRefresh();
        private void QueueRefresh() {
            if (_disposed || _queued || _root.Dispatcher.HasShutdownStarted) return;
            _queued = true;
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _queued = false; if (!_disposed) Refresh(); }));
        }
        private void Refresh() {
            var context = _root.TemplatedParent ?? _root.Parent ?? _root;
            bool hasPicture = GlassMaterial.GetWallpaperSource(context) is not null;
            if (_root is Panel panel) {
                Color color = GlassMaterial.GetFallbackColor(_root);
                if (_paintedFallback != color) { panel.Background = new SolidColorBrush(color); _paintedFallback = color; }
                if (_clippedSize != _root.RenderSize && _root.RenderSize.Width > 0 && _root.RenderSize.Height > 0) {
                    _clippedSize = _root.RenderSize;
                    double radius = GetCornerRadius(_root);
                    _root.Clip = new RectangleGeometry(new Rect(_clippedSize), radius, radius);
                }
            }
            // Without an owned picture the popup is fully opaque. Transparent pixels must
            // never reveal or sample the Windows desktop under a wallpaper-only interface.
            GlassMaterial.SetIsEnabled(_root, hasPicture && GlassMaterial.GetIsEnabled(context));
            GlassMaterial.SetDesktopBackdropEnabled(_root, false);
            if (!hasPicture || _host is null || GlassMaterial.GetWallpaperHost(context) is not { } original) return;
            try {
                Point origin = _root.PointFromScreen(original.PointToScreen(new Point()));
                Point end = _root.PointFromScreen(original.PointToScreen(new Point(original.RenderSize.Width, original.RenderSize.Height)));
                _host.Width = Math.Max(1, end.X - origin.X); _host.Height = Math.Max(1, end.Y - origin.Y);
                _host.Opacity = original.Opacity;
                GlassMaterial.SetWallpaperStretch(_host, original is Image image ? image.Stretch : GlassMaterial.GetWallpaperStretch(original));
                if (original.Effect is BlurEffect blur) {
                    if (_host.Effect is not BlurEffect current || current.Radius != blur.Radius || current.KernelType != blur.KernelType)
                        _host.Effect = blur.CloneCurrentValue();
                } else if (_host.Effect is not null) _host.Effect = null;
                Canvas.SetLeft(_host, origin.X); Canvas.SetTop(_host, origin.Y);
                GlassMaterial.SetWallpaperHost(_root, _host);
            } catch (InvalidOperationException) { }
        }
        public void Dispose() {
            if (_disposed) return; _disposed = true;
            _root.LayoutUpdated -= LayoutUpdated;
            if (_root is Panel panel && _layer is not null) panel.Children.Remove(_layer);
            _root.ClearValue(GlassMaterial.IsEnabledProperty); _root.ClearValue(GlassMaterial.WallpaperHostProperty);
            _root.ClearValue(GlassMaterial.DesktopBackdropEnabledProperty);
            if (_root is Panel painted) painted.ClearValue(Panel.BackgroundProperty);
            _root.ClearValue(UIElement.ClipProperty);
        }
    }
}
