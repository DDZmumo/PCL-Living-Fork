// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PCLLF.Wpf.Glass;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>Projects existing contextual selection rows into the glass rail without owning navigation.</summary>
internal static class GlassContextNavigation
{
    internal static bool RequiresSidebar(MyPageLeft page)
    {
        return page != null && page is not PageLaunchLeft && page.Visibility == Visibility.Visible &&
            page.Width != 0 && page.MaxWidth > 0 && page.Children.Count > 0;
    }

    internal static IReadOnlyList<GlassSidebarItem> Build(MyPageLeft page)
    {
        if (!RequiresSidebar(page)) return Array.Empty<GlassSidebarItem>();
        page.Dispatcher.VerifyAccess();
        if (page is PageInstanceLeft instanceLeft) instanceLeft.SyncInstanceContext();
        var result = new List<GlassSidebarItem>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Collect(page, visited, result);
        return result;
    }

    private static void Collect(DependencyObject node, HashSet<DependencyObject> visited, List<GlassSidebarItem> result)
    {
        if (!visited.Add(node)) return;
        if (GlassClientUiPolicy.Excludes(node)) return;
        // Inspect declared visibility rather than inherited IsVisible: the source page can live in a closed portal.
        if (node is UIElement element && element.Visibility != Visibility.Visible) return;
        if (node is FrameworkElement framework && framework.TemplatedParent != null) return;
        if (node is MyListItem list)
        {
            string label = string.IsNullOrWhiteSpace(list.Title) ? list.LabTitle.Text : list.Title;
            if (!string.IsNullOrWhiteSpace(label) && list.Type is MyListItem.CheckType.RadioBox or MyListItem.CheckType.Clickable)
            {
                bool command = list.Type == MyListItem.CheckType.Clickable;
                ReadIcon(list.PathLogo, out string geometry, out ImageSource image);
                result.Add(new GlassSidebarItem
                {
                    Label = label.Trim(),
                    ToolTip = TooltipText(list.ToolTip),
                    IconData = geometry,
                    Image = image,
                    IsSelected = !command && list.Checked,
                    IsCommand = command,
                    IsEnabled = list.IsEnabled,
                    Invoked = () => { if (list.IsEnabled && list.Visibility == Visibility.Visible) list.PerformClick(); },
                    Actions = RowActions(list),
                    ContextRequested = list.ContextMenu is { } menu ? target => ShowContextMenu(menu, target) : null,
                    Source = list,
                });
            }
            // Row content is read through the item itself; its accessories become sidebar actions.
            return;
        }
        // Category captions in the original left pages are small, dimmed text blocks (e.g. 社区资源 / 版本工具).
        if (node is TextBlock { Text: { Length: > 0 } caption } heading && heading.FontSize <= 13 && heading.Opacity < 1 &&
            !string.IsNullOrWhiteSpace(caption))
        {
            result.Add(new GlassSidebarItem { Label = caption.Trim(), IsHeader = true, IsEnabled = true });
            return;
        }
        if (node is MyRadioButton radio)
        {
            string label = string.IsNullOrWhiteSpace(radio.Text) ? radio.LabText.Text : radio.Text;
            if (!string.IsNullOrWhiteSpace(label))
            {
                ReadIcon(radio.ShapeLogo, out string geometry, out ImageSource image);
                result.Add(new GlassSidebarItem
                {
                    Label = label.Trim(),
                    ToolTip = TooltipText(radio.ToolTip),
                    IconData = geometry,
                    Image = image,
                    IsSelected = radio.Checked,
                    IsEnabled = radio.IsEnabled,
                    Invoked = () => { if (radio.IsEnabled && radio.Visibility == Visibility.Visible) radio.PerformClick(); },
                });
            }
            return;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency) Collect(dependency, visited, result);
        if (node is Visual or Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Collect(VisualTreeHelper.GetChild(node, i), visited, result);
    }

    private static IReadOnlyList<GlassSidebarAction> RowActions(MyListItem list)
    {
        var actions = new List<GlassSidebarAction>();
        foreach (MyIconButton button in list.Buttons ?? Array.Empty<MyIconButton>())
        {
            if (button is null || button.Visibility == Visibility.Collapsed || GlassClientUiPolicy.Excludes(button)) continue;
            actions.Add(new GlassSidebarAction
            {
                ToolTip = TooltipText(button.ToolTip) ?? string.Empty,
                IconData = button.Logo,
                Invoked = () => { if (button.IsEnabled) button.PerformClick(); },
            });
        }
        return actions;
    }

    private static void ShowContextMenu(ContextMenu source, FrameworkElement target)
    {
        bool darkText = target.TryFindResource("GlassTextBrush") is SolidColorBrush { Color: var color } && color.R + color.G + color.B < 480;
        GlassActionMenu.FromContextMenu(source, target, darkText).IsOpen = true;
    }

    private static void ReadIcon(FrameworkElement logo, out string geometry, out ImageSource image)
    {
        geometry = null;
        image = null;
        if (logo is Path { Data: { } data } && !data.Bounds.IsEmpty)
            geometry = data.ToString(CultureInfo.InvariantCulture);
        else if (logo is Image bitmap)
            image = bitmap.Source;
        else if (logo is Panel { Background: ImageBrush brush })
            image = brush.ImageSource;
        else if (logo is Border { Background: ImageBrush borderBrush })
            image = borderBrush.ImageSource;
    }

    private static string TooltipText(object tooltip) => tooltip switch
    {
        string text => text,
        TextBlock text => text.Text,
        ToolTip { Content: string text } => text,
        ToolTip { Content: TextBlock text } => text.Text,
        _ => null,
    };
}
