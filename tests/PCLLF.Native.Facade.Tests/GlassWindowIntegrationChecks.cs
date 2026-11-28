// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections;
using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
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
        var small = ((Size MinSize, double RailAllowance))Native.Static("PCL.GlassShellIntegration", "FitWorkArea",
            new Size(980, 560), new Size(911, 486))!;
        var wide = ((Size MinSize, double RailAllowance))Native.Static("PCL.GlassShellIntegration", "FitWorkArea",
            new Size(980, 560), new Size(1920, 1040))!;
        Native.Check(small.RailAllowance == 0 && small.MinSize.Width <= 911 && small.MinSize.Height <= 486 &&
            wide.RailAllowance > 0 && wide.MinSize.Width == 980 + wide.RailAllowance,
            "Compact monitor bounds cannot contain the window or the normal monitor lost its rail allowance.");
        bool interactive = Environment.GetEnvironmentVariable("PCLLF_GLASS_INTERACTIVE") == "1";
        object? previousMain = Native.State(typeof(ModMain), "FrmMain").GetValue(null);
        FormMain? window = null;
        MainHost? windowPorts = null;
        WindowResizer? previewResizer = null;
        var resizeWorkers = new List<Thread>();
        var resizeFailures = new List<Exception>();
        int initialBackdropWindows = CountDesktopBackdropWindows();
        var unexpectedPorts = new List<string>();
        Native.Cache("UiShellStyle", 1);
        Native.Cache("UiGlassWallpaperPath", "");
        string wallpaper = Path.Combine(scope.Root, "preview-wallpaper.png");
        WriteWallpaper(wallpaper);
        string legacyPictures = Path.Combine(AppContext.BaseDirectory, "PCL", "Pictures");
        Directory.CreateDirectory(legacyPictures);
        File.Copy(wallpaper, Path.Combine(legacyPictures, "legacy-wallpaper.png"));
        int previewPage = 0;
        bool previewDependencies = true, previewAutoFill = true;
        bool previewMotion = interactive;
        bool previewDarkText = true;
        try {
            Action<MainHost> configure = host => {
                windowPorts = host;
                void Unexpected(string port) { unexpectedPorts.Add(port); throw new InvalidOperationException("Glass fixture attempted a forbidden startup/platform port: " + port); }
                long tick = 0;
                host.Clock = () => Environment.TickCount64;
                host.StartTick = () => tick; host.WriteStartTick = value => tick = value;
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
                host.CheckUpdates = _ => Unexpected("CheckUpdates");
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
            Native.Check(window.Title == ModBase.LauncherDisplayName, "The product window does not expose its launcher display name.");
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Loaded"));
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Closing"));
            var state = new Func<GlassLauncherViewState>(() => new GlassLauncherViewState {
                IsHome = previewPage == 0, SelectedNavigationId = previewPage,
                PageTitle = previewPage == 0 ? "" : "界面预览 · 原生页面容器",
                AccountName = "界面预览", HasInstance = true, InstanceName = "Minecraft 1.21.4",
                InstanceDescription = "预览数据 · 未连接游戏", CanLaunch = false, LaunchText = "预览模式",
                LaunchStatus = "此预览不会启动游戏", DependencyCheckEnabled = previewDependencies, AutoFillEnabled = previewAutoFill,
                RecentWorldAvailable = false, UseDarkText = previewDarkText, ReduceMotion = !previewMotion
            });
            Native.Property(typeof(FormMain), "GlassPreviewState").SetValue(window, state);
            var integration = Native.Get(window, "glassShell") ?? throw new InvalidOperationException("Actual FormMain did not attach GlassShellIntegration.");
            var view = (GlassLauncherView)Native.Property(typeof(FormMain), "GlassView").GetValue(window)!;
            IsolatePreviewActions(integration, view);
            view.NavigationRequested += page => {
                previewPage = page;
                if (page == (int)FormMain.PageType.Link) window.PageChange(FormMain.PageType.Link);
                else window.PageCurrent = (FormMain.PageType)page;
                Refresh(window);
            };
            view.DependencyCheckChanged += value => { previewDependencies = value; Refresh(window); };
            view.AutoFillChanged += value => { previewAutoFill = value; Refresh(window); };
            view.CloseRequested += (_, _) => window.Close();
            view.SetAccountContent(new TextBlock { Text = "界面预览不会读取或更改登录账号。", Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap });
            window.AllowDrop = false;
            window.Title = "PCL-LF 工作台界面预览";
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
            var home = (ContentControl)view.PageContentHost.Content;
            Native.Check(home.GetType().Name == "GlassHomePage" && ReferenceEquals(home, window.PageRight),
                "The actual window should host the new homepage.");
            if (!interactive) {
                CheckStartupWithRestoredInstance(window, view, animate => { previewMotion = animate; Refresh(window); });
                CheckDirectTransitions(window, view, home);
            }
            if (!interactive) {
                var launchPage = view.PageContentHost.Content;
                Part<Button>(view, "NavLink").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualAssetChecks.Pump(() => !ReferenceEquals(view.PageContentHost.Content, launchPage), "the Terracotta lobby is mounted");
                var lobby = (FrameworkElement)view.PageContentHost.Content;
                window.UpdateLayout();
                VisualAssetChecks.Drain();
                Native.Check(window.PageCurrent == FormMain.PageType.Link &&
                    AutomationProperties.GetName(Part<Button>(view, "NavLink")) == "联机", "The 联机 rail entry did not select the lobby.");
                Native.Check(Descendants<TextBlock>(lobby).Any(label => label.Text.Contains("© Burning_TNT", StringComparison.Ordinal)),
                    "The projected lobby lost the upstream attribution.");
                Native.Check(Descendants<GlassSurface>(lobby).Any(card => card.BlurRadius > 0), "The lobby did not use themed glass cards.");
                CheckTerracottaStages(window, view, lobby,
                    animate => { previewMotion = animate; Refresh(window); },
                    darkText => { previewDarkText = darkText; Refresh(window); });
                previewPage = 0;
                window.PageChange(FormMain.PageType.Launch);
                Refresh(window);
                VisualAssetChecks.Pump(() => ReferenceEquals(view.PageContentHost.Content, home), "the lobby returns to the same native Home");
                CheckDirectSettings(window, view, page => { previewPage = page; Refresh(window); });
                CheckDirectResources(window, view, page => { previewPage = page; Refresh(window); });
                CheckDirectInstallation(window, view, scope.Root, page => { previewPage = page; Refresh(window); });
                CheckDirectInstanceSelection(window, view, scope.Root, page => { previewPage = page; Refresh(window); });
                CheckMoreAndJava(window, view, page => { previewPage = page; Refresh(window); });
            }
            var root = (Grid)window.Content;
            Native.Check(new[] { "PanHint", "PanMsg", "PanMainLeft", "PanMainRight" }.All(name => window.FindName(name) is null),
                "The actual window still creates retired shell controls.");
            Native.Check(Descendants<Button>(root).Any(button => button.Name == "TasksButton"),
                "The visible task action is absent from the workbench.");
            CheckSurfaces(view);
            double allowance = view.LeftExpansionAllowance;
            view.SetLeftExpansionAllowance(GlassLauncherView.RailExpandDelta + GlassLauncherView.RailOvershootHeadroom);
            window.UpdateLayout(); VisualAssetChecks.Drain(); Refresh(window);
            Native.Check(view.GetSurfaceRegions()[0].Bounds.Left > 100, "The collapsed rail fixture did not reserve space for expansion.");
            CheckSurfaces(view);
            view.SetLeftExpansionAllowance(allowance);
            window.UpdateLayout(); VisualAssetChecks.Drain(); Refresh(window);
            CheckResizeGrips(window, view);
            window.Width = 980; window.Height = 560;
            window.UpdateLayout(); VisualAssetChecks.Drain(); Refresh(window);
            CheckSurfaces(view); CheckResizeGrips(window, view);
            window.Width = 1110; window.Height = 740;
            window.UpdateLayout(); VisualAssetChecks.Drain(); Refresh(window);
            CheckSurfaces(view);
            previewPage = 1; window.PageChange(FormMain.PageType.Download);
            Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
            CheckSurfaces(view);
            Native.Check(Part<StackPanel>(view, "SidebarPanel").Children.Count > 0 && view.FindName("NavigationPopover") is null &&
                window.FindName("PanMainLeft") is null,
                "Context navigation must use new sidebar elements with the original state source hidden.");
            previewPage = 0; window.PageChange(FormMain.PageType.Launch);
            Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows,
                "The workspace created a native desktop-blur companion without a wallpaper.");
            Console.WriteLine("Actual workspace FormMain: launcher-owned background; no native desktop backdrop.");
            Native.Cache("UiGlassWallpaperPath", wallpaper); Refresh(window); VisualAssetChecks.Drain();
            ImageSource source = GlassMaterial.GetWallpaperSource(view) ?? GlassMaterial.GetWallpaperSource(view.FindName("ShellRoot") as DependencyObject ?? view)
                ?? throw new InvalidOperationException("Actual FormMain wallpaper was not inherited by the glass surfaces.");
            Native.Check(ReferenceEquals(Part<Image>(view, "WallpaperImage").Source, source) &&
                Part<Image>(view, "WallpaperImage").Visibility == Visibility.Visible,
                "The workspace did not display its configured launcher wallpaper.");
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows,
                "Wallpaper mode created a native desktop-blur companion.");
            CheckWallpaperToolTip(window, view, source);
            if (!interactive && Environment.GetEnvironmentVariable("PCLLF_VIDEO_WALLPAPER_SAMPLE") is { Length: > 0 } video)
                CheckSelectedVideoWallpaper(window, view, video);
            if (!interactive) CheckCrashDialog(window, integration);
            var boundedBrush = (ImageBrush)Native.Call(Native.Type("PCL.SetupPageRuntime"), "LoadBoundedBackgroundBrush", null, wallpaper)!;
            Native.Check(!boundedBrush.IsFrozen && boundedBrush.ImageSource is { IsFrozen: true },
                "Pictures background brush must remain mutable for suit/alignment updates while keeping a frozen BitmapSource.");
            LauncherAppearanceSettings.BackgroundRefresh(false, true);
            Native.Check(ReferenceEquals(Part<Image>(view, "WallpaperImage").Source, source),
                "Refreshing the configured wallpaper replaced it with the image in the legacy Pictures folder.");
            Native.Cache("UiGlassWallpaperPath", "");
            window.UpdateBackgroundAndTitleBar();
            Native.Check(Part<Image>(view, "WallpaperImage").Source is null,
                "Clearing the selection exposed the wallpaper from the legacy folder.");
            Refresh(window); VisualAssetChecks.Drain();
            var refreshTimer = (System.Windows.Threading.DispatcherTimer)Native.Get(Native.Get(window, "glassShell")!, "_refreshTimer")!;
            window.WindowState = WindowState.Minimized; VisualAssetChecks.Observe(100);
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows, "Minimize created a desktop backdrop.");
            nint mainHandle = new WindowInteropHelper(window).Handle;
            // Foreground taskbar animation can defer the actual minimize past the DP change.
            // Restoring before it commits can be ignored, then the delayed minimize wins.
            VisualAssetChecks.Pump(() => IsIconic(mainHandle) && window.WindowState == WindowState.Minimized,
                "actual FormMain native minimize completion", 5000);
            Native.Check(!refreshTimer.IsEnabled, "The minimized workspace kept polling its UI state.");
            window.WindowState = WindowState.Normal; window.UpdateLayout();
            long? restoredSince = null;
            try {
                VisualAssetChecks.Pump(() => {
                    if (window.WindowState != WindowState.Normal || IsIconic(mainHandle) || !IsWindowVisible(mainHandle) || !IsWindowEnabled(mainHandle)) {
                        restoredSince = null;
                        return false;
                    }
                    restoredSince ??= Environment.TickCount64;
                    return Environment.TickCount64 - restoredSince.Value >= 150;
                }, "stable actual FormMain native restore completion", 5000);
            } catch (InvalidOperationException error) {
                throw new InvalidOperationException($"Restore ended with WPF state={window.WindowState}, iconic={IsIconic(mainHandle)}, " +
                    $"visible={IsWindowVisible(mainHandle)}, enabled={IsWindowEnabled(mainHandle)}, timer={refreshTimer.IsEnabled}.", error);
            }
            Native.Check(refreshTimer.IsEnabled, "The restored workspace did not resume refreshing its UI state.");
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows, "Restore created a desktop backdrop.");
            CheckSurfaces(view);
            if (!interactive) CheckNativeContentDialog(window, integration);
            if (interactive) {
                bool closed = false; window.Closed += (_, _) => closed = true;
                window.SizeChanged += (_, args) => Console.WriteLine(FormattableString.Invariant(
                    $"PREVIEW SIZE actual-dip={args.NewSize.Width:0.###}x{args.NewSize.Height:0.###}; requested-dip={window.Width:0.###}x{window.Height:0.###}"));
                window.PreviewKeyDown += (_, args) => {
                    if (args.Key != Key.F6 || args.IsRepeat) return;
                    bool minimum = Math.Abs(window.Width - 980) < 0.5 && Math.Abs(window.Height - 560) < 0.5;
                    window.Width = minimum ? 1110 : 980;
                    window.Height = minimum ? 740 : 560;
                    window.UpdateLayout();
                    Console.WriteLine(FormattableString.Invariant($"PREVIEW F6 requested-dip={window.Width:0}x{window.Height:0}"));
                    args.Handled = true;
                };
                nint previewHandle = new WindowInteropHelper(window).Handle;
                Native.Check(IsWindowVisible(previewHandle) && !IsIconic(previewHandle) && IsWindowEnabled(previewHandle),
                    "Interactive actual FormMain is logically loaded but its native HWND is hidden, minimized or disabled.");
                window.Activate();
                Console.WriteLine("INTERACTIVE workspace FormMain preview ready; fixture-owned data; launcher wallpaper only; F6 toggles 810x470 / 1110x740; close the window to finish (10-minute limit).");
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
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows, "Workspace lifecycle leaked a desktop backdrop.");
            Native.State(typeof(ModMain), "FrmMain").SetValue(null, previousMain);
            environment.AssertNoForbiddenIo();
        }
        Console.WriteLine("PASS workspace FormMain: single native workspace; original commands and resize handlers, launcher wallpaper/opaque background, zero desktop composition windows, minimize/restore and cleanup; startup/account/game/profile access guarded.");
    }

    private static void CheckTerracottaStages(FormMain window, GlassLauncherView view, FrameworkElement lobby,
        Action<bool> setMotion, Action<bool> setDarkText)
    {
        var sidebar = Part<StackPanel>(view, "SidebarPanel");
        Button SidebarAction(string text) => Descendants<Button>(sidebar).Single(button => AutomationProperties.GetName(button) == text);
        bool ShowsAction(string text) => Descendants<Button>(lobby).Any(button => button.IsVisible &&
            (Equals(button.Content, text) || Descendants<TextBlock>(button).Any(label => label.Text == text)));
        bool ShowsCodeInput() => Descendants<TextBox>(lobby).Any(box => box.IsVisible && box.IsEnabled);

        Native.Check(ShowsAction("下载并启用") || ShowsAction("启用陶瓦联机"),
            "The lobby did not offer an explicit optional component action.");
        Native.Check(!SidebarAction("创建房间").IsEnabled && !SidebarAction("加入房间").IsEnabled && !ShowsCodeInput(),
            "The component stage exposed a lobby flow before Terracotta was enabled.");
        foreach (string action in new[] { "创建房间", "加入房间" })
            Native.Check(Descendants<System.Windows.Shapes.Path>(SidebarAction(action))
                .Any(icon => icon.Data is not null && icon.Width == 16), "A 联机 sidebar action lost its icon.");

        var source = Native.Property(lobby.GetType(), "Controller").GetValue(lobby)!;
        Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content), "Opening multiplayer created a transitional page source.");
        ((System.Windows.Threading.DispatcherTimer)Native.Get(source, "_poll")!).Stop();
        using var http = new HttpClient();
        using var connection = (IDisposable)Native.Type("PCL.TerracottaConnection")
            .GetConstructor(Native.Flags, null, [typeof(HttpClient)], null)!.Invoke([http]);
        var state = Native.Type("PCL.TerracottaRoom").GetConstructors(Native.Flags)
            .Single(ctor => ctor.GetParameters().Length == 6);
        void SetState(string name, long index, string? code = null, string? address = null)
        {
            Native.Call(source.GetType(), "SetRoom", source, state.Invoke([name, index, code, address, null, null]));
            Refresh(window);
            window.UpdateLayout();
            VisualAssetChecks.Drain();
        }
        Native.Set(source, "_connection", connection);
        try
        {
            SetState("waiting", 1);
            Native.Check(SidebarAction("创建房间").IsEnabled && SidebarAction("加入房间").IsEnabled &&
                ShowsAction("创建房间") && !ShowsCodeInput(), "The ready lobby did not show only the create flow.");
            if (!SystemParameters.HighContrast)
            {
                static double Luminance(Color color) => .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;
                foreach (bool darkText in new[] { false, true })
                {
                    setDarkText(darkText);
                    window.UpdateLayout();
                    VisualAssetChecks.Drain();
                    var create = Descendants<Button>(lobby).Single(button => button.IsVisible && Equals(button.Content, "创建房间"));
                    var createSurface = (Border)create.Template.FindName("Surface", create);
                    VisualAssetChecks.Pump(() => createSurface.Background is SolidColorBrush brush &&
                        brush.Color == ((SolidColorBrush)view.FindResource("GlassAccentBrush")).Color && (brush.Color.R < 128) == darkText,
                        "the create-room button uses the new theme's accent color");
                    var caption = Descendants<TextBlock>(create).Single(label => label.IsVisible && label.Text == "创建房间");
                    Color fill = ((SolidColorBrush)createSurface.Background).Color;
                    Color ink = ((SolidColorBrush)caption.Foreground).Color;
                    double faceLuminance = Luminance(fill), textLuminance = Luminance(ink);
                    double contrast = (Math.Max(faceLuminance, textLuminance) + .05) / (Math.Min(faceLuminance, textLuminance) + .05);
                    Native.Check(create.IsEnabled && ink == ((SolidColorBrush)create.Foreground).Color && contrast >= 4.5,
                        $"Create room caption ({ink}) has {contrast:F2}:1 contrast against {fill}; the button specifies {create.Foreground}.");
                }
            }
            if (PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation)
            {
                setMotion(true);
                var stack = Descendants<ScrollViewer>(lobby).Select(scroll => scroll.Content).OfType<StackPanel>()
                    .First(content => content.RenderTransform is TransformGroup);
                SidebarAction("加入房间").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualAssetChecks.Pump(() => stack.Opacity is > .1 and < .9 && !ShowsCodeInput(), "the create page fades out");
                SidebarAction("创建房间").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Native.Check(stack.Opacity is > 0 and < .999, "Returning to the visible flow snapped its animation.");
                VisualAssetChecks.Pump(() => stack.Opacity > .999 && ShowsAction("创建房间"), "the interrupted flow returns");
                SidebarAction("加入房间").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VisualAssetChecks.Pump(() => ShowsCodeInput() && stack.Opacity is > .05 and < .99, "the join page animates in");
                VisualAssetChecks.Pump(() => stack.Opacity > .999, "the join page settles");
                setMotion(false);
            }
            SidebarAction("加入房间").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            VisualAssetChecks.Drain();
            Native.Check(ShowsCodeInput() && ShowsAction("粘贴") && !ShowsAction("创建房间"),
                "The join flow did not replace the create flow.");
            var join = Descendants<Button>(lobby).Single(button => button.IsVisible && Equals(button.Content, "加入房间"));
            join.ApplyTemplate();
            var surface = (Border)join.Template.FindName("Surface", join);
            var label = Descendants<TextBlock>(join).Single(caption => caption.IsVisible && caption.Text == "加入房间");
            Native.Check(!join.IsEnabled && join.Opacity > .999 &&
                ReferenceEquals(surface.Background, join.FindResource("GlassPresentationField")) &&
                ReferenceEquals(label.Foreground, join.FindResource("GlassPresentationSecondary")),
                "The empty join form dimmed its disabled caption or lost the neutral disabled palette.");

            const string code = "U/YNZE-U61D-2206-HXRG";
            SetState("host-ok", 2, code);
            Native.Check(!ShowsCodeInput() && ShowsAction("退出房间") && ShowsAction("复制") &&
                Descendants<TextBlock>(lobby).Any(label => label.IsVisible && label.Text == code),
                "Created rooms did not use the shared lobby with an adjacent copy action.");
            SetState("guest-ok", 3, address: "127.0.0.1:25565");
            Native.Check(ShowsAction("退出房间") && Descendants<TextBlock>(lobby).Any(label =>
                label.IsVisible && label.Text == "127.0.0.1:25565"), "Joined rooms did not reuse the shared lobby for the direct address.");
        }
        finally { Native.Set(source, "_connection", null); }
    }

    private static void CheckStartupWithRestoredInstance(FormMain window, GlassLauncherView view, Action<bool> animate)
    {
        if (!PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation) return;
        var selectionComplete = Native.Property(typeof(FormMain), "GlassInitialSelectionComplete");
        object? previousSelection = selectionComplete.GetValue(window);
        var list = ModMinecraft.McInstanceListLoader;
        var previousListState = list.State;
        Type preload = Native.Type("PCL.GlassStartupPreload");
        var clock = (System.Diagnostics.Stopwatch)Native.Field(preload, "Clock").GetValue(null)!;
        var releaseSlot = Native.Field(preload, "_release");
        object? previousRelease = releaseSlot.GetValue(null);
        try {
            selectionComplete.SetValue(window, false);
            list.State = ModBase.LoadState.Waiting;
            animate(true);
            view.BeginSplash();
            Refresh(window);
            Native.Check(view.IsSplashing && view.SplashProgress < 1, "The splash finished before the initial selection was ready.");

            clock.Restart(); // Optional preloads have only just begun.
            var fetched = new TaskCompletionSource<Newtonsoft.Json.Linq.JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseSlot.SetValue(null, fetched.Task);
            selectionComplete.SetValue(window, true);
            Refresh(window);
            Native.Check(view.SplashProgress == 1 && list.State == ModBase.LoadState.Waiting,
                "A restored instance still held the splash for an unused list scan or other preloads.");
            Native.Check(view.SplashStageText == "正在检查启动器更新", "The splash did not reflect the pending startup update check.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            VisualAssetChecks.Pump(() => !view.IsSplashing, "show the workspace without waiting for the splash timeout", 9000);
            Console.WriteLine($"Restored-instance splash completed in {elapsed.Elapsed.TotalSeconds:0.00}s in the WPF fixture.");
        }
        finally {
            clock.Reset();
            releaseSlot.SetValue(null, previousRelease);
            if (view.IsSplashing) Native.Call(typeof(GlassLauncherView), "FinishSplash", view);
            selectionComplete.SetValue(window, previousSelection);
            list.State = previousListState;
            animate(false);
        }
    }

    private static void CheckDirectTransitions(FormMain window, GlassLauncherView view, ContentControl home)
    {
        window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI);
        VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassPersonalizationPage",
            "mount the personalization workspace");
        var personalization = view.PageContentHost.Content;
        window.PageChange(FormMain.PageType.Launch);
        VisualAssetChecks.Pump(() => ReferenceEquals(view.PageContentHost.Content, home), "return to the same home");
        window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI);
        VisualAssetChecks.Pump(() => ReferenceEquals(view.PageContentHost.Content, personalization), "reopen the same personalization workspace");
        window.PageChange(FormMain.PageType.Launch);
        VisualAssetChecks.Pump(() => ReferenceEquals(view.PageContentHost.Content, home), "restore the home workspace");
    }

    private static void CheckNativeContentDialog(FormMain window, object integration) {
        var body = (FrameworkElement)Activator.CreateInstance(Native.Type("PCL.ReleaseNotesPromptView"), Native.Flags, null,
            ["# 测试说明", false, (Func<bool, bool>)(_ => true), (Action<string>)(_ => { })], null)!;
        int created = 0;
        var message = new ModMain.MyMsgBoxConverter {
            Title = "fixture native dialog", Button1 = "确定", Button2 = "取消", UseNativeContent = true,
            ContentFactory = () => { created++; return body; }
        };
        ModMain.WaitingMyMsgBox.Enqueue(message);
        ModMain.MyMsgBoxTick();
        try {
            object dialog = Native.Get(integration, "_directMessage")!;
            Native.Check(dialog is FrameworkElement && created == 1 &&
                ReferenceEquals(((ContentControl)Native.Get(dialog, "_nativeContentHost")!).Content, body),
                "The message queue did not mount the actual native content once.");
            Native.Check(window.FindName("PanMsg") is null,
                "A native custom dialog initialized the old overlay.");
            Native.Call(integration.GetType(), "HandleDirectMessageKey", integration, Key.Escape);
            VisualAssetChecks.Pump(() => Native.Get(integration, "_directMessage") is null, "native content dialog closes");
            Native.Check(Equals(message.Result, 2) && !message.WaitFrame.Continue && body.Parent is null,
                "Closing the native dialog did not release its request and content.");
        } finally {
            if (Native.Get(integration, "_directMessage") is { } direct)
                Native.Call(integration.GetType(), "RemoveDirectMessage", integration, direct);
        }
    }

    private static void CheckCrashDialog(FormMain window, object integration) {
        var projection = (FrameworkElement)Native.Get(integration, "_modalView")!;
        var effects = Native.Get(integration, "_modalEffects")!;
        PropertyInfo progress = Native.Property(effects.GetType(), "Progress");
        double Progress() => (double)progress.GetValue(effects)!;
        FieldInfo videoPaused = Native.Field(Native.Type("PCL.GlassVideoWallpaper"), "_paused");
        var message = new ModMain.MyMsgBoxConverter {
            Title = "Minecraft 出现错误",
            Text = string.Join("\n", Enumerable.Range(1, 48).Select(i => $"- 配置 / 环境资源 · 修改 · config/fixture-{i}.json")),
            Button1 = "确定", Button2 = "最近变更", Button3 = "导出错误报告"
        };
        double previousHeight = window.Height;
        int marginChanges = 0;
        var margin = DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(FrameworkElement));
        EventHandler observeMargin = (_, _) => {
            if (++marginChanges > 16) throw new InvalidOperationException("Crash report layout keeps changing the dialog inset.");
        };
        try {
            window.Height = 680;
            window.UpdateLayout();
            margin.AddValueChanged(projection, observeMargin);
            ModMain.WaitingMyMsgBox.Enqueue(message);
            ModMain.MyMsgBoxTick();
            Native.Check(Native.Get(integration, "_directMessage") is not null, "The message queue did not open the crash report in the glass shell.");
            Native.Check(window.FindName("PanMsg") is null, "Opening a crash report created a retired message container.");
            Refresh(window);
            VisualAssetChecks.Pump(() => Native.Get(integration, "_crashBanner") is not null && Progress() > .999, "long crash report opens");
            int settledInsets = marginChanges;
            for (int pass = 0; pass < 7; pass++) {
                Native.Call(integration.GetType(), "AlignModalToBody", integration);
                window.UpdateLayout();
            }
            Native.Check(marginChanges == settledInsets, "Rearranging a long crash report restarted measurement of its banner inset.");
            Console.WriteLine($"Crash report layout settled after {marginChanges} inset changes.");
            Native.Check(!(bool)videoPaused.GetValue(null)!, "The crash dialog paused wallpaper playback.");
            object banner = Native.Get(integration, "_crashBanner")!;
            VisualAssetChecks.Pump(() => !(bool)Native.Get(banner, "_running")!, "crash banner stops rendering after entrance");
            message.IsExited = true;
            Refresh(window);
            if (Progress() > .001) {
                Native.Check(!(bool)videoPaused.GetValue(null)!, "The modal blur exit paused wallpaper playback.");
                message.IsExited = false;
                Refresh(window);
                Native.Check(!(bool)videoPaused.GetValue(null)!, "Reopening a crash report paused wallpaper playback.");
                message.IsExited = true;
                Refresh(window);
            }
            VisualAssetChecks.Pump(() => !(bool)videoPaused.GetValue(null)! && Progress() <= .001,
                "crash backdrop closes during wallpaper playback");
        } finally {
            margin.RemoveValueChanged(projection, observeMargin);
            if (Native.Get(integration, "_directMessage") is { } direct)
                Native.Call(integration.GetType(), "RemoveDirectMessage", integration, direct);
            window.Height = previousHeight;
            Refresh(window);
            VisualAssetChecks.Drain();
        }
    }

    private static void CheckDirectSettings(FormMain window, GlassLauncherView view, Action<int> setPreviewPage) {
        bool automatic = Settings.Get<bool>("ToolLauncherUpdateAutoCheck");
        Native.Cache("ToolLauncherUpdateAutoCheck", false);
        try {
            setPreviewPage((int)FormMain.PageType.Setup);
            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassLauncherUpdatePage",
                "the native update page opens");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content), "Opening updates created a hidden page or sidebar.");

            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassPersonalizationPage",
                "the native personalization page opens");
            var page = (FrameworkElement)view.PageContentHost.Content;
            window.UpdateLayout();
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && Descendants<TrackDragSlider>(page).Any(),
                "Personalization did not use native glass editors.");

            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassLaunchOptionsPage",
                "the native launch options open");
            window.UpdateLayout();
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && Descendants<TrackDragSlider>((FrameworkElement)view.PageContentHost.Content).Any(),
                "Launch options constructed an old page or lost its memory editor.");

            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupSystem);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content is PageSetupSystem, "the native system settings open");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && ((FrameworkElement)view.PageContentHost.Content).FindName("TextBackupStoragePath") is TextBox,
                "System settings constructed an old page or lost its backup directory editor.");

            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupPersonal);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content is PageSetupPersonal, "the native synchronization settings open");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && ((FrameworkElement)view.PageContentHost.Content).FindName("TextSearch") is TextBox,
                "Synchronization settings constructed an old page or lost its native search field.");

            window.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
            Refresh(window);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassLauncherUpdatePage",
                "returning from personalization opens the native update page");
        } finally {
            Native.Cache("ToolLauncherUpdateAutoCheck", automatic);
            window.PageChange(FormMain.PageType.Launch);
            setPreviewPage((int)FormMain.PageType.Launch);
        }
    }

    private static void CheckDirectResources(FormMain window, GlassLauncherView view, Action<int> preview)
    {
        try
        {
            window.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod);
            preview((int)FormMain.PageType.Download);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassResourceBrowserPage",
                "the resource center mounts its glass search page");
            var results = view.PageContentHost.Content;
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) &&
                window.PageCurrentSub == FormMain.PageSubType.DownloadMod,
                "Opening the glass search page created an original download page or selected the wrong category.");

            var project = new ResourceProject(new Newtonsoft.Json.Linq.JObject {
                ["Platform"] = (int)PCLCS.ResourcePlatforms.Modrinth, ["Types"] = (int)PCLCS.ResourceTypes.Mod,
                ["Id"] = "FixtureProject", ["RawName"] = "测试资源", ["Description"] = "简介样本", ["DownloadCount"] = 3,
                ["LastUpdate"] = new DateTime(2025, 2, 3), ["Drops"] = new Newtonsoft.Json.Linq.JArray(210),
                ["Tags"] = new Newtonsoft.Json.Linq.JArray()
            });
            window.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.ResourceDetail,
                Additional = new object[] { project, new List<string>(), "", PCLCS.ModLoaders.None, PCLCS.ResourceTypes.Mod, null! } });
            preview((int)FormMain.PageType.ResourceDetail);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassResourceDetailPage",
                "resource details are built in the glass page");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content),
                "Opening direct resource details constructed the old detail page.");
            window.PageBack();
            preview((int)FormMain.PageType.Download);
            VisualAssetChecks.Pump(() => ReferenceEquals(results, view.PageContentHost.Content),
                "back from resource details returns to the same search page");
            Native.Check(window.PageCurrentSub == FormMain.PageSubType.DownloadMod, "Back lost the active download category.");

            window.PageChange(FormMain.PageType.Favorites, FormMain.PageSubType.DownloadDataPack);
            preview((int)FormMain.PageType.Favorites);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassResourceBrowserPage" &&
                !ReferenceEquals(results, view.PageContentHost.Content), "favorites open a native local-search page");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) &&
                window.PageCurrentSub == FormMain.PageSubType.DownloadDataPack, "Favorites reused the classic page or lost the selected category.");
        }
        finally
        {
            window.PageChange(FormMain.PageType.Launch);
            preview((int)FormMain.PageType.Launch);
        }
    }

    private static void CheckDirectInstallation(FormMain window, GlassLauncherView view, string root, Action<int> preview)
    {
        var priorClient = ModDownload.DlClientListLoader;
        var priorInstall = ModMain.FrmDownloadInstall;
        var priorModify = ModMain.FrmDownloadModify;
        var priorOwner = LauncherInstanceContext.Instance;
        string? pending = PageDownloadInstall.VersionWaitingSelect;
        int starts = 0;
        var catalog = new ModLoader.LoaderTask<string, ModDownload.DlClientListResult>("Install navigation fixture", _ => {
            starts++;
            throw new InvalidOperationException("A direct route started a network request.");
        });
        catalog.Output = new ModDownload.DlClientListResult { Value = new JObject {
            ["versions"] = new JArray(new JObject {
                ["id"] = "1.21.1", ["type"] = "release", ["releaseTime"] = DateTime.UtcNow,
                ["url"] = "https://example.invalid/test.json"
            })
        } };
        catalog.State = ModBase.LoadState.Finished;
        try {
            ModDownload.DlClientListLoader = catalog;
            PageDownloadInstall.VersionWaitingSelect = null;
            var install = ModMain.FrmDownloadInstall = new PageDownloadInstall();
            GlassPresentationChecks.FreezeInstallFeeds(GlassPresentationChecks.InstallHost(install));
            window.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall);
            preview((int)FormMain.PageType.Download);
            VisualAssetChecks.Pump(() => ReferenceEquals(install, view.PageContentHost.Content), "the installer mounts as its own WPF page");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && window.FindName("PanMainRight") is null && starts == 0,
                "Opening the installer populated a hidden source host.");

            var versionFolder = Path.Combine(root, "install-navigation", "versions", "Example");
            Directory.CreateDirectory(versionFolder);
            LauncherInstanceContext.Instance = new ModMinecraft.McInstance(versionFolder);
            object navigation = Native.Property(typeof(FormMain), "GlassInstanceNavigation").GetValue(window)!;
            var modify = (PageDownloadModify)Native.Call(navigation.GetType(), "GetPage", navigation, FormMain.PageSubType.InstanceModify)!;
            GlassPresentationChecks.FreezeInstallFeeds(GlassPresentationChecks.InstallHost(modify));
            object service = Native.Property(typeof(PageDownloadModify), "ModifyService").GetValue(modify)!;
            Native.Set(Native.Get(service, "host")!, "InitializeLoader", (Action)(() => { }));
            window.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceModify);
            preview((int)FormMain.PageType.InstanceSetup);
            VisualAssetChecks.Pump(() => ReferenceEquals(modify, view.PageContentHost.Content), "ordinary modification mounts its own WPF page");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content) && window.FindName("PanMainRight") is null && starts == 0,
                "Opening ordinary modification populated a hidden source host.");
        }
        finally {
            window.PageChange(FormMain.PageType.Launch);
            preview((int)FormMain.PageType.Launch);
            LauncherInstanceContext.Instance = priorOwner;
            ModDownload.DlClientListLoader = priorClient;
            ModMain.FrmDownloadInstall = priorInstall;
            ModMain.FrmDownloadModify = priorModify;
            PageDownloadInstall.VersionWaitingSelect = pending;
        }
    }

    private static void CheckDirectInstanceSelection(FormMain window, GlassLauncherView view, string root, Action<int> preview)
    {
        var priorOwner = LauncherInstanceContext.Instance;
        try
        {
            window.PageChange(FormMain.PageType.InstanceSelect);
            preview((int)FormMain.PageType.InstanceSelect);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassInstanceSelectionPage",
                "selecting a game instance mounts the direct page");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content),
                "The glass instance chooser created the classic folder sidebar or right page.");
            Native.Check(Descendants<TextBlock>(Part<StackPanel>(view, "SidebarPanel")).Any(text => text.Text == "文件夹列表"),
                "The native instance chooser lost the file-folder sidebar.");

            var game = Path.Combine(root, "glass-managed-resources");
            Directory.CreateDirectory(game);
            LauncherInstanceContext.Instance = new ModMinecraft.McInstance(game);
            window.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceResourcePacks);
            preview((int)FormMain.PageType.InstanceSetup);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassLocalResourcesPage",
                "a local resource tab opens directly from instance management");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content),
                "The instance resource tab created a hidden legacy sidebar or list.");
            var local = (FrameworkElement)view.PageContentHost.Content;
            var resourceType = Native.Type("PCL.InstanceResourceEntry");
            var entry = Activator.CreateInstance(resourceType, nonPublic: true)!;
            Native.Set(entry, "Name", "本地资源样本");
            Native.Set(entry, "Description", "直接页面行");
            string folder = Path.Combine(game, "resourcepacks");
            Native.Set(entry, "FullPath", Path.Combine(folder, "sample.zip"));
            var items = Array.CreateInstance(resourceType, 1);
            items.SetValue(entry, 0);
            object body = Native.Get(local, "_controller")!;
            Native.Call(body.GetType(), "ShowResources", body, folder, folder, items);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Descendants<TextBlock>(local).Any(label => label.Text == "本地资源样本"),
                "The native local-resource controller failed to render an item.");
            window.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceJava);
            preview((int)FormMain.PageType.InstanceSetup);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassInstanceJavaPage",
                "the instance Java options mount directly");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content),
                "Instance Java created the classic settings page.");
        }
        finally
        {
            window.PageChange(FormMain.PageType.Launch);
            LauncherInstanceContext.Instance = priorOwner;
            preview((int)FormMain.PageType.Launch);
        }
    }

    private static void CheckMoreAndJava(FormMain window, GlassLauncherView view, Action<int> preview)
    {
        try
        {
            Show(FormMain.PageType.Other, FormMain.PageSubType.OtherAbout, "GlassAboutPage");
            var about = (FrameworkElement)view.PageContentHost.Content;
            Native.Check(Descendants<TextBlock>(about).Any(text => text.Text == "龙腾猫跃"),
                "The native About page lost the original credits.");
            var licenses = Descendants<Expander>(about).Single(expander => expander.Header is TextBlock { Text: "许可与版权声明" });
            licenses.IsExpanded = true;
            VisualAssetChecks.Pump(() => Descendants<Button>(about).Any(button => Equals(button.Content, "保存对应源码")),
                "the native About page retains the bundled software source action");
            Show(FormMain.PageType.Other, FormMain.PageSubType.OtherHelp, "GlassHelpPage");

            Show(FormMain.PageType.Setup, FormMain.PageSubType.SetupJava, "GlassJavaManagementPage");
            var installed = (FrameworkElement)view.PageContentHost.Content;
            Descendants<Button>(installed).Single(button => Equals(button.Content, "下载 Java"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            preview((int)FormMain.PageType.HelpDetail);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == "GlassJavaDownloadPage",
                "download opens the independent Java catalogue");
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content),
                "Java settings mounted the original launch settings page.");
            window.PageBack();
            preview((int)FormMain.PageType.Setup);
            VisualAssetChecks.Pump(() => ReferenceEquals(installed, view.PageContentHost.Content), "return from the catalogue to the same Java management view");
        }
        finally
        {
            window.PageChange(FormMain.PageType.Launch);
            preview((int)FormMain.PageType.Launch);
        }

        void Show(FormMain.PageType page, FormMain.PageSubType sub, string expected)
        {
            window.PageChange(page, sub);
            preview((int)page);
            VisualAssetChecks.Pump(() => view.PageContentHost.Content?.GetType().Name == expected, $"the product window opens {expected}");
            window.UpdateLayout();
            VisualAssetChecks.Drain();
            Native.Check(ReferenceEquals(window.PageRight, view.PageContentHost.Content), $"{expected} mounted a legacy wrapper.");
        }
    }

    private static void CheckWallpaperToolTip(FormMain window, GlassLauncherView view, ImageSource wallpaper) {
        var owner = Part<Button>(view, "NavDownload");
        Native.Check(!MeloongCore.Wpf.ModernTooltipService.GetUseModernTooltip(owner),
            "The legacy hover renderer still overrides workbench tooltips.");
        int previousMode = Settings.Get<int>("UiLauncherColorMode");
        try {
            foreach (int mode in new[] { 0, 1 }) {
                Native.Cache("UiLauncherColorMode", mode);
                Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
                VisualAssetChecks.Pump(() => PCLLF.Presentation.Foundation.LegacyThemePalette.IsDarkTheme == (mode == 1),
                    "Visible WPF palette did not reach the requested theme.");
                Refresh(window);
                const string description = "注意：Mojang 于 Minecraft 26.1 移除了该设置，因此该设置在新版本中无效。\n\n" +
                    "该信息会显示在游戏主界面的左下角，与 F3 调试页面的左上角。\n若留空，则跟随全局设置的值。";
                var tooltip = new ToolTip { Content = description, PlacementTarget = owner };
                if (mode == 0) tooltip.MaxWidth = 230;
                try {
                    tooltip.IsOpen = true;
                    VisualAssetChecks.Pump(() => tooltip.IsLoaded && VisualTreeHelper.GetChildrenCount(tooltip) > 0,
                        "The workbench tooltip did not show its native popup.");
                    VisualAssetChecks.Drain();
                    var pane = (Panel)VisualTreeHelper.GetChild(tooltip, 0);
                    var frost = pane.Children.OfType<GlassSurface>().Single();
                    var text = (TextBlock)VisualTreeHelper.GetChild(frost.Child, 0);
                    var unwrapped = new TextBlock { Text = description, Style = null, FontFamily = text.FontFamily, FontSize = text.FontSize };
                    unwrapped.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Native.Check(text.Text == description && text.TextWrapping == TextWrapping.Wrap &&
                        text.TextTrimming == TextTrimming.None && ReferenceEquals(text.Foreground, tooltip.Foreground),
                        "The workbench tooltip truncated its plain text or lost the tooltip foreground.");
                    Native.Check(text.ActualHeight > unwrapped.DesiredSize.Height && tooltip.ActualWidth <= tooltip.MaxWidth,
                        "The workbench tooltip did not grow vertically to fit long paragraphs within its width.");
                    FrameworkElement? host = GlassMaterial.GetWallpaperHost(frost);
                    Rect ScreenBounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
                        element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
                    bool onWallpaper = ScreenBounds(Part<FrameworkElement>(view, "WallpaperViewport")).Contains(ScreenBounds(frost));
                    Native.Check(frost.BlurRadius == 24 && !frost.ExtendWallpaper && GlassPopupBackdrop.GetEnable(pane),
                        "The tooltip did not use the popup's window-aware frost.");
                    Native.Check(onWallpaper
                        ? ReferenceEquals(frost.WallpaperSource, wallpaper) && host is not null &&
                          ReferenceEquals(PresentationSource.FromVisual(host), PresentationSource.FromVisual(tooltip))
                        : frost.WallpaperSource is null && host is null &&
                          (GlassMaterial.GetDesktopBackdropEnabled(frost) || !GlassMaterial.GetIsEnabled(frost)),
                        "The tooltip's material did not match its position relative to the wallpaper.");
                    if (!SystemParameters.HighContrast) {
                        var foreground = ((SolidColorBrush)tooltip.Foreground).Color;
                        Native.Check((foreground.R + foreground.G + foreground.B > 450) == (mode == 1), "Tooltip text did not follow the current appearance.");
                    }
                }
                finally { tooltip.IsOpen = false; VisualAssetChecks.Drain(); }
            }
        }
        finally {
            Native.Cache("UiLauncherColorMode", previousMode);
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            Refresh(window);
        }
    }

    internal static void CheckScrollingToolTip() {
        // Drive WPF's hover service directly so the fixture does not move the user's cursor.
        object popupService = Native.Property(typeof(FrameworkElement), "PopupControlService").GetValue(null)!;
        Type popupServiceType = popupService.GetType();
        object mouse = Enum.Parse(typeof(ToolTipService).GetNestedType("TriggerAction", BindingFlags.NonPublic)!, "Mouse");
        var owner = new Button { Content = "跟随全局设置", VerticalAlignment = VerticalAlignment.Top, Height = 32 };
        var nextOwner = new Button { Content = "尽量使用官方源", Height = 32 };
        var rows = new StackPanel();
        rows.Children.Add(owner);
        rows.Children.Add(nextOwner);
        var scrollContent = new Border { Width = 600, Height = 600, Child = rows };
        var scroll = new ScrollViewer { Content = scrollContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window {
            Content = scroll, Width = 320, Height = 200, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false
        };
        var tooltip = new ToolTip { Content = "跟随全局设置", Style = (Style)Native.Static("PCL.GlassCommandChrome", "ToolTipStyle", false)! };
        owner.ToolTip = tooltip;
        var nextTooltip = new ToolTip { Content = "尽量使用官方源", Style = tooltip.Style };
        nextOwner.ToolTip = nextTooltip;
        ToolTipService.SetInitialShowDelay(owner, 0);
        ToolTipService.SetInitialShowDelay(nextOwner, 0);
        Native.Static("PCL.GlassCommandChrome", "SetToolTipScrollTracking", scroll, true);
        void Hover(Button target) => Native.Call(popupServiceType, "BeginShowToolTip", popupService, target, mouse);
        int opened = 0;
        tooltip.Opened += (_, _) => opened++;
        int nextOpened = 0;
        nextTooltip.Opened += (_, _) => nextOpened++;
        try {
            window.Show();
            VisualAssetChecks.Pump(() => scroll.ScrollableHeight > 8 && scroll.ScrollableWidth > 8,
                "The tooltip test did not have a scrollable viewport.");
            scroll.ScrollToVerticalOffset(4);
            VisualAssetChecks.Pump(() => scroll.VerticalOffset >= 4, "The tooltip test did not start scrolling.");
            Hover(owner);
            VisualAssetChecks.Observe(45);
            Native.Check(!tooltip.IsOpen && opened == 0, "WPF created a tooltip during scrolling with no previous popup.");

            VisualAssetChecks.Observe(180);
            Hover(owner);
            VisualAssetChecks.Pump(() => opened == 1, "The tooltip did not open over scrollable content.");
            Native.Static("PCL.GlassCommandChrome", "PositionToolTipAtPointer", tooltip, new Point(24, 16));
            Rect firstAnchor = tooltip.PlacementRectangle;
            Native.Static("PCL.GlassCommandChrome", "PositionToolTipAtPointer", tooltip, new Point(240, 16));
            Native.Check(tooltip.IsOpen && tooltip.PlacementRectangle.X - firstAnchor.X == 216 && tooltip.PlacementRectangle.Y == firstAnchor.Y,
                "Moving across the same tooltip owner kept the original pointer anchor.");
            scrollContent.Height += 100;
            VisualAssetChecks.Drain();
            Native.Check(tooltip.IsOpen, "An extent change closed a tooltip without scrolling.");
            scroll.ScrollToVerticalOffset(8);
            VisualAssetChecks.Pump(() => scroll.VerticalOffset >= 8, "The tooltip test did not scroll vertically.");
            VisualAssetChecks.Pump(() => !tooltip.IsOpen && tooltip.PlacementRectangle.IsEmpty,
                "Scrolling did not close the pointer tooltip and release its mouse anchor.");

            for (int offset = 16; offset <= 40; offset += 8) {
                scroll.ScrollToVerticalOffset(offset);
                VisualAssetChecks.Pump(() => scroll.VerticalOffset >= offset, "The tooltip test did not continue scrolling.");
                Hover(nextOwner);
                VisualAssetChecks.Observe(45);
                Native.Check(!nextTooltip.IsOpen && nextOpened == 0, "WPF created a new tooltip while the viewport was still scrolling.");
            }
            VisualAssetChecks.Observe(180);
            Hover(nextOwner);
            VisualAssetChecks.Pump(() => nextOpened == 1, "WPF did not reopen the suppressed tooltip after scrolling.", timeout: 1000);
            scroll.ScrollToHorizontalOffset(8);
            VisualAssetChecks.Pump(() => scroll.HorizontalOffset >= 8, "The tooltip test did not scroll horizontally.");
            Native.Check(!nextTooltip.IsOpen, "The workbench tooltip stayed open as the page scrolled horizontally.");
        }
        finally {
            Native.Static("PCL.GlassCommandChrome", "SetToolTipScrollTracking", scroll, false);
            Native.Call(popupServiceType, "DismissToolTips", popupService);
            window.Close();
            VisualAssetChecks.Drain();
        }
    }

    private static void CheckSelectedVideoWallpaper(FormMain window, GlassLauncherView view, string path)
    {
        string previous = Settings.Get<string>("UiGlassWallpaperPath");
        var image = Part<Image>(view, "WallpaperImage");
        try
        {
            Native.Cache("UiGlassWallpaperPath", path);
            Refresh(window);
            var service = Native.Type("PCL.GlassVideoWallpaper");
            var decoder = (VideoWallpaperPlayback)Native.Field(service, "_synchronized").GetValue(null)!;
            // Delay only the first paired frame; the shell remains visible and its startup timeout still runs.
            decoder.SetPaused(true);
            VisualAssetChecks.Pump(() => image.Source is DrawingImage { Drawing: VideoDrawing { Player.HasVideo: true } },
                "show the temporary wallpaper while the paired decoder is delayed", 10000);
            var native = (VideoDrawing)((DrawingImage)image.Source).Drawing;
            VisualAssetChecks.Pump(() => native.Player.Position > TimeSpan.FromMilliseconds(80), "temporary video advances");
            var material = (DrawingImage)GlassMaterial.GetWallpaperSource(view)!;
            var brush = (VisualBrush)((GeometryDrawing)material.Drawing).Brush;
            var plane = (DrawingVisual)VisualTreeHelper.GetChild(brush.Visual, 0);
            var tiles = (ImageBrush)((GeometryDrawing)plane.Drawing.Children[0]).Brush;
            Native.Check(ReferenceEquals(tiles.ImageSource, image.Source) && plane.Effect is System.Windows.Media.Effects.BlurEffect,
                "Temporary glass and wallpaper did not share the live video drawing.");
            decoder.SetPaused(false);
            VisualAssetChecks.Pump(() => ReferenceEquals(image.Source, decoder.Source) &&
                ReferenceEquals(GlassMaterial.GetWallpaperSource(view), decoder.FrostedSource) &&
                Native.Field(service, "_player").GetValue(null) is null,
                "the paired decoder did not reclaim both layers after a slow start", 10000);
            bool sharp = false, frost = false;
            int paired = 0, mismatched = 0;
            EventHandler onSharp = (_, _) => sharp = true;
            EventHandler onFrost = (_, _) => frost = true;
            EventHandler rendering = (_, _) => {
                if (sharp && frost) paired++;
                if (sharp != frost) mismatched++;
                sharp = frost = false;
            };
            decoder.Source.Changed += onSharp;
            decoder.FrostedSource.Changed += onFrost;
            CompositionTarget.Rendering += rendering;
            try { VisualAssetChecks.Observe(2000); }
            finally {
                CompositionTarget.Rendering -= rendering;
                decoder.Source.Changed -= onSharp;
                decoder.FrostedSource.Changed -= onFrost;
            }
            Native.Check(paired >= 5 && mismatched == 0, $"Wallpaper and glass updated out of step: {paired} paired, {mismatched} unmatched.");
            Console.WriteLine($"PASS slow video startup handed back to paired playback: {paired} paired render ticks in 2s, {mismatched} unmatched.");
        }
        finally
        {
            Native.Cache("UiGlassWallpaperPath", previous);
            Refresh(window);
        }
    }

    private static void Refresh(FormMain window) => Native.Call(typeof(FormMain), "RefreshGlassShell", window);
    private static void RegisterResizeGrips(FormMain window, WindowResizer resizer) {
        var shell = Native.Get(window, "glassShell")!;
        UIElement Grip(int index) => (UIElement)Native.Call(shell.GetType(), "ResizeGrip", shell, index)!;
        resizer.addResizerDown(Grip(0));
        resizer.addResizerLeft(Grip(1));
        resizer.addResizerLeftDown(Grip(2));
        resizer.addResizerLeftUp(Grip(3));
        resizer.addResizerRight(Grip(4));
        resizer.addResizerRightDown(Grip(5));
        resizer.addResizerRightUp(Grip(6));
        resizer.addResizerUp(Grip(7));
    }
    private static void CheckResizeGrips(FormMain window, GlassLauncherView view) {
        PropertyInfo storeProperty = typeof(UIElement).GetProperty("EventHandlersStore", Native.Flags)
            ?? throw new MissingMemberException("UIElement.EventHandlersStore");
        var shell = (GlassShellIntegration)Native.Get(window, "glassShell")!;
        int index = 0;
        foreach (string name in new[] { "ResizerB", "ResizerL", "ResizerLB", "ResizerLT", "ResizerR", "ResizerRB", "ResizerRT", "ResizerT" }) {
            var grip = (FrameworkElement)shell.ResizeGrip(index++);
            object store = storeProperty.GetValue(grip) ?? throw new InvalidOperationException("Resize grip has no actual routed handlers: " + name);
            var handlers = (Array?)Native.Call(store.GetType(), "GetRoutedEventHandlers", store, UIElement.MouseLeftButtonDownEvent);
            bool actual = handlers?.Cast<object>().Any(info => Native.Property(info.GetType(), "Handler").GetValue(info) is Delegate handler &&
                handler.Method.DeclaringType == typeof(WindowResizer) && handler.Method.Name == "MouseDown") == true;
            Native.Check(actual, "Actual native resize handler was not registered on " + name);
        }
        var regions = view.GetSurfaceRegions();
        Native.Check(regions.Count == 3, "Expected three surfaces for resize grip placement.");
        Rect rail = regions[0].Bounds, body = regions[1].Bounds, controls = regions[2].Bounds;
        Point right = view.TranslatePoint(new Point(body.Right - 2, (controls.Bottom + body.Bottom) / 2), window);
        Point left = view.TranslatePoint(new Point(rail.Left + 2, rail.Top + rail.Height / 2), window);
        Point top = view.TranslatePoint(new Point((body.Left + controls.Left) / 2, body.Top + 2), window);
        Point bottom = view.TranslatePoint(new Point(body.Left + body.Width / 2, body.Bottom - 2), window);
        Point topRight = view.TranslatePoint(new Point(controls.Right - 2, controls.Top + 2), window);
        foreach (var (point, name, grip) in new[] { (right, "ResizerR", 4), (left, "ResizerL", 1), (top, "ResizerT", 7), (bottom, "ResizerB", 0), (topRight, "ResizerRT", 6) })
            Native.Check(ReferenceEquals(window.InputHitTest(point), shell.ResizeGrip(grip)),
                "Actual glass inner edge is not hit-testable by its original resize grip: " + name);
        var account = Part<Button>(view, "AccountButton");
        Rect avatar = account.TransformToAncestor(view).TransformBounds(new Rect(account.RenderSize));
        var avatarHit = window.InputHitTest(view.TranslatePoint(new Point(avatar.Left + 8, avatar.Bottom - 8), window)) as DependencyObject;
        Native.Check(avatarHit != null && IsDescendant(avatarHit, account), "The corner resize grip covers the login avatar.");
    }
    private static void CheckSurfaces(GlassLauncherView view) {
        GlassShellChecks.CheckChromeTargets(view, includeWindowOverlays: true);
        var regions = view.GetSurfaceRegions();
        Native.Check(regions.Count == 3 && regions.All(region => region.CornerRadius == 12) &&
            Math.Abs(regions[1].Bounds.Left - regions[0].Bounds.Right - 12) < .2,
            "The native window must match the separated rounded rail/body/controls geometry.");
        var window = (FormMain)System.Windows.Window.GetWindow(view);
        var integration = Native.Get(window, "glassShell")!;
        var controller = (GlassWindowRegionClip)Native.Get(integration, "_windowRegion")!;
        var root = (FrameworkElement)window.Content;
        Native.Check(controller.LastRegionError == 0 && root.Clip is null && ReferenceEquals(root.OpacityMask, view.SurfaceOpacityMask) &&
            Part<Border>(view, "MainSurface").OpacityMask is null,
            "The native region or complete presentation/resize-layer clip did not follow the painted surfaces.");
        nint hwnd = new WindowInteropHelper(window).Handle;
        nint shape = CreateRectRgn(0, 0, 0, 0);
        Native.Check(shape != 0, "Could not allocate the independent native region observation.");
        try {
            NativeFrameBounds frame = default;
            Native.Check(GetWindowRgn(hwnd, shape) != 0 && GetWindowRect(hwnd, out frame),
                "The actual HWND has no readable separated window region.");
            bool Contains(Point point) {
                Point screen = view.PointToScreen(point);
                return PtInRegion(shape, (int)Math.Floor(screen.X - frame.Left), (int)Math.Floor(screen.Y - frame.Top));
            }
            Point gap = new((regions[0].Bounds.Right + regions[1].Bounds.Left) / 2, view.ActualHeight / 2);
            Point topStep = new((regions[1].Bounds.Left + regions[2].Bounds.Left) / 2, regions[1].Bounds.Top / 2);
            Point rightStep = new((regions[1].Bounds.Right + regions[2].Bounds.Right) / 2, (regions[2].Bounds.Bottom + regions[1].Bounds.Bottom) / 2);
            Native.Check(!Contains(gap) && !Contains(topStep) && !Contains(rightStep),
                "The actual HWND still includes the transparent gap or staggered insets outside the three surfaces.");
            if (!view.IsRailExpanded && !view.IsRailAnimating && regions[0].Bounds.Left > 1)
                Native.Check(!Contains(new Point(regions[0].Bounds.Left / 2, view.ActualHeight / 2)),
                    "The collapsed rail's invisible expansion space still intercepts pointer input.");
            foreach (GlassWindowRegion surface in regions) {
                Rect area = surface.Bounds;
                Native.Check(Contains(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)), "A painted workspace surface lies outside its native region.");
                foreach (Point corner in new[] { new Point(area.Left + .5, area.Top + .5), new Point(area.Right - .5, area.Top + .5), new Point(area.Left + .5, area.Bottom - .5), new Point(area.Right - .5, area.Bottom - .5) })
                    Native.Check(Contains(corner), "A one-bit native rounded region is cutting off antialiased alpha: " + corner);
            }
        } finally { DeleteObject(shape); }
        var pixels = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        pixels.Render(root);
        GlassShellChecks.CheckSurfacePixels(view, pixels, "actual window presentation and resize layers");
    }

    private static void IsolatePreviewActions(object integration, GlassLauncherView view) {
        object runtime = Native.Get(integration, "_runtime")!;
        foreach (string eventName in new[] { "StartRequested", "CancelRequested", "SelectInstanceRequested", "InstanceSettingsRequested", "AccountRequested", "RecentWorldRequested", "BackRequested", "NavigationRequested", "DependencyCheckChanged", "AutoFillChanged" }) {
            EventInfo actualEvent = typeof(GlassLauncherView).GetEvent(eventName)!;
            MethodInfo actualHandler = Native.Method(runtime.GetType(), "On" + eventName);
            actualEvent.RemoveEventHandler(view, Delegate.CreateDelegate(actualEvent.EventHandlerType!, runtime, actualHandler));
        }
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeFrameBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeFrameBounds rect);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    private static int CountDesktopBackdropWindows() {
        int count = 0;
        EnumWindows((hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != (uint)Environment.ProcessId) return true;
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString().StartsWith("PCLLF.Glass.DesktopBackdrop.", StringComparison.Ordinal)) count++;
            return true;
        }, 0);
        return count;
    }
    private delegate bool WindowVisitor(nint hwnd, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
    private static long GetExtendedStyle(nint hwnd) => nint.Size == 8 ? GetWindowLongPtr(hwnd, -20).ToInt64() : GetWindowLong(hwnd, -20);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
