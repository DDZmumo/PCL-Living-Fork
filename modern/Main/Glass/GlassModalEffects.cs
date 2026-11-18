// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Focuses an in-window dialog without blurring its own text or controls.</summary>
internal sealed class GlassModalEffects : IDisposable {
    private readonly FrameworkElement _background;
    // The layer that is blurred and sampled for the dialog's frost. The shell passes only the main body so the
    // icon rail stays crisp; input to the whole background (rail included) is still withheld while a modal is open.
    private readonly FrameworkElement _blurTarget;
    private readonly FrameworkElement _owner;
    private readonly Border _scrim;
    private readonly FrameworkElement _dialog;
    private readonly BlurEffect _blur = new() { Radius = 0, RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
    private readonly BitmapCache _blurCache = new() { EnableClearType = false, RenderAtScale = 0.5 };
    private readonly SpringMotion _motion;
    private readonly Effect? _originalEffect;
    private readonly CacheMode? _originalCacheMode;
    private object? _message;
    private bool _disposed;
    private DispatcherOperation? _snapshotRefresh;
    internal double Progress => _motion.Value;
    internal bool HasActiveMessage => _message is not null;

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, Border scrim, FrameworkElement dialog)
        : this(owner, background, background, scrim, dialog) { }

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, FrameworkElement blurTarget, Border scrim, FrameworkElement dialog) {
        _owner = owner; _background = background; _blurTarget = blurTarget; _scrim = scrim; _dialog = dialog;
        _originalEffect = blurTarget.Effect;
        _originalCacheMode = blurTarget.CacheMode;
        _motion = new SpringMotion(owner, 0, Apply, new SpringParameters(.26, 1));
        blurTarget.SizeChanged += OnBackgroundSizeChanged;
    }

    internal void SetMessage(object? message) {
        if (_disposed || ReferenceEquals(_message, message)) return;
        _message = message;
        if (message is not null) {
            _scrim.Visibility = Visibility.Visible;
            ImageSource? snapshot = Snapshot();
            GlassMaterial.SetIgnoreWallpaperHostEffect(_dialog, true);
            GlassMaterial.SetWallpaperSource(_dialog, snapshot);
            GlassMaterial.SetWallpaperHost(_dialog, _blurTarget);
            GlassMaterial.SetWallpaperStretch(_dialog, Stretch.Fill);
            GlassMaterial.SetDesktopBackdropEnabled(_dialog, false);
            _blurTarget.CacheMode ??= _blurCache;
            _blurTarget.Effect = _blur;
        }
        // Input ownership changes immediately, independently of the fade/spring endpoint.
        _background.IsHitTestVisible = message is null;
        _dialog.IsHitTestVisible = message is not null;
        _scrim.IsHitTestVisible = message is not null;
        _motion.Retarget(message is null ? 0 : 1);
    }

    private void OnBackgroundSizeChanged(object sender, SizeChangedEventArgs args) => RefreshSnapshot();
    internal void RefreshSnapshot() {
        if (_disposed || _message is null || _owner.Dispatcher.HasShutdownStarted || _snapshotRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _snapshotRefresh = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
            _snapshotRefresh = null;
            if (_disposed || _message is null) return;
            GlassMaterial.SetWallpaperSource(_dialog, Snapshot());
        }));
    }

    private ImageSource? Snapshot() {
        if (_blurTarget.ActualWidth <= 0 || _blurTarget.ActualHeight <= 0) return null;
        Effect? effect = _blurTarget.Effect;
        CacheMode? cacheMode = _blurTarget.CacheMode;
        try {
            _blurTarget.Effect = _originalEffect;
            _blurTarget.CacheMode = _originalCacheMode;
            DpiScale dpi = VisualTreeHelper.GetDpi(_blurTarget);
            var image = new RenderTargetBitmap((int)Math.Ceiling(_blurTarget.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(_blurTarget.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            // Render at the target's own origin even when it sits inside a larger layout (the body inside the shell).
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
            using (DrawingContext context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(_blurTarget) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, _blurTarget.ActualWidth, _blurTarget.ActualHeight));
            image.Render(visual); image.Freeze(); return image;
        } catch (InvalidOperationException) { return null; }
        finally {
            _blurTarget.CacheMode = cacheMode;
            _blurTarget.Effect = effect;
        }
    }

    private void Apply(double value) {
        double p = Math.Clamp(value, 0, 1);
        _blur.Radius = 12 * p;
        _scrim.Opacity = .18 * p;
        _scrim.Visibility = p > .001 || _message is not null ? Visibility.Visible : Visibility.Collapsed;
        _dialog.Opacity = p;
        if (p <= .001 && _message is null) {
            _blurTarget.Effect = _originalEffect;
            _blurTarget.CacheMode = _originalCacheMode;
            GlassMaterial.SetWallpaperSource(_dialog, null);
            _dialog.ClearValue(GlassMaterial.IgnoreWallpaperHostEffectProperty);
        }
    }

    public void Dispose() {
        if (_disposed) return; _disposed = true;
        _blurTarget.SizeChanged -= OnBackgroundSizeChanged;
        if (_snapshotRefresh?.Status == DispatcherOperationStatus.Pending) _snapshotRefresh.Abort();
        _snapshotRefresh = null;
        _motion.Dispose();
        _blurTarget.Effect = _originalEffect;
        _blurTarget.CacheMode = _originalCacheMode;
        _background.IsHitTestVisible = true;
        _scrim.IsHitTestVisible = false; _scrim.Visibility = Visibility.Collapsed;
        _scrim.Opacity = 0; _dialog.IsHitTestVisible = false; _dialog.Opacity = 0; _message = null;
        GlassMaterial.SetWallpaperSource(_dialog, null);
        _dialog.ClearValue(GlassMaterial.IgnoreWallpaperHostEffectProperty);
        _dialog.ClearValue(GlassMaterial.WallpaperHostProperty);
    }
}
