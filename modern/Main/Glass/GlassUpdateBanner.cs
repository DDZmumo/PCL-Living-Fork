// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// A call-to-action strip for the one thing on a resource page that needs attention: available updates. It names the
/// count, offers a primary action and a way to review first, and stays hidden while there is nothing to do.
/// </summary>
internal sealed class GlassUpdateBanner : ContentControl
{
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = new() { FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Border _wash = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14) };

    internal GlassUpdateBanner(Action updateAll, Action review)
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 0, 0, 12);

        var badge = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var disc = new Ellipse();
        disc.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
        var arrow = new Path { Data = Geometry.Parse(ModBase.Logo.IconButtonArrowUp), Width = 18, Height = 18, Stretch = Stretch.Uniform };
        arrow.SetResourceReference(Shape.FillProperty, "GlassPresentationOnAccent");
        badge.Children.Add(disc);
        badge.Children.Add(arrow);

        _title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };

        var reviewButton = new Button { Content = "查看", Style = GlassDialogControls.Style("GlassPresentationButton"), Margin = new Thickness(0, 0, 8, 0) };
        reviewButton.Click += (_, _) => review();
        var updateButton = new Button { Content = "全部更新", Style = GlassDialogControls.Style("GlassPresentationAccentButton") };
        updateButton.Click += (_, _) => updateAll();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { reviewButton, updateButton } };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labels, 1);
        Grid.SetColumn(actions, 2);
        row.Children.Add(badge);
        row.Children.Add(labels);
        row.Children.Add(actions);

        var surface = new GlassSurface { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0,
            Child = new Grid { Children = { _wash, new Border { Padding = new Thickness(16, 12, 14, 12), Child = row } } } };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationSelected");
        Content = surface;
        Loaded += (_, _) => PaintWash();
        AutomationProperties.SetName(this, "可用更新");
    }

    internal void Show(int count, string subject)
    {
        Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 0) return;
        _title.Text = $"{count} 个{subject}有可用更新";
        _detail.Text = "可以先查看有哪些更新，再决定是否全部更新。";
    }

    private void PaintWash()
    {
        if (TryFindResource("GlassPresentationAccent") is not SolidColorBrush { Color: var accent }) return;
        var wash = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        wash.GradientStops.Add(new GradientStop(Color.FromArgb(46, accent.R, accent.G, accent.B), 0));
        wash.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), .75));
        wash.Freeze();
        _wash.Background = wash;
    }
}
