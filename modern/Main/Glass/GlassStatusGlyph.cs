// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>
/// Animated status mark: a regular hexagon that draws itself in, then its sign. <see cref="Kind.Failed"/> writes an ×
/// stroke by stroke and gives a short shake (loading failed); <see cref="Kind.Empty"/> writes a ? and drops its dot in
/// with a bounce, then tilts once (nothing in the list). It plays each time it appears on screen.
/// </summary>
internal sealed class GlassStatusGlyph : Viewbox
{
    internal enum Kind { Failed, Empty }

    private const double Size = 64, Stroke = 3.2;
    private readonly Kind _kind;
    private readonly Path _hexagon, _first, _second;
    private readonly Ellipse? _dot;
    private readonly ScaleTransform _scale = new(1, 1), _dotScale = new(1, 1);
    private readonly RotateTransform _turn = new(), _tilt = new();
    private readonly double _hexLength, _firstLength, _secondLength;
    private SpringMotion? _hexMotion, _firstMotion, _secondMotion, _dotMotion, _tiltMotion;

    internal GlassStatusGlyph(Kind kind, double size = 56, string signBrush = "GlassPresentationText",
        string outlineBrush = "GlassPresentationSecondary")
    {
        _kind = kind;
        Width = Height = size;
        var canvas = new Canvas { Width = Size, Height = Size, RenderTransformOrigin = new Point(.5, .5) };
        canvas.RenderTransform = new TransformGroup { Children = { _scale, _turn, _tilt } };
        Child = canvas;

        // Pointy-top hexagon, circumradius 26 around the centre.
        var hex = new PathFigure { IsClosed = true };
        for (int index = 0; index < 6; index++)
        {
            double angle = Math.PI / 180 * (-90 + 60 * index);
            var point = new Point(32 + 26 * Math.Cos(angle), 32 + 26 * Math.Sin(angle));
            if (index == 0) hex.StartPoint = point; else hex.Segments.Add(new LineSegment(point, true));
        }
        _hexLength = 6 * 26;
        _hexagon = Line(new PathGeometry(new[] { hex }), outlineBrush, _hexLength);

        if (kind == Kind.Failed)
        {
            _first = Line(Geometry.Parse("M23.5,23.5 L40.5,40.5"), "GlassPresentationDanger", _firstLength = 24.1);
            _second = Line(Geometry.Parse("M40.5,23.5 L23.5,40.5"), "GlassPresentationDanger", _secondLength = 24.1);
        }
        else
        {
            // The hook of the question mark (two curves), then the dot below it.
            _first = Line(Geometry.Parse("M25,26 C25,17.5 39,17.5 39,25 C39,30.5 32,30 32,36.5"), signBrush, _firstLength = 38);
            _second = Line(Geometry.Parse("M32,36.5 L32,36.5"), signBrush, _secondLength = 1);
            _second.Visibility = Visibility.Collapsed;
            _dot = new Ellipse { Width = 5, Height = 5, RenderTransformOrigin = new Point(.5, .5), RenderTransform = _dotScale };
            _dot.SetResourceReference(Shape.FillProperty, signBrush);
            Canvas.SetLeft(_dot, 29.5); Canvas.SetTop(_dot, 41);
        }
        canvas.Children.Add(_hexagon); canvas.Children.Add(_first); canvas.Children.Add(_second);
        if (_dot is not null) canvas.Children.Add(_dot);
        Reset();
        IsVisibleChanged += (_, _) => { if (IsVisible) Play(); else Reset(); };
    }

    private static Path Line(Geometry data, string brushKey, double length)
    {
        var path = new Path { Data = data, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        // Dash pattern in stroke widths: one dash as long as the line, offset away until it draws in.
        double units = length / Stroke + 1;
        path.StrokeDashArray = new DoubleCollection { units, units };
        path.StrokeDashOffset = units;
        return path;
    }

    private static void Draw(Path path, double length, double progress) =>
        path.StrokeDashOffset = (length / Stroke + 1) * (1 - Math.Clamp(progress, 0, 1));

    private void Reset()
    {
        foreach (SpringMotion? motion in new[] { _hexMotion, _firstMotion, _secondMotion, _dotMotion, _tiltMotion }) motion?.Dispose();
        _hexMotion = _firstMotion = _secondMotion = _dotMotion = _tiltMotion = null;
        Draw(_hexagon, _hexLength, 0); Draw(_first, _firstLength, 0); Draw(_second, _secondLength, 0);
        _dotScale.ScaleX = _dotScale.ScaleY = 0;
        _scale.ScaleX = _scale.ScaleY = .7; _turn.Angle = -30; _tilt.Angle = 0;
        Opacity = 0;
    }

    private void Play()
    {
        Reset();
        if (!MotionPreferences.AllowsAnimation)
        {
            Opacity = 1; _scale.ScaleX = _scale.ScaleY = 1; _turn.Angle = 0;
            Draw(_hexagon, _hexLength, 1); Draw(_first, _firstLength, 1); Draw(_second, _secondLength, 1);
            _dotScale.ScaleX = _dotScale.ScaleY = 1;
            return;
        }
        // 1. The hexagon grows and turns into place while its outline draws around.
        _hexMotion = new SpringMotion(this, 0, value =>
        {
            Opacity = Math.Clamp(value * 2.5, 0, 1);
            _scale.ScaleX = _scale.ScaleY = .7 + .3 * value;
            _turn.Angle = -30 * (1 - value);
            Draw(_hexagon, _hexLength, value * 1.15);
        }, new SpringParameters(.55, .72));
        _hexMotion.Retarget(1);
        // 2. The sign is written.
        _firstMotion = new SpringMotion(this, 0, value => Draw(_first, _firstLength, value), new SpringParameters(.32, 1));
        _secondMotion = new SpringMotion(this, 0, value => Draw(_second, _secondLength, value), new SpringParameters(.28, 1));
        After(.34, () => _firstMotion?.Retarget(1));
        if (_kind == Kind.Failed)
        {
            After(.52, () => _secondMotion?.Retarget(1));
            // 3. A short "no" shake once the × is complete.
            _tiltMotion = new SpringMotion(this, 0, value => _tilt.Angle = value, new SpringParameters(.18, .22));
            After(.78, () => { if (_tiltMotion is null) return; _tiltMotion.Snap(9); _tiltMotion.Retarget(0); });
        }
        else
        {
            // 3. The dot drops in with a bounce, then the whole mark tilts once, as if wondering.
            _dotMotion = new SpringMotion(this, 0, value => _dotScale.ScaleX = _dotScale.ScaleY = Math.Max(0, value), new SpringParameters(.34, .5));
            After(.66, () => _dotMotion?.Retarget(1));
            _tiltMotion = new SpringMotion(this, 0, value => _tilt.Angle = value, new SpringParameters(.6, .45));
            After(.9, () => _tiltMotion?.Retarget(-10));
            After(1.5, () => _tiltMotion?.Retarget(0));
        }
    }

    private void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); if (IsVisible) action(); };
        timer.Start();
    }
}
