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
        _button = new GlassCommandButton { Width = 42, Height = 42, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(10) };
        Bind(UIElement.VisibilityProperty, source, UIElement.VisibilityProperty);
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
        content.Children.Add(progress); _button.Content = content;
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
        var tooltip = GlassCommandChrome.ToolTip(_button, useDarkText);
        tooltip.SetBinding(ContentControl.ContentProperty, new Binding(nameof(FrameworkElement.ToolTip)) { Source = _source, Converter = TooltipText.Instance });
        _button.ToolTip = tooltip;
    }
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
        _button.Click -= OnClick;
        _button.PreviewMouseRightButtonDown -= OnRightDown;
        _button.PreviewMouseRightButtonUp -= OnRightUp;
        _button.MouseLeave -= OnLeave;
        if (_button.ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        BindingOperations.ClearAllBindings(_button); _button.Dispose();
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
