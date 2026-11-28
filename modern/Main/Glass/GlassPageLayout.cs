// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>
/// Applies the layout rules the glass shell used for the launcher's pages before they were mounted directly:
/// settings pages read as a left column of at most 800 px with section titles above compact cards, editors keep a
/// fixed 220 px column, commands are compact 34 px buttons toned by what they do, and collapsible cards keep the
/// short 40 px header row. The rules are idempotent, so pages can be normalized again after building more content.
/// </summary>
internal static class GlassPageLayout
{
    internal enum Mode { None, Cards, Settings }
    internal enum CardRole { Content, SettingsSection, Expander, Toolbar, Java }

    // Measured in DIPs. These are the distinct surfaces used by ProjectCard, Toolbar and JavaPlate.
    internal static void SetCardFrame(GlassSurface surface, CardRole role)
    {
        (string padding, string radius) = role switch
        {
            CardRole.SettingsSection => ("GlassCardSettingsPadding", "GlassCardSettingsRadius"),
            CardRole.Expander => ("GlassCardExpanderPadding", "GlassPresentationCardRadius"),
            CardRole.Toolbar => ("GlassCardToolbarPadding", "GlassCardToolbarRadius"),
            CardRole.Java => ("GlassCardJavaPadding", "GlassCardJavaRadius"),
            _ => ("GlassCardContentPadding", "GlassPresentationCardRadius")
        };
        var resources = GlassPageResources.GetSharedResources();
        surface.Padding = (Thickness)resources[padding];
        surface.CornerRadius = (CornerRadius)resources[radius];
    }

    internal static double CardGap(CardRole role, Mode mode = Mode.Cards) => role switch
    {
        CardRole.Java => 14,
        CardRole.Toolbar or CardRole.SettingsSection => 18,
        _ => mode == Mode.Settings ? 18 : 16
    };

    internal static void SizeToolbarAction(Button button)
    {
        button.Margin = new Thickness(0, 0, 8, 8);
        button.MinWidth = 0;
        button.Padding = new Thickness(16, 0, 16, 0);
        button.Height = button.MinHeight = 34;
        button.FontSize = 13.5;
        button.SetValue(DoneProperty, true);
    }

    internal static Grid SearchField()
    {
        var grid = new Grid { Height = 32, MinHeight = 32 };
        PreserveRowSize(grid);
        return grid;
    }

    internal static void PreserveRowSize(Grid grid) => grid.SetValue(DoneProperty, true);
    /// <summary>Keeps a tile-like button out of the compact command sizing (34 px height, command padding).</summary>
    internal static void PreserveTile(Button button) => button.SetValue(DoneProperty, true);

    private const double ColumnWidth = 800;
    private const double EditorWidth = 220;
    private const double RowHeight = 38;

    /// <summary>Opts an element out of the fixed settings editor width.</summary>
    internal static readonly DependencyProperty KeepWidthProperty = DependencyProperty.RegisterAttached(
        "KeepWidth", typeof(bool), typeof(GlassPageLayout), new PropertyMetadata(false));
    internal static void SetKeepWidth(DependencyObject element, bool value) => element.SetValue(KeepWidthProperty, value);
    private static bool KeepsWidth(DependencyObject element) => (bool)element.GetValue(KeepWidthProperty);

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "LayoutAttached", typeof(bool), typeof(GlassPageLayout), new PropertyMetadata(false));
    private static readonly DependencyProperty DoneProperty = DependencyProperty.RegisterAttached(
        "LayoutDone", typeof(bool), typeof(GlassPageLayout), new PropertyMetadata(false));

    // Actions that overwrite, repair or reset existing files: worth a second look, but not destructive.
    private static readonly string[] CautionWords = ["补全", "修复", "重置", "覆盖", "清理", "清除", "重新", "替换", "还原", "更新"];

    internal static Mode ModeOf(object page) => page.GetType().Name switch {
        "PageInstanceMmcComponents" => Mode.Cards,
        "GlassLaunchOptionsPage" or "GlassLocalResourcesPage" or "GlassPersonalizationPage" => Mode.Settings,
        string name when name.StartsWith("PageSetup", StringComparison.Ordinal) => Mode.Settings,
        string name when name.StartsWith("PageInstance", StringComparison.Ordinal) => Mode.Settings,
        "PageDownloadInstall" or "PageDownloadModify" or "PageOtherTest" or "GlassHelpPage" or "GlassHelpDetailPage" or
            "GlassAboutPage" or "GlassResourceBrowserPage" or "GlassResourceDetailPage" => Mode.Cards,
        _ => Mode.None
    };

    /// <summary>Normalizes a page now (when loaded) and whenever it is loaded again.</summary>
    internal static void Attach(FrameworkElement page)
    {
        Mode mode = ModeOf(page);
        if (mode == Mode.None) return;
        if (!(bool)page.GetValue(AttachedProperty))
        {
            page.SetValue(AttachedProperty, true);
            page.Loaded += (_, _) => Schedule(page, mode);
        }
        if (page.IsLoaded) Schedule(page, mode);
        else Apply(page, mode);
    }

    private static void Schedule(FrameworkElement page, Mode mode)
    {
        Apply(page, mode);
        // Pages fill some sections after their first load (async readers); pick those up once they exist.
        page.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(page, mode)));
    }

    internal static void Apply(FrameworkElement page, Mode mode)
    {
        if (mode == Mode.Settings) ConstrainColumn(page);
        Walk(page, mode, 0);
    }

    // ---- Page column --------------------------------------------------------------------------------------------

    private static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "LayoutColumn", typeof(bool), typeof(GlassPageLayout), new PropertyMetadata(false));

    private static void ConstrainColumn(FrameworkElement page)
    {
        if (FindColumn(page, 0) is not { } column || (bool)column.GetValue(ColumnProperty)) return;
        Thickness margin = column.Margin;
        column.Margin = new Thickness(0, Math.Min(12, margin.Top), 0, Math.Min(16, margin.Bottom));
        AlignSettingsColumn(column, Ancestor<ScrollViewer>(column, page) ?? page);
    }

    internal static void AlignSettingsColumn(FrameworkElement column, FrameworkElement host)
    {
        column.SetValue(ColumnProperty, true);
        column.HorizontalAlignment = HorizontalAlignment.Left;
        column.MaxWidth = ColumnWidth;
        // Keep the 12 DIP scrollbar lane when an Auto scrollbar is collapsed.
        void Fit() => column.Width = host.ActualWidth > 12 ? Math.Min(ColumnWidth, host.ActualWidth - 12) : double.NaN;
        host.SizeChanged += (_, args) => { if (args.WidthChanged) Fit(); };
        Fit();
    }

    // The page body is the first panel that keeps the classic 25 px page gutter on both sides.
    private static FrameworkElement? FindColumn(DependencyObject parent, int depth)
    {
        if (depth > 5) return null;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not FrameworkElement element) continue;
            if (element is Panel and not GlassSelectionDock && element.Margin.Left >= 20 && element.Margin.Right >= 20) return element;
            if (element is StackPanel { Orientation: Orientation.Vertical } && parent is ScrollViewer) return element;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is FrameworkElement element && element is not (ItemsControl or GlassSelectionDock) && FindColumn(element, depth + 1) is { } found) return found;
        return null;
    }

    private static T? Ancestor<T>(DependencyObject element, DependencyObject stop) where T : DependencyObject
    {
        for (DependencyObject? item = LogicalTreeHelper.GetParent(element); item is not null && !ReferenceEquals(item, stop); item = LogicalTreeHelper.GetParent(item))
            if (item is T match) return match;
        return null;
    }

    // ---- Tree walk ----------------------------------------------------------------------------------------------

    private static void Walk(DependencyObject element, Mode mode, int depth)
    {
        if (depth > 40 || element is GlassSelectionDock) return;
        switch (element)
        {
            case GlassSurface surface: Card(surface, mode); break;
            case Grid grid when mode == Mode.Settings: Rows(grid); break;
            case Button button: Command(button); break;
            case ComboBox combo: Choice(combo, mode); break;
            case TextBox text: Field(text); break;
            case TextBlock { Name: "LabEmpty" } empty when mode == Mode.Settings: EmptyState(empty); break;
            case Border { Child: TextBlock } notice when notice is not GlassSurface && IsNotice(notice): GlassDialogControls.Callout(notice); break;
            case CheckBox check when mode == Mode.Settings && check.Style == Shared("GlassPresentationCheckBox"):
                check.Style = Shared("GlassPresentationSettingsCheckBox");
                break;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(element).Cast<object>().ToArray())
            if (child is DependencyObject next) Walk(next, mode, depth + 1);
    }

    private static Style? Shared(string key) => GlassPageResources.GetSharedResources()[key] as Style;

    // ---- Cards --------------------------------------------------------------------------------------------------

    private static void Card(GlassSurface surface, Mode mode)
    {
        if ((bool)surface.GetValue(DoneProperty)) return;
        if (surface.Child is Expander { } expander && expander.Style == Shared("GlassPresentationExpander"))
        {
            surface.SetValue(DoneProperty, true);
            // A collapsed section is a single 40 px header row, like a Fluent expander.
            SetCardFrame(surface, CardRole.Expander);
            if (surface.Margin.Bottom > 0) surface.Margin = new Thickness(surface.Margin.Left, surface.Margin.Top, surface.Margin.Right, CardGap(CardRole.Expander, mode));
            if (expander.Header is string title) expander.Header = Heading(title, 15);
            else if (expander.Header is TextBlock heading) { heading.FontSize = 15; heading.FontWeight = FontWeights.SemiBold; }
            return;
        }
        if (mode != Mode.Settings || surface.Child is not StackPanel body || body.Children.Count < 2) return;
        // Only the page's own top-level sections; nested cards keep their titles inside.
        if (surface.Parent is not StackPanel { Orientation: Orientation.Vertical } sections || !(bool)sections.GetValue(ColumnProperty)) return;
        if (HeaderOf(body.Children[0]) is not { } header) return;
        surface.SetValue(DoneProperty, true);

        // Settings sections: the title sits above a compact card instead of inside it.
        UIElement lead = body.Children[0];
        body.Children.RemoveAt(0);
        header.FontSize = 16;
        header.FontWeight = FontWeights.SemiBold;
        if (lead is FrameworkElement leadElement) leadElement.Margin = new Thickness(0, 0, 0, 12);
        if (!ReferenceEquals(lead, header)) header.Margin = new Thickness();

        int index = sections.Children.IndexOf(surface);
        sections.Children.RemoveAt(index);
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, CardGap(CardRole.SettingsSection)),
            Children = { lead is FrameworkElement leadTitle ? GlassSettingRows.HeadingPlate(leadTitle) : lead, surface } };
        // The card's own visibility (set by its page) hides the whole section, title included.
        section.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(Visibility)) { Source = surface });
        sections.Children.Insert(index, section);

        surface.Margin = new Thickness();
        SetCardFrame(surface, CardRole.SettingsSection);
        // When the body was a wrapper of one panel, unwrap it so the card edges stay flush.
        TrimCardEdge(body, top: true);
        TrimCardEdge(body, top: false);
    }

    private static TextBlock? HeaderOf(UIElement lead) => lead switch {
        TextBlock { FontWeight: var weight } text when weight.ToOpenTypeWeight() >= 600 && text.FontSize >= 15 => text,
        DockPanel dock when dock.Children.OfType<TextBlock>().FirstOrDefault(label => label.FontSize >= 15 && label.FontWeight.ToOpenTypeWeight() >= 600) is { } heading => heading,
        StackPanel { Children.Count: > 0 } stack when stack.Children[0] is TextBlock { FontWeight: var weight } text &&
            weight.ToOpenTypeWeight() >= 600 && text.FontSize >= 15 && !stack.Children.OfType<Control>().Any() => text,
        _ => null
    };

    private static bool TrimCardEdge(FrameworkElement element, bool top)
    {
        if (element.Visibility == Visibility.Collapsed) return false;
        if (element is StackPanel stack)
        {
            IEnumerable<FrameworkElement> children = stack.Children.OfType<FrameworkElement>();
            bool vertical = stack.Orientation == Orientation.Vertical;
            bool hasContent = false;
            foreach (FrameworkElement child in vertical && !top ? children.Reverse() : children)
            {
                if (!TrimCardEdge(child, top)) continue;
                hasContent = true;
                if (vertical) break;
            }
            if (!hasContent && element.MinHeight == 0 && double.IsNaN(element.Height))
            {
                element.Margin = new Thickness();
                return false;
            }
        }
        else if (element is WrapPanel wrap && !wrap.Children.Cast<UIElement>().Any(child => child.Visibility != Visibility.Collapsed) &&
                 element.MinHeight == 0 && double.IsNaN(element.Height))
        {
            element.Margin = new Thickness();
            return false;
        }

        // Wrapped buttons each contribute half a row gap. The card supplies that space at its outer edges.
        double inset = 0;
        if (element is WrapPanel { Orientation: Orientation.Horizontal } actions && actions.Children.Count > 0 && actions.Children.Cast<UIElement>().All(child => child is Button))
        {
            inset = -4;
            foreach (Button button in actions.Children) {
                var gap = button.Margin;
                button.Margin = new Thickness(gap.Left, 4, gap.Right, 4);
            }
        }
        Thickness margin = element.Margin;
        element.Margin = new Thickness(margin.Left, top ? inset : margin.Top, margin.Right, top ? margin.Bottom : inset);
        return true;
    }

    private static TextBlock Heading(string text, double size)
    {
        var heading = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "GlassPresentationTextStyle");
        heading.FontSize = size;
        heading.FontWeight = FontWeights.SemiBold;
        heading.TextWrapping = TextWrapping.NoWrap;
        return heading;
    }

    internal static FrameworkElement TitledSettingsCard(string title, UIElement content)
    {
        var header = Heading(title, 16);
        header.Margin = new Thickness(0, 0, 0, 12);
        var inset = new Border { Padding = new Thickness(0, 4, 0, 0), Child = content };
        var surface = GlassPageResources.Card(inset, CardRole.SettingsSection);
        surface.Margin = new Thickness();
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, CardGap(CardRole.SettingsSection)), Children = { header, surface } };
        AutomationProperties.SetName(group, title);
        return group;
    }

    // ---- Settings rows ------------------------------------------------------------------------------------------

    private static bool Interactive(DependencyObject element, int depth) => element switch {
        ButtonBase or TextBoxBase or Selector or RangeBase or PasswordBox => true,
        _ when depth <= 0 => false,
        _ => LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().Any(child => Interactive(child, depth - 1))
    };

    internal static void SizeRowToEditor(Grid grid) => Rows(grid, sizeToEditor: true);

    private static void Rows(Grid grid, bool sizeToEditor = false)
    {
        if ((bool)grid.GetValue(DoneProperty)) return;
        grid.SetValue(DoneProperty, true);
        var cells = grid.Children.OfType<FrameworkElement>().ToArray();
        for (int index = 0; index < grid.RowDefinitions.Count; index++)
        {
            RowDefinition row = grid.RowDefinitions[index];
            bool controls = cells.Any(cell => Grid.GetRow(cell) == index && Interactive(cell, 3));
            if (!controls) continue;
            if (row.Height.IsAbsolute && row.Height.Value < RowHeight) row.Height = GridLength.Auto;
            row.MinHeight = Math.Max(RowHeight, row.MinHeight);
        }
        if (!sizeToEditor && grid.RowDefinitions.Count <= 1 && grid.Children.Count > 0 && cells.Any(cell => Interactive(cell, 3)) &&
            !cells.All(cell => cell is Button))
            grid.MinHeight = Math.Max(grid.MinHeight, RowHeight);

        // A label and one editor: the editor keeps a fixed column on the right, the label takes the rest.
        if (grid.ColumnDefinitions.Count != 2 || cells.Length != 2) return;
        if (cells.FirstOrDefault(cell => Grid.GetColumn(cell) == 0) is not TextBlock label) return;
        if (cells.FirstOrDefault(cell => Grid.GetColumn(cell) == 1) is not { } editor || KeepsWidth(editor)) return;
        if (Editor(editor) is not { } input) return;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[0].SharedSizeGroup = null;
        grid.ColumnDefinitions[1].Width = GridLength.Auto;
        label.FontSize = 14;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 16, 0);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        editor.Width = EditorWidth;
    }

    private static Control? Editor(FrameworkElement element) => element switch {
        ComboBox or Slider => (Control)element,
        TextBox { AcceptsReturn: false } text => text,
        Grid { Children.Count: 2 } host when host.Children[0] is TextBox { AcceptsReturn: false } text && host.Children[1] is TextBlock => text,
        _ => null
    };

    // A message in a small rounded field-coloured border (or a named hint) reads as an information callout.
    // Single-line labels (chips, badges) are never notices.
    private static bool IsNotice(Border border) =>
        border.Child is TextBlock { TextWrapping: not TextWrapping.NoWrap } &&
        border.CornerRadius.TopLeft is >= 7 and <= 9 && (border.Name.StartsWith("Hint", StringComparison.Ordinal) ||
            border.Background is not null && ReferenceEquals(border.Background, border.TryFindResource("GlassPresentationField")));

    // Empty lists: a centred "?" glyph above the message, shown and hidden together with it.
    private static void EmptyState(TextBlock message)
    {
        if ((bool)message.GetValue(DoneProperty) || message.Parent is not Panel owner) return;
        message.SetValue(DoneProperty, true);
        var glyph = new GlassStatusGlyph(GlassStatusGlyph.Kind.Empty, 52) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, message.Margin.Top, 0, 0) };
        glyph.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(Visibility)) { Source = message });
        owner.Children.Insert(owner.Children.IndexOf(message), glyph);
        message.HorizontalAlignment = HorizontalAlignment.Stretch;
        message.MaxWidth = 420;
        message.TextAlignment = TextAlignment.Center;
        message.Margin = new Thickness(0, 12, 0, 0);
    }

    // ---- Controls -----------------------------------------------------------------------------------------------

    private static void Command(Button button)
    {
        bool accent = button.Style == Shared("GlassPresentationAccentButton");
        if (!accent && button.Style != Shared("GlassPresentationButton")) return;
        if ((bool)button.GetValue(DoneProperty)) return;
        button.SetValue(DoneProperty, true);
        string label = button.Content as string ?? (button.Content as TextBlock)?.Text ?? string.Empty;
        // Compact Fluent-sized commands: the classic fixed 35-55 px heights and wide minimums made every action a slab.
        bool fixedCell = button.Parent is Grid grid && Grid.GetColumn(button) < grid.ColumnDefinitions.Count &&
            grid.ColumnDefinitions[Grid.GetColumn(button)].Width.IsAbsolute;
        double height = double.IsNaN(button.Height) ? 0 : button.Height;
        if (height is 0 or (> 30 and < 46))
        {
            button.Height = 34;
            button.MinHeight = 34;
        }
        if (!fixedCell && label.Length > 0)
        {
            if (!double.IsNaN(button.Width) && button.Width >= 100) button.Width = double.NaN;
            button.MinWidth = Math.Max(button.MinWidth, 88);
        }
        if (label.Length > 0)
        {
            button.Padding = new Thickness(14, 0, 14, 0);
            button.FontSize = 13.5;
        }
        if (button.VerticalAlignment == VerticalAlignment.Stretch) button.VerticalAlignment = VerticalAlignment.Center;
        // Tone by warning level; danger and primary are already set by their pages.
        if (!accent && button.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue &&
            label.Length > 0 && CautionWords.Any(label.Contains))
            button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationCautionText");
    }

    private static void Choice(ComboBox combo, Mode mode)
    {
        if (combo.Style != Shared("GlassPresentationComboBox") || (bool)combo.GetValue(DoneProperty)) return;
        combo.SetValue(DoneProperty, true);
        if (!double.IsNaN(combo.Height) && combo.Height < 34) combo.ClearValue(FrameworkElement.HeightProperty);
        combo.MaxDropDownHeight = Math.Min(260, combo.MaxDropDownHeight);
        if (mode != Mode.Settings || KeepsWidth(combo) || combo.Parent is not (Grid or Panel)) return;
        // Settings choices are a fixed-width field at the end of their row.
        combo.Width = EditorWidth;
        combo.MinWidth = Math.Min(combo.MinWidth, EditorWidth);
        combo.MaxWidth = EditorWidth;
        combo.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private static void Field(TextBox text)
    {
        if (text.AcceptsReturn || (bool)text.GetValue(DoneProperty)) return;
        if (text.Style == Shared("GlassPresentationSearchBox")) return;
        if (text.Style != Shared("GlassPresentationTextBox")) return;
        text.SetValue(DoneProperty, true);
        if (IsSearch(text)) { Search(text); return; }
        if (double.IsNaN(text.Height) || text.Height < 36) text.Height = 36;
    }

    /// <summary>
    /// A page action floating over the wallpaper with no card beneath it (版本独立设置, 开始导出…): the button carries its
    /// own frosted layer so the translucent fill reads over a blur.
    /// </summary>
    internal static Button FrostedAction(string text, string? icon = null) => Frost(new Button(), text, icon);

    /// <summary>Restyles an existing page button (keeping its handlers) as a frosted floating action.</summary>
    internal static Button Frost(Button button, string text, string? icon = null)
    {
        if ((bool)button.GetValue(DoneProperty) && Equals(AutomationProperties.GetName(button), text) && button.Content is Grid) return button;
        button.Style = Shared("GlassPresentationButton");
        button.Padding = new Thickness();
        button.MinHeight = 38;
        button.ClearValue(FrameworkElement.HeightProperty);
        button.ClearValue(FrameworkElement.MinWidthProperty);
        button.FontWeight = FontWeights.SemiBold;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Stretch;
        button.SetValue(DoneProperty, true);
        var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
        frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Width = 16, Height = 16, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
            content.Children.Add(glyph);
        }
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        content.Children.Add(label);
        button.Content = new Grid { Children = { frost, content } };
        // The frost covers the template's hover layer, so hover lightens the frost itself.
        button.MouseEnter += (_, _) => frost.Opacity = .82;
        button.MouseLeave += (_, _) => frost.Opacity = 1;
        AutomationProperties.SetName(button, text);
        return button;
    }

    // ---- Search -------------------------------------------------------------------------------------------------

    private static bool IsSearch(TextBox text)
    {
        if (text.Name.Contains("Search", StringComparison.OrdinalIgnoreCase)) return true;
        string hint = AutomationProperties.GetName(text);
        return hint.StartsWith("搜索", StringComparison.Ordinal) || hint.StartsWith("筛选", StringComparison.Ordinal);
    }

    /// <summary>
    /// Page search fields: a frosted 32 px field with a magnifier. When the page follows it with a card of commands,
    /// both become one toolbar card, the field on top and its commands in a row underneath.
    /// </summary>
    private static void Search(TextBox text)
    {
        // The placeholder host (field + hint) is replaced as a unit.
        FrameworkElement unit = text.Parent is Grid { Children.Count: 2 } placeholderHost && placeholderHost.Children[1] is TextBlock ? placeholderHost : text;
        if (unit.Parent is not Panel owner) return;
        TextBlock? hint = unit is Grid hostGrid ? (TextBlock)hostGrid.Children[1] : null;
        Thickness margin = unit.Margin;
        int index = owner.Children.IndexOf(unit);
        owner.Children.RemoveAt(index);
        if (unit is Grid oldHost) oldHost.Children.Clear();

        text.Style = Shared("GlassPresentationSearchBox");
        text.ClearValue(FrameworkElement.HeightProperty);
        text.Margin = new Thickness();
        var field = SearchField();
        Grid.SetRow(field, Grid.GetRow(unit)); Grid.SetColumn(field, Grid.GetColumn(unit));
        Grid.SetRowSpan(field, Grid.GetRowSpan(unit)); Grid.SetColumnSpan(field, Grid.GetColumnSpan(unit));
        DockPanel.SetDock(field, DockPanel.GetDock(unit));
        var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
        frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        field.Children.Add(frost);
        field.Children.Add(text);
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        icon.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        field.Children.Add(icon);
        if (hint is not null)
        {
            hint.Margin = new Thickness(35, 0, 32, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            field.Children.Add(hint);
        }
        var clear = new Button { Content = "×", Style = Shared("GlassPresentationRowButton"), Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(),
            HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(clear, "清空搜索");
        clear.Click += (_, _) => { text.Clear(); text.Focus(); };
        text.TextChanged += (_, _) => clear.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        clear.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        field.Children.Add(clear);

        // A following card of only commands joins the field as one toolbar.
        if (owner is StackPanel { Orientation: Orientation.Vertical } && index < owner.Children.Count &&
            owner.Children[index] is GlassSurface { Child: WrapPanel actions } plate &&
            actions.Children.Count > 0 && actions.Children.OfType<UIElement>().All(child => child is Button))
        {
            plate.Child = null;
            var inner = new StackPanel { Children = { field } };
            actions.Margin = new Thickness(0, 10, 0, 0);
            foreach (Button button in actions.Children.OfType<Button>()) {
                Command(button);
                SizeToolbarAction(button);
            }
            inner.Children.Add(actions);
            plate.Child = inner;
            SetCardFrame(plate, CardRole.Toolbar);
            plate.Margin = new Thickness(0, 0, 0, CardGap(CardRole.Toolbar));
            plate.SetValue(DoneProperty, true);
            return;
        }
        field.Margin = owner.Children.Count == 0 && owner.Parent is GlassSurface ? new Thickness() :
            new Thickness(margin.Left, margin.Top, margin.Right, Math.Max(margin.Bottom, 10));
        owner.Children.Insert(index, field);
    }
}
