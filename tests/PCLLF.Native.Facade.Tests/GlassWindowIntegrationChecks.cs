// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PCL;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Windowing;
using PCLLF.Wpf.Glass;
using Application = System.Windows.Application;
using MainHost = PCLLF.Presentation.Windowing.LegacyMainWindowHost<PCL.FormMain.PageStackData, PCL.ModMinecraft.McInstance, PCL.ModAnimation.AniData>;

namespace PCLLF.Native.Facade.Tests;

// Genuine FormMain constructor, BAML and GlassShellIntegration. Only pre-existing platform ports
// and explicit presentation inputs are fixture-owned; original startup/termination never run.
internal static class GlassWindowIntegrationChecks {
    internal static void Run(FixtureEnvironment environment) {
        VisualAssetChecks.VerifyChild(environment);
        using var scope = new VisualAssetChecks.Scope(environment, "glass-window");
        bool interactive = Environment.GetEnvironmentVariable("PCLLF_GLASS_INTERACTIVE") == "1";
        object? previousMain = Native.State(typeof(ModMain), "FrmMain").GetValue(null);
        FormMain? window = null;
        MainHost? windowPorts = null;
        WindowResizer? previewResizer = null;
        var resizeWorkers = new List<Thread>();
        var resizeFailures = new List<Exception>();
        nint materialHandle = 0;
        var unexpectedPorts = new List<string>();
        Native.Cache("UiShellStyle", 1);
        Native.Cache("UiGlassWallpaperPath", "");
        string wallpaper = Path.Combine(scope.Root, "preview-wallpaper.png");
        WriteWallpaper(wallpaper);
        int previewPage = 0;
        bool previewDependencies = true, previewAutoFill = true;
        try {
            var left = new MyPageLeft();
            var contextRows = new StackPanel(); left.Children.Add(contextRows);
            contextRows.Children.Add(new MyListItem { Title = "界面预览", Type = MyListItem.CheckType.RadioBox, Logo = "M2,2 H18 V18 H2 Z" });
            var right = new MyPageRight {
                Child = new TextBlock { Text = "原生页面容器预览\n\n此测试保留真正的 FormMain 页面、任务与提示容器。\n预览数据不连接账号、游戏或下载服务。", Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap }
            };
            Action<MainHost> configure = host => {
                windowPorts = host;
                void Unexpected(string port) { unexpectedPorts.Add(port); throw new InvalidOperationException("Glass fixture attempted a forbidden startup/platform port: " + port); }
                long tick = 0;
                host.Clock = () => Environment.TickCount64;
                host.StartTick = () => tick; host.WriteStartTick = value => tick = value;
                host.CreateLaunchLeft = () => { }; host.CreateLaunchRight = () => { };
                host.LeftPage = _ => new LegacyMainLeftPage { Element = left };
                host.RightPage = _ => right;
                host.ReadInteger = key => key == "SystemLastVersionReg" ? ModBase.VersionCode : Settings.Get<int>(key);
                host.HasSaved = _ => true; host.ThemeRefresh = _ => { };
                host.IsAdmin = () => false; host.IsDebug = () => false;
                host.PackEnabled = () => false; host.ApplyPackVisibility = _ => { };
                host.StartFolderLoader = () => { };
                host.Hint = (_, _) => { }; host.HasBlockingFileOperation = () => true;
                host.ForceExit = () => Unexpected("ForceExit");
                host.WriteSetting = (key, _) => Unexpected("WriteSetting:" + key);
                host.NewThread = (_, name, _) => Unexpected("NewThread:" + name);
                host.StartAnimationLoop = () => Unexpected("StartAnimationLoop");
                host.StartMainTimer = () => Unexpected("StartMainTimer");
                host.JavaInitialize = () => Unexpected("JavaInitialize");
                host.StartClientList = () => Unexpected("StartClientList");
                host.CheckUpdates = () => Unexpected("CheckUpdates");
                host.ClearTaskTemp = () => Unexpected("ClearTaskTemp");
                host.Ui = work => work(); host.UiWait = work => work(); host.UiPosted = (work, _) => work();
                host.CreateResizer = target => new WindowResizer(target,
                    value => value * ((PresentationSource.FromVisual(target) as HwndSource)?.CompositionTarget?.TransformFromDevice.M11 ?? 1),
                    (work, name) => {
                        int gesture = resizeWorkers.Count + 1;
                        if (interactive) Console.WriteLine("PREVIEW RESIZE worker-start #" + gesture + "; WPF-left=" + Mouse.LeftButton + "; native-left-down=" + ((GetAsyncKeyState(1) & 0x8000) != 0));
                        var thread = new Thread(() => {
                            try { work(); }
                            catch (Exception error) { lock (resizeFailures) resizeFailures.Add(error); }
                            finally { if (interactive) Console.WriteLine("PREVIEW RESIZE worker-end #" + gesture); }
                        }) { IsBackground = true, Name = "Native facade " + name };
                        resizeWorkers.Add(thread); thread.Start();
                    });
                host.LaunchButton = () => { }; host.CancelLaunch = () => { };
                host.IsLaunching = () => false; host.IsInstallSelection = () => false;
                host.CanRefresh = _ => false; host.RefreshHidden = () => { };
                bool hidden = false;
                host.HiddenForceShow = () => hidden; host.WriteHiddenForceShow = value => hidden = value;
                host.Animations.Code = (work, _, _) => { work(); return default; };
                host.Animations.Start = (_, _, _) => { }; host.Animations.Stop = _ => { };
                host.Animations.Opacity = (_, _, _, _, _, _) => default;
                host.Animations.X = (_, _, _, _, _, _) => default;
                host.Animations.Width = (_, _, _, _, _, _) => default;
                host.Animations.Double = (_, _, _, _, _, _) => default;
            };
            ConstructorInfo constructor = typeof(FormMain).GetConstructor(Native.Flags, null, [typeof(Action<MainHost>)], null)
                ?? throw new MissingMethodException("Actual FormMain guarded constructor is unavailable.");
            window = (FormMain)constructor.Invoke([configure]);
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Loaded"));
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Closing"));
            var state = new Func<GlassLauncherViewState>(() => new GlassLauncherViewState {
                IsHome = previewPage == 0, SelectedNavigationId = previewPage,
                PageTitle = previewPage == 0 ? "" : "界面预览 · 原生页面容器",
                AccountName = "界面预览", HasInstance = true, InstanceName = "玻璃界面预览",
                InstanceDescription = "预览数据 · 未连接游戏", CanLaunch = false, LaunchText = "预览模式",
                LaunchStatus = "此预览不会启动游戏", DependencyCheckEnabled = previewDependencies, AutoFillEnabled = previewAutoFill,
                RecentWorldAvailable = false, UseDarkText = true, ReduceMotion = !interactive
            });
            Native.Property(typeof(FormMain), "GlassPreviewState").SetValue(window, state);
            var integration = Native.Get(window, "glassShell") ?? throw new InvalidOperationException("Actual FormMain did not attach GlassShellIntegration.");
            var view = (GlassLauncherView)Native.Property(typeof(FormMain), "GlassView").GetValue(window)!;
            var backdrop = (GlassWindowBackdrop)Native.Get(integration, "_backdrop")!;
            IsolatePreviewActions(integration, view);
            view.NavigationRequested += page => { previewPage = page; window.PageCurrent = (FormMain.PageType)page; Refresh(window); };
            view.DependencyCheckChanged += value => { previewDependencies = value; Refresh(window); };
            view.AutoFillChanged += value => { previewAutoFill = value; Refresh(window); };
            view.WallpaperRequested += (_, _) => {
                Native.Cache("UiGlassWallpaperPath", string.IsNullOrEmpty(Settings.Get<string>("UiGlassWallpaperPath")) ? wallpaper : "");
                Refresh(window);
            };
            view.CloseRequested += (_, _) => window.Close();
            view.SetAccountContent(new TextBlock { Text = "界面预览不会读取或更改登录账号。", Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap });
            window.AllowDrop = false;
            window.Title = "PCL-LF 玻璃界面预览";
            window.Topmost = false; window.ShowActivated = interactive; window.ShowInTaskbar = interactive;
            window.Width = 1110; window.Height = 740;
            window.Left = interactive ? Math.Max(20, (SystemParameters.WorkArea.Width - window.Width) / 2) : -30000;
            window.Top = interactive ? Math.Max(20, (SystemParameters.WorkArea.Height - window.Height) / 2) : -30000;
            Native.Check(window.GetType().Assembly == Native.Main && Native.Get(window, "Controller") is not null,
                "Glass integration fixture replaced the genuine FormMain or its original controller.");
            window.Show();
            // Original Loaded is intentionally detached. Invoke only its real resizer factory
            // and registration APIs at the same post-HWND point, without startup services.
            previewResizer = windowPorts!.CreateResizer(window);
            RegisterResizeGrips(window, previewResizer);
            window.UpdateLayout(); VisualAssetChecks.Observe(550); Refresh(window); VisualAssetChecks.Drain();
            environment.AssertNoForbiddenIo();
            Native.Check(window.IsLoaded && window.AllowsTransparency && window.Opacity == 1,
                "Actual glass FormMain lost its alpha-enabled HWND or opaque foreground content.");
            Native.Check(ReferenceEquals(view.PageContentHost.Content, Part<Border>(window, "PanMainRight")) && right.IsLoaded && left.IsLoaded,
                "Actual legacy page containers were recreated or removed from the loaded FormMain tree: portal=" +
                ReferenceEquals(view.PageContentHost.Content, Part<Border>(window, "PanMainRight")) + "; rightLoaded=" + right.IsLoaded + "; leftLoaded=" + left.IsLoaded);
            var root = (Grid)window.Content;
            foreach (string part in new[] { "PanHint", "PanMsg" })
                Native.Check(ReferenceEquals(VisualTreeHelper.GetParent(Part<FrameworkElement>(window, part)), root), "Actual overlay was not portaled: " + part);
            Native.Check(IsDescendant(Part<FrameworkElement>(window, "BtnExtraDownload"), root), "Actual download/task button was dropped.");
            Native.Check(((UIElement)Native.Get(integration, "_legacyRoot")!).Visibility == Visibility.Hidden,
                "Original BAML root must remain loaded and hidden beneath the shell.");
            CheckSurfaces(view);
            CheckResizeGrips(window, view);
            previewPage = 1; window.PageCurrent = FormMain.PageType.Download; Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
            CheckSurfaces(view, 5);
            Native.Check(ReferenceEquals(Part<ContentControl>(view, "NavigationContent").Content, Part<Border>(window, "PanMainLeft")),
                "Actual contextual page container was not portaled into the glass navigation.");
            previewPage = 0; window.PageCurrent = FormMain.PageType.Launch; Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
            materialHandle = backdrop.NativeBackdropHandle;
            if (backdrop.HasDesktopBackdrop) {
                Native.Check(backdrop.SupportsSeparatedRegions && backdrop.Backend == GlassBackdropBackend.HostBackdropComposition && materialHandle != 0 && IsWindow(materialHandle),
                    "Actual glass shell reports desktop material without its clipped native companion.");
                Native.Check(GetWindowTextLength(materialHandle) == 0,
                    "Purely decorative native backdrop must not expose an accessible window title.");
                Native.Check((GetExtendedStyle(materialHandle) & 0x082000A0L) == 0x082000A0L,
                    "Decorative backdrop lost mandatory NOACTIVATE/NOREDIRECTIONBITMAP/TOOLWINDOW/TRANSPARENT styles.");
            } else {
                Native.Check(backdrop.Status is GlassBackdropStatus.UnsupportedPlatform or GlassBackdropStatus.HighContrast or GlassBackdropStatus.TransparencyDisabled or GlassBackdropStatus.Failed,
                    "Unexpected actual FormMain material fallback: " + backdrop.Status + "; " + backdrop.DiagnosticMessage);
            }
            Console.WriteLine("Actual glass FormMain material: " + backdrop.Status + " / " + backdrop.Backend + "; " + backdrop.DiagnosticMessage);
            Native.Cache("UiGlassWallpaperPath", wallpaper); Refresh(window); VisualAssetChecks.Drain();
            ImageSource source = GlassMaterial.GetWallpaperSource(view) ?? GlassMaterial.GetWallpaperSource(view.FindName("ShellRoot") as DependencyObject ?? view)
                ?? throw new InvalidOperationException("Actual FormMain wallpaper was not inherited by the glass surfaces.");
            var surfaces = Descendants<GlassSurface>(view).ToArray();
            Native.Check(surfaces.Length >= 7 && surfaces.All(surface => ReferenceEquals(surface.WallpaperSource, source) && surface.Effect is null),
                "Actual wallpaper material lost shared image coordinates or blurred its content.");
            Native.Check(backdrop.Status is GlassBackdropStatus.Wallpaper or GlassBackdropStatus.HighContrast or GlassBackdropStatus.TransparencyDisabled,
                "Actual FormMain left native desktop material enabled behind the launcher wallpaper.");
            Native.Check(materialHandle == 0 || !IsWindowVisible(materialHandle), "Wallpaper mode left the real native companion visible.");
            Native.Cache("UiGlassWallpaperPath", ""); Refresh(window); VisualAssetChecks.Drain();
            window.WindowState = WindowState.Minimized; VisualAssetChecks.Observe(100);
            Native.Check(materialHandle == 0 || !IsWindowVisible(materialHandle), "Actual FormMain minimize left its companion visible: state=" + window.WindowState +
                "; iconic=" + IsIconic(new WindowInteropHelper(window).Handle) + "; backend=" + backdrop.Backend + "; status=" + backdrop.Status);
            nint mainHandle = new WindowInteropHelper(window).Handle;
            // Foreground taskbar animation can defer the actual minimize past the DP change.
            // Restoring before it commits can be ignored, then the delayed minimize wins.
            VisualAssetChecks.Pump(() => IsIconic(mainHandle) && window.WindowState == WindowState.Minimized,
                "actual FormMain native minimize completion", 5000);
            window.WindowState = WindowState.Normal; window.UpdateLayout();
            long? restoredSince = null;
            VisualAssetChecks.Pump(() => {
                if (window.WindowState != WindowState.Normal || IsIconic(mainHandle) || !IsWindowVisible(mainHandle) || !IsWindowEnabled(mainHandle)) {
                    restoredSince = null;
                    return false;
                }
                restoredSince ??= Environment.TickCount64;
                return Environment.TickCount64 - restoredSince.Value >= 150;
            }, "stable actual FormMain native restore completion", 5000);
            if (backdrop.HasDesktopBackdrop) Native.Check(IsWindowVisible(backdrop.NativeBackdropHandle), "Actual FormMain restore lost the native material.");
            CheckSurfaces(view);
            if (interactive) {
                bool closed = false; window.Closed += (_, _) => closed = true;
                window.SizeChanged += (_, args) => Console.WriteLine(FormattableString.Invariant(
                    $"PREVIEW SIZE actual-dip={args.NewSize.Width:0.###}x{args.NewSize.Height:0.###}; requested-dip={window.Width:0.###}x{window.Height:0.###}"));
                window.PreviewKeyDown += (_, args) => {
                    if (args.Key != Key.F6 || args.IsRepeat) return;
                    bool minimum = Math.Abs(window.Width - 810) < 0.5 && Math.Abs(window.Height - 470) < 0.5;
                    window.Width = minimum ? 1110 : 810;
                    window.Height = minimum ? 740 : 470;
                    window.UpdateLayout();
                    Console.WriteLine(FormattableString.Invariant($"PREVIEW F6 requested-dip={window.Width:0}x{window.Height:0}"));
                    args.Handled = true;
                };
                nint previewHandle = new WindowInteropHelper(window).Handle;
                Native.Check(IsWindowVisible(previewHandle) && !IsIconic(previewHandle) && IsWindowEnabled(previewHandle),
                    "Interactive actual FormMain is logically loaded but its native HWND is hidden, minimized or disabled.");
                window.Activate();
                Console.WriteLine("INTERACTIVE actual FormMain preview ready; fixture-owned presentation data; native desktop material, wallpaper button toggles owned image; F6 toggles 810x470 / 1110x740; close the window to finish (10-minute limit).");
                VisualAssetChecks.Pump(() => closed, "interactive actual FormMain preview", 600000);
            }
            Native.Check(unexpectedPorts.Count == 0, "Actual FormMain entered guarded original startup/termination: " + string.Join(",", unexpectedPorts));
            environment.AssertNoForbiddenIo();
        } finally {
            if (previewResizer is not null) {
                foreach (string flag in new[] { "resizeRight", "resizeLeft", "resizeUp", "resizeDown" })
                    Native.Field(typeof(WindowResizer), flag).SetValue(previewResizer, false);
                VisualAssetChecks.Pump(() => resizeWorkers.All(worker => !worker.IsAlive), "owned native resize workers", 3000);
                lock (resizeFailures) Native.Check(resizeFailures.Count == 0, "Owned native resize worker failed: " + string.Join("; ", resizeFailures));
            }
            if (window is not null && Application.Current.Windows.Cast<Window>().Contains(window)) window.Close();
            VisualAssetChecks.Drain();
            Native.Check(materialHandle == 0 || !IsWindow(materialHandle), "Closing actual FormMain leaked its native backdrop companion.");
            Native.State(typeof(ModMain), "FrmMain").SetValue(null, previousMain);
            environment.AssertNoForbiddenIo();
        }
        Console.WriteLine("PASS actual glass FormMain: original constructor/BAML/controller, live page/overlay/task portals, original resize handler registration and inner-edge hit testing, rounded floating regions, untitled decorative native material or explicit OS fallback, shared wallpaper frosting, minimize/restore and native handle cleanup; startup/account/game/profile access guarded.");
    }

    private static void Refresh(FormMain window) => Native.Call(typeof(FormMain), "RefreshGlassShell", window);
    private static void RegisterResizeGrips(FormMain window, WindowResizer resizer) {
        resizer.addResizerDown(Part<FrameworkElement>(window, "ResizerB"));
        resizer.addResizerLeft(Part<FrameworkElement>(window, "ResizerL"));
        resizer.addResizerLeftDown(Part<FrameworkElement>(window, "ResizerLB"));
        resizer.addResizerLeftUp(Part<FrameworkElement>(window, "ResizerLT"));
        resizer.addResizerRight(Part<FrameworkElement>(window, "ResizerR"));
        resizer.addResizerRightDown(Part<FrameworkElement>(window, "ResizerRB"));
        resizer.addResizerRightUp(Part<FrameworkElement>(window, "ResizerRT"));
        resizer.addResizerUp(Part<FrameworkElement>(window, "ResizerT"));
    }
    private static void CheckResizeGrips(FormMain window, GlassLauncherView view) {
        PropertyInfo storeProperty = typeof(UIElement).GetProperty("EventHandlersStore", Native.Flags)
            ?? throw new MissingMemberException("UIElement.EventHandlersStore");
        foreach (string name in new[] { "ResizerB", "ResizerL", "ResizerLB", "ResizerLT", "ResizerR", "ResizerRB", "ResizerRT", "ResizerT" }) {
            FrameworkElement grip = Part<FrameworkElement>(window, name);
            object store = storeProperty.GetValue(grip) ?? throw new InvalidOperationException("Resize grip has no actual routed handlers: " + name);
            var handlers = (Array?)Native.Call(store.GetType(), "GetRoutedEventHandlers", store, UIElement.MouseLeftButtonDownEvent);
            bool actual = handlers?.Cast<object>().Any(info => Native.Property(info.GetType(), "Handler").GetValue(info) is Delegate handler &&
                handler.Method.DeclaringType == typeof(WindowResizer) && handler.Method.Name == "MouseDown") == true;
            Native.Check(actual, "Actual native resize handler was not registered on " + name);
        }
        Rect area = view.GetSurfaceRegions()[0].Bounds;
        Point right = view.TranslatePoint(new Point(area.Right - 2, area.Top + area.Height / 2), window);
        Point left = view.TranslatePoint(new Point(area.Left + 2, area.Top + area.Height / 2), window);
        Point top = view.TranslatePoint(new Point(area.Left + area.Width / 2, area.Top + 2), window);
        Point bottom = view.TranslatePoint(new Point(area.Left + area.Width / 2, area.Bottom - 2), window);
        foreach (var (point, name) in new[] { (right, "ResizerR"), (left, "ResizerL"), (top, "ResizerT"), (bottom, "ResizerB") })
            Native.Check(ReferenceEquals(window.InputHitTest(point), Part<FrameworkElement>(window, name)),
                "Actual glass inner edge is not hit-testable by its original resize grip: " + name);
    }
    private static void CheckSurfaces(GlassLauncherView view, int expectedRegions = 4) {
        Native.Check(Part<GlassSurface>(view, "MainSurface").CornerRadius.TopLeft == 36 && Part<GlassSurface>(view, "PrimaryCard").CornerRadius.TopLeft == 16,
            "Actual FormMain main/card rounding changed.");
        foreach (string name in new[] { "SideRail", "AccountSurface", "NavigationSurface", "WindowControlsSurface" })
            Native.Check(Part<GlassSurface>(view, name).CornerRadius.TopLeft == 28, "Floating pill rounding changed: " + name);
        var regions = view.GetSurfaceRegions();
        Native.Check(regions.Count == expectedRegions && regions.All(region => region.Bounds.Width > 0 && region.Bounds.Height > 0),
            "Actual FormMain floating region count differs: expected " + expectedRegions + ", actual " + regions.Count);
    }

    private static void IsolatePreviewActions(object integration, GlassLauncherView view) {
        object runtime = Native.Get(integration, "_runtime")!;
        foreach (string eventName in new[] { "StartRequested", "CancelRequested", "SelectInstanceRequested", "InstanceSettingsRequested", "AccountRequested", "RecentWorldRequested", "BackRequested", "NavigationRequested", "DependencyCheckChanged", "AutoFillChanged" }) {
            EventInfo actualEvent = typeof(GlassLauncherView).GetEvent(eventName)!;
            MethodInfo actualHandler = Native.Method(runtime.GetType(), "On" + eventName);
            actualEvent.RemoveEventHandler(view, Delegate.CreateDelegate(actualEvent.EventHandlerType!, runtime, actualHandler));
        }
        EventInfo wallpaper = typeof(GlassLauncherView).GetEvent("WallpaperRequested")!;
        wallpaper.RemoveEventHandler(view, Delegate.CreateDelegate(wallpaper.EventHandlerType!, integration, Native.Method(integration.GetType(), "OnWallpaper")));
    }

    private static T Part<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Actual glass window part missing: " + name);
    private static bool IsDescendant(DependencyObject element, DependencyObject ancestor) {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void WriteWallpaper(string path) {
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen()) {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(67, 124, 149), Color.FromRgb(234, 174, 119), 35), null, new Rect(0, 0, 1200, 800));
            for (int index = 0; index < 9; index++)
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Rect(index * 170 - 160, 80 + index % 3 * 150, 120, 520), 28, 28);
        }
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        VisualAssetChecks.Save(bitmap, path);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint hwnd);
    private static long GetExtendedStyle(nint hwnd) => nint.Size == 8 ? GetWindowLongPtr(hwnd, -20).ToInt64() : GetWindowLong(hwnd, -20);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
