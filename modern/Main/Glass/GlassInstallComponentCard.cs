// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PCLCS;
using PCLLF.Wpf.Glass;

namespace PCL;

internal sealed class GlassInstallComponentCard
{
    internal GlassSurface Card { get; }
    internal StackPanel Versions { get; } = new();
    internal Expander Picker { get; }
    internal Button Clear { get; }
    internal DownloadInstallComponentView Selection { get; }
    internal DownloadModifyComparison? Comparison { get; }

    internal GlassInstallComponentCard(GlassPageResources theme, string title, string iconAddress, bool modify)
    {
        var label = theme.Note("", 12.5);
        label.MaxWidth = 260;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.TextWrapping = TextWrapping.NoWrap;
        label.VerticalAlignment = VerticalAlignment.Center;
        var selectedIcon = StatusGlyph("M1,5 L4,8 10,1", "GlassPresentationText");
        selectedIcon.Visibility = Visibility.Collapsed;
        var lockIcon = StatusGlyph("M2.5,6 V4 A3.5,3.5 0 0 1 9.5,4 V6 M1,6 H11 V12 H1 Z", "GlassPresentationSecondary");
        var lockStyle = new Style(typeof(System.Windows.Shapes.Path));
        lockStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        var unavailable = new DataTrigger { Binding = new Binding(nameof(UIElement.Visibility)) { Source = Versions }, Value = Visibility.Collapsed };
        unavailable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
        lockStyle.Triggers.Add(unavailable);
        lockIcon.Style = lockStyle;
        var current = new Grid { RenderTransform = new TranslateTransform(), VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { selectedIcon, lockIcon, label } } };
        var badgeStyle = new Style(typeof(Border));
        var selected = new DataTrigger { Binding = new Binding(nameof(UIElement.Visibility)) { Source = selectedIcon }, Value = Visibility.Visible };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlassPresentationSelected")));
        badgeStyle.Triggers.Add(selected);
        badge.Style = badgeStyle;
        current.Children.Add(badge);

        Clear = GlassLocalModRow.CreateButton("取消选择 " + title, ModBase.Logo.IconButtonCross);
        Clear.Width = Clear.Height = Clear.MinWidth = Clear.MinHeight = 26;
        Clear.SetResourceReference(FrameworkElement.MarginProperty, "GlassPresentationExpanderActionMargin");
        Clear.Visibility = Visibility.Collapsed;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Clear);

        var caption = theme.Text(title);
        caption.FontWeight = FontWeights.SemiBold; caption.FontSize = 15;
        caption.VerticalAlignment = VerticalAlignment.Center;
        var details = new Grid { Children = { current } };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(caption);
        Grid.SetColumn(details, 1);
        header.Children.Add(details);
        Grid.SetColumn(buttons, 2);
        header.Children.Add(buttons);

        Picker = new Expander {
            Style = theme.Style("GlassPresentationExpander"),
            Header = header, Content = Versions
        };
        System.Windows.Automation.AutomationProperties.SetName(Picker, "选择 " + title + " 版本");
        Card = GlassPageResources.Card(Picker, GlassPageLayout.CardRole.Expander);
        var available = new Style(typeof(Expander), Picker.Style);
        var locked = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = Versions }, Value = Visibility.Collapsed };
        locked.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
        locked.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        locked.Setters.Add(new Setter(UIElement.OpacityProperty, .55));
        available.Triggers.Add(locked);
        Picker.Style = available;
        Selection = new DownloadInstallComponentView {
            Card = Card, MainSwap = () => Versions, Info = current, Versions = Versions,
            Clear = Clear, Image = selectedIcon, Label = label,
            IsSwapped = () => !Picker.IsExpanded, SetSwapped = collapsed => Picker.IsExpanded = !collapsed
        };

        if (modify) {
            var restore = GlassLocalModRow.CreateButton("恢复原有 " + title + " 版本", ModBase.Logo.IconButtonRefresh);
            restore.SetResourceReference(FrameworkElement.MarginProperty, "GlassPresentationExpanderActionMargin");
            restore.Visibility = Visibility.Collapsed;
            buttons.Children.Insert(0, restore);
            Comparison = CreateComparison(theme, iconAddress, Card, details, current, label, restore,
                () => Versions, Selection.SetSwapped, Clear);
        }
    }

    private static System.Windows.Shapes.Path StatusGlyph(string data, string color)
    {
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 11, Height = 10,
            Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color);
        return glyph;
    }

    internal static DownloadModifyComparison CreateComparison(GlassPageResources theme, string iconAddress,
        FrameworkElement card, Panel details, Grid current, TextBlock label, Button restore,
        System.Func<FrameworkElement?> mainSwap, System.Action<bool> setSwapped, FrameworkElement? clear = null)
    {
        var comparison = new Grid { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        comparison.ColumnDefinitions.Add(new ColumnDefinition());
        comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        comparison.ColumnDefinitions.Add(new ColumnDefinition());

        DockPanel VersionSide(string name, out TextBlock version)
        {
            var icon = MakeIcon(iconAddress, 18);
            icon.Name = name;
            version = theme.Note("无", 13);
            version.VerticalAlignment = VerticalAlignment.Center;
            version.TextWrapping = TextWrapping.NoWrap;
            version.TextTrimming = TextTrimming.CharacterEllipsis;
            var side = new DockPanel { Children = { icon, version }, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            return side;
        }

        var oldSide = VersionSide("PreviousVersionIcon", out var previous);
        var newSide = VersionSide("SelectedVersionIcon", out _);
        var arrow = theme.Note("→", 14);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.Margin = new Thickness(6, 0, 6, 0);
        comparison.Children.Add(oldSide);
        Grid.SetColumn(arrow, 1);
        comparison.Children.Add(arrow);
        Grid.SetColumn(newSide, 2);
        comparison.Children.Add(newSide);
        details.Children.Add(comparison);
        return new DownloadModifyComparison {
            Card = card, Restore = restore, Current = current, Compare = comparison, Clear = clear,
            Label = label, OldLabel = previous, MainSwap = mainSwap, SetSwapped = setSwapped
        };
    }

    private static Image MakeIcon(string address, double size) => new() {
        Source = GlassInstallRow.Icon(address), Width = size, Height = size, Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center
    };
}
