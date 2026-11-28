// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PCLLF.Wpf.Glass;

namespace PCL;

internal sealed class GlassHomepageViewport : StackPanel
{
    private readonly Dictionary<DependencyObject, Image[]> _fitted = new();

    internal GlassHomepageViewport()
    {
        var card = new Style(typeof(GlassSurface));
        card.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlassPresentationFill")));
        card.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("GlassPresentationEdge")));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(.7)));
        card.Setters.Add(new Setter(GlassSurface.BlurRadiusProperty, 24d));
        Resources[typeof(GlassSurface)] = card;
        SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationText");
        SetResourceReference(TextElement.FontFamilyProperty, "GlassPresentationFont");
    }

    // Legacy homepage markup names PCL's palette. Keep those names local to the outlet so authored
    // templates use the same live brushes as the surrounding UI. A fragment's own resources still win.
    internal void ApplyPalette(ResourceDictionary palette)
    {
        SolidColorBrush Brush(string role) => (SolidColorBrush)palette["GlassPresentation" + role];
        void Alias(string name, SolidColorBrush brush) {
            Resources["ColorBrush" + name] = brush;
            Resources["ColorObject" + name] = ThemeTransition.PaletteTarget(brush)!.Value;
        }
        Alias("1", Brush("Text"));
        Alias("2", Brush("Accent"));
        Alias("3", Brush("Accent"));
        Alias("4", Brush("Secondary"));
        // Authors also use tone 5 as an opaque banner with explicitly black text.
        var accent = ThemeTransition.PaletteTarget(Brush("Accent"))!.Value;
        var paper = ThemeTransition.PaletteTarget(Brush("OnAccent"))!.Value;
        var (light, dark) = accent.R + accent.G + accent.B >= paper.R + paper.G + paper.B ? (accent, paper) : (paper, accent);
        var midtone = new SolidColorBrush(Color.FromRgb((byte)((2 * light.R + dark.R) / 3),
            (byte)((2 * light.G + dark.G) / 3), (byte)((2 * light.B + dark.B) / 3)));
        midtone.Freeze();
        Alias("5", SystemParameters.HighContrast ? Brush("Fill") : midtone);
        Alias("6", Brush("Pressed"));
        Alias("7", Brush("OnAccent"));
        Alias("8", Brush("Popup"));
        Alias("Bg0", Brush("Selected"));
        Alias("Bg1", Brush("Hover"));
        Alias("Gray1", Brush("Text"));
        Alias("Gray2", Brush("Secondary"));
        Alias("Gray3", Brush("Secondary"));
        Alias("Gray4", Brush("Secondary"));
        Alias("Gray5", Brush("Track"));
        Alias("Gray6", Brush("Edge"));
        Alias("Gray7", Brush("Selected"));
        Alias("Gray8", Brush("Field"));
        Alias("TextSecondary", Brush("Secondary"));
        Alias("OnAccent", Brush("OnAccent"));
        Alias("Card", Brush("Fill"));
        Alias("Dialog", Brush("DialogFill"));
        Alias("Popup", Brush("Popup"));
        Alias("SemiTransparent", Brushes.Transparent);
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is not null && _fitted.Remove(visualRemoved, out Image[] previous))
            foreach (var image in previous) image.InvalidateProperty(Image.StretchProperty);
        if (visualAdded is null) return;
        var fitted = Artwork(visualAdded).Where(HasFlexibleFrame).ToArray();
        if (fitted.Length == 0) return;
        _fitted.Add(visualAdded, fitted);
        foreach (var image in fitted) image.SetCurrentValue(Image.StretchProperty, Stretch.Uniform);
    }

    private static IEnumerable<Image> Artwork(DependencyObject root)
    {
        // MyImage owns its authored Stretch (including styles). Its inner bitmap is an implementation detail.
        if (root is LauncherMarkupImage) yield break;
        if (root is Image image) { yield return image; yield break; }
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Artwork(child)) yield return descendant;
    }

    // An auto-sized border with only an upper bound can shrink to fit the whole image.
    // Fixed frames and explicit clips continue to use their authored stretch mode.
    private static bool HasFlexibleFrame(Image image) =>
        image.Stretch == Stretch.UniformToFill && !DependencyPropertyHelper.GetValueSource(image, Image.StretchProperty).IsExpression &&
        image.Clip is null && !image.ClipToBounds && double.IsNaN(image.Width) && double.IsNaN(image.Height) &&
        image.Parent is Border { Clip: null, ClipToBounds: false } frame && double.IsNaN(frame.Width) && double.IsNaN(frame.Height) &&
        (double.IsFinite(frame.MaxHeight) || double.IsFinite(frame.MaxWidth));

}
