// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>New command-button chrome; no PCL control template or press animation is reused.</summary>
internal sealed class GlassCommandButton : Button, IDisposable {
    public static readonly DependencyProperty ShapeRadiusProperty = DependencyProperty.Register(
        nameof(ShapeRadius), typeof(double), typeof(GlassCommandButton), new PropertyMetadata(12d));
    private readonly ScaleTransform _press = new(1, 1);
    private readonly SpringMotion _pressMotion;
    private bool _disposed;
    public double ShapeRadius { get => (double)GetValue(ShapeRadiusProperty); set => SetValue(ShapeRadiusProperty, value); }
    internal bool ReduceMotion { get; set; }
    public GlassCommandButton() {
        RenderTransform = _press; RenderTransformOrigin = new Point(.5, .5);
        _pressMotion = new SpringMotion(this, 1, value => _press.ScaleX = _press.ScaleY = value, new SpringParameters(.16, 1));
        IsKeyboardFocusWithinChanged += (_, _) => UpdateMotion();
    }
    protected override void OnMouseEnter(MouseEventArgs args) { base.OnMouseEnter(args); UpdateMotion(); }
    protected override void OnMouseLeave(MouseEventArgs args) { base.OnMouseLeave(args); UpdateMotion(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args) {
        base.OnPropertyChanged(args);
        if (IsInitialized && (args.Property == IsPressedProperty || args.Property == IsEnabledProperty)) UpdateMotion();
    }
    private void UpdateMotion() {
        if (_disposed) return;
        double scale = IsPressed ? .98 : 1;
        _pressMotion.Retarget(scale, ReduceMotion);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _pressMotion.Dispose(); }
}

internal static class GlassCommandChrome {
    internal static readonly ComponentResourceKey ContrastWindowKey = new(typeof(GlassCommandChrome), "NativeContrastWindow");
    internal static readonly ComponentResourceKey ContrastTextKey = new(typeof(GlassCommandChrome), "NativeContrastText");
    internal static readonly ComponentResourceKey ContrastGrayKey = new(typeof(GlassCommandChrome), "NativeContrastGray");
    internal static readonly ComponentResourceKey ContrastHighlightKey = new(typeof(GlassCommandChrome), "NativeContrastHighlight");
    internal static readonly ComponentResourceKey ContrastHighlightTextKey = new(typeof(GlassCommandChrome), "NativeContrastHighlightText");
    private static readonly Style LightButton = BuildButton(true, false), DarkButton = BuildButton(false, false), ContrastButton = BuildButton(false, true);
    private static readonly Style LightFloatingButton = BuildButton(true, false, floating: true), DarkFloatingButton = BuildButton(false, false, floating: true);
    internal static Style ButtonStyle(bool darkText) => SystemParameters.HighContrast ? ContrastButton : darkText ? LightButton : DarkButton;
    internal static Style FloatingButtonStyle(bool darkText) => SystemParameters.HighContrast ? ContrastButton : darkText ? LightFloatingButton : DarkFloatingButton;
    internal static void RefreshContrastResources(ResourceDictionary resources) {
        // Legacy theme code shadows public SystemColors keys in Application.Resources.
        // Private keys retain live native system colors without changing that engine.
        void Put(object key, SolidColorBrush value) {
            if (resources[key] is SolidColorBrush current && current.Color == value.Color) return;
            resources[key] = value;
        }
        Put(ContrastWindowKey, SystemColors.WindowBrush);
        Put(ContrastTextKey, SystemColors.WindowTextBrush);
        Put(ContrastGrayKey, SystemColors.GrayTextBrush);
        Put(ContrastHighlightKey, SystemColors.HighlightBrush);
        Put(ContrastHighlightTextKey, SystemColors.HighlightTextBrush);
    }
    internal static Brush Brush(string value) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush;
    }
    internal static void CopyMaterial(FrameworkElement source, FrameworkElement target, bool allowDesktopBackdrop = false) {
        // A tooltip over the desktop rail uses that rail's material. Wallpaper popups sample their nearest image owner.
        bool desktop = allowDesktopBackdrop && GlassMaterial.GetWallpaperSource(source) is null && GlassMaterial.GetDesktopBackdropEnabled(source);
        if (!desktop) source = MaterialOwner(source);
        GlassMaterial.SetWallpaperSource(target, GlassMaterial.GetWallpaperSource(source));
        GlassMaterial.SetWallpaperHost(target, GlassMaterial.GetWallpaperHost(source));
        GlassMaterial.SetWallpaperStretch(target, GlassMaterial.GetWallpaperStretch(source));
        GlassMaterial.SetOpacityMultiplier(target, GlassMaterial.GetOpacityMultiplier(source));
        GlassMaterial.SetIsEnabled(target, GlassMaterial.GetIsEnabled(source));
        GlassMaterial.SetFallbackColor(target, GlassMaterial.GetFallbackColor(source));
        GlassMaterial.SetDesktopBackdropEnabled(target, desktop);
    }
    private static FrameworkElement MaterialOwner(FrameworkElement element) {
        for (DependencyObject? current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement candidate && GlassMaterial.GetWallpaperSource(candidate) is not null) return candidate;
        return element;
    }
    internal static FrameworkElementFactory PopupRoot(FrameworkElementFactory content, bool darkText, double radius, Thickness padding, bool allowDesktopBackdrop = false) {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(GlassPopupBackdrop.EnableProperty, true);
        root.SetValue(GlassPopupBackdrop.CornerRadiusProperty, radius);
        root.SetValue(GlassMaterial.FallbackColorProperty, darkText ? Color.FromRgb(246, 248, 252) : Color.FromRgb(32, 32, 32));
        var surface = new FrameworkElementFactory(typeof(GlassSurface));
        surface.SetValue(GlassSurface.CornerRadiusProperty, new CornerRadius(radius));
        // Light enough a tint that the sampled wallpaper blur reads through (a frosted pane, not a solid card).
        surface.SetValue(GlassSurface.BackgroundProperty, Brush(darkText ? "#C8F6F8FC" : "#B0202020"));
        surface.SetValue(GlassSurface.BorderBrushProperty, Brush(darkText ? "#24172330" : "#20FFFFFF"));
        surface.SetValue(GlassSurface.BorderThicknessProperty, new Thickness(.8));
        surface.SetValue(GlassSurface.PaddingProperty, padding);
        surface.SetValue(GlassSurface.HighlightOpacityProperty, 0d);
        surface.SetValue(GlassSurface.BlurRadiusProperty, allowDesktopBackdrop ? 24d : 16d);
        surface.SetValue(GlassSurface.TintOpacityProperty, allowDesktopBackdrop ? 1d : .72d);
        surface.AppendChild(content); root.AppendChild(surface); return root;
    }
    /// <summary>
    /// Implicit frosted tooltip for every plain <c>ToolTip="…"</c> in the shell and its pages (light or dark). The popup
    /// samples the launcher wallpaper through the owner's material; text ignores the application-wide TextBlock style.
    /// </summary>
    internal static Style ToolTipStyle(bool darkText) {
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(System.Windows.Controls.ToolTip.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Top));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(darkText ? "#F21F2B33" : "#F8FFFFFF")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness()));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness()));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 360d));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        style.Setters.Add(new Setter(System.Windows.Controls.ToolTip.HasDropShadowProperty, false));
        if (SystemParameters.HighContrast) {
            RefreshContrastResources(style.Resources);
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ContrastTextKey)));
        }
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToolTip)) {
            VisualTree = PopupRoot(presenter, darkText, 10, new Thickness(11, 7, 11, 8), allowDesktopBackdrop: true)
        }));
        style.Setters.Add(new EventSetter(System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler(OnToolTipOpened)));
        return style;
    }
    private static void OnToolTipOpened(object sender, RoutedEventArgs args) {
        if (sender is not ToolTip tooltip) return;
        if (SystemParameters.HighContrast) RefreshContrastResources(tooltip.Resources);
        if (tooltip.PlacementTarget is FrameworkElement owner) CopyMaterial(owner, tooltip, allowDesktopBackdrop: true);
        // String content becomes a TextBlock that would pick up the application's themed TextBlock style; keep the
        // tooltip's own foreground and wrap long descriptions.
        if (!tooltip.Resources.Contains(typeof(TextBlock))) {
            var text = new Style(typeof(TextBlock));
            text.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = tooltip }));
            text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            text.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d));
            tooltip.Resources[typeof(TextBlock)] = text;
        }
    }
    internal static ToolTip ToolTip(FrameworkElement owner, bool darkText) => new() { PlacementTarget = owner, Style = ToolTipStyle(darkText) };
    private static Style BuildButton(bool darkText, bool highContrast) => BuildButton(darkText, highContrast, floating: false);
    private static Style BuildButton(bool darkText, bool highContrast, bool floating) {
        var style = new Style(typeof(GlassCommandButton));
        bool sceneFill = floating && !darkText;
        Brush fill = floating && darkText ? Brushes.Transparent : Brush(sceneFill ? "#78202020" : darkText ? "#EAF2F4F8" : "#D9232B38");
        style.Setters.Add(new Setter(Control.BackgroundProperty, fill));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(darkText ? "#EF25333C" : "#F5FFFFFF")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(darkText ? "#24172330" : "#20FFFFFF")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(.7)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        if (highContrast) {
            RefreshContrastResources(style.Resources);
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ContrastWindowKey)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ContrastTextKey)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ContrastTextKey)));
        }
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        // The main/popup surface already owns frost. Controls add only a thin interactive
        // layer, keeping wallpaper colors and avoiding another full image blur per button.
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetBinding(Border.CornerRadiusProperty, new Binding(nameof(GlassCommandButton.ShapeRadius)) {
            RelativeSource = RelativeSource.TemplatedParent, Converter = RadiusConverter.Instance
        });
        // Dark floating actions bind the fill above their own scene sample.
        if (sceneFill) chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        else chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GlassCommandButton)) { VisualTree = chrome }));
        var fills = sceneFill
            ? new[] { (UIElement.IsMouseOverProperty, "#96525252"), (UIElement.IsKeyboardFocusedProperty, "#96525252"), (Button.IsPressedProperty, "#B81C1C1C") }
            : new[] { (UIElement.IsMouseOverProperty, darkText ? "#FFF8FAFD" : "#F5323E50"), (Button.IsPressedProperty, darkText ? "#FFE3E9F1" : "#FF283445"), (UIElement.IsKeyboardFocusedProperty, darkText ? "#FFF3F6FB" : "#F5323E50") };
        foreach (var (property, background) in fills) {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty, Brush(background)));
            trigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(sceneFill ? "#55FFFFFF" : darkText ? "#655C7FAE" : "#707EA5DA")));
            if (highContrast) {
                trigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ContrastHighlightKey)));
                trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ContrastHighlightTextKey)));
                trigger.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ContrastHighlightTextKey)));
            }
            style.Triggers.Add(trigger);
        }
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, highContrast ? 1d : .42d));
        if (highContrast) {
            disabled.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ContrastWindowKey)));
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ContrastGrayKey)));
            disabled.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ContrastGrayKey)));
        }
        style.Triggers.Add(disabled);
        return style;
    }
    private sealed class RadiusConverter : IValueConverter {
        internal static readonly RadiusConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new CornerRadius(value is double radius ? radius : 12);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
