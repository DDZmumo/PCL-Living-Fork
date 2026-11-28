// SPDX-License-Identifier: MPL-2.0
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL;

/// <summary>
/// The instance overview's lead: a large rounded icon, the instance name, its description, and the game version and
/// loaders as chips so the instance's identity reads at a glance.
/// </summary>
internal sealed class GlassInstanceHeader : Grid
{
    private readonly TextBlock _subtitle;
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 8, 0, 0) };
    internal string Info
    {
        get => _subtitle.Text;
        set { _subtitle.Text = value; RefreshChips(value); }
    }

    internal GlassInstanceHeader(ModMinecraft.McInstance instance)
    {
        MinHeight = 64;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        var logo = new Image {
            Source = GlassShellRuntime.LoadInstanceIcon(instance.Logo, instance.PathVersion), Stretch = Stretch.Uniform,
            Width = 56, Height = 56, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 12, 12)
        };
        var title = new TextBlock { Text = instance.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        _subtitle = new TextBlock { FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, _subtitle, _chips } };
        SetColumn(labels, 1);
        Children.Add(logo);
        Children.Add(labels);
        Tag = instance;
        Info = instance.Info;
    }

    // "正式版 1.21.11, Fabric 0.19.3" → chips for each component; descriptive text stays in the subtitle line.
    private void RefreshChips(string info)
    {
        _chips.Children.Clear();
        string[] parts = (info ?? "").Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool looksLikeVersions = parts.Length > 0 && parts.All(part => part.Any(char.IsDigit) && part.Length <= 32);
        _chips.Visibility = looksLikeVersions ? Visibility.Visible : Visibility.Collapsed;
        _subtitle.Visibility = looksLikeVersions ? Visibility.Collapsed : Visibility.Visible;
        if (!looksLikeVersions) return;
        for (int index = 0; index < parts.Length; index++)
            _chips.Children.Add(GlassHeroCard.Chip(parts[index], accent: index == 0));
    }
}
