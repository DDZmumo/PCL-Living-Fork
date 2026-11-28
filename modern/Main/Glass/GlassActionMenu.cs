// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>A fresh WPF command menu with glass popup, item and submenu primitives.</summary>
internal sealed class GlassActionMenu : ContextMenu {
    private readonly bool _darkText;
    private ContextMenu? _original;
    private bool? _contrastMode;
    internal Func<bool>? CanActivateOwner { get; set; }
    private bool OwnerIsLive() => CanActivateOwner?.Invoke() != false;
    internal GlassActionMenu(FrameworkElement placementTarget, bool useDarkText, bool reduceMotion = false) {
        PlacementTarget = placementTarget ?? throw new ArgumentNullException(nameof(placementTarget));
        _darkText = useDarkText;
        Background = Brushes.Transparent; BorderThickness = new Thickness(); Padding = new Thickness();
        MinWidth = 238; MaxWidth = 420;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"); FontSize = 13;
        Foreground = GlassCommandChrome.Brush(useDarkText ? "#EF263339" : "#F5FFFFFF");
        Resources["GlassTextBrush"] = Foreground;
        Resources["GlassSecondaryTextBrush"] = GlassCommandChrome.Brush(useDarkText ? "#AB354249" : "#BCFFFFFF");
        Resources["GlassHoverBrush"] = GlassCommandChrome.Brush(useDarkText ? "#32FFFFFF" : "#2AFFFFFF");
        Resources["GlassSelectedBrush"] = GlassCommandChrome.Brush(useDarkText ? "#44FFFFFF" : "#3AFFFFFF");
        RefreshContrastPalette();
        Resources[typeof(Separator)] = SeparatorStyle();
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        Template = new ControlTemplate(typeof(ContextMenu)) {
            VisualTree = GlassCommandChrome.PopupRoot(items, useDarkText, 14, new Thickness(7))
        };
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
        Opened += OnOpened;
        Closed += OnClosed;
    }
    internal MenuItem AddCommand(string text, Action action, bool enabled = true, string? iconData = null, bool check = false, bool allowServerAction = false) {
        ArgumentNullException.ThrowIfNull(action);
        if (!allowServerAction && GlassClientUiPolicy.ServerAction(text)) return new MenuItem { Header = text, Visibility = Visibility.Collapsed, IsEnabled = false };
        var item = new LifetimeMenuItem(OwnerIsLive) { Header = text, IsEnabled = enabled, IsChecked = check, Style = (Style)Resources[typeof(MenuItem)] };
        if (!string.IsNullOrWhiteSpace(iconData)) {
            try { item.Icon = MakeIcon(Geometry.Parse(iconData)); }
            catch (FormatException) { }
        }
        item.Click += (_, _) => { if (OwnerIsLive()) action(); }; Items.Add(item); return item;
    }
    internal void AddSeparator() => Items.Add(new Separator());
    internal void Show() { if (PlacementTarget is FrameworkElement target) GlassCommandChrome.CopyMaterial(target, this); IsOpen = true; }

    internal static GlassActionMenu FromContextMenu(ContextMenu original, FrameworkElement placementTarget, bool useDarkText, bool reduceMotion = false) {
        ArgumentNullException.ThrowIfNull(original);
        var menu = new GlassActionMenu(placementTarget, useDarkText, reduceMotion) { _original = original };
        menu.ProjectItems(); return menu;
    }
    internal static GlassActionMenu FromContextMenu(ContextMenu original, bool useDarkText, bool reduceMotion = false) =>
        FromContextMenu(original, original.PlacementTarget as FrameworkElement ?? throw new InvalidOperationException("A projected context menu needs a placement target."), useDarkText, reduceMotion);

    private void OnOpened(object sender, RoutedEventArgs args) {
        if (!OwnerIsLive()) { IsOpen = false; return; }
        RefreshContrastPalette();
        if (PlacementTarget is FrameworkElement target) GlassCommandChrome.CopyMaterial(target, this);
        if (_original is null) return;
        _original.RaiseEvent(new RoutedEventArgs(OpenedEvent, _original));
        ProjectItems();
    }
    private void OnClosed(object sender, RoutedEventArgs args) {
        _original?.RaiseEvent(new RoutedEventArgs(ClosedEvent, _original));
    }
    private void RefreshContrastPalette() {
        bool highContrast = SystemParameters.HighContrast;
        if (highContrast) {
            GlassCommandChrome.RefreshContrastResources(Resources);
            SetResourceReference(Control.ForegroundProperty, GlassCommandChrome.ContrastTextKey);
        } else Foreground = GlassCommandChrome.Brush(_darkText ? "#EF263339" : "#F5FFFFFF");
        Resources["GlassTextBrush"] = Foreground;
        if (_contrastMode == highContrast) return;
        _contrastMode = highContrast;
        Resources[typeof(MenuItem)] = ItemStyle(_darkText, highContrast);
    }
    private void ProjectItems() {
        Items.Clear();
        if (_original is null) return;
        foreach (object source in _original.Items) {
            if (source is Separator) { if (Items.Count > 0 && Items[Items.Count - 1] is not Separator) Items.Add(new Separator()); }
            else if (source is MenuItem item && !GlassClientUiPolicy.Excludes(item)) Items.Add(Project(item));
        }
        if (Items.Count > 0 && Items[Items.Count - 1] is Separator) Items.RemoveAt(Items.Count - 1);
    }
    private MenuItem Project(MenuItem source) {
        var result = new LifetimeMenuItem(OwnerIsLive) { Header = HeaderText(source.Header), Icon = CopyIcon(source.Icon), Style = (Style)Resources[typeof(MenuItem)] };
        foreach (DependencyProperty property in new[] { IsEnabledProperty, VisibilityProperty, MenuItem.IsCheckableProperty, MenuItem.StaysOpenOnClickProperty, MenuItem.InputGestureTextProperty, MenuItem.CommandParameterProperty })
            result.SetBinding(property, new Binding { Source = source, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
        result.SetBinding(MenuItem.CommandProperty, new Binding(nameof(MenuItem.Command)) { Source = source, Converter = new LifetimeCommandConverter(source, OwnerIsLive) });
        result.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(MenuItem.IsChecked)) { Source = source, Mode = BindingMode.TwoWay });
        result.SetBinding(MenuItem.CommandTargetProperty, new Binding(nameof(MenuItem.CommandTarget)) { Source = source, Converter = new OriginalCommandTarget(source) });
        result.Click += (_, args) => {
            if (!OwnerIsLive() || !ReferenceEquals(args.OriginalSource, result) || !source.IsEnabled) return;
            source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, source));
            // Native MenuItem invokes its bound original command exactly once after Click.
        };
        foreach (object child in source.Items) {
            if (child is Separator) { if (result.Items.Count > 0 && result.Items[result.Items.Count - 1] is not Separator) result.Items.Add(new Separator()); }
            else if (child is MenuItem item && !GlassClientUiPolicy.Excludes(item)) result.Items.Add(Project(item));
        }
        if (result.Items.Count > 0 && result.Items[result.Items.Count - 1] is Separator) result.Items.RemoveAt(result.Items.Count - 1);
        return result;
    }
    private UIElement? CopyIcon(object? icon) => icon switch {
        Path path => MakeIcon(path.Data?.CloneCurrentValue()),
        Image image => new Image { Source = image.Source, Width = 16, Height = 16, Stretch = Stretch.Uniform },
        Geometry geometry => MakeIcon(geometry.CloneCurrentValue()),
        _ => null
    };
    private static Path MakeIcon(Geometry? geometry) {
        var icon = new Path { Data = geometry, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        icon.SetBinding(Path.FillProperty, new Binding(nameof(Control.Foreground)) {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1)
        });
        return icon;
    }
    private static string HeaderText(object? header) {
        if (header is string text) return text;
        if (header is TextBlock block) return block.Text;
        if (header is AccessText access) return access.Text;
        if (header is ContentControl content) return HeaderText(content.Content);
        if (header is DependencyObject root) {
            var parts = new List<string>();
            foreach (object child in LogicalTreeHelper.GetChildren(root)) { string value = HeaderText(child); if (!string.IsNullOrWhiteSpace(value)) parts.Add(value); }
            return string.Join(" ", parts);
        }
        return header?.ToString() ?? "";
    }
    private static Style SeparatorStyle() {
        var style = new Style(typeof(Separator));
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.BackgroundProperty, GlassCommandChrome.Brush("#30FFFFFF"));
        line.SetValue(FrameworkElement.HeightProperty, .7d); line.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 5, 12, 5));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Separator)) { VisualTree = line }));
        return style;
    }
    private static Style ItemStyle(bool darkText, bool highContrast) {
        var style = new Style(typeof(MenuItem));
        if (highContrast) style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(GlassCommandChrome.ContrastTextKey)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness()));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 10, 11, 10)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        var root = new FrameworkElementFactory(typeof(Grid));
        var row = new FrameworkElementFactory(typeof(Border), "Row");
        row.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        row.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        row.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var layout = new FrameworkElementFactory(typeof(DockPanel));
        var arrow = new FrameworkElementFactory(typeof(TextBlock), "Arrow");
        arrow.SetValue(TextBlock.TextProperty, "›"); arrow.SetValue(TextBlock.FontSizeProperty, 19d);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(12, -2, 0, -2)); arrow.SetValue(DockPanel.DockProperty, Dock.Right);
        arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed); layout.AppendChild(arrow);
        var gesture = new FrameworkElementFactory(typeof(TextBlock)); gesture.SetValue(DockPanel.DockProperty, Dock.Right);
        gesture.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(MenuItem.InputGestureTextProperty));
        gesture.SetValue(UIElement.OpacityProperty, highContrast ? 1d : .6d); gesture.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0)); layout.AppendChild(gesture);
        var check = new FrameworkElementFactory(typeof(TextBlock), "Check"); check.SetValue(TextBlock.TextProperty, "✓");
        check.SetValue(FrameworkElement.WidthProperty, 20d); check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed); layout.AppendChild(check);
        var icon = new FrameworkElementFactory(typeof(ContentPresenter)); icon.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.IconProperty));
        icon.SetValue(FrameworkElement.WidthProperty, 20d); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0)); layout.AppendChild(icon);
        var title = new FrameworkElementFactory(typeof(ContentPresenter)); title.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        title.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true); title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); layout.AppendChild(title);
        row.AppendChild(layout); root.AppendChild(row);
        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.AllowsTransparencyProperty, true); popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(MenuItem.IsSubmenuOpen)) { RelativeSource = RelativeSource.TemplatedParent });
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        popup.AppendChild(GlassCommandChrome.PopupRoot(items, darkText, 12, new Thickness(7)));
        root.AppendChild(popup);
        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, GlassCommandChrome.Brush(darkText ? "#DCE7EEF8" : "#E43B4A61")));
        if (highContrast) {
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(GlassCommandChrome.ContrastHighlightKey)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(GlassCommandChrome.ContrastHighlightTextKey)));
        }
        style.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, highContrast ? 1d : .4d));
        if (highContrast) disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(GlassCommandChrome.ContrastGrayKey)));
        style.Triggers.Add(disabled);
        var checkedItem = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true }; checkedItem.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Check")); template.Triggers.Add(checkedItem);
        var submenu = new Trigger { Property = ItemsControl.HasItemsProperty, Value = true }; submenu.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Arrow")); template.Triggers.Add(submenu);
        return style;
    }
    private sealed class OriginalCommandTarget(MenuItem source) : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value as IInputElement ?? source;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private sealed class LifetimeMenuItem(Func<bool> isLive) : MenuItem {
        protected override void OnClick() { if (isLive()) base.OnClick(); }
    }
    private sealed class LifetimeCommandConverter(MenuItem source, Func<bool> isLive) : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ICommand command ? new LifetimeCommand(command, source, isLive) : null!;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private sealed class LifetimeCommand(ICommand command, MenuItem source, Func<bool> isLive) : ICommand {
        public bool CanExecute(object? parameter) => isLive() && source.IsEnabled && (command is RoutedCommand routed ? routed.CanExecute(parameter, source.CommandTarget ?? source) : command.CanExecute(parameter));
        public void Execute(object? parameter) {
            if (!CanExecute(parameter)) return;
            if (command is RoutedCommand routed) routed.Execute(parameter, source.CommandTarget ?? source); else command.Execute(parameter);
        }
        public event EventHandler? CanExecuteChanged { add => command.CanExecuteChanged += value; remove => command.CanExecuteChanged -= value; }
    }
}
