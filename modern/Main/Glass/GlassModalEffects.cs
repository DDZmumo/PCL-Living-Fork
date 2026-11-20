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
    private readonly FrameworkElement _dialog;
    private readonly BlurEffect _blur = new() { Radius = 0, RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
    // A light frost: enough to separate the dialog from the page without smearing the whole interface.
    private readonly BitmapCache _blurCache = new() { EnableClearType = false, RenderAtScale = 1 };
    private const double BlurPeak = 6;
    // Dialog pop: grows from slightly smaller and lower with a soft iOS-like spring; leaves the same way back.
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly SpringMotion _motion;
    private readonly Effect? _originalEffect;
    private readonly CacheMode? _originalCacheMode;
    private object? _message;
    private bool _disposed;
    private bool _warning;
    private bool _resizing;
    private bool _capturedBackgroundInput;
    private bool _backgroundWasEnabled, _backgroundWasHitTestVisible;
    private IInputElement? _returnFocus;
    private static readonly Color NeutralScrim = Color.FromRgb(0, 0, 0), WarningScrim = Color.FromRgb(120, 8, 10);
    private readonly SolidColorBrush _scrimBrush = new(NeutralScrim);
    private DispatcherOperation? _snapshotRefresh;
    internal double Progress => _motion.Value;
    internal bool HasActiveMessage => _message is not null;

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, Border scrim, FrameworkElement dialog)
        : this(owner, background, background, scrim, dialog) { }

    internal GlassModalEffects(FrameworkElement owner, FrameworkElement background, FrameworkElement blurTarget, Border scrim, FrameworkElement dialog) {
        _owner = owner; _background = background; _blurTarget = blurTarget; _scrim = scrim; _dialog = dialog;
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
        _dialog.IsHitTestVisible = message is not null;
        _scrim.IsHitTestVisible = message is not null;
        if (message is not null) CaptureInput(message);
        else RestoreInput();
        _motion.Retarget(message is null ? 0 : 1);
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
        if (_disposed || _resizing || _message is null || _owner.Dispatcher.HasShutdownStarted || _snapshotRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _snapshotRefresh = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
            _snapshotRefresh = null;
            if (_disposed || _resizing || _message is null) return;
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
        _blur.Radius = BlurPeak * p;
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
        RestoreInput();
        _scrim.IsHitTestVisible = false; _scrim.Visibility = Visibility.Collapsed;
        _scrim.Opacity = 0; _dialog.IsHitTestVisible = false; _dialog.Opacity = 0; _message = null;
        GlassMaterial.SetWallpaperSource(_dialog, null);
        _dialog.ClearValue(GlassMaterial.IgnoreWallpaperHostEffectProperty);
        _dialog.ClearValue(GlassMaterial.WallpaperHostProperty);
    }
}
