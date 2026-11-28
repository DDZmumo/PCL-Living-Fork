// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

internal sealed class GlassHistoryRow : Grid, IEnvironmentHistoryPageRow
{
    private readonly CheckBox _check;
    private readonly Button _select;
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _description = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    internal StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    public FrameworkElement Element => this;
    public JObject? Point { get; private set; }
    public bool Checked { get => _check.IsChecked == true; set => _check.IsChecked = value; }

    internal GlassHistoryRow()
    {
        MinHeight = 52;
        Background = System.Windows.Media.Brushes.Transparent; // the whole row tracks the pointer
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Edge-to-edge hover band, like the other resource lists.
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        SetColumnSpan(band, 3);
        Children.Add(band);
        var hover = new PCLLF.Wpf.Motion.SpringMotion(this, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new PCLLF.Wpf.Motion.SpringParameters(.18, 1));
        MouseEnter += (_, _) => hover.Retarget(1);
        MouseLeave += (_, _) => hover.Retarget(0);
        var separator = new Border { Height = .7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32, 0, 8, 0), IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        SetColumnSpan(separator, 3);
        Children.Add(separator);

        _check = new CheckBox {
            Style = GlassDialogControls.Style("GlassPresentationRowSelector"),
            Width = 28, Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _description.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        _select = new Button {
            Style = GlassDialogControls.Style("GlassPresentationBandRow"), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _description } }
        };
        SetColumn(_select, 1);
        SetColumn(Actions, 2);
        Children.Add(_check);
        Children.Add(_select);
        Children.Add(Actions);
    }

    public void Bind(JObject point)
    {
        Point = point;
        string title = (string?)point["label"] ?? "还原点";
        DateTime? created = (DateTime?)point["created"];
        string date = created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        string health = (bool?)point["healthy"] == true ? " · 正常运行" : "";
        string? description = (string?)point["description"];
        _title.Text = title;
        _description.Text = date + health + (string.IsNullOrEmpty(description) ? "" : " · " + description);
        ToolTip = title + "\n" + _description.Text;
        AutomationProperties.SetName(_check, "选择 " + title);
        AutomationProperties.SetName(_select, title);
    }

    public void SetButtons(List<EnvironmentHistoryPageIcon> buttons)
    {
        Actions.Children.Clear();
        foreach (var button in buttons) Actions.Children.Add(button.Element);
    }

    public void Subscribe(Action click, Action<object> changed)
    {
        _select.Click += (_, _) => click();
        _check.Checked += (_, _) => changed(this);
        _check.Unchecked += (_, _) => changed(this);
    }
}
