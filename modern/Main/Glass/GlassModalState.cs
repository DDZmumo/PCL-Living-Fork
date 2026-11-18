// SPDX-License-Identifier: MPL-2.0
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

internal interface IGlassModalState { bool IsModalOpen { get; } }
internal static class GlassModalState {
    internal static bool IsOpen(object message) => message is not null && (message is not IGlassModalState state || state.IsModalOpen);
    internal static UIElement FindActive(Panel panel) => panel.Children.OfType<UIElement>()
        .FirstOrDefault(child => child.Visibility == Visibility.Visible && IsOpen(child));
}

public partial class MyMsgText : IGlassModalState {
    bool IGlassModalState.IsModalOpen => MyConverter is { IsExited: false };
}
public partial class MyMsgInput : IGlassModalState {
    bool IGlassModalState.IsModalOpen => MyConverter is { IsExited: false };
}
public partial class MyMsgSelect : IGlassModalState {
    bool IGlassModalState.IsModalOpen => MyConverter is { IsExited: false };
}
public partial class MyMsgLogin : IGlassModalState {
    bool IGlassModalState.IsModalOpen => _modalConverter is { IsExited: false };
}
