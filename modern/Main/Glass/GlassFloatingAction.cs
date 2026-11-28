// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

internal sealed class GlassFloatingAction : IDisposable {
    internal const string BackToTopIcon = "M858.496 188.9024 173.1072 188.9024c-30.2848 0-54.8352-24.5504-54.8352-54.8352L118.272 106.6496c0-30.2848 24.5504-54.8352 54.8352-54.8352l685.3888 0c30.2848 0 54.8352 24.5504 54.8352 54.8352l0 27.4176C913.3312 164.352 888.7808 188.9024 858.496 188.9024L858.496 188.9024zM150.6048 550.8608c0 0 300.0064-240.3584 303.0272-243.328 13.9776-13.5936 31.1808-21.8624 48.8192-24.7552 1.7152-0.3072 3.4304-0.5888 5.1456-0.768 2.7392-0.3072 5.4528-0.3584 8.192-0.3328 2.7392-0.0256 5.4272 0.0256 8.1664 0.3328 1.7408 0.1792 3.4304 0.4864 5.1456 0.768 17.664 2.8928 34.8672 11.1616 48.8192 24.7552 3.0464 2.944 303.0016 243.328 303.0016 243.328 32.384 31.5136 29.6192 63.9744-2.7392 95.5136-32.3328 31.5392-75.648 2.9696-108.0064-28.544l-185.8816-147.1232 0 485.8368c0 30.3104-24.5248 54.8608-54.8352 54.8608l-27.392 0c-30.2848 0-54.8352-24.5504-54.8352-54.8608L447.232 470.7072l-185.8304 147.0976c-32.3584 31.5392-75.6992 60.1344-108.032 28.5696C121.0368 614.8352 118.272 582.3744 150.6048 550.8608L150.6048 550.8608zM150.6048 550.8608";
    private readonly GlassCommandButton _button;
    private readonly string _caption;
    private readonly Action _invoke;
    private readonly Action? _alternate;
    private readonly Path _icon;
    private bool _rightHeld, _disposed;
    private bool? _darkText;
    private bool? _highContrast;
    private Border? _backdropFrame;
    private bool _trackingBackdrop, _backdropSuspended;
    private readonly GlassSceneFrost _sceneFrost = new() { Margin = new Thickness(-13) };
    private readonly Border _sceneTint = new() { Margin = new Thickness(-13), CornerRadius = new CornerRadius(23), IsHitTestVisible = false };
    internal Button View => _button;
    internal GlassFloatingAction(string name, string caption, string icon, bool useDarkText,
        Func<bool> canShow, Action invoke, Action? alternate = null, bool reduceMotion = false) {
        _caption = caption;
        _invoke = invoke;
        _alternate = alternate;
        CanShow = canShow;
        _button = new GlassCommandButton { Width = 46, Height = 46, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(13), ShapeRadius = 23,
            Name = name, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            RenderTransform = _presenceScale, RenderTransformOrigin = new Point(.5, .5) };
        _presence = new SpringMotion(_button, 0, ApplyPresence, new SpringParameters(.42, .7));
        AutomationProperties.SetName(_button, caption);
        var content = new Grid();
        _icon = new Path { Width = 19, Height = 19, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        _icon.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        SetIcon(icon);
        content.Children.Add(_icon);
        var ringTrack = new Ellipse { Margin = new Thickness(-16), StrokeThickness = 2.4, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        ringTrack.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _button, Converter = Faint.Instance });
        _ring = new Path { Margin = new Thickness(-16), StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Stretch = Stretch.None, Visibility = Visibility.Collapsed };
        _ring.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        _ringFigure.Segments.Add(_ringArc);
        _ringGeometry.Figures.Add(_ringFigure);
        _ringTrack = ringTrack;
        _ring.SizeChanged += (_, _) => DrawRing(_ringDrawn);
        // Head: a small glowing dot riding the end of the arc, breathing gently while a task runs.
        _ringHead = new Ellipse { Width = 6, Height = 6, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = .9, Color = Colors.White } };
        _ringHead.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        _ringHeadLayer = new Canvas { Margin = new Thickness(-16), IsHitTestVisible = false };
        _ringHeadLayer.Children.Add(_ringHead);
        content.Children.Add(_ringHeadLayer);
        _ringHead.Visibility = Visibility.Collapsed;
        // The ring glides to each new value on a soft spring instead of jumping between refresh ticks.
        _ringMotion = new SpringMotion(_button, 0, DrawRing, new SpringParameters(.6, 1)) { MaxFrameSeconds = 1 / 30d };
        content.Children.Add(ringTrack); content.Children.Add(_ring);
        _frost = new GlassSurface { Margin = new Thickness(-13), CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(0),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, Background = GlassCommandChrome.Brush("#96F2F4F8"), IsHitTestVisible = false };
        content.Children.Insert(0, _frost);
        _sceneTint.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { Source = _button });
        content.Children.Insert(0, _sceneTint);
        content.Children.Insert(0, _sceneFrost);
        // Ripple: rings that spread from the button when it appears.
        for (int index = 0; index < 2; index++)
        {
            var wave = new Ellipse { Margin = new Thickness(-13), StrokeThickness = 1.6, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1) };
            wave.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
            _waves[index] = wave; content.Children.Insert(0, wave);
        }
        _button.Content = content;
        _button.Click += OnClick;
        _button.PreviewMouseRightButtonDown += OnRightDown;
        _button.PreviewMouseRightButtonUp += OnRightUp;
        _button.MouseLeave += OnLeave;
        _button.MouseEnter += OnHoverEnter;
        _button.MouseLeave += OnHoverLeave;
        _sceneTint.Loaded += OnBackdropLoaded;
        _sceneTint.Unloaded += OnBackdropUnloaded;
        _sceneTint.IsVisibleChanged += OnBackdropVisibilityChanged;
        RefreshAppearance(useDarkText, reduceMotion);
    }
    internal void SetIcon(string data)
    {
        if (Equals(_icon.Tag, data)) return;
        _icon.Data = Geometry.Parse(data);
        _icon.Tag = data;
    }
    internal void RefreshAppearance(bool useDarkText, bool reduceMotion = false) {
        if (_disposed) return;
        _button.ReduceMotion = reduceMotion;
        bool contrast = SystemParameters.HighContrast;
        if (contrast) {
            // Style.Resources becomes read-only after application. Element resources stay
            // mutable and take precedence over the cached style's initial native palette.
            GlassCommandChrome.RefreshContrastResources(_button.Resources);
            if (_button.ToolTip is ToolTip currentTooltip) GlassCommandChrome.RefreshContrastResources(currentTooltip.Resources);
        }
        if (_darkText != useDarkText || _highContrast != contrast) {
            _darkText = useDarkText;
            _highContrast = contrast;
            _button.Style = GlassCommandChrome.FloatingButtonStyle(useDarkText);
            _frost.Visibility = !contrast && useDarkText ? Visibility.Visible : Visibility.Collapsed;
            _sceneTint.Visibility = !contrast && !useDarkText ? Visibility.Visible : Visibility.Collapsed;
            if (_button.ToolTip is ToolTip previous) previous.IsOpen = false;
            var tooltip = GlassCommandChrome.ToolTip(_button, useDarkText);
            tooltip.Content = _caption;
            _button.ToolTip = tooltip;
        }
        RefreshBackdrop();
    }

    internal void SetBackdrop(Border frame) {
        _backdropFrame = frame;
        RefreshBackdrop();
    }

    internal void SetBackdropSuspended(bool suspended) {
        if (_backdropSuspended == suspended) return;
        _backdropSuspended = suspended;
        if (suspended) StopBackdrop();
    }

    private void RefreshBackdrop() {
        if (!_disposed && !_backdropSuspended && _button.IsVisible && _sceneTint.IsLoaded && _sceneTint.IsVisible &&
            _backdropFrame is { IsLoaded: true, IsVisible: true } frame && GlassMaterial.AllowsTransparency(_sceneTint) &&
            PresentationSource.FromVisual(_sceneTint) is { } source && ReferenceEquals(source, PresentationSource.FromVisual(frame))) {
            if (!_trackingBackdrop) {
                _trackingBackdrop = true;
                _button.LayoutUpdated += OnBackdropLayout;
            }
            _sceneFrost.UpdateBackdrop(_sceneTint, frame, frame, _sceneTint.CornerRadius.TopLeft);
        } else StopBackdrop();
    }
    private void StopBackdrop() {
        if (_trackingBackdrop) {
            _trackingBackdrop = false;
            _button.LayoutUpdated -= OnBackdropLayout;
        }
        _sceneFrost.ClearBackdrop();
    }
    private void OnBackdropLayout(object? sender, EventArgs args) => RefreshBackdrop();
    private void OnBackdropLoaded(object sender, RoutedEventArgs args) => RefreshBackdrop();
    private void OnBackdropUnloaded(object sender, RoutedEventArgs args) => StopBackdrop();
    private void OnBackdropVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) {
        if ((bool)args.NewValue) RefreshBackdrop();
        else StopBackdrop();
    }
    private readonly ScaleTransform _presenceScale = new(1, 1);
    private readonly SpringMotion _presence;
    private readonly Path _ring;
    private readonly PathGeometry _ringGeometry = new();
    private readonly PathFigure _ringFigure = new();
    private readonly ArcSegment _ringArc = new() { SweepDirection = SweepDirection.Clockwise, IsStroked = true };
    private readonly Ellipse[] _waves = new Ellipse[2];
    private Ellipse _ringTrack = null!;
    private Ellipse _ringHead = null!;
    private Canvas _ringHeadLayer = null!;
    private SpringMotion _ringMotion = null!;
    private double _ringValue = -1;
    private bool _animateRingHead = true;

    internal Func<double?>? TotalProgress { get; set; }
    private Func<bool> CanShow { get; }
    internal bool IsShown => _wantShown;

    internal void RefreshProgress()
    {
        if (_disposed || TotalProgress is null) return;
        SetProgress(TotalProgress());
    }

    internal void SetProgress(double? value, bool reset = false, bool animateHead = true)
    {
        if (_disposed) return;
        Visibility visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        _ring.Visibility = _ringTrack.Visibility = visibility;
        bool headChanged = _animateRingHead != animateHead;
        _animateRingHead = animateHead;
        if (visibility != Visibility.Visible || headChanged)
            SetRingHeadActive(visibility == Visibility.Visible && _ringDrawn is > 0 and < .999);
        double ring = value ?? 0;
        bool unchanged = Math.Abs(ring - _ringValue) < .002;
        bool settled = _ringMotion.Value == ring && !_ringMotion.IsRunning;
        if (unchanged && (!reset || settled)) return;
        bool completed = _ringValue < .999 && ring >= .999;
        reset |= ring < _ringValue - .2; // a new task batch: restart from its own value instead of rewinding slowly
        _ringValue = ring;
        if (reset || _button.ReduceMotion || !MotionPreferences.AllowsAnimation) _ringMotion.Snap(ring);
        else _ringMotion.Retarget(ring);
        if (completed && animateHead && !_button.ReduceMotion && MotionPreferences.AllowsAnimation) Ripple();
    }
    private readonly GlassSurface _frost;

    // Appearing pops in from 55 % on a bouncy spring and sends out a ripple; leaving (for example when the task page
    // opens) shrinks and fades before collapsing. Reduced motion switches immediately.
    internal void RefreshVisibility()
    {
        if (_disposed) return;
        bool show = CanShow();
        if (show == _wantShown) return;
        _wantShown = show;
        _button.IsHitTestVisible = show;
        bool snap = _button.ReduceMotion || !MotionPreferences.AllowsAnimation || !_button.IsLoaded;
        if (show)
        {
            bool wasHidden = _button.Visibility != Visibility.Visible;
            _button.Visibility = Visibility.Visible;
            if (snap) { _presence.Snap(1); return; }
            if (wasHidden) _presence.Snap(0);
            _presence.Retarget(1);
            if (wasHidden) Ripple();
        }
        else if (snap) { _presence.Snap(0); _button.Visibility = Visibility.Collapsed; }
        else _presence.Retarget(0);
    }

    private void ApplyPresence(double value)
    {
        _presenceScale.ScaleX = _presenceScale.ScaleY = .55 + .45 * value;
        _button.Opacity = Math.Clamp(value, 0, 1);
        if (!_wantShown && value <= MotionTokens.VisibilityThreshold) _button.Visibility = Visibility.Collapsed;
    }

    private bool _wantShown;

    internal void Ripple()
    {
        for (int index = 0; index < _waves.Length; index++)
        {
            Ellipse wave = _waves[index];
            var delay = TimeSpan.FromMilliseconds(120 + 220 * index);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var grow = new System.Windows.Media.Animation.DoubleAnimation(1, 1.9, TimeSpan.FromMilliseconds(720)) { BeginTime = delay, EasingFunction = ease };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(.55, 0, TimeSpan.FromMilliseconds(720)) { BeginTime = delay, EasingFunction = ease };
            var scale = (ScaleTransform)wave.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            wave.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    // Progress ring: clockwise from the top, drawn on the ring element's own box (it sits just outside the button edge).
    private void DrawRing(double value)
    {
        _ringDrawn = Math.Clamp(value, 0, 1);
        double size = Math.Min(_ring.ActualWidth, _ring.ActualHeight);
        if (_ringDrawn <= 0 || size <= 0 || _ring.Visibility != Visibility.Visible) { _ring.Data = null; SetRingHeadActive(false); return; }
        double radius = size / 2 - _ring.StrokeThickness / 2;
        var centre = new Point(_ring.ActualWidth / 2, _ring.ActualHeight / 2);
        double angle = Math.Min(_ringDrawn, .9999) * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
        _ringFigure.StartPoint = start;
        _ringArc.Point = end;
        _ringArc.Size = new Size(radius, radius);
        _ringArc.IsLargeArc = angle > Math.PI;
        if (_ring.Data is null) _ring.Data = _ringGeometry;
        if (_ringHead is not null)
        {
            SetRingHeadActive(_ringDrawn < .999);
            Canvas.SetLeft(_ringHead, end.X - _ringHead.Width / 2);
            Canvas.SetTop(_ringHead, end.Y - _ringHead.Height / 2);
        }
    }
    private double _ringDrawn;
    private bool _ringHeadBreathing;

    private void SetRingHeadActive(bool active)
    {
        if (_ringHead is null) return;
        _ringHead.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        bool shouldBreathe = active && _animateRingHead && !_disposed && !_button.ReduceMotion && MotionPreferences.AllowsAnimation;
        if (_ringHeadBreathing == shouldBreathe) return;
        _ringHeadBreathing = shouldBreathe;
        if (shouldBreathe)
        {
            var breathe = new System.Windows.Media.Animation.DoubleAnimation(1, .35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            _ringHead.BeginAnimation(UIElement.OpacityProperty, breathe);
        }
        else
        {
            _ringHead.BeginAnimation(UIElement.OpacityProperty, null);
            _ringHead.Opacity = 1;
        }
    }

    private void OnHoverEnter(object sender, MouseEventArgs args) => _frost.Opacity = .8;
    private void OnHoverLeave(object sender, MouseEventArgs args) => _frost.Opacity = 1;
    private void OnClick(object sender, RoutedEventArgs args) { if (!_disposed && _button.IsEnabled && _wantShown) _invoke(); }
    private void OnRightDown(object sender, MouseButtonEventArgs args) {
        _rightHeld = !_disposed && _button.IsEnabled && _alternate is not null;
        if (_rightHeld) { _button.Focus(); args.Handled = true; }
    }
    private void OnRightUp(object sender, MouseButtonEventArgs args) {
        bool invoke = _rightHeld; _rightHeld = false;
        if (invoke && !_disposed) { _alternate?.Invoke(); args.Handled = true; }
    }
    private void OnLeave(object sender, MouseEventArgs args) => _rightHeld = false;
    public void Dispose() {
        if (_disposed) return; _disposed = true;
        SetRingHeadActive(false);
        foreach (Ellipse wave in _waves)
        {
            if (wave?.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            wave?.BeginAnimation(UIElement.OpacityProperty, null);
        }
        _presence.Dispose();
        _ringMotion.Dispose();
        _button.MouseEnter -= OnHoverEnter;
        _button.MouseLeave -= OnHoverLeave;
        _button.Click -= OnClick;
        _button.PreviewMouseRightButtonDown -= OnRightDown;
        _button.PreviewMouseRightButtonUp -= OnRightUp;
        _button.MouseLeave -= OnLeave;
        _sceneTint.Loaded -= OnBackdropLoaded;
        _sceneTint.Unloaded -= OnBackdropUnloaded;
        _sceneTint.IsVisibleChanged -= OnBackdropVisibilityChanged;
        StopBackdrop();
        _backdropFrame = null;
        if (_button.ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        BindingOperations.ClearAllBindings(_button); _button.Dispose();
    }
    private sealed class Faint : IValueConverter {
        internal static readonly Faint Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is SolidColorBrush brush ? new SolidColorBrush(Color.FromArgb(48, brush.Color.R, brush.Color.G, brush.Color.B)) : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    internal sealed class TooltipText : IValueConverter {
        internal static readonly TooltipText Instance = new();
        internal static string Text(object? value) => value switch {
            null => "", string text => text, TextBlock text => text.Text, AccessText text => text.Text,
            ToolTip tooltip => Text(tooltip.Content), ContentControl content => Text(content.Content),
            _ => ""
        };
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Text(value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
