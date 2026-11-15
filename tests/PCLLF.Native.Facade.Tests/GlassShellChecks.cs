// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

// The genuine compiled Main view in an owned offscreen WPF window. FormMain startup and DWM
// are deliberately covered by their separate integration/material checks.
internal static class GlassShellChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        VisualAssetChecks.VerifyChild(environment);
        Native.Check(typeof(GlassLauncherView).Assembly == Native.Main && Application.Current.Dispatcher.CheckAccess(),
            "Glass shell checks require the actual Main view on its real WPF Dispatcher.");
        Application app = Application.Current;
        Window? previousMain = app.MainWindow;
        ShutdownMode previousMode = app.ShutdownMode;
        int previousWindowCount = app.Windows.Count;
        Window? window = null;
        try
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Window measures content against Win32's monitor/DPI max-track size. The component's
            // intended viewport must remain exact even on a smaller hosted service desktop.
            var view = new GlassLauncherView { Width = 1120, Height = 720 };
            var viewport = new Canvas { ClipToBounds = false };
            viewport.Children.Add(view);
            DrawingImage icon = Image();
            view.SetState(State(icon));
            view.SetWallpaper(Image());
            window = new Window
            {
                Width = 1120,
                Height = 720,
                Content = viewport,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -30000,
                Top = -30000,
            };
            window.Show();
            VisualAssetChecks.Pump(() => view.IsLoaded && Math.Abs(view.ActualWidth - 1120) < 1 && Math.Abs(view.ActualHeight - 720) < 1,
                "Actual glass shell did not load at its exact component viewport.");
            Flush(window);
            Console.WriteLine($"Glass shell component viewport: {view.ActualWidth}x{view.ActualHeight} DIP; native host: {window.ActualWidth}x{window.ActualHeight} DIP.");
            ConstrainedHost(window, view);

            CapsuleRegions(view);
            Layout(window, view, 1120, 720);
            CheckboxSnapshots(view, icon);
            Layout(window, view, 810, 470);
            Events(view, icon);
            Expansion(view, icon);
            Portals(window, view, icon);
            ContentHost(view, icon);
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            if (window != null)
            {
                window.Content = null;
                window.Close();
            }
            VisualAssetChecks.Drain();
            app.MainWindow = previousMain;
            app.ShutdownMode = previousMode;
        }
        Native.Check(app.Windows.Count == previousWindowCount, "Glass shell checks left an owned window open.");
        Console.WriteLine("PASS glass shell: genuine WPF view, four rounded islands, default/minimum layout and targets, events, snapshots/toggles, reversible expansion, and exclusive accessible portals.");
    }

    private static GlassLauncherViewState State(ImageSource image, bool reduceMotion = true, bool dependency = true,
        bool autoFill = true, bool launching = false, bool options = true, bool home = true, bool back = false) => new()
    {
        IsHome = home,
        SelectedNavigationId = home ? 0 : 3,
        CanGoBack = back,
        PageTitle = back ? "实例设置" : string.Empty,
        AccountName = "Fixture player · 用于布局验证的较长账户名称",
        AccountAvatar = image,
        HasInstance = true,
        InstanceName = "Minecraft 1.21.11 · Fabric · 用于布局验证的较长实例名称",
        InstanceDescription = "Minecraft 1.21.11 · Fabric",
        InstanceIcon = image,
        CanLaunch = true,
        IsLaunching = launching,
        LaunchOptionsAvailable = options,
        LaunchStatus = launching ? "正在准备游戏文件" : string.Empty,
        LaunchProgress = launching ? 0.45 : null,
        DependencyCheckEnabled = dependency,
        AutoFillEnabled = autoFill,
        RecentWorldAvailable = true,
        RecentWorldName = "用于布局验证的较长世界名称",
        RecentWorldDetail = "11/15 · Minecraft 1.21.11 · Fabric",
        RecentWorldIcon = image,
        UseDarkText = true,
        ReduceMotion = reduceMotion,
    };

    private static DrawingImage Image()
    {
        var group = new DrawingGroup();
        using (DrawingContext drawing = group.Open())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(71, 111, 129)), null, new Rect(0, 0, 96, 96));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(180, 207, 177)), null, new Rect(0, 52, 96, 44));
            drawing.DrawEllipse(Brushes.WhiteSmoke, null, new Point(25, 25), 9, 9);
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void CapsuleRegions(GlassLauncherView view)
    {
        IReadOnlyList<GlassWindowRegion> regions = view.GetSurfaceRegions();
        Native.Check(regions.Count == 4, "Home should expose only the main surface and three distinct header capsules.");
        string[] names = ["MainSurface", "AccountSurface", "NavigationSurface", "WindowControlsSurface"];
        for (int i = 0; i < names.Length; i++)
        {
            Rect expected = Bounds(Part<FrameworkElement>(view, names[i]), view);
            double radius = i == 0 ? 36 : 28;
            Native.Check(regions.Any(region => Same(region.Bounds, expected) && Math.Abs(region.CornerRadius - radius) < 0.01),
                "Native region did not retain the actual " + names[i] + " bounds/radius.");
            for (int j = 0; j < i; j++)
                NoOverlap(expected, Bounds(Part<FrameworkElement>(view, names[j]), view), "The outer glass islands overlap: " + names[i] + '/' + names[j]);
        }
        foreach (string name in new[] { "PrimaryCard", "DependencyCard", "RecentWorldCard" })
        {
            var surface = Part<GlassSurface>(view, name);
            Native.Check(surface.CornerRadius == new CornerRadius(16), "A home card lost its reference 16px radius: " + name);
        }
    }

    private static void Layout(Window window, GlassLauncherView view, int width, int height)
    {
        view.Width = width; view.Height = height;
        window.Width = width; window.Height = height;
        VisualAssetChecks.Pump(() => Math.Abs(view.ActualWidth - width) < 1 && Math.Abs(view.ActualHeight - height) < 1,
            "Glass shell did not reach the requested viewport.");
        Flush(window);
        CapsuleRegions(view);
        Rect main = Bounds(Part<FrameworkElement>(view, "MainSurface"), view);
        Rect primary = Bounds(Part<FrameworkElement>(view, "PrimaryCard"), view);
        Native.Check(main.Contains(primary), "The current-instance card extends beyond the main surface at " + width);
        Native.Check(Math.Abs(primary.Left - main.Left - 26.8) < 1 && Math.Abs(primary.Top - main.Top - 26.8) < 1 &&
            Math.Abs(primary.Bottom - main.Bottom + 26.8) < 1,
            "The current-instance card is not anchored to the main top-left or does not fill its padded height at " + width);
        Rect header = Bounds(Part<FrameworkElement>(view, "Header"), view);
        double inset = width < 930 ? 14 : 70;
        Native.Check(Math.Abs(header.Left - main.Left - inset) < 1 && Math.Abs(main.Right - header.Right - inset) < 1,
            "The detached header is no longer inset from both main edges at " + width);
        Native.Check(primary.Contains(Bounds(Part<FrameworkElement>(view, "LaunchButton"), view)) &&
            primary.Contains(Bounds(Part<FrameworkElement>(view, "InstanceNameText"), view)),
            "A long instance label or launch action escapes its card at " + width);
        NoOverlap(Bounds(Part<FrameworkElement>(view, "LaunchButton"), view),
            Bounds(Button(view, "实例设置"), view), "Launch and instance settings overlap at " + width);

        foreach (string name in new[] { "LaunchButton", "ExpandButton", "InstanceSettingsButton", "WallpaperButton", "AccountButton", "NavLaunch", "NavDownload", "NavFavorites", "NavSettings", "NavOther" })
            Target(Part<Button>(view, name), width);
        foreach (string name in new[] { "选择实例", "最小化窗口", "关闭窗口" }) Target(Button(view, name), width);

        // Expanded placement is checked at both viewports, then restored for the next case.
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").IsHitTestVisible, "Auxiliary cards did not become interactive.");
        Rect auxiliary = Bounds(Part<FrameworkElement>(view, "AuxiliaryCards"), view);
        primary = Bounds(Part<FrameworkElement>(view, "PrimaryCard"), view);
        Native.Check(Math.Abs(primary.Left - main.Left - 26.8) < 1 && Math.Abs(primary.Top - main.Top - 26.8) < 1,
            "Expanding options moved the anchored current-instance card at " + width);
        Native.Check(main.Contains(primary) && main.Contains(auxiliary), "Expanded cards extend beyond the main surface at " + width);
        NoOverlap(primary, auxiliary, "Expanded cards overlap at " + width);
        NoOverlap(Bounds(Part<FrameworkElement>(view, "DependencyCard"), view),
            Bounds(Part<FrameworkElement>(view, "RecentWorldCard"), view), "The two expanded cards overlap at " + width);
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").Visibility == Visibility.Hidden,
            "Auxiliary cards did not close after layout verification.");
        DetachedGaps(window, view, width);
    }

    private static void ConstrainedHost(Window window, GlassLauncherView view)
    {
        // Exercise the same Window.GetWindowMinMax measure/arrange clamp locally on large displays.
        // The actual FormMain/monitor sizing contract remains in glass-window integration checks.
        window.MaxWidth = 960;
        window.MaxHeight = 600;
        Flush(window);
        Native.Check(window.ActualWidth <= 961 && window.ActualHeight <= 601 && view.IsLoaded &&
            Math.Abs(view.ActualWidth - 1120) < 1 && Math.Abs(view.ActualHeight - 720) < 1,
            "A constrained native host changed the exact loaded component viewport.");
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        Flush(window);
    }

    private static void DetachedGaps(Window window, GlassLauncherView view, int width)
    {
        view.SetSidebar(new[] { new GlassSidebarItem { Label = "上下文选项", IsSelected = true } });
        Flush(window);
        Rect main = Bounds(Part<FrameworkElement>(view, "MainSurface"), view);
        Rect rail = Bounds(Part<FrameworkElement>(view, "SideRail"), view);
        Native.Check(main.Left - rail.Right >= 20 && main.Left - rail.Right <= 25,
            "The contextual rail is not detached by its reference gap at " + width);
        Rect account = Bounds(Part<FrameworkElement>(view, "AccountSurface"), view);
        Rect navigation = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
        Rect controls = Bounds(Part<FrameworkElement>(view, "WindowControlsSurface"), view);
        var samples = new[]
        {
            new Point((account.Right + navigation.Left) / 2, account.Top + account.Height / 2),
            new Point((navigation.Right + controls.Left) / 2, controls.Top + controls.Height / 2),
            new Point(navigation.Left + navigation.Width / 2, (navigation.Bottom + main.Top) / 2),
            new Point((rail.Right + main.Left) / 2, rail.Top + rail.Height / 2),
            new Point(main.Left + 2, main.Top + 2),
        };
        foreach (bool wallpaper in new[] { true, false })
        {
            view.SetWallpaper(wallpaper ? Image() : null!);
            Flush(window);
            if (wallpaper)
            {
                var host = Part<Image>(view, "WallpaperImage");
                var clip = host.Clip as RectangleGeometry;
                Native.Check(Math.Abs(host.ActualWidth - view.ActualWidth) < 1 && clip != null &&
                    Same(clip.Rect, main) && clip.RadiusX == 36 && clip.RadiusY == 36,
                    "Wallpaper source mapping or its strict main-only rounded clipping changed.");
            }
            var render = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            render.Render(view);
            foreach (Point sample in samples)
            {
                byte[] pixel = new byte[4];
                render.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), pixel, 4, 0);
                Native.Check(pixel[3] == 0, "A detached capsule/rail/main gap is not truly transparent in " +
                    (wallpaper ? "wallpaper" : "desktop") + " mode at " + width + ": " + sample);
            }
        }
        view.SetWallpaper(Image());
        view.SetSidebar(Array.Empty<GlassSidebarItem>());
        Flush(window);
    }

    private static void CheckboxSnapshots(GlassLauncherView view, ImageSource image)
    {
        var dependency = new List<bool>(); var autoFill = new List<bool>();
        view.DependencyCheckChanged += dependency.Add;
        view.AutoFillChanged += autoFill.Add;
        try
        {
            view.SetState(State(image, dependency: false, autoFill: true));
            view.SetState(State(image, dependency: true, autoFill: false));
            Native.Check(dependency.Count == 0 && autoFill.Count == 0, "Applying a state snapshot emitted user setting callbacks.");
            Native.Check(ReferenceEquals(Part<Image>(view, "InstanceImage").Source, image) &&
                ReferenceEquals(Part<Image>(view, "AccountAvatarImage").Source, image) &&
                ReferenceEquals(Part<Image>(view, "RecentWorldImage").Source, image), "The view replaced supplied actual images.");
            Click(Part<Button>(view, "ExpandButton"));
            VisualAssetChecks.Drain();
            Toggle(Part<CheckBox>(view, "DependencyCheckBox"));
            Toggle(Part<CheckBox>(view, "AutoFillCheckBox"));
            Native.Check(dependency.SequenceEqual(new[] { false }) && autoFill.SequenceEqual(new[] { true }),
                "Actual checkbox automation did not forward the toggled values exactly once.");
            view.SetState(State(image, dependency: true, autoFill: false));
            Native.Check(dependency.Count == 1 && autoFill.Count == 1, "A later snapshot fed setting callbacks back into the owner.");
            Click(Part<Button>(view, "ExpandButton"));
        }
        finally
        {
            view.DependencyCheckChanged -= dependency.Add;
            view.AutoFillChanged -= autoFill.Add;
        }
    }

    private static void Events(GlassLauncherView view, ImageSource image)
    {
        var calls = new Dictionary<string, int>();
        void Add(string key) => calls[key] = calls.GetValueOrDefault(key) + 1;
        view.StartRequested += (_, _) => Add("start");
        view.CancelRequested += (_, _) => Add("cancel");
        view.SelectInstanceRequested += (_, _) => Add("select");
        view.InstanceSettingsRequested += (_, _) => Add("settings");
        view.MinimizeRequested += (_, _) => Add("minimize");
        view.CloseRequested += (_, _) => Add("close");
        view.RecentWorldRequested += (_, _) => Add("world");
        view.WallpaperRequested += (_, _) => Add("wallpaper");
        view.BackRequested += (_, _) => Add("back");
        var navigation = new List<int>(); view.NavigationRequested += navigation.Add;
        view.SetState(State(image));
        Click(Part<Button>(view, "LaunchButton"));
        Click(Button(view, "选择实例"));
        Click(Part<Button>(view, "InstanceSettingsButton"));
        Click(Button(view, "最小化窗口"));
        Click(Button(view, "关闭窗口"));
        Click(Part<Button>(view, "WallpaperButton"));
        foreach (string name in new[] { "NavDownload", "NavFavorites", "NavSettings", "NavOther", "NavLaunch" }) Click(Part<Button>(view, name));
        Native.Check(navigation.SequenceEqual(new[] { 1, 10, 3, 4, 0 }), "Top navigation emitted altered legacy page identities.");
        Click(Part<Button>(view, "ExpandButton"));
        Click(Part<Button>(view, "RecentWorldButton"));
        Click(Part<Button>(view, "ExpandButton"));
        view.SetState(State(image, launching: true)); Click(Part<Button>(view, "LaunchButton"));
        view.SetState(State(image, back: true)); Click(Button(view, "返回上一页"));
        foreach (string name in new[] { "start", "cancel", "select", "settings", "minimize", "close", "world", "wallpaper", "back" })
            Native.Check(calls.GetValueOrDefault(name) == 1, "Actual shell action was lost or duplicated: " + name);
        view.SetState(State(image));
        view.NavigationRequested -= navigation.Add;
    }

    private static void Expansion(GlassLauncherView view, ImageSource image)
    {
        view.SetState(State(image, reduceMotion: false));
        Button plus = Part<Button>(view, "ExpandButton");
        FrameworkElement cards = Part<FrameworkElement>(view, "AuxiliaryCards");
        Click(plus);
        VisualAssetChecks.Observe(30);
        for (int index = 0; index < 4; index++)
        {
            Native.Check(plus.IsEnabled && plus.IsHitTestVisible, "Expansion locked input while a spring was running.");
            Click(plus);
            VisualAssetChecks.Observe(15);
        }
        VisualAssetChecks.Pump(() => cards.IsHitTestVisible && cards.Opacity > 0.999,
            "Rapid reversal did not finish in the user's final expanded state.");
        Native.Check(AutomationProperties.GetName(plus) == "收起启动选项", "Expansion accessibility state differs from its final action.");
        view.SetState(State(image, options: false));
        Native.Check(cards.Visibility == Visibility.Hidden && !plus.IsEnabled && plus.Visibility == Visibility.Hidden,
            "Busy state left launch option cards or their trigger available.");
        view.SetState(State(image));
        Native.Check(cards.IsHitTestVisible && cards.Opacity == 1 && plus.Visibility == Visibility.Visible,
            "Restoring availability discarded the user's expanded choice.");
        Click(plus);
        Native.Check(cards.Visibility == Visibility.Hidden && cards.Opacity == 0,
            "Reduced motion did not close auxiliary cards immediately.");
    }

    private static void Portals(Window window, GlassLauncherView view, ImageSource image)
    {
        view.SetState(State(image));
        var accountContent = new TextBox { Text = "Fixture account", MinWidth = 160, Height = 36 };
        var navigationContent = new Button { Content = "Fixture contextual action", MinWidth = 160, Height = 40 };
        int accountRequests = 0, navigationRequests = 0;
        view.AccountRequested += (_, _) => accountRequests++;
        view.NavigationMenuRequested += (_, _) => navigationRequests++;
        view.SetAccountContent(accountContent);
        view.SetNavigationContent(navigationContent);
        Flush(window);
        Button account = Part<Button>(view, "AccountButton");
        Button menu = Part<Button>(view, "NavigationMenuButton");
        FrameworkElement accountPopover = Part<FrameworkElement>(view, "AccountPopover");
        FrameworkElement navigationPopover = Part<FrameworkElement>(view, "NavigationPopover");
        Target(menu, 810);
        Click(account);
        Native.Check(accountRequests == 1 && accountPopover.IsVisible && !navigationPopover.IsVisible,
            "Account action did not open the actual exclusive portal.");
        Native.Check(ReferenceEquals(Part<ContentControl>(view, "AccountContent").Content, accountContent), "Account content was copied or replaced.");
        Escape(accountContent);
        Native.Check(accountPopover.Visibility == Visibility.Collapsed && ReferenceEquals(FocusManager.GetFocusedElement(window), account),
            "Escape did not dismiss the account portal and restore logical focus to its trigger.");
        Click(menu);
        Native.Check(navigationRequests == 1 && navigationPopover.IsVisible && !accountPopover.IsVisible,
            "Context action did not open the actual exclusive portal.");
        Native.Check(ReferenceEquals(Part<ContentControl>(view, "NavigationContent").Content, navigationContent), "Context navigation content was copied or replaced.");
        Flush(window);
        Native.Check(view.GetSurfaceRegions().Any(region => Math.Abs(region.CornerRadius - 24) < 0.01 && Same(region.Bounds, Bounds(navigationPopover, view))),
            "The visible contextual portal was omitted from native rounded regions.");
        Escape(navigationContent);
        Native.Check(navigationPopover.Visibility == Visibility.Collapsed && ReferenceEquals(FocusManager.GetFocusedElement(window), menu),
            "Escape did not dismiss context navigation and restore logical focus to its trigger.");
        view.SetAccountOpen(true); view.SetNavigationOpen(true);
        Native.Check(!accountPopover.IsVisible && navigationPopover.IsVisible, "Opening context navigation left both glass portals visible.");
        view.SetAccountOpen(true);
        Native.Check(accountPopover.IsVisible && !navigationPopover.IsVisible, "Opening the account left both glass portals visible.");
        view.SetAccountOpen(false);
        view.SetAccountContent(null!); view.SetNavigationContent(null!); view.SetSidebar(Array.Empty<GlassSidebarItem>());
        Flush(window);
        CapsuleRegions(view);
    }

    private static void ContentHost(GlassLauncherView view, ImageSource image)
    {
        var page = new Grid();
        var originalRightContainer = new ContentControl { Content = page };
        view.SetPageContent(originalRightContainer);
        view.SetState(State(image));
        Flush(Window.GetWindow(view));
        Native.Check(originalRightContainer.IsLoaded && page.IsLoaded && view.PageContentHost.Visibility == Visibility.Hidden &&
            !view.PageContentHost.IsVisible && !view.PageContentHost.IsHitTestVisible,
            "Home did not materialize and retain the original right page while keeping it hidden from UI and input.");
        view.SetState(State(image, home: false));
        Native.Check(ReferenceEquals(view.PageContentHost.Content, originalRightContainer) && view.PageContentHost.IsVisible &&
            !Part<FrameworkElement>(view, "HomePanel").IsVisible, "The main content slot did not host the supplied actual page.");
        view.SetState(State(image));
        Native.Check(!view.PageContentHost.IsVisible && Part<FrameworkElement>(view, "HomePanel").IsVisible,
            "Returning home did not restore the current-instance shell.");
        Native.Check(page.IsLoaded && originalRightContainer.IsLoaded, "Returning home unloaded the backend's actual original right page.");
        view.SetPageContent(null!);
    }

    private static T Part<T>(GlassLauncherView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException("Actual glass shell control missing: " + name);

    private static Button Button(GlassLauncherView view, string name) => Descendants(view).OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Click(Button button)
    {
        Native.Check(button.IsEnabled && button.IsVisible && button.IsHitTestVisible,
            "Cannot activate an unavailable real shell action: " + AutomationProperties.GetName(button));
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new InvalidOperationException("Actual button has no Invoke automation pattern.");
        invoke.Invoke();
        VisualAssetChecks.Drain();
    }

    private static void Toggle(CheckBox checkbox)
    {
        Native.Check(checkbox.IsEnabled && checkbox.IsVisible, "Cannot toggle an unavailable real checkbox.");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(checkbox) ?? new CheckBoxAutomationPeer(checkbox);
        var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider ?? throw new InvalidOperationException("Actual checkbox has no Toggle automation pattern.");
        toggle.Toggle();
        VisualAssetChecks.Drain();
    }

    private static void Escape(UIElement source)
    {
        PresentationSource presentation = PresentationSource.FromVisual(source) ?? throw new InvalidOperationException("Escape requires an actual hosted presentation source.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        VisualAssetChecks.Drain();
        Native.Check(args.Handled, "The actual portal did not consume Escape.");
    }

    private static void Target(Button button, int width)
    {
        Native.Check(button.ActualWidth >= 35.9 && button.ActualHeight >= 35.9,
            "A visible hit target is smaller than 36px at " + width + ": " + AutomationProperties.GetName(button));
        Native.Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), "A shell action lacks its accessibility name.");
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static bool Same(Rect first, Rect second) => Math.Abs(first.X - second.X) < 0.1 && Math.Abs(first.Y - second.Y) < 0.1 &&
        Math.Abs(first.Width - second.Width) < 0.1 && Math.Abs(first.Height - second.Height) < 0.1;

    private static void NoOverlap(Rect first, Rect second, string reason)
    {
        Rect intersection = Rect.Intersect(first, second);
        Native.Check(intersection.IsEmpty || intersection.Width * intersection.Height < 0.1, reason);
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        VisualAssetChecks.Drain();
        window.UpdateLayout();
    }
}
