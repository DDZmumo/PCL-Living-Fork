// SPDX-License-Identifier: MPL-2.0
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCLLF.Presentation.Controls;

namespace PCL;

/// <summary>Client-only presentation choices; source data and download implementations remain intact.</summary>
internal static class GlassClientUiPolicy
{
    // Check the key first: GlassShellPreferred itself reads the non-hidden UiShellStyle setting.
    // This changes the modern presentation's read policy without changing stored preferences.
    internal static bool IgnoresHiddenSetting(string key)
        => key?.StartsWith("UiHidden", StringComparison.Ordinal) == true && FormMain.GlassShellPreferred;

    internal static bool Excludes(DependencyObject source) => source switch
    {
        MyCard { Name: "CardSwitch" } card when AppearanceSettings(card) => true,
        MyIconButtonView button => ServerAction(Caption(button.ToolTip)),
        MyButtonView button => ServerAction(button.Text),
        MyTextButtonView button => ServerAction(button.Text),
        MyIconTextButtonView button => ServerAction(button.Text),
        MyExtraTextButtonView button => ServerAction(button.Text),
        MenuItem item => ServerAction(Caption(item.Header)),
        ButtonBase button => ServerAction(Caption(button.Content)),
        MyListItem item when NavigationItem(item) => ServerAction(item.Title),
        MyRadioButton radio when NavigationItem(radio) => ServerAction(radio.Text),
        _ => false,
    };

    internal static bool ServerAction(string caption)
        => caption?.Trim().TrimEnd('…') is "下载服务端" or "安装服务端" or "安装为服务端" or "服务端文件" or "下载服务端文件";

    private static bool NavigationItem(DependencyObject source)
    {
        for (DependencyObject parent = source; parent != null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is MyPageLeft) return true;
        return false;
    }

    private static bool AppearanceSettings(DependencyObject source)
    {
        for (DependencyObject parent = source; parent != null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is PageSetupUI) return true;
        return false;
    }

    private static string Caption(object content) => content switch
    {
        string text => text,
        TextBlock text => text.Text,
        AccessText text => text.Text,
        ToolTip tip => Caption(tip.Content),
        ContentControl control => Caption(control.Content),
        Panel panel => string.Concat(panel.Children.OfType<FrameworkElement>().Select(Caption)),
        _ => null,
    };
}
