// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly Border? _videoOverlay;
    private readonly ImageBrush? _videoBrush;
    private readonly Image? _crashSnapshot;
    private double _bodyOpacity;
    private readonly FrameworkElement _dialog;
    private readonly BlurEffect _blur = new() { Radius = 0, RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
    // A light frost: enough to separate the dialog from the page without smearing the whole interface.
    private readonly BitmapCache _blurCache = new() { EnableClearType = false, RenderAtScale = 1 };
    private const double BlurPeak = 6;
    private const double VideoFrostOpacity = .55;
    // Dialog pop: grows from slightly smaller and lower with a soft iOS-like spring; leaves the same way back.
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly SpringMotion _motion;
    private readonly Effect? _originalEffect;
    private readonly CacheMode? _originalCacheMode;
    private Image? _videoWallpaper;
    private ImageSource? _videoFrost;
    private object? _message;
    private bool _disposed;
    private bool _warning;
    private bool _resizing;
    private bool _backdropActive;
    private bool _capturedBackgroundInput;
    private bool _backgroundWasEnabled, _backgroundWasHitTestVisible;
    private IInputElement? _returnFocus;
    private static readonly Color NeutralScrim = Color.FromRgb(0, 0, 0), WarningScrim = Color.FromRgb(120, 8, 10);
    private readonly SolidColorBrush _scrimBrush = new(NeutralScrim);
    private DispatcherOperation? _snapshotRefresh;
    internal double Progress => _motion.Value;
    /// <summary>Called with the veil's progress (0–1) on every step, for surfaces outside the body that go under it too.</summary>
    internal Action<double, double>? Companion { get; set; }
    internal bool HasActiveMessage => _message is not null;

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, Border scrim, FrameworkElement dialog)
        : this(owner, background, background, scrim, dialog) { }

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, FrameworkElement blurTarget, Border scrim, FrameworkElement dialog,
        Border? videoOverlay = null, Image? crashSnapshot = null) {
        _owner = owner; _background = background; _blurTarget = blurTarget; _scrim = scrim; _dialog = dialog;
        _videoOverlay = videoOverlay;
        _crashSnapshot = crashSnapshot;
        _videoBrush = videoOverlay?.Background as ImageBrush;
        if (_videoBrush is not null) {
            _videoBrush.Stretch = Stretch.Fill;
            _videoBrush.ViewportUnits = BrushMappingMode.Absolute;
            _videoOverlay!.SizeChanged += OnVideoSizeChanged;
        }
        _originalEffect = blurTarget.Effect;
        _originalCacheMode = blurTarget.CacheMode;
        _motion = new SpringMotion(owner, 0, Apply, new SpringParameters(.42, .82)); // iOS-style spring: quick, one soft overshoot
        _scrim.Background = _scrimBrush;
        _dialog.Focusable = true;
        FocusManager.SetIsFocusScope(_dialog, true);
        KeyboardNavigation.SetTabNavigation(_dialog, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(_dialog, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(_dialog, KeyboardNavigationMode.Contained);
        blurTarget.SizeChanged += OnBackgroundSizeChanged;
    }

    internal static bool IsCrashReport(object? message) => message is GlassMessageDialog { Title: "Minecraft 出现错误" };

    internal void SetMessage(object? message) {
        if (_disposed || ReferenceEquals(_message, message)) return;
        _message = message;
        if (message is not null) {
            bool warning = GlassModalState.IsWarning(message);
            if (_motion.Value <= .001) _scrimBrush.Color = warning ? WarningScrim : NeutralScrim;
            else if (warning != _warning)
                _scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(warning ? WarningScrim : NeutralScrim, TimeSpan.FromMilliseconds(260)) {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                }, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            if (_motion.Value <= .001) _scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _warning = warning;
            _scrim.Visibility = Visibility.Visible;
            _backdropActive = true;
            UpdateBackdrop();
        }
        // Input ownership changes immediately, independently of the fade/spring endpoint.
        _dialog.IsHitTestVisible = message is not null;
        _scrim.IsHitTestVisible = message is not null;
        if (message is not null) CaptureInput(message);
        else RestoreInput();
        _motion.Retarget(message is null ? 0 : 1);
    }

    internal void SetVideoBackdrop(Image? wallpaper, ImageSource? frost) {
        if (_disposed || ReferenceEquals(_videoWallpaper, wallpaper) && ReferenceEquals(_videoFrost, frost)) return;
        if (!ReferenceEquals(_videoWallpaper, wallpaper)) {
            if (_videoWallpaper is not null) _videoWallpaper.SizeChanged -= OnVideoSizeChanged;
            _videoWallpaper = wallpaper;
            if (wallpaper is not null) wallpaper.SizeChanged += OnVideoSizeChanged;
        }
        _videoFrost = frost;
        if (_backdropActive)
        {
            UpdateBackdrop();
            UpdateCompanion(Math.Clamp(_motion.Value, 0, 1));
        }
    }

    private void UpdateBackdrop() {
        GlassMaterial.SetIgnoreWallpaperHostEffect(_dialog, true);
        GlassMaterial.SetDesktopBackdropEnabled(_dialog, false);
        if (_videoWallpaper is { } wallpaper) {
            // Use the player's existing blurred texture in both places. A live blur on the body would
            // invalidate the cache of the entire workspace on every video frame.
            RestoreBodyEffect();
            ReleaseCrashSnapshot();
            GlassMaterial.SetWallpaperHost(_dialog, wallpaper);
            GlassMaterial.SetWallpaperSource(_dialog, _videoFrost);
        } else {
            UpdateStaticBackdrop();
        }
        if (_videoBrush is not null) _videoBrush.ImageSource = _videoFrost;
        UpdateVideoOverlay(Math.Clamp(_motion.Value, 0, 1));
        AlignVideoOverlay();
    }

    private void UpdateStaticBackdrop() {
        ImageSource? snapshot = Snapshot();
        GlassMaterial.SetWallpaperStretch(_dialog, Stretch.Fill);
        if (IsCrashReport(_message) && _crashSnapshot is not null && snapshot is not null) {
            // The workspace can continue to change behind a long report. Blur a single still instead
            // of invalidating the entire live body whenever its controls animate or update.
            RestoreBodyEffect();
            if (_crashSnapshot.Source is null) _bodyOpacity = _blurTarget.Opacity;
            _crashSnapshot.Source = snapshot;
            _crashSnapshot.Effect = _blur;
            _crashSnapshot.Visibility = Visibility.Visible;
            _blurTarget.SetCurrentValue(UIElement.OpacityProperty, 0d);
            GlassMaterial.SetWallpaperHost(_dialog, _crashSnapshot);
        } else {
            ReleaseCrashSnapshot();
            GlassMaterial.SetWallpaperHost(_dialog, _blurTarget);
            _blurTarget.CacheMode ??= _blurCache;
            _blurTarget.Effect = _blur;
        }
        GlassMaterial.SetWallpaperSource(_dialog, snapshot);
    }

    private void ReleaseCrashSnapshot() {
        if (_crashSnapshot?.Source is null) return;
        _blurTarget.SetCurrentValue(UIElement.OpacityProperty, _bodyOpacity);
        _crashSnapshot.Visibility = Visibility.Collapsed;
        _crashSnapshot.Source = null;
        _crashSnapshot.Effect = null;
    }

    private void UpdateVideoOverlay(double progress) {
        if (_videoOverlay is null) return;
        double opacity = _videoWallpaper is not null && _videoBrush?.ImageSource is not null && !SystemParameters.HighContrast
            ? VideoFrostOpacity * progress : 0;
        _videoOverlay.Opacity = opacity;
        Visibility visibility = opacity > .001 ? Visibility.Visible : Visibility.Collapsed;
        if (_videoOverlay.Visibility == visibility) return;
        _videoOverlay.Visibility = visibility;
        if (visibility == Visibility.Visible) AlignVideoOverlay();
    }

    private void OnVideoSizeChanged(object sender, SizeChangedEventArgs args) => AlignVideoOverlay();

    private void AlignVideoOverlay() {
        if (_videoOverlay is not { IsVisible: true } || _videoBrush?.ImageSource is null ||
            _videoWallpaper is not { IsVisible: true } wallpaper || wallpaper.RenderSize.Width <= 0 || wallpaper.RenderSize.Height <= 0) return;
        // Match Image.OnRender's arranged rectangle, including the part clipped by the viewport.
        // An independently centred ImageBrush crops differently on mismatched aspect ratios.
        Rect bounds = wallpaper.TransformToVisual(_videoOverlay).TransformBounds(new Rect(wallpaper.RenderSize));
        if (bounds != _videoBrush.Viewport) _videoBrush.Viewport = bounds;
    }

    private void RestoreBodyEffect() {
        _blurTarget.Effect = _originalEffect;
        _blurTarget.CacheMode = _originalCacheMode;
    }

    private void ClearBackdrop() {
        _backdropActive = false;
        RestoreBodyEffect();
        ReleaseCrashSnapshot();
        GlassMaterial.SetWallpaperSource(_dialog, null);
        _dialog.ClearValue(GlassMaterial.IgnoreWallpaperHostEffectProperty);
        _dialog.ClearValue(GlassMaterial.WallpaperHostProperty);
        if (_videoBrush is not null) _videoBrush.ImageSource = null;
        UpdateVideoOverlay(0);
    }

    private void CaptureInput(object message)
    {
        if (!_capturedBackgroundInput)
        {
            _capturedBackgroundInput = true;
            _backgroundWasEnabled = _background.IsEnabled;
            _backgroundWasHitTestVisible = _background.IsHitTestVisible;
            _returnFocus = _background.IsKeyboardFocusWithin ? Keyboard.FocusedElement : null;
            _background.SetCurrentValue(UIElement.IsEnabledProperty, false);
            _background.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        }
        _ = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || !ReferenceEquals(_message, message) || !_dialog.IsVisible ||
                Window.GetWindow(_dialog) is { IsActive: false }) return;
            if (!_dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) _dialog.Focus();
        }));
    }

    private void RestoreInput()
    {
        if (!_capturedBackgroundInput) return;
        _capturedBackgroundInput = false;
        _background.SetCurrentValue(UIElement.IsEnabledProperty, _backgroundWasEnabled);
        _background.SetCurrentValue(UIElement.IsHitTestVisibleProperty, _backgroundWasHitTestVisible);
        IInputElement? previous = _returnFocus;
        _returnFocus = null;
        if (Window.GetWindow(_dialog) is { IsActive: false }) return;
        if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true }) Keyboard.Focus(previous);
        else if (_dialog.IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }

    internal void SetWindowResizing(bool resizing)
    {
        if (_resizing == resizing) return;
        _resizing = resizing;
        if (!resizing) { RefreshSnapshot(); return; }
        if (_snapshotRefresh?.Status == DispatcherOperationStatus.Pending) _snapshotRefresh.Abort();
        _snapshotRefresh = null;
    }

    private void OnBackgroundSizeChanged(object sender, SizeChangedEventArgs args) => RefreshSnapshot();
    internal void RefreshSnapshot() {
        if (_disposed || _resizing || _message is null || _videoWallpaper is not null || _owner.Dispatcher.HasShutdownStarted || _snapshotRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _snapshotRefresh = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
            _snapshotRefresh = null;
            if (_disposed || _resizing || _message is null || _videoWallpaper is not null) return;
            UpdateStaticBackdrop();
        }));
    }

    private ImageSource? Snapshot() {
        if (_blurTarget.ActualWidth <= 0 || _blurTarget.ActualHeight <= 0) return null;
        Effect? effect = _blurTarget.Effect;
        CacheMode? cacheMode = _blurTarget.CacheMode;
        bool bodyHidden = _crashSnapshot?.Source is not null;
        try {
            if (bodyHidden) _blurTarget.SetCurrentValue(UIElement.OpacityProperty, _bodyOpacity);
            _blurTarget.Effect = _originalEffect;
            _blurTarget.CacheMode = _originalCacheMode;
            DpiScale dpi = VisualTreeHelper.GetDpi(_blurTarget);
            var image = new RenderTargetBitmap((int)Math.Ceiling(_blurTarget.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(_blurTarget.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
            var bounds = new Rect(_blurTarget.RenderSize);
            // An absolute VisualBrush viewbox uses the visual's parent coordinates. The output bitmap starts at
            // (0, 0) even when layout centres the source or a descendant paints outside the body's bounds.
            Rect viewbox = VisualTreeHelper.GetParent(_blurTarget) is Visual parent
                ? _blurTarget.TransformToVisual(parent).TransformBounds(bounds) : bounds;
            using (DrawingContext context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(_blurTarget) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox }, null, bounds);
            image.Render(visual); image.Freeze(); return image;
        } catch (InvalidOperationException) { return null; }
        finally {
            if (bodyHidden) _blurTarget.SetCurrentValue(UIElement.OpacityProperty, 0d);
            _blurTarget.CacheMode = cacheMode;
            _blurTarget.Effect = effect;
        }
    }

    private void Apply(double value) {
        double p = Math.Clamp(value, 0, 1);
        _blur.Radius = BlurPeak * p;
        UpdateCompanion(p);
        // The raw (overshooting) value drives the pop so the dialog settles with a slight bounce.
        double v = Math.Max(0, value);
        if (p >= .999 && Math.Abs(value - 1) < .002) {
            if (_dialog.RenderTransform is not null) _dialog.RenderTransform = null; // crisp text at rest
        } else {
            if (_dialog.RenderTransform is null) {
                _dialog.RenderTransformOrigin = new Point(.5, .5);
                _dialog.RenderTransform = new TransformGroup { Children = { _pop, _lift } };
            }
            double scale = .9 + .1 * v;
            _pop.ScaleX = _pop.ScaleY = scale;
            _lift.Y = 22 * (1 - Math.Min(v, 1.05));
        }
        _scrim.Opacity = (_warning ? .30 : .18) * p;
        _scrim.Visibility = p > .001 || _message is not null ? Visibility.Visible : Visibility.Collapsed;
        _dialog.Opacity = p;
        if (p <= .001 && _message is null && _backdropActive) ClearBackdrop();
        else UpdateVideoOverlay(p);
    }

    private void UpdateCompanion(double progress) => Companion?.Invoke(progress, _videoWallpaper is null ? _blur.Radius : 0);

    public void Dispose() {
        if (_disposed) return; _disposed = true;
        _blurTarget.SizeChanged -= OnBackgroundSizeChanged;
        if (_videoOverlay is not null) _videoOverlay.SizeChanged -= OnVideoSizeChanged;
        if (_videoWallpaper is not null) _videoWallpaper.SizeChanged -= OnVideoSizeChanged;
        if (_snapshotRefresh?.Status == DispatcherOperationStatus.Pending) _snapshotRefresh.Abort();
        _snapshotRefresh = null;
        _motion.Dispose();
        ClearBackdrop();
        RestoreInput();
        _scrim.IsHitTestVisible = false; _scrim.Visibility = Visibility.Collapsed;
        _scrim.Opacity = 0; _dialog.IsHitTestVisible = false; _dialog.Opacity = 0; _message = null;
    }
}
