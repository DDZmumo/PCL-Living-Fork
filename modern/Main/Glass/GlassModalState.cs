// SPDX-License-Identifier: MPL-2.0
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

internal interface IGlassModalState { bool IsModalOpen { get; } }
/// <summary>A message dialog that asks to confirm a destructive or risky action (legacy IsWarn).</summary>
internal interface IGlassModalTone { bool IsWarning { get; } }
internal static class GlassModalState {
    internal static bool IsWarning(object? message) => message is IGlassModalTone { IsWarning: true };
    /// <summary>Whether the message dialog that contains <paramref name="element"/> is a warning.</summary>
    internal static bool IsWarningAncestor(DependencyObject? element) {
        for (DependencyObject? parent = element; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is IGlassModalTone tone) return tone.IsWarning;
        return false;
    }
    internal static bool IsOpen(object message) => message is not null && (message is not IGlassModalState state || state.IsModalOpen);
    internal static UIElement FindActive(Panel panel) => panel.Children.OfType<UIElement>()
        .FirstOrDefault(child => child.Visibility == Visibility.Visible && IsOpen(child));
}

