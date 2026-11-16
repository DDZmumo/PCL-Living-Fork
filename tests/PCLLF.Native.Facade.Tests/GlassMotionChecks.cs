// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCLLF.Native.Facade.Tests;

// Actual native view and template states; no backend/login/game input is started.
internal static class GlassMotionChecks
{
    internal static void Run(Window window, GlassLauncherView view, ImageSource icon)
    {
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
        Until(() => Math.Abs(Thumb(toggle).X) < .01, "Toggle unchecked endpoint");
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
        Until(() => Math.Abs(Thumb(toggle).X - ToggleFeedback.GetTravel(toggle)) < .01, "Toggle checked endpoint");
        view.SetState(State(icon, true, dependency: false));
        Check(Thumb(toggle).X == 0 && popup.Visibility == Visibility.Collapsed,
            "Reduced motion left an animated toggle or popup between states.");
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
    private static TranslateTransform Thumb(CheckBox toggle) => toggle.Template.FindName("ThumbTransform", toggle) as TranslateTransform ??
        throw new InvalidOperationException("The native toggle template does not expose its shared spring thumb.");
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
