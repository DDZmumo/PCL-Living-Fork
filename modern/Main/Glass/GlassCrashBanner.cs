// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>
/// The playful crash banner shown above the "Minecraft 出现错误" dialog: a winking round face pops in, a thumbs-up
/// swings in front of it and gives a few quick taps, then "干得好啊" slides in and "您的游戏崩溃啦!!!" slams down with a
/// little shake. Afterwards the face bobs gently and the thumb taps again now and then. Layout follows the artwork:
/// face on the left, the thumb over its lower right, two lines of text tilted slightly upwards to the right.
/// </summary>
internal sealed class GlassCrashBanner : Viewbox
{
    // Design canvas (units): face 200 wide; everything else placed relative to it as in the original artwork.
    private const double Width0 = 1030, Height0 = 270;
    private readonly Canvas _canvas = new() { Width = Width0, Height = Height0, ClipToBounds = false };
    private readonly FrameworkElement _face, _hand, _praise, _crashed;
    private readonly ScaleTransform _faceScale = new(1, 1), _handScale = new(1, 1), _praiseScale = new(1, 1), _crashedScale = new(1, 1);
    private readonly RotateTransform _faceTurn = new(), _handTurn = new(), _handWiggle = new();
    private readonly TranslateTransform _faceBob = new(), _handShift = new(), _praiseShift = new(), _shake = new();
    private readonly SpringMotion _faceIn, _handIn, _praiseIn, _crashedIn, _out;
    private readonly Stopwatch _clock = new();
    private double _wiggleStart = double.NaN, _shakeStart = double.NaN, _nextWiggle = double.NaN;
    private bool _landed, _running;
    private Action? _gone;

    internal GlassCrashBanner()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        IsHitTestVisible = false;
        Child = _canvas;
        _canvas.RenderTransform = _shake;

        _face = Picture("Face.png", 200, 0, 50,
            new TransformGroup { Children = { _faceScale, _faceTurn, _faceBob } }, new Point(.5, .5));
        _praise = Picture("Praise.png", 272, 178, 40,
            new TransformGroup { Children = { _praiseScale, new RotateTransform(-3.5), _praiseShift } }, new Point(0, 1));
        _crashed = Picture("Crashed.png", 820, 194, 106,
            new TransformGroup { Children = { _crashedScale, new RotateTransform(-2.5) } }, new Point(.3, .6));
        // The thumb pivots at its wrist (lower left).
        _hand = Picture("Hand.png", 94, 134, 160,
            new TransformGroup { Children = { _handScale, _handTurn, _handWiggle, _handShift } }, new Point(.15, .9));

        // As in the artwork: the thumb lies over the face but under the lettering.
        foreach (FrameworkElement part in new[] { _face, _hand, _praise, _crashed }) { part.Opacity = 0; _canvas.Children.Add(part); }

        _faceIn = new SpringMotion(this, 0, value =>
        {
            _face.Opacity = Math.Clamp(value * 2, 0, 1);
            _faceScale.ScaleX = _faceScale.ScaleY = .25 + .75 * value;
            _faceTurn.Angle = -32 * (1 - value);
        }, new SpringParameters(.5, .58));
        _handIn = new SpringMotion(this, 0, value =>
        {
            _hand.Opacity = Math.Clamp(value * 2, 0, 1);
            _handScale.ScaleX = _handScale.ScaleY = .55 + .45 * value;
            _handTurn.Angle = 48 * (1 - value);
            _handShift.X = 70 * (1 - value); _handShift.Y = 90 * (1 - value);
            if (value > .96 && double.IsNaN(_wiggleStart)) _wiggleStart = Seconds;
        }, new SpringParameters(.42, .62));
        _praiseIn = new SpringMotion(this, 0, value =>
        {
            _praise.Opacity = Math.Clamp(value * 1.6, 0, 1);
            _praiseScale.ScaleX = .35 + .65 * value; _praiseScale.ScaleY = .7 + .3 * value;
            _praiseShift.X = -46 * (1 - value);
        }, new SpringParameters(.4, .66));
        _crashedIn = new SpringMotion(this, 0, value =>
        {
            _crashed.Opacity = Math.Clamp(value * 2.2, 0, 1);
            _crashedScale.ScaleX = _crashedScale.ScaleY = 1.9 - .9 * value;
            // The moment it lands the whole banner shakes once, as if hit.
            if (!_landed && value > .97) { _landed = true; _shakeStart = Seconds; _nextWiggle = Seconds + 2.6; }
        }, new SpringParameters(.36, .6));
        _out = new SpringMotion(this, 1, value =>
        {
            Opacity = Math.Clamp(value, 0, 1);
            var scale = .94 + .06 * value;
            RenderTransformOrigin = new Point(.5, .5);
            RenderTransform = new ScaleTransform(scale, scale);
            if (value <= .01 && _gone is { } gone) { _gone = null; Stop(); gone(); }
        }, new SpringParameters(.3, 1));
        Unloaded += (_, _) => Stop();
    }

    private double Seconds => _clock.Elapsed.TotalSeconds;

    private FrameworkElement Picture(string file, double width, double left, double top, Transform transform, Point origin)
    {
        var source = new BitmapImage(new Uri("pack://application:,,,/LivingPCL;component/Images/Crash/" + file));
        var image = new Image { Source = source, Width = width, Stretch = Stretch.Uniform, RenderTransform = transform, RenderTransformOrigin = origin };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, left); Canvas.SetTop(image, top);
        return image;
    }

    /// <summary>Plays the entrance: face, thumb (with its taps), the first line, then the second line's slam.</summary>
    internal void Play()
    {
        if (!MotionPreferences.AllowsAnimation)
        {
            foreach (SpringMotion motion in new[] { _faceIn, _handIn, _praiseIn, _crashedIn }) motion.Snap(1);
            return;
        }
        _running = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
        At(0, () => _faceIn.Retarget(1));
        At(.32, () => _handIn.Retarget(1));
        At(.78, () => _praiseIn.Retarget(1));
        At(1.12, () => _crashedIn.Retarget(1));
    }

    /// <summary>Fades and shrinks the banner away, then calls <paramref name="gone"/>.</summary>
    internal void Dismiss(Action gone)
    {
        _gone = gone;
        if (!MotionPreferences.AllowsAnimation) { _out.Snap(0); return; }
        _out.Retarget(0);
    }

    private void At(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); if (_running) action(); };
        timer.Start();
    }

    // Wiggle: three quick taps of the thumb (a damped sine about the wrist). Shake: a short damped sideways jolt.
    // Idle: the face bobs slowly; the thumb taps again every few seconds.
    private void OnFrame(object? sender, EventArgs args)
    {
        double now = Seconds;
        if (!double.IsNaN(_wiggleStart))
        {
            double t = now - _wiggleStart;
            if (t < .9) _handWiggle.Angle = -16 * Math.Sin(t * Math.PI * 2 * 3.3) * (1 - t / .9);
            else { _handWiggle.Angle = 0; _wiggleStart = double.NaN; }
        }
        if (!double.IsNaN(_shakeStart))
        {
            double t = now - _shakeStart;
            if (t < .42) _shake.X = 9 * Math.Sin(t * Math.PI * 2 * 7) * (1 - t / .42);
            else { _shake.X = 0; _shakeStart = double.NaN; }
        }
        if (_landed)
        {
            _faceBob.Y = 3.5 * Math.Sin(now * 2.1);
            if (!double.IsNaN(_nextWiggle) && now >= _nextWiggle && double.IsNaN(_wiggleStart)) { _wiggleStart = now; _nextWiggle = now + 3.8; }
        }
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
    }
}
