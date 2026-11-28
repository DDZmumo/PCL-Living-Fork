// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>Memory overview of the launch settings: one segmented bar with a legend of used, game and free memory.</summary>
internal sealed class GlassMemoryMeter : StackPanel
{
    private readonly Grid _bar = new() { Height = 8 };
    private readonly Border _game = new() { Margin = new Thickness(0, 0, 2, 0) };
    private readonly TextBlock _total = Label(12);
    private readonly (StackPanel Item, Ellipse Dot, TextBlock Value) _used, _allocated, _free;
    private readonly TextBlock _warning = Label(12);

    internal GlassMemoryMeter()
    {
        Margin = new Thickness(0, 11, 2, 0);
        for (int index = 0; index < 3; index++) _bar.ColumnDefinitions.Add(new ColumnDefinition());
        var track = new Border { CornerRadius = new CornerRadius(4), Child = _bar };
        track.SetResourceReference(Border.BackgroundProperty, "GlassPresentationTrack");
        // Rounded ends for the whole bar; segments are separated by a 2 px gap.
        _bar.SizeChanged += (_, args) => _bar.Clip = new RectangleGeometry(new Rect(args.NewSize), 4, 4);
        var used = new Border { Margin = new Thickness(0, 0, 2, 0), Opacity = .32 };
        used.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSecondary");
        Grid.SetColumn(_game, 1);
        _bar.Children.Add(used);
        _bar.Children.Add(_game);
        Children.Add(track);

        var legend = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        _total.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        DockPanel.SetDock(_total, Dock.Right);
        legend.Children.Add(_total);
        _used = Entry(legend, "已使用");
        _used.Dot.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
        _used.Dot.Opacity = .32;
        _allocated = Entry(legend, "游戏分配");
        _free = Entry(legend, "剩余可用");
        _free.Dot.SetResourceReference(Shape.FillProperty, "GlassPresentationTrack");
        Children.Add(legend);
        _warning.Text = "游戏分配超过了当前剩余可用内存，启动时可能需要使用虚拟内存。";
        _warning.Margin = new Thickness(0, 6, 0, 0);
        _warning.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
        Children.Add(_warning);
    }

    private static TextBlock Label(double size)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        text.SetResourceReference(StyleProperty, "GlassPresentationTextStyle");
        text.FontSize = size;
        return text;
    }

    private static (StackPanel, Ellipse, TextBlock) Entry(DockPanel legend, string caption)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = Label(12);
        name.Text = caption;
        name.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var value = Label(13);
        value.FontWeight = FontWeights.SemiBold;
        value.Margin = new Thickness(6, 0, 0, 0);
        item.Children.Add(dot);
        item.Children.Add(name);
        item.Children.Add(value);
        DockPanel.SetDock(item, Dock.Left);
        legend.Children.Add(item);
        return (item, dot, value);
    }

    private static string Gigabytes(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    internal void Show(double used, double game, double total)
    {
        double free = Math.Max(0, total - used - game);
        bool over = total > 0 && used + game > total + .05;
        _bar.ColumnDefinitions[0].Width = new GridLength(used, GridUnitType.Star);
        _bar.ColumnDefinitions[1].Width = new GridLength(Math.Min(game, Math.Max(0, total - used)), GridUnitType.Star);
        _bar.ColumnDefinitions[2].Width = new GridLength(free, GridUnitType.Star);
        string tone = over ? "GlassPresentationDanger" : "GlassPresentationAccent";
        _game.SetResourceReference(Border.BackgroundProperty, tone);
        _allocated.Dot.SetResourceReference(Shape.FillProperty, tone);
        _used.Value.Text = Gigabytes(used);
        _allocated.Value.Text = Gigabytes(game);
        _free.Value.Text = Gigabytes(free);
        _total.Text = "共 " + Gigabytes(total);
        _warning.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"内存：已使用 {Gigabytes(used)}，游戏分配 {Gigabytes(game)}，共 {Gigabytes(total)}");
    }
}
