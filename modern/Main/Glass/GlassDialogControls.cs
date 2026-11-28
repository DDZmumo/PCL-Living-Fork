// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PCLLF.Wpf.Motion;

namespace PCL;

internal enum DialogTone { Information, Error, Warning }

internal static class GlassDialogControls
{
    internal static Style Style(string key) => (Style)GlassPageResources.GetSharedResources()[key];

    internal static Button Button(string text) => new() {
        Content = text, Style = Style("GlassPresentationButton"), Padding = new Thickness(12, 0, 12, 0)
    };

    internal static CheckBox Check(string text, bool value) => new() {
        Content = text, IsChecked = value, Style = Style("GlassPresentationCheckBox")
    };

    internal static TextBox TextBox(string text = "") => new() {
        Text = text, Style = Style("GlassPresentationTextBox")
    };

    internal static ScrollViewer Scroll()
    {
        var scroll = new ScrollViewer { Style = Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(scroll, true);
        return scroll;
    }

    internal static Grid WithPlaceholder(TextBox field, string text)
    {
        AutomationProperties.SetName(field, text);
        var placeholder = new TextBlock {
            Text = text, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0)
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        GlassPageResources.BindTextPlaceholder(field, placeholder);
        return new Grid { Children = { field, placeholder } };
    }

    internal static Border Section(string title, UIElement content)
    {
        var header = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var frame = new Border { Child = new StackPanel { Children = { header, content } },
            Margin = new Thickness(3, 3, 8, 12), Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(.7) };
        frame.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        AutomationProperties.SetName(frame, title);
        return frame;
    }

    internal static Border Notice(string text, DialogTone tone)
    {
        var notice = new PCLLF.Wpf.Glass.GlassSurface { Child = new TextBlock { TextWrapping = TextWrapping.Wrap }, BlurRadius = 24,
            Margin = new Thickness(0, 8, 0, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(.7) };
        Callout(notice);
        UpdateNotice(notice, text, tone);
        return notice;
    }

    /// <summary>Turns a bordered message into a callout: a round badge with an "i" or "!" ahead of the text.</summary>
    internal static void Callout(Border notice)
    {
        if (notice.Child is not TextBlock label) return;
        notice.Child = null;
        var badge = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
        badge.Children.Add(new System.Windows.Shapes.Ellipse { StrokeThickness = 1.4, Fill = System.Windows.Media.Brushes.Transparent });
        badge.Children.Add(new System.Windows.Shapes.Path { StrokeThickness = 1.6, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round, Data = System.Windows.Media.Geometry.Parse("M9,8 V13 M9,5.4 V5.6") });
        label.FontSize = 13;
        label.TextWrapping = TextWrapping.Wrap;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness();
        Grid.SetColumn(label, 1);
        var layout = new Grid { Children = { badge, label } };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        notice.Child = layout;
        notice.CornerRadius = new CornerRadius(10);
        notice.Padding = new Thickness(14, 10, 12, 10);
        bool warn = notice.Tag is DialogTone.Warning or DialogTone.Error;
        Tone(notice, label, warn);
    }

    internal static TextBlock NoticeText(Border notice) =>
        notice.Child as TextBlock ?? ((Grid)notice.Child).Children.OfType<TextBlock>().First();

    internal static void UpdateNotice(Border notice, string text, DialogTone tone)
    {
        bool warn = tone is DialogTone.Warning or DialogTone.Error;
        notice.Tag = tone;
        var label = NoticeText(notice);
        label.Text = text;
        Tone(notice, label, warn);
    }

    // Information sits on the neutral field glass in the secondary text colour; warnings are tinted.
    private static void Tone(Border notice, TextBlock label, bool warn)
    {
        notice.SetResourceReference(Border.BackgroundProperty, warn ? "GlassPresentationWarnFill" : "GlassPresentationField");
        notice.SetResourceReference(Border.BorderBrushProperty, warn ? "GlassPresentationWarnEdge" : "GlassPresentationControlStroke");
        label.SetResourceReference(TextBlock.ForegroundProperty, warn ? "GlassPresentationWarnText" : "GlassPresentationSecondary");
        if (notice.Child is not Grid { Children: [Grid badge, ..] }) return;
        foreach (var shape in badge.Children.OfType<System.Windows.Shapes.Shape>())
            shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, warn ? "GlassPresentationWarnText" : "GlassPresentationSecondary");
        if (badge.Children[1] is System.Windows.Shapes.Path glyph)
            glyph.Data = System.Windows.Media.Geometry.Parse(warn ? "M9,5 V10 M9,12.6 V12.8" : "M9,8 V13 M9,5.4 V5.6");
    }
}

/// <summary>A two-line choice with a native radio group and a single selection action.</summary>
internal sealed class GlassDialogChoiceRow : Grid
{
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
    private readonly RadioButton _radio;
    internal bool IsSelected { get => _radio.IsChecked == true; set => _radio.IsChecked = value; }
    internal event Action? Selected;
    internal event Action? SelectionChanged;

    internal GlassDialogChoiceRow(string group)
    {
        _title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        _radio = new RadioButton { GroupName = group, Style = GlassDialogControls.Style("GlassPresentationRadioBox"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 12, 6), Content = new StackPanel { Children = { _title, _detail } } };
        _radio.Checked += (_, _) => { Selected?.Invoke(); SelectionChanged?.Invoke(); };
        _radio.Unchecked += (_, _) => SelectionChanged?.Invoke();
        Children.Add(new Border { Child = _radio, CornerRadius = new CornerRadius(8), Background = System.Windows.Media.Brushes.Transparent });
        Margin = new Thickness(3, 0, 7, 8);
        MinHeight = 55;
    }

    internal void Configure(string title, string detail, object data, string hint)
    {
        _title.Text = title;
        _detail.Text = detail;
        Tag = data;
        ToolTip = hint;
        AutomationProperties.SetName(_radio, title);
        AutomationProperties.SetHelpText(_radio, detail);
    }
}
