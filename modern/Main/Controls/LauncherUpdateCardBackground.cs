// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PCL;

// This background belongs only to the launcher identity card. Native clocks
// animate brush transforms; no layout, file work or update state is animated.
internal sealed class LauncherUpdateCardBackground : Grid {
    private readonly Rectangle first = new(), second = new(), third = new();
    private readonly TranslateTransform firstFlow = new(), secondFlow = new();
    private readonly TranslateTransform thirdFlow = new();
    private readonly List<AnimationClock> clocks = new();
    private readonly DispatcherTimer settingsWatcher;
    private bool moving;
    private double speed = -1;

    internal LauncherUpdateCardBackground() {
        IsHitTestVisible = false;
        Children.Add(first); Children.Add(second); Children.Add(third); Children.Add(new ThemeStarfield(16));
        Animate(firstFlow, TranslateTransform.XProperty, -0.32, 0.32, 8);
        Animate(firstFlow, TranslateTransform.YProperty, 0.24, -0.24, 8);
        Animate(secondFlow, TranslateTransform.XProperty, 0.3, -0.3, 11);
        Animate(secondFlow, TranslateTransform.YProperty, -0.2, 0.2, 11);
        Animate(thirdFlow, TranslateTransform.XProperty, -.25, .2, 13);
        Animate(thirdFlow, TranslateTransform.YProperty, .15, -.15, 13);
        settingsWatcher = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        settingsWatcher.Tick += (_, _) => RefreshMotion();
        SizeChanged += (_, args) => Clip = new RectangleGeometry(new Rect(new Point(), args.NewSize), 5, 5);
        ModTheme.WatchTheme(this, RefreshPalette);
        Loaded += (_, _) => RefreshActivity();
        Unloaded += (_, _) => RefreshActivity();
        IsVisibleChanged += (_, _) => RefreshActivity();
        RefreshPalette();
    }
    private void Animate(Animatable target, DependencyProperty property, double from, double to, double seconds) {
        var animation = new DoubleAnimationUsingKeyFrames {
            Duration = TimeSpan.FromSeconds(seconds), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), new KeySpline(0.42, 0, 0.58, 1)));
        var clock = (AnimationClock)animation.CreateClock(true);
        clock.Controller.Pause(); target.ApplyAnimationClock(property, clock); clocks.Add(clock);
    }
    private void RefreshActivity() {
        if (IsLoaded && IsVisible) { settingsWatcher.Start(); RefreshMotion(); }
        else { settingsWatcher.Stop(); PauseMotion(); }
    }
    private void RefreshMotion() {
        double currentSpeed = ModAnimation.AniSpeed;
        bool allowed = IsLoaded && IsVisible && ModAnimation.AniControlEnabled == 0 && currentSpeed < 200 && SystemParameters.ClientAreaAnimation;
        if (!allowed) { PauseMotion(); return; }
        if (speed != currentSpeed) {
            speed = currentSpeed;
            foreach (var clock in clocks) clock.Controller.SpeedRatio = currentSpeed;
        }
        if (moving) return;
        foreach (var clock in clocks) clock.Controller.Resume();
        moving = true;
    }
    private void PauseMotion() {
        foreach (var clock in clocks) clock.Controller.Pause();
        moving = false;
    }
    private void RefreshPalette() {
        bool dark = ModTheme.IsDarkTheme;
        Background = new LinearGradientBrush {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection {
                new(dark ? Color.FromRgb(11, 23, 45) : Color.FromRgb(183, 222, 255), 0),
                new(dark ? Color.FromRgb(24, 39, 76) : Color.FromRgb(248, 252, 255), .48),
                new(dark ? Color.FromRgb(38, 56, 92) : Color.FromRgb(196, 206, 250), 1)
            }
        };
        Color blue = dark ? Color.FromArgb(110, 45, 117, 199) : Color.FromArgb(150, 70, 145, 245);
        Color clear = Color.FromArgb(0, blue.R, blue.G, blue.B);
        first.Fill = new LinearGradientBrush {
            StartPoint = new Point(0, 1), EndPoint = new Point(1, 0), RelativeTransform = firstFlow,
            GradientStops = new GradientStopCollection { new(clear, 0), new(blue, .45), new(clear, 1) }
        };
        second.Fill = Glow(dark ? Color.FromArgb(100, 93, 77, 161) : Color.FromArgb(160, 190, 171, 247), new Point(.78, .55), secondFlow);
        third.Fill = Glow(dark ? Color.FromArgb(90, 34, 122, 145) : Color.FromArgb(170, 134, 225, 237), new Point(.22, .65), thirdFlow);
    }
    private static Brush Glow(Color color, Point center, Transform flow) {
        var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = 0.55, RadiusY = 1.0, RelativeTransform = flow };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        return brush;
    }
}
