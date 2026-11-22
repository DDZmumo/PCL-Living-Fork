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
            var left = new MyPageLeft();
            var contextRows = new StackPanel(); left.Children.Add(contextRows);
            contextRows.Children.Add(new MyListItem { Title = "界面预览", Type = MyListItem.CheckType.RadioBox, Logo = "M2,2 H18 V18 H2 Z" });
            var right = new MyPageRight {
                Child = new TextBlock { Name = "WorkspaceFixtureContent", Text = "原生页面容器预览\n\n此测试保留真正的 FormMain 页面、任务与提示容器。\n预览数据不连接账号、游戏或下载服务。", Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap }
            };
            Action<MainHost> configure = host => {
                windowPorts = host;
                void Unexpected(string port) { unexpectedPorts.Add(port); throw new InvalidOperationException("Glass fixture attempted a forbidden startup/platform port: " + port); }
                long tick = 0;
                host.Clock = () => Environment.TickCount64;
                host.StartTick = () => tick; host.WriteStartTick = value => tick = value;
                host.CreateLaunchLeft = () => { }; host.CreateLaunchRight = () => { };
                host.LeftPage = _ => new LegacyMainLeftPage { Element = left };
                var productRightPage = host.RightPage;
                host.RightPage = page => page == (int)FormMain.PageType.Link ? productRightPage(page) : right;
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
            VerifyHiddenShortcut(window, windowPorts!);
            environment.AssertNoForbiddenIo();
            Native.Check(window.IsLoaded && window.AllowsTransparency && window.Opacity == 1,
                "Actual glass FormMain lost its alpha-enabled HWND or opaque foreground content.");
            Native.Check(view.PageContentHost.Content is FrameworkElement &&
                !ReferenceEquals(view.PageContentHost.Content, Part<Border>(window, "PanMainRight")) && right.IsLoaded && left.IsLoaded,
                "The new presentation must be distinct while the command/state sources stay loaded.");
            Native.Check(!Part<Border>(window, "PanMainRight").IsVisible && !right.IsVisible && !left.IsVisible,
                "An original page is visible inside the new glass presentation.");
            if (!interactive && PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation) {
                previewMotion = true;
                var legacyRight = Part<Border>(window, "PanMainRight");
                ContentControl departingHost = view.PageContentHost;
                var departing = (ContentControl)departingHost.Content;
                TextBlock departingText = Descendants<TextBlock>(departing).Single(text => text.Name == "WorkspaceFixtureContent");
                var replacement = new MyPageRight { Child = new TextBlock { Text = "右侧页面切换测试" } };
                void RequestReplacement()
                {
                    previewPage = 1;
                    window.PageCurrent = FormMain.PageType.Download;
                    window.PageRight = replacement;
                    right.PageOnExit();
                    right.PageOnForceExit();
                    legacyRight.Child = null;
                    Refresh(window);
                }
                void ReturnHome()
                {
                    previewPage = 0;
                    window.PageCurrent = FormMain.PageType.Launch;
                    window.PageRight = right;
                    legacyRight.Child = right;
                    right.PageOnEnter();
                    Refresh(window);
                }
                RequestReplacement();
                Native.Check(right.Child.Visibility == Visibility.Collapsed && departingText.Visibility == Visibility.Visible,
                    "The original page's exit hid the content inside the departing WPF projection.");
                Native.Check(ReferenceEquals(departingHost.Content, departing) && departingHost.Opacity > .99,
                    "The shell replaced the outgoing projection during the legacy page's empty interval.");
                VisualAssetChecks.Pump(() => departingHost.Opacity is > .2 and < .9,
                    "the right page starts exiting before its replacement is available", timeout: 1500);
                Native.Check(!departingHost.IsHitTestVisible && departingHost.RenderTransform is TransformGroup exit &&
                    exit.Children.OfType<ScaleTransform>().Single().ScaleX < .999 &&
                    Math.Abs(exit.Children.OfType<TranslateTransform>().Single().Y) > 1,
                    "The pending right-page exit lost depth, travel or input isolation.");
                double departingOpacity = departingHost.Opacity;
                ReturnHome();
                Native.Check(ReferenceEquals(departingHost.Content, departing) && departingHost.IsHitTestVisible &&
                    departingHost.Opacity == departingOpacity, "Canceling a pending replacement did not reverse the same page from its current pose.");
                VisualAssetChecks.Pump(() => departingHost.Opacity > .999 && !view.IsTransitioning, "the restored page returns to rest");

                RequestReplacement();
                legacyRight.Child = replacement;
                Refresh(window);
                Native.Check(ReferenceEquals(view.PageContentHost.Content, departing),
                    "Cold page projection ran in the navigation feedback pass.");
                VisualAssetChecks.Pump(() => !ReferenceEquals(view.PageContentHost.Content, departing), "queued right page projection");
                Native.Check(ReferenceEquals(departingHost.Content, departing) &&
                    departing.Content is not null && !departingHost.IsHitTestVisible,
                    "The shell disposed the outgoing right-page projection before its animation.");
                var interrupted = (ContentControl)view.PageContentHost.Content;
                ReturnHome();
                Native.Check(ReferenceEquals(view.PageContentHost.Content, departing) && departingHost.IsHitTestVisible,
                    "Returning Home must start the right-page transition in the same pass as the sidebar.");
                VisualAssetChecks.Pump(() => interrupted.Content is null && !view.IsTransitioning,
                    "rapid A to B to A settles and the interrupted page is disposed");
                Native.Check(departing.Content is not null,
                    "Returning to an outgoing page constructed another projection or disposed the restored one.");
                previewMotion = false;
                previewPage = 0;
                window.PageCurrent = FormMain.PageType.Launch;
                Refresh(window); VisualAssetChecks.Drain();
            }
            if (!interactive) CheckInstanceProjectionCache(window, view, scope.Root);
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
                VisualAssetChecks.Pump(() => !ReferenceEquals(view.PageContentHost.Content, lobby), "the lobby returns to Home");
            }
            var root = (Grid)window.Content;
            foreach (string part in new[] { "PanHint", "PanMsg" })
                Native.Check(!ReferenceEquals(VisualTreeHelper.GetParent(Part<FrameworkElement>(window, part)), root) &&
                    !Part<FrameworkElement>(window, part).IsVisible, "An original overlay is still visible: " + part);
            Native.Check(IsDescendant(Part<FrameworkElement>(window, "BtnExtraDownload"), root), "Actual download/task button was dropped.");
            Native.Check(((UIElement)Native.Get(integration, "_legacyRoot")!).Visibility == Visibility.Hidden,
                "Original BAML root must remain loaded and hidden beneath the shell.");
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
            previewPage = 1; window.PageCurrent = FormMain.PageType.Download; Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
            CheckSurfaces(view);
            Native.Check(Part<StackPanel>(view, "SidebarPanel").Children.Count > 0 && view.FindName("NavigationPopover") is null &&
                !Part<Border>(window, "PanMainLeft").IsVisible,
                "Context navigation must use new sidebar elements with the original state source hidden.");
            previewPage = 0; window.PageCurrent = FormMain.PageType.Launch; Refresh(window); window.UpdateLayout(); VisualAssetChecks.Observe(300);
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
            if (!interactive) CheckCrashDialog(window, integration);
            var boundedBrush = (ImageBrush)Native.Call(Native.Type("PCL.SetupPageRuntime"), "LoadBoundedBackgroundBrush", null, wallpaper)!;
            Native.Check(!boundedBrush.IsFrozen && boundedBrush.ImageSource is { IsFrozen: true },
                "Pictures background brush must remain mutable for suit/alignment updates while keeping a frozen BitmapSource.");
            Canvas imgBack = Part<Canvas>(window, "ImgBack");
            Brush? oldImgBack = imgBack.Background;
            try {
                imgBack.Background = boundedBrush;
                PageSetupUI.BackgroundRefresh(false, true);
                Native.Check(ReferenceEquals(imgBack.Background, boundedBrush), "Glass refreshed a wallpaper from the legacy folder.");
                Native.Cache("UiGlassWallpaperPath", "");
                window.UpdateBackgroundAndTitleBar();
                Native.Check(Part<Image>(view, "WallpaperImage").Source is null,
                    "Clearing the selection exposed the wallpaper from the legacy folder.");
            } finally { imgBack.Background = oldImgBack; }
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
            VisualAssetChecks.Pump(() => {
                if (window.WindowState != WindowState.Normal || IsIconic(mainHandle) || !IsWindowVisible(mainHandle) || !IsWindowEnabled(mainHandle)) {
                    restoredSince = null;
                    return false;
                }
                restoredSince ??= Environment.TickCount64;
                return Environment.TickCount64 - restoredSince.Value >= 150;
            }, "stable actual FormMain native restore completion", 5000);
            Native.Check(refreshTimer.IsEnabled, "The restored workspace did not resume refreshing its UI state.");
            Native.Check(CountDesktopBackdropWindows() == initialBackdropWindows, "Restore created a desktop backdrop.");
            CheckSurfaces(view);
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
        Console.WriteLine("PASS workspace FormMain: independent presentation over hidden state sources; original commands and resize handlers, launcher wallpaper/opaque background, zero desktop composition windows, minimize/restore and cleanup; startup/account/game/profile access guarded.");
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

        var source = window.PageRight;
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

    private static void CheckInstanceProjectionCache(FormMain window, GlassLauncherView view, string root)
    {
        var legacy = Part<Border>(window, "PanMainRight");
        var previous = window.PageCurrent;
        var previousPage = window.PageRight;
        var previousChild = legacy.Child;
        var previousInstance = PageInstanceLeft.Instance;
        try {
            PageInstanceLeft.Instance = new ModMinecraft.McInstance(Path.Combine(root, "versions", "cache-one"));
            window.PageCurrent = FormMain.PageType.InstanceSetup;
            var pages = Enumerable.Range(0, 5).Select(index => new MyPageRight { Child = new TextBlock { Text = "实例页 " + index } }).ToArray();
            ContentControl Mount(MyPageRight page) {
                object displayed = view.PageContentHost.Content;
                window.PageRight = page;
                legacy.Child = page;
                Refresh(window);
                VisualAssetChecks.Pump(() => !ReferenceEquals(view.PageContentHost.Content, displayed), "queued instance page projection");
                return (ContentControl)view.PageContentHost.Content;
            }
            var first = Mount(pages[0]);
            var second = Mount(pages[1]);
            VisualAssetChecks.Drain();
            Native.Check(ReferenceEquals(Mount(pages[0]), first), "Switching back to the same instance rebuilt its detached page.");
            Mount(pages[2]); Mount(pages[3]); Mount(pages[4]);
            VisualAssetChecks.Pump(() => second.Content is null, "Old instance pages exceeded the bounded cache.");
            var old = (ContentControl)view.PageContentHost.Content;
            PageInstanceLeft.Instance = new ModMinecraft.McInstance(Path.Combine(root, "versions", "cache-two"));
            Native.Check(!ReferenceEquals(Mount(pages[4]), old), "A different instance reused the old instance's page projection.");
        } finally {
            PageInstanceLeft.Instance = previousInstance;
            window.PageCurrent = previous;
            window.PageRight = previousPage;
            legacy.Child = previousChild;
            Refresh(window);
            VisualAssetChecks.Drain();
        }
    }

    private static void VerifyHiddenShortcut(FormMain window, MainHost host) {
        object controller = Native.Get(window, "Controller")!;
        FieldInfo viewField = controller.GetType().GetFields(Native.Flags).Single(field => field.FieldType == typeof(LegacyMainWindowView));
        var view = (LegacyMainWindowView)viewField.GetValue(controller)!;
        Func<bool>? previousMode = view.UsesGlassShell;
        bool previousHidden = host.HiddenForceShow();
        void Press() => Native.Call(controller.GetType(), "KeyDown", controller,
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F12) { RoutedEvent = Keyboard.KeyDownEvent });
        try {
            view.UsesGlassShell = () => true; Press();
            Native.Check(host.HiddenForceShow() == previousHidden, "F12 still changed the removed visibility feature in the modern shell.");
            view.UsesGlassShell = () => false; Press();
            Native.Check(host.HiddenForceShow() != previousHidden, "The classic shell lost its original F12 visibility shortcut.");
        }
        finally { view.UsesGlassShell = previousMode; host.WriteHiddenForceShow(previousHidden); }
    }

    private static void CheckCrashDialog(FormMain window, object integration) {
        var panel = Part<Grid>(window, "PanMsg");
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
        var modal = new MyMsgText(message);
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
            panel.Children.Add(modal);
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
            panel.Children.Remove(modal);
            window.Height = previousHeight;
            Refresh(window);
            VisualAssetChecks.Drain();
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
                var tooltip = new ToolTip { Content = "资源中心提示", PlacementTarget = owner };
                try {
                    tooltip.IsOpen = true;
                    VisualAssetChecks.Pump(() => tooltip.IsLoaded && VisualTreeHelper.GetChildrenCount(tooltip) > 0,
                        "The workbench tooltip did not show its native popup.");
                    VisualAssetChecks.Drain();
                    var pane = (Panel)VisualTreeHelper.GetChild(tooltip, 0);
                    var frost = pane.Children.OfType<GlassSurface>().Single();
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
                        var text = ((SolidColorBrush)tooltip.Foreground).Color;
                        Native.Check((text.R + text.G + text.B > 450) == (mode == 1), "Tooltip text did not follow the current appearance.");
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
        var regions = view.GetSurfaceRegions();
        Native.Check(regions.Count == 3, "Expected three surfaces for resize grip placement.");
        Rect rail = regions[0].Bounds, body = regions[1].Bounds, controls = regions[2].Bounds;
        Point right = view.TranslatePoint(new Point(body.Right - 2, (controls.Bottom + body.Bottom) / 2), window);
        Point left = view.TranslatePoint(new Point(rail.Left + 2, rail.Top + rail.Height / 2), window);
        Point top = view.TranslatePoint(new Point((body.Left + controls.Left) / 2, body.Top + 2), window);
        Point bottom = view.TranslatePoint(new Point(body.Left + body.Width / 2, body.Bottom - 2), window);
        Point topRight = view.TranslatePoint(new Point(controls.Right - 2, controls.Top + 2), window);
        foreach (var (point, name) in new[] { (right, "ResizerR"), (left, "ResizerL"), (top, "ResizerT"), (bottom, "ResizerB"), (topRight, "ResizerRT") })
            Native.Check(ReferenceEquals(window.InputHitTest(point), Part<FrameworkElement>(window, name)),
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
