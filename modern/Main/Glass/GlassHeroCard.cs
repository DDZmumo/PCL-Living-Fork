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
/// The page's lead card: what matters most on the page, stated first and large. An accent-tinted icon disc, a large
/// title, a status line under it, the page's main actions on the right and an optional footer row. The card carries a
/// faint accent wash so it reads as the focus of the page without leaving the glass material.
/// </summary>
public sealed class GlassHeroCard : ContentControl
{
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>An existing (named) text element for the title, when code rewrites it.</summary>
    public TextBlock? TitleContent { get; set; }
    /// <summary>Status under the title: text or any element (chips, a summary line).</summary>
    public object? Status { get; set; }
    /// <summary>The main actions, right-aligned beside the title.</summary>
    public UIElement? Actions { get; set; }
    /// <summary>A row under the header across the full card (details, secondary actions).</summary>
    public UIElement? Footer { get; set; }

    private readonly Border _wash = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(16) };

    public GlassHeroCard()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(0, 0, 0, 22);
    }

    private bool _built;

    protected override void OnInitialized(EventArgs args)
    {
        base.OnInitialized(args);
        Built();
    }

    /// <summary>Builds the card now (code-created cards); XAML cards build when initialized. Building happens once.</summary>
    internal GlassHeroCard Built()
    {
        if (_built) return this;
        _built = true;
        Content = Build();
        return this;
    }

    private UIElement Build()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon disc: an accent tint with the glyph in the accent colour.
        var disc = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Ellipse { Opacity = .18 };
        fill.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
        var ring = new Ellipse { StrokeThickness = 1, Opacity = .35 };
        ring.SetResourceReference(Shape.StrokeProperty, "GlassPresentationAccent");
        disc.Children.Add(fill);
        disc.Children.Add(ring);
        if (Icon.Length > 0)
        {
            var glyph = new Path { Data = Geometry.Parse(Icon), Width = 24, Height = 24, Stretch = Stretch.Uniform };
            glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            disc.Children.Add(glyph);
        }
        header.Children.Add(disc);

        var title = TitleContent ?? new TextBlock { Text = Title };
        title.FontSize = 20;
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness();
        title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { title } };
        if (Status is string text)
        {
            var line = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            labels.Children.Add(line);
        }
        else if (Status is UIElement element)
        {
            if (element is FrameworkElement framed && framed.Margin == default) framed.Margin = new Thickness(0, 6, 0, 0);
            labels.Children.Add(element);
        }
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);
        if (Actions is FrameworkElement actions)
        {
            actions.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);
        }

        var body = new StackPanel { Children = { header } };
        if (Footer is FrameworkElement footer)
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 14), IsHitTestVisible = false };
            rule.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
            body.Children.Add(rule);
            body.Children.Add(footer);
        }

        var layers = new Grid();
        layers.Children.Add(_wash);
        layers.Children.Add(new Border { Padding = new Thickness(22, 20, 22, 20), Child = body });
        var card = new GlassSurface { Child = layers, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(.7),
            BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        Loaded += (_, _) => PaintWash();
        AutomationProperties.SetName(this, title.Text);
        return card;
    }

    // A faint diagonal wash of the accent colour from the icon corner, fading out across the card.
    private void PaintWash()
    {
        if (TryFindResource("GlassPresentationAccent") is not SolidColorBrush { Color: var accent }) return;
        var wash = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        wash.GradientStops.Add(new GradientStop(Color.FromArgb(38, accent.R, accent.G, accent.B), 0));
        wash.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), .6));
        wash.Freeze();
        _wash.Background = wash;
    }

    /// <summary>A small rounded status chip; <paramref name="accent"/> tints it with the accent colour.</summary>
    internal static Border Chip(string text, bool accent = false)
    {
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "GlassPresentationText" : "GlassPresentationSecondary");
        var chip = new Border { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, accent ? "GlassPresentationSelected" : "GlassPresentationField");
        return chip;
    }
}

/// <summary>Square-ish tiles: a quick action (icon over a label) or a statistic (large number over a label).</summary>
internal static class GlassTiles
{
    /// <summary>Turns an existing page button into an action tile, keeping its name, handlers and visibility.</summary>
    internal static void Action(Button button, string icon, string title)
    {
        var glyph = new Path { Data = Geometry.Parse(icon), Width = 20, Height = 20, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        var label = new TextBlock { Text = title, FontSize = 13, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        button.Style = GlassDialogControls.Style("GlassPresentationButton");
        button.Height = 78;
        button.MinWidth = 0;
        button.Margin = new Thickness(0, 0, 10, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        Frost(button, new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { glyph, label } },
            new Thickness(10, 8, 10, 8));
        AutomationProperties.SetName(button, title);
    }

    /// <summary>
    /// Puts a tile's content on its own frosted layer. Tiles that sit straight on the wallpaper need the blur beneath
    /// them like every card does; the frost covers the template's hover layer, so hover lightens the frost instead.
    /// </summary>
    internal static void Frost(Button button, UIElement content, Thickness padding)
    {
        var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), BlurRadius = 24, TintOpacity = 1,
            HighlightOpacity = 0, IsHitTestVisible = false };
        frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        button.Content = null;
        button.Content = new Grid { Children = { frost, new Border { Padding = padding, Child = content } } };
        button.Padding = new Thickness();
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Stretch;
        button.MouseEnter += (_, _) => frost.Opacity = .82;
        button.MouseLeave += (_, _) => frost.Opacity = 1;
        // A tile is sized by its content, not squeezed into a 34 px command by the page layout rules.
        GlassPageLayout.PreserveTile(button);
    }

    /// <summary>A statistic tile that opens the matching page; set the number through the returned text.</summary>
    internal static (Button Tile, TextBlock Value) Stat(string icon, string title, System.Action open)
    {
        var value = new TextBlock { Text = "—", FontSize = 22, FontWeight = FontWeights.SemiBold };
        value.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var glyph = new Path { Data = Geometry.Parse(icon), Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
        var caption = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var content = new StackPanel { Children = { value, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { glyph, caption } } } };
        var tile = new Button { Content = content, Style = GlassDialogControls.Style("GlassPresentationButton"), Height = 72, MinWidth = 0,
            Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = "打开" + title };
        AutomationProperties.SetName(tile, title);
        tile.Click += (_, _) => open();
        return (tile, value);
    }
}

/// <summary>
/// One step of a multi-step workflow: a numbered disc on a connector line at the left, a frosted card with the step's
/// title, explanation and controls at the right. <see cref="IsPrimary"/> marks the step the user acts on now.
/// </summary>
public sealed class GlassStepCard : ContentControl
{
    public int Number { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsPrimary { get; set; }
    /// <summary>Hides the connector below the disc (the final step).</summary>
    public bool IsLast { get; set; }

    public GlassStepCard()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnInitialized(EventArgs args)
    {
        base.OnInitialized(args);
        var body = Content as UIElement;
        Content = null;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        // Connector from this disc down to the next step.
        var line = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, -2),
            Opacity = .35, Visibility = IsLast ? Visibility.Collapsed : Visibility.Visible };
        line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSecondary");
        layout.Children.Add(line);
        var disc = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var fill = new Ellipse();
        fill.SetResourceReference(Shape.FillProperty, IsPrimary ? "GlassPresentationAccent" : "GlassPresentationFill");
        var ring = new Ellipse { StrokeThickness = 1 };
        ring.SetResourceReference(Shape.StrokeProperty, IsPrimary ? "GlassPresentationAccentStroke" : "GlassPresentationEdge");
        var number = new TextBlock { Text = Number.ToString(), FontSize = 14, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(TextBlock.ForegroundProperty, IsPrimary ? "GlassPresentationOnAccent" : "GlassPresentationText");
        disc.Children.Add(fill);
        disc.Children.Add(ring);
        disc.Children.Add(number);
        layout.Children.Add(disc);

        var title = new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var stack = new StackPanel { Children = { title } };
        if (Description.Length > 0)
        {
            var note = new TextBlock { Text = Description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            stack.Children.Add(note);
        }
        if (body is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(element);
        }
        var card = new GlassSurface { Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 16, 20, 18),
            BorderThickness = new Thickness(IsPrimary ? 1 : .7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, Margin = new Thickness(0, 0, 0, 18) };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        card.SetResourceReference(Border.BorderBrushProperty, IsPrimary ? "GlassPresentationFocusEdge" : "GlassPresentationEdge");
        Grid.SetColumn(card, 1);
        layout.Children.Add(card);
        Content = layout;
        AutomationProperties.SetName(this, $"第 {Number} 步：{Title}");
    }
}
