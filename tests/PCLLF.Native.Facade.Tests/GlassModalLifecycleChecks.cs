// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PCL;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassModalLifecycleChecks {
    internal static void Run(FixtureEnvironment environment) {
        using var scope = new VisualAssetChecks.Scope(environment, "modal-lifecycle");
        StateOnly();
        Effects();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS modal lifecycle: login/text/input/select logical exit state, unknown visible fallback, immediate scrim input ownership, resized DPI-aware frozen snapshot, independent projected input and reversible blur/close cleanup; no authentication work.");
    }
    private static void StateOnly() {
        Type state = Native.Type("PCL.GlassModalState");
        foreach (var (type, field) in new[] {
            (typeof(MyMsgText), "MyConverter"), (typeof(MyMsgInput), "MyConverter"),
            (typeof(MyMsgSelect), "MyConverter"), (typeof(MyMsgLogin), "_modalConverter")
        }) {
            // Deliberately evaluate only the bool seam. No WPF base property, constructor,
            // native login initialization, worker, clipboard, browser or request is invoked.
            object dialog = RuntimeHelpers.GetUninitializedObject(type);
            var converter = new ModMain.MyMsgBoxConverter();
            Native.Field(type, field).SetValue(dialog, converter);
            Native.Check((bool)Native.Call(state, "IsOpen", null, dialog)!, "Open modal type was omitted: " + type.Name);
            converter.IsExited = true;
            Native.Check(!(bool)Native.Call(state, "IsOpen", null, dialog)!, "Exited modal still owns focus: " + type.Name);
        }
        var panel = new Grid(); var unknown = new Grid(); panel.Children.Add(unknown);
        Native.Check(ReferenceEquals(Native.Call(state, "FindActive", null, panel), unknown), "Unknown visible modal became invisible.");
        unknown.Visibility = Visibility.Collapsed;
        Native.Check(Native.Call(state, "FindActive", null, panel) is null, "Collapsed unknown modal kept the scrim active.");
    }
    private static void Effects() {
        var root = new Grid();
        var background = new Grid { Background = Brushes.SteelBlue };
        background.Children.Add(new TextBlock { Text = "Owned application scene", FontSize = 20 });
        var resize = new Canvas(); resize.Children.Add(new Border { Width = 8, Height = 300, Background = Brushes.Transparent });
        Panel.SetZIndex(resize, 15);
        var scrim = new Border { Background = Brushes.Black }; Panel.SetZIndex(scrim, 25);
        var input = new TextBox { Text = "before" };
        background.Children.Add(new Grid { Visibility = Visibility.Hidden, Children = { input } });
        using var presentation = GlassPresentation.Create(input);
        var modalCard = new GlassSurface { BlurRadius = 22, Child = presentation.View };
        var dialog = new Border { Width = 280, Height = 160, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = modalCard };
        Panel.SetZIndex(dialog, 30);
        root.Children.Add(background); root.Children.Add(resize); root.Children.Add(scrim); root.Children.Add(dialog);
        var window = new Window { Content = root, Width = 640, Height = 440, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        object? effects = null;
        try {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            var projectedInput = VisualDescendants(presentation.View).OfType<TextBox>().Single();
            int windowKeys = 0;
            root.KeyDown += (_, _) => windowKeys++;
            foreach (var key in new[] { System.Windows.Input.Key.F5, System.Windows.Input.Key.Escape })
            {
                projectedInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(projectedInput), Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            }
            Native.Check(windowKeys == 2, $"Two projected shortcuts reached the window {windowKeys} times.");
            Type cacheType = typeof(GlassSurface).Assembly.GetType("PCLLF.Wpf.Glass.WallpaperBlurCache", true)!;
            long buildsBeforeOpen = (long)Native.Property(cacheType, "BuildCount").GetValue(null)!;
            Type type = Native.Type("PCL.GlassModalEffects");
            effects = Activator.CreateInstance(type, Native.Flags, null, [root, background, scrim, dialog], null)!;
            object message = new(); Native.Call(type, "SetMessage", effects, message);
            Native.Check(scrim.Visibility == Visibility.Visible && scrim.IsHitTestVisible && !background.IsHitTestVisible && !background.IsEnabled && dialog.IsHitTestVisible &&
                System.Windows.Input.KeyboardNavigation.GetTabNavigation(dialog) == System.Windows.Input.KeyboardNavigationMode.Cycle &&
                ReferenceEquals(root.InputHitTest(new Point(3, 90)), scrim), "The first modal frame left an outer resize grip clickable.");
            VisualAssetChecks.Pump(() => (double)Native.Property(type, "Progress").GetValue(effects)! > .999, "modal open endpoint");
            VisualAssetChecks.Drain();
            long buildsAfterOpen = (long)Native.Property(cacheType, "BuildCount").GetValue(null)!;
            Native.Check(buildsAfterOpen - buildsBeforeOpen <= 1, $"Modal open spring triggered {buildsAfterOpen - buildsBeforeOpen} wallpaper blur rebuilds instead of <= 1.");
            var first = GlassMaterial.GetWallpaperSource(dialog) as BitmapSource;
            Native.Check(first is { IsFrozen: true } && dialog.Effect is null && background.Effect is BlurEffect,
                "Modal source did not use a frozen app-only snapshot with sharp foreground.");
            projectedInput.Text = "edited-in-modal";
            Native.Check(input.Text == "edited-in-modal", "Projected modal text did not update its actual source.");
            Native.Call(type, "SetWindowResizing", effects, true);
            background.Width = 480; background.Height = 280; window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(ReferenceEquals(GlassMaterial.GetWallpaperSource(dialog), first), "Dragging replaced the modal screenshot before the final layout.");
            Native.Call(type, "SetWindowResizing", effects, false);
            VisualAssetChecks.Pump(() => GlassMaterial.GetWallpaperSource(dialog) is BitmapSource next && !ReferenceEquals(next, first), "modal resized snapshot");
            var resized = (BitmapSource)GlassMaterial.GetWallpaperSource(dialog)!; DpiScale dpi = VisualTreeHelper.GetDpi(background);
            Native.Check(resized.PixelWidth == (int)Math.Ceiling(background.ActualWidth * dpi.DpiScaleX) && resized.PixelHeight == (int)Math.Ceiling(background.ActualHeight * dpi.DpiScaleY),
                "Modal resize snapshot retained old bounds or ignored DPI.");
            Native.Call(type, "SetMessage", effects, new object?[] { null });
            Native.Check(background.IsHitTestVisible && background.IsEnabled && !dialog.IsHitTestVisible && !scrim.IsHitTestVisible, "Logical close delayed input ownership.");
            Native.Call(type, "SetMessage", effects, new object());
            Native.Check(!background.IsHitTestVisible && dialog.IsHitTestVisible, "Reopening during close did not reverse input ownership.");
            VisualAssetChecks.Drain();
            long buildsBeforeClose = (long)Native.Property(cacheType, "BuildCount").GetValue(null)!;
            Native.Call(type, "SetMessage", effects, new object?[] { null });
            VisualAssetChecks.Pump(() => background.Effect is null && scrim.Visibility == Visibility.Collapsed, "modal close fade endpoint");
            long buildsAfterClose = (long)Native.Property(cacheType, "BuildCount").GetValue(null)!;
            Native.Check(buildsAfterClose == buildsBeforeClose, $"Modal close spring triggered {buildsAfterClose - buildsBeforeClose} redundant wallpaper blur rebuilds.");
            Native.Check(GlassMaterial.GetWallpaperSource(dialog) is null, "Closed modal retained its focus snapshot.");
        } finally {
            (effects as IDisposable)?.Dispose(); window.Content = null; window.Close(); VisualAssetChecks.Drain();
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root) {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in VisualDescendants(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }
}
