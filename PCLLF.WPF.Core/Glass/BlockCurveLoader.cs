// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using PCLLF.Wpf.Motion;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// An indeterminate loading indicator shaped like an isometric block: a faint cube outline (hexagonal silhouette
/// with the three inner edges meeting at the centre) and a comet of dots running around the hexagonal contour.
/// The comet follows the Math Curve Loaders idea (a head with a trail whose size and opacity fall off as
/// (1 − offset)^0.56); the outline breathes slightly. Independent implementation.
/// Rendering runs per frame only while loaded, visible and active; reduced motion shows a single still frame.
/// </summary>
public sealed class BlockCurveLoader : FrameworkElement {
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(BlockCurveLoader),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BlockCurveLoader), new FrameworkPropertyMetadata(true, OnActivityChanged));

    private const int Particles = 44;
    private const double TrailSpan = .32, TravelPeriod = 2.6, PulsePeriod = 3.2, Breath = .04;
    private const double StillProgress = .1;

    private readonly Stopwatch _clock = new();
    private Brush _topFace = Brushes.Transparent, _sideFace = Brushes.Transparent;
    private Brush[] _dots = [];
    private Pen _outline = new(Brushes.White, 1);
    private bool _subscribed;

    public BlockCurveLoader() {
        Width = 48; Height = 48;
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => UpdateSubscription();
        IsVisibleChanged += (_, _) => UpdateSubscription();
        UpdatePalette(Colors.White);
    }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>False pauses the animation (e.g. while hidden behind a determinate progress bar).</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private static void OnAppearanceChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        => ((BlockCurveLoader)element).UpdatePalette((args.NewValue as SolidColorBrush)?.Color ?? Colors.White);
    private static void OnActivityChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) => ((BlockCurveLoader)element).UpdateSubscription();

    private void UpdatePalette(Color color) {
        var dots = new Brush[Particles];
        for (int index = 0; index < Particles; index++) {
            double offset = index / (double)(Particles - 1);
            double fade = Math.Pow(1 - offset, .56);
            byte alpha = (byte)Math.Round(255 * (.04 + .96 * fade));
            dots[index] = Frozen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
        }
        _dots = dots;
        // A lit top face and a dimmer side keep the outline reading as a solid block.
        _topFace = Frozen(new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)));
        _sideFace = Frozen(new SolidColorBrush(Color.FromArgb(10, color.R, color.G, color.B)));
        _outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(56, color.R, color.G, color.B))), 1.2) {
            LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        });
    }

    private bool Animating => IsLoaded && IsVisible && IsActive && MotionPreferences.AllowsAnimation;

    private void UpdateSubscription() {
        bool animate = Animating;
        if (animate == _subscribed) { InvalidateVisual(); return; }
        _subscribed = animate;
        if (animate) { _clock.Start(); CompositionTarget.Rendering += OnRendering; }
        else { _clock.Stop(); CompositionTarget.Rendering -= OnRendering; }
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs args) => InvalidateVisual();

    protected override void OnRender(DrawingContext context) {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 12) return;
        bool animate = _subscribed;
        double time = animate ? _clock.Elapsed.TotalSeconds : 0;
        double head = animate ? time / TravelPeriod % 1 : StillProgress;
        double pulse = (Math.Sin(time / PulsePeriod * Math.PI * 2) + 1) / 2;
        double maxDot = size * .035; // head dot radius; the trail shrinks to a quarter of it
        double radius = (size / 2 - maxDot) * (1 - Breath + Breath * pulse);
        Point centre = new(ActualWidth / 2, ActualHeight / 2);

        // Pointy-top hexagon = isometric cube silhouette: top, upper-right, lower-right, bottom, lower-left, upper-left.
        var corners = new Point[6];
        for (int index = 0; index < 6; index++) {
            double angle = (-90 + 60 * index) * Math.PI / 180;
            corners[index] = new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
        }
        context.DrawGeometry(_topFace, null, Polygon(corners[5], corners[0], corners[1], centre));
        context.DrawGeometry(_sideFace, null, Polygon(corners[1], corners[2], corners[3], centre));
        context.DrawGeometry(null, _outline, Polygon(corners));
        context.DrawLine(_outline, centre, corners[1]);
        context.DrawLine(_outline, centre, corners[3]);
        context.DrawLine(_outline, centre, corners[5]);

        // Comet around the contour, tail first so the head is drawn on top.
        Point At(double progress) {
            double position = (progress % 1 + 1) % 1 * 6;
            int edge = (int)position;
            double t = position - edge;
            Point from = corners[edge % 6], to = corners[(edge + 1) % 6];
            return new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }
        for (int index = Particles - 1; index >= 0; index--) {
            double offset = index / (double)(Particles - 1);
            double fade = Math.Pow(1 - offset, .56);
            double dot = maxDot * (.25 + .75 * fade);
            context.DrawEllipse(_dots[index], null, At(head - offset * TrailSpan), dot, dot);
        }
    }

    private static Geometry Polygon(params Point[] points) {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open()) {
            path.BeginFigure(points[0], true, true);
            path.PolyLineTo(points[1..], true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
