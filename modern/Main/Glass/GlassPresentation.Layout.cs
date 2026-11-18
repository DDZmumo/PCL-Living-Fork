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
using System.Windows.Shapes;
using PCLLF.Presentation.Controls;
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
            MyCard card => ProjectCard(card),
            MyHintView hint => ProjectHint(hint),
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
            ListBox list => ProjectListBox(list),
            TextBlock text => ProjectText(text),
            Image image => ProjectBitmap(image),
            Shape shape => ProjectShape(shape),
            ScrollViewer scroll => ProjectScroll(scroll),
            Grid { Name: "PanRamDisplay" } memory => ProjectMemoryMeter(memory),
            Grid grid => ProjectGrid(grid),
            StackPanel stack => ProjectStack(stack),
            WrapPanel wrap => ProjectWrap(wrap),
            DockPanel dock => ProjectDock(dock),
            Canvas canvas => ProjectCanvas(canvas),
            Border border => ProjectBorder(border),
            Decorator decorator => ProjectDecorator(decorator),
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
            if (!double.IsNaN(source.Height) && source.Height < 38 && source.Children.OfType<FrameworkElement>().Any(Interactive)) grid.MinHeight = 40;
            Children(grid, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
            // Java list: the original paints its summary over an empty drop-down; keep both in one full-width field.
            if (source.Name == "PanAdvanceJava")
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

    private static bool Interactive(FrameworkElement element) => element is MyButtonView or MyCheckBoxView or MyRadioBoxView or MySliderView or MyListItemView or TextBoxBase or Selector or ButtonBase or PasswordBox;
    // Wrapped editors (for example a Grid hosting a ComboBox and size inputs) still need the projected control height.
    private static bool ContainsInteractive(FrameworkElement element, int depth)
        => Interactive(element) || depth > 0 && element is Panel or Decorator or ContentControl &&
           LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>().Any(child => child.Visibility != Visibility.Collapsed && ContainsInteractive(child, depth - 1));

    private Node ProjectStack(StackPanel source)
    {
        var stack = new StackPanel { Orientation = source.Orientation };
        var node = new Node(stack);
        bool settingsSections = SettingsContext(source) && source.Orientation == Orientation.Vertical && source.Children.OfType<MyCard>().Any();
        node.Pull = () =>
        {
            Children(stack, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
            if (settingsSections)
            {
                stack.MaxWidth = 800;
                stack.HorizontalAlignment = HorizontalAlignment.Left;
                if (_root.ActualWidth > 12) stack.Width = Math.Min(800, _root.ActualWidth - 12);
            }
        };
        return node;
    }
    private Node ProjectWrap(WrapPanel source)
    {
        var wrap = new WrapPanel { Orientation = source.Orientation };
        var node = new Node(wrap);
        node.Pull = () => Children(wrap, source.Children.OfType<DependencyObject>().Select(child => Project(child)).Where(child => child != null).ToArray());
        return node;
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
            border = new GlassSurface {
                Padding = source.Padding, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(.8),
                BlurRadius = 22, HighlightOpacity = 0
            };
            // The modal projection receives its app-only scene snapshot from integration.
            // Never replace a missing/transparent snapshot with a direct desktop backdrop.
            GlassMaterial.SetDesktopBackdropEnabled(border, false);
            border.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
            border.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        }
        else border = new Border { Padding = source.Padding, CornerRadius = new CornerRadius(14) };
        var node = new Node(border);
        node.Pull = () => { UIElement child = Project(source.Child); if (!ReferenceEquals(border.Child, child)) border.Child = child; };
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
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = source.VerticalScrollBarVisibility, HorizontalScrollBarVisibility = source.HorizontalScrollBarVisibility, PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(scroll, true);
        double expectedSource = double.NaN, expectedTarget = double.NaN;
        ScrollChangedEventHandler changed = (_, args) =>
        {
            if (!Live(scroll) || !ReferenceEquals(args.OriginalSource, source) || Math.Abs(args.VerticalChange) < .01) return;
            if (!double.IsNaN(expectedSource) && Math.Abs(source.VerticalOffset - expectedSource) < 1) { expectedSource = double.NaN; return; }
            expectedSource = double.NaN;
            double fraction = source.ScrollableHeight <= 0 ? 0 : source.VerticalOffset / source.ScrollableHeight;
            expectedTarget = Math.Clamp(fraction * scroll.ScrollableHeight, 0, scroll.ScrollableHeight);
            scroll.ScrollToVerticalOffset(expectedTarget);
        };
        source.ScrollChanged += changed;
        scroll.ScrollChanged += (_, args) =>
        {
            if (!Live(scroll) || !ReferenceEquals(args.OriginalSource, scroll) || Math.Abs(args.VerticalChange) < .01) return;
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
            placeholder.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Refresh));
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
        var body = new StackPanel();
        bool settingsSection = SettingsContext(source) && !source.CanSwap && source.SwapControl is null;
        var header = PlainText(source.Title); header.FontSize = settingsSection ? 16 : 15; header.FontWeight = FontWeights.SemiBold; header.Margin = new Thickness(0, 0, 0, 12);
        Bind(header, TextBlock.TextProperty, source, nameof(source.Title));
        var stack = new StackPanel(); stack.Children.Add(header); stack.Children.Add(body);
        // Cards frost the wallpaper directly beneath them (their own blurred sample); the page background stays sharp.
        var surface = new GlassSurface { CornerRadius = new CornerRadius(settingsSection ? 10 : 14), Padding = settingsSection ? new Thickness(16, 12, 16, 14) : new Thickness(20), Child = stack, BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
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
        GlassResourceInfo detailInfo = null;
        // Install components (Forge, Fabric, OptiFine...): the original shows the chosen version, "可以添加" or the reason it
        // cannot be chosen in a small info row plus a clear button. Those move into the header (see InstallStatus).
        Grid installInfo = source.CanSwap ? source.Children.OfType<Grid>().FirstOrDefault(grid => grid.Name.StartsWith("Pan", StringComparison.Ordinal) && grid.Name.EndsWith("Info", StringComparison.Ordinal) && grid.Children.OfType<TextBlock>().Any()) : null;
        FrameworkElement installClear = installInfo is null ? null : source.Children.OfType<FrameworkElement>().FirstOrDefault(child => child.Name.EndsWith("Clear", StringComparison.Ordinal));
        node.Pull = () =>
        {
            string caption = new TextRange(source.MainTextBlock.ContentStart, source.MainTextBlock.ContentEnd).Text;
            header.SetCurrentValue(TextBlock.TextProperty, caption);
            header.Visibility = string.IsNullOrWhiteSpace(caption) ? Visibility.Collapsed : Visibility.Visible;
            var children = source.Children.Cast<UIElement>().Skip(3).Where(child => !ReferenceEquals(child, installInfo) && !ReferenceEquals(child, installClear)).OfType<DependencyObject>().ToList();
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
            expander.Expanded += (_, _) =>
            {
                if (!Live(expander) || pulling || !source.IsSwapped || opening) return;
                opening = true;
                expander.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    opening = false;
                    if (!Live(expander) || !expander.IsExpanded || !source.IsSwapped) return;
                    source.PerformClick(); Refresh();
                }));
            };
            expander.Collapsed += (_, _) => { if (Live(expander) && !pulling && !source.IsSwapped) { source.PerformClick(); Refresh(); } };
            Action innerPull = node.Pull;
            // While a deferred open is pending the source still reads collapsed; do not pull that stale state back.
            node.Pull = () => { innerPull(); if (opening) return; pulling = true; try { expander.IsExpanded = !source.IsSwapped; } finally { pulling = false; } };
        }
        return node;
    }

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
        var clear = new Button { Content = "×", Style = Style("GlassPresentationRowButton"), Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(clear, "清空搜索"); clear.Click += (_, _) => { if (!Live(clear)) return; source.BtnClear.PerformClick(); Refresh(); }; host.Children.Add(clear);
        var node = new Node(host);
        node.Pull = () =>
        {
            input.ToolTip = source.HintText; placeholder.Text = source.HintText;
            placeholder.Visibility = string.IsNullOrEmpty(source.Text) ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = string.IsNullOrEmpty(source.Text) ? Visibility.Collapsed : Visibility.Visible;
        };
        return node;
    }

    // Callout keeping the original hint's meaning: blue = information, yellow = warning, red = danger.
    // Tinted surface, a coloured bar on the left and a matching icon; text stays at full contrast.
    private Node ProjectHint(MyHintView source)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        var disc = new Ellipse(); var glyph = new Path { Stroke = Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        badge.Children.Add(disc); badge.Children.Add(glyph); grid.Children.Add(badge);
        var text = PlainText(source.LabText.Text); Bind(text, TextBlock.TextProperty, source.LabText, nameof(TextBlock.Text));
        text.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(text, 1); grid.Children.Add(text);
        var close = new Button { Style = Style("GlassPresentationRowButton"), Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(8), Margin = new Thickness(12, -4, -6, -4), VerticalAlignment = VerticalAlignment.Top,
            Content = new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } };
        ((Path)close.Content).SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        AutomationProperties.SetName(close, "关闭提示"); close.Click += (_, _) => { if (!Live(close)) return; source.BtnClose.PerformClick(); Refresh(); }; Grid.SetColumn(close, 2); grid.Children.Add(close);
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 10) };
        var body = new Border { Padding = new Thickness(16, 12, 14, 12), Child = grid };
        var frame = new Grid(); frame.Children.Add(body); frame.Children.Add(bar);
        var surface = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = frame };
        surface.MouseLeftButtonUp += (_, args) => { if (Live(surface) && args.OriginalSource is not Button) { source.PerformClick(); Refresh(); } };
        MyHintView.Themes? applied = null;
        var node = new Node(surface);
        node.Pull = () =>
        {
            close.Visibility = source.CanClose ? Visibility.Visible : Visibility.Collapsed;
            if (applied == source.Theme) return;
            applied = source.Theme;
            Color tone = source.Theme switch { MyHintView.Themes.Blue => Color.FromRgb(160, 160, 160), MyHintView.Themes.Yellow => Color.FromRgb(255, 176, 32), _ => Color.FromRgb(255, 69, 58) };
            if (SystemParameters.HighContrast) { surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill"); surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge"); }
            else
            {
                surface.Background = new SolidColorBrush(Color.FromArgb(30, tone.R, tone.G, tone.B));
                surface.BorderBrush = new SolidColorBrush(Color.FromArgb(70, tone.R, tone.G, tone.B));
            }
            bar.Background = disc.Fill = new SolidColorBrush(tone);
            glyph.Stroke = source.Theme == MyHintView.Themes.Yellow ? new SolidColorBrush(Color.FromRgb(40, 30, 0)) : Brushes.White;
            glyph.Data = Geometry.Parse(source.Theme == MyHintView.Themes.Blue ? "M10,8.6 V14 M10,5.8 V6.1" : "M10,5.6 V11 M10,13.9 V14.2");
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
            progress.Maximum = 1; progress.Value = source.State.Progress;
            progress.Visibility = running && source.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        };
        return node;
    }
    private Node ProjectModRow(MyLocalModItem source)
    {
        var row = new Grid { MinHeight = 52 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var checkbox = new CheckBox { Style = Style("GlassPresentationRowSelector"), Width = 28, Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(checkbox, "选择资源"); row.Children.Add(checkbox);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var title = PlainText(source.Title); var description = PlainText(source.Description);
        title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        description.FontSize = 12; description.Margin = new Thickness(0, 2, 0, 0); description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        stack.Children.Add(title); stack.Children.Add(description);
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var detailsContent = new Grid(); detailsContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); detailsContent.ColumnDefinitions.Add(new ColumnDefinition()); detailsContent.Children.Add(image); detailsContent.Children.Add(stack); Grid.SetColumn(stack, 1);
        var details = new Button { Style = Style("GlassPresentationBandRow"), Content = detailsContent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 52 };
        Grid.SetColumn(details, 1); row.Children.Add(details);
        HoverBand(row);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 2); row.Children.Add(actions);
        var separator = RowSeparator(76); Grid.SetColumnSpan(separator, 3); row.Children.Add(separator);
        RoutedEventHandler selection = (_, _) => { if (!Live(checkbox) || source.Checked == (checkbox.IsChecked == true)) return; source.EnsureActions(); source.Checked = checkbox.IsChecked == true; Refresh(); };
        checkbox.Checked += selection; checkbox.Unchecked += selection;
        details.Click += (_, _) => { if (!Live(details)) return; source.PerformClick(); Refresh(); };
        ForwardContextClick(details, source, source.EnsureActions);
        ForwardContextClick(checkbox, source, source.EnsureActions);
        row.MouseEnter += (_, _) => { if (!Live(row)) return; source.EnsureActions(); Refresh(); };
        row.IsKeyboardFocusWithinChanged += (_, args) => { if (args.NewValue is true && Live(row)) { source.EnsureActions(); Refresh(); } };
        details.PreviewMouseLeftButtonDown += (_, args) => {
            if (!Live(details) || args.ClickCount != 2 || source.ResourceFolderRequested is null || source.ResourceEntry?.IsDirectory != true) return;
            source.PerformFolderOpen(); args.Handled = true; Refresh();
        };
        details.PreviewKeyDown += (_, args) => {
            if (!Live(details) || args.Key != Key.Enter || source.ResourceFolderRequested is null || source.ResourceEntry?.IsDirectory != true) return;
            source.PerformFolderOpen(); args.Handled = true; Refresh();
        };
        var node = new Node(row);
        node.Cleanup.Add(() => ToggleFeedback.SetIsEnabled(checkbox, false));
        node.Pull = () =>
        {
            checkbox.IsChecked = source.Checked; title.Text = source.Title; title.TextDecorations = source.LabTitle.TextDecorations;
            description.Text = string.Join(" · ", new[] { source.SubTitle, source.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));
            details.ToolTip = source.ToolTip as string ?? description.Text;
            AutomationProperties.SetName(details, source.Title ?? string.Empty); // a mod without a parsed name has a null title
            image.Source = source.ResourceLogo.Visibility == Visibility.Visible ? source.ResourceLogo.Source : ((Image)source.PathLogo).Source;
            var buttons = new List<DependencyObject> { source.BtnUpdate, source.BtnFavorite };
            if (source.Buttons != null) buttons.AddRange(source.Buttons);
            if (source.ButtonStack is Panel panel) buttons.AddRange(panel.Children.OfType<DependencyObject>());
            Children(actions, buttons.Distinct<DependencyObject>(ReferenceEqualityComparer.Instance).Select(item => Project(item)).Where(item => item != null).ToArray());
        };
        return node;
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
        return new Node(text);
    }
    private TextBlock PlainText(string value) { var text = new TextBlock { Text = value ?? string.Empty }; TextStyle(text); return text; }
    private Node ProjectBitmap(Image source)
    {
        var image = new Image { Stretch = source.Stretch, StretchDirection = source.StretchDirection };
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
        Shape target = source switch { Path path => new Path { Data = path.Data }, Ellipse => new Ellipse(), Rectangle rectangle => new Rectangle { RadiusX = Math.Max(8, rectangle.RadiusX), RadiusY = Math.Max(8, rectangle.RadiusY) }, Line line => new Line { X1 = line.X1, X2 = line.X2, Y1 = line.Y1, Y2 = line.Y2 }, _ => new Path() };
        target.Stretch = source.Stretch; target.StrokeThickness = source.StrokeThickness;
        target.Fill = source.Fill; target.Stroke = source.Stroke;
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
