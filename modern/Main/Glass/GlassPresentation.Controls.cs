// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private Node ActionButton(FrameworkElement source, string textPath, Action activate, string logo = null, bool iconOnly = false, Func<Geometry> readIcon = null, bool frosted = false)
    {
        var button = new Button { Style = Style(iconOnly ? "GlassPresentationRowButton" : "GlassPresentationButton") };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Path iconPath = null;
        if (readIcon != null || !string.IsNullOrWhiteSpace(logo) || iconOnly)
        {
            iconPath = (Path)Icon(logo); content.Children.Add(iconPath);
            if (iconOnly) iconPath.Margin = new Thickness();
        }
        var text = new TextBlock { Name = "LabText", VerticalAlignment = VerticalAlignment.Center };
        TextStyle(text);
        if (textPath != null) Bind(text, TextBlock.TextProperty, source, textPath);
        else text.Text = source.ToolTip as string ?? string.Empty;
        if (!iconOnly) content.Children.Add(text);
        button.Content = content;
        if (frosted)
        {
            // Floating page actions (e.g. 开始下载) sit over the wallpaper with no card beneath: give the button its own
            // frosted layer, filling the whole chrome behind the label, so the translucent fill reads over a blur.
            var frost = new GlassSurface { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0, IsHitTestVisible = false };
            frost.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
            button.Content = null; // release the label before re-parenting it under the frost
            // The frost fills the whole chrome: the button has no padding and stretches its content; the label carries
            // the spacing itself and stays centred.
            content.Margin = new Thickness(16, 6, 16, 6);
            content.HorizontalAlignment = HorizontalAlignment.Center; content.VerticalAlignment = VerticalAlignment.Center;
            var layered = new Grid();
            layered.Children.Add(frost); layered.Children.Add(content);
            button.Content = layered;
            button.Padding = new Thickness();
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
            button.MinHeight = 38; button.FontWeight = FontWeights.SemiBold;
            // The frost covers the template's own hover layer, so hover lightens the frost itself.
            button.MouseEnter += (_, _) => frost.Opacity = .82; button.MouseLeave += (_, _) => frost.Opacity = 1;
        }
        if (iconOnly) { button.Width = 38; button.MinWidth = 38; button.Padding = new Thickness(10); }
        if (textPath != null) Bind(button, AutomationProperties.NameProperty, source, textPath);
        else AutomationProperties.SetName(button, source.ToolTip as string ?? source.Name);
        button.Click += (_, _) => { if (!Live(button)) return; activate(); Refresh(); };
        button.PreviewMouseRightButtonUp += (_, args) =>
        {
            if (!Live(button)) return;
            var forwarded = new MouseButtonEventArgs(args.MouseDevice, args.Timestamp, args.ChangedButton) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            source.RaiseEvent(forwarded);
            if (forwarded.Handled) args.Handled = true;
            Refresh();
        };
        var node = new Node(button);
        int lastColor = -1;
        node.Pull = () =>
        {
            text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            button.GetBindingExpression(AutomationProperties.NameProperty)?.UpdateTarget();
            if (textPath is null) AutomationProperties.SetName(button, source.ToolTip as string ?? source.Name);
            if (iconPath != null && readIcon != null)
            {
                iconPath.Data = readIcon();
                iconPath.Visibility = iconPath.Data is null || iconPath.Data.Bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            }
            if (string.IsNullOrEmpty(text.Text))
            {
                string inline = source switch { MyButtonView value => value.LabText.Text, MyIconTextButtonView value => value.LabText.Text, MyExtraTextButtonView value => value.LabText.Text, _ => string.Empty };
                if (!string.IsNullOrEmpty(inline)) { text.SetCurrentValue(TextBlock.TextProperty, inline); button.SetCurrentValue(AutomationProperties.NameProperty, inline); }
            }
            if (source is MyButtonView original)
            {
                // Tone by warning level: highlight = primary (accent fill); danger = the original red buttons (delete…);
                // caution = actions that rewrite or replace files; everything else stays a neutral glass button.
                ButtonTone tone = original.ColorType switch
                {
                    MyButtonView.ColorState.Highlight => ButtonTone.Primary,
                    MyButtonView.ColorState.Red => ButtonTone.Danger,
                    _ => IsCautionAction(text.Text) ? ButtonTone.Caution : ButtonTone.Neutral,
                };
                if (lastColor != (int)tone)
                {
                    lastColor = (int)tone;
                    (string fill, string edge, string label) = tone switch
                    {
                        // All tones stay on the same glass surface; the primary is a brighter pane of glass, warnings
                        // are carried only by the label colour (monochrome theme, no solid slabs).
                        ButtonTone.Primary => ("GlassPresentationPrimaryFill", "GlassPresentationPrimaryEdge", "GlassPresentationText"),
                        ButtonTone.Danger => ("GlassPresentationField", "GlassPresentationControlStroke", "GlassPresentationDangerText"),
                        ButtonTone.Caution => ("GlassPresentationField", "GlassPresentationControlStroke", "GlassPresentationCautionText"),
                        _ => ("GlassPresentationField", "GlassPresentationControlStroke", "GlassPresentationText"),
                    };
                    button.SetResourceReference(Control.BackgroundProperty, fill);
                    button.SetResourceReference(Control.BorderBrushProperty, edge);
                    text.SetResourceReference(TextBlock.ForegroundProperty, label);
                    if (iconPath is not null) iconPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, label);
                    // Only the primary action is bold; warning tones are carried by colour alone (calmer, simpler).
                    text.FontWeight = tone is ButtonTone.Primary ? FontWeights.SemiBold : FontWeights.Normal;
                }
                // Compact Fluent-sized command: the original's fixed 35-55 px heights and wide minimums made every action a slab.
                if (button.Height != 34)
                {
                    button.Height = 34; button.MinHeight = 34; button.Width = double.NaN; button.MinWidth = 88;
                    button.Padding = new Thickness(14, 0, 14, 0); button.FontSize = 13.5; text.FontSize = 13.5;
                    button.VerticalAlignment = VerticalAlignment.Center;
                }
            }
            else if (source is MyIconTextButtonView && !FloatingCommandBarContext(source))
            {
                ButtonTone tone = text.Text == "删除" || source.Name.Contains("Delete", StringComparison.Ordinal) ? ButtonTone.Danger : ButtonTone.Neutral;
                if (lastColor != (int)tone)
                {
                    lastColor = (int)tone;
                    string label = tone == ButtonTone.Danger ? "GlassPresentationDangerText" : "GlassPresentationText";
                    text.SetResourceReference(TextBlock.ForegroundProperty, label);
                    if (iconPath is not null) iconPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, label);
                }
            }
            if (source is MyIconButtonView icon) button.SetCurrentValue(UIElement.IsEnabledProperty, source.IsEnabled && icon.CanClick);
            if (source is MyExtraTextButtonView extra) button.SetCurrentValue(UIElement.VisibilityProperty, extra.Show ? source.Visibility : Visibility.Collapsed);
        };
        return node;
    }

    private enum ButtonTone { Neutral, Primary, Caution, Danger }

    // Actions that overwrite, repair or reset existing files: worth a second look, but not destructive.
    private static readonly string[] CautionWords = { "补全", "修复", "重置", "覆盖", "清理", "清除", "重新", "替换", "还原", "更新" };
    private static bool IsCautionAction(string label) => !string.IsNullOrEmpty(label) && CautionWords.Any(label.Contains);

    private Node ProjectCheckbox(MyCheckBox source)
    {
        var checkbox = new CheckBox { Style = Style(SettingsContext(source) ? "GlassPresentationSettingsCheckBox" : "GlassPresentationCheckBox") };
        var label = new TextBlock(); TextStyle(label);
        if (SettingsContext(source)) label.FontSize = 14;
        Bind(label, TextBlock.TextProperty, source, nameof(source.Text)); checkbox.Content = label;
        Bind(checkbox, AutomationProperties.NameProperty, source, nameof(source.Text));
        Bind(checkbox, ToggleButton.IsCheckedProperty, source, nameof(source.Checked));
        bool forwarding = false;
        RoutedEventHandler changed = (_, _) =>
        {
            if (!Live(checkbox) || forwarding) return;
            forwarding = true;
            try { source.SetChecked(checkbox.IsChecked == true, true); checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget(); }
            finally { forwarding = false; }
            Refresh();
        };
        checkbox.Checked += changed; checkbox.Unchecked += changed;
        var node = new Node(checkbox);
        node.Pull = () => {
            // Export and other dynamic forms write directly to the source Inlines.
            string caption = new TextRange(source.LabText.ContentStart, source.LabText.ContentEnd).Text;
            if (source.Name == "CheckOptionsPclCustom") caption = caption.Replace("功能隐藏设置、", string.Empty, StringComparison.Ordinal);
            label.SetCurrentValue(TextBlock.TextProperty, caption);
            checkbox.SetCurrentValue(AutomationProperties.NameProperty, caption);
        };
        InstallSwitchMotion(checkbox, node);
        return node;
    }

    private Node ProjectRadioBox(MyRadioBox source)
    {
        var radio = new RadioButton { Style = Style("GlassPresentationRadioBox") };
        var text = new TextBlock(); TextStyle(text); Bind(text, TextBlock.TextProperty, source, nameof(source.Text)); radio.Content = text;
        Bind(radio, AutomationProperties.NameProperty, source, nameof(source.Text));
        Bind(radio, ToggleButton.IsCheckedProperty, source, nameof(source.Checked));
        radio.Checked += (_, _) => { if (!Live(radio) || source.Checked) return; source.SetChecked(true, true); radio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget(); Refresh(); };
        return new Node(radio) { Pull = () => radio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget() };
    }

    private Node ProjectRadioButton(MyRadioButton source)
    {
        var radio = new RadioButton { Style = Style("GlassPresentationRadioButton") };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (source.ShapeLogo?.Data != null) row.Children.Add(Icon(source.ShapeLogo.Data.ToString()));
        var label = new TextBlock(); TextStyle(label); Bind(label, TextBlock.TextProperty, source, nameof(source.Text)); row.Children.Add(label);
        radio.Content = row;
        Bind(radio, AutomationProperties.NameProperty, source, nameof(source.Text));
        radio.Checked += (_, _) => { if (Live(radio) && !source.Checked) { source.PerformClick(); Refresh(); } };
        var node = new Node(radio);
        node.Pull = () => radio.IsChecked = source.Checked;
        return node;
    }

    private Node ProjectSlider(MySlider source)
    {
        var slider = new Slider { Style = Style("GlassPresentationSlider"), Minimum = 0, SmallChange = source.ValueByKey, LargeChange = Math.Max(1, source.ValueByKey * 5) };
        var dragHint = new SliderDragHint(slider);
        bool pulling = false;
        slider.ValueChanged += (_, _) =>
        {
            if (!Live(slider) || pulling) return;
            source.Value = checked((int)Math.Round(slider.Value));
            pulling = true;
            try { slider.Value = source.Value; }
            finally { pulling = false; }
            Refresh();
        };
        var node = new Node(slider);
        node.Pull = () =>
        {
            pulling = true;
            try
            {
                slider.Maximum = Math.Max(1, source.MaxValue); slider.Value = source.Value;
                slider.SmallChange = Math.Max(1, source.ValueByKey);
                string valueText = source.GetHintText?.DynamicInvoke(source.Value)?.ToString();
                slider.ToolTip = valueText ?? source.ToolTip;
                dragHint.SetText(valueText);
            }
            finally { pulling = false; }
        };
        node.Cleanup.Add(dragHint.Dispose);
        return node;
    }

    private sealed class SliderDragHint : IDisposable
    {
        private readonly Slider _slider;
        private ToolTip _hint;
        private Thumb _thumb;
        private string _valueText;

        internal SliderDragHint(Slider slider)
        {
            _slider = slider;
            _slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted), handledEventsToo: true);
            _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), handledEventsToo: true);
            _slider.Unloaded += OnUnloaded;
            _slider.IsVisibleChanged += OnAvailabilityChanged;
            _slider.IsEnabledChanged += OnAvailabilityChanged;
        }

        internal void SetText(string text)
        {
            _valueText = text;
            if (_hint is null) return;
            if (text is null) Close();
            else if (!Equals(_hint.Content, text)) _hint.Content = text;
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            if (e.OriginalSource is not Thumb thumb || _valueText is null || !_slider.IsVisible || !_slider.IsEnabled) return;
            Close();
            _thumb = thumb;
            _hint = new ToolTip {
                Style = _slider.TryFindResource(typeof(ToolTip)) as Style ?? GlassCommandChrome.ToolTipStyle(!ModTheme.IsDarkTheme),
                PlacementTarget = _slider, Placement = PlacementMode.Custom, CustomPopupPlacementCallback = PlaceDragHint,
                Content = _valueText, IsHitTestVisible = false, Focusable = false
            };
            _hint.Closed += (owner, _) => ((ToolTip)owner).PlacementTarget = null;
            UpdatePosition(this, EventArgs.Empty);
            _slider.LayoutUpdated += UpdatePosition;
            _hint.IsOpen = true;
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e) => Close();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Close();
        private void OnAvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Close(); }

        private void UpdatePosition(object sender, EventArgs e)
        {
            if (_hint is not { } hint || _thumb is not { } thumb) return;
            var anchor = new Rect(thumb.TranslatePoint(new Point(), _slider), thumb.RenderSize);
            if (hint.PlacementRectangle != anchor) hint.PlacementRectangle = anchor;
        }

        private static CustomPopupPlacement[] PlaceDragHint(Size popup, Size thumb, Point offset)
        {
            double x = (thumb.Width - popup.Width) / 2;
            return [new(new Point(x, -popup.Height - 5), PopupPrimaryAxis.Horizontal),
                    new(new Point(x, thumb.Height + 5), PopupPrimaryAxis.Horizontal)];
        }

        private void Close()
        {
            if (_hint is not { } hint) return;
            _slider.LayoutUpdated -= UpdatePosition;
            _hint = null;
            _thumb = null;
            hint.IsOpen = false;
        }

        public void Dispose()
        {
            Close();
            _slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            _slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            _slider.Unloaded -= OnUnloaded;
            _slider.IsVisibleChanged -= OnAvailabilityChanged;
            _slider.IsEnabledChanged -= OnAvailabilityChanged;
        }
    }

    private sealed class EditorProjection(Action commit)
    {
        internal readonly Action Commit = commit;
        internal WeakReference<TextBox>? VisibleEditor;
    }

    private static readonly ConditionalWeakTable<TextBox, EditorProjection> ProjectedEditors = new();

    internal static void RegisterProjectedEditor(TextBox source, Action commit) => ProjectedEditors.Add(source, new(commit));

    internal static bool EditorHasFocus(TextBox source) =>
        source.IsKeyboardFocusWithin ||
        (ProjectedEditors.TryGetValue(source, out var editor) &&
         editor.VisibleEditor?.TryGetTarget(out var visible) == true && visible.IsKeyboardFocusWithin);

    internal static void BindTextPlaceholder(TextBox input, TextBlock placeholder)
    {
        var visibility = new MultiBinding { Mode = BindingMode.OneWay, Converter = TextPlaceholderVisibility.Instance };
        visibility.Bindings.Add(new Binding(nameof(TextBox.Text)) { Source = input });
        visibility.Bindings.Add(new Binding(nameof(UIElement.IsKeyboardFocusWithin)) { Source = input });
        visibility.Bindings.Add(new Binding(nameof(TextBlock.Text)) { Source = placeholder });
        placeholder.SetBinding(UIElement.VisibilityProperty, visibility);
    }

    private sealed class TextPlaceholderVisibility : IMultiValueConverter
    {
        internal static readonly TextPlaceholderVisibility Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values switch
        {
            // An IME can display preedit even while Text still contains the last committed value.
            [_, true, string { Length: > 0 }] => Visibility.Hidden,
            [string { Length: 0 }, false, string { Length: > 0 }] => Visibility.Visible,
            _ => Visibility.Collapsed
        };

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private Node ProjectTextInput(TextBox source)
    {
        var input = new TextBox { Style = Style("GlassPresentationTextBox"), IsReadOnly = source.IsReadOnly,
            AcceptsReturn = source.AcceptsReturn, AcceptsTab = source.AcceptsTab, TextWrapping = source.TextWrapping,
            MaxLength = source.MaxLength, VerticalScrollBarVisibility = source.VerticalScrollBarVisibility,
            HorizontalScrollBarVisibility = source.HorizontalScrollBarVisibility };
        if (!source.AcceptsReturn) input.Height = 36;
        else
        {
            input.VerticalContentAlignment = VerticalAlignment.Top;
            input.Padding = new Thickness(12, 8, 12, 8);
            if (source.MinHeight > 0) input.MinHeight = source.MinHeight;
            if (!double.IsPositiveInfinity(source.MaxHeight)) input.MaxHeight = source.MaxHeight;
        }
        Bind(input, TextBox.TextProperty, source, nameof(TextBox.Text), BindingMode.TwoWay);
        input.Name = source.Name;
        ForwardKeys(input, source);
        var host = new Grid(); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.Children.Add(input);
        var placeholder = new TextBlock { Margin = new Thickness(15, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        TextStyle(placeholder); placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); host.Children.Add(placeholder);
        BindTextPlaceholder(input, placeholder);
        var validationText = new TextBlock { FontSize = 11, Margin = new Thickness(3, 6, 3, 0), Visibility = Visibility.Collapsed };
        TextStyle(validationText); validationText.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger"); Grid.SetRow(validationText, 1); host.Children.Add(validationText);
        var node = new Node(host);
        if (ProjectedEditors.TryGetValue(source, out var editor))
        {
            var visible = new WeakReference<TextBox>(input);
            editor.VisibleEditor = visible;
            input.LostKeyboardFocus += (_, _) => { if (Live(input) && !input.IsKeyboardFocusWithin) editor.Commit(); };
            node.Cleanup.Add(() => { if (ReferenceEquals(editor.VisibleEditor, visible)) editor.VisibleEditor = null; });
        }
        node.Pull = () =>
        {
            placeholder.Text = (source as MyTextBoxView)?.HintText ?? string.Empty;
            string validation = (source as MyTextBoxView)?.ValidateResult;
            input.ToolTip = string.IsNullOrEmpty(validation) ? source.ToolTip : validation;
            validationText.Text = validation ?? string.Empty;
            validationText.Visibility = source is MyTextBoxView { ShowValidateResult: true } && !string.IsNullOrEmpty(source.Text) && !string.IsNullOrEmpty(validation) ? Visibility.Visible : Visibility.Collapsed;
            input.IsReadOnly = source.IsReadOnly;
        };
        return node;
    }

    private Node ProjectCombo(ComboBox source)
    {
        bool javaActions = source.Name is "ComboArgumentJavaSelect" or "ComboAdvanceJava";
        ComboBox combo = javaActions ? new JavaActionComboBox() : new ComboBox();
        combo.Style = Style("GlassPresentationComboBox");
        combo.IsEditable = source.IsEditable;
        combo.IsReadOnly = source.IsReadOnly;
        combo.IsTextSearchEnabled = !javaActions && source.IsTextSearchEnabled;
        combo.StaysOpenOnEdit = source.StaysOpenOnEdit;
        combo.MaxDropDownHeight = Math.Min(260, source.MaxDropDownHeight);
        var identities = new List<object>();
        string selectionTip = null;
        bool pulling = false;
        combo.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, combo) || !Live(combo) || pulling) return;
            if (javaActions)
            {
                int index = combo.SelectedIndex;
                if (index < 0) return;
                // Java items are commands. The instance controller validates and persists the choice in their click handler.
                if (index < identities.Count && index < source.Items.Count &&
                    ReferenceEquals(identities[index], source.Items[index]) && identities[index] is MyListItem item)
                {
                    combo.IsDropDownOpen = false;
                    item.PerformClick();
                }
            }
            else source.SelectedIndex = combo.SelectedIndex;
            Refresh();
        };
        if (javaActions)
        {
            combo.PreviewKeyDown += (_, args) =>
            {
                if (combo.IsDropDownOpen || args.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)) return;
                combo.IsDropDownOpen = true;
                args.Handled = true;
            };
        }
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!Live(combo) || pulling || !source.IsEditable) return;
            if (source is MyComboBoxView custom) custom.Text = combo.Text;
            else source.Text = combo.Text;
        }));
        ForwardKeys(combo, source);
        var node = new Node(combo);
        RouteDropDownWheel(combo, node);
        node.Pull = () =>
        {
            pulling = true;
            try
            {
                object[] items = source.Items.Cast<object>().ToArray();
                if (identities.Count != items.Length || !identities.Zip(items).All(pair => ReferenceEquals(pair.First, pair.Second)))
                {
                    combo.Items.Clear(); identities.Clear(); identities.AddRange(items);
                    foreach (object item in items)
                    {
                        var choice = new ComboBoxItem { Style = Style("GlassPresentationComboItem") };
                        if (javaActions && item is MyListItem java)
                        {
                            var row = new Grid();
                            row.ColumnDefinitions.Add(new ColumnDefinition());
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                            Bind(title, TextBlock.TextProperty, java, nameof(java.Title));
                            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
                            Grid.SetColumn(actions, 1);
                            row.Children.Add(title); row.Children.Add(actions);
                            choice.Content = row;
                            Bind(choice, AutomationProperties.NameProperty, java, nameof(java.Title));
                            Bind(choice, UIElement.IsEnabledProperty, java, nameof(UIElement.IsEnabled));
                        }
                        else if (item is ContentControl original)
                        {
                            choice.Content = Content(original.Content);
                            Bind(choice, UIElement.IsEnabledProperty, original, nameof(UIElement.IsEnabled));
                            Bind(choice, UIElement.VisibilityProperty, original, nameof(UIElement.Visibility));
                            choice.ToolTip = original.ToolTip as string;
                        }
                        else choice.Content = item?.ToString() ?? string.Empty;
                        combo.Items.Add(choice);
                    }
                }
                // The closed Java field has its own live summary, which must not overlap a retained menu choice.
                combo.SelectedIndex = javaActions ? -1 : source.SelectedIndex;
                if (source.ToolTip is null && combo.SelectedItem is ComboBoxItem { Content: string selectedText } && selectionTip != selectedText) {
                    selectionTip = selectedText;
                    combo.ToolTip = new ToolTip { Content = new TextBlock { Text = selectedText, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
                }
                for (int index = 0; index < items.Length; index++)
                {
                    var choice = (ComboBoxItem)combo.Items[index];
                    if (javaActions && items[index] is MyListItemView java && choice.Content is Grid row)
                    {
                        var actions = (StackPanel)row.Children[1];
                        Children(actions, java.Buttons?.Select(button => Project(button)).Where(item => item != null).ToArray() ?? Array.Empty<UIElement>());
                    }
                    else if (items[index] is ContentControl original) choice.Content = Content(original.Content);
                }
                if (source.IsEditable)
                {
                    string text = source is MyComboBoxView custom ? custom.Text : source.Text;
                    if (combo.Text != text) combo.Text = text;
                }
            }
            finally { pulling = false; }
        };
        return node;
    }

    private sealed class JavaActionComboBox : ComboBox
    {
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Let the page scroll when the closed menu has focus, without invoking a Java command.
            if (IsDropDownOpen) base.OnMouseWheel(e);
        }
    }

    private static void RouteDropDownWheel(ComboBox combo, Node node)
    {
        Window window = null;
        UIElement popupContent = null;
        ScrollViewer options = null;
        MouseWheelEventHandler wheel = (_, args) =>
        {
            if (!combo.IsDropDownOpen || args.Handled) return;
            if (options is not null)
                options.ScrollToVerticalOffset(Math.Clamp(options.VerticalOffset - args.Delta / 120d * 72, 0, options.ScrollableHeight));
            args.Handled = true;
        };
        void Detach()
        {
            window?.RemoveHandler(Mouse.PreviewMouseWheelEvent, wheel);
            popupContent?.RemoveHandler(Mouse.PreviewMouseWheelEvent, wheel);
            window = null;
            popupContent = null;
            options = null;
        }
        EventHandler opened = (_, _) =>
        {
            Detach();
            options = combo.Template.FindName("DropDownScroll", combo) as ScrollViewer;
            popupContent = (combo.Template.FindName("PART_Popup", combo) as Popup)?.Child;
            window = Window.GetWindow(combo);
            window?.AddHandler(Mouse.PreviewMouseWheelEvent, wheel);
            popupContent?.AddHandler(Mouse.PreviewMouseWheelEvent, wheel);
            // Freeze any scroll glide that was already moving the field's page when the list opened.
            for (DependencyObject ancestor = combo; ancestor is Visual; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer viewer) SmoothScroll.Stop(viewer);
        };
        EventHandler closed = (_, _) => Detach();
        RoutedEventHandler unloaded = (_, _) => Detach();
        combo.DropDownOpened += opened;
        combo.DropDownClosed += closed;
        combo.Unloaded += unloaded;
        node.Cleanup.Add(() =>
        {
            Detach();
            combo.DropDownOpened -= opened;
            combo.DropDownClosed -= closed;
            combo.Unloaded -= unloaded;
        });
    }

    private Node ProjectListRow(MyListItem source)
    {
        bool flat = !NavigationRow(source);
        var host = new Grid { MinHeight = flat ? 52 : 38 }; host.ColumnDefinitions.Add(new ColumnDefinition()); host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RoundIcon(image);
        var geometry = new Path { Width = 22, Height = 22, Margin = new Thickness(2, 0, 16, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        geometry.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GlassPresentationText"); content.Children.Add(image); content.Children.Add(geometry);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(labels, 1); content.Children.Add(labels);
        var title = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }; TextStyle(title); title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; labels.Children.Add(title);
        Bind(title, TextBlock.TextProperty, source, nameof(source.Title));
        var info = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }; TextStyle(info); info.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); labels.Children.Add(info);
        var date = new TextBlock { FontSize = 12, Margin = new Thickness(16, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }; TextStyle(date); date.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); Grid.SetColumn(date, 2); content.Children.Add(date);
        var primaryText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        primaryText.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationAccent"); Grid.SetColumn(primaryText, 3); content.Children.Add(primaryText);
        ButtonBase action;
        if (source.Type == MyListItem.CheckType.CheckBox)
        {
            var checkbox = new CheckBox { Style = Style(flat ? "GlassPresentationRowSelector" : "GlassPresentationCheckBox"), Content = content };
            RoutedEventHandler changed = (_, _) => { if (!Live(checkbox) || checkbox.IsChecked == source.Checked) return; source.PerformClick(); Refresh(); };
            checkbox.Checked += changed; checkbox.Unchecked += changed;
            action = checkbox;
        }
        else if (source.Type == MyListItem.CheckType.RadioBox)
        {
            var radio = new RadioButton { Style = Style("GlassPresentationRadioButton"), Content = content };
            bool activatedByCheck = false;
            radio.Checked += (_, _) =>
            {
                if (!Live(radio) || source.Checked) return;
                activatedByCheck = true;
                radio.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => activatedByCheck = false));
                source.PerformClick(); Refresh();
            };
            radio.Click += (_, _) => { if (!Live(radio)) return; if (activatedByCheck) { activatedByCheck = false; return; } source.PerformClick(); Refresh(); }; action = radio;
        }
        else
        {
            var button = new Button { Style = Style(flat ? "GlassPresentationListAction" : "GlassPresentationRowButton"), Content = content };
            button.Click += (_, _) => { if (!Live(button)) return; source.PerformClick(); Refresh(); }; action = button;
        }
        Bind(action, AutomationProperties.NameProperty, source, nameof(source.Title));
        ForwardContextClick(action, source);
        // Download pages keep rows clean: the row itself installs/opens, and its accessories (save, update log)
        // are offered on right-click instead of as trailing buttons.
        bool downloadRow = DownloadContext(source);
        if (downloadRow) AccessoryMenu(action, () => ((MyListItemView)source).Buttons?.OfType<MyIconButtonView>() ?? Enumerable.Empty<MyIconButtonView>(), source);
        if (flat) { action.MinHeight = 52; action.Height = 52; action.Padding = new Thickness(8, 6, 8, 6); }
        // Every flat list row (download, help, more…) uses the same edge-to-edge hover band, never a boxed highlight.
        if (flat && action is Button rowButton) { rowButton.Style = Style("GlassPresentationBandRow"); action.MinHeight = 52; action.Height = 52; HoverBand(host); }
        host.Children.Add(action);
        if (flat) host.Children.Add(RowSeparator(52));
        var accessories = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(accessories, 1); host.Children.Add(accessories);
        var node = new Node(host);
        node.Pull = () =>
        {
            source.EnsureActions();
            title.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            if (string.IsNullOrEmpty(source.Title) || !BindingOperations.IsDataBound(source.LabTitle, TextBlock.TextProperty))
                title.SetCurrentValue(TextBlock.TextProperty, new TextRange(source.LabTitle.ContentStart, source.LabTitle.ContentEnd).Text);
            action.SetCurrentValue(AutomationProperties.NameProperty, title.Text ?? string.Empty);
            primaryText.Text = downloadRow ? string.Empty : VersionPrimaryText(source);
            primaryText.Visibility = string.IsNullOrEmpty(primaryText.Text) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(action, string.IsNullOrEmpty(primaryText.Text) ? source.Info ?? string.Empty : primaryText.Text + " " + source.Title);
            info.Text = source.Info; info.Visibility = string.IsNullOrEmpty(source.Info) ? Visibility.Collapsed : Visibility.Visible;
            date.Text = source.Tag is ResourceVersion version && version.ReleaseDate.Year > 1900 ? version.ReleaseDate.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            date.Visibility = downloadRow || string.IsNullOrEmpty(date.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (source.Tag is ResourceVersion && !string.IsNullOrEmpty(source.Info)) info.Text = string.Join("，", source.Info.Split('，').Where(part => !part.StartsWith("更新于 ", StringComparison.Ordinal)));
            action.ToolTip = source.ToolTip as string ?? source.Info;
            if (action is ToggleButton toggle) toggle.IsChecked = source.Checked;
            image.Source = (source.PathLogo as Image)?.Source ?? ((source.PathLogo as Panel)?.Background as ImageBrush)?.ImageSource;
            geometry.Data = (source.PathLogo as Path)?.Data;
            // Launcher releases: the WPF edition (1.6 and later, and development builds) shows its own mark in the theme's
            // text colour; earlier releases keep the classic icon.
            if (InPage<PageSetupUpdate>(source) && LauncherEdition.IsWpf(source.Title) && LauncherEdition.WpfMark is { } mark)
            {
                image.Source = null;
                geometry.Data = mark;
                geometry.Width = geometry.Height = 32;
                geometry.Margin = new Thickness(0, 0, 12, 0);
            }
            image.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
            geometry.Visibility = geometry.Data is null ? Visibility.Collapsed : Visibility.Visible;
            Children(accessories, downloadRow ? Array.Empty<UIElement>() : ((MyListItemView)source).Buttons?.OfType<DependencyObject>().Select(item => Project(item)).Where(item => item != null).ToArray() ?? Array.Empty<UIElement>());
        };
        return node;
    }

    // Rows inside the download pages (version lists, resource lists and resource details).
    private static bool DownloadContext(DependencyObject source)
    {
        for (DependencyObject item = source; item != null; item = LogicalTreeHelper.GetParent(item) ?? (item is Visual ? VisualTreeHelper.GetParent(item) : null))
            if (item.GetType().Name.StartsWith("PageDownload", StringComparison.Ordinal)) return true;
        return false;
    }

    // Right-click menu listing a row's accessory actions (by their tooltip), used when the trailing buttons are hidden.
    private void AccessoryMenu(FrameworkElement target, Func<IEnumerable<MyIconButtonView>> read, FrameworkElement source)
    {
        target.PreviewMouseRightButtonUp += (_, args) =>
        {
            if (args.Handled || !Live(target) || source.ContextMenu != null) return;
            var buttons = read().Where(button => button.Visibility == Visibility.Visible && button.IsEnabled && !GlassClientUiPolicy.Excludes(button) &&
                !string.IsNullOrWhiteSpace(button.ToolTip as string)).ToArray();
            if (buttons.Length == 0) return;
            bool darkText = (_root.Resources["GlassPresentationText"] as SolidColorBrush)?.Color is { } color && color.R + color.G + color.B < 480;
            var menu = new GlassActionMenu(target, darkText);
            foreach (MyIconButtonView button in buttons)
            {
                MyIconButtonView captured = button;
                menu.AddCommand((string)captured.ToolTip, () => { if (Live(target)) { captured.PerformClick(); Refresh(); } }, iconData: captured.Logo);
            }
            menu.Show();
            args.Handled = true;
        };
    }

    private static string VersionPrimaryText(MyListItem source)
    {
        if (source.Type != MyListItem.CheckType.Clickable || !source.HasClickAction) return string.Empty;
        bool minecraft = source.Tag is Newtonsoft.Json.Linq.JObject version && version["id"]?.Type == Newtonsoft.Json.Linq.JTokenType.String && version["url"]?.Type == Newtonsoft.Json.Linq.JTokenType.String;
        for (DependencyObject item = source; item != null; item = LogicalTreeHelper.GetParent(item))
        {
            if (item is PageDownloadModify) return string.Empty;
            if (item is PageDownloadInstall) return minecraft ? "安装" : string.Empty;
        }
        return source.Tag is ResourceVersion ? "下载" : string.Empty;
    }

    private static bool NavigationRow(DependencyObject source)
    {
        for (DependencyObject item = source; item != null; item = LogicalTreeHelper.GetParent(item))
            if (item is MyPageLeft) return true;
        return false;
    }

    private static Border RowSeparator(double left)
    {
        var separator = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(left, 0, 8, 0), IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        return separator;
    }

    private void ForwardKeys(UIElement target, UIElement source)
    {
        void Forward(object sender, KeyEventArgs args)
        {
            if (!Live(target)) return;
            if (args.Key is not (Key.Enter or Key.Escape) && (args.Key < Key.F1 || args.Key > Key.F12)) return;
            PresentationSource presentation = PresentationSource.FromVisual(source);
            if (presentation is null) return;
            var forwarded = new KeyEventArgs(args.KeyboardDevice, presentation, args.Timestamp, args.Key) { RoutedEvent = args.RoutedEvent };
            source.RaiseEvent(forwarded);
            // Both trees bubble to the same Window. The forwarded route has already run its shortcuts.
            if (forwarded.Handled || ReferenceEquals(PresentationSource.FromVisual(target), presentation)) args.Handled = true;
        }
        target.KeyDown += Forward; target.KeyUp += Forward;
    }

    private void ForwardContextClick(UIElement target, FrameworkElement source, Action prepare = null)
    {
        target.PreviewMouseRightButtonUp += (_, args) =>
        {
            if (!Live(target) || !source.IsEnabled) return;
            prepare?.Invoke();
            var preview = new MouseButtonEventArgs(args.MouseDevice, args.Timestamp, args.ChangedButton) {
                RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = source
            };
            source.RaiseEvent(preview);
            bool handled = preview.Handled;
            // A projected ContextMenu retains its own popup and lifecycle. Rows without
            // a menu still have real right-click commands (instance options/mod details).
            if (!handled && source.ContextMenu is null)
            {
                var release = new MouseButtonEventArgs(args.MouseDevice, args.Timestamp, args.ChangedButton) {
                    RoutedEvent = UIElement.MouseRightButtonUpEvent, Source = source
                };
                source.RaiseEvent(release);
                handled = release.Handled;
            }
            if (handled) args.Handled = true;
            Refresh();
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Geometry> IconGeometryCache = new(StringComparer.Ordinal);

    private static Geometry ParseIconGeometry(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        return IconGeometryCache.GetOrAdd(data, static raw =>
        {
            try
            {
                Geometry parsed = Geometry.Parse(raw);
                if (parsed.CanFreeze) parsed.Freeze();
                return parsed;
            }
            catch (FormatException)
            {
                return null;
            }
        });
    }

    private FrameworkElement Icon(string data)
    {
        var icon = new Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Data = ParseIconGeometry(data);
        if (icon.Data is null) icon.Visibility = Visibility.Collapsed;
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GlassPresentationText");
        return icon;
    }

    private static void InstallSwitchMotion(CheckBox checkbox, Node node)
    {
        ToggleFeedback.SetIsEnabled(checkbox, true);
        node.Cleanup.Add(() => ToggleFeedback.SetIsEnabled(checkbox, false));
    }
}
