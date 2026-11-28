// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PCLCS;

/// <summary>How serious a dependency finding is; drives the colour of bars, badges and chips.</summary>
public enum ModDependencyTone { Neutral, Accent, Caution, Danger }

/// <summary>
/// Shared look of the mod dependency views (graph, launch check, removal impact): glass surfaces and text tones,
/// a status lead at the top, and cards with a tone bar so the severity of each item reads before its text.
/// All colours are resource references so the views follow the glass palette and theme changes.
/// </summary>
public static class ModDependencyVisuals {
    public static string TextKey(ModDependencyTone tone) => tone switch {
        ModDependencyTone.Danger => "GlassPresentationDangerText",
        ModDependencyTone.Caution => "GlassPresentationCautionText",
        ModDependencyTone.Accent => "GlassPresentationText",
        _ => "GlassPresentationSecondary"
    };

    public static string FillKey(ModDependencyTone tone) => tone switch {
        ModDependencyTone.Danger => "GlassPresentationDangerFill",
        ModDependencyTone.Caution => "GlassPresentationCautionFill",
        ModDependencyTone.Accent => "GlassPresentationSelected",
        _ => "GlassPresentationField"
    };

    public static string BarKey(ModDependencyTone tone) => tone switch {
        ModDependencyTone.Danger => "GlassPresentationDangerText",
        ModDependencyTone.Caution => "GlassPresentationCautionText",
        ModDependencyTone.Accent => "GlassPresentationAccent",
        _ => "GlassPresentationSecondary"
    };

    public static TextBlock Text(string? text, double size = 13, bool strong = false, string key = "GlassPresentationText") {
        TextBlock label = new() { Text = text ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, key);
        return label;
    }

    /// <summary>A rounded tag; the text must not repeat a name shown elsewhere in the view.</summary>
    public static Border Chip(string text, ModDependencyTone tone) {
        TextBlock label = new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, tone == ModDependencyTone.Neutral ? "GlassPresentationSecondary" : TextKey(tone));
        Border chip = new() { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6) };
        chip.SetResourceReference(Border.BackgroundProperty, FillKey(tone));
        return chip;
    }

    /// <summary>A round badge with a one-character glyph (✓ ? ! ✕), used for node status and the lead.</summary>
    public static Grid Badge(string glyph, ModDependencyTone tone, double size) {
        Ellipse disc = new();
        disc.SetResourceReference(Shape.FillProperty, FillKey(tone));
        Ellipse ring = new() { StrokeThickness = 1 };
        ring.SetResourceReference(Shape.StrokeProperty, BarKey(tone));
        TextBlock mark = new() { Text = glyph, FontSize = size * .55, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, size * .04) };
        mark.SetResourceReference(TextBlock.ForegroundProperty, BarKey(tone));
        return new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Children = { disc, ring, mark } };
    }

    /// <summary>
    /// The view's lead: a badge, the headline and the counts as chips. <paramref name="title"/> is the single sentence
    /// that states what the dialog is asking or showing.
    /// </summary>
    public static Border Lead(string glyph, ModDependencyTone tone, string title, IEnumerable<Border> chips, string? note = null) {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid badge = Badge(glyph, tone, 36);
        badge.Margin = new Thickness(0, 0, 12, 0);
        badge.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(badge);
        StackPanel labels = new() { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 14, true));
        WrapPanel tags = new() { Margin = new Thickness(0, 7, 0, -6) };
        foreach (Border chip in chips) tags.Children.Add(chip);
        if (tags.Children.Count > 0) labels.Children.Add(tags);
        if (!string.IsNullOrEmpty(note)) {
            TextBlock hint = Text(note, 12, false, "GlassPresentationSecondary");
            hint.Margin = new Thickness(0, 6, 0, 0);
            labels.Children.Add(hint);
        }
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        Border lead = new() { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(.7),
            Margin = new Thickness(3, 0, 7, 12) };
        lead.SetResourceReference(Border.BackgroundProperty, FillKey(tone == ModDependencyTone.Neutral ? ModDependencyTone.Neutral : tone));
        lead.SetResourceReference(Border.BorderBrushProperty, tone == ModDependencyTone.Danger ? "GlassPresentationDangerEdge" :
            tone == ModDependencyTone.Caution ? "GlassPresentationCautionEdge" : "GlassPresentationEdge");
        return lead;
    }

    /// <summary>
    /// An item card: a tone bar on the left, the icon, the name in bold and the reason underneath in the secondary
    /// colour. The card keeps a visible hairline border so its outline reads on any background.
    /// </summary>
    public static Border Card(ImageSource? icon, string? name, string? reason, ModDependencyTone tone, object? tag = null, object? toolTip = null) {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Border bar = new() { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2, 0, 2) };
        bar.SetResourceReference(Border.BackgroundProperty, BarKey(tone));
        content.Children.Add(bar);
        Image image = new() { Source = icon, Width = 30, Height = 30, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(11, 0, 10, 0), Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 6, 6) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Grid.SetColumn(image, 1);
        content.Children.Add(image);
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(name, 13.5, true));
        if (!string.IsNullOrEmpty(reason)) {
            TextBlock detail = Text(reason, 12, false, "GlassPresentationSecondary");
            detail.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(detail);
        }
        Grid.SetColumn(text, 2);
        content.Children.Add(text);
        Border box = new() { Child = content, BorderThickness = new Thickness(.7), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 10, 12, 10),
            Margin = new Thickness(4), MinHeight = 56, Tag = tag, ToolTip = toolTip };
        box.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        box.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationControlStroke");
        return box;
    }
}
