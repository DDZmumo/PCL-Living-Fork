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
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private Node CreateNode(DependencyObject source)
    {
        return source switch
        {
            MySearchBox search => ProjectSearch(search),
            FrameworkElement virtualElement when virtualElement is IControlVirtualElement virtualized => ProjectVirtual(virtualElement, virtualized),
            MyButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick),
            MyTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick),
            MyExtraTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick, readIcon: () => button.Path?.Data),
            MyIconButtonView button => ActionButton(button, null, button.PerformClick, iconOnly: true, readIcon: () => button.Path?.Data),
            MyIconTextButtonView button => ActionButton(button, nameof(button.Text), button.PerformClick, readIcon: () => button.ShapeLogo?.Data),
            MyCheckBox checkbox => ProjectCheckbox(checkbox),
            MyRadioBox radio => ProjectRadioBox(radio),
            MyRadioButton radio => ProjectRadioButton(radio),
            MySlider slider => ProjectSlider(slider),
            MyListItem row => ProjectListRow(row),
            MyLocalModItem mod => ProjectModRow(mod),
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
        bool settingsRow = SettingsContext(source) && source.ColumnDefinitions.Count == 2 && source.Children.Count == 2 &&
            source.Children.OfType<TextBlock>().Any(label => Grid.GetColumn(label) == 0) &&
            source.Children.OfType<FrameworkElement>().Any(editor => Grid.GetColumn(editor) == 1 && editor is MyComboBoxView or MySliderView or TextBoxBase or PasswordBox);
        if (source.Name == "PanMsg") grid.Background = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
        var node = new Node(grid);
        node.Pull = () =>
        {
            if (source.Name == "PanMsg") grid.Background = source.Children.OfType<UIElement>().Any(child => child.Visibility == Visibility.Visible) ? new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)) : Brushes.Transparent;
            while (grid.RowDefinitions.Count > source.RowDefinitions.Count) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
            while (grid.ColumnDefinitions.Count > source.ColumnDefinitions.Count) grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
            while (grid.RowDefinitions.Count < source.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition());
            while (grid.ColumnDefinitions.Count < source.ColumnDefinitions.Count) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int index = 0; index < source.RowDefinitions.Count; index++)
            {
                RowDefinition original = source.RowDefinitions[index], target = grid.RowDefinitions[index];
                bool controls = source.Children.OfType<FrameworkElement>().Any(child => Grid.GetRow(child) == index && Interactive(child));
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
            if (settingsRow)
            {
                foreach (FrameworkElement editor in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetColumn(child) == 1))
                {
                    editor.VerticalAlignment = VerticalAlignment.Center; editor.Width = double.NaN;
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

    private static bool Interactive(FrameworkElement element) => element is MyButtonView or MyCheckBoxView or MyRadioBoxView or MySliderView or MyListItemView or TextBoxBase or Selector or ButtonBase or PasswordBox;

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
            border = new Border { Padding = source.Padding, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(.8) };
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
        var node = new Node(scroll); node.Cleanup.Add(() => source.ScrollChanged -= changed);
        node.Pull = () => { object child = Content(source.Content); if (!ReferenceEquals(scroll.Content, child)) scroll.Content = child; };
        return node;
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

    private Node ProjectCard(MyCard source)
    {
        var body = new StackPanel();
        bool settingsSection = SettingsContext(source) && !source.CanSwap && source.SwapControl is null;
        var header = PlainText(source.Title); header.FontSize = settingsSection ? 16 : 15; header.FontWeight = FontWeights.SemiBold; header.Margin = new Thickness(0, 0, 0, 12);
        Bind(header, TextBlock.TextProperty, source, nameof(source.Title));
        var stack = new StackPanel(); stack.Children.Add(header); stack.Children.Add(body);
        var surface = new Border { CornerRadius = new CornerRadius(14), Padding = settingsSection ? new Thickness(14, 10, 14, 12) : new Thickness(20), Child = stack, BorderThickness = new Thickness(.7) };
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
        node.Pull = () =>
        {
            header.Visibility = string.IsNullOrWhiteSpace(source.Title) ? Visibility.Collapsed : Visibility.Visible;
            var children = source.Children.Cast<UIElement>().Skip(3).OfType<DependencyObject>().ToList();
            if (source.BorderChild is DependencyObject inner) children.Insert(0, inner);
            Children(body, children.Select(child => Project(child, true)).Where(child => child != null).ToArray());
        };
        if (source.CanSwap || source.SwapControl != null)
        {
            var expander = new Expander { Style = Style("GlassPresentationExpander"), Header = header, Content = body, IsExpanded = !source.IsSwapped, Padding = new Thickness() };
            stack.Children.Clear(); surface.Child = expander;
            bool pulling = false;
            expander.Expanded += (_, _) => { if (Live(expander) && !pulling && source.IsSwapped) { source.PerformClick(); Refresh(); } };
            expander.Collapsed += (_, _) => { if (Live(expander) && !pulling && !source.IsSwapped) { source.PerformClick(); Refresh(); } };
            Action innerPull = node.Pull;
            node.Pull = () => { innerPull(); pulling = true; try { expander.IsExpanded = !source.IsSwapped; } finally { pulling = false; } };
        }
        return node;
    }

    private Node ProjectSearch(MySearchBox source)
    {
        var host = new Grid { Height = 32, MinHeight = 32 };
        var input = new TextBox { Style = Style("GlassPresentationSearchBox"), Name = string.IsNullOrEmpty(source.Name) ? string.Empty : source.Name + "Input" };
        Bind(input, TextBox.TextProperty, source.TextBox, nameof(TextBox.Text), BindingMode.TwoWay);
        ForwardKeys(input, source.TextBox);
        host.Children.Add(input);
        var searchIcon = new Path { Data = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 M11,11 L16,16"), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchIcon.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary"); host.Children.Add(searchIcon);
        var placeholder = PlainText(source.HintText); placeholder.Margin = new Thickness(32, 0, 32, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); host.Children.Add(placeholder);
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

    private Node ProjectHint(MyHintView source)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = PlainText(source.LabText.Text); Bind(text, TextBlock.TextProperty, source.LabText, nameof(TextBlock.Text)); grid.Children.Add(text);
        var close = new Button { Content = "×", Style = Style("GlassPresentationButton"), Width = 36, MinHeight = 36, Padding = new Thickness(8), Margin = new Thickness(12, -3, -3, -3) };
        AutomationProperties.SetName(close, "关闭提示"); close.Click += (_, _) => { if (!Live(close)) return; source.BtnClose.PerformClick(); Refresh(); }; Grid.SetColumn(close, 1); grid.Children.Add(close);
        var surface = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Child = grid, BorderThickness = new Thickness(.7) };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill"); surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        surface.MouseLeftButtonUp += (_, args) => { if (Live(surface) && args.OriginalSource is not Button) { source.PerformClick(); Refresh(); } };
        var node = new Node(surface); node.Pull = () => close.Visibility = source.CanClose ? Visibility.Visible : Visibility.Collapsed;
        return node;
    }
    private Node ProjectLoading(MyLoadingView source)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        var progress = new ProgressBar { IsIndeterminate = true, Width = 140, Height = 3, Background = Brushes.Transparent, BorderThickness = new Thickness() }; progress.SetResourceReference(RangeBase.ForegroundProperty, "GlassPresentationText"); stack.Children.Add(progress);
        var text = PlainText(source.Text); text.Margin = new Thickness(0, 14, 0, 0); text.TextAlignment = TextAlignment.Center; stack.Children.Add(text);
        var retry = new Button { Content = stack, Style = Style("GlassPresentationRowButton") };
        retry.Click += (_, _) => { if (!Live(retry)) return; source.PerformClick(); Refresh(); };
        Bind(text, TextBlock.TextProperty, source.LabText, nameof(TextBlock.Text));
        var node = new Node(retry); node.Pull = () =>
        {
            if (!source.IsLoaded) return;
            progress.IsIndeterminate = !source.ShowProgress;
            progress.Maximum = 1; progress.Value = source.State.Progress;
            progress.Visibility = source.State.LoadingState == MyLoadingView.MyLoadingState.Run ? Visibility.Visible : Visibility.Collapsed;
        };
        return node;
    }
    private Node ProjectModRow(MyLocalModItem source)
    {
        var row = new Grid { MinHeight = 52 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var checkbox = new CheckBox { Style = Style("GlassPresentationRowSelector"), Width = 28, Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(checkbox, "选择资源"); row.Children.Add(checkbox);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var title = PlainText(source.Title); var description = PlainText(source.Description);
        title.FontSize = 14; title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        description.FontSize = 12; description.Margin = new Thickness(0, 2, 0, 0); description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        stack.Children.Add(title); stack.Children.Add(description);
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var detailsContent = new Grid(); detailsContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); detailsContent.ColumnDefinitions.Add(new ColumnDefinition()); detailsContent.Children.Add(image); detailsContent.Children.Add(stack); Grid.SetColumn(stack, 1);
        var details = new Button { Style = Style("GlassPresentationListAction"), Content = detailsContent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 52 };
        Grid.SetColumn(details, 1); row.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 2); row.Children.Add(actions);
        var separator = RowSeparator(76); Grid.SetColumnSpan(separator, 3); row.Children.Add(separator);
        RoutedEventHandler selection = (_, _) => { if (!Live(checkbox) || source.Checked == (checkbox.IsChecked == true)) return; source.Checked = checkbox.IsChecked == true; Refresh(); };
        checkbox.Checked += selection; checkbox.Unchecked += selection;
        details.Click += (_, _) => { if (!Live(details)) return; source.PerformClick(); Refresh(); };
        row.MouseEnter += (_, _) => { if (!Live(row)) return; source.RefreshColor(source, EventArgs.Empty); Refresh(); };
        var node = new Node(row); node.Pull = () =>
        {
            checkbox.IsChecked = source.Checked; title.Text = source.Title; title.TextDecorations = source.LabTitle.TextDecorations;
            description.Text = string.Join(" · ", new[] { source.SubTitle, source.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));
            details.ToolTip = source.ToolTip as string ?? description.Text;
            AutomationProperties.SetName(details, source.Title);
            image.Source = source.ResourceLogo.Visibility == Visibility.Visible ? source.ResourceLogo.Source : ((Image)source.PathLogo).Source;
            var buttons = new List<DependencyObject> { source.BtnUpdate, source.BtnFavorite };
            if (source.Buttons != null) buttons.AddRange(source.Buttons);
            if (source.ButtonStack is Panel panel) buttons.AddRange(panel.Children.OfType<DependencyObject>());
            Children(actions, buttons.Distinct<DependencyObject>(ReferenceEqualityComparer.Instance).Select(item => Project(item)).Where(item => item != null).ToArray());
        };
        return node;
    }

    private Node ProjectResourceRow(MyResourceItem source)
    {
        var row = new Grid { MinHeight = 56 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }; content.Children.Add(image);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(labels, 1); content.Children.Add(labels);
        var title = PlainText(source.Title); title.FontSize = 14; title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(title);
        var metadata = PlainText(source.Description); metadata.FontSize = 12; metadata.Margin = new Thickness(0, 2, 0, 0); metadata.TextWrapping = TextWrapping.NoWrap; metadata.TextTrimming = TextTrimming.CharacterEllipsis; metadata.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); labels.Children.Add(metadata);
        var time = PlainText(source.LabTime.Text); time.FontSize = 12; time.Margin = new Thickness(16, 0, 8, 0); time.VerticalAlignment = VerticalAlignment.Center; time.TextWrapping = TextWrapping.NoWrap; time.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); Grid.SetColumn(time, 2); content.Children.Add(time);
        var details = new Button { Style = Style("GlassPresentationListAction"), Content = content, Height = 56 };
        details.Click += (_, _) => { if (!Live(details)) return; source.PerformClick(); Refresh(); }; row.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 1); row.Children.Add(actions);
        var separator = RowSeparator(52); Grid.SetColumnSpan(separator, 2); row.Children.Add(separator);
        var node = new Node(row);
        node.Pull = () =>
        {
            title.Text = source.Title; time.Text = source.LabTime.Text;
            metadata.Text = string.Join(" · ", new[] { source.SubTitle, source.Description, source.LabVersion.Text, source.LabDownload.Text }.Where(value => !string.IsNullOrWhiteSpace(value)));
            image.Source = ((Image)source.PathLogo).Source;
            details.SetCurrentValue(UIElement.IsEnabledProperty, source.IsEnabled && source.CanInteraction);
            details.ToolTip = source.Description; AutomationProperties.SetName(details, source.Title);
            Children(actions, new[] { Project(source.BtnFavorite) }.Where(action => action != null).ToArray());
        };
        return node;
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
        ToggleButton toggle = source is CheckBox ? new CheckBox { Style = Style("GlassPresentationCheckBox") } : new RadioButton { Style = Style("GlassPresentationRadioButton") };
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
