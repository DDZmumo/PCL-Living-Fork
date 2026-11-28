// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

// New view primitives over genuine original logical actions and an isolated in-window backdrop.
internal static class WorkbenchCommandChecks {
    internal static void Run(FixtureEnvironment environment) {
        VisualAssetChecks.VerifyChild(environment);
        using var scope = new VisualAssetChecks.Scope(environment, "workbench-commands");
        bool shown = true;
        double? progress = .5;
        var order = new List<string>();
        object? adapter = null;
        Window? window = null;
        ContextMenu? menu = null;
        try {
            var action = new GlassFloatingAction("MusicFixture", "播放或暂停", "M2,2 H18 V18 H2 Z", false,
                () => shown, () => order.Add("left"), () => order.Add("right"), reduceMotion: true);
            action.TotalProgress = () => progress;
            action.RefreshVisibility();
            adapter = action;
            var button = action.View;
            window = new Window { Content = button, Width = 420, Height = 280, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(!GlassMaterial.GetDesktopBackdropEnabled(button), "Desktop backdrop must default to disabled.");
            Native.Check(AutomationProperties.GetName(button) == "播放或暂停", "New extra lost its accessible semantic label.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Native.Check(order.SequenceEqual(new[] { "left" }), "The floating action should execute once.");
            shown = false; action.RefreshVisibility();
            Native.Check(button.Visibility == Visibility.Collapsed && !button.IsHitTestVisible, "Hidden actions still intercept input.");
            shown = true; action.RefreshVisibility(); action.RefreshProgress();
            Native.Check((double)Native.Get(action, "_ringValue")! == .5, "The task ring lost its application progress.");
            CheckContrastStyle(button);
            if (!SystemParameters.HighContrast) CheckFloatingFrost(window, button, action);

            int clicks = 0, commands = 0, opened = 0, closed = 0;
            var menuTrace = new List<string>();
            var original = new ContextMenu(); original.Opened += (_, _) => { opened++; menuTrace.Add("source-open"); }; original.Closed += (_, _) => { closed++; menuTrace.Add("source-close"); };
            var originalItem = new MenuItem { Header = new TextBlock { Text = "原操作" }, IsCheckable = true, CommandParameter = "owned-command-argument",
                Command = new TestCommand(parameter => { Native.Check(Equals(parameter, "owned-command-argument"), "Menu command parameter changed."); commands++; menuTrace.Add("command"); }) };
            originalItem.Click += (_, _) => { clicks++; menuTrace.Add("source-click"); }; original.Items.Add(originalItem);
            var submenu = new MenuItem { Header = "子操作" }; submenu.Items.Add(new MenuItem { Header = "嵌套操作" }); original.Items.Add(submenu);
            Type menuType = Native.Type("PCL.GlassActionMenu");
            MethodInfo project = menuType.GetMethod("FromContextMenu", Native.Flags, null, [typeof(ContextMenu), typeof(FrameworkElement), typeof(bool), typeof(bool)], null)!;
            menu = (ContextMenu)project.Invoke(null, [original, button, false, true])!;
            menu.Opened += (_, _) => menuTrace.Add("proxy-open"); menu.Closed += (_, _) => menuTrace.Add("proxy-close");
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = 20; menu.VerticalOffset = 20; menu.IsOpen = true;
            VisualAssetChecks.Observe(200);
            Native.Check(CountNativeHosts() == 0, "Wallpaper-only command menu created a desktop material HWND.");
            var childMenu = (MenuItem)menu.Items[1]; childMenu.IsSubmenuOpen = true; VisualAssetChecks.Observe(100);
            Native.Check(CountNativeHosts() == 0, "Wallpaper-only submenu created a desktop material HWND.");
            childMenu.IsSubmenuOpen = false;
            var item = (MenuItem)menu.Items[0]; item.ApplyTemplate();
            Native.Check(!ReferenceEquals(item.Header, originalItem.Header) && item.Command is not null &&
                Equals(item.CommandParameter, originalItem.CommandParameter) && ReferenceEquals(item.CommandTarget, originalItem.CommandTarget ?? originalItem),
                "Menu projection reused a visual or lost its original command parameter/target.");
            typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
            VisualAssetChecks.Observe(100); menu.IsOpen = false;
            VisualAssetChecks.Pump(() => closed == 1, "projected command menu actual Closed lifecycle");
            VisualAssetChecks.Drain();
            Native.Check(clicks == 1 && commands == 1 && opened == 1 && closed == 1 && originalItem.IsChecked,
                "New menu lost original Click/command/checked/Open/Closed semantics: " + clicks + "/" + commands + "/" + opened + "/" + closed + "; checked=" + originalItem.IsChecked + "; trace=" + string.Join(",", menuTrace));
            Native.Property(menuType, "CanActivateOwner").SetValue(menu, new Func<bool>(() => false));
            typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
            Native.Check(!item.Command!.CanExecute(item.CommandParameter), "Inactive owner left its projected command executable.");
            item.Command.Execute(item.CommandParameter);
            Native.Check(clicks == 1 && commands == 1 && originalItem.IsChecked,
                "Late projected activation reached an inactive owner or changed its checked state.");
            Native.Check(CountNativeHosts() == 0, "Command popup cleanup left a native desktop host.");
            environment.AssertNoForbiddenIo();
        } finally {
            if (menu is not null) menu.IsOpen = false;
            (adapter as IDisposable)?.Dispose();
            if (window is not null) { window.Content = null; window.Close(); }
            VisualAssetChecks.Drain(); environment.AssertNoForbiddenIo();
        }
        Console.WriteLine("PASS wallpaper-only workbench commands: new standard Button views, exact logical action/custom-event guards, observable availability/label/progress, fresh menu visuals with original command lifecycle, zero desktop backdrop HWNDs and complete cleanup.");
    }

    private static void CheckFloatingFrost(Window window, Button button, object adapter)
    {
        var host = new Grid();
        var wallpaper = new Image { Stretch = Stretch.Fill };
        var frame = new Border { Margin = new Thickness(32, 15, 17, 22), Background = Stripes(Brushes.White, Brushes.Black), Child = wallpaper };
        window.Content = null;
        host.Children.Add(frame);
        host.Children.Add(button);
        window.Content = host;
        try {
            Native.Call(adapter.GetType(), "SetBackdrop", adapter, frame);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var fills = button.Style.Setters.OfType<Setter>().Concat(button.Style.Triggers.OfType<Trigger>().SelectMany(trigger => trigger.Setters.OfType<Setter>()));
            Native.Check(fills.Where(setter => setter.Property == Control.BackgroundProperty || setter.Property == Control.BorderBrushProperty)
                .All(setter => setter.Value is SolidColorBrush brush && brush.Color.R == brush.Color.G && brush.Color.G == brush.Color.B),
                "A dark floating action still has a colored fill or edge in one of its interaction states.");
            if (!GlassMaterial.AllowsTransparency(button)) return;

            var brush = (VisualBrush)Native.Get(Native.Get(adapter, "_sceneFrost")!, "_body")!;
            Native.Check(!GlassMaterial.GetDesktopBackdropEnabled(button) && ReferenceEquals(brush.Visual, frame),
                "The dark action did not sample the default page when native glass was unavailable.");
            button.Opacity = 0;
            int sharpContrast = Contrast(Sample(host, button));
            button.Opacity = 1;
            var gray = Sample(host, button);
            int frostContrast = Contrast(gray);
            Native.Check(sharpContrast > 160 && frostContrast < sharpContrast / 3 &&
                gray.Chunk(4).All(pixel => Math.Abs(pixel[0] - pixel[2]) <= 1 && Math.Abs(pixel[1] - pixel[2]) <= 1),
                $"Default page stripes did not render as neutral frost: contrast {sharpContrast}->{frostContrast}.");

            var wallpaperSource = new DrawingImage(new GeometryDrawing(Stripes(Brushes.DarkOrange, Brushes.Maroon), null,
                new RectangleGeometry(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight))));
            wallpaper.Source = wallpaperSource;
            GlassMaterial.SetWallpaperSource(host, wallpaperSource);
            GlassMaterial.SetWallpaperHost(host, wallpaper);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var colored = Sample(host, button).Chunk(4).ToArray();
            Native.Check(ReferenceEquals(brush.Visual, frame) && colored.Average(pixel => pixel[2] - pixel[0]) > 18,
                "Dark floating frost did not keep the wallpaper color from the same page sample.");

            Native.Call(adapter.GetType(), "RefreshAppearance", adapter, true, true);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(brush.Visual is null, "The light action retained the dark scene sample.");
            Native.Call(adapter.GetType(), "RefreshAppearance", adapter, false, true);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(ReferenceEquals(brush.Visual, frame) && button.Background is SolidColorBrush { Color.A: > 0 },
                $"Switching back to the dark theme did not restore the frost and tint. Source bound: {ReferenceEquals(brush.Visual, frame)}. Tint: {button.Background}.");
            Native.Call(adapter.GetType(), "SetBackdropSuspended", adapter, true);
            window.Width += 10; window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Call(adapter.GetType(), "RefreshAppearance", adapter, false, true);
            Native.Check(brush.Visual is null && button.IsVisible, "Layout during page motion reattached the live backdrop or hid the floating action.");
            Native.Call(adapter.GetType(), "SetBackdropSuspended", adapter, false);
            Native.Call(adapter.GetType(), "RefreshAppearance", adapter, false, true);
            Native.Check(ReferenceEquals(brush.Visual, frame), "The floating backdrop did not return once the page settled.");
            button.Visibility = Visibility.Collapsed;
            Native.Check(brush.Visual is null, "A hidden dark floating action kept sampling the page.");
            Console.WriteLine($"Floating action dark frost over the default page and colored wallpaper: stripe contrast {sharpContrast}->{frostContrast}.");
        } finally {
            button.Visibility = Visibility.Visible;
            host.Children.Remove(button);
            window.Content = button;
            window.UpdateLayout(); VisualAssetChecks.Drain();
        }

        static DrawingBrush Stripes(Brush light, Brush dark) {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 4, 8))));
            drawing.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(4, 0, 4, 8))));
            return new DrawingBrush(drawing) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 8, 8), TileMode = TileMode.Tile };
        }
        static byte[] Sample(Grid host, Button button) {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var point = button.TranslatePoint(new Point(11, 11), host);
            var pixels = new byte[24 * 4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 24, 1), pixels, pixels.Length, 0);
            return pixels;
        }
        static int Contrast(byte[] pixels) {
            int[] channels = pixels.Chunk(4).Select(pixel => (int)pixel[2]).ToArray();
            return channels.Max() - channels.Min();
        }
    }

    private static void CheckContrastStyle(Button button) {
        Type chrome = Native.Type("PCL.GlassCommandChrome");
        MethodInfo create = chrome.GetMethod("BuildButton", Native.Flags, null, [typeof(bool), typeof(bool)], null)!;
        MethodInfo refresh = chrome.GetMethod("RefreshContrastResources", Native.Flags)!;
        object textKey = Native.Field(chrome, "ContrastTextKey").GetValue(null)!;
        var palette = chrome.GetFields(Native.Flags).Where(field => typeof(ResourceKey).IsAssignableFrom(field.FieldType))
            .Select(field => field.GetValue(null)!).ToDictionary(key => key, key => (Had: button.Resources.Contains(key), Value: button.Resources[key]));
        Style original = button.Style;
        try {
            button.Style = (Style)create.Invoke(null, [false, true])!;
            button.ApplyTemplate(); VisualAssetChecks.Drain();
            Native.Check(button.Style.IsSealed, "Contrast palette refresh must cover an already applied cached style.");
            button.Resources[textKey] = Brushes.Magenta;
            Native.Check(button.Foreground is SolidColorBrush stale && stale.Color == Colors.Magenta,
                "The private contrast resource change did not reach the command foreground.");
            refresh.Invoke(null, [button.Resources]);
            Native.Check(button.Foreground is SolidColorBrush foreground && foreground.Color == SystemColors.WindowTextColor &&
                button.Background is SolidColorBrush background && background.Color == SystemColors.WindowColor,
                "High-contrast command style did not use the system window/text colors. " + ContrastDiagnostics(button));
            button.IsEnabled = false; VisualAssetChecks.Drain();
            Native.Check(button.Foreground is SolidColorBrush disabled && disabled.Color == SystemColors.GrayTextColor && button.Opacity == 1,
                "High-contrast disabled command did not preserve the system disabled-text color. " + ContrastDiagnostics(button));
        } finally {
            button.IsEnabled = true; button.Style = original;
            foreach (var (key, saved) in palette) { if (saved.Had) button.Resources[key] = saved.Value; else button.Resources.Remove(key); }
            VisualAssetChecks.Drain();
        }
    }
    private static string ContrastDiagnostics(Button button) {
        string Describe(DependencyProperty property) {
            object value = button.GetValue(property);
            var source = DependencyPropertyHelper.GetValueSource(button, property);
            object local = button.ReadLocalValue(property);
            return property.Name + "=" + (value is SolidColorBrush brush ? brush.Color.ToString() : value?.GetType().Name ?? "null") +
                "; valueSource=" + source.BaseValueSource + "; expression=" + source.IsExpression +
                "; local=" + (local == DependencyProperty.UnsetValue ? "unset" : local?.GetType().Name ?? "null");
        }
        string Resource(object key) => button.TryFindResource(key) is SolidColorBrush brush ? brush.Color.ToString() : "not-solid";
        return Describe(Control.ForegroundProperty) + "; " + Describe(Control.BackgroundProperty) +
            "; enabled=" + button.IsEnabled + "; hover=" + button.IsMouseOver + "; pressed=" + button.IsPressed +
            "; keyboardFocused=" + button.IsKeyboardFocused + "; focusWithin=" + button.IsKeyboardFocusWithin +
            "; systemText=" + SystemColors.WindowTextColor + "; systemWindow=" + SystemColors.WindowColor +
            "; resolvedText=" + Resource(SystemColors.WindowTextBrushKey) + "; resolvedWindow=" + Resource(SystemColors.WindowBrushKey);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static int CountNativeHosts() {
        int count = 0;
        EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "PCLLF.Glass.DesktopBackdrop.1") count++;
            return true;
        }, 0);
        return count;
    }
    private sealed class TestCommand(Action<object?> execute) : ICommand {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
    private delegate bool WindowEnum(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowEnum callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
