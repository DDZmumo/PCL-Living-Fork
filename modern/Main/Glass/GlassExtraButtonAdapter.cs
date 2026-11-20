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
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>A new standard Button view over the original extra-button state and logical actions.</summary>
internal sealed class GlassExtraButtonAdapter : IDisposable {
    private readonly MyExtraButton _source;
    private readonly GlassCommandButton _button;
    private bool _rightHeld, _disposed;
    private bool? _darkText;
    private bool? _highContrast;
    internal Button View => _button;
    internal GlassExtraButtonAdapter(MyExtraButton source, bool useDarkText, bool reduceMotion = false) {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        // Round floating action (iOS-style): the progress is a ring around the edge, not a line underneath.
        _button = new GlassCommandButton { Width = 46, Height = 46, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(13), ShapeRadius = 23,
            RenderTransform = _presenceScale, RenderTransformOrigin = new Point(.5, .5) };
        _presence = new SpringMotion(_button, source.Visibility == Visibility.Visible ? 1 : 0, ApplyPresence, new SpringParameters(.42, .7));
        _visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
        _visibility.AddValueChanged(source, OnSourceVisibilityChanged);
        _button.Visibility = source.Visibility;
        Bind(UIElement.IsEnabledProperty, source, UIElement.IsEnabledProperty);
        Bind(UIElement.IsHitTestVisibleProperty, source, UIElement.IsHitTestVisibleProperty);
        _button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(FrameworkElement.ToolTip)) { Source = source, Converter = TooltipText.Instance });
        _button.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(FrameworkElement.ToolTip)) { Source = source, Converter = TooltipText.Instance });
        var content = new Grid();
        var icon = new Path { Width = 19, Height = 19, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .5), IsHitTestVisible = false };
        icon.SetBinding(Path.DataProperty, new Binding(nameof(Path.Data)) { Source = source.Path });
        icon.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        icon.SetBinding(UIElement.RenderTransformProperty, new Binding(nameof(UIElement.RenderTransform)) { Source = source.Path });
        content.Children.Add(icon);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 2, Margin = new Thickness(-3, 0, -3, -6), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, BorderThickness = new Thickness() };
        progress.SetBinding(RangeBaseValue(), new Binding(nameof(RectangleGeometry.Rect)) { Source = source.RectProgress, Converter = ProgressValue.Instance });
        progress.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = source.PanProgress });
        progress.SetBinding(Control.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        var track = new FrameworkElementFactory(typeof(Grid), "PART_Track");
        track.SetValue(Panel.BackgroundProperty, GlassCommandChrome.Brush("#14FFFFFF"));
        var indicator = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
        indicator.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.AppendChild(indicator);
        progress.Template = new ControlTemplate(typeof(ProgressBar)) { VisualTree = track };
        // The original's progress line stays (it carries the real value and availability) but draws as a ring.
        progress.Opacity = 0;
        content.Children.Add(progress);
        var ringTrack = new Ellipse { Margin = new Thickness(-16), StrokeThickness = 2.4, IsHitTestVisible = false };
        ringTrack.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _button, Converter = Faint.Instance });
        ringTrack.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = progress });
        _ring = new Path { Margin = new Thickness(-16), StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Stretch = Stretch.None };
        _ring.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        _ring.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = progress });
        progress.ValueChanged += (_, args) => { if (TotalProgress is null) DrawRing(args.NewValue); };
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
        // Frosted base: the floating button sits over the page/wallpaper, so it blurs what is beneath it (a round glass
        // disc under the icon) instead of painting an opaque slab. The chrome above it stays a light translucent tint.
        _frost = new PCLLF.Wpf.Glass.GlassSurface { Margin = new Thickness(-13), CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(0),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
        content.Children.Insert(0, _frost);
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
        RefreshAppearance(useDarkText, reduceMotion);
    }
    // Kept as a method to make the source binding explicit; the source Progress CLR property
    // is not observable, while its actual RectangleGeometry.Rect is a dependency property.
    private static DependencyProperty RangeBaseValue() => System.Windows.Controls.Primitives.RangeBase.ValueProperty;
    private void Bind(DependencyProperty target, DependencyObject source, DependencyProperty property) =>
        _button.SetBinding(target, new Binding { Source = source, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
    internal void RefreshAppearance(bool useDarkText, bool reduceMotion = false) {
        if (_disposed) return;
        _button.ReduceMotion = reduceMotion;
        Style style = GlassCommandChrome.ButtonStyle(useDarkText);
        if (SystemParameters.HighContrast) {
            // Style.Resources becomes read-only after application. Element resources stay
            // mutable and take precedence over the cached style's initial native palette.
            GlassCommandChrome.RefreshContrastResources(_button.Resources);
            if (_button.ToolTip is ToolTip currentTooltip) GlassCommandChrome.RefreshContrastResources(currentTooltip.Resources);
        }
        if (_darkText == useDarkText && _highContrast == SystemParameters.HighContrast) return;
        _darkText = useDarkText;
        _highContrast = SystemParameters.HighContrast;
        _button.Style = style;
        // Translucent tint over the frost (the shared style's fill is opaque); hover brightens the tint.
        // High contrast keeps the system window/text colours from the shared style, without any glass.
        bool contrast = SystemParameters.HighContrast;
        _frost.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
        Color tint = useDarkText ? Color.FromArgb(150, 242, 244, 248) : Color.FromArgb(120, 28, 32, 40);
        _frost.Background = new SolidColorBrush(tint);
        // SetCurrentValue (not a local value) so any later style, such as a contrast style, still governs the fill.
        if (!contrast) _button.SetCurrentValue(Control.BackgroundProperty, Brushes.Transparent);
        _button.MouseEnter -= OnHoverEnter; _button.MouseLeave -= OnHoverLeave;
        _button.MouseEnter += OnHoverEnter; _button.MouseLeave += OnHoverLeave;
        var tooltip = GlassCommandChrome.ToolTip(_button, useDarkText);
        tooltip.SetBinding(ContentControl.ContentProperty, new Binding(nameof(FrameworkElement.ToolTip)) { Source = _source, Converter = TooltipText.Instance });
        _button.ToolTip = tooltip;
    }
    private readonly ScaleTransform _presenceScale = new(1, 1);
    private readonly SpringMotion _presence;
    private readonly System.ComponentModel.DependencyPropertyDescriptor _visibility;
    private readonly Path _ring;
    private readonly Ellipse[] _waves = new Ellipse[2];
    private Ellipse _ringTrack = null!;
    private Ellipse _ringHead = null!;
    private Canvas _ringHeadLayer = null!;
    private SpringMotion _ringMotion = null!;
    private double _ringValue = -1;

    /// <summary>
    /// Optional overall progress (0-1) for this action, or null when there is none. When set (the task button), the ring
    /// shows it instead of the original button's own progress line, and hides when no task is running.
    /// </summary>
    internal Func<double?>? TotalProgress { get; set; }

    internal void RefreshProgress()
    {
        if (_disposed || TotalProgress is null) return;
        double? value = TotalProgress();
        Visibility visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        BindingOperations.ClearBinding(_ring, UIElement.VisibilityProperty);
        BindingOperations.ClearBinding(_ringTrack, UIElement.VisibilityProperty);
        _ring.Visibility = _ringTrack.Visibility = visibility;
        if (visibility != Visibility.Visible) SetRingHeadActive(false);
        double ring = value ?? 0;
        if (Math.Abs(ring - _ringValue) < .002) return;
        bool completed = _ringValue < .999 && ring >= .999;
        bool reset = ring < _ringValue - .2; // a new task batch: restart from its own value instead of rewinding slowly
        _ringValue = ring;
        if (reset || _button.ReduceMotion || !MotionPreferences.AllowsAnimation) _ringMotion.Snap(ring);
        else _ringMotion.Retarget(ring);
        if (completed && !_button.ReduceMotion && MotionPreferences.AllowsAnimation) Ripple();
    }
    private readonly PCLLF.Wpf.Glass.GlassSurface _frost;

    // Appearing pops in from 55 % on a bouncy spring and sends out a ripple; leaving (for example when the task page
    // opens) shrinks and fades before collapsing. Reduced motion switches immediately.
    private void OnSourceVisibilityChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool show = _source.Visibility == Visibility.Visible;
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
        else if (snap) { _presence.Snap(0); _button.Visibility = _source.Visibility; }
        else _presence.Retarget(0);
    }

    private void ApplyPresence(double value)
    {
        _presenceScale.ScaleX = _presenceScale.ScaleY = .55 + .45 * value;
        _button.Opacity = Math.Clamp(value, 0, 1);
        if (_source.Visibility != Visibility.Visible && value <= MotionTokens.VisibilityThreshold) _button.Visibility = _source.Visibility;
    }

    private void Ripple()
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
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure }); geometry.Freeze();
        _ring.Data = geometry;
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
        bool shouldBreathe = active && !_disposed && !_button.ReduceMotion && MotionPreferences.AllowsAnimation;
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
    private void OnClick(object sender, RoutedEventArgs args) { if (!_disposed) _source.PerformClick(); }
    private void OnRightDown(object sender, MouseButtonEventArgs args) {
        _rightHeld = !_disposed && _source.IsEnabled && _source.CanRightClick;
        if (_rightHeld) { _button.Focus(); args.Handled = true; }
    }
    private void OnRightUp(object sender, MouseButtonEventArgs args) {
        bool invoke = _rightHeld; _rightHeld = false;
        if (invoke && !_disposed) { _source.PerformClick(true); args.Handled = true; }
    }
    private void OnLeave(object sender, MouseEventArgs args) => _rightHeld = false;
    public void Dispose() {
        if (_disposed) return; _disposed = true;
        _visibility.RemoveValueChanged(_source, OnSourceVisibilityChanged);
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
        if (_button.ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        BindingOperations.ClearAllBindings(_button); _button.Dispose();
    }
    private sealed class Faint : IValueConverter {
        internal static readonly Faint Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is SolidColorBrush brush ? new SolidColorBrush(Color.FromArgb(48, brush.Color.R, brush.Color.G, brush.Color.B)) : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private sealed class ProgressValue : IValueConverter {
        internal static readonly ProgressValue Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Rect rect ? Math.Clamp(rect.Height / 40, 0, 1) : 0d;
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
