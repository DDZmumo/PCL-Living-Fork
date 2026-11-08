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

// Sparse decoration only; no input, work progress or layout animation.
internal sealed class ThemeStarfield : Canvas {
    private readonly List<AnimationClock> clocks = new();
    private readonly List<Point> positions = new();
    private readonly DispatcherTimer watcher;
    private bool moving;
    internal ThemeStarfield(int count) : this(count, Random.Shared) { }
    internal ThemeStarfield(int count, Random random) {
        IsHitTestVisible = false; ClipToBounds = true;
        for (int i = 0; i < count; i++) {
            Point position = new();
            for (int attempt = 0; attempt < 32; attempt++) {
                position = new Point(.03 + random.NextDouble() * .94, .08 + random.NextDouble() * .84);
                if (positions.TrueForAll(other => (other - position).Length > .1)) break;
            }
            positions.Add(position);
            double size = i % 4 == 0 ? 4 : i % 3 == 0 ? 2.2 : 1.4;
            Shape star = i % 4 == 0 ? new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,0 L6.1,3.9 10,5 6.1,6.1 5,10 3.9,6.1 0,5 3.9,3.9 Z"), Stretch = Stretch.Fill } : new Ellipse();
            star.Width = star.Height = size;
            star.Fill = new SolidColorBrush(i % 3 == 0 ? Color.FromRgb(190, 210, 255) : Color.FromRgb(215, 246, 255));
            star.Opacity = .18;
            double duration = 1.2 + random.NextDouble() * .6;
            var animation = new DoubleAnimation(.16, .66, TimeSpan.FromSeconds(duration)) {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var clock = (AnimationClock)animation.CreateClock(true); clock.Controller.Pause(); star.ApplyAnimationClock(OpacityProperty, clock); clocks.Add(clock);
            clock.Controller.Seek(TimeSpan.FromSeconds(random.NextDouble() * duration * 2), TimeSeekOrigin.BeginTime);
            Children.Add(star);
        }
        watcher = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        watcher.Tick += (_, _) => RefreshActivity();
        SizeChanged += (_, _) => { for (int i = 0; i < Children.Count; i++) {
            SetLeft(Children[i], Math.Max(0, ActualWidth - 6) * positions[i].X);
            SetTop(Children[i], Math.Max(0, ActualHeight - 6) * positions[i].Y);
        } };
        Loaded += (_, _) => RefreshActivity(); Unloaded += (_, _) => RefreshActivity(); IsVisibleChanged += (_, _) => RefreshActivity();
        ModTheme.WatchTheme(this, () => { Visibility = ModTheme.IsDarkTheme ? Visibility.Visible : Visibility.Collapsed; RefreshActivity(); });
        Visibility = ModTheme.IsDarkTheme ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshActivity() {
        bool visible = IsLoaded && IsVisible && ModTheme.IsDarkTheme;
        if (visible) watcher.Start(); else watcher.Stop();
        double speed = ModAnimation.AniSpeed;
        bool active = visible && ModAnimation.AniControlEnabled == 0 && speed > 0 && speed < 200 && SystemParameters.ClientAreaAnimation;
        foreach (var clock in clocks) {
            if (active) { clock.Controller.SpeedRatio = speed; if (!moving) clock.Controller.Resume(); }
            else clock.Controller.Pause();
        }
        moving = active;
    }
}
