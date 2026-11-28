// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// A pick-one list that drops down under a button and looks exactly like the glass combo box's list: the same frosted
/// panel and shadow, rows that light up on hover, and the current choice marked by a soft fill and a short accent bar.
/// </summary>
internal static class GlassChoicePopup
{
    internal sealed record Option(string Label, Action Run, string? Icon = null);
    private static readonly DependencyProperty ActivePopupProperty = DependencyProperty.RegisterAttached(
        "ActivePopup", typeof(Popup), typeof(GlassChoicePopup));

    // The brushes the rows use; the popup is not in the page's tree, so they are copied from the button that opens it.
    private static readonly string[] Brushes = ["GlassPresentationFill", "GlassPresentationEdge", "GlassPresentationHover",
        "GlassPresentationAccent", "GlassPresentationText"];

    internal static void Show(FrameworkElement target, IReadOnlyList<string> options, int selected, Action<int> choose)
        => Show(target, options.Select((label, index) => new Option(label, () => choose(index))).ToArray(), selected);

    internal static Popup Show(FrameworkElement target, IReadOnlyList<Option> options, int selected = -1,
        PlacementMode placement = PlacementMode.Bottom)
    {
        if (Current(target) is { IsOpen: true } active) {
            active.IsOpen = false;
            return active;
        }
        var popup = new Popup { PlacementTarget = target, Placement = placement, AllowsTransparency = true, StaysOpen = false,
            PopupAnimation = MotionPreferences.AllowsAnimation ? PopupAnimation.Fade : PopupAnimation.None,
            HorizontalOffset = -12, Focusable = false };
        var list = new StackPanel();
        var surface = new GlassSurface { Child = list, CornerRadius = new CornerRadius(10), Padding = new Thickness(4), BlurRadius = 24,
            BorderThickness = new Thickness(1), MinWidth = Math.Max(target.ActualWidth, 200),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = .28, Color = Colors.Black, RenderingBias = RenderingBias.Performance } };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        var backdrop = new Grid { Children = { surface } };
        GlassPopupBackdrop.SetEnable(backdrop, true);
        GlassPopupBackdrop.SetCornerRadius(backdrop, 10);
        var root = new Border { Padding = new Thickness(12, 2, 12, 18), Background = System.Windows.Media.Brushes.Transparent, Child = backdrop };
        foreach (string key in Brushes)
            if (target.TryFindResource(key) is { } brush) root.Resources[key] = brush;
        root.Resources[typeof(TextBlock)] = target.TryFindResource(typeof(TextBlock)) ?? new Style(typeof(TextBlock));
        popup.Child = root;

        for (int index = 0; index < options.Count; index++)
        {
            Option option = options[index];
            list.Children.Add(Row(option, index == selected, () => {
                if (!popup.IsOpen) return;
                popup.IsOpen = false;
                if (target.IsLoaded && target.IsVisible) option.Run();
            }));
        }
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Cycle);
        popup.KeyDown += (_, args) => { if (args.Key == Key.Escape) { popup.IsOpen = false; args.Handled = true; } };
        popup.Opened += (_, _) => { if (list.Children.Count > 0) ((UIElement)list.Children[Math.Clamp(selected, 0, list.Children.Count - 1)]).Focus(); };
        target.Unloaded += CloseOnUnload;
        popup.Closed += (_, _) => {
            target.Unloaded -= CloseOnUnload;
            if (!ReferenceEquals(Current(target), popup)) return;
            target.ClearValue(ActivePopupProperty);
            if (target.IsLoaded && target.IsVisible && (root.IsKeyboardFocusWithin || Keyboard.FocusedElement is null)) target.Focus();
        };
        target.SetValue(ActivePopupProperty, popup);
        popup.IsOpen = true;
        return popup;

        void CloseOnUnload(object sender, RoutedEventArgs args) => popup.IsOpen = false;
    }

    internal static Popup? Current(FrameworkElement target) => (Popup?)target.GetValue(ActivePopupProperty);

    // Same anatomy as GlassPresentationComboItem: a rounded hover surface, an accent pill for the selection, the text.
    private static FrameworkElement Row(Option option, bool selected, Action pick)
    {
        var surface = new Border { CornerRadius = new CornerRadius(6), Background = System.Windows.Media.Brushes.Transparent };
        var pill = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Hidden };
        pill.SetResourceReference(Border.BackgroundProperty, "GlassPresentationAccent");
        var label = new TextBlock { Text = option.Label, FontSize = 14, Margin = new Thickness(option.Icon is null ? 14 : 38, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var row = new Grid { MinHeight = 34, Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Focusable = true, Background = System.Windows.Media.Brushes.Transparent,
            FocusVisualStyle = null, Children = { surface, pill, label } };
        if (option.Icon is { } iconData) {
            var icon = new Path { Data = Geometry.Parse(iconData), Width = 16, Height = 16, Stretch = Stretch.Uniform,
                Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            icon.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
            row.Children.Add(icon);
        }
        AutomationProperties.SetName(row, option.Label);
        void Light(bool on)
        {
            if (on || selected) surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHover");
            else surface.Background = System.Windows.Media.Brushes.Transparent;
        }
        Light(false);
        row.MouseEnter += (_, _) => Light(true);
        row.MouseLeave += (_, _) => Light(row.IsKeyboardFocused);
        row.GotKeyboardFocus += (_, _) => Light(true);
        row.LostKeyboardFocus += (_, _) => Light(row.IsMouseOver);
        row.MouseLeftButtonUp += (_, args) => { args.Handled = true; pick(); };
        row.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Enter or Key.Space) { args.Handled = true; pick(); }
            else if (args.Key is Key.Down or Key.Up)
            {
                args.Handled = true;
                row.MoveFocus(new TraversalRequest(args.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
            }
        };
        return row;
    }
}
