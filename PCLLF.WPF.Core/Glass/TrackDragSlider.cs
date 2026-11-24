// SPDX-License-Identifier: MPL-2.0
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PCLLF.Wpf.Glass;

/// <summary>Continues a move-to-point press as a native thumb drag.</summary>
public sealed class TrackDragSlider : Slider
{
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var track = Template?.FindName("PART_Track", this) as Track;
        var thumb = track?.Thumb;
        bool trackPress = !e.Handled && IsMoveToPointEnabled && thumb is { IsMouseOver: false, IsDragging: false };
        base.OnPreviewMouseLeftButtonDown(e);
        if (!trackPress || !e.Handled || track is null || thumb is null) return;

        // Update the thumb's position before it records the drag origin and captures the pointer.
        track.UpdateLayout();
        if (!thumb.IsEnabled || !thumb.IsVisible) return;
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left, e.StylusDevice) {
            RoutedEvent = Mouse.MouseDownEvent
        });
    }
}
