// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

// New view primitives over genuine original logical actions. No pointer synthesis, desktop
// material, account data, game launch, settings persistence or substitute Main assembly.
internal static class WorkbenchCommandChecks {
    internal static void Run(FixtureEnvironment environment) {
        VisualAssetChecks.VerifyChild(environment);
        using var scope = new VisualAssetChecks.Scope(environment, "workbench-commands");
        var source = new MyExtraButton { Visibility = Visibility.Visible, IsHitTestVisible = true, IsEnabled = true, CanRightClick = true, ToolTip = "播放或暂停", Logo = "M2,2 H18 V18 H2 Z" };
        var order = new List<string>();
        source.Click += (sender, args) => { Native.Check(ReferenceEquals(sender, source) && args is null, "Logical extra activation fabricated pointer input."); order.Add("left"); };
        source.RightClick += (sender, args) => { Native.Check(ReferenceEquals(sender, source) && args is null, "Logical secondary activation fabricated pointer input."); order.Add("right"); };
        var services = ControlServices.Current;
        Action<DependencyObject> originalCustom = services.RaiseCustomEvent;
        services.RaiseCustomEvent = element => { Native.Check(ReferenceEquals(element, source), "Extra custom-event target changed."); order.Add("custom"); };
        object? adapter = null;
        Window? window = null;
        ContextMenu? menu = null;
        try {
            Type adapterType = Native.Type("PCL.GlassExtraButtonAdapter");
            adapter = Activator.CreateInstance(adapterType, Native.Flags, null, [source, false, true], null)!;
            var button = Native.Property(adapterType, "View").GetValue(adapter) as Button
                ?? throw new InvalidOperationException("The new command has no native Button presentation.");
            Native.Check(!ReferenceEquals(button, source), "Workbench extra reused its source visual.");
            window = new Window { Content = button, Width = 420, Height = 280, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(!GlassMaterial.GetDesktopBackdropEnabled(button), "Desktop backdrop must default to disabled.");
            Native.Check(AutomationProperties.GetName(button) == "播放或暂停", "New extra lost its accessible semantic label.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Native.Check(order.SequenceEqual(new[] { "left", "custom" }), "New primary command lost original action/custom-event order.");
            order.Clear(); source.PerformClick(true);
            Native.Check(order.SequenceEqual(new[] { "right" }), "Secondary command fired the left custom-event tail.");
            order.Clear(); source.CanRightClick = false; source.PerformClick(true);
            source.IsEnabled = false; source.PerformClick(); source.PerformClick(true);
            Native.Check(order.Count == 0, "Disabled or unavailable extra action was executed.");
            VisualAssetChecks.Drain(); Native.Check(!button.IsEnabled, "Enabled binding did not reach the new Button.");
            source.IsEnabled = true; source.Visibility = Visibility.Collapsed; source.IsHitTestVisible = false;
            VisualAssetChecks.Drain();
            Native.Check(button.Visibility == Visibility.Collapsed && !button.IsHitTestVisible, "Visibility/hit-test binding changed original availability.");
            source.Visibility = Visibility.Visible; source.IsHitTestVisible = true; source.ToolTip = "更新后的动作说明"; source.Progress = .5;
            VisualAssetChecks.Drain();
            Native.Check(AutomationProperties.GetName(button) == "更新后的动作说明", "Dynamic tooltip label was not projected.");
            var bar = Descendants<ProgressBar>(button).Single();
            Native.Check(Math.Abs(bar.Value - .5) < .0001 && bar.Visibility == Visibility.Visible, "Real extra progress changes did not reach the new progress view.");
            var sourceViews = Descendants<UIElement>(source).Append(source).ToHashSet();
            Native.Check(!Descendants<UIElement>(button).Any(sourceViews.Contains), "A source PCL visual was embedded in the new Button.");
            CheckContrastStyle(button, source);

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
            services.RaiseCustomEvent = originalCustom;
            VisualAssetChecks.Drain(); environment.AssertNoForbiddenIo();
        }
        Console.WriteLine("PASS wallpaper-only workbench commands: new standard Button views, exact logical action/custom-event guards, observable availability/label/progress, fresh menu visuals with original command lifecycle, zero desktop backdrop HWNDs and complete cleanup.");
    }
    private static void CheckContrastStyle(Button button, MyExtraButton source) {
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
            source.IsEnabled = false; VisualAssetChecks.Drain();
            Native.Check(button.Foreground is SolidColorBrush disabled && disabled.Color == SystemColors.GrayTextColor && button.Opacity == 1,
                "High-contrast disabled command did not preserve the system disabled-text color. " + ContrastDiagnostics(button));
        } finally {
            source.IsEnabled = true; button.Style = original;
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
