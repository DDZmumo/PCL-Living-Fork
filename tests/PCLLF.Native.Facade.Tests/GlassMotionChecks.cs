// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCLLF.Native.Facade.Tests;

// Actual native view and template states; no backend/login/game input is started.
internal static class GlassMotionChecks
{
    private static void WallpaperSamplingDuringTransform()
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open()) {
            context.DrawRectangle(new LinearGradientBrush(Colors.Crimson, Colors.DodgerBlue, 0), null, new Rect(0, 0, 640, 480));
            for (int x = 0; x < 640; x += 96) context.DrawRectangle(Brushes.White, null, new Rect(x, 0, 18, 480));
            for (int y = 0; y < 480; y += 120) context.DrawRectangle(Brushes.DarkGreen, null, new Rect(0, y, 640, 18));
        }
        drawing.Freeze(); var picture = new DrawingImage(drawing); picture.Freeze();
        var root = new Canvas { Width = 640, Height = 480 };
        var wallpaper = new Image { Source = picture, Width = 640, Height = 480, Stretch = Stretch.Fill };
        root.Children.Add(wallpaper);
        var surface = new GlassSurface { Background = Brushes.Transparent, BorderThickness = new Thickness(), CornerRadius = new CornerRadius(), BlurRadius = 24,
            WallpaperSource = picture, WallpaperHost = wallpaper, HighlightOpacity = 0 };
        var scale = new ScaleTransform(1, 1); var rise = new TranslateTransform();
        var transforms = new TransformGroup { Children = { scale, rise } };
        var moving = new Border { Width = 340, Height = 180, Child = surface, RenderTransform = transforms, RenderTransformOrigin = new Point(.5, .5) };
        Canvas.SetLeft(moving, 140); Canvas.SetTop(moving, 140); root.Children.Add(moving);
        var sibling = new GlassSurface { Width = 100, Height = 60, Background = Brushes.Transparent, BorderThickness = new Thickness(), BlurRadius = 24,
            WallpaperSource = picture, WallpaperHost = wallpaper, HighlightOpacity = 0 };
        Canvas.SetLeft(sibling, 20); Canvas.SetTop(sibling, 20); root.Children.Add(sibling);
        var window = new Window { Content = root, Width = 640, Height = 480, WindowStyle = WindowStyle.None, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        byte[] Capture() {
            var bitmap = new RenderTargetBitmap(640, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var pixels = new byte[220 * 80 * 4]; bitmap.CopyPixels(new Int32Rect(200, 190, 220, 80), pixels, 220 * 4, 0); return pixels;
        }
        try {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain(); Capture();
            if (!(bool)Native.Property(typeof(GlassSurface), "CanUseTransparency").GetValue(surface)!) return;
            object Plane(GlassSurface item) => Native.Get(Native.Get(item, "_material")!, "_wallpaper")!;
            ImageSource Texture(GlassSurface item) {
                object frame = Native.Get(Plane(item), "Frame") ?? throw new InvalidOperationException("The wallpaper texture was not prepared.");
                return (ImageSource)Native.Property(frame.GetType(), "Source").GetValue(frame)!;
            }
            ImageSource texture = Texture(surface);
            Check(texture is BitmapSource { IsFrozen: true } && ReferenceEquals(texture, Texture(sibling)) &&
                ((UIElement)Plane(surface)).Effect is null && ((UIElement)Plane(sibling)).Effect is null,
                "Cards of one host did not share their frozen blur or retained per-card blur effects.");
            Type cache = typeof(GlassSurface).Assembly.GetType("PCLLF.Wpf.Glass.WallpaperBlurCache", true)!;
            long built = (long)Native.Property(cache, "BuildCount").GetValue(null)!;
            byte[] reference = Capture();
            foreach (var pose in new[] { (.96, 14d), (.98, 7d), (1d, 0d) }) {
                scale.ScaleX = scale.ScaleY = pose.Item1; rise.Y = pose.Item2;
                VisualAssetChecks.Drain();
                byte[] animated = Capture();
                double drift = animated.Zip(reference, (first, second) => Math.Abs(first - second)).Average();
                Check(drift < .5, $"The wallpaper moved in world space under an animated card: scale={pose.Item1}, rise={pose.Item2}, mean difference={drift:0.###}.");
                Native.Call(typeof(GlassSurface), "InvalidateMaterial", surface); window.UpdateLayout(); VisualAssetChecks.Drain();
                byte[] settled = Capture();
                double difference = animated.Zip(settled, (first, second) => Math.Abs(first - second)).Average();
                Check(difference < .5, $"Wallpaper sampling jumped after a pure render transform settled: scale={pose.Item1}, rise={pose.Item2}, mean pixel difference={difference:0.###}.");
                Check(ReferenceEquals(texture, Texture(surface)) && (long)Native.Property(cache, "BuildCount").GetValue(null)! == built,
                    "An entrance transform rebuilt the wallpaper blur instead of reusing its prepared texture.");
            }
            moving.RenderTransform = new TransformGroup { Children = { new ScaleTransform(.97, .97), new TranslateTransform(0, 9) } };
            moving.RenderTransformOrigin = new Point(); VisualAssetChecks.Drain();
            Check(Capture().Zip(reference, (first, second) => Math.Abs(first - second)).Average() < .5 &&
                (long)Native.Property(cache, "BuildCount").GetValue(null)! == built,
                "Replacing an entrance transform/origin moved the wallpaper or rebuilt its blur.");
            if (MotionPreferences.AllowsAnimation) StaggeredEntrance.Play(new[] { moving });
            window.Content = null; VisualAssetChecks.Drain();
            Check(((System.Collections.IDictionary)Native.Get(surface, "_transformWatches")!).Count == 0 && Native.Get(Plane(surface), "Frame") is null,
                "An unloaded surface retained transform observers or a prepared wallpaper frame.");
            Check((Native.Get(Plane(surface), "_brush") as ImageBrush)?.ImageSource is null,
                "The retained material drawing kept an unloaded card's blur texture alive.");
            Check(moving.Opacity == 1, "An unloaded entrance retained its animation state.");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    internal static void Run(Window window, GlassLauncherView view, ImageSource icon)
    {
        WallpaperSamplingDuringTransform();
        ResetPointerIntent(view);
        view.SetState(State(icon, true));
        view.SetAccountContent(new TextBox { Text = "Motion fixture", Height = 40 });
        view.SetState(State(icon, false));
        var popup = Part<FrameworkElement>(view, "AccountPopover");
        var accountMotion = Motion(view, "_accountMotion");
        bool animates = MotionPreferences.AllowsAnimation;
        if (animates)
        {
            AtInFlightRendering(() => view.SetAccountOpen(true),
                () => popup.Opacity is > 0 and < 1 && accountMotion.Velocity > 0,
                () =>
                {
                    Check(popup.IsHitTestVisible, "Opening popover locked input.");
                    MotionSample before = Sample(accountMotion);
                    view.SetAccountOpen(false);
                    MotionSample after = Sample(accountMotion);
                    CriticalContinuity(before, after, MotionTokens.Reveal, "Popover");
                    Check(after.Target == 0 && after.IsRunning && !popup.IsHitTestVisible,
                        "Popover reversal did not retain its new target/running state or dismissed input remained active.");
                }, "Popover first in-flight rendering");
            view.SetAccountOpen(true);
            Check(popup.IsHitTestVisible, "Reopening a closing popover locked input.");
        }
        else view.SetAccountOpen(true);
        Until(() => Math.Abs(popup.Opacity - 1) < .001, "Popover final open endpoint");
        view.SetAccountOpen(false);
        Until(() => popup.Visibility == Visibility.Collapsed, "Popover final closed endpoint");

        var navigation = Motion(view, "_navigationMotion");
        if (animates)
        {
            AtInFlightRendering(() => view.SetState(State(icon, false, navigation: 3)),
                () => navigation.Value > 0 && navigation.Value < navigation.Target && navigation.Velocity > 0,
                () =>
                {
                    MotionSample before = Sample(navigation);
                    view.SetState(State(icon, false, navigation: 0));
                    MotionSample after = Sample(navigation);
                    CriticalContinuity(before, after, MotionTokens.Reveal, "Navigation");
                    Check(after.Target < before.Target && after.IsRunning, "Navigation reversal lost its destination or stopped early.");
                }, "Navigation first in-flight rendering");
        }
        else view.SetState(State(icon, false, navigation: 0));
        Until(() => !navigation.IsRunning, "Navigation final endpoint");
        NavigationHover(view, icon, animates);

        view.SetState(State(icon, true, home: false));
        view.SetPageContent(new Grid { Background = Brushes.OrangeRed });
        window.UpdateLayout();
        view.SetState(State(icon, false, home: false));
        var next = new Grid { Background = Brushes.SteelBlue };
        view.SetPageContent(next);
        var snapshot = Part<Image>(view, "PageSnapshot");
        Check(ReferenceEquals(view.PageContentHost.Content, next) && view.PageContentHost.IsHitTestVisible,
            "Page transition delayed actual navigation or input ownership.");
        if (animates)
            Check(snapshot.Source is not null && snapshot.Source.IsFrozen && !snapshot.IsHitTestVisible,
                "Page replacement failed to preserve a frozen noninteractive outgoing frame.");
        Observe(30);
        var final = new Grid { Background = Brushes.ForestGreen };
        view.SetPageContent(final);
        Until(() => snapshot.Source is null && view.PageContentHost.Opacity > .999, "Rapid page replacement final endpoint");
        Check(ReferenceEquals(view.PageContentHost.Content, final), "An old page completion restored stale content.");

        view.SetState(State(icon, true));
        var plus = Part<Button>(view, "ExpandButton");
        plus.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        window.UpdateLayout();
        var toggle = Part<CheckBox>(view, "DependencyCheckBox");
        toggle.ApplyTemplate();
        view.SetState(State(icon, false));
        ToggleFeedback.SetReduceMotion(toggle, false);
        toggle.IsChecked = false;
        Until(() => CheckTrack(toggle).Opacity < .01 && CheckGlyph(toggle).Opacity < .01 && Math.Abs(CheckGlyph(toggle).StrokeDashOffset - 7.6) < .05, "Checkbox unchecked endpoint");
        if (animates)
        {
            SpringMotion position = TogglePosition(toggle);
            AtInFlightRendering(() => toggle.IsChecked = true,
                () => position.Value is > 0 and < 1 && position.Velocity > 0,
                () =>
                {
                    MotionSample before = Sample(position);
                    toggle.IsChecked = false;
                    MotionSample after = Sample(position);
                    CriticalContinuity(before, after, MotionTokens.Toggle, "Toggle");
                    Check(after.Target == 0 && after.IsRunning, "Native toggle reversal lost its destination or stopped early.");
                }, "Toggle first in-flight rendering");
            toggle.IsChecked = true;
        }
        else toggle.IsChecked = true;
        Until(() => Math.Abs(CheckTrack(toggle).Opacity - 1) < .01 && Math.Abs(CheckCore(toggle).ScaleX - 1) < .01 && Math.Abs(CheckGlyph(toggle).StrokeDashOffset) < .05, "Checkbox checked endpoint");
        view.SetState(State(icon, true, dependency: false));
        Check(CheckTrack(toggle).Opacity == 0 && CheckGlyph(toggle).Opacity == 0 && popup.Visibility == Visibility.Collapsed,
            "Reduced motion left an animated checkbox or popup between states.");
        view.SetAccountOpen(true);
        Check(popup.Opacity == 1, "Reduced-motion popover did not open immediately.");
        view.SetAccountOpen(false);
        view.SetAccountContent(null!);
        view.SetPageContent(null!);
        plus.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        view.SetState(State(icon, true));
        Console.WriteLine("PASS glass motion: reversible native popover/nav velocity, outgoing-page snapshot and rapid replacement, native toggle travel/reversal, immediate input ownership and reduced-motion endpoints; animations=" + animates + ".");
    }

    private static GlassLauncherViewState State(ImageSource icon, bool reduce, bool home = true, int navigation = 0, bool dependency = true) => new()
    {
        IsHome = home, SelectedNavigationId = navigation, ReduceMotion = reduce, UseDarkText = true,
        HasInstance = true, CanLaunch = true, InstanceName = "Motion fixture", InstanceIcon = icon,
        AccountAvatar = icon, RecentWorldIcon = icon, DependencyCheckEnabled = dependency, AutoFillEnabled = true,
    };
    private static SpringMotion Motion(GlassLauncherView view, string name) =>
        (SpringMotion)(typeof(GlassLauncherView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) ??
            throw new InvalidOperationException("Actual view spring missing: " + name));

    private static void NavigationHover(GlassLauncherView view, ImageSource icon, bool animates)
    {
        view.SetState(State(icon, true));
        var button = Part<Button>(view, "NavDownload");
        button.ApplyTemplate();
        var motions = (Dictionary<Button, SpringMotion>)(typeof(GlassLauncherView)
            .GetField("_navigationHoverMotions", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) ??
            throw new InvalidOperationException("Actual navigation hover lifecycle state missing."));
        SpringMotion hover = motions.TryGetValue(button, out SpringMotion? owned) && owned is not null ? owned :
            throw new InvalidOperationException("Loaded navigation has no owned hover spring.");
        var layer = button.Template.FindName("NavHoverLayer", button) as Border ??
            throw new InvalidOperationException("Actual navigation hover template layer missing.");
        var translation = button.Template.FindName("NavHoverTranslation", button) as TranslateTransform ??
            throw new InvalidOperationException("Actual navigation hover template translation missing.");
        var selection = Part<Border>(view, "NavSelection");
        var navigation = Motion(view, "_navigationMotion");
        double selectionPosition = navigation.Value;
        Rect bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
        // Exercise the actual spring-to-template seam without changing the desktop pointer.
        // Physical MouseEnter/Leave admission is a separate interactive input check.
        hover.Retarget(1, true);
        Check(layer.Opacity == 1 && translation.X == 1.5 && navigation.Value == selectionPosition &&
            System.Windows.Automation.AutomationProperties.GetItemStatus(button) == string.Empty &&
            System.Windows.Automation.AutomationProperties.GetItemStatus(Part<Button>(view, "NavLaunch")) == "当前页面",
            "A hover replaced the persistent selection or lost its independent low-amplitude presentation.");
        Check(!ReferenceEquals(layer.Background, selection.Background) && selection.Child is Border marker && marker.Width == 3 &&
            !selection.IsHitTestVisible && !layer.IsHitTestVisible && button.FocusVisualStyle is not null &&
            button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize)) == bounds,
            "Hover/selection/focus layers were merged or feedback moved the navigation hit rectangle.");
        if (!SystemParameters.HighContrast)
            Check(layer.Background is SolidColorBrush hoverBrush && selection.Background is SolidColorBrush selectedBrush &&
                hoverBrush.Color != selectedBrush.Color, "Neutral hover and selected emphasis use the same color.");
        button.IsEnabled = false;
        Check(layer.Opacity == 0 && translation.X == 0 && !hover.IsRunning,
            "Disabling a hovered navigation action retained feedback or a live animation.");
        button.IsEnabled = true;
        if (animates)
        {
            view.SetState(State(icon, false));
            AtInFlightRendering(() => hover.Retarget(1),
                () => hover.Value is > 0 and < 1 && hover.Velocity > 0,
                () => {
                    MotionSample before = Sample(hover);
                    hover.Retarget(0);
                    CriticalContinuity(before, Sample(hover), MotionTokens.Reveal, "Navigation hover");
                    Check(button.IsHitTestVisible && button.IsEnabled && navigation.Value == selectionPosition,
                        "Hover reversal blocked input or changed persistent selection.");
                }, "Navigation hover first in-flight rendering");
        }
        view.SetState(State(icon, true));
        Check(layer.Opacity == 0 && translation.X == 0 && !hover.IsRunning,
            "Reduced motion left navigation hover between states.");
    }
    private readonly record struct MotionSample(double Value, double Velocity, double Target, bool IsRunning, double Timestamp);
    private static MotionSample Sample(SpringMotion motion) => new(motion.Value, motion.Velocity, motion.Target, motion.IsRunning,
        (double)(typeof(SpringMotion).GetField("_lastTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(motion) ??
            throw new InvalidOperationException("Spring rendering timestamp missing.")));
    private static void CriticalContinuity(MotionSample before, MotionSample after, SpringParameters parameters, string owner)
    {
        Check(parameters.DampingRatio == 1 && before.IsRunning && before.Velocity > 0,
            owner + " continuity requires a genuinely moving critical spring.");
        double elapsed = after.Timestamp - before.Timestamp;
        Check(double.IsFinite(elapsed) && elapsed >= 0, owner + " rendering clock moved backwards.");
        // Retarget advances the previous destination to the actual call time before changing it.
        // Compare its exact physical state there, not against an arbitrary distance from a stale frame.
        double omega = 2 * Math.PI / parameters.ResponseSeconds;
        double displacement = before.Value - before.Target;
        double combined = before.Velocity + omega * displacement;
        double decay = Math.Exp(-omega * elapsed);
        double expected = before.Target + decay * (displacement + combined * elapsed);
        double expectedVelocity = decay * (before.Velocity - omega * combined * elapsed);
        if (Math.Abs(expected - before.Target) <= parameters.PositionTolerance && Math.Abs(expectedVelocity) <= parameters.VelocityTolerance)
        {
            expected = before.Target;
            expectedVelocity = 0;
        }
        Check(Math.Abs(after.Value - expected) <= 1e-10 && Math.Abs(after.Velocity - expectedVelocity) <= 1e-9,
            owner + " retarget changed physical value/velocity beyond exact elapsed-time advancement. dt=" + elapsed +
            "; expected=" + expected + "/" + expectedVelocity + "; actual=" + after.Value + "/" + after.Velocity);
    }
    private static SpringMotion TogglePosition(CheckBox toggle)
    {
        var property = typeof(ToggleFeedback).GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as DependencyProperty ??
            throw new InvalidOperationException("Actual toggle feedback state property missing.");
        object state = toggle.GetValue(property) ?? throw new InvalidOperationException("Actual toggle feedback state missing.");
        return state.GetType().GetField("_position", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state) as SpringMotion ??
            throw new InvalidOperationException("Actual native toggle position spring missing.");
    }
    private static void AtInFlightRendering(Action start, Func<bool> inFlight, Action interrupt, string description)
    {
        bool complete = false;
        Exception? failure = null;
        EventHandler rendered = (_, _) =>
        {
            if (complete || !inFlight()) return;
            complete = true;
            try { interrupt(); }
            catch (Exception error) { failure = error; }
        };
        CompositionTarget.Rendering += rendered;
        try { start(); Until(() => complete, description); }
        finally { CompositionTarget.Rendering -= rendered; }
        if (failure is not null) throw failure;
    }
    private static T Part<T>(GlassLauncherView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException("Actual motion view part missing: " + name);
    private static FrameworkElement CheckTrack(CheckBox toggle) => toggle.Template.FindName("TrackActive", toggle) as FrameworkElement ??
        throw new InvalidOperationException("The native checkbox template does not expose its active double-frame track.");
    private static ScaleTransform CheckCore(CheckBox toggle) => toggle.Template.FindName("CheckCoreScale", toggle) as ScaleTransform ??
        throw new InvalidOperationException("The native checkbox template does not expose its core scale spring.");
    private static System.Windows.Shapes.Shape CheckGlyph(CheckBox toggle) => toggle.Template.FindName("CheckMark", toggle) as System.Windows.Shapes.Shape ??
        throw new InvalidOperationException("The native checkbox template does not expose its drawn checkmark path.");
    private static void ResetPointerIntent(GlassLauncherView view) => view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    { RoutedEvent = Mouse.PreviewMouseDownEvent });
    private static void Observe(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        Until(() => clock.ElapsedMilliseconds >= milliseconds, "Display observation");
    }
    private static void Until(Func<bool> ready, string description)
    {
        if (ready()) return;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherOperation? deadlineOperation = null;
        object gate = new();
        bool stopped = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        EventHandler sample = (_, _) =>
        {
            try { if (ready()) frame.Continue = false; }
            catch (Exception error) { failure = error; frame.Continue = false; }
        };
        timer.Tick += sample;
        using var deadline = new System.Threading.Timer(_ =>
        {
            lock (gate)
            {
                if (!stopped) deadlineOperation = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
            }
        }, null, 4000, Timeout.Infinite);
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally
        {
            lock (gate) stopped = true;
            timer.Stop(); timer.Tick -= sample;
            if (deadlineOperation?.Status == DispatcherOperationStatus.Pending) deadlineOperation.Abort();
        }
        if (failure is not null) throw failure;
        Check(ready(), "Bounded native motion wait failed: " + description);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
