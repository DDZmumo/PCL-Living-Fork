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
using System.Runtime.InteropServices;
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
            LeftwardRailExpansion(window, view, icon);
            RailMaterialIsolation(window, view, icon);
            DesktopMaterial(window, view, icon);
            ExtendedWallpaperSample(window);
            VideoFrameContinuity(window);
            BrandVector(window, view, icon);
            AvatarPixelGrid(window, view, icon);
            Layout(window, view, 1120, 720);
            CheckboxSnapshots(view, icon);
            Layout(window, view, 810, 470);
            ContextTitlesAndCaption(window, view, icon);
            Events(view, icon);
            Expansion(view, icon);
            Portals(window, view, icon);
            ContentHost(view, icon);
            GlassMotionChecks.Run(window, view, icon);
            StartupSplash(window, view, icon);
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

    private static void VideoFrameContinuity(Window window)
    {
        var preferences = typeof(GlassSurface).Assembly.GetType("PCLLF.Wpf.Glass.GlassSystemPreferences", throwOnError: true)!;
        if (SystemParameters.HighContrast || !(bool)Native.Property(preferences, "TransparencyEnabled").GetValue(null)!) return;
        var viewport = (Canvas)window.Content;
        var drawing = new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 160, 90)));
        var host = new Image { Source = new DrawingImage(drawing), Width = 400, Height = 300, Stretch = Stretch.UniformToFill };
        BitmapSource? presented = null;
        int publications = 0;
        using var sampler = new VideoWallpaperSampler(window.Dispatcher, source => {
            if (source is BitmapSource frame) { presented = frame; publications++; }
        });
        viewport.Children.Add(host);
        try {
            sampler.SetHost(host);
            sampler.SetPaused(false);
            byte[] pixel = new byte[4];
            bool PixelMatches(byte blue, byte red) {
                if (presented is not { } frame) return false;
                frame.CopyPixels(new Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, 1, 1), pixel, 4, 0);
                return pixel[0] == blue && pixel[2] == red && pixel[3] == 255;
            }
            VisualAssetChecks.Pump(() => PixelMatches(0, 255), "first video wallpaper sample");
            drawing.Brush = Brushes.Transparent;
            host.Width += 15;
            VisualAssetChecks.Observe(160);
            Native.Check(PixelMatches(0, 255), "An empty video readback replaced the last good frame.");
            drawing.Brush = Brushes.Blue;
            VisualAssetChecks.Pump(() => PixelMatches(255, 0), "video wallpaper recovery after an empty sample");
            Native.Check(publications == 1 && presented!.PixelWidth == 160 && presented.PixelHeight == 90,
                "A viewport resize recreated or recropped the shared video frame.");
        } finally { viewport.Children.Remove(host); }
    }

    private static void StartupSplash(Window window, GlassLauncherView view, ImageSource icon)
    {
        if (!PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation) return;
        view.SetState(State(icon, reduceMotion: false));
        view.SetWallpaper(null!);
        Flush(window);

        Type preload = Native.Type("PCL.GlassStartupPreload");
        var slot = Native.Field(preload, "_interfaceShown");
        object? previous = slot.GetValue(null);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.SetValue(null, ready);
        EventHandler shown = (_, _) => Native.Call(preload, "MarkInterfaceShown", null);
        view.SplashFinished += shown;
        Task<bool> prompt = Task.Run(() => (bool)Native.Call(preload, "WaitForInterface", null)!);
        try
        {
            view.BeginSplash();
            Native.Check(view.IsSplashing && Part<FrameworkElement>(view, "ApplicationBackdrop").Opacity == 0 &&
                Part<FrameworkElement>(view, "WorkspaceContentLayer").Opacity == 0,
                "The workspace covered the startup splash's backdrop.");
            Native.Check(!prompt.Wait(100) && !(bool)Native.Call(preload, "WaitForInterface", null)!,
                "An automatic prompt passed the startup gate while the loading page was visible.");
            view.SplashProgress = 1;
            view.CompleteSplash();
            VisualAssetChecks.Pump(() => !view.IsSplashing, "complete startup splash", 12000);
            Native.Check(prompt.Wait(2000) && prompt.Result && Part<FrameworkElement>(view, "SplashLayer").Visibility == Visibility.Collapsed &&
                Part<FrameworkElement>(view, "ApplicationBackdrop").Opacity == 1 && Part<FrameworkElement>(view, "WorkspaceContentLayer").Opacity == 1,
                "Startup did not restore the workspace before releasing its automatic prompts.");
        }
        finally
        {
            ready.TrySetResult(false);
            view.SplashFinished -= shown;
            slot.SetValue(null, previous);
        }
    }

    private static GlassLauncherViewState State(ImageSource image, bool reduceMotion = true, bool dependency = true,
        bool autoFill = true, bool launching = false, bool options = true, bool home = true, bool back = false, bool useDarkText = true,
        int? navigation = null, bool resourceList = false, string? pageTitle = null) => new()
    {
        IsHome = home,
        SelectedNavigationId = navigation ?? (home ? 0 : 3),
        CanGoBack = back,
        PageTitle = pageTitle ?? (back ? "实例设置" : string.Empty),
        IsResourceList = resourceList,
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
        Rect controls = Bounds(Part<FrameworkElement>(view, "WindowControlsSurface"), view);
        Native.Check(regions.Count == 3 && Same(regions[0].Bounds, rail) && Same(regions[1].Bounds, body) && Same(regions[2].Bounds, controls) &&
            regions[0].CornerRadius == 12 && regions[1].CornerRadius == 12 && regions[2].CornerRadius == 12,
            "Painted surfaces and window shapes must share the exact rail/body/controls bounds and rounded corners.");
        Rect logo = Bounds(Part<FrameworkElement>(view, "AppLogoImage"), view);
        Rect account = Bounds(Part<FrameworkElement>(view, "AccountButton"), view);
        DpiScale dpi = VisualTreeHelper.GetDpi(view);
        Native.Check(Contains(frame, rail) && Contains(frame, secondary) && Contains(frame, header) &&
            Math.Abs(rail.Width - 64) < 1 && rail.Left < 2 && secondary.Left > rail.Right && header.Left >= secondary.Right - .05,
            $"The 64px icon rail, vertical secondary pane and right content are not separate inside the single frame. frame={frame}; rail={rail}; secondary={secondary}; header={header}.");
        Native.Check(Math.Abs(body.Left - rail.Right - 12) < .2 && Math.Abs(rail.Top - body.Top) < .2 &&
            Math.Abs((rail.Top - 10) * dpi.DpiScaleY) <= 1.01 && Math.Abs((view.ActualWidth - body.Right - 10) * dpi.DpiScaleX) <= 1.01 &&
            Contains(frame, body) && Contains(frame, controls) && controls.Top < 1 &&
            Math.Abs(controls.Right - view.ActualWidth) < .2 && !Rect.Intersect(controls, body).IsEmpty &&
            logo.Width == 32 && logo.Top < 64 &&
            account.Bottom > frame.Bottom - 16 && account.Top > frame.Bottom - 64 &&
            view.FindName("WallpaperButton") is null,
            $"The 12px transparent gap, 10px staggered controls capsule, top app logo or bottom login avatar is misplaced, or a floating wallpaper entry returned (it belongs to settings). frame={frame}; rail={rail}; body={body}; controls={controls}; logo={logo}; account={account}.");
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
        double railCenterX = rail.Left + rail.Width / 2;
        foreach (string id in new[] { "NavLaunch", "NavDownload", "NavFavorites", "NavSettings", "NavOther" })
        {
            Button navButton = Part<Button>(view, id);
            Rect target = Bounds(navButton, view);
            Native.Check(Contains(rail, target) && target.Top >= last - .05, "Primary icons are not ordered vertically: " + id);
            var glyph = Descendants(navButton).OfType<System.Windows.Shapes.Path>().Single();
            Rect glyphBounds = glyph.TransformToAncestor(view).TransformBounds(glyph.RenderedGeometry.Bounds);
            double glyphCenterX = glyphBounds.Left + glyphBounds.Width / 2;
            double glyphCenterY = glyphBounds.Top + glyphBounds.Height / 2;
            double buttonCenterY = target.Top + target.Height / 2;
            Native.Check(Math.Abs(glyphCenterX - railCenterX) <= 1.6 && Math.Abs(glyphCenterY - buttonCenterY) <= 1.6,
                $"Collapsed rail icon glyph is off-center for {id}: glyphCenter=({glyphCenterX:0.##},{glyphCenterY:0.##}), expected=({railCenterX:0.##},{buttonCenterY:0.##}).");
            last = target.Bottom;
        }
        Button railToggle = Part<Button>(view, "RailToggleButton");
        Rect toggleTarget = Bounds(railToggle, view);
        var toggleGlyph = Descendants(railToggle).OfType<System.Windows.Shapes.Path>().Single();
        Rect toggleGlyphBounds = toggleGlyph.TransformToAncestor(view).TransformBounds(toggleGlyph.RenderedGeometry.Bounds);
        Native.Check(Math.Abs((toggleGlyphBounds.Left + toggleGlyphBounds.Width / 2) - railCenterX) <= 1.6 &&
            Math.Abs((toggleGlyphBounds.Top + toggleGlyphBounds.Height / 2) - (toggleTarget.Top + toggleTarget.Height / 2)) <= 1.6,
            "Collapsed rail toggle chevron is off-center in the 64-DIP rail.");
        var panel = (Border)Part<FrameworkElement>(view, "PrimaryCard");
        Native.Check(panel.BorderThickness == new Thickness(0) && panel.Background is SolidColorBrush transparent && transparent.Color.A == 0 &&
            ((SolidColorBrush)view.Resources["GlassWindowBrush"]).Color.A == 255,
            "The launch composition regained a heavy enclosing card or lost its opaque application base.");
        CheckChromeTargets(view);
    }

    private static void LeftwardRailExpansion(Window window, GlassLauncherView view, ImageSource icon)
    {
        double allowance = GlassLauncherView.RailExpandDelta + GlassLauncherView.RailOvershootHeadroom;
        Button toggle = Part<Button>(view, "RailToggleButton");
        try
        {
            view.Width = 1120 + allowance; window.Width = 1120 + allowance;
            view.SetLeftExpansionAllowance(allowance);
            VisualAssetChecks.Pump(() => Math.Abs(view.ActualWidth - (1120 + allowance)) < 1, "Leftward rail allowance viewport");
            Flush(window);
            Target(toggle, 1120);
            NoOverlap(Bounds(Part<Button>(view, "NavOther"), view), Bounds(toggle, view), "Rail toggle overlaps the last navigation button.");
            NoOverlap(Bounds(toggle, view), Bounds(Part<Button>(view, "AccountButton"), view), "Rail toggle overlaps the account avatar button.");
            Rect collapsedRail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
            Rect collapsedBody = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
            Native.Check(!view.IsRailExpanded && Math.Abs(collapsedRail.Width - GlassLauncherView.RailCollapsedWidth) < .2 &&
                Part<TextBlock>(view, "NavLaunchText").Visibility == Visibility.Collapsed &&
                ToolTipService.GetIsEnabled(Part<Button>(view, "NavSettings")),
                "Collapsed rail already showed labels, disabled icon tooltips, or deviated from 64 DIP.");
            Click(toggle);
            Flush(window);
            Rect expandedRail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
            Rect expandedBody = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
            Native.Check(view.IsRailExpanded &&
                !ToolTipService.GetIsEnabled(Part<Button>(view, "NavSettings")) &&
                Math.Abs(expandedBody.Left - collapsedBody.Left) < .1 &&
                Math.Abs(expandedBody.Width - collapsedBody.Width) < .1 &&
                Math.Abs(expandedRail.Right - collapsedRail.Right) < .1 &&
                Math.Abs(collapsedRail.Left - expandedRail.Left - GlassLauncherView.RailExpandDelta) < .2 &&
                Math.Abs(expandedRail.Width - GlassLauncherView.RailExpandedWidth) < .2,
                $"Rail did not expand strictly leftward while keeping BodyFrame fixed. collapsedRail={collapsedRail}; expandedRail={expandedRail}; collapsedBody={collapsedBody}; expandedBody={expandedBody}.");
            Native.Check(Part<TextBlock>(view, "RailBrandText").Text == "PCL-LF", "Expanded rail brand wordmark did not display PCL-LF.");
            foreach (string labelName in new[] { "RailBrandText", "NavLaunchText", "NavDownloadText", "NavFavoritesText", "NavSettingsText", "NavOtherText", "RailToggleText", "AccountNameText" })
            {
                var label = Part<TextBlock>(view, labelName);
                Native.Check(label.Visibility == Visibility.Visible && label.Opacity > .99 && Contains(expandedRail, Bounds(label, view)),
                    "Expanded rail label missing or outside rail bounds: " + labelName);
            }
            Click(toggle);
            Flush(window);
            Rect retractedRail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
            Native.Check(!view.IsRailExpanded && Same(retractedRail, collapsedRail), "Retracting rail did not return to exact 64 DIP bounds.");
            if (PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation)
            {
                view.SetState(State(icon, reduceMotion: false, home: false, navigation: 3));
                Native.Check(Part<FrameworkElement>(view, "SecondarySurface").CacheMode is null,
                    "SecondarySurface attached a BitmapCache during page flip, which freezes shifted wallpaper coordinates.");
                view.SetState(State(icon, reduceMotion: false));
                Native.Check(Part<FrameworkElement>(view, "SecondarySurface").CacheMode is null,
                    "SecondarySurface attached a BitmapCache when flipping back to Home.");
                Click(toggle);
                VisualAssetChecks.Pump(() => view.IsRailAnimating && Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view).Width > 80, "Rail iOS spring expansion start");
                Click(toggle);
                VisualAssetChecks.Pump(() => !view.IsRailAnimating && !view.IsRailExpanded, "Rail iOS spring mid-flight reversal settle");
                Flush(window);
                Native.Check(Same(Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view), collapsedRail),
                    "Mid-flight reversed iOS spring did not settle back on collapsed rail bounds.");
            }
        }
        finally
        {
            view.SetState(State(icon));
            view.SetRailExpanded(false, true);
            view.SetLeftExpansionAllowance(0);
            view.Width = 1120; window.Width = 1120;
            VisualAssetChecks.Pump(() => Math.Abs(view.ActualWidth - 1120) < 1, "Restore 1120 viewport");
            Flush(window);
        }
    }

    internal static void CheckChromeTargets(GlassLauncherView view, bool includeWindowOverlays = false)
    {
        Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
        Rect controls = Bounds(Part<FrameworkElement>(view, "WindowControlsSurface"), view);
        Button minimize = Part<Button>(view, "MinimizeButton"), close = Part<Button>(view, "CloseButton");
        Rect minimum = Bounds(minimize, view), closing = Bounds(close, view);
        DpiScale dpi = VisualTreeHelper.GetDpi(view);
        double leftInset = (minimum.Left - controls.Left) * dpi.DpiScaleX;
        double topInset = (closing.Top - controls.Top) * dpi.DpiScaleY;
        double rightInset = (controls.Right - closing.Right) * dpi.DpiScaleX;
        double bottomInset = (controls.Bottom - closing.Bottom) * dpi.DpiScaleY;
        Native.Check(Contains(controls, minimum) && Contains(controls, closing) &&
            closing.Top < body.Top && closing.Right > body.Right && minimum.Left < body.Right && closing.Bottom > body.Top &&
            Math.Abs(leftInset - 6 * dpi.DpiScaleX) <= 1.01 &&
            Math.Abs(rightInset - 6 * dpi.DpiScaleX) <= 1.01 &&
            Math.Abs(topInset - 6 * dpi.DpiScaleY) <= 1.01 &&
            Math.Abs(bottomInset - 6 * dpi.DpiScaleY) <= 1.01 &&
            Math.Abs((minimum.Top - closing.Top) * dpi.DpiScaleY) <= 1.01 &&
            Math.Abs((closing.Left - minimum.Right - 4) * dpi.DpiScaleX) <= 1.01,
            $"Staggered caption padding/spacing differs after layout rounding at DPI {dpi.DpiScaleX}/{dpi.DpiScaleY}: left={leftInset}, top={topInset}, right={rightInset}, bottom={bottomInset}.");
        NoOverlap(minimum, closing, "Minimize and close hit rectangles overlap.");
        var title = Part<TextBlock>(view, "PageTitleText");
        if (!string.IsNullOrEmpty(title.Text))
        {
            NoOverlap(Bounds(title, view), minimum, "A page title extends underneath the minimize action.");
            NoOverlap(Bounds(title, view), closing, "A page title extends underneath the close action.");
        }
        foreach (Button button in new[] { minimize, close })
        {
            Target(button, (int)view.ActualWidth);
            Rect bounds = Bounds(button, view);
            foreach (Point point in new[] { new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
                new Point(bounds.Left + 8, bounds.Top + 8), new Point(bounds.Right - 8, bounds.Bottom - 8) })
            {
                IInputElement? hit = includeWindowOverlays
                    ? Window.GetWindow(view).InputHitTest(view.TranslatePoint(point, Window.GetWindow(view)))
                    : view.InputHitTest(point);
                Native.Check(hit is DependencyObject element && IsWithin(element, button),
                    "An overlay or title intercepts the actual caption target: " + button.Name + " at " + point);
            }
        }
    }

    private static void ContextTitlesAndCaption(Window window, GlassLauncherView view, ImageSource image)
    {
        foreach (var (page, title, resource) in new[] { (7, "实例管理", false), (5, "选择实例", false), (1, "资源中心", true), (3, "设置", false) })
        {
            view.SetState(State(image, home: false, back: true, navigation: page, resourceList: resource,
                pageTitle: "用于检查截断和标题按钮边界的较长页面标题 · 版本设置 - Fabulously Optimized"));
            view.SetSidebar(new[] { new GlassSidebarItem { Label = "上下文检查", IsSelected = true } });
            Flush(window);
            Native.Check(Part<TextBlock>(view, "SecondaryTitleText").Text == title,
                "A contextual page was labeled as home: page=" + page);
            Native.Check(Part<RowDefinition>(view, "PageHeaderRow").ActualHeight == 88 &&
                Part<FrameworkElement>(view, "PageHeading").Margin.Top == 40,
                "Non-home page headings lost their shared 88-DIP header and 40-DIP top inset.");
            NoOverlap(Bounds(Part<TextBlock>(view, "SecondaryTitleText"), view), Bounds(Part<FrameworkElement>(view, "SideRail"), view),
                "The secondary heading overlaps the contextual click rows.");
            CheckChromeTargets(view);
        }
        view.SetSidebar(Array.Empty<GlassSidebarItem>());
        view.SetState(State(image));
        var workspaceType = Native.Type("PCL.GlassAccountWorkspace");
        var constructor = workspaceType.GetConstructor(Native.Flags, null, [Native.Type("PCL.PageLaunchLeft"), typeof(GlassLauncherView)], null)!;
        using var workspace = (IDisposable)constructor.Invoke([null, view]);
        var scene = (Grid)Native.Property(workspaceType, "Scene").GetValue(workspace)!;
        var side = (FrameworkElement)Native.Property(workspaceType, "Side").GetValue(workspace)!;
        // Scene hosts the account page first and the skin library page after it.
        var titleGroup = (Grid)((Grid)scene.Children[0]).Children[0];
        var frost = (GlassSurface)titleGroup.Children[0];
        var accountTitle = (TextBlock)titleGroup.Children[1];
        view.SetAccountWorkspace(side, scene);
        try {
            view.SetAccountWorkspaceOpen(true, immediate: true);
            Flush(window);
            var pageFrost = Part<GlassSurface>(view, "PageTitleBacking");
            Native.Check(accountTitle.Text == "切换账号" && frost.IsVisible && ReferenceEquals(frost.Style, pageFrost.Style) &&
                ReferenceEquals(frost.WallpaperSource, view.FindName("WallpaperImage") is Image wallpaper ? wallpaper.Source : null) &&
                frost.WallpaperSource is not null && Contains(Bounds(frost, view), Bounds(accountTitle, view)),
                "The account-switch heading lost the shared frosted material directly behind its text.");
            var deck = (Grid)Native.Get(workspace, "_deck")!;
            var cards = (List<FrameworkElement>)Native.Get(workspace, "_cards")!;
            var front = (GlassSurface)Native.Call(workspaceType, "DeckCard", workspace, new TextBlock { Text = "当前账号" }, "当前账号")!;
            var back = (GlassSurface)Native.Call(workspaceType, "DeckCard", workspace, new TextBlock { Text = "添加正版账号" }, "添加正版账号")!;
            cards.Add(front); deck.Children.Add(front);
            cards.Add(back); deck.Children.Add(back);
            Native.Call(workspaceType, "LayoutDeck", workspace);
            view.SetWallpaper(null!); view.SetDesktopBackdropActive(true);
            Flush(window);
            Native.Check(front.Child is Grid { Effect: null } && back.Child is Grid { Effect: System.Windows.Media.Effects.BlurEffect { Radius: > 0 } },
                "Desktop backdrop left the rear account label sharp through the front card or blurred the active card.");
            view.SetWallpaper(image); view.SetDesktopBackdropActive(false);
            Flush(window);
            Native.Check(back.Child is Grid { Effect: null }, "Wallpaper mode unnecessarily blurred the account-card text.");
        } finally {
            view.SetAccountWorkspace(null!, null!);
            Flush(window);
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
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
        Rect recentWorld = Bounds(Part<FrameworkElement>(view, "RecentWorldCard"), view);
        Rect choose = Bounds(Button(view, "选择实例"), view);
        Rect manage = Bounds(Part<Button>(view, "InstanceSettingsButton"), view);
        Native.Check(Contains(pane, profile) && Contains(pane, launch) && Contains(pane, icon) && Contains(pane, recentWorld) &&
            recentWorld.Left >= pane.Left + 11.5 && recentWorld.Right <= pane.Right - 11.5 &&
            icon.Width == 64 && icon.Height == 64 && icon.Bottom <= title.Top + .05 && title.Bottom <= launch.Top + .05 &&
            launch.Bottom <= choose.Top + .05 && Math.Abs(launch.Width - (width < 960 ? 180 : 190)) < 1 &&
            Math.Abs(launch.Height - 48) < 1,
            $"The game icon/name/start/recent-world hierarchy overflowed or left the home pane at {width}: pane={pane}, recentWorld={recentWorld}.");
        Native.Check(Contains(pane, choose) && Contains(pane, manage) && choose.Right < manage.Left &&
            Contains(pane, Bounds(Part<Button>(view, "ExpandButton"), view)) &&
            Bounds(Part<Button>(view, "ExpandButton"), view).Bottom < icon.Top,
            "The home header plus/footer selection-management actions do not match their pane positions.");
        Native.Check(!Descendants(Part<FrameworkElement>(view, "HomePanel")).OfType<Button>().Any(button => button.IsVisible && button.IsHitTestVisible),
            "The default right home area retained active central launch/account controls.");
        foreach (string id in new[] { "LaunchButton", "ExpandButton", "InstanceSettingsButton", "AccountButton", "NavLaunch", "NavDownload", "NavFavorites", "NavSettings", "NavOther" }) Target(Part<Button>(view, id), width);
        foreach (string name in new[] { "选择实例", "最小化窗口", "关闭窗口" }) Target(Button(view, name), width);
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").IsHitTestVisible, "Startup options");
        Rect options = Bounds(Part<FrameworkElement>(view, "AuxiliaryCards"), view);
        Native.Check(Contains(frame, options) && options.Left >= pane.Left - 1 && options.Right <= pane.Right + 1 && options.Top < 140,
            "Header plus did not reveal options near the top inside the home pane: " + options + " pane " + pane + " frame " + frame);
        NoOverlap(launch, options, "Startup options cover the primary launch action.");
        NoOverlap(Bounds(Part<FrameworkElement>(view, "DependencyCard"), view), Bounds(Part<FrameworkElement>(view, "RecentWorldCard"), view), "Startup cards overlap.");
        Click(Part<Button>(view, "ExpandButton"));
        VisualAssetChecks.Pump(() => Part<FrameworkElement>(view, "AuxiliaryCards").Visibility == Visibility.Hidden, "Close startup options");
        InternalWallpaperAndContext(window, view, width);
    }

    private static void RailMaterialIsolation(Window window, GlassLauncherView view, ImageSource icon)
    {
        double density = GlassMaterial.GetOpacityMultiplier(view);
        var failures = new List<string>();
        ImageSource Pattern(bool invert) {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open()) {
                context.DrawRectangle(invert ? Brushes.White : Brushes.Black, null, new Rect(0, 0, 560, 720));
                context.DrawRectangle(invert ? Brushes.Black : Brushes.White, null, new Rect(560, 0, 560, 720));
            }
            drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
        }
        byte[] Pixel(Point point) {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view); byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0); return pixel;
        }
        try {
            foreach (bool darkText in new[] { false, true }) {
                view.SetState(State(icon, useDarkText: darkText)); view.SetWallpaper(null!);
                GlassMaterial.SetOpacityMultiplier(view, .85); view.SetDesktopBackdropActive(true); Flush(window);
                Rect rail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
                Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
                Rect controls = Bounds(Part<FrameworkElement>(view, "WindowControlsSurface"), view);
                Point railPoint = new(rail.Left + rail.Width / 2, rail.Top + rail.Height * .65);
                Point bodyPoint = new(body.Right - 64, body.Bottom - 100);
                Point controlsOverhangPoint = new(controls.Right - 4, controls.Top + controls.Height / 2);
                Point bodyCornerOutsideArc = new(body.Right - 2, body.Top + 2);
                byte[] railPixel = Pixel(railPoint), bodyPixel = Pixel(bodyPoint), overhangPixel = Pixel(controlsOverhangPoint);
                Native.Check(view.GetDesktopBackdropRegions().Count == 3, "The default material no longer covers all three painted surfaces.");
                bool transparent = (bool)Native.Property(typeof(GlassSurface), "CanUseTransparency").GetValue(Part<GlassSurface>(view, "ApplicationBackdrop"))!;
                if (transparent && railPixel[3] >= bodyPixel[3] * .75)
                    failures.Add($"rail/main opacity no longer differs (darkText={darkText}, alpha={railPixel[3]}/{bodyPixel[3]})");
                if (transparent && overhangPixel[3] >= bodyPixel[3] * .75)
                    failures.Add($"controls overhang lost translucent desktop material (darkText={darkText}, alpha={overhangPixel[3]}/{bodyPixel[3]})");
                var wallpaper = Part<Image>(view, "WallpaperImage");
                view.SetWallpaper(Pattern(false)); wallpaper.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40 }; Flush(window);
                byte[] firstRail = Pixel(railPoint), firstBody = Pixel(bodyPoint), firstOverhang = Pixel(controlsOverhangPoint), firstCorner = Pixel(bodyCornerOutsideArc);
                var viewport = Part<Grid>(view, "WallpaperViewport");
                if (!viewport.ClipToBounds || !Contains(body, Bounds(viewport, view))) failures.Add("the wallpaper clip viewport extends outside the main panel");
                var backdropRegions = view.GetDesktopBackdropRegions();
                Native.Check(GlassMaterial.GetWallpaperSource(Part<GlassSurface>(view, "NavigationSurface")) is null &&
                    GlassMaterial.GetDesktopBackdropEnabled(Part<GlassSurface>(view, "NavigationSurface")) && !GlassMaterial.GetDesktopBackdropEnabled(Part<FrameworkElement>(view, "BodyFrame")) &&
                    backdropRegions.Count == 2 && Same(backdropRegions[0].Bounds, rail) && Same(backdropRegions[1].Bounds, controls),
                    "Wallpaper mode disabled rail/controls Acrylic or kept native sampling under the wallpaper panel.");
                view.SetWallpaper(Pattern(true)); Flush(window);
                byte[] secondRail = Pixel(railPoint), secondBody = Pixel(bodyPoint), secondOverhang = Pixel(controlsOverhangPoint), secondCorner = Pixel(bodyCornerOutsideArc);
                if (Enumerable.Range(0, 4).Any(channel => Math.Abs(firstRail[channel] - secondRail[channel]) > 3))
                    failures.Add("a wallpaper pattern/blur is visible through the detached rail");
                if (!SystemParameters.HighContrast && transparent &&
                    Enumerable.Range(0, 3).All(channel => Math.Abs(firstCorner[channel] - secondCorner[channel]) < 8))
                    failures.Add("window controls frost remained clipped underneath BodyFrame instead of blurring BodyFrame's top-right edge from above");
                Point unmaskedBodyCornerBelowControls = new(body.Right - 1, controls.Bottom + 14);
                byte[] belowControlsRightEdge = Pixel(new Point(body.Right + 2, controls.Bottom + 14));
                if (belowControlsRightEdge[3] != 0)
                    failures.Add("body wallpaper leaked into the staggered right inset below WindowControlsSurface");
                _ = unmaskedBodyCornerBelowControls;
                if (!SystemParameters.HighContrast && Enumerable.Range(0, 3).All(channel => Math.Abs(firstBody[channel] - secondBody[channel]) < 40))
                    failures.Add("the main panel lost its actual wallpaper while isolating the rail");
                if (!darkText && !SystemParameters.HighContrast && transparent) {
                    var uniformGreyDrawing = new DrawingGroup();
                    using (DrawingContext ctx = uniformGreyDrawing.Open())
                        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(197, 197, 197)), null, new Rect(0, 0, 1120, 720));
                    uniformGreyDrawing.Freeze();
                    var uniformGrey = new DrawingImage(uniformGreyDrawing);
                    uniformGrey.Freeze();
                    wallpaper.Effect = null;
                    view.SetWallpaper(uniformGrey); Flush(window);
                    byte[] controlsCenterBody = Pixel(new Point(controls.Left + 18, controls.Bottom - 8));
                    byte[] controlsNearBodyEdge = Pixel(new Point(body.Right - 4, body.Top + 18));
                    byte[] controlsJustOutsideBody = Pixel(new Point(body.Right + 2, body.Top + 18));
                    static double CompositeOverDesktop(byte[] pbgra, byte desktopChannel) => pbgra[2] + (255 - pbgra[3]) / 255d * desktopChannel;
                    double centerOnLavender = CompositeOverDesktop(controlsCenterBody, 225);
                    double insideEdgeOnLavender = CompositeOverDesktop(controlsNearBodyEdge, 225);
                    double outsideEdgeOnLavender = CompositeOverDesktop(controlsJustOutsideBody, 225);
                    if (centerOnLavender < 115 || insideEdgeOnLavender < centerOnLavender - 6 || Math.Abs(outsideEdgeOnLavender - insideEdgeOnLavender) > 16)
                        failures.Add($"window controls frost darkened or stepped sharply across BodyFrame edge (center={centerOnLavender:0.#}, insideEdge={insideEdgeOnLavender:0.#}, outsideEdge={outsideEdgeOnLavender:0.#})");
                    Rect sec = Bounds(Part<FrameworkElement>(view, "SecondarySurface"), view);
                    byte[] secTop = Pixel(new Point(sec.Left + sec.Width / 2, sec.Top + 6));
                    byte[] secMid = Pixel(new Point(sec.Left + sec.Width / 2, sec.Top + sec.Height / 2));
                    if (Math.Abs(secTop[0] - secMid[0]) > 4)
                        failures.Add($"WallpaperBlurCache darkened near top edge (top={secTop[0]}, mid={secMid[0]})");
                }
                view.SetWallpaper(Pattern(true)); wallpaper.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40 }; Flush(window);
                double previousOpacity = wallpaper.Opacity;
                wallpaper.Opacity = 0; Flush(window);
                byte[] zeroOpacityBody = Pixel(bodyPoint);
                wallpaper.Opacity = previousOpacity;
                if (zeroOpacityBody[3] != 255)
                    failures.Add($"zero-opacity wallpaper exposed the desktop (darkText={darkText}, alpha={zeroOpacityBody[3]})");
                wallpaper.Effect = null;
            }
            Native.Check(failures.Count == 0, "Independent surface materials: " + string.Join("; ", failures));
        }
        finally {
            var wallpaper = Part<Image>(view, "WallpaperImage");
            wallpaper.Opacity = 1; wallpaper.Effect = null; view.SetDesktopBackdropActive(false);
            GlassMaterial.SetOpacityMultiplier(view, density); view.SetState(State(icon)); view.SetWallpaper(Image()); Flush(window);
        }
    }

    private static void DesktopMaterial(Window window, GlassLauncherView view, ImageSource icon)
    {
        double density = GlassMaterial.GetOpacityMultiplier(view);
        try {
            view.SetState(State(icon, useDarkText: false)); view.SetWallpaper(null!);
            GlassMaterial.SetOpacityMultiplier(view, .85); view.SetDesktopBackdropActive(true); Flush(window);
            var backdrop = Part<GlassSurface>(view, "ApplicationBackdrop");
            Native.Check(GlassMaterial.GetDesktopBackdropEnabled(backdrop) && GlassMaterial.GetDesktopBackdropEnabled(Part<GlassSurface>(view, "NavigationSurface")),
                "The main surface still blocks the desktop material inherited by its navigation rail.");
            Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
            Rect rail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
            byte[] Sample(Point point) {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view); byte[] pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0); return pixel;
            }
            Point empty = new(body.Right - 64, body.Bottom - 100);
            byte[] tint = Sample(empty);
            bool enabled = (bool)Native.Property(typeof(GlassSurface), "CanUseTransparency").GetValue(backdrop)!;
            Native.Check(enabled ? tint[3] is >= 215 and <= 219 : tint[3] == 255,
                "The main backdrop ignores its 85% opacity or lost the solid accessibility fallback: " + tint[3]);
            Native.Check(Math.Abs(tint[0] - tint[1]) <= 1 && Math.Abs(tint[1] - tint[2]) <= 1,
                "The dark material regained a blue background tint.");
            Native.Check(Sample(new Point((rail.Right + body.Left) / 2, view.ActualHeight / 2))[3] == 0,
                "Desktop material filled the transparent gap between the detached surfaces.");
            Native.Check(view.Opacity == 1 && Part<Button>(view, "LaunchButton").Opacity == 1 && ((SolidColorBrush)view.Resources["GlassTextBrush"]).Color.A == 255,
                "Background translucency faded the foreground content.");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast && enabled) {
                GlassMaterial.SetDesktopBackdropEnabled(window, true);
                try {
                    using var liveBackdrop = GlassWindowBackdrop.Attach(window, new GlassBackdropOptions {
                        UseDarkTheme = true, ManageWindowRegion = false, RegionsForClientSize = view.GetDesktopBackdropRegions
                    });
                    liveBackdrop.SetRegions(view.GetDesktopBackdropRegions());
                    Native.Check(liveBackdrop.HasDesktopBackdrop && liveBackdrop.NativeBackdropHandle != 0,
                        "Live Windows 11 host-backdrop composition did not activate: " + liveBackdrop.DiagnosticMessage);
                    object host = Native.Get(liveBackdrop, "_compositionHost")!;
                    object comp = Native.Get(host, "_composition")!;
                    bool blurred = (bool)Native.Property(host.GetType(), "HasBlurredEffectBrush").GetValue(host)!;
                    string diag = (string)Native.Property(comp.GetType(), "BlurDiagnostic").GetValue(comp)!;
                    Native.Check(blurred, "Desktop host-backdrop brush did not compile its Direct2D GaussianBlur effect factory: " + diag);
                    view.SetWallpaper(Image()); Flush(window);
                    liveBackdrop.SetRegions(view.GetDesktopBackdropRegions());
                    ResizeDesktopBackdrop(window, view, liveBackdrop);
                } finally {
                    view.SetWallpaper(null!); Flush(window);
                    GlassMaterial.SetDesktopBackdropEnabled(window, false);
                }
            }
            view.SetDesktopBackdropActive(false); Flush(window);
            Native.Check(Sample(empty)[3] == 255, "Losing the native backdrop left an unsupported transparent main surface.");
        }
        finally {
            view.SetDesktopBackdropActive(false); GlassMaterial.SetOpacityMultiplier(view, density);
            view.SetState(State(icon)); view.SetWallpaper(Image()); Flush(window);
        }
    }

    private static void ResizeDesktopBackdrop(Window window, GlassLauncherView view, GlassWindowBackdrop backdrop)
    {
        nint main = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        nint material = backdrop.NativeBackdropHandle;
        var resizeFlags = new List<uint>();
        SubclassProc observe = (hwnd, message, wParam, lParam, _, _) =>
        {
            if (message == 0x0047 && lParam != 0)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                if ((position.Flags & 0x0001) == 0) resizeFlags.Add(position.Flags);
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        double width = window.Width, height = window.Height, viewWidth = view.Width, viewHeight = view.Height;
        object host = Native.Get(backdrop, "_compositionHost")!;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        Native.Check(SetWindowSubclass(material, observe, 1, 0), "Could not observe the owned backdrop HWND.");
        try
        {
            foreach (int delta in new[] { -100, 0 })
            {
                resizeFlags.Clear();
                window.Width = width + delta; window.Height = height + delta;
                Flush(window);
                Native.Check(GetClientRect(main, out ClientRect primary), "Could not read the resized client area.");
                Native.Check(GetClientRect(material, out ClientRect companion) &&
                    primary.Right == companion.Right && primary.Bottom == companion.Bottom,
                    "The frosted companion's native client area no longer matches the launcher after resizing.");
                Native.Check(resizeFlags.Count != 0 && resizeFlags.All(flags => (flags & 0x0008) == 0),
                    "A native backdrop resize suppressed repainting the exposed area.");
                // The fixed component viewport still represents the old layout here.
                var arranged = view.GetDesktopBackdropRegions();
                var actual = (GlassWindowRegion[])Native.Get(host, "_regions")!;
                double widthDelta = primary.Right / dpi.DpiScaleX - view.ActualWidth;
                double heightDelta = primary.Bottom / dpi.DpiScaleY - view.ActualHeight;
                Native.Check(actual.Length == 2 &&
                    Math.Abs(actual[0].Bounds.Bottom - (arranged[0].Bounds.Bottom + heightDelta)) < 1 &&
                    Math.Abs(actual[1].Bounds.Right - (arranged[1].Bounds.Right + widthDelta)) < 1,
                    "The wallpaper-mode rail or caption frost kept its old composition clip while the window resized.");
                view.Width = viewWidth + delta; view.Height = viewHeight + delta;
                Flush(window);
                backdrop.SetRegions(view.GetDesktopBackdropRegions());
            }
        }
        finally
        {
            RemoveWindowSubclass(material, observe, 1);
            window.Width = width; window.Height = height; view.Width = viewWidth; view.Height = viewHeight;
            Flush(window);
            GC.KeepAlive(observe);
        }
    }

    private static void ExtendedWallpaperSample(Window window)
    {
        var viewport = (Canvas)window.Content;
        var host = new Border { Width = 80, Height = 64 };
        Canvas.SetLeft(host, 80);
        var wallpaper = new DrawingImage(new GeometryDrawing(Brushes.Teal, null, new RectangleGeometry(new Rect(0, 0, 80, 64))));
        wallpaper.Freeze();
        var surface = new GlassSurface { Width = 120, Height = 64, Background = Brushes.Transparent, BlurRadius = 16, HighlightOpacity = 0 };
        GlassMaterial.SetWallpaperSource(surface, wallpaper);
        GlassMaterial.SetWallpaperHost(surface, host);
        viewport.Children.Add(host); viewport.Children.Add(surface);
        try {
            Flush(window);
            if (!surface.IsTransparencyActive) return;
            byte[] Sample(int x) {
                var bitmap = new RenderTargetBitmap(120, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                byte[] pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, 32, 1, 1), pixel, 4, 0);
                return pixel;
            }
            byte[] inside = Sample(105), outside = Sample(16);
            Native.Check(Math.Abs(inside[2] - outside[2]) > 50, "The fixture did not cross the wallpaper boundary.");
            surface.ExtendWallpaper = true;
            Flush(window);
            Native.Check(Sample(16).Zip(inside).All(channel => Math.Abs(channel.First - channel.Second) < 5),
                "A wallpaper-sampling popup changed to a solid color past the launcher panel boundary.");
        } finally {
            viewport.Children.Remove(surface); viewport.Children.Remove(host);
            Flush(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Handle, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out ClientRect bounds);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

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
            clip != null && clip.Children.Count == 3 && clip.Children.Cast<RectangleGeometry>().Select((shape, index) =>
                Same(shape.Rect, regions[index].Bounds) && shape.RadiusX == regions[index].CornerRadius && shape.RadiusY == regions[index].CornerRadius).All(value => value) &&
            image.Clip is null && Part<Border>(view, "MainSurface").Clip is null &&
            ReferenceEquals(Part<Border>(view, "MainSurface").OpacityMask, view.SurfaceOpacityMask),
            "The custom wallpaper clip must match all three rounded surfaces and preserve their transparent gaps.");
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
        Native.Check(Contains(pane, row) && row.Height > row.Width && row.Left < header.Left,
            "Context navigation is not the shared vertical secondary pane at " + width);
        Native.Check(view.GetSurfaceRegions().Count == 3, "Context navigation changed the three workspace window shapes.");
        view.SetSidebar(Array.Empty<GlassSidebarItem>());
        view.SetState(State(wallpaper));
        Flush(window);
    }

    internal static void CheckSurfacePixels(GlassLauncherView view, RenderTargetBitmap bitmap, string mode)
    {
        Rect rail = Bounds(Part<FrameworkElement>(view, "NavigationSurface"), view);
        Rect body = Bounds(Part<FrameworkElement>(view, "BodyFrame"), view);
        Rect controls = Bounds(Part<FrameworkElement>(view, "WindowControlsSurface"), view);
        byte Alpha(Point point) {
            byte[] value = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), value, 4, 0);
            return value[3];
        }
        Point gap = new((rail.Right + body.Left) / 2, view.ActualHeight / 2);
        Point topStep = new((body.Left + controls.Left) / 2, body.Top / 2);
        Point rightStep = new((body.Right + controls.Right) / 2, (controls.Bottom + body.Bottom) / 2);
        foreach (Point outside in new[] { gap, topStep, rightStep })
            Native.Check(Alpha(outside) == 0 && !view.SurfaceClip.FillContains(outside),
                "The transparent gap or staggered inset is still painted or hit-shaped in " + mode + ": " + outside);
        for (int y = 0; y < 12; y++)
        for (int x = 0; x < 12; x++)
            Native.Check(Alpha(new Point(rail.Left + x, rail.Top + y)) == Alpha(new Point(body.Left + x, body.Top + y)),
                "Equal outer radii have different antialias coverage in " + mode);
        var surfaceChecks = new[] {
            (Bounds: rail, Corners: new[] { (true, true), (false, true), (true, false), (false, false) }),
            (Bounds: body, Corners: new[] { (true, true), (true, false), (false, false) }),
            (Bounds: controls, Corners: new[] { (true, true), (false, true), (false, false) })
        };
        foreach (var (surface, corners) in surfaceChecks) {
            Native.Check(Alpha(new Point(surface.Left + surface.Width / 2, surface.Top + surface.Height / 2)) == 255,
                "A workspace surface lost its opaque launcher-owned base in " + mode);
            foreach (var (left, top) in corners) {
                Point extreme = new(left ? surface.Left : surface.Right - 1, top ? surface.Top : surface.Bottom - 1);
                Native.Check(Alpha(extreme) == 0, "A rounded surface retained a painted square corner in " + mode + ": " + extreme);
                int partial = 0;
                for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++) {
                    Point point = new(left ? surface.Left + x : surface.Right - 1 - x, top ? surface.Top + y : surface.Bottom - 1 - y);
                    if (Alpha(point) is > 0 and < 255) partial++;
                }
                Native.Check(partial >= 4, "A workspace corner lost its antialiased alpha coverage in " + mode);
            }
        }
        Native.Check(Alpha(new Point(body.Right - 1, body.Top)) == 255 && Alpha(new Point(controls.Left, controls.Bottom - 1)) == 255,
            "The staggered controls capsule and main panel overlap lost opaque coverage in " + mode);
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
        view.BackRequested += (_, _) => Add("back");
        var navigation = new List<int>(); view.NavigationRequested += navigation.Add;
        view.SetState(State(image));
        Click(Part<Button>(view, "LaunchButton"));
        Click(Button(view, "选择实例"));
        Click(Part<Button>(view, "InstanceSettingsButton"));
        Click(Button(view, "最小化窗口"));
        Click(Button(view, "关闭窗口"));
        foreach (string name in new[] { "NavDownload", "NavFavorites", "NavSettings", "NavOther", "NavLaunch" }) Click(Part<Button>(view, name));
        Native.Check(navigation.SequenceEqual(new[] { 1, 10, 3, 4, 0 }), "Top navigation emitted altered legacy page identities.");
        Click(Part<Button>(view, "ExpandButton"));
        Click(Part<Button>(view, "RecentWorldButton"));
        Click(Part<Button>(view, "ExpandButton"));
        view.SetState(State(image, launching: true)); Click(Part<Button>(view, "LaunchButton"));
        view.SetState(State(image, back: true)); Click(Button(view, "返回上一页"));
        foreach (string name in new[] { "start", "cancel", "select", "settings", "minimize", "close", "world", "back" })
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
        int accountRequests = 0, refreshes = 0, opened = 0, menus = 0;
        view.AccountRequested += (_, _) => accountRequests++;
        view.SetAccountContent(accountContent);
        // The contextual sidebar carries everything the removed "all page options" portal used to: captions, command rows,
        // row accessories and context menus.
        view.SetSidebar(new[] {
            new GlassSidebarItem { Label = "社区资源", IsHeader = true },
            new GlassSidebarItem { Label = "模组", IsSelected = true, Invoked = () => opened++, ContextRequested = _ => menus++,
                Actions = new[] { new GlassSidebarAction { ToolTip = "刷新", IconData = "M0,0 H10 V10 H0 Z", Invoked = () => refreshes++ } } },
            new GlassSidebarItem { Label = "添加已有文件夹", IsCommand = true, Invoked = () => opened++ },
        });
        Flush(window);
        Native.Check(view.FindName("NavigationMenuButton") is null && view.FindName("NavigationPopover") is null,
            "The redundant all-page-options portal returned beside the sidebar.");
        var sidebar = Part<StackPanel>(view, "SidebarPanel");
        Native.Check(sidebar.Children.Count == 3 && sidebar.Children[0] is TextBlock { Text: "社区资源" } caption && caption.FontSize < 13,
            "Sidebar category captions are missing or not rendered as small headers.");
        Click(Button(view, "模组"));
        Click(Button(view, "模组：刷新"));
        Click(Button(view, "添加已有文件夹"));
        Native.Check(opened == 2 && refreshes == 1, "Sidebar rows or their accessories did not reach the original actions.");
        Button account = Part<Button>(view, "AccountButton");
        FrameworkElement accountPopover = Part<FrameworkElement>(view, "AccountPopover");
        Click(account);
        Native.Check(accountRequests == 1 && accountPopover.IsVisible, "Account action did not open the actual portal.");
        Native.Check(ReferenceEquals(Part<ContentControl>(view, "AccountContent").Content, accountContent), "Account content was copied or replaced.");
        Escape(accountContent);
        Native.Check(accountPopover.Visibility == Visibility.Collapsed && ReferenceEquals(FocusManager.GetFocusedElement(window), account),
            "Escape did not dismiss the account portal and restore logical focus to its trigger.");
        AccountWorkspaceDrag(window, view);
        view.SetAccountContent(null!); view.SetSidebar(Array.Empty<GlassSidebarItem>());
        view.SetState(State(image));
        Flush(window);
        WorkbenchFrame(view);
    }

    private static void AccountWorkspaceDrag(Window window, GlassLauncherView view)
    {
        var sideTitle = new TextBlock { Text = "游戏账号" };
        var sceneTitle = new TextBlock { Text = "切换账号" };
        var action = new Button { Content = "使用账号" };
        var input = new TextBox { Text = "离线账号" };
        var deckCard = new Border { Background = Brushes.Transparent, Child = new TextBlock { Text = "已保存账号" } };
        view.SetAccountWorkspace(sideTitle, new StackPanel { Children = { sceneTitle, deckCard, action, input } });
        view.SetAccountWorkspaceOpen(true, immediate: true);
        Flush(window);
        int drags = 0;
        EventHandler dragged = (_, _) => drags++;
        view.DragRequested += dragged;
        try
        {
            static void Press(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
            Press(sideTitle); Press(sceneTitle);
            Native.Check(drags == 2, "Account workspace headings do not relay window drag requests.");
            var protection = (DependencyProperty)Native.Field(typeof(GlassLauncherView), "PreventWindowDragProperty").GetValue(null)!;
            deckCard.SetValue(protection, true);
            Press(deckCard); Press(action); Press(input);
            Native.Check(drags == 2, "Window dragging intercepted account cards or their interactive controls.");
        }
        finally
        {
            view.DragRequested -= dragged;
            view.SetAccountWorkspace(null!, null!);
            Flush(window);
        }
    }

    private static void ContentHost(GlassLauncherView view, ImageSource image)
    {
        var page = new Grid();
        var originalRightContainer = new ContentControl { Content = page };
        view.SetPageContent(originalRightContainer);
        view.SetState(State(image));
        Flush(Window.GetWindow(view));
        // Home shows the original right page's homepage content (cards) and lets it take input.
        Native.Check(originalRightContainer.IsLoaded && page.IsLoaded && view.PageContentHost.Visibility == Visibility.Visible &&
            view.PageContentHost.IsHitTestVisible,
            "Home did not materialize and show the original right page's homepage content.");
        view.SetState(State(image, home: false));
        Native.Check(ReferenceEquals(view.PageContentHost.Content, originalRightContainer) && view.PageContentHost.IsVisible &&
            !Part<FrameworkElement>(view, "HomePanel").IsVisible, "The main content slot did not host the supplied actual page.");
        view.SetState(State(image));
        Native.Check(view.PageContentHost.IsVisible && Part<FrameworkElement>(view, "HomePanel").IsVisible,
            "Returning home did not restore the current-instance shell next to its homepage content.");
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

    private static bool Contains(Rect outer, Rect inner) => !outer.IsEmpty && !inner.IsEmpty &&
        inner.Left >= outer.Left - 0.05 && inner.Top >= outer.Top - 0.05 &&
        inner.Right <= outer.Right + 0.05 && inner.Bottom <= outer.Bottom + 0.05;

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
