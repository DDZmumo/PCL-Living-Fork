// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    /// <summary>
    /// Memory overview (PanRamDisplay in launch and per-instance settings) as one segmented bar with a legend.
    /// The original positions its labels by animating margins over three star columns, which a structural copy
    /// cannot reproduce; this reads the same column proportions and label texts instead.
    /// </summary>
    private Node ProjectMemoryMeter(Grid source)
    {
        TextBlock Label(string name) => source.Children.OfType<FrameworkElement>()
            .SelectMany(child => child is Panel panel ? panel.Children.OfType<FrameworkElement>().Prepend(child) : new[] { child })
            .OfType<TextBlock>().FirstOrDefault(text => text.Name == name);

        var root = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };
        var bar = new Grid { Height = 8 };
        for (int index = 0; index < 3; index++) bar.ColumnDefinitions.Add(new ColumnDefinition());
        var track = new Border { CornerRadius = new CornerRadius(4), Child = bar };
        track.SetResourceReference(Border.BackgroundProperty, "GlassPresentationTrack");
        // Rounded ends for the whole bar; segments are separated by a 2px gap.
        bar.SizeChanged += (_, args) => bar.Clip = new RectangleGeometry(new Rect(args.NewSize), 4, 4);
        var used = new Border { Margin = new Thickness(0, 0, 2, 0), Opacity = .32 };
        used.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSecondary");
        var game = new Border { Margin = new Thickness(0, 0, 2, 0) };
        Grid.SetColumn(game, 1);
        bar.Children.Add(used); bar.Children.Add(game);
        root.Children.Add(track);

        var legend = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var total = PlainText(string.Empty); total.FontSize = 12; total.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        DockPanel.SetDock(total, Dock.Right);
        legend.Children.Add(total);
        (StackPanel Item, Ellipse Dot, TextBlock Value) Entry(string caption)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = PlainText(caption); name.FontSize = 12; name.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            var value = PlainText(string.Empty); value.FontSize = 13; value.FontWeight = FontWeights.SemiBold; value.Margin = new Thickness(6, 0, 0, 0);
            item.Children.Add(dot); item.Children.Add(name); item.Children.Add(value);
            DockPanel.SetDock(item, Dock.Left);
            legend.Children.Add(item);
            return (item, dot, value);
        }
        var usedEntry = Entry("已使用"); usedEntry.Dot.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary"); usedEntry.Dot.Opacity = .32;
        var gameEntry = Entry("游戏分配");
        var freeEntry = Entry("剩余可用"); freeEntry.Dot.SetResourceReference(Shape.FillProperty, "GlassPresentationTrack");
        root.Children.Add(legend);
        var warning = PlainText("游戏分配超过了当前剩余可用内存，启动时可能需要使用虚拟内存。"); warning.FontSize = 12; warning.Margin = new Thickness(0, 6, 0, 0);
        warning.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
        root.Children.Add(warning);

        var node = new Node(root);
        node.Pull = () =>
        {
            double Star(int index) => index < source.ColumnDefinitions.Count && source.ColumnDefinitions[index].Width.IsStar ? Math.Max(0, source.ColumnDefinitions[index].Width.Value) : 0;
            string usedText = Label("LabRamUsed")?.Text?.Trim() ?? string.Empty;
            string totalText = (Label("LabRamTotal")?.Text ?? string.Empty).Replace("/", string.Empty).Trim();
            string gameText = Label("LabRamGame")?.Text?.Trim() ?? string.Empty;
            double usedGb = Gigabytes(usedText), totalGb = Gigabytes(totalText), gameGb = Gigabytes(gameText);
            double freeGb = Math.Max(0, totalGb - usedGb - gameGb);
            bool over = totalGb > 0 && usedGb + gameGb > totalGb + .05;

            bar.ColumnDefinitions[0].Width = new GridLength(Star(0), GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(Star(1), GridUnitType.Star);
            bar.ColumnDefinitions[2].Width = new GridLength(Star(2), GridUnitType.Star);
            game.SetResourceReference(Border.BackgroundProperty, over ? "GlassPresentationDanger" : "GlassPresentationAccent");
            gameEntry.Dot.SetResourceReference(Shape.FillProperty, over ? "GlassPresentationDanger" : "GlassPresentationAccent");
            usedEntry.Value.Text = usedText;
            gameEntry.Value.Text = gameText;
            freeEntry.Value.Text = totalGb > 0 ? FormatGigabytes(freeGb) : string.Empty;
            freeEntry.Item.Visibility = totalGb > 0 ? Visibility.Visible : Visibility.Collapsed;
            total.Text = string.IsNullOrEmpty(totalText) ? string.Empty : "共 " + totalText;
            warning.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(root, $"内存：已使用 {usedText}，游戏分配 {gameText}，共 {totalText}");
        };
        return node;
    }

    private static double Gigabytes(string text)
    {
        string number = new(text.TakeWhile(character => char.IsDigit(character) || character is '.' or ',').ToArray());
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return 0;
        return text.Contains("MB", StringComparison.OrdinalIgnoreCase) ? value / 1024 : value;
    }

    private static string FormatGigabytes(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
