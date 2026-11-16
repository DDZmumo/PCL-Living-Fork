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
    internal static Style ButtonStyle(bool darkText) => SystemParameters.HighContrast ? ContrastButton : darkText ? LightButton : DarkButton;
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
    internal static void CopyMaterial(FrameworkElement source, FrameworkElement target) {
        GlassMaterial.SetWallpaperSource(target, GlassMaterial.GetWallpaperSource(source));
        GlassMaterial.SetWallpaperHost(target, GlassMaterial.GetWallpaperHost(source));
        GlassMaterial.SetWallpaperStretch(target, GlassMaterial.GetWallpaperStretch(source));
        GlassMaterial.SetOpacityMultiplier(target, GlassMaterial.GetOpacityMultiplier(source));
        GlassMaterial.SetIsEnabled(target, GlassMaterial.GetIsEnabled(source));
        GlassMaterial.SetFallbackColor(target, GlassMaterial.GetFallbackColor(source));
        GlassMaterial.SetDesktopBackdropEnabled(target, false);
    }
    internal static FrameworkElementFactory PopupRoot(FrameworkElementFactory content, bool darkText, double radius, Thickness padding) {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(WallpaperPopupContext.EnableProperty, true);
        root.SetValue(WallpaperPopupContext.CornerRadiusProperty, radius);
        root.SetValue(GlassMaterial.FallbackColorProperty, darkText ? Color.FromRgb(246, 248, 252) : Color.FromRgb(30, 37, 48));
        var surface = new FrameworkElementFactory(typeof(GlassSurface));
        surface.SetValue(GlassSurface.CornerRadiusProperty, new CornerRadius(radius));
        surface.SetValue(GlassSurface.BackgroundProperty, Brush(darkText ? "#F2F6F8FC" : "#E8202732"));
        surface.SetValue(GlassSurface.BorderBrushProperty, Brush(darkText ? "#24172330" : "#20FFFFFF"));
        surface.SetValue(GlassSurface.BorderThicknessProperty, new Thickness(.8));
        surface.SetValue(GlassSurface.PaddingProperty, padding);
        surface.SetValue(GlassSurface.HighlightOpacityProperty, 0d);
        surface.SetValue(GlassSurface.BlurRadiusProperty, 16d);
        surface.SetValue(GlassSurface.TintOpacityProperty, .72d);
        surface.AppendChild(content); root.AppendChild(surface); return root;
    }
    internal static ToolTip ToolTip(FrameworkElement owner, bool darkText) {
        var tooltip = new ToolTip {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            Background = Brushes.Transparent, Foreground = Brush(darkText ? "#F21F2B33" : "#F8FFFFFF"),
            BorderThickness = new Thickness(), Padding = new Thickness(), MaxWidth = 360,
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"), FontSize = 12
        };
        if (SystemParameters.HighContrast) {
            RefreshContrastResources(tooltip.Resources);
            tooltip.SetResourceReference(Control.ForegroundProperty, ContrastTextKey);
        }
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        tooltip.Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = PopupRoot(presenter, darkText, 12, new Thickness(12, 9, 12, 9)) };
        tooltip.Opened += (_, _) => {
            if (SystemParameters.HighContrast) RefreshContrastResources(tooltip.Resources);
            CopyMaterial(owner, tooltip);
        };
        return tooltip;
    }
    private static Style BuildButton(bool darkText, bool highContrast) {
        var style = new Style(typeof(GlassCommandButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(darkText ? "#EAF2F4F8" : "#D9232B38")));
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
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GlassCommandButton)) { VisualTree = chrome }));
        foreach (var (property, background) in new[] { (UIElement.IsMouseOverProperty, darkText ? "#FFF8FAFD" : "#F5323E50"), (Button.IsPressedProperty, darkText ? "#FFE3E9F1" : "#FF283445"), (UIElement.IsKeyboardFocusedProperty, darkText ? "#FFF3F6FB" : "#F5323E50") }) {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty, Brush(background)));
            trigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(darkText ? "#655C7FAE" : "#707EA5DA")));
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
