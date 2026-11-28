// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>
/// Settings layout: a titled group is a heading above one card; inside, each setting is a row with its title and a
/// one-line explanation on the left and its control on the right, rows separated by hairlines. Wide editors (multi-line
/// text, compound inputs) sit under the title and take the full row width.
/// </summary>
internal static class GlassSettingRows
{
    internal const double ControlWidth = 220;

    /// <summary>Adds a titled settings group to <paramref name="sections"/> and returns the panel its rows go in.</summary>
    internal static GlassSettingRowStack Group(Panel sections, string title, out TextBlock heading)
    {
        heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var rows = new GlassSettingRowStack();
        var card = Card(rows);
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 22), Children = { HeadingPlate(heading), card } };
        AutomationProperties.SetName(group, title);
        sections.Children.Add(group);
        return rows;
    }

    internal static GlassSettingRowStack Group(Panel sections, string title) => Group(sections, title, out _);

    /// <summary>
    /// A section title that sits on the wallpaper: a small frosted pill behind the text keeps it readable over busy
    /// images without turning it into another card. The heading keeps its own margin outside the pill.
    /// </summary>
    internal static FrameworkElement HeadingPlate(FrameworkElement heading)
    {
        Thickness margin = heading.Margin;
        heading.Margin = new Thickness();
        var plate = new GlassSurface { Child = heading, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4),
            BorderThickness = new Thickness(0), BlurRadius = 20, TintOpacity = 1, HighlightOpacity = 0,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Math.Max(0, margin.Left - 2), margin.Top, margin.Right, margin.Bottom - 2) };
        plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        return plate;
    }

    /// <summary>A collapsible group: the heading row opens the card's rows.</summary>
    internal static GlassSettingRowStack Expander(Panel sections, string title, Style expanderStyle)
    {
        var rows = new GlassSettingRowStack { Margin = new Thickness(0, 2, 0, 0) };
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var expander = new System.Windows.Controls.Expander { Header = heading, Content = rows, Style = expanderStyle };
        var card = new GlassSurface { Child = expander, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 8, 16, 8),
            BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, Margin = new Thickness(0, 0, 0, 22) };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        sections.Children.Add(card);
        return rows;
    }

    private static GlassSurface Card(UIElement content)
    {
        var card = new GlassSurface { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 2, 16, 2),
            BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return card;
    }

    /// <summary>Adds a setting row. A null control makes a title-only row (for content added to <see cref="GlassSettingRow.Below"/>).</summary>
    internal static GlassSettingRow Row(Panel rows, string title, string? description, FrameworkElement? control, bool wide = false)
    {
        var row = new GlassSettingRow(title, description, control, wide);
        rows.Children.Add(row);
        return row;
    }

    /// <summary>A switch for a boolean setting, in the row's control column.</summary>
    internal static CheckBox Switch(string? key)
    {
        var toggle = new CheckBox { Style = GlassDialogControls.Style("GlassPresentationSwitch"), VerticalAlignment = VerticalAlignment.Center };
        if (key is not null) SettingService.SetKey(toggle, key);
        return toggle;
    }
}

/// <summary>One setting: title and explanation on the left, the control on the right (or underneath when wide).</summary>
internal sealed class GlassSettingRow : Grid
{
    internal TextBlock Title { get; }
    internal TextBlock Description { get; }
    /// <summary>Content shown under the title across the full row (wide editors, conditional sub-options).</summary>
    internal StackPanel Below { get; } = new() { Margin = new Thickness(0, 10, 0, 0) };

    internal GlassSettingRow(string title, string? description, FrameworkElement? control, bool wide)
        : this(new TextBlock { Text = title }, description, control, wide) { }

    /// <summary>A row whose title is an existing text element (pages that rewrite the caption at runtime).</summary>
    internal GlassSettingRow(TextBlock title, string? description, FrameworkElement? control, bool wide)
    {
        MinHeight = 58;
        Margin = new Thickness(0, 11, 0, 11);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        GlassPageLayout.PreserveRowSize(this);

        Title = title;
        title.FontSize = 14;
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness();
        title.VerticalAlignment = VerticalAlignment.Top;
        Title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        Description = new TextBlock { Text = description ?? "", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible };
        Description.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0), Children = { Title, Description } };
        Children.Add(labels);

        Grid.SetRow(Below, 1);
        Grid.SetColumnSpan(Below, 2);
        Below.Visibility = Visibility.Collapsed;
        Children.Add(Below);

        if (control is null) return;
        AutomationProperties.SetName(control, title.Text);
        if (wide)
        {
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.Width = double.NaN;
            AddBelow(control);
            return;
        }
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        if (control is ComboBox or TextBox or Slider || control is Grid { Children: [TextBox, ..] })
            control.Width = GlassSettingRows.ControlWidth;
        GlassPageLayout.SetKeepWidth(control, true);
        Grid.SetColumn(control, 1);
        Children.Add(control);
    }

    internal void AddBelow(UIElement content)
    {
        Below.Children.Add(content);
        Below.Visibility = Visibility.Visible;
    }
}

/// <summary>A column of setting rows that draws a hairline between neighbouring visible rows.</summary>
public sealed class GlassSettingRowStack : StackPanel
{
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Size size = base.ArrangeOverride(arrangeSize);
        InvalidateVisual();
        return size;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (TryFindResource("GlassPresentationSeparator") is not Brush brush) return;
        bool first = true;
        foreach (UIElement child in Children.OfType<UIElement>())
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (first) { first = false; continue; }
            double top = VisualTreeHelper.GetOffset(child).Y - (child is FrameworkElement element ? element.Margin.Top : 0);
            context.DrawRectangle(brush, null, new Rect(0, Math.Round(top), ActualWidth, 1));
        }
    }
}

/// <summary>XAML form of a settings group: a heading over one card of setting rows (its children).</summary>
public sealed class GlassSettingGroup : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(GlassSettingGroup),
        new PropertyMetadata("", (target, args) => { if (((GlassSettingGroup)target)._heading is { } heading) heading.Text = (string)args.NewValue; }));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Folds the rows under a heading row inside the card instead of a heading above it.</summary>
    public bool IsCollapsible { get; set; }
    /// <summary>Destructive actions: the card and heading take the danger tint so they stand apart from ordinary settings.</summary>
    public bool IsDanger { get; set; }
    private TextBlock? _heading;

    protected override void OnInitialized(EventArgs args)
    {
        base.OnInitialized(args);
        var rows = new GlassSettingRowStack();
        foreach (UIElement child in Children.OfType<UIElement>().ToArray())
        {
            Children.Remove(child);
            rows.Children.Add(child);
        }
        _heading = new TextBlock { Text = Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var card = new GlassSurface { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        card.SetResourceReference(Border.BorderBrushProperty, IsDanger ? "GlassPresentationDangerEdge" : "GlassPresentationEdge");
        if (IsDanger) _heading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDangerText");
        if (IsCollapsible)
        {
            rows.Margin = new Thickness(0, 2, 0, 0);
            var expander = new System.Windows.Controls.Expander { Header = _heading, Content = rows };
            expander.SetResourceReference(StyleProperty, "GlassPresentationExpander");
            card.Child = expander;
            card.Padding = new Thickness(16, 8, 16, 8);
            Children.Add(card);
        }
        else
        {
            _heading.Margin = new Thickness(2, 0, 0, 10);
            if (IsDanger)
            {
                // A faint danger wash under the rows, on top of the card's frosted fill.
                var wash = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(12), Margin = new Thickness(-16, -2, -16, -2) };
                wash.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDangerFill");
                card.Child = new Grid { Children = { wash, rows } };
            }
            else card.Child = rows;
            card.Padding = new Thickness(16, 2, 16, 2);
            Children.Add(GlassSettingRows.HeadingPlate(_heading));
            Children.Add(card);
        }
        if (ReadLocalValue(MarginProperty) == DependencyProperty.UnsetValue) Margin = new Thickness(0, 0, 0, 22);
        AutomationProperties.SetName(this, Title);
    }
}

/// <summary>
/// XAML form of one setting row: <see cref="Title"/> and <see cref="Description"/> on the left, the content (a control)
/// on the right, or under the title when <see cref="IsWide"/>. <see cref="Extra"/> goes under the title as well.
/// The row hides when its control is collapsed.
/// </summary>
public sealed class GlassSettingItem : ContentControl
{
    public string Title { get; set; } = "";
    /// <summary>Use an existing (named) text element as the title, e.g. when code rewrites the caption.</summary>
    public TextBlock? TitleContent { get; set; }
    public string Description { get; set; } = "";
    public bool IsWide { get; set; }
    public UIElement? Extra { get; set; }

    public GlassSettingItem()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnInitialized(EventArgs args)
    {
        base.OnInitialized(args);
        var control = Content as FrameworkElement;
        Content = null;
        string? description = Description.Length == 0 ? null : Description;
        var row = TitleContent is { } title ? new GlassSettingRow(title, description, control, IsWide) : new GlassSettingRow(Title, description, control, IsWide);
        if (Extra is { } extra) row.AddBelow(extra);
        Content = row;
        if (control is not null && control.ReadLocalValue(VisibilityProperty) is not BindingExpressionBase)
            SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = control });
    }
}
