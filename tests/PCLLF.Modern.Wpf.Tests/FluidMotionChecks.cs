// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Motion;

namespace PCLLF.Modern.Wpf.Tests;

internal static class FluidMotionChecks {
    internal static void Run() {
        CheckRetargeting();
        CheckFrameRatesAndSuspension();
        CheckDriverLifecycle();
        CheckPressFeedback();
        CheckStaticHandlerRelease();
        Console.WriteLine("PASS native fluid motion: exact 60/144 Hz trajectories, velocity-preserving rapid reversals, rest/suspension, accessibility fallback, unload/dispose and rendering-handler collection, native pointer press feedback.");
    }

    private static void CheckRetargeting() {
        var spring = new SpringValue(0);
        spring.Retarget(1);
        for (int frame = 0; frame < 6; frame++) spring.Advance(1.0 / 60);
        double position = spring.Value, velocity = spring.Velocity;
        Require(position > 0 && position < 1 && velocity > 0, "The spring did not respond to an opening target.");
        spring.Retarget(0);
        Require(spring.Value == position && spring.Velocity == velocity, "Reversal reset the live value or velocity.");
        spring.Advance(.00001);
        Require(spring.Velocity > 0 && spring.Value > position,
            "Reversal abruptly changed velocity instead of first carrying the existing momentum.");
        for (int change = 0; change < 24; change++) {
            position = spring.Value; velocity = spring.Velocity;
            spring.Retarget(change % 2 == 0 ? 1 : 0);
            Require(position == spring.Value && velocity == spring.Velocity, "A rapid reversal introduced a discontinuity.");
            spring.Advance(.013);
            Require(double.IsFinite(spring.Value) && double.IsFinite(spring.Velocity), "Rapid toggling produced a non-finite state.");
        }
        spring.Retarget(1);
        spring.Advance(2);
        Require(spring.IsAtRest && spring.Value == 1 && spring.Velocity == 0, "The final reversal did not settle exactly.");

        var monotonic = new SpringValue(0);
        monotonic.Retarget(1);
        double last = 0;
        for (int frame = 0; frame < 200; frame++) {
            monotonic.Advance(1.0 / 144);
            Require(monotonic.Value >= last && monotonic.Value <= 1, "The shared critically damped token overshot a static panel target.");
            last = monotonic.Value;
        }
        Require(monotonic.IsAtRest, "The shared spatial spring never reached rest.");

        var handoff = new SpringValue(0, initialVelocity: 3);
        handoff.Retarget(1);
        Require(handoff.Velocity == 3, "The spring discarded an initial gesture velocity.");
        double original = handoff.Value;
        handoff.Advance(0);
        Require(handoff.Value == original && handoff.Velocity == 3, "A zero-length frame changed the spring state.");
        Throws<ArgumentOutOfRangeException>(() => handoff.Retarget(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => handoff.Advance(-1));
        Throws<ArgumentOutOfRangeException>(() => handoff.Advance(double.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => new SpringValue(0, default(SpringParameters)));
        Throws<ArgumentOutOfRangeException>(() => new SpringParameters(0));
    }

    private static void CheckFrameRatesAndSuspension() {
        foreach (double damping in new[] { .8, .99999999, 1.0, 1.00000001, 1.4 }) {
            var parameters = new SpringParameters(.4, damping);
            SpringValue sixty = Simulate(parameters, 60), highRefresh = Simulate(parameters, 144);
            Close(sixty.Value, highRefresh.Value, 1e-11, "Refresh rate changed spring position.");
            Close(sixty.Velocity, highRefresh.Velocity, 1e-10, "Refresh rate changed spring velocity.");
            var irregular = new SpringValue(-3, parameters, 2);
            irregular.Retarget(7);
            foreach (double interval in new[] { .005, .091, .007, .149, .001, .247 }) irregular.Advance(interval);
            Close(sixty.Value, irregular.Value, 1e-11, "Irregular rendering intervals changed the physical trajectory.");
            Close(sixty.Velocity, irregular.Velocity, 1e-10, "Irregular rendering intervals changed physical velocity.");
            irregular.Advance(60);
            Require(irregular.IsAtRest && irregular.Value == 7 && irregular.Velocity == 0,
                "The analytical spring failed to settle after a long rendering suspension.");
        }
        var veryLongPause = new SpringValue(0);
        veryLongPause.Retarget(1);
        veryLongPause.Advance(double.MaxValue);
        Require(veryLongPause.IsAtRest && veryLongPause.Value == 1,
            "A very long suspend produced an overflow instead of a stable resting state.");
    }

    private static SpringValue Simulate(SpringParameters parameters, int refreshRate) {
        var spring = new SpringValue(-3, parameters, 2);
        spring.Retarget(7);
        for (int frame = 0; frame < refreshRate / 2; frame++) spring.Advance(1.0 / refreshRate);
        return spring;
    }

    private static void CheckDriverLifecycle() {
        var owner = new Border();
        var clock = new ManualClock();
        bool allowAnimation = true;
        int writes = 0;
        double presentation = double.NaN;
        using var motion = new SpringMotion(owner, 0, value => { presentation = value; writes++; },
            MotionTokens.Spatial, clock, () => allowAnimation, () => true);
        Require(presentation == 0 && !motion.IsRunning && clock.Starts == 0, "An idle driver subscribed to rendering.");
        motion.Retarget(1);
        Require(motion.IsRunning && clock.Starts == 1 && clock.IsActive, "Active motion did not subscribe exactly once.");
        clock.Advance(.08);
        double value = motion.Value, velocity = motion.Velocity;
        motion.Retarget(0);
        Require(motion.Value == value && motion.Velocity == velocity && clock.Starts == 1,
            "The driver reset velocity or added another render subscription on reversal.");
        motion.Retarget(1);
        clock.Advance(2);
        Require(!motion.IsRunning && !clock.IsActive && presentation == 1 && motion.Velocity == 0,
            "The resting driver did not detach or publish its exact endpoint.");
        int settledWrites = writes;
        clock.Advance(.02);
        Require(writes == settledWrites, "Idle motion continued setting presentation values.");

        motion.Retarget(0);
        clock.Advance(.04);
        allowAnimation = false;
        clock.Advance(.01);
        Require(!motion.IsRunning && !clock.IsActive && motion.Value == 0,
            "A changed accessibility preference did not finish and detach active spatial motion.");
        int starts = clock.Starts;
        motion.Retarget(1);
        Require(motion.Value == 1 && motion.Velocity == 0 && clock.Starts == starts,
            "Reduced motion subscribed to a spatial animation.");
        allowAnimation = true;
        motion.Retarget(0, immediate: true);
        Require(motion.Value == 0 && clock.Starts == starts, "An immediate keyboard transition used the render loop.");

        motion.Retarget(1);
        clock.Advance(.03);
        owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(!motion.IsRunning && !clock.IsActive && motion.Value == 1,
            "Unloading a moving visual left its render subscription active.");
        motion.Retarget(0);
        motion.Dispose();
        Require(!clock.IsActive && !motion.IsRunning, "Disposing active motion did not detach rendering.");
        int disposedWrites = writes;
        owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        clock.Advance(.05);
        Require(writes == disposedWrites, "A disposed driver retained lifecycle/frame callbacks.");
        Throws<ObjectDisposedException>(() => motion.Retarget(1));

        var faultyClock = new ManualClock();
        bool throwFromSetter = false;
        using var faulty = new SpringMotion(new Border(), 0, _ => {
            if (throwFromSetter) throw new InvalidOperationException("Synthetic setter failure");
        }, MotionTokens.Spatial, faultyClock, () => true, () => true);
        faulty.Retarget(1);
        throwFromSetter = true;
        Throws<InvalidOperationException>(() => faultyClock.Advance(.01));
        Require(!faulty.IsRunning && !faultyClock.IsActive, "A failing presentation setter leaked its static rendering callback.");
    }

    private static void CheckPressFeedback() {
        var original = new TranslateTransform(4, 7);
        var origin = new Point(.2, .3);
        var button = new Button { RenderTransform = original, RenderTransformOrigin = origin };
        PressFeedback.SetIsEnabled(button, true);
        var group = button.RenderTransform as TransformGroup ??
            throw new InvalidOperationException("Pointer feedback did not create a transform group.");
        Require(group.Children.Count == 2 && ReferenceEquals(group.Children[0], original),
            "Pointer press feedback replaced the button's existing transform.");
        Require(button.RenderTransformOrigin == origin,
            "Enabling pointer feedback moved the existing transform's origin.");
        var scale = (ScaleTransform)group.Children[1];
        RaiseMouse(button, UIElement.PreviewMouseLeftButtonDownEvent);
        // Unloaded controls finish immediately, so this also checks the no-rendering fallback.
        Require(scale.ScaleX == MotionTokens.PressedScale && scale.ScaleY == MotionTokens.PressedScale,
            "The native button did not respond on pointer-down.");
        RaiseMouse(button, UIElement.PreviewMouseLeftButtonUpEvent);
        Require(scale.ScaleX == 1 && scale.ScaleY == 1, "Pointer-up did not release the native button.");
        RaiseMouse(button, UIElement.PreviewMouseLeftButtonDownEvent);
        button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(scale.ScaleX == 1 && scale.ScaleY == 1, "An unloaded pressed button retained a pressed transform.");
        PressFeedback.SetIsEnabled(button, false);
        Require(ReferenceEquals(button.RenderTransform, original) && button.RenderTransformOrigin == origin,
            "Disabling pointer feedback did not restore the original transform and origin.");
    }

    private static void RaiseMouse(Button button, RoutedEvent routedEvent) => button.RaiseEvent(
        new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routedEvent });

    private static void CheckStaticHandlerRelease() {
        WeakReference clock = CreateReleasedClock();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!clock.IsAlive, "CompositionTarget.Rendering retained a stopped clock through its static event.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedClock() {
        var clock = new CompositionMotionClock();
        clock.Start(_ => { });
        Require(clock.IsSubscribed, "The real WPF frame source did not subscribe.");
        clock.Stop();
        Require(!clock.IsSubscribed, "The real WPF frame source did not stop.");
        return new WeakReference(clock);
    }

    private sealed class ManualClock : IMotionClock {
        private Action<double>? _frame;
        public double TimestampSeconds { get; private set; }
        internal int Starts { get; private set; }
        internal bool IsActive => _frame != null;
        public void Start(Action<double> frame) {
            Require(_frame == null, "The driver subscribed a second frame callback.");
            _frame = frame; Starts++;
        }
        public void Stop() => _frame = null;
        internal void Advance(double seconds) { TimestampSeconds += seconds; _frame?.Invoke(TimestampSeconds); }
    }

    private static void Close(double expected, double actual, double tolerance, string message) =>
        Require(Math.Abs(expected - actual) <= tolerance, message + " Expected " + expected + ", actual " + actual + ".");
    private static void Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
