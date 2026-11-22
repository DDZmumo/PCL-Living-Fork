// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCLCS;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Foundation;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private Node CreateNode(DependencyObject source)
    {
        return source switch
        {
            PageResource resourcePage => ProjectResourcePage(resourcePage),
            PageSetupUpdate updatePage => ProjectLauncherUpdate(updatePage),
            PageSpeedRight taskPage => ProjectTaskManager(taskPage),
            MySearchBox search => ProjectSearch(search),
            FrameworkElement virtualElement when virtualElement is IControlVirtualElement virtualized => ProjectVirtual(virtualElement, virtualized),
            MyButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick),
            MyTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick),
            MyExtraTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick, readIcon: () => button.Path?.Data, frosted: true),
            MyIconButtonView button => ActionButton(button, null, button.PerformClick, iconOnly: true, readIcon: () => button.Path?.Data),
            MyIconTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick, readIcon: () => button.ShapeLogo?.Data),
            MyCheckBox checkbox => ProjectCheckbox(checkbox),
            MyRadioBox radio => ProjectRadioBox(radio),
            MyRadioButton radio => ProjectRadioButton(radio),
            MySlider slider => ProjectSlider(slider),
            MyListItem row => ProjectListRow(row),
            MyLocalModItem mod => ProjectModRow(mod),
            MyScreenshotItem screenshot => ProjectScreenshot(screenshot),
            MyResourceItem resource => ProjectResourceRow(resource),
            KeybindingPageRow binding => ProjectKeybindingRow(binding),
            MyCard group when group.Parent is StackPanel { Name: "PanBindings" } => ProjectKeybindingGroup(group),
            MyCard card => ProjectCard(card),
            MyHintView hint => ProjectHint(hint),
            TextBlock { Name: "FavoriteStatus" or "LabEmpty" } empty => ProjectEmptyState(empty),
            MyLoadingView loading => ProjectLoading(loading),
            ComboBox combo => ProjectCombo(combo),
            TextBox input => ProjectTextInput(input),
            PasswordBox password => ProjectPassword(password),
            Button button => ProjectNativeButton(button),
            CheckBox checkbox => ProjectNativeToggle(checkbox),
            RadioButton radio => ProjectNativeToggle(radio),
            Slider slider => ProjectNativeSlider(slider),
            ProgressBar progress => ProjectProgress(progress),
            FlowDocumentScrollViewer document => Document(document),
            RichTextBox document => RichDocument(document),
            DataGrid dataGrid => ProjectDataGrid(dataGrid),
            ListBox list => ProjectListBox(list),
            TextBlock text => ProjectText(text),
            Image image => ProjectBitmap(image),
            Shape shape => ProjectShape(shape),
            ScrollViewer scroll => ProjectScroll(scroll),
            Grid { Name: "PanRamDisplay" } memory => ProjectMemoryMeter(memory),
            Grid grid => ProjectGrid(grid),
            StackPanel { Name: "PanBindings" } bindings => ProjectKeybindingList(bindings),
            StackPanel { Name: "PanOptions" } options when SourceAncestor<PageInstanceExport>(options) is not null => ProjectExportOptions(options),
            StackPanel { Name: "PanCustom" } custom when SourceAncestor<PageLaunchRight>(custom) is { } homepage &&
                ReferenceEquals(homepage.PanCustom, custom) => ProjectHomeContent(homepage),
            StackPanel stack => ProjectStack(stack),
            WrapPanel wrap => ProjectWrap(wrap),
            UniformGrid uniform => ProjectUniformGrid(uniform),
            DockPanel dock => ProjectDock(dock),
            Canvas canvas => ProjectCanvas(canvas),
            Border border => ProjectBorder(border),
            Decorator decorator => ProjectDecorator(decorator),
            Expander expander => ProjectExpander(expander),
            ContentControl content => ProjectContentControl(content),
            ItemsControl items => ProjectItems(items),
            ContentPresenter presenter => ProjectPresenter(presenter),
            FrameworkElement element => ProjectStructural(element),
            _ => new Node(new Grid()),
        };
    }

    private object Content(object content) => content is DependencyObject dependency ? Project(dependency) : content;

    private Node ProjectGrid(Grid source)
    {
        if (source.Name == "PanManageButtons" && InPage<PageInstanceResources>(source))
        {
            var commands = new WrapPanel();
            return new Node(commands) { Pull = () =>
            {
                Children(commands, source.Children.OfType<DependencyObject>().Select(item => Project(item)).Where(item => item != null).ToArray());
                foreach (Button button in commands.Children.OfType<Button>()) button.Margin = new Thickness(0, 4, 10, 4);
            }};
        }
        var grid = new Grid();
        bool homeChoices = SettingsContext(source) && source.Children.OfType<MyRadioBox>().Any(item => item.Name == "RadioCustomType0");
        bool settingsRow = SettingsContext(source) && source.ColumnDefinitions.Count == 2 && source.Children.Count == 2 &&
            source.Children.OfType<TextBlock>().Any(label => Grid.GetColumn(label) == 0) &&
            source.Children.OfType<FrameworkElement>().Any(editor => Grid.GetColumn(editor) == 1 && editor is MyComboBoxView or MySliderView or TextBoxBase or PasswordBox);
        // Modal dimming/blur belongs to the independently animated integration scrim.
        if (source.Name == "PanMsg") grid.Background = Brushes.Transparent;
        var node = new Node(grid);
        node.Pull = () =>
        {
            while (grid.RowDefinitions.Count > source.RowDefinitions.Count) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
            while (grid.ColumnDefinitions.Count > source.ColumnDefinitions.Count) grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
            while (grid.RowDefinitions.Count < source.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition());
            while (grid.ColumnDefinitions.Count < source.ColumnDefinitions.Count) grid.ColumnDefinitions.Add(new ColumnDefinition());
            // Rows whose every element is collapsed (and the spacer row right after them) take no space,
            // so options hidden by the current mode do not leave a gap in the card.
            bool RowHidden(int row)
            {
                var cells = source.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == row).ToArray();
                return cells.Length > 0 && cells.All(child => child.Visibility == Visibility.Collapsed);
            }
            bool RowEmpty(int row) => !source.Children.OfType<FrameworkElement>().Any(child => Grid.GetRow(child) == row);
            for (int index = 0; index < source.RowDefinitions.Count; index++)
            {
                RowDefinition original = source.RowDefinitions[index], target = grid.RowDefinitions[index];
                if (RowHidden(index) || RowEmpty(index) && index > 0 && RowHidden(index - 1))
                {
                    target.Height = new GridLength(0); target.MinHeight = 0;
                    continue;
                }
                bool controls = source.Children.OfType<FrameworkElement>().Any(child => Grid.GetRow(child) == index && ContainsInteractive(child, 3));
                target.Height = original.Height.IsAbsolute && original.Height.Value < 38 && controls ? GridLength.Auto : original.Height;
                target.MinHeight = controls ? Math.Max(38, original.MinHeight) : original.MinHeight;
            }
            for (int index = 0; index < source.ColumnDefinitions.Count; index++)
            {
                ColumnDefinition original = source.ColumnDefinitions[index], target = grid.ColumnDefinitions[index];
                target.Width = settingsRow ? index == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(220) : original.Width;
                target.MinWidth = settingsRow ? 0 : original.MinWidth; target.MaxWidth = settingsRow ? double.PositiveInfinity : original.MaxWidth; target.SharedSizeGroup = settingsRow ? null : original.SharedSizeGroup;
            }
            if (!double.IsNaN(source.Height) && source.Height < 38 && source.Children.OfType<FrameworkElement>().Any(Interactive) &&
                !(SettingsContext(source) && source.Children.Cast<UIElement>().All(child => child is MyButtonView)))
                grid.MinHeight = 40;
            Children(grid, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
            // Java list: the original paints its summary over an empty drop-down; keep both in one full-width field.
            if (source.Name is "PanAdvanceJava" or "PanArgumentJavaSelect")
            {
                foreach (ComboBox field in grid.Children.OfType<ComboBox>())
                { field.Width = double.NaN; field.MaxWidth = double.PositiveInfinity; field.HorizontalAlignment = HorizontalAlignment.Stretch; }
                foreach (TextBlock summary in grid.Children.OfType<TextBlock>())
                { summary.Margin = new Thickness(13, 0, 36, 0); summary.VerticalAlignment = VerticalAlignment.Center; summary.IsHitTestVisible = false; summary.TextWrapping = TextWrapping.NoWrap; summary.TextTrimming = TextTrimming.CharacterEllipsis; Panel.SetZIndex(summary, 1); }
            }
            if (homeChoices)
                foreach (RadioButton choice in grid.Children.OfType<RadioButton>())
                {
                    // Four mutually exclusive modes in one row read as a segmented control; a ring would wrap them at the minimum width.
                    choice.Style = Style("GlassPresentationRadioButton"); choice.Padding = new Thickness(4, 6, 4, 6); choice.HorizontalContentAlignment = HorizontalAlignment.Center;
                    if (choice.Content is TextBlock label) { label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextAlignment = TextAlignment.Center; }
                }
            if (settingsRow)
            {
                foreach (FrameworkElement editor in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetColumn(child) == 1))
                {
                    editor.VerticalAlignment = VerticalAlignment.Center; editor.Width = editor is ComboBox ? 220 : double.NaN;
                    editor.MinWidth = Math.Min(editor.MinWidth, 220); editor.MaxWidth = 220;
                }
                foreach (TextBlock label in grid.Children.OfType<TextBlock>().Where(child => Grid.GetColumn(child) == 0))
                {
                    label.FontSize = 14; label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 16, 0);
                }
            }
        };
        return node;
    }

    private Grid _inlineRow;
    // One row: fixed-size items, the text field (or else the last item) takes the remaining width.
    private Grid InlineRow(IReadOnlyList<UIElement> items)
    {
        _inlineRow ??= new Grid();
        Grid row = _inlineRow;
        while (row.ColumnDefinitions.Count < items.Count) row.ColumnDefinitions.Add(new ColumnDefinition());
        while (row.ColumnDefinitions.Count > items.Count) row.ColumnDefinitions.RemoveAt(row.ColumnDefinitions.Count - 1);
        int fill = items.Count - 1;
        for (int index = 0; index < items.Count; index++)
            if (items[index] is TextBox || items[index] is Panel panel && panel.Children.OfType<TextBox>().Any()) { fill = index; break; }
        for (int index = 0; index < items.Count; index++)
        {
            bool last = index == fill;
            row.ColumnDefinitions[index].Width = last ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            Grid.SetColumn(items[index], index); Grid.SetRow(items[index], 0); Grid.SetColumnSpan(items[index], 1);
            if (items[index] is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 0, index == items.Count - 1 ? 0 : 12, 0);
                element.VerticalAlignment = VerticalAlignment.Center;
                if (!last) element.HorizontalAlignment = HorizontalAlignment.Left;
                else { element.HorizontalAlignment = HorizontalAlignment.Stretch; element.Width = double.NaN; }
            }
        }
        Children(row, items);
        return row;
    }

    private static Grid KeybindingColumns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        return grid;
    }

    private TextBlock KeybindingLabel(string value, bool secondary = false)
    {
        TextBlock label = PlainText(value);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (secondary)
        {
            label.FontSize = 12;
            label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        }
        return label;
    }

    private Node ProjectKeybindingList(StackPanel source)
    {
        var list = new StackPanel();
        var columns = KeybindingColumns();
        columns.Margin = new Thickness(0, 12, 0, 4);
        string[] titles = { "动作", "个人方案", "同步状态", "当前按键" };
        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock title = KeybindingLabel(titles[i], secondary: true);
            if (i == 3) title.TextAlignment = TextAlignment.Center;
            Grid.SetColumn(title, i);
            columns.Children.Add(title);
        }
        return new Node(list) { Pull = () =>
        {
            var rows = source.Children.Cast<UIElement>().Select(item => Project(item)).Where(item => item is not null).ToList();
            if (rows.Count > 0) rows.Insert(0, columns);
            Children(list, rows);
        } };
    }

    private Node ProjectKeybindingGroup(MyCard source)
    {
        var group = new StackPanel();
        TextBlock caption = KeybindingLabel(source.Title);
        caption.FontWeight = FontWeights.SemiBold;
        caption.Margin = new Thickness(0, 12, 0, 4);
        return new Node(group) { Pull = () =>
        {
            var rows = source.Children.OfType<StackPanel>().Select(panel => Project(panel, cardContent: true)).Where(item => item is not null).ToList();
            rows.Insert(0, caption);
            Children(group, rows);
        } };
    }

    /// <summary>
    /// Export options: each top-level option (游戏本体, Mod, 资源包, 光影包…) is a themed expander (the same spring-animated
    /// header, chevron and rule as the other folding cards) whose header holds the option itself and a count of chosen
    /// items; its sub-options lie in two columns inside. Groups start folded. Checking goes through the original boxes.
    /// </summary>
    private Node ProjectExportOptions(StackPanel source)
    {
        var list = new StackPanel();
        var groups = new Dictionary<UIElement, (Expander Expander, Grid Header, TextBlock Count, UniformGrid Items, FrameworkElement Plain)>(ReferenceEqualityComparer.Instance);
        var node = new Node(list);
        node.Pull = () =>
        {
            var views = new List<UIElement>();
            UIElement[] children = source.Children.Cast<UIElement>().ToArray();
            for (int index = 0; index < children.Length; index++)
            {
                if (children[index] is not FrameworkElement head || head.Visibility == Visibility.Collapsed) continue;
                var subs = new List<FrameworkElement>();
                while (index + 1 < children.Length && children[index + 1] is FrameworkElement { Margin.Left: > 0 } sub)
                {
                    index++;
                    if (sub.Visibility == Visibility.Collapsed) continue;
                    // Resource packs, shaders and saves: the controller fills a nested StackPanel with one box per file.
                    // (A check box is itself a Grid, so only StackPanels are containers.)
                    if (sub is StackPanel nested) subs.AddRange(nested.Children.OfType<FrameworkElement>().Where(item => item.Visibility != Visibility.Collapsed));
                    else subs.Add(sub);
                }
                if (!groups.TryGetValue(head, out var group))
                {
                    var header = new Grid();
                    header.ColumnDefinitions.Add(new ColumnDefinition());
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var count = PlainText(""); count.FontSize = 12; count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 0, 0);
                    count.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                    Grid.SetColumn(count, 1); header.Children.Add(count);
                    var items = new UniformGrid { Columns = 2, Margin = new Thickness(30, 0, 0, 4) };
                    var expander = new Expander { Style = Style("GlassPresentationExpander"), Header = header, Content = items, IsExpanded = false, Margin = new Thickness(0, 2, 0, 2) };
                    AutomationProperties.SetName(expander, "导出选项分组");
                    // A group without sub-options is just its own row (no chevron to open).
                    var plain = new Grid { MinHeight = 36 };
                    group = (expander, header, count, items, plain);
                    groups[head] = group;
                }
                bool foldable = subs.Count > 0;
                Panel holder = foldable ? group.Header : (Panel)group.Plain;
                if (Project(head) is FrameworkElement headView)
                {
                    if (headView.Parent is Panel owner && !ReferenceEquals(owner, holder)) owner.Children.Remove(headView);
                    headView.Margin = new Thickness(0); headView.VerticalAlignment = VerticalAlignment.Center;
                    if (!holder.Children.Contains(headView)) holder.Children.Insert(0, headView);
                }
                var subViews = subs.Select(sub => Project(sub)).OfType<FrameworkElement>().ToArray();
                foreach (FrameworkElement view in subViews)
                {
                    if (view.Parent is Panel owner && !ReferenceEquals(owner, group.Items)) owner.Children.Remove(view);
                    view.Margin = new Thickness(0, 0, 12, 0);
                }
                Children(group.Items, subViews);
                int chosen = subs.Count(sub => sub is MyCheckBox { Checked: true });
                group.Count.Text = foldable ? $"已选 {chosen} / {subs.Count}" : "";
                views.Add(foldable ? group.Expander : group.Plain);
            }
            Children(list, views);
        };
        return node;
    }

    private Node ProjectKeybindingRow(KeybindingPageRow source)
    {
        var columns = KeybindingColumns();
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        names.Children.Add(KeybindingLabel(source.Entry.Name));
        if (!string.IsNullOrEmpty(source.Detail)) names.Children.Add(KeybindingLabel(source.Detail, secondary: true));
        names.ToolTip = string.IsNullOrEmpty(source.Detail) ? source.Entry.Name : source.Entry.Name + "\n" + source.Detail;
        columns.Children.Add(names);

        if (source.Personal is { } profile)
        {
            TextBlock key = KeybindingLabel(profile.DisplayKey ?? "未保存", secondary: profile.DisplayKey is null);
            key.Margin = new Thickness(0, 0, 10, 0);
            key.ToolTip = profile.DisplayKey ?? "个人方案尚未保存此键位";
            Grid.SetColumn(key, 1);
            columns.Children.Add(key);
            if (profile.DisplayKey is not null)
            {
                TextBlock status = KeybindingLabel(profile.Participates ? "参与" : "未参与", secondary: !profile.Participates);
                var badge = new Border
                {
                    Child = status, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 1),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = profile.Participates ? "参与同步" : "未参与同步"
                };
                badge.SetResourceReference(Border.BackgroundProperty, profile.Participates ? "GlassPresentationSelected" : "GlassPresentationField");
                Grid.SetColumn(badge, 2);
                columns.Children.Add(badge);
            }
        }

        var row = new Border { Child = columns, MinHeight = 40, Padding = new Thickness(0, 3, 0, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationSeparator");
        return new Node(row) { Pull = () =>
        {
            if (Project(source.KeyButton) is not Button keyButton) return;
            keyButton.Width = double.NaN;
            keyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            keyButton.Height = 30; keyButton.MinHeight = 30; keyButton.FontSize = 13;
            Grid.SetColumn(keyButton, 3);
            if (!columns.Children.Contains(keyButton)) columns.Children.Add(keyButton);
        } };
    }

    private static bool Interactive(FrameworkElement element) => element is MyButtonView or MyCheckBoxView or MyRadioBoxView or MySliderView or MyListItemView or TextBoxBase or Selector or ButtonBase or PasswordBox;
    // Wrapped editors (for example a Grid hosting a ComboBox and size inputs) still need the projected control height.
    private static bool ContainsInteractive(FrameworkElement element, int depth)
        => Interactive(element) || depth > 0 && element is Panel or Decorator or ContentControl &&
           LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>().Any(child => child.Visibility != Visibility.Collapsed && ContainsInteractive(child, depth - 1));

    // The homepage loader places the authored WPF tree in this outlet. Its templates,
    // name scopes and event handlers remain part of the live tree.
    private Node ProjectHomeContent(PageLaunchRight page)
    {
        var host = new HomepageViewport();
        var card = new Style(typeof(GlassSurface));
        card.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlassPresentationFill")));
        card.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("GlassPresentationEdge")));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(.7)));
        card.Setters.Add(new Setter(GlassSurface.BlurRadiusProperty, 24d));
        host.Resources[typeof(GlassSurface)] = card;
        host.SetResourceReference(TextElement.ForegroundProperty, "ColorBrush1");
        TextElement.SetFontFamily(host, (FontFamily)(_root.TryFindResource("GlassPresentationFont") ?? new FontFamily("Microsoft YaHei UI")));
        ApplyHomepagePalette(host);
        LegacyThemePalette.Watch(host, () => ApplyHomepagePalette(host));

        var node = new Node(host);
        node.Cleanup.Add(page.PresentHomepageIn(host));
        return node;
    }

    private sealed class HomepageViewport : StackPanel
    {
        private readonly Dictionary<DependencyObject, Image[]> _fitted = new();

        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            if (visualRemoved is not null && _fitted.Remove(visualRemoved, out Image[] previous))
                foreach (Image image in previous) image.InvalidateProperty(Image.StretchProperty);
            if (visualAdded is null) return;
            Image[] fitted = Artwork(visualAdded).Where(HasFlexibleFrame).ToArray();
            if (fitted.Length == 0) return;
            _fitted.Add(visualAdded, fitted);
            foreach (Image image in fitted) image.SetCurrentValue(Image.StretchProperty, Stretch.Uniform);
        }

        private static IEnumerable<Image> Artwork(DependencyObject root)
        {
            if (root is Image image) { yield return image; yield break; }
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (Image descendant in Artwork(child)) yield return descendant;
        }

        // An auto-sized border with only an upper bound can shrink to fit the whole image.
        // Fixed frames and explicit clips continue to use their authored stretch mode.
        private static bool HasFlexibleFrame(Image image) =>
            image.Stretch == Stretch.UniformToFill && !DependencyPropertyHelper.GetValueSource(image, Image.StretchProperty).IsExpression &&
            image.Clip is null && !image.ClipToBounds && double.IsNaN(image.Width) && double.IsNaN(image.Height) &&
            image.Parent is Border { Clip: null, ClipToBounds: false } frame && double.IsNaN(frame.Width) && double.IsNaN(frame.Height) &&
            (double.IsFinite(frame.MaxHeight) || double.IsFinite(frame.MaxWidth));
    }

    private static readonly string[] HomepageNeutralTones = [
        "1", "2", "3", "4", "5", "6", "7", "8", "Bg0", "Bg1",
        "Gray1", "Gray2", "Gray3", "Gray4", "Gray5", "Gray6", "Gray7", "Gray8",
        "White", "HalfWhite", "SemiWhite", "SemiTransparent", "Card", "Background", "BackgroundTransparentSidebar",
        "Dialog", "Popup", "TextSecondary", "OnAccent", "TitleForeground", "TitleSelected", "Shadow"
    ];
    private static readonly byte[] HomepageLightRamp = [36, 67, 101, 108, 178, 222, 236, 248];
    private static readonly byte[] HomepageDarkRamp = [244, 220, 188, 178, 139, 70, 54, 34];

    // Keep the original light/dark relationships and opacity of the PCL resources.
    // Fragment-local colors and semantic status colors still resolve normally.
    private static void ApplyHomepagePalette(FrameworkElement host)
    {
        byte[] ramp = LegacyThemePalette.IsDarkTheme ? HomepageDarkRamp : HomepageLightRamp;
        for (int index = 0; index < HomepageNeutralTones.Length; index++)
        {
            string tone = HomepageNeutralTones[index];
            string brushKey = "ColorBrush" + tone, colorKey = "ColorObject" + tone;
            Color? source = (Application.Current?.TryFindResource(brushKey) as SolidColorBrush)?.Color ??
                Application.Current?.TryFindResource(colorKey) as Color?;
            if (source is not Color color) continue;
            // The numbered colors form an accent/foreground ramp. Converting the old blue
            // accent alone can make both ends of a pair nearly the same brightness.
            byte shade = index < ramp.Length ? ramp[index] : (byte)Math.Round(.2126 * color.R + .7152 * color.G + .0722 * color.B);
            Color neutral = Color.FromArgb(color.A, shade, shade, shade);
            if (host.Resources[brushKey] is SolidColorBrush existing && existing.Color == neutral) continue;
            var brush = new SolidColorBrush(neutral); brush.Freeze();
            host.Resources[brushKey] = brush;
            host.Resources[colorKey] = neutral;
        }
    }

    // Rounded corners for 32 px list icons (mods, worlds, resource packs, authors…).
    private static void RoundIcon(Image image)
    {
        var clip = new RectangleGeometry(new Rect(0, 0, image.Width, image.Height), 7, 7);
        clip.Freeze();
        image.Clip = clip;
    }

    // "Nothing here" notices (empty favourites, empty resource folders, no backups…): the hexagon writes a ?, the
    // message sits under it, centred.
    private Node ProjectEmptyState(TextBlock source)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 18, 16, 14) };
        stack.Children.Add(new GlassStatusGlyph(GlassStatusGlyph.Kind.Empty, 52) { HorizontalAlignment = HorizontalAlignment.Center });
        var text = PlainText(source.Text);
        Bind(text, TextBlock.TextProperty, source, nameof(source.Text));
        text.TextWrapping = TextWrapping.Wrap; text.TextAlignment = TextAlignment.Center; text.MaxWidth = 420; text.Margin = new Thickness(0, 12, 0, 0);
        text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        stack.Children.Add(text);
        // Favourites: the notice is the whole page's content, so it is centred in the visible area below its card's top.
        if (source.Name == "FavoriteStatus")
        {
            var frame = new Grid();
            stack.VerticalAlignment = VerticalAlignment.Center;
            frame.Children.Add(stack);
            void Fit()
            {
                ScrollViewer? scroll = null;
                for (DependencyObject? item = frame; item is not null; item = VisualTreeHelper.GetParent(item))
                    if (item is ScrollViewer found) { scroll = found; break; }
                if (scroll is null || !frame.IsLoaded) return;
                double top;
                try { top = frame.TransformToAncestor(scroll).Transform(new Point()).Y + scroll.VerticalOffset; }
                catch (InvalidOperationException) { return; }
                double room = Math.Max(0, scroll.ViewportHeight - top - 40);
                if (Math.Abs(frame.MinHeight - room) > 1) frame.MinHeight = room;
            }
            // Header and viewport sizes can both change after the empty state first appears.
            // Fit is a no-op unless the free space changes.
            frame.LayoutUpdated += (_, _) => Fit();
            return new Node(frame);
        }
        return new Node(stack);
    }

    private Node ProjectStack(StackPanel source, Func<DependencyObject[]> readChildren = null)
    {
        var stack = new StackPanel { Orientation = source.Orientation };
        var node = new Node(stack);
        Action requestViewport = IsInstanceResourceList(source) ? BindResourceViewport(stack, node) : null;
        DependencyObject[] previousRows = [];
        // MySearchBox derives from MyCard but belongs to the field layout of its enclosing card.
        bool settingsSections = SettingsContext(source) && source.Orientation == Orientation.Vertical &&
            source.Children.Cast<UIElement>().Any(child => child is MyCard and not MySearchBox);
        if (settingsSections) _responsiveStacks.Add(stack);

        int streamRevision = 0;
        System.Windows.Threading.DispatcherOperation streamOperation = null;
        node.Cleanup.Add(() =>
        {
            if (settingsSections) _responsiveStacks.Remove(stack);
            streamRevision++;
            if (streamOperation?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending)
                streamOperation.Abort();
            streamOperation = null;
        });

        node.Pull = () =>
        {
            DependencyObject[] raw = readChildren?.Invoke() ?? source.Children.OfType<DependencyObject>().ToArray();
            bool rowsChanged = requestViewport is not null && !raw.SequenceEqual(previousRows, ReferenceEqualityComparer.Instance);
            if (rowsChanged) previousRows = raw;
            // Stream long item lists progressively during live interactive navigation so frame 0 renders the first
            // screenful in < 5ms instead of building 50-300 rows at once. Headless/test sessions stay synchronous.
            bool canStream = readChildren is null && source.Orientation == Orientation.Vertical && raw.Length > 20 &&
                (_pageAppearance is not null || (_root.IsLoaded && ModAnimation.AniControlEnabled == 0)) &&
                raw[0] is MyLocalModItem or MyListItem or MyResourceItem;

            if (!canStream)
            {
                streamRevision++;
                if (streamOperation?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending)
                    streamOperation.Abort();
                streamOperation = null;
                Children(stack, ProjectWithToolbars(raw));
            }
            else
            {
                // Publish a usable first batch before filling the rest in short dispatcher slices.
                // A cold template load can make a fixed 16-row batch miss several frames.
                int initialCount = Math.Min(raw.Length, 16);
                long firstBatchStarted = Stopwatch.GetTimestamp();
                var immediate = new List<UIElement>(raw.Length);
                int firstPending = -1;
                for (int index = 0; index < raw.Length; index++)
                {
                    DependencyObject item = raw[index];
                    if (_nodes.ContainsKey(item) || index < initialCount && (index < 4 || Stopwatch.GetElapsedTime(firstBatchStarted).TotalMilliseconds < 4))
                    {
                        if (Project(item) is UIElement projected) immediate.Add(projected);
                    }
                    else
                    {
                        if (firstPending < 0) firstPending = index;
                    }
                }
                Children(stack, immediate);

                if (firstPending >= 0)
                {
                    int activeRevision = ++streamRevision;
                    if (streamOperation?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending)
                        streamOperation.Abort();

                    int cursor = firstPending;
                    void PumpNextBatch()
                    {
                        streamOperation = null;
                        if (_disposed || !Live(stack) || activeRevision != streamRevision) return;
                        int start = cursor, end = Math.Min(raw.Length, cursor + 18);
                        long started = Stopwatch.GetTimestamp();
                        for (; cursor < end; )
                        {
                            if (cursor > start && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 3) break;
                            int index = cursor++;
                            DependencyObject item = raw[index];
                            if (source.Children.IndexOf(item as UIElement) < 0) continue;
                            if (Project(item) is not UIElement projected || stack.Children.Contains(projected)) continue;
                            // Insert in source order: rows realized earlier (beyond this batch) must stay after it.
                            int position = 0;
                            for (int before = index - 1; before >= 0; before--)
                                if (_nodes.TryGetValue(raw[before], out Node previous) && stack.Children.IndexOf(previous.View) is int found and >= 0)
                                { position = found + 1; break; }
                            stack.Children.Insert(position, projected);
                        }
                        requestViewport?.Invoke();
                        if (cursor < raw.Length)
                            streamOperation = stack.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(PumpNextBatch));
                    }
                    streamOperation = stack.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(PumpNextBatch));
                }
            }

            if (rowsChanged) requestViewport();
            if (settingsSections)
            {
                stack.MaxWidth = 800;
                stack.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Width = _root.ActualWidth > 12 ? Math.Min(800, _root.ActualWidth - 12) : double.NaN;
            }
        };
        return node;
    }

    private static bool IsInstanceResourceList(StackPanel source) => source.Name == "PanList" &&
        (InPage<PageInstanceMod>(source) || InPage<PageInstanceResources>(source));

    private Action BindResourceViewport(StackPanel stack, Node node)
    {
        ScrollViewer scroll = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background, stack.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };

        void Schedule()
        {
            if (scroll is not null && stack.IsVisible && Live(stack)) timer.Start();
        }
        void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (ReferenceEquals(args.OriginalSource, scroll)) Schedule();
        }
        void Detach()
        {
            timer.Stop();
            if (scroll is not null) scroll.ScrollChanged -= Scrolled;
            scroll = null;
        }
        RoutedEventHandler loaded = (_, _) => {
            Detach();
            for (DependencyObject parent = stack; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer viewer) { scroll = viewer; break; }
            if (scroll is not null) scroll.ScrollChanged += Scrolled;
            Schedule();
        };
        RoutedEventHandler unloaded = (_, _) => Detach();
        SizeChangedEventHandler sized = (_, _) => Schedule();
        DependencyPropertyChangedEventHandler visible = (_, _) => { if (stack.IsVisible) Schedule(); else timer.Stop(); };
        stack.Loaded += loaded; stack.Unloaded += unloaded;
        stack.SizeChanged += sized; stack.IsVisibleChanged += visible;
        node.Cleanup.Add(() => {
            Detach(); stack.Loaded -= loaded; stack.Unloaded -= unloaded;
            stack.SizeChanged -= sized; stack.IsVisibleChanged -= visible;
        });

        timer.Tick += (_, _) => {
            if (scroll is null || !Live(stack) || !stack.IsVisible || scroll.ViewportHeight <= 0) { timer.Stop(); return; }
            var viewport = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement ?? scroll;
            double top = -stack.TranslatePoint(new Point(), viewport).Y;
            double bottom = top + scroll.ViewportHeight;
            double buffer = Math.Min(208, scroll.ViewportHeight / 2);
            int prepared = 0;
            long start = Stopwatch.GetTimestamp();

            // Finish visible work first. During a fast scroll, the next slice uses the new viewport.
            bool Process(double from, double to, bool entering)
            {
                foreach (UIElement child in stack.Children)
                {
                    if (child is not FrameworkElement { IsVisible: true, ActualHeight: > 0 } row) continue;
                    double y = VisualTreeHelper.GetOffset(row).Y;
                    if (y >= to) break;
                    if (y + row.ActualHeight <= from || row.GetValue(LifetimeProperty) is not Node item || item.IsDisposed) continue;
                    if (item.PrepareResourceRow is Action prepare)
                    {
                        if (prepared >= 4 || prepared > 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) return true;
                        item.PrepareResourceRow = null;
                        prepare();
                        prepared++;
                    }
                    if (entering && item.RevealResourceRow is Action reveal)
                    {
                        item.RevealResourceRow = null;
                        reveal();
                    }
                }
                return false;
            }
            if (!Process(top, bottom, true) && !Process(top - buffer, bottom + buffer, false)) timer.Stop();
        };
        return Schedule;
    }
    private const double ActionRowHalfGap = 4;

    private static bool IsActionWrap(WrapPanel panel) => panel.Orientation == Orientation.Horizontal &&
        panel.Children.Count > 0 && panel.Children.Cast<UIElement>().All(child => child is Button);

    private Node ProjectWrap(WrapPanel source)
    {
        var wrap = new WrapPanel { Orientation = source.Orientation };
        bool settings = SettingsContext(source);
        var node = new Node(wrap);
        node.Pull = () =>
        {
            Children(wrap, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
            if (settings && IsActionWrap(wrap))
                foreach (Button button in wrap.Children.OfType<Button>())
                    button.Margin = new Thickness(button.Margin.Left, ActionRowHalfGap, button.Margin.Right, ActionRowHalfGap);
        };
        return node;
    }

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
        double inset = element is WrapPanel actions && IsActionWrap(actions) ? -ActionRowHalfGap : 0;
        Thickness margin = element.Margin;
        element.Margin = new Thickness(margin.Left, top ? inset : margin.Top, margin.Right, top ? margin.Bottom : inset);
        return true;
    }
    private Node ProjectDock(DockPanel source)
    {
        var dock = new DockPanel { LastChildFill = source.LastChildFill };
        var node = new Node(dock);
        node.Pull = () => Children(dock, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
        return node;
    }
    private Node ProjectCanvas(Canvas source)
    {
        var canvas = new Canvas();
        var node = new Node(canvas);
        node.Pull = () => Children(canvas, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
        return node;
    }
    private Node ProjectBorder(Border source)
    {
        Border border;
        if (source.Name == "PanBorder" && AncestorMessage(source))
        {
            bool warning = GlassModalState.IsWarningAncestor(source);
            border = new GlassSurface {
                Padding = new Thickness(Math.Max(source.Padding.Left, 6), Math.Max(source.Padding.Top, 4), Math.Max(source.Padding.Right, 6), Math.Max(source.Padding.Bottom, 4)),
                CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1),
                BlurRadius = 30, HighlightOpacity = 0
            };
            // The modal projection receives its app-only scene snapshot from integration.
            // Never replace a missing/transparent snapshot with a direct desktop backdrop.
            GlassMaterial.SetDesktopBackdropEnabled(border, false);
            border.SetResourceReference(Border.BackgroundProperty, warning ? "GlassPresentationWarnFill" : "GlassPresentationDialogFill");
            border.SetResourceReference(Border.BorderBrushProperty, warning ? "GlassPresentationWarnEdge" : "GlassPresentationDialogEdge");
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect {
                BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = .22, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                Color = warning ? Color.FromRgb(90, 10, 10) : Colors.Black
            };
        }
        else border = new Border { Padding = source.Padding, CornerRadius = new CornerRadius(14) };
        if (source.Tag is ModDependencyGraphNodeData dependency)
        {
            bool selected = source.BorderThickness.Left > 1;
            border.CornerRadius = new CornerRadius(9);
            border.BorderThickness = new Thickness(1);
            border.SetResourceReference(Border.BackgroundProperty, selected ? "GlassPresentationSelected" : "GlassPresentationField");
            border.SetResourceReference(Border.BorderBrushProperty, dependency.IsConflict || dependency.NeedsAttention ? "GlassPresentationWarnEdge" :
                selected ? "GlassPresentationFocusEdge" : "GlassPresentationEdge");
        }
        if (InPage<MyScreenshotItem>(source))
        {
            border.ClipToBounds = true;
            border.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        }
        var node = new Node(border);
        node.Pull = () =>
        {
            if (border is not GlassSurface)
                border.Padding = node.CardContent && source.Padding.Top >= 36
                    ? new Thickness(0, 4, 0, 0)
                    : source.Padding;
            UIElement child = Project(source.Child);
            if (!ReferenceEquals(border.Child, child)) border.Child = child;
        };
        return node;
    }
    private Node ProjectUniformGrid(UniformGrid source)
    {
        var uniform = new UniformGrid();
        var node = new Node(uniform);
        node.Pull = () =>
        {
            uniform.Rows = source.Rows;
            uniform.Columns = source.Columns;
            uniform.FirstColumn = source.FirstColumn;
            UIElement[] items = source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray();
            for (int index = 0; index < items.Length; index++)
            {
                if (items[index] is FrameworkElement element && element.Margin == new Thickness() && items.Length > 1 && source.Rows <= 1)
                    element.Margin = new Thickness(index == 0 ? 0 : 6, 0, 0, 0);
            }
            Children(uniform, items);
        };
        return node;
    }
    private Node ProjectExpander(Expander source)
    {
        var expander = new Expander
        {
            Style = Style("GlassPresentationExpander"),
            IsExpanded = source.IsExpanded,
            Padding = new Thickness(),
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 6, 14, 8),
            BorderThickness = new Thickness(1),
            Child = expander,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationControlStroke");

        bool pulling = false;
        RoutedEventHandler syncExpanded = (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, expander) || !Live(frame) || pulling || source.IsExpanded == expander.IsExpanded) return;
            source.IsExpanded = expander.IsExpanded;
            Refresh();
        };
        expander.Expanded += syncExpanded;
        expander.Collapsed += syncExpanded;

        var headerText = PlainText(string.Empty);
        headerText.FontWeight = FontWeights.SemiBold;
        headerText.VerticalAlignment = VerticalAlignment.Center;

        var node = new Node(frame);
        node.Pull = () =>
        {
            pulling = true;
            try
            {
                if (source.Header is string textHeader)
                {
                    headerText.Text = textHeader;
                    if (!ReferenceEquals(expander.Header, headerText)) expander.Header = headerText;
                }
                else
                {
                    object projectedHeader = Content(source.Header);
                    if (!ReferenceEquals(expander.Header, projectedHeader)) expander.Header = projectedHeader;
                }
                object projectedContent = Content(source.Content);
                if (!ReferenceEquals(expander.Content, projectedContent)) expander.Content = projectedContent;
                expander.IsExpanded = source.IsExpanded;
            }
            finally { pulling = false; }
        };
        return node;
    }
    private Node ProjectDataGrid(DataGrid source)
    {
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("GlassPresentationSecondary")));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.5d));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("GlassPresentationEdge")));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("GlassPresentationText")));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));

        var grid = new DataGrid
        {
            IsReadOnly = source.IsReadOnly,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = Brushes.Transparent,
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ColumnHeaderStyle = headerStyle,
            CellStyle = cellStyle,
        };
        grid.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "GlassPresentationSeparator");

        foreach (DataGridColumn column in source.Columns)
        {
            if (column is DataGridTextColumn textColumn)
            {
                BindingBase clonedBinding = textColumn.Binding is Binding oldBinding && oldBinding.Path is not null
                    ? new Binding(oldBinding.Path.Path) { Mode = BindingMode.OneWay }
                    : textColumn.Binding;
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = textColumn.Header,
                    Binding = clonedBinding,
                    Width = textColumn.Width,
                });
            }
        }

        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = grid,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationControlStroke");
        if (!double.IsNaN(source.Height) && source.Height > 0) grid.Height = source.Height;

        var node = new Node(frame);
        node.Pull = () =>
        {
            if (!ReferenceEquals(grid.ItemsSource, source.ItemsSource))
                grid.ItemsSource = source.ItemsSource;
        };
        return node;
    }
    private Node ProjectDecorator(Decorator source)
    {
        var border = new Border(); var node = new Node(border);
        node.Pull = () => { UIElement child = Project(source.Child); if (!ReferenceEquals(border.Child, child)) border.Child = child; };
        return node;
    }

    private Node ProjectContentControl(ContentControl source)
    {
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var node = new Node(content);
        node.Pull = () => { object child = Content(source.Content); if (!ReferenceEquals(content.Content, child)) content.Content = child; };
        return node;
    }
    private Node ProjectPresenter(ContentPresenter source)
    {
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var node = new Node(content);
        node.Pull = () => { object child = Content(source.Content); if (!ReferenceEquals(content.Content, child)) content.Content = child; };
        return node;
    }
    private Node ProjectScroll(ScrollViewer source)
    {
        var scroll = new ScrollViewer { Style = Style("GlassPresentationScrollViewer"), VerticalScrollBarVisibility = source.VerticalScrollBarVisibility, HorizontalScrollBarVisibility = source.HorizontalScrollBarVisibility, PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(scroll, true);
        double expectedSource = double.NaN, expectedTarget = double.NaN;
        // The homepage is never laid out in its original page while the shell shows it, so its offsets mean nothing:
        // the home scroller keeps its own position instead of mirroring the hidden original.
        bool independent = source.Name == "PanBack" && (source.Parent is FrameworkElement { Parent: PageLaunchRight } || source.Parent is PageLaunchRight);
        ScrollChangedEventHandler changed = (_, args) =>
        {
            if (independent || !Live(scroll) || !ReferenceEquals(args.OriginalSource, source) || Math.Abs(args.VerticalChange) < .01) return;
            if (!double.IsNaN(expectedSource) && Math.Abs(source.VerticalOffset - expectedSource) < 1) { expectedSource = double.NaN; return; }
            expectedSource = double.NaN;
            double fraction = source.ScrollableHeight <= 0 ? 0 : source.VerticalOffset / source.ScrollableHeight;
            expectedTarget = Math.Clamp(fraction * scroll.ScrollableHeight, 0, scroll.ScrollableHeight);
            scroll.ScrollToVerticalOffset(expectedTarget);
        };
        source.ScrollChanged += changed;
        scroll.ScrollChanged += (_, args) =>
        {
            if (independent || !Live(scroll) || !ReferenceEquals(args.OriginalSource, scroll) || Math.Abs(args.VerticalChange) < .01) return;
            if (!double.IsNaN(expectedTarget) && Math.Abs(scroll.VerticalOffset - expectedTarget) < 1) { expectedTarget = double.NaN; return; }
            expectedTarget = double.NaN;
            double fraction = scroll.ScrollableHeight <= 0 ? 0 : scroll.VerticalOffset / scroll.ScrollableHeight;
            expectedSource = Math.Clamp(fraction * source.ScrollableHeight, 0, source.ScrollableHeight);
            source.ScrollToVerticalOffset(expectedSource);
        };
        // The page-level scroller softens content passing under its top/bottom edges with a progressive blur.
        var node = new Node(PageScroller(source) ? new ScrollEdgeFade(scroll) : scroll); node.Cleanup.Add(() => source.ScrollChanged -= changed);
        node.Pull = () => { object child = Content(source.Content); if (!ReferenceEquals(scroll.Content, child)) scroll.Content = child; };
        return node;
    }
    private bool PageScroller(ScrollViewer source)
    {
        if (_source.Name != "PanMainRight" || source.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return false;
        for (DependencyObject item = LogicalTreeHelper.GetParent(source) ?? VisualTreeHelper.GetParent(source); item != null && !ReferenceEquals(item, _source);
             item = LogicalTreeHelper.GetParent(item) ?? (item is Visual ? VisualTreeHelper.GetParent(item) : null))
            if (item is ScrollViewer) return false;
        return true;
    }
    private Node ProjectItems(ItemsControl source)
    {
        var stack = new StackPanel(); var node = new Node(stack);
        node.Pull = () => Children(stack, source.Items.Cast<object>().Select(item => item is DependencyObject dependency ? Project(dependency) : PlainText(item?.ToString() ?? string.Empty)).Where(item => item != null).ToArray());
        return node;
    }
    private Node ProjectListBox(ListBox source)
    {
        var list = new ListBox { BorderThickness = new Thickness(), Background = Brushes.Transparent, SelectionMode = source.SelectionMode };
        list.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        var identity = new List<object>(); bool pulling = false;
        list.SelectionChanged += (_, _) => { if (Live(list) && !pulling) source.SelectedIndex = list.SelectedIndex; };
        var node = new Node(list);
        node.Pull = () =>
        {
            pulling = true;
            try
            {
                object[] items = source.Items.Cast<object>().ToArray();
                if (identity.Count != items.Length || !identity.Zip(items).All(pair => ReferenceEquals(pair.First, pair.Second)))
                {
                    list.Items.Clear(); identity.Clear(); identity.AddRange(items);
                    foreach (object item in items) list.Items.Add(new ListBoxItem { Padding = new Thickness(8), Content = item is DependencyObject dependency ? Project(dependency) : item });
                }
                else for (int index = 0; index < items.Length; index++)
                    if (items[index] is DependencyObject dependency) ((ListBoxItem)list.Items[index]).Content = Project(dependency);
                list.SelectedIndex = source.SelectedIndex;
            }
            finally { pulling = false; }
        };
        return node;
    }
    private Node ProjectStructural(FrameworkElement source)
    {
        var stack = new StackPanel(); var node = new Node(stack);
        node.Pull = () =>
        {
            var children = LogicalTreeHelper.GetChildren(source).OfType<DependencyObject>().Where(child => child is UIElement).ToArray();
            if (children.Length == 0 && source is UIElement)
            {
                int count = VisualTreeHelper.GetChildrenCount(source);
                children = Enumerable.Range(0, count).Select(index => VisualTreeHelper.GetChild(source, index)).Where(child => child is UIElement).ToArray();
            }
            if (children.Length == 0 && source.GetType().Assembly == typeof(MyButton).Assembly) _unsupported.Add(source.GetType().FullName);
            Children(stack, children.Select(child => Project(child)).Where(child => child != null).ToArray());
        };
        return node;
    }

    private Node ProjectVirtual(FrameworkElement source, IControlVirtualElement virtualized)
    {
        var placeholder = new Border { MinHeight = !double.IsNaN(source.Height) && source.Height > 0 ? source.Height : 44 };
        bool realized = false;
        EventHandler enter = (_, _) =>
        {
            if (!Live(placeholder) || realized || !placeholder.IsVisible || placeholder.ActualWidth <= 0) return;
            DependencyObject ancestor = placeholder;
            while (ancestor != null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is not ScrollViewer scroll || !new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(
                placeholder.TransformToAncestor(scroll).TransformBounds(new Rect(placeholder.RenderSize)))) return;
            realized = true;
            virtualized.InitializeElement();
            QueueRefresh();
        };
        placeholder.LayoutUpdated += enter;
        var node = new Node(placeholder); node.Cleanup.Add(() => placeholder.LayoutUpdated -= enter);
        return node;
    }

    internal static bool IsFloatingCommandBar(DependencyObject source) =>
        source is MyCard { Name: "CardSelect" or "CardCapture" };

    internal static bool FloatingCommandBarContext(DependencyObject source)
    {
        for (DependencyObject current = source; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (IsFloatingCommandBar(current)) return true;
        return false;
    }

    private Node ProjectFloatingCommandBar(MyCard source)
    {
        var entranceScale = new ScaleTransform(1, 1);
        var entranceTranslate = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(entranceScale);
        transformGroup.Children.Add(entranceTranslate);

        var host = new Grid
        {
            RenderTransformOrigin = new Point(0.5, 1.0),
            RenderTransform = transformGroup,
        };
        Panel.SetZIndex(host, 12);

        var shadowCaster = new Border
        {
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(1),
            Opacity = 0.52,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 7,
                Direction = 270,
                Opacity = 0.36,
                Color = Colors.Black,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
        };
        shadowCaster.SetResourceReference(Border.BackgroundProperty, "GlassPresentationPopup");
        host.Children.Add(shadowCaster);

        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var surface = new GlassSurface
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6, 5, 6, 5),
            BorderThickness = new Thickness(1),
            BlurRadius = 30,
            TintOpacity = 1,
            HighlightOpacity = 0,
            Child = bar,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDockFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationDockEdge");
        host.Children.Add(surface);

        var statusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusDot.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusRow.Children.Add(statusDot);

        var statusBadge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Height = 32,
            Padding = new Thickness(10, 0, 11, 0),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusRow,
        };
        statusBadge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDockBadgeFill");

        static Border CreateDockDivider()
        {
            var divider = new Border
            {
                Width = 1,
                Height = 16,
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            divider.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
            return divider;
        }

        Border leadingDivider = CreateDockDivider();
        Border trailingDivider = CreateDockDivider();
        TextBlock attachedLabel = null;
        var watchedButtons = new HashSet<UIElement>(ReferenceEqualityComparer.Instance);
        var mutationElements = new List<UIElement>();
        var dismissElements = new List<UIElement>();

        void UpdateSeparators()
        {
            bool hasMutation = mutationElements.Any(item => item.Visibility == Visibility.Visible);
            bool hasDismiss = dismissElements.Any(item => item.Visibility == Visibility.Visible);
            leadingDivider.Visibility = (hasMutation || hasDismiss) ? Visibility.Visible : Visibility.Collapsed;
            trailingDivider.Visibility = (hasMutation && hasDismiss) ? Visibility.Visible : Visibility.Collapsed;
        }

        var entranceMotion = new SpringMotion(
            host,
            1d,
            progress =>
            {
                entranceTranslate.Y = 12d * (1d - progress);
                double scale = 0.96d + 0.04d * progress;
                entranceScale.ScaleX = scale;
                entranceScale.ScaleY = scale;
            },
            new SpringParameters(0.24, 0.86));

        DependencyPropertyChangedEventHandler visibilityWatcher = (_, _) => UpdateSeparators();
        DependencyPropertyChangedEventHandler hostVisibleChanged = (_, args) =>
        {
            if (args.NewValue is not true) return;
            if (ModAnimation.AniControlEnabled > 0)
            {
                entranceMotion.Snap(1d);
                return;
            }
            entranceMotion.Snap(0d);
            entranceMotion.Retarget(1d);
        };
        host.IsVisibleChanged += hostVisibleChanged;

        var node = new Node(host);
        node.Cleanup.Add(() =>
        {
            host.IsVisibleChanged -= hostVisibleChanged;
            entranceMotion.Dispose();
            foreach (UIElement button in watchedButtons)
                button.IsVisibleChanged -= visibilityWatcher;
            watchedButtons.Clear();
        });

        node.Pull = () =>
        {
            if (_root.Resources["GlassPresentationDockFill"] is SolidColorBrush dockBrush)
                GlassMaterial.SetFallbackColor(surface, Color.FromRgb(dockBrush.Color.R, dockBrush.Color.G, dockBrush.Color.B));
            var rawChildren = source.Children.Cast<UIElement>().Skip(3).ToList();
            TextBlock labelSource = rawChildren.OfType<TextBlock>().FirstOrDefault();
            var buttonSources = new List<FrameworkElement>();
            foreach (UIElement child in rawChildren)
            {
                if (ReferenceEquals(child, labelSource)) continue;
                if (child is Panel group)
                {
                    foreach (FrameworkElement nested in group.Children.OfType<FrameworkElement>())
                        buttonSources.Add(nested);
                }
                else if (child is FrameworkElement direct)
                {
                    buttonSources.Add(direct);
                }
            }

            var items = new List<UIElement>();
            if (labelSource is not null && Project(labelSource, true) is TextBlock projectedLabel)
            {
                projectedLabel.FontSize = 12.5;
                projectedLabel.FontWeight = FontWeights.SemiBold;
                projectedLabel.TextWrapping = TextWrapping.NoWrap;
                projectedLabel.TextTrimming = TextTrimming.CharacterEllipsis;
                projectedLabel.MaxWidth = source.Name == "CardCapture" ? 340 : 240;
                projectedLabel.VerticalAlignment = VerticalAlignment.Center;
                projectedLabel.Margin = new Thickness();
                projectedLabel.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
                statusBadge.ToolTip = string.IsNullOrWhiteSpace(projectedLabel.Text) || projectedLabel.Text.Length <= 22 ? null : projectedLabel.Text;
                if (!ReferenceEquals(attachedLabel, projectedLabel))
                {
                    if (attachedLabel is not null) statusRow.Children.Remove(attachedLabel);
                    if (VisualTreeHelper.GetParent(projectedLabel) is Panel oldParent && !ReferenceEquals(oldParent, statusRow))
                        oldParent.Children.Remove(projectedLabel);
                    statusRow.Children.Add(projectedLabel);
                    attachedLabel = projectedLabel;
                }
                items.Add(statusBadge);
            }

            mutationElements.Clear();
            dismissElements.Clear();

            foreach (FrameworkElement btnSource in buttonSources)
            {
                if (Project(btnSource, true) is not UIElement projectedElement) continue;
                string name = btnSource.Name ?? string.Empty;
                string caption = (projectedElement as Button)?.Content is Panel p
                    ? p.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty
                    : string.Empty;

                bool isDismiss = name.EndsWith("Cancel", StringComparison.Ordinal) ||
                    name.EndsWith("CancelBinding", StringComparison.Ordinal) ||
                    name.EndsWith("Deselect", StringComparison.Ordinal) ||
                    caption is "取消选择" or "取消修改";
                bool isDanger = !isDismiss && (
                    name.Contains("Delete", StringComparison.Ordinal) ||
                    caption == "删除" ||
                    btnSource is MyButtonView { ColorType: MyButtonView.ColorState.Red });

                if (projectedElement is Button dockButton)
                {
                    dockButton.Style = Style("GlassPresentationDockButton");
                    dockButton.Height = 32;
                    dockButton.MinHeight = 32;
                    dockButton.Width = double.NaN;
                    dockButton.MinWidth = 0;
                    dockButton.Padding = new Thickness(9, 0, 10, 0);
                    dockButton.Margin = new Thickness(2, 0, 2, 0);
                    dockButton.VerticalAlignment = VerticalAlignment.Center;

                    Path icon = (dockButton.Content as Panel)?.Children.OfType<Path>().FirstOrDefault();
                    TextBlock label = (dockButton.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
                    if (icon is not null)
                    {
                        icon.Width = 13.5;
                        icon.Height = 13.5;
                        icon.Margin = new Thickness(0, 0, 5.5, 0);
                    }
                    if (label is not null)
                    {
                        label.FontSize = 12.5;
                        label.FontWeight = FontWeights.Medium;
                    }

                    if (isDanger)
                    {
                        dockButton.SetResourceReference(Control.BackgroundProperty, "GlassPresentationDangerFill");
                        dockButton.BorderBrush = Brushes.Transparent;
                        dockButton.SetResourceReference(Control.ForegroundProperty, "GlassPresentationDangerText");
                        label?.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDangerText");
                        icon?.SetResourceReference(Shape.FillProperty, "GlassPresentationDangerText");
                    }
                    else if (isDismiss)
                    {
                        dockButton.Background = Brushes.Transparent;
                        dockButton.BorderBrush = Brushes.Transparent;
                        dockButton.SetResourceReference(Control.ForegroundProperty, "GlassPresentationSecondary");
                        label?.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                        icon?.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
                    }
                    else
                    {
                        dockButton.Background = Brushes.Transparent;
                        dockButton.BorderBrush = Brushes.Transparent;
                        dockButton.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
                        label?.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
                        icon?.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
                    }
                }

                if (watchedButtons.Add(projectedElement))
                    projectedElement.IsVisibleChanged += visibilityWatcher;

                if (isDismiss) dismissElements.Add(projectedElement);
                else mutationElements.Add(projectedElement);
            }

            if (items.Count > 0) items.Add(leadingDivider);
            items.AddRange(mutationElements);
            items.Add(trailingDivider);
            items.AddRange(dismissElements);

            Children(bar, items);
            UpdateSeparators();
        };

        return node;
    }

    private Node ProjectCard(MyCard source)
    {
        if (IsFloatingCommandBar(source)) return ProjectFloatingCommandBar(source);
        if (IsActionCard(source)) return ProjectActionCard(source);
        if (source.Name == "CardMinecraft" && SourceAncestor<PageDownloadModify>(source) is { } modify)
            return ProjectMinecraftChange(source, modify);
        var body = new StackPanel();
        bool settingsSection = SettingsContext(source) && !source.CanSwap && source.SwapControl is null;
        var header = PlainText(source.Title); header.FontSize = settingsSection ? 16 : 15; header.FontWeight = FontWeights.SemiBold; header.Margin = new Thickness(0, 0, 0, 12);
        Bind(header, TextBlock.TextProperty, source, nameof(source.Title));
        var stack = new StackPanel(); stack.Children.Add(header); stack.Children.Add(body);
        // Cards frost the wallpaper directly beneath them (their own blurred sample); the page background stays sharp.
        var surface = new GlassSurface { CornerRadius = new CornerRadius(settingsSection ? 10 : 14), Padding = settingsSection ? new Thickness(16, 14, 16, 14) : new Thickness(20), Child = stack, BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill"); surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        UIElement view = surface;
        if (settingsSection)
        {
            stack.Children.Clear(); surface.Child = body;
            stack.Children.Add(header); stack.Children.Add(surface);
            surface.Name = string.IsNullOrEmpty(source.Name) ? string.Empty : source.Name + "Surface";
            view = stack;
        }
        var node = new Node(view);
        Action musicSettings = source.Name == "CardMusic" && SourceAncestor<PageSetupUI>(source) is { } setup
            ? BuildMusicSettings(setup, body, node) : null;
        GlassResourceInfo detailInfo = null;
        // The header can be shown on its own. Build an initially collapsed body on
        // its first expansion, and retain any controls the user has already opened.
        bool bodyRealized = !source.CanSwap;
        // Install components (Forge, Fabric, OptiFine...): the original shows the chosen version, "可以添加" or the reason it
        // cannot be chosen in a small info row plus a clear button. Those move into the header (see InstallStatus).
        Grid installInfo = source.CanSwap ? source.Children.OfType<Grid>().FirstOrDefault(grid => grid.Name.StartsWith("Pan", StringComparison.Ordinal) && grid.Name.EndsWith("Info", StringComparison.Ordinal) && grid.Children.OfType<TextBlock>().Any()) : null;
        FrameworkElement installClear = installInfo is null ? null : source.Children.OfType<FrameworkElement>().FirstOrDefault(child => child.Name.EndsWith("Clear", StringComparison.Ordinal));
        FrameworkElement syncStatus = source.CanSwap && InPage<PageSetupPersonal>(source)
            ? source.Children.OfType<FrameworkElement>().FirstOrDefault(child => child.Tag is string tag &&
                (tag.StartsWith("sync-state:", StringComparison.Ordinal) || tag.StartsWith("sync-category:", StringComparison.Ordinal)))
            : null;
        node.Pull = () =>
        {
            string caption = new TextRange(source.MainTextBlock.ContentStart, source.MainTextBlock.ContentEnd).Text;
            header.SetCurrentValue(TextBlock.TextProperty, caption);
            header.Visibility = string.IsNullOrWhiteSpace(caption) ? Visibility.Collapsed : Visibility.Visible;
            if (musicSettings is not null) { musicSettings(); return; }
            if (!bodyRealized && source.IsSwapped) return;
            bodyRealized = true;
            var children = source.Children.Cast<UIElement>().Skip(3).Where(child => !ReferenceEquals(child, installInfo) && !ReferenceEquals(child, installClear) && !ReferenceEquals(child, syncStatus)).OfType<DependencyObject>().ToList();
            if (source.BorderChild is DependencyObject inner) children.Insert(0, inner);
            var projected = children.Select(child => Project(child, true)).Where(child => child != null).ToList();
            if (CardSupplement?.Invoke(source) is UIElement supplement) projected.Insert(0, supplement);
            // Resource detail: an info panel (versions, loaders, source, downloads, update, tags) under the project header.
            if (source.Name == "CardIntro" && DetailPage(source)?.GlassProject is ResourceProject project)
            {
                detailInfo ??= new GlassResourceInfo();
                detailInfo.Show(project);
                projected.Insert(Math.Min(1, projected.Count), detailInfo);
            }
            // Install name card: the original overlays back button, icon and name field with fixed margins in one row.
            if (source.Name == "CardSelectName") { Children(body, new UIElement[] { InlineRow(projected) }); return; }
            Children(body, projected);
            if (settingsSection)
            {
                TrimCardEdge(body, top: true);
                TrimCardEdge(body, top: false);
            }
        };
        if (source.CanSwap || source.SwapControl != null)
        {
            header.Margin = new Thickness();
            // A collapsed section is a single 40px header row; keep it compact like a Fluent expander.
            surface.Padding = new Thickness(18, 10, 18, 10);
            // Release both logical parents before assigning the new header/content owners.
            stack.Children.Clear();
            object headerContent = header;
            Action<Expander> pullStatus = null;
            if (installInfo is not null) (headerContent, pullStatus) = InstallStatus(source, header, installInfo, installClear);
            else if (syncStatus is not null)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.VerticalAlignment = VerticalAlignment.Center;
                header.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(header);
                headerContent = row;
                pullStatus = _ =>
                {
                    if (Project(syncStatus, true) is not FrameworkElement status) return;
                    status.Margin = new Thickness(12, 0, 0, 0);
                    status.VerticalAlignment = VerticalAlignment.Center;
                    if (status is TextBlock text) text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                    Grid.SetColumn(status, 1);
                    if (!row.Children.Contains(status)) row.Children.Add(status);
                };
            }
            var expander = new Expander { Style = Style("GlassPresentationExpander"), Header = headerContent, Content = body, IsExpanded = !source.IsSwapped, Padding = new Thickness() };
            surface.Child = expander;
            if (pullStatus is not null)
            {
                Action statusPull = node.Pull;
                node.Pull = () => { statusPull(); pullStatus(expander); };
            }
            bool pulling = false, opening = false;
            // Opening a long list (all releases) builds hundreds of rows. Let the chevron and reveal start first, then
            // load at a lower priority; the expand motion picks up the new height when the rows arrive.
            expander.Expanded += (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, expander) || !Live(expander) || pulling || !source.IsSwapped || opening) return;
                opening = true;
                expander.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    opening = false;
                    if (!Live(expander) || !expander.IsExpanded || !source.IsSwapped) return;
                    source.PerformClick(); Refresh();
                }));
            };
            expander.Collapsed += (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, expander) || !Live(expander) || pulling || source.IsSwapped) return;
                source.PerformClick();
                Refresh();
            };
            Action innerPull = node.Pull;
            // While a deferred open is pending the source still reads collapsed; do not pull that stale state back.
            node.Pull = () => { innerPull(); if (opening) return; pulling = true; try { expander.IsExpanded = !source.IsSwapped; } finally { pulling = false; } };
        }
        return node;
    }

    private Node ProjectMinecraftChange(MyCard source, PageDownloadModify page)
    {
        var versionInfo = source.Children.OfType<Grid>().Single(grid => grid.Name == "PanMinecraftInfo");
        var current = versionInfo.Children.OfType<TextBlock>().Single();
        var compare = source.Children.OfType<Grid>().Single(grid => grid.Name == "PanMinecraftCompare");
        var versions = compare.Children.OfType<DockPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()).ToArray();
        var change = source.Children.OfType<Grid>().Single(grid => grid.Name == "PanMinecraftChange");
        var restore = source.Children.OfType<MyIconButton>().Single(button => button.Name == "BtnMinecraftRestore");

        var title = PlainText(source.Title);
        title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        var version = PlainText("");
        version.FontSize = 12.5; version.TextTrimming = TextTrimming.CharacterEllipsis; version.TextWrapping = TextWrapping.NoWrap;
        var badge = new Border { Child = version, MaxWidth = 260, Margin = new Thickness(10, 0, 12, 0), Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(11) };
        badge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSelected");
        var restoreSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title); Grid.SetColumn(badge, 1); header.Children.Add(badge);
        Grid.SetColumn(restoreSlot, 2); header.Children.Add(restoreSlot);

        var body = new StackPanel();
        var expander = new Expander { Name = "MinecraftVersionPicker", Style = Style("GlassPresentationExpander"), Header = header, Content = body };
        AutomationProperties.SetName(expander, "更换 Minecraft 版本");
        var search = new TextBox { Name = "MinecraftVersionSearch", Style = Style("GlassPresentationTextBox"), Height = 34 };
        AutomationProperties.SetName(search, "搜索 Minecraft 版本");
        var searchHint = PlainText("搜索版本");
        searchHint.Margin = new Thickness(14, 0, 0, 0); searchHint.VerticalAlignment = VerticalAlignment.Center;
        searchHint.IsHitTestVisible = false;
        searchHint.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var searchHost = new Grid { Margin = new Thickness(0, 2, 0, 8), Children = { search, searchHint } };
        BindTextPlaceholder(search, searchHint);
        body.Children.Add(searchHost);
        var list = new ListBox { Name = "MinecraftVersionOptions", Style = Style("GlassPresentationPickerList"), MaxHeight = 260, BorderThickness = new Thickness(), Background = Brushes.Transparent };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, "可选 Minecraft 版本");
        body.Children.Add(list);
        var empty = PlainText("没有符合条件的版本");
        empty.Margin = new Thickness(10, 8, 10, 8); empty.Visibility = Visibility.Collapsed;
        body.Children.Add(empty);

        JArray? catalog = null;
        MinecraftVersionChoice[] choices = Array.Empty<MinecraftVersionChoice>();
        void FillChoices()
        {
            if (!ReferenceEquals(catalog, page.MinecraftVersions))
            {
                catalog = page.MinecraftVersions;
                choices = catalog?.OfType<JObject>()
                    .Where(entry => !string.IsNullOrWhiteSpace((string?)entry["id"]) && !string.IsNullOrWhiteSpace((string?)entry["url"]))
                    .Select(entry => new MinecraftVersionChoice((string)entry["id"]!, entry)).ToArray() ?? Array.Empty<MinecraftVersionChoice>();
            }
            string query = search.Text.Trim();
            var filtered = query.Length == 0 ? choices : choices.Where(choice => choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = filtered;
            empty.Text = choices.Length == 0 ? "版本列表暂不可用" : "没有符合条件的版本";
            empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Choose(MinecraftVersionChoice choice)
        {
            if (!Live(expander) || !expander.IsExpanded || !expander.IsEnabled || change.Visibility != Visibility.Visible ||
                !ReferenceEquals(catalog, page.MinecraftVersions) || !page.ChooseMinecraftInPlace(choice.Entry)) return;
            expander.IsExpanded = false;
            Refresh();
        }
        var itemButton = new FrameworkElementFactory(typeof(Button));
        itemButton.SetValue(FrameworkElement.StyleProperty, Style("GlassPresentationBandRow"));
        itemButton.SetValue(FrameworkElement.MinHeightProperty, 32d);
        itemButton.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        itemButton.SetValue(Control.PaddingProperty, new Thickness(10, 0, 10, 0));
        itemButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(MinecraftVersionChoice.Name)));
        itemButton.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Button { DataContext: MinecraftVersionChoice choice }) Choose(choice);
        }));
        list.ItemTemplate = new DataTemplate { VisualTree = itemButton };
        list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || args.OriginalSource is Button || list.SelectedItem is not MinecraftVersionChoice choice) return;
            args.Handled = true; Choose(choice);
        };
        search.TextChanged += (_, _) => { if (expander.IsExpanded) FillChoices(); };
        expander.Expanded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, expander) || !Live(expander)) return;
            FillChoices();
        };
        var surface = new GlassSurface { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 10, 18, 10),
            Child = expander, BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return new Node(surface) { Pull = () =>
        {
            version.Text = compare.Visibility == Visibility.Visible ? string.Join(" → ", versions.Select(label => label.Text)) : current.Text;
            badge.ToolTip = version.Text;
            bool canChange = change.Visibility == Visibility.Visible && source.IsHitTestVisible;
            expander.IsEnabled = canChange;
            if (!canChange) expander.IsExpanded = false;
            else if (expander.IsExpanded && !ReferenceEquals(catalog, page.MinecraftVersions)) FillChoices();
            UIElement projectedRestore = Project(restore, true);
            if (projectedRestore is FrameworkElement item)
            {
                item.Margin = new Thickness(6, 0, 0, 0);
                item.Width = item.Height = item.MinHeight = 30;
            }
            if (!ReferenceEquals(restoreSlot.Content, projectedRestore)) restoreSlot.Content = projectedRestore;
        }};
    }

    private sealed record MinecraftVersionChoice(string Name, JObject Entry);

    private static readonly Geometry CheckGlyph = FrozenGeometry("M1,5 L4,8 10,1");
    private static readonly Geometry LockGlyph = FrozenGeometry("M2.5,6 V4 A3.5,3.5 0 0 1 9.5,4 V6 M1,6 H11 V12 H1 Z");
    private static Geometry FrozenGeometry(string data) { Geometry geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    /// <summary>
    /// Header for an install component card: title on the left and, on the right, one of three distinct states:
    /// selected (tinted pill with a check, the chosen version and a clear button), available ("可以添加", secondary) or
    /// unavailable (lock and the original reason; the title dims and the card cannot open).
    /// </summary>
    private (FrameworkElement Header, Action<Expander> Pull) InstallStatus(MyCard source, TextBlock title, Grid info, FrameworkElement clearSource)
    {
        TextBlock label = info.Children.OfType<TextBlock>().First();
        FrameworkElement image = info.Children.OfType<FrameworkElement>().FirstOrDefault(child => child is not TextBlock);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);

        var glyph = new Path { Width = 11, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var text = PlainText(string.Empty); text.FontSize = 12.5; text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis; text.MaxWidth = 260;
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
        pillContent.Children.Add(glyph); pillContent.Children.Add(text);
        var pill = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = pillContent, IsHitTestVisible = false };
        Grid.SetColumn(pill, 1); row.Children.Add(pill);

        if (clearSource is not null && Project(clearSource, true) is FrameworkElement clearView)
        {
            clearView.Margin = new Thickness(4, 0, 0, 0); clearView.VerticalAlignment = VerticalAlignment.Center;
            clearView.Width = clearView.Height = 26; clearView.MinHeight = 26;
            Grid.SetColumn(clearView, 2); row.Children.Add(clearView);
        }

        void Pull(Expander expander)
        {
            bool unavailable = source.MainSwap.Visibility != Visibility.Visible;
            bool selected = !unavailable && image is not null && image.Visibility == Visibility.Visible;
            text.Text = label.Text;
            pill.ToolTip = label.Text;
            if (selected)
            {
                pill.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSelected");
                text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText"); text.FontWeight = FontWeights.SemiBold;
                glyph.Data = CheckGlyph; glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationText");
                glyph.Visibility = Visibility.Visible;
            }
            else
            {
                pill.Background = Brushes.Transparent;
                text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); text.FontWeight = FontWeights.Normal;
                glyph.Data = LockGlyph; glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
                glyph.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
            }
            // An incompatible component reads as unavailable at a glance: the title dims and the header no longer opens.
            title.Opacity = unavailable ? .45 : 1;
            expander.IsHitTestVisible = !unavailable; expander.Focusable = !unavailable;
        }
        return (row, Pull);
    }

    private static PageDownloadResourceDetail DetailPage(DependencyObject element)
    {
        for (DependencyObject current = element; current is not null; current = LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null))
            if (current is PageDownloadResourceDetail page) return page;
        return null;
    }

    private Node ProjectSearch(MySearchBox source)
    {
        var host = new Grid { Height = 32, MinHeight = 32 };
        // Page search boxes sit on the wallpaper with no card beneath; a frosted plate keeps the field readable.
        var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
        frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        host.Children.Add(frost);
        var input = new TextBox { Style = Style("GlassPresentationSearchBox"), Name = string.IsNullOrEmpty(source.Name) ? string.Empty : source.Name + "Input" };
        Bind(input, TextBox.TextProperty, source.TextBox, nameof(TextBox.Text), BindingMode.TwoWay);
        ForwardKeys(input, source.TextBox);
        host.Children.Add(input);
        var searchIcon = new Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchIcon.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary"); host.Children.Add(searchIcon);
        var placeholder = PlainText(source.HintText); placeholder.Margin = new Thickness(35, 0, 32, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); host.Children.Add(placeholder);
        BindTextPlaceholder(input, placeholder);
        var clear = new Button { Content = "×", Style = Style("GlassPresentationRowButton"), Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(clear, "清空搜索"); clear.Click += (_, _) => { if (!Live(clear)) return; source.BtnClear.PerformClick(); Refresh(); }; host.Children.Add(clear);
        var node = new Node(host);
        node.Pull = () =>
        {
            input.ToolTip = source.HintText; placeholder.Text = source.HintText;
            clear.Visibility = string.IsNullOrEmpty(source.Text) ? Visibility.Collapsed : Visibility.Visible;
        };
        return node;
    }

    // Callout keeping the original hint's meaning: blue = information, yellow = warning, red = danger.
    // Tinted surface, a coloured bar on the left and a matching icon; text stays at full contrast.
    // "| 1.0.27 · …" (the classic list's lead-in) reads as stray punctuation here.
    private static string CleanLead(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string text = value.Trim();
        while (text.StartsWith("|", StringComparison.Ordinal)) text = text.Substring(1).TrimStart();
        return text.Replace(" | ", " · ");
    }

    // An untitled card whose content is only buttons (the resource pages' 打开文件夹 / 从文件安装 / 全选 card).
    private static bool IsActionCard(MyCard source)
    {
        if (!string.IsNullOrWhiteSpace(source.Title) || source.CanSwap || !InstanceContext(source)) return false;
        var content = source.Children.Cast<UIElement>().Skip(3).OfType<Panel>().ToList();
        if (content.Count == 0 || source.Children.Count - 3 != content.Count) return false;
        bool any = false;
        foreach (Panel panel in content)
            foreach (UIElement child in panel.Children)
            {
                if (child is not (MyButtonView or MyIconTextButtonView or MyTextButtonView)) return false;
                any = true;
            }
        return any;
    }

    private static bool InstanceContext(DependencyObject source)
    {
        for (DependencyObject item = source; item != null; item = LogicalTreeHelper.GetParent(item))
            if (item.GetType().Name is { } name && name.StartsWith("PageInstance", StringComparison.Ordinal) && name != "PageInstanceLeft") return true;
        return false;
    }

    private readonly Dictionary<MySearchBox, (GlassSurface Plate, StackPanel Inner)> _toolbars = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Projects a stack's children; a search box followed by an action card (the resource and restore-point pages) become
    /// one toolbar card: the search field on top, its actions in a row underneath.
    /// </summary>
    private UIElement[] ProjectWithToolbars(DependencyObject[] raw)
    {
        var views = new List<UIElement>(raw.Length);
        for (int index = 0; index < raw.Length; index++)
        {
            if (Project(raw[index]) is not UIElement view) continue;
            if (raw[index] is MySearchBox search)
            {
                int next = index + 1;
                var skipped = new List<UIElement>();
                while (next < raw.Length && raw[next] is UIElement { Visibility: not Visibility.Visible } hidden)
                {
                    if (Project(raw[next]) is UIElement hiddenView) skipped.Add(hiddenView);
                    next++;
                }
                if (next < raw.Length && raw[next] is MyCard card && IsActionCard(card) && Project(card) is UIElement actions)
                {
                    views.Add(Toolbar(search, view, actions));
                    views.AddRange(skipped); // still projected (they may show later), after the toolbar
                    index = next;
                    continue;
                }
            }
            views.Add(view);
        }
        return views.ToArray();
    }

    private UIElement Toolbar(MySearchBox search, UIElement field, UIElement actions)
    {
        if (!_toolbars.TryGetValue(search, out var toolbar))
        {
            var inner = new StackPanel();
            var plate = new GlassSurface { Child = inner, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 10, 10, 2), BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
            plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
            plate.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
            toolbar = (plate, inner);
            _toolbars[search] = toolbar;
        }
        foreach (UIElement part in new[] { field, actions })
            if (part is FrameworkElement { Parent: Panel owner } && !ReferenceEquals(owner, toolbar.Inner)) owner.Children.Remove(part);
        Children(toolbar.Inner, new[] { field, actions });
        if (field is FrameworkElement searchView) searchView.Margin = new Thickness(0);
        if (actions is GlassSurface actionPlate)
        {
            // The toolbar's plate already frosts the wallpaper: the action row sits on it without a plate of its own.
            actionPlate.Background = Brushes.Transparent; actionPlate.BorderThickness = new Thickness(0); actionPlate.Padding = new Thickness(0);
            actionPlate.BlurRadius = 0; actionPlate.PaintsWallpaper = false; actionPlate.Margin = new Thickness(0, 10, 0, 0);
        }
        toolbar.Plate.Margin = new Thickness(0, 0, 0, 18);
        return toolbar.Plate;
    }

    // The action card as a toolbar: its buttons in one wrapping row of compact pills, no plate around them.
    private Node ProjectActionCard(MyCard source)
    {
        var row = new WrapPanel();
        var plate = new GlassSurface { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 10, 2, 2), BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        plate.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        var node = new Node(plate);
        node.Pull = () =>
        {
            var buttons = source.Children.Cast<UIElement>().Skip(3).OfType<Panel>()
                .SelectMany(panel => panel.Children.OfType<DependencyObject>())
                .Select(child => Project(child)).Where(child => child != null).ToArray();
            Children(row, buttons);
            foreach (FrameworkElement button in row.Children.OfType<FrameworkElement>())
            {
                button.Margin = new Thickness(0, 0, 8, 8);
                button.Height = 34; button.MinHeight = 34; button.MinWidth = 0;
                if (button is Control control) { control.Padding = new Thickness(16, 0, 16, 0); control.FontSize = 13.5; }
            }
        };
        return node;
    }

    private Node ProjectHint(MyHintView source)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
        var disc = new Ellipse { StrokeThickness = 1.4, Fill = Brushes.Transparent }; var glyph = new Path { Stroke = Brushes.White, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        badge.Children.Add(disc); badge.Children.Add(glyph); grid.Children.Add(badge);
        var text = PlainText(source.LabText.Text); Bind(text, TextBlock.TextProperty, source.LabText, nameof(TextBlock.Text));
        text.VerticalAlignment = VerticalAlignment.Center; text.FontSize = 13; text.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(text, 1); grid.Children.Add(text);
        var close = new Button { Style = Style("GlassPresentationRowButton"), Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(8), Margin = new Thickness(12, -4, -6, -4), VerticalAlignment = VerticalAlignment.Top,
            Content = new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } };
        ((Path)close.Content).SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        AutomationProperties.SetName(close, "关闭提示"); close.Click += (_, _) => { if (!Live(close)) return; source.BtnClose.PerformClick(); Refresh(); }; Grid.SetColumn(close, 2); grid.Children.Add(close);
        var body = new Border { Padding = new Thickness(14, 10, 12, 10), Child = grid };
        var surface = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(.7), Child = body };
        surface.MouseLeftButtonUp += (_, args) => { if (Live(surface) && args.OriginalSource is not Button) { source.PerformClick(); Refresh(); } };
        MyHintView.Themes? applied = null;
        var node = new Node(surface);
        node.Pull = () =>
        {
            close.Visibility = source.CanClose ? Visibility.Visible : Visibility.Collapsed;
            if (applied == source.Theme) return;
            applied = source.Theme;
            bool info = source.Theme == MyHintView.Themes.Blue;
            Color tone = source.Theme switch { MyHintView.Themes.Yellow => Color.FromRgb(255, 176, 32), _ => Color.FromRgb(255, 69, 58) };
            if (SystemParameters.HighContrast || info)
            {
                // Information: the field's neutral glass, a hairline edge and the secondary text colour.
                surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
                surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationControlStroke");
                disc.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
                glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
                text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            }
            else
            {
                surface.Background = new SolidColorBrush(Color.FromArgb(26, tone.R, tone.G, tone.B));
                surface.BorderBrush = new SolidColorBrush(Color.FromArgb(60, tone.R, tone.G, tone.B));
                disc.Stroke = glyph.Stroke = new SolidColorBrush(tone);
                text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
            }
            glyph.Data = Geometry.Parse(source.Theme == MyHintView.Themes.Blue ? "M9,8 V13 M9,5.4 V5.6" : "M9,5 V10 M9,12.6 V12.8");
            AutomationProperties.SetHelpText(surface, source.Theme switch { MyHintView.Themes.Blue => "提示", MyHintView.Themes.Yellow => "注意", _ => "警告" });
        };
        return node;
    }
    private Node ProjectLoading(MyLoadingView source)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        // Indeterminate work shows the block-curve loader; known progress keeps a thin accent bar under it.
        var loader = new BlockCurveLoader { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
        loader.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent"); stack.Children.Add(loader);
        // Loading failed: the hexagon writes an × (it replays each time the failure appears).
        var failed = new GlassStatusGlyph(GlassStatusGlyph.Kind.Failed, 52) { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        stack.Children.Add(failed);
        var progress = new ProgressBar { Width = 140, Height = 3, Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(), Visibility = Visibility.Collapsed };
        progress.SetResourceReference(RangeBase.ForegroundProperty, "GlassPresentationAccent"); progress.SetResourceReference(Control.BackgroundProperty, "GlassPresentationTrack"); stack.Children.Add(progress);
        var text = PlainText(source.Text); text.Margin = new Thickness(0, 14, 0, 0); text.TextAlignment = TextAlignment.Center; stack.Children.Add(text);
        // The loading notice sits on its own frosted plate so it reads over any wallpaper.
        var plate = new GlassSurface { Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 20, 28, 20), BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill"); plate.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        stack.Margin = new Thickness();
        var retry = new Button { Content = plate, Style = Style("GlassPresentationRowButton"), HorizontalAlignment = HorizontalAlignment.Center };
        retry.Click += (_, _) => { if (!Live(retry)) return; source.PerformClick(); Refresh(); };
        Bind(text, TextBlock.TextProperty, source.LabText, nameof(TextBlock.Text));
        var node = new Node(retry); node.Pull = () =>
        {
            if (!source.IsLoaded) return;
            bool running = source.State.LoadingState == MyLoadingView.MyLoadingState.Run;
            loader.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            loader.IsActive = running;
            failed.Visibility = source.State.LoadingState == MyLoadingView.MyLoadingState.Error ? Visibility.Visible : Visibility.Collapsed;
            progress.Maximum = 1; progress.Value = source.State.Progress;
            progress.Visibility = running && source.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        };
        return node;
    }
    private Node ProjectModRow(MyLocalModItem source)
    {
        bool resourceRow = LogicalTreeHelper.GetParent(source) is StackPanel list && IsInstanceResourceList(list);
        bool waitingForDetails = resourceRow && LazyLoadBehavior.HasViewportAction(source);
        string localName = resourceRow ? System.IO.Path.GetFileNameWithoutExtension(source.Entry?.EnabledName ?? source.ResourceEntry?.Name ?? "") : "";
        var row = new Grid { MinHeight = 52 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var checkbox = new CheckBox { Style = Style("GlassPresentationRowSelector"), Width = 28, Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(checkbox, "选择资源"); row.Children.Add(checkbox);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var title = PlainText(source.Title); var description = PlainText(source.Description);
        title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        description.FontSize = 12; description.Margin = new Thickness(0, 2, 0, 0); description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        stack.Children.Add(title); stack.Children.Add(description);
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RoundIcon(image);
        if (resourceRow)
        {
            var icon = new MultiBinding { Converter = LocalResourceIcon.Instance, Mode = BindingMode.OneWay };
            icon.Bindings.Add(new Binding(nameof(UIElement.Visibility)) { Source = source.ResourceLogo });
            icon.Bindings.Add(new Binding { Source = source.ResourceLogo, Path = new PropertyPath(Image.SourceProperty) });
            icon.Bindings.Add(new Binding { Source = source.PathLogo, Path = new PropertyPath(Image.SourceProperty) });
            image.SetBinding(Image.SourceProperty, icon);
        }
        var detailsContent = new Grid(); detailsContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); detailsContent.ColumnDefinitions.Add(new ColumnDefinition()); detailsContent.Children.Add(image); detailsContent.Children.Add(stack); Grid.SetColumn(stack, 1);
        var details = new Button { Style = Style("GlassPresentationBandRow"), Content = detailsContent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 52 };
        Grid.SetColumn(details, 1); row.Children.Add(details);
        HoverBand(row);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(actions, "资源操作");
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        var separator = RowSeparator(76); Grid.SetColumnSpan(separator, 3); row.Children.Add(separator);
        RoutedEventHandler selection = (_, _) => { if (!Live(checkbox) || source.Checked == (checkbox.IsChecked == true)) return; source.EnsureActions(); source.Checked = checkbox.IsChecked == true; Refresh(); };
        checkbox.Checked += selection; checkbox.Unchecked += selection;
        details.Click += (_, _) => { if (!Live(details)) return; source.PerformClick(); Refresh(); };
        ForwardContextClick(details, source, source.EnsureActions);
        ForwardContextClick(checkbox, source, source.EnsureActions);
        var node = new Node(row);
        bool pointerInside = false, keyboardReveal = false;
        void UpdateActionVisibility()
        {
            bool reveal = pointerInside || (keyboardReveal && row.IsKeyboardFocusWithin);
            actions.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            if (!reveal || !Live(row) || source.Buttons is not null) return;
            source.EnsureActions();
            node.Pull?.Invoke();
        }
        row.MouseEnter += (_, _) => { pointerInside = true; UpdateActionVisibility(); };
        row.MouseLeave += (_, _) => { pointerInside = false; UpdateActionVisibility(); };
        row.PreviewMouseDown += (_, _) => { keyboardReveal = false; UpdateActionVisibility(); };
        row.PreviewKeyDown += (_, _) => { keyboardReveal = true; UpdateActionVisibility(); };
        row.IsKeyboardFocusWithinChanged += (_, args) =>
        {
            keyboardReveal = args.NewValue is true && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
            UpdateActionVisibility();
        };
        details.PreviewMouseLeftButtonDown += (_, args) => {
            if (!Live(details) || args.ClickCount != 2 || source.ResourceFolderRequested is null || source.ResourceEntry?.IsDirectory != true) return;
            source.PerformFolderOpen(); args.Handled = true; Refresh();
        };
        details.PreviewKeyDown += (_, args) => {
            if (!Live(details) || args.Key != Key.Enter || source.ResourceFolderRequested is null || source.ResourceEntry?.IsDirectory != true) return;
            source.PerformFolderOpen(); args.Handled = true; Refresh();
        };
        node.Cleanup.Add(() => ToggleFeedback.SetIsEnabled(checkbox, false));
        void PullDetails()
        {
            title.Text = string.IsNullOrWhiteSpace(source.Title) ? localName : source.Title;
            title.TextDecorations = source.LabTitle.TextDecorations;
            description.Text = waitingForDetails && string.IsNullOrWhiteSpace(source.Title) ? "正在加载资源信息…" :
                string.Join(" · ", new[] { CleanLead(source.SubTitle), CleanLead(source.Description) }.Where(value => !string.IsNullOrWhiteSpace(value)));
            details.ToolTip = source.ToolTip as string ?? description.Text;
            AutomationProperties.SetName(details, title.Text);
            if (!resourceRow) image.Source = source.ResourceLogo.Visibility == Visibility.Visible ? source.ResourceLogo.Source : ((Image)source.PathLogo).Source;
        }
        if (waitingForDetails)
        {
            node.PrepareResourceRow = () => {
                LazyLoadBehavior.CompleteViewportAction(source);
                waitingForDetails = false;
                PullDetails();
                if (MotionPreferences.AllowsAnimation) detailsContent.Opacity = .65;
            };
            node.RevealResourceRow = () => {
                double opacity = detailsContent.Opacity;
                detailsContent.Opacity = 1;
                if (!MotionPreferences.AllowsAnimation) return;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(150);
                detailsContent.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                var offset = new TranslateTransform();
                detailsContent.RenderTransform = offset;
                offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            };
        }
        node.Pull = () =>
        {
            checkbox.IsChecked = source.Checked;
            PullDetails();
            var buttons = new List<DependencyObject> { source.BtnUpdate, source.BtnFavorite };
            if (source.Buttons != null) buttons.AddRange(source.Buttons);
            if (source.ButtonStack is Panel panel) buttons.AddRange(panel.Children.OfType<DependencyObject>());
            Children(actions, buttons.Distinct<DependencyObject>(ReferenceEqualityComparer.Instance).Select(item => Project(item)).Where(item => item != null).ToArray());
        };
        return node;
    }

    private sealed class LocalResourceIcon : IMultiValueConverter
    {
        internal static readonly LocalResourceIcon Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            values[0] is Visibility.Visible && values[1] is ImageSource resourceIcon ? resourceIcon : values[2] as ImageSource;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private Node ProjectScreenshot(MyScreenshotItem source)
    {
        Node node = ProjectGrid(source);
        var grid = (Grid)node.View;
        grid.Focusable = true;
        bool SelectionControl(object origin) {
            for (DependencyObject item = origin as DependencyObject; item != null && !ReferenceEquals(item, grid); item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
                if (item is ToggleButton) return true;
            return false;
        }
        grid.PreviewMouseLeftButtonDown += (_, args) => {
            if (!Live(grid) || SelectionControl(args.OriginalSource)) return;
            grid.Focus(); source.PerformClick(args.ClickCount); args.Handled = true; Refresh();
        };
        grid.PreviewKeyDown += (_, args) => {
            if (!Live(grid) || args.Key != Key.Enter || SelectionControl(args.OriginalSource)) return;
            source.PerformClick(2); args.Handled = true; Refresh();
        };
        Action pull = node.Pull;
        node.Pull = () => { pull(); AutomationProperties.SetName(grid, source.Entry?.Name ?? "截图"); };
        return node;
    }

    private Node ProjectResourceRow(MyResourceItem source)
    {
        var row = new Grid { MinHeight = 52 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }; content.Children.Add(image);
        RoundIcon(image);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(labels, 1); content.Children.Add(labels);
        var title = PlainText(string.Empty); title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(title);
        var metadata = PlainText(string.Empty); metadata.FontSize = 12; metadata.Margin = new Thickness(0, 2, 0, 0); metadata.TextWrapping = TextWrapping.NoWrap; metadata.TextTrimming = TextTrimming.CharacterEllipsis; metadata.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); labels.Children.Add(metadata);
        var downloads = PlainText(string.Empty); downloads.FontSize = 12; downloads.Margin = new Thickness(16, 0, 8, 0); downloads.VerticalAlignment = VerticalAlignment.Center; downloads.TextWrapping = TextWrapping.NoWrap; downloads.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); Grid.SetColumn(downloads, 2); content.Children.Add(downloads);
        var details = new Button { Style = Style("GlassPresentationListAction"), Content = content, Height = 52, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var descriptionTip = new TextBlock { MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
        details.ToolTip = new ToolTip { Content = descriptionTip, MaxWidth = 384 };
        details.Click += (_, _) => { if (!Live(details)) return; source.PerformClick(); Refresh(); };
        details.Style = Style("GlassPresentationBandRow"); HoverBand(row);
        row.Children.Add(details);
        // Download lists drop the trailing download count; the favourite button stays visible.
        bool downloadRow = DownloadContext(source);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 1); row.Children.Add(actions);
        var separator = RowSeparator(52); Grid.SetColumnSpan(separator, 2); row.Children.Add(separator);
        string lastTitle = null, lastTags = null;
        var node = new Node(row);
        node.Pull = () =>
        {
            // Tags is setter-only; its original presentation contains the actual translated
            // labels. Copy their text into new inline badges, never their visual instances.
            string[] tags = source.PanTags.Visibility == Visibility.Visible
                ? source.PanTags.Children.OfType<FrameworkElement>().Where(element => element.Visibility == Visibility.Visible)
                    .Select(element => element is TextBlock text ? text.Text : (element as Border)?.Child is TextBlock label ? label.Text : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            string textTitle = source.Title ?? string.Empty, signature = string.Join("\u001f", tags);
            if (lastTitle != textTitle || lastTags != signature)
            {
                lastTitle = textTitle; lastTags = signature; title.Inlines.Clear(); title.Inlines.Add(new Run(textTitle));
                foreach (string tag in tags)
                {
                    var text = PlainText(tag); text.FontSize = 10; text.FontWeight = FontWeights.Normal; text.TextWrapping = TextWrapping.NoWrap;
                    text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                    var badge = new Border { Child = text, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(7, 0, 0, 0) };
                    badge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
                    title.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
                }
            }
            string platform = source.LabSource.Text?.Trim();
            if (string.IsNullOrWhiteSpace(platform) && source.Tag is ResourceProject project)
                platform = project.Platform == PCLCS.ResourcePlatforms.Modrinth ? "Modrinth" : project.Platform == PCLCS.ResourcePlatforms.CurseForge ? "CurseForge" : string.Empty;
            metadata.Text = string.Join(" · ", new[] { source.LabVersion.Text, platform }.Where(value => !string.IsNullOrWhiteSpace(value)));
            downloads.Text = string.IsNullOrWhiteSpace(source.LabDownload.Text) ? string.Empty : source.LabDownload.Text.Trim() + " 下载";
            downloads.Visibility = downloadRow || downloads.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            image.Source = ((Image)source.PathLogo).Source;
            // The detail page's project header is display-only rather than unavailable: keep it undimmed and simply inert.
            // Other non-interactive rows stay disabled, matching the original guard.
            bool header = !source.CanInteraction && DetailPage(source) is not null;
            details.SetCurrentValue(UIElement.IsEnabledProperty, source.IsEnabled && (source.CanInteraction || header));
            details.IsHitTestVisible = source.CanInteraction;
            descriptionTip.Text = string.Join("\n", new[] { textTitle, source.SubTitle, source.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));
            AutomationProperties.SetName(details, textTitle ?? string.Empty);
            Children(actions, new[] { Project(source.BtnFavorite) }.Where(action => action != null).ToArray());
        };
        return node;
    }

    // Edge-to-edge hover band behind the whole row (all columns); it fades in and out on a short spring.
    private static void HoverBand(Grid row)
    {
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        Grid.SetColumnSpan(band, Math.Max(1, row.ColumnDefinitions.Count));
        row.Children.Insert(0, band);
        row.Background = Brushes.Transparent; // the whole row, not just the text, tracks the pointer
        var fade = new SpringMotion(row, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new SpringParameters(.18, 1));
        row.MouseEnter += (_, _) => fade.Retarget(1);
        row.MouseLeave += (_, _) => fade.Retarget(0);
    }

    private Node ProjectText(TextBlock source)
    {
        var text = PlainText(source.Text);
        Bind(text, TextBlock.TextProperty, source, nameof(source.Text));
        text.TextWrapping = source.TextWrapping; text.TextTrimming = source.TextTrimming; text.TextAlignment = source.TextAlignment;
        text.FontSize = Math.Clamp(source.FontSize, 11, 28); text.FontWeight = source.FontWeight;
        if (source.Opacity < .9 && source.Opacity > 0) text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        if (source.Name is "LabValidation" or "LabCloneValidation")
            text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
        if (InPage<ModDependencyGraphView>(source) && source.FontWeight != FontWeights.SemiBold)
            text.SetResourceReference(TextBlock.ForegroundProperty,
                source.Tag is ModDependencyGraphNodeData { IsConflict: true } or ModDependencyGraphNodeData { NeedsAttention: true }
                    ? "GlassPresentationWarnText" : "GlassPresentationSecondary");
        // Message dialog title: a calm semibold heading; red for warnings.
        if (source.Name == "LabTitle" && AncestorMessage(source))
        {
            text.FontSize = 20; text.FontWeight = FontWeights.SemiBold;
            text.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            if (GlassModalState.IsWarningAncestor(source)) text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationWarnText");
        }
        return new Node(text);
    }
    private TextBlock PlainText(string value) { var text = new TextBlock { Text = value ?? string.Empty }; TextStyle(text); return text; }
    private Node ProjectBitmap(Image source)
    {
        var image = new Image { Stretch = source.Stretch, StretchDirection = source.StretchDirection };
        // Small square pictures on pages (resource / mod icons, author avatars) get the same soft corners as list icons.
        image.SizeChanged += (_, args) =>
        {
            Size size = args.NewSize;
            image.Clip = size.Width > 0 && size.Width <= 128 && Math.Abs(size.Width - size.Height) < 2
                ? new RectangleGeometry(new Rect(size), size.Width * .2, size.Width * .2) : null;
        };
        var node = new Node(image); node.Pull = () => {
            image.Source = source.Source;
            BitmapScalingMode scaling = RenderOptions.GetBitmapScalingMode(source);
            RenderOptions.SetBitmapScalingMode(image, scaling);
            image.SnapsToDevicePixels = source.SnapsToDevicePixels || scaling == BitmapScalingMode.NearestNeighbor;
            image.UseLayoutRounding = source.UseLayoutRounding || scaling == BitmapScalingMode.NearestNeighbor;
        };
        return node;
    }
    private Node ProjectShape(Shape source)
    {
        Shape target = source switch {
            Path path => new Path { Data = path.Data }, Polygon polygon => new Polygon { Points = polygon.Points },
            Ellipse => new Ellipse(), Rectangle rectangle => new Rectangle { RadiusX = Math.Max(8, rectangle.RadiusX), RadiusY = Math.Max(8, rectangle.RadiusY) },
            Line line => new Line { X1 = line.X1, X2 = line.X2, Y1 = line.Y1, Y2 = line.Y2 }, _ => new Path()
        };
        target.Stretch = source.Stretch; target.StrokeThickness = source.StrokeThickness;
        target.Fill = source.Fill; target.Stroke = source.Stroke;
        target.StrokeDashArray = source.StrokeDashArray;
        if (source.Tag is ModDependencyGraphRelation relation)
        {
            string brush = relation switch {
                ModDependencyGraphRelation.Conflict => "GlassPresentationWarnText",
                ModDependencyGraphRelation.Candidate => "GlassPresentationSecondary",
                _ => "GlassPresentationAccent"
            };
            target.SetResourceReference(target is Polygon ? Shape.FillProperty : Shape.StrokeProperty, brush);
        }
        var node = new Node(target);
        node.Pull = () => { if (target is Path path && source is Path original) path.Data = original.Data; };
        return node;
    }

    private Node ProjectNativeButton(Button source)
    {
        var button = new Button { Style = Style("GlassPresentationButton"), Command = source.Command, CommandParameter = source.CommandParameter, CommandTarget = source.CommandTarget };
        button.Click += (_, _) => { if (!Live(button)) return; source.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, source)); Refresh(); };
        var node = new Node(button); node.Pull = () => button.Content = Content(source.Content);
        return node;
    }
    private Node ProjectNativeToggle(ToggleButton source)
    {
        ToggleButton toggle = source is CheckBox ? new CheckBox { Style = Style(SettingsContext(source) ? "GlassPresentationSettingsCheckBox" : "GlassPresentationCheckBox") } : new RadioButton { Style = Style("GlassPresentationRadioButton") };
        Bind(toggle, ToggleButton.IsCheckedProperty, source, nameof(source.IsChecked), BindingMode.TwoWay);
        toggle.Click += (_, _) => { if (Live(toggle)) source.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, source)); };
        var node = new Node(toggle); node.Pull = () => toggle.Content = Content(source.Content);
        if (toggle is CheckBox checkbox) InstallSwitchMotion(checkbox, node);
        return node;
    }
    private Node ProjectNativeSlider(Slider source)
    {
        var slider = new Slider { Style = Style("GlassPresentationSlider") };
        Bind(slider, RangeBase.ValueProperty, source, nameof(source.Value), BindingMode.TwoWay);
        Bind(slider, RangeBase.MinimumProperty, source, nameof(source.Minimum)); Bind(slider, RangeBase.MaximumProperty, source, nameof(source.Maximum));
        return new Node(slider);
    }
    private Node ProjectProgress(ProgressBar source)
    {
        var progress = new ProgressBar { BorderThickness = new Thickness(), Height = 4, Background = Brushes.Transparent };
        progress.SetResourceReference(Control.ForegroundProperty, "GlassPresentationAccent");
        Bind(progress, RangeBase.ValueProperty, source, nameof(source.Value)); Bind(progress, RangeBase.MinimumProperty, source, nameof(source.Minimum)); Bind(progress, RangeBase.MaximumProperty, source, nameof(source.Maximum)); Bind(progress, ProgressBar.IsIndeterminateProperty, source, nameof(source.IsIndeterminate));
        return new Node(progress);
    }
    private Node ProjectPassword(PasswordBox source)
    {
        var password = new PasswordBox { Style = Style("GlassPresentationPasswordBox"), MaxLength = source.MaxLength };
        bool pulling = false;
        password.PasswordChanged += (_, _) => { if (Live(password) && !pulling) source.Password = password.Password; };
        var node = new Node(password); node.Pull = () => { pulling = true; try { if (password.Password != source.Password) password.Password = source.Password; } finally { pulling = false; } };
        return node;
    }

    private ContextMenu ProjectContextMenu(ContextMenu source, FrameworkElement target)
    {
        var menu = GlassActionMenu.FromContextMenu(source, target, (_root.Resources["GlassPresentationText"] as SolidColorBrush)?.Color is { } color && color.R + color.G + color.B < 480);
        menu.CanActivateOwner = () => Live(target);
        return menu;
    }
}
