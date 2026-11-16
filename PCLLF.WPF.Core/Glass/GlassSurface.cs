// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// A Border-compatible application-wallpaper surface. Optional blur affects only its image layer.
/// A missing image uses the inherited opaque fallback; desktop material is disabled by default.
/// </summary>
public sealed class GlassSurface : Border {
    public static readonly DependencyProperty WallpaperSourceProperty = GlassMaterial.WallpaperSourceProperty;
    public static readonly DependencyProperty WallpaperHostProperty = GlassMaterial.WallpaperHostProperty;
    public static readonly DependencyProperty IsMaterialEnabledProperty = GlassMaterial.IsEnabledProperty;
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassSurface), new FrameworkPropertyMetadata(0d, OnMaterialPropertyChanged),
        value => value is double number && double.IsFinite(number) && number >= 0 && number <= 100);
    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity), typeof(double), typeof(GlassSurface), new FrameworkPropertyMetadata(1d, OnMaterialPropertyChanged),
        value => value is double number && double.IsFinite(number) && number >= 0 && number <= 1);
    public static readonly DependencyProperty HighlightOpacityProperty = DependencyProperty.Register(
        nameof(HighlightOpacity), typeof(double), typeof(GlassSurface), new FrameworkPropertyMetadata(0d, OnMaterialPropertyChanged),
        value => value is double number && double.IsFinite(number) && number >= 0 && number <= 1);

    private readonly MaterialLayer _material;
    private Rect _lastWallpaperBounds = Rect.Empty;
    private FrameworkElement? _watchedHost;
    private Effect? _watchedEffect;
    private static readonly DependencyPropertyDescriptor? HostOpacity = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
    private static readonly DependencyPropertyDescriptor? HostEffect = DependencyPropertyDescriptor.FromProperty(UIElement.EffectProperty, typeof(UIElement));

    static GlassSurface() {
        // Native composition already supplies the frosted sample. A thin neutral tint and
        // small highlight preserve its colors instead of stacking a gray veil on every card.
        var tint = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        tint.Freeze();
        var edge = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        edge.Freeze();
        BackgroundProperty.OverrideMetadata(typeof(GlassSurface), new FrameworkPropertyMetadata(tint, OnMaterialPropertyChanged));
        BorderBrushProperty.OverrideMetadata(typeof(GlassSurface), new FrameworkPropertyMetadata(edge, OnMaterialPropertyChanged));
        BorderThicknessProperty.OverrideMetadata(typeof(GlassSurface), new FrameworkPropertyMetadata(new Thickness(1), OnMaterialPropertyChanged));
        CornerRadiusProperty.OverrideMetadata(typeof(GlassSurface), new FrameworkPropertyMetadata(new CornerRadius(16), OnMaterialPropertyChanged));
    }

    public GlassSurface() {
        _material = new MaterialLayer(this);
        AddVisualChild(_material);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
    }

    public ImageSource? WallpaperSource { get => GlassMaterial.GetWallpaperSource(this); set => GlassMaterial.SetWallpaperSource(this, value); }
    public FrameworkElement? WallpaperHost { get => GlassMaterial.GetWallpaperHost(this); set => GlassMaterial.SetWallpaperHost(this, value); }
    public bool IsMaterialEnabled { get => GlassMaterial.GetIsEnabled(this); set => GlassMaterial.SetIsEnabled(this, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double TintOpacity { get => (double)GetValue(TintOpacityProperty); set => SetValue(TintOpacityProperty, value); }
    public double HighlightOpacity { get => (double)GetValue(HighlightOpacityProperty); set => SetValue(HighlightOpacityProperty, value); }

    internal bool CanUseTransparency => IsMaterialEnabled && !SystemParameters.HighContrast && GlassSystemPreferences.TransparencyEnabled &&
        (WallpaperSource is not null || GlassMaterial.GetDesktopBackdropEnabled(this));
    private double ImageOpacity => Math.Clamp(WallpaperHost?.Opacity ?? 1, 0, 1);
    private Stretch ImageStretch => WallpaperHost is Image image ? image.Stretch : GlassMaterial.GetWallpaperStretch(WallpaperHost ?? this);
    private double EffectiveBlurRadius {
        get {
            double global = WallpaperHost?.Effect is BlurEffect { KernelType: KernelType.Gaussian } blur ? blur.Radius : 0;
            return Math.Sqrt(BlurRadius * BlurRadius + global * global);
        }
    }

    // The material is a separate visual behind Border.Child, so no effect ever reaches content.
    protected override int VisualChildrenCount => base.VisualChildrenCount + (_material is null ? 0 : 1);
    protected override Visual GetVisualChild(int index) {
        if (_material is not null) {
            if (index == 0) return _material;
            index--;
        }
        return base.GetVisualChild(index);
    }

    protected override Size MeasureOverride(Size constraint) {
        var result = base.MeasureOverride(constraint);
        _material.Measure(result);
        return result;
    }

    protected override Size ArrangeOverride(Size arrangeSize) {
        var result = base.ArrangeOverride(arrangeSize);
        _material.Arrange(new Rect(arrangeSize));
        return result;
    }

    // MaterialLayer owns background/border rendering, including rounded clipping of its blur.
    protected override void OnRender(DrawingContext drawingContext) { }

    internal void InvalidateMaterial() {
        if (_material is null) return;
        WatchWallpaperHost();
        _lastWallpaperBounds = Rect.Empty;
        _material.Update();
        InvalidateArrange();
        InvalidateVisual();
    }

    private static void OnMaterialPropertyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) =>
        ((GlassSurface)element).InvalidateMaterial();

    private void OnLoaded(object sender, RoutedEventArgs args) {
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        InvalidateMaterial();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) {
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        UnwatchWallpaperHost();
    }
    private void WatchWallpaperHost() {
        var host = IsLoaded ? WallpaperHost : null;
        if (!ReferenceEquals(host, _watchedHost)) {
            UnwatchWallpaperHost(); _watchedHost = host;
            if (host is not null) { HostOpacity?.AddValueChanged(host, HostPresentationChanged); HostEffect?.AddValueChanged(host, HostPresentationChanged); }
        }
        var effect = host?.Effect;
        if (ReferenceEquals(effect, _watchedEffect)) return;
        if (_watchedEffect is { IsFrozen: false }) _watchedEffect.Changed -= HostPresentationChanged;
        _watchedEffect = effect;
        if (effect is { IsFrozen: false }) effect.Changed += HostPresentationChanged;
    }
    private void UnwatchWallpaperHost() {
        if (_watchedHost is not null) { HostOpacity?.RemoveValueChanged(_watchedHost, HostPresentationChanged); HostEffect?.RemoveValueChanged(_watchedHost, HostPresentationChanged); }
        if (_watchedEffect is { IsFrozen: false }) _watchedEffect.Changed -= HostPresentationChanged;
        _watchedHost = null; _watchedEffect = null;
    }
    private void HostPresentationChanged(object? sender, EventArgs args) => InvalidateMaterial();

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs args) => QueuePreferenceRefresh();
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => QueuePreferenceRefresh();
    private void QueuePreferenceRefresh() {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
            if (!IsLoaded) return;
            GlassSystemPreferences.Refresh();
            InvalidateMaterial();
        }));
    }

    private void OnLayoutUpdated(object? sender, EventArgs args) {
        if (WallpaperSource is null || !CanUseTransparency) return;
        var bounds = GetWallpaperBounds(this);
        if (bounds == _lastWallpaperBounds) return;
        _lastWallpaperBounds = bounds;
        _material.InvalidateWallpaper();
    }

    private Rect GetWallpaperBounds(Visual target) {
        var host = WallpaperHost ?? Window.GetWindow(this)?.Content as FrameworkElement ?? this;
        try {
            if (host.RenderSize.Width > 0 && host.RenderSize.Height > 0)
                return host.TransformToVisual(target).TransformBounds(new Rect(host.RenderSize));
        } catch (InvalidOperationException) {
            // Disconnected designer trees use a local preview until the shared host is loaded.
        }
        return new Rect(RenderSize);
    }

    private sealed class MaterialLayer : FrameworkElement {
        private readonly GlassSurface _owner;
        private readonly WallpaperPlane _wallpaper;
        private readonly FinishPlane _finish;
        private readonly BlurEffect _blur = new() { RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
        private Size _maskSize;
        private CornerRadius _maskCorners;
        private bool _hasMask;

        internal MaterialLayer(GlassSurface owner) {
            _owner = owner;
            IsHitTestVisible = false;
            Focusable = false;
            _wallpaper = new WallpaperPlane(owner) { IsHitTestVisible = false };
            _finish = new FinishPlane(owner) { IsHitTestVisible = false };
            AddVisualChild(_wallpaper);
            AddVisualChild(_finish);
        }

        protected override int VisualChildrenCount => 2;
        protected override Visual GetVisualChild(int index) => index switch {
            0 => _wallpaper,
            1 => _finish,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        internal void Update() {
            double radius = _owner.EffectiveBlurRadius;
            _blur.Radius = radius;
            _wallpaper.Effect = radius > 0 ? _blur : null;
            _wallpaper.Visibility = _owner.WallpaperSource is not null && _owner.CanUseTransparency ? Visibility.Visible : Visibility.Hidden;
            UpdateMask(RenderSize);
            InvalidateMeasure();
            InvalidateArrange();
            _wallpaper.InvalidateVisual();
            _finish.InvalidateVisual();
        }

        internal void InvalidateWallpaper() => _wallpaper.InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize) {
            _wallpaper.Measure(availableSize);
            _finish.Measure(availableSize);
            return new Size();
        }

        protected override Size ArrangeOverride(Size finalSize) {
            // Sample beyond the surface edge before blurring, then clip the finished material.
            var margin = _owner.EffectiveBlurRadius * 2;
            _wallpaper.Arrange(new Rect(-margin, -margin, finalSize.Width + margin * 2, finalSize.Height + margin * 2));
            _finish.Arrange(new Rect(finalSize));
            UpdateMask(finalSize);
            return finalSize;
        }

        private void UpdateMask(Size size) {
            if (size.Width <= 0 || size.Height <= 0) {
                Clip = Geometry.Empty; OpacityMask = null; _hasMask = false; return;
            }
            if (_hasMask && _maskSize == size && _maskCorners == _owner.CornerRadius) return;
            var bounds = new Rect(size);
            // Limit the *post-effect* descendant bounds before mapping the mask: the
            // wallpaper child deliberately extends outside this rectangle for blur sampling.
            var rectangle = new RectangleGeometry(bounds); rectangle.Freeze(); Clip = rectangle;
            var drawing = new GeometryDrawing(Brushes.White, null, RoundedGeometry(bounds, _owner.CornerRadius));
            drawing.Freeze();
            var mask = new DrawingBrush(drawing) {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds,
                Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            };
            mask.Freeze(); OpacityMask = mask;
            _maskSize = size; _maskCorners = _owner.CornerRadius; _hasMask = true;
        }
    }

    private sealed class WallpaperPlane(GlassSurface owner) : FrameworkElement {
        protected override void OnRender(DrawingContext drawingContext) {
            if (owner.WallpaperSource is not { } source || !owner.CanUseTransparency) return;
            var wallpaperBounds = owner.GetWallpaperBounds(this);
            if (wallpaperBounds.IsEmpty || wallpaperBounds.Width <= 0 || wallpaperBounds.Height <= 0) return;
            var brush = new ImageBrush(source) {
                Stretch = owner.ImageStretch,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = wallpaperBounds,
                TileMode = TileMode.None
            };
            Color color = GlassMaterial.GetFallbackColor(owner);
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), null, new Rect(RenderSize));
            drawingContext.PushOpacity(owner.ImageOpacity);
            drawingContext.DrawRectangle(brush, null, new Rect(RenderSize));
            drawingContext.Pop();
        }
    }

    private sealed class FinishPlane(GlassSurface owner) : FrameworkElement {
        protected override void OnRender(DrawingContext drawingContext) {
            var bounds = new Rect(RenderSize);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var outer = RoundedGeometry(bounds, owner.CornerRadius);
            var transparent = owner.CanUseTransparency;
            var background = owner.Background;
            if (!transparent) {
                background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OpaqueBrush(GlassMaterial.GetFallbackColor(owner));
            }
            drawingContext.PushOpacity(transparent ? owner.TintOpacity * GlassMaterial.GetOpacityMultiplier(owner) : 1);
            drawingContext.DrawGeometry(background, null, outer);
            drawingContext.Pop();

            if (transparent && owner.HighlightOpacity > 0) {
                var highlight = new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(0, 0), new Point(0, 1));
                drawingContext.PushOpacity(owner.HighlightOpacity);
                drawingContext.DrawGeometry(highlight, null, outer);
                drawingContext.Pop();
            }

            var thickness = owner.BorderThickness;
            if (thickness.Left + thickness.Top + thickness.Right + thickness.Bottom <= 0) return;
            var innerBounds = new Rect(thickness.Left, thickness.Top,
                Math.Max(0, bounds.Width - thickness.Left - thickness.Right),
                Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom));
            var innerRadius = new CornerRadius(
                Math.Max(0, owner.CornerRadius.TopLeft - Math.Max(thickness.Left, thickness.Top)),
                Math.Max(0, owner.CornerRadius.TopRight - Math.Max(thickness.Right, thickness.Top)),
                Math.Max(0, owner.CornerRadius.BottomRight - Math.Max(thickness.Right, thickness.Bottom)),
                Math.Max(0, owner.CornerRadius.BottomLeft - Math.Max(thickness.Left, thickness.Bottom)));
            var border = new CombinedGeometry(GeometryCombineMode.Exclude, outer, RoundedGeometry(innerBounds, innerRadius));
            drawingContext.DrawGeometry(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : owner.BorderBrush, null, border);
        }

        private static Brush OpaqueBrush(Color color) {
            return new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        }
    }

    private static Geometry RoundedGeometry(Rect bounds, CornerRadius corners) {
        if (bounds.Width <= 0 || bounds.Height <= 0) return Geometry.Empty;
        var tl = Math.Min(corners.TopLeft, Math.Min(bounds.Width, bounds.Height) / 2);
        var tr = Math.Min(corners.TopRight, Math.Min(bounds.Width, bounds.Height) / 2);
        var br = Math.Min(corners.BottomRight, Math.Min(bounds.Width, bounds.Height) / 2);
        var bl = Math.Min(corners.BottomLeft, Math.Min(bounds.Width, bounds.Height) / 2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) {
            context.BeginFigure(new Point(bounds.Left + tl, bounds.Top), true, true);
            context.LineTo(new Point(bounds.Right - tr, bounds.Top), true, false);
            context.ArcTo(new Point(bounds.Right, bounds.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Right, bounds.Bottom - br), true, false);
            context.ArcTo(new Point(bounds.Right - br, bounds.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Left + bl, bounds.Bottom), true, false);
            context.ArcTo(new Point(bounds.Left, bounds.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Left, bounds.Top + tl), true, false);
            context.ArcTo(new Point(bounds.Left + tl, bounds.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
