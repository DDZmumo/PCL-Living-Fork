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

            WorkbenchFrame(view);
            BrandVector(window, view, icon);
            AvatarPixelGrid(window, view, icon);
            Layout(window, view, 1120, 720);
            CheckboxSnapshots(view, icon);
            Layout(window, view, 810, 470);
            Events(view, icon);
            Expansion(view, icon);
            Portals(window, view, icon);
            ContentHost(view, icon);
            GlassMotionChecks.Run(window, view, icon);
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
        Console.WriteLine("PASS glass shell: genuine WPF workbench, two rounded surfaces and transparent gap, internal wallpaper, crisp layered pixel avatar, vertical navigation, three-pane single-instance launch pane, default/minimum layout and targets, events, snapshots/toggles, reversible options and accessible panels.");
    }

    private static GlassLauncherViewState State(ImageSource image, bool reduceMotion = true, bool dependency = true,
        bool autoFill = true, bool launching = false, bool options = true, bool home = true, bool back = false, bool useDarkText = true) => new()
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
        UseDarkText = useDarkText,
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

    private static void AvatarPixelGrid(Window window, GlassLauncherView view, ImageSource restoreIcon)
    {
        BitmapSource face = AvatarLayer(false), hat = AvatarLayer(true);
        var avatar = (DrawingImage)Native.Static("PCL.GlassShellRuntime", "ComposeAccountAvatar", face, hat)!;
        var drawing = (DrawingGroup)avatar.Drawing;
        Native.Check(drawing.Children.Count == 2 && drawing.Children.Cast<ImageDrawing>().All(layer => layer.Rect == new Rect(0, 0, 32, 32)),
            "Face and transparent hat must share the same full 32-DIP grid.");
        view.SetState(State(avatar)); Flush(window);
        Image image = Part<Image>(view, "AccountAvatarImage");
        Native.Check(image.ActualWidth == 32 && image.ActualHeight == 32 && image.SnapsToDevicePixels && image.UseLayoutRounding &&
            RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.NearestNeighbor,
            "The actual footer avatar lost its 32-DIP pixel-aligned nearest-neighbor presentation.");
        uint[] palette = { 0xFF000000, 0xFFFFFFFF, 0xFFFF00FF, 0xFF00FFFF };
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 1.75d })
        {
            byte[] pixels = AvatarPixels(image, scale);
            int size = (int)(32 * scale), block = size / 8;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                int sx = x / block, sy = y / block;
                uint expected = sx == 0 && sy == 0 ? palette[2] : sx == 7 && sy == 7 ? palette[3] : palette[(sx + sy) % 2];
                Native.Check(AvatarColor(pixels, (y * size + x) * 4) == expected,
                    $"Avatar face/hat pixels blurred or shifted at {scale * 100:0}% ({size}px), pixel {x},{y}.");
            }
            // Same drawing under the previous smooth image policy must introduce colors
            // absent from the 8x8 sources; this prevents a metadata-only regression check.
            var smooth = new Image { Source = avatar, Width = 32, Height = 32, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(smooth, BitmapScalingMode.LowQuality);
            smooth.Measure(new Size(32, 32)); smooth.Arrange(new Rect(0, 0, 32, 32));
            byte[] blended = AvatarPixels(smooth, scale);
            Native.Check(Enumerable.Range(0, size * size).Any(pixel => !palette.Contains(AvatarColor(blended, pixel * 4))),
                "The old smooth-scaling control did not distinguish pixel-art interpolation at " + scale);
        }
        // Ordinary photos retain their own source policy; projection must not force every
        // Image to nearest-neighbor while preserving the original skin image policy.
        var source = new Image { Source = face, Width = 32, Height = 32 };
        RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.NearestNeighbor);
        using (var session = PCL.GlassPresentation.Create(source))
        {
            var projected = (Image)((ContentControl)session.View).Content;
            Native.Check(RenderOptions.GetBitmapScalingMode(projected) == BitmapScalingMode.NearestNeighbor && projected.SnapsToDevicePixels,
                "Projected skin image lost the source pixel-art policy.");
            RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.HighQuality); session.Refresh();
            Native.Check(RenderOptions.GetBitmapScalingMode(projected) == BitmapScalingMode.HighQuality,
                "Image projection forced a photo to the avatar's pixel-art policy.");
        }
        view.SetState(State(restoreIcon)); Flush(window);
    }

    private static BitmapSource AvatarLayer(bool hat)
    {
        var pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int at = (y * 8 + x) * 4;
            if (hat)
            {
                if (x == 0 && y == 0) { pixels[at] = 255; pixels[at + 2] = 255; pixels[at + 3] = 255; }
                else if (x == 7 && y == 7) { pixels[at] = 255; pixels[at + 1] = 255; pixels[at + 3] = 255; }
            }
            else
            {
                byte value = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value; pixels[at + 3] = 255;
            }
        }
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, pixels, 8 * 4);
        image.Freeze(); return image;
    }
    private static byte[] AvatarPixels(Image image, double scale)
    {
        int size = (int)(32 * scale);
        var bitmap = new RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(image);
        var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0); return pixels;
    }
    private static uint AvatarColor(byte[] pixels, int at) => (uint)pixels[at] | (uint)pixels[at + 1] << 8 | (uint)pixels[at + 2] << 16 | (uint)pixels[at + 3] << 24;

    private static void BrandVector(Window window, GlassLauncherView view, ImageSource icon)
    {
        Image logo = Part<Image>(view, "AppLogoImage");
        Native.Check(logo.Source is DrawingImage, "The new application logo did not load its supplied vector resource.");
        double Brightness()
        {
            byte[] pixels = AvatarPixels(logo, 1);
            long alpha = 0, color = 0;
            for (int at = 0; at < pixels.Length; at += 4) {
                alpha += pixels[at + 3];
                color += pixels[at] + pixels[at + 1] + pixels[at + 2];
            }
            Native.Check(alpha > 3000, "The new vector logo is empty or has no resolved foreground.");
            return color * 255d / (3 * alpha);
        }
        view.SetState(State(icon)); Flush(window);
        double darkInk = Brightness();
        view.SetState(State(icon, useDarkText: false)); Flush(window);
        double lightInk = Brightness();
        Native.Check(darkInk < 100 && lightInk > 180, "The monochrome application logo did not follow light/dark theme foreground colors.");
        view.SetState(State(icon)); Flush(window);
    }

    private static void WorkbenchFrame(GlassLauncherView view)
    {
        Rect frame = Bounds(Part<FrameworkElement>(view, "MainSurface"), view);
        var regions = view.GetSurfaceRegions();
        Rect rail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
        Rect secondary = Bounds(Part<FrameworkElement>(view, "SecondarySurface"), view);
        Rect header = Bounds(Part<FrameworkElement>(view, "Header"), view);
        Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
        Native.Check(regions.Count == 2 && Same(regions[0].Bounds, rail) && Same(regions[1].Bounds, body) &&
            regions[0].CornerRadius == 12 && regions[1].CornerRadius == 12,
            "Painted surfaces and window shapes must share the exact rail/body bounds and rounded corners.");
        Rect logo = Bounds(Part<FrameworkElement>(view, "AppLogoImage"), view);
        Rect account = Bounds(Part<FrameworkElement>(view, "AccountButton"), view);
        Rect wallpaperAction = Bounds(Part<FrameworkElement>(view, "WallpaperButton"), view);
        Native.Check(frame.Contains(rail) && frame.Contains(secondary) && frame.Contains(header) &&
            Math.Abs(rail.Width - 64) < 1 && rail.Left < 2 && secondary.Left > rail.Right && header.Left >= secondary.Right,
            "The 64px icon rail, vertical secondary pane and right content are not separate inside the single frame.");
        Native.Check(Math.Abs(body.Left - rail.Right - 12) < .2 && frame.Contains(body) && logo.Width == 32 && logo.Top < 64 &&
            account.Bottom > frame.Bottom - 16 && account.Top > frame.Bottom - 64 &&
            body.Contains(wallpaperAction) && wallpaperAction.Right > body.Right - 24 && wallpaperAction.Bottom > body.Bottom - 24,
            "The 12px transparent gap, top app logo, bottom login avatar or body-bottom-right wallpaper entry is misplaced.");
        DpiScale dpi = VisualTreeHelper.GetDpi(view);
        double[] avatarInsets = { (account.Left - rail.Left) * dpi.DpiScaleX, (rail.Right - account.Right) * dpi.DpiScaleX, (rail.Bottom - account.Bottom) * dpi.DpiScaleY };
        double[] logoInsets = { (logo.Left - rail.Left) * dpi.DpiScaleX, (rail.Right - logo.Right) * dpi.DpiScaleX, (logo.Top - rail.Top) * dpi.DpiScaleY };
        Native.Check(avatarInsets.Max() - avatarInsets.Min() <= 1.01 && logoInsets.Max() - logoInsets.Min() <= 1.01,
            "Rail avatar/logo outer padding differs by more than one device pixel: avatar=" + string.Join(",", avatarInsets) + "; logo=" + string.Join(",", logoInsets));
        foreach (string name in new[] { "NavigationHeaderEndcap", "AccountSurface" }) {
            Rect endcap = Bounds(Part<FrameworkElement>(view, name), view);
            Native.Check(Math.Abs(endcap.Width * dpi.DpiScaleX - endcap.Height * dpi.DpiScaleY) <= 1.01,
                "Rail endcap must stay square after layout/DPI rounding: " + name);
        }
        double last = 0;
        foreach (string id in new[] { "NavLaunch", "NavDownload", "NavFavorites", "NavSettings", "NavOther" })
        {
            Rect target = Bounds(Part<Button>(view, id), view);
            Native.Check(rail.Contains(target) && target.Top >= last, "Primary icons are not ordered vertically: " + id);
            last = target.Bottom;
        }
        var panel = (Border)Part<FrameworkElement>(view, "PrimaryCard");
        Native.Check(panel.BorderThickness == new Thickness(0) && panel.Background is SolidColorBrush transparent && transparent.Color.A == 0 &&
            ((SolidColorBrush)view.Resources["GlassWindowBrush"]).Color.A == 255,
            "The launch composition regained a heavy enclosing card or lost its opaque application base.");
    }

    private static void Layout(Window window, GlassLauncherView view, int width, int height)
    {
        view.Width = width; view.Height = height; window.Width = width; window.Height = height;
        VisualAssetChecks.Pump(() => Math.Abs(view.ActualWidth - width) < 1 && Math.Abs(view.ActualHeight - height) < 1, "Reference-shell viewport");
        Flush(window); WorkbenchFrame(view);
        Rect frame = Bounds(Part<FrameworkElement>(view, "MainSurface"), view);
        Rect pane = Bounds(Part<FrameworkElement>(view, "SecondarySurface"), view);
        Rect profile = Bounds(Part<FrameworkElement>(view, "PrimaryCard"), view);
        Rect icon = Bounds(Part<FrameworkElement>(view, "InstanceImage"), view);
        Rect title = Bounds(Part<FrameworkElement>(view, "InstanceNameText"), view);
        Rect launch = Bounds(Part<FrameworkElement>(view, "LaunchButton"), view);
        Rect choose = Bounds(Button(view, "选择实例"), view);
        Rect manage = Bounds(Part<Button>(view, "InstanceSettingsButton"), view);
        Native.Check(pane.Contains(profile) && pane.Contains(launch) && pane.Contains(icon) &&
            icon.Width == 64 && icon.Height == 64 && icon.Bottom <= title.Top && title.Bottom <= launch.Top &&
            launch.Bottom <= choose.Top && Math.Abs(launch.Width - (width < 960 ? 180 : 190)) < 1 &&
            Math.Abs(launch.Height - 48) < 1,
            "The game icon/name/start hierarchy is not inside the left home pane at " + width);
        Native.Check(pane.Contains(choose) && pane.Contains(manage) && choose.Right < manage.Left &&
            pane.Contains(Bounds(Part<Button>(view, "ExpandButton"), view)) &&
            Bounds(Part<Button>(view, "ExpandButton"), view).Bottom < icon.Top,
            "The home header plus/footer selection-management actions do not match their pane positions.");
        Native.Check(!Descendants(Part<FrameworkElement>(view, "HomePanel")).OfType<Button>().Any(button => button.IsVisible && button.IsHitTestVisible),
            "The default right home area retained active central launch/account controls.");
        foreach (string id in new[] { "LaunchButton", "ExpandButton", "InstanceSettingsButton", "WallpaperButton", "AccountButton", "NavLaunch", "NavDownload", "NavFavorites", "NavSettings", "NavOther" }) Target(Part<Button>(view, id), width);
        foreach (string name in new[] { "选择实例", "最小化窗口", "关闭窗口" }) Target(Button(view, name), width);
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").IsHitTestVisible, "Startup options");
        Rect options = Bounds(Part<FrameworkElement>(view, "AuxiliaryCards"), view);
        Native.Check(frame.Contains(options) && options.Left >= pane.Right && options.Top < 100,
            "Header plus did not reveal options near the top immediately right of the home pane.");
        NoOverlap(launch, options, "Startup options cover the primary launch action.");
        NoOverlap(Bounds(Part<FrameworkElement>(view, "DependencyCard"), view), Bounds(Part<FrameworkElement>(view, "RecentWorldCard"), view), "Startup cards overlap.");
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").Visibility == Visibility.Hidden, "Close startup options");
        InternalWallpaperAndContext(window, view, width);
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

    private static void InternalWallpaperAndContext(Window window, GlassLauncherView view, int width)
    {
        view.SetWallpaper(null!);
        Flush(window);
        var opaque = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        opaque.Render(view);
        byte[] pixel = new byte[4];
        opaque.CopyPixels(new Int32Rect((int)view.ActualWidth / 2, (int)view.ActualHeight / 2, 1, 1), pixel, 4, 0);
        Native.Check(pixel[3] == 255, "The wallpaper-free application exposed the Windows desktop at " + width);
        CheckSurfacePixels(view, opaque, "solid background");
        ImageSource wallpaper = Image();
        view.SetWallpaper(wallpaper);
        Flush(window);
        var image = Part<Image>(view, "WallpaperImage");
        var clip = view.SurfaceClip as GeometryGroup;
        var regions = view.GetSurfaceRegions();
        Native.Check(ReferenceEquals(image.Source, wallpaper) && image.IsVisible && !image.IsHitTestVisible &&
            clip != null && clip.Children.Count == 2 && clip.Children.Cast<RectangleGeometry>().Select((shape, index) =>
                Same(shape.Rect, regions[index].Bounds) && shape.RadiusX == regions[index].CornerRadius && shape.RadiusY == regions[index].CornerRadius).All(value => value) &&
            image.Clip is null && Part<Border>(view, "MainSurface").Clip is null &&
            ReferenceEquals(Part<Border>(view, "MainSurface").OpacityMask, view.SurfaceOpacityMask),
            "The custom wallpaper clip must match both rounded surfaces and preserve their transparent gap.");
        var wallpaperPixels = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        wallpaperPixels.Render(view);
        CheckSurfacePixels(view, wallpaperPixels, "launcher wallpaper");
        view.SetState(State(wallpaper, home: false));
        view.SetSidebar(new[] { new GlassSidebarItem { Label = "上下文选项", IsSelected = true } });
        Flush(window);
        Rect row = Bounds(Part<FrameworkElement>(view, "SideRail"), view);
        Rect header = Bounds(Part<FrameworkElement>(view, "Header"), view);
        Rect frame = Bounds(Part<FrameworkElement>(view, "MainSurface"), view);
        Rect pane = Bounds(Part<FrameworkElement>(view, "SecondarySurface"), view);
        Native.Check(pane.Contains(row) && row.Height > row.Width && row.Left < header.Left,
            "Context navigation is not the shared vertical secondary pane at " + width);
        Native.Check(view.GetSurfaceRegions().Count == 2, "Context navigation changed the two workspace window shapes.");
        view.SetSidebar(Array.Empty<GlassSidebarItem>());
        view.SetState(State(wallpaper));
        Flush(window);
    }

    internal static void CheckSurfacePixels(GlassLauncherView view, RenderTargetBitmap bitmap, string mode)
    {
        Rect rail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
        Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
        byte Alpha(Point point) {
            byte[] value = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), value, 4, 0);
            return value[3];
        }
        Point gap = new((rail.Right + body.Left) / 2, view.ActualHeight / 2);
        Native.Check(Alpha(gap) == 0 && !view.SurfaceClip.FillContains(gap), "The workspace gap is still painted or hit-shaped in " + mode);
        for (int y = 0; y < 12; y++)
        for (int x = 0; x < 12; x++)
            Native.Check(Alpha(new Point(rail.Left + x, rail.Top + y)) == Alpha(new Point(body.Left + x, body.Top + y)),
                "Equal outer radii have different antialias coverage in " + mode);
        foreach (Rect surface in new[] { rail, body }) {
            Native.Check(Alpha(new Point(surface.Left + surface.Width / 2, surface.Top + surface.Height / 2)) == 255,
                "A workspace surface lost its opaque launcher-owned base in " + mode);
            foreach (Point corner in new[] { surface.TopLeft, new Point(surface.Right - 1, surface.Top), new Point(surface.Left, surface.Bottom - 1), new Point(surface.Right - 1, surface.Bottom - 1) })
                Native.Check(Alpha(corner) == 0, "A rounded surface retained a painted square corner in " + mode + ": " + corner);
            foreach (var (left, top) in new[] { (true, true), (false, true), (true, false), (false, false) }) {
                int partial = 0;
                for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++) {
                    Point point = new(left ? surface.Left + x : surface.Right - 1 - x, top ? surface.Top + y : surface.Bottom - 1 - y);
                    if (Alpha(point) is > 0 and < 255) partial++;
                }
                Native.Check(partial >= 4, "A workspace corner lost its antialiased alpha coverage in " + mode);
            }
        }
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
        view.SetState(State(image, home: false));
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
        Native.Check(view.GetSurfaceRegions().Count == 2 && Bounds(Part<FrameworkElement>(view, "BodyFrame"), view).Contains(Bounds(navigationPopover, view)),
            "The contextual panel escaped the application frame or recreated a native backdrop island.");
        Escape(navigationContent);
        Native.Check(navigationPopover.Visibility == Visibility.Collapsed && ReferenceEquals(FocusManager.GetFocusedElement(window), menu),
            "Escape did not dismiss context navigation and restore logical focus to its trigger.");
        view.SetAccountOpen(true); view.SetNavigationOpen(true);
        Native.Check(!accountPopover.IsVisible && navigationPopover.IsVisible, "Opening context navigation left both glass portals visible.");
        view.SetAccountOpen(true);
        Native.Check(accountPopover.IsVisible && !navigationPopover.IsVisible, "Opening the account left both glass portals visible.");
        view.SetAccountOpen(false);
        view.SetAccountContent(null!); view.SetNavigationContent(null!); view.SetSidebar(Array.Empty<GlassSidebarItem>());
        view.SetState(State(image));
        Flush(window);
        WorkbenchFrame(view);
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

    private static double Radius(GlassLauncherView view, string key) => ((CornerRadius)view.Resources[key]).TopLeft;

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
