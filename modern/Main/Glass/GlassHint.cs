// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// A page callout: information sits on the neutral field glass in the secondary text colour; warnings are tinted.
/// A closable hint can remember its dismissal in a boolean setting.
/// </summary>
internal sealed class GlassHint : ContentControl
{
    private readonly GlassSurface _surface = new();
    internal enum Tone { Information, Warning, Danger }

    private readonly TextBlock _text = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    internal string Text { get => _text.Text; set => _text.Text = value; }

    internal GlassHint(string text, Tone tone = Tone.Information, string? dismissSetting = null)
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = _surface;
        _surface.CornerRadius = new CornerRadius(10);
        _surface.BorderThickness = new Thickness(.7);
        _surface.BlurRadius = 24;
        var grid = new Grid();
        GlassPageLayout.PreserveRowSize(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
        var disc = new Ellipse { StrokeThickness = 1.4, Fill = Brushes.Transparent };
        var glyph = new Path { StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse(tone == Tone.Information ? "M9,8 V13 M9,5.4 V5.6" : "M9,5 V10 M9,12.6 V12.8") };
        badge.Children.Add(disc);
        badge.Children.Add(glyph);
        grid.Children.Add(badge);
        _text.SetResourceReference(StyleProperty, "GlassPresentationTextStyle");
        _text.FontSize = 13;
        _text.Text = text;
        Grid.SetColumn(_text, 1);
        grid.Children.Add(_text);
        _surface.Child = new Border { Padding = new Thickness(14, 10, 12, 10), Child = grid };

        if (tone == Tone.Information || SystemParameters.HighContrast)
        {
            _surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
            _surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationControlStroke");
            disc.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
            glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
            _text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        }
        else
        {
            Color color = tone == Tone.Warning ? Color.FromRgb(255, 176, 32) : Color.FromRgb(255, 69, 58);
            _surface.Background = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B));
            _surface.BorderBrush = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
            disc.Stroke = glyph.Stroke = new SolidColorBrush(color);
            _text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        }
        AutomationProperties.SetHelpText(this, tone switch { Tone.Information => "提示", Tone.Warning => "注意", _ => "警告" });

        if (dismissSetting is null) return;
        if (Settings.Get<bool>(dismissSetting)) Visibility = Visibility.Collapsed;
        var cross = new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        cross.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        var close = new Button { Style = GlassDialogControls.Style("GlassPresentationRowButton"), Width = 28, Height = 28, MinHeight = 28,
            Padding = new Thickness(8), Margin = new Thickness(12, -4, -6, -4), VerticalAlignment = VerticalAlignment.Top, Content = cross };
        AutomationProperties.SetName(close, "关闭提示");
        close.Click += (_, _) => {
            Settings.Set(dismissSetting, true);
            Visibility = Visibility.Collapsed;
        };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
    }
}
