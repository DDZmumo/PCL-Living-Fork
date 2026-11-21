// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Media;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>
/// Audio spectrum drawn in the body's backdrop: above the wallpaper, below the panes (whose frosted glass blurs it).
/// The spectrum runs from bass on the left to treble on the right. Bars: one rounded bar per band, standing on the bottom
/// edge from the left edge to the right. Line: a smooth wave through the spectrum with a soft glow and a filled body
/// fading upwards.
/// </summary>
internal sealed class GlassAudioVisualizer : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(GlassAudioVisualizer),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => {
            var spectrum = (GlassAudioVisualizer)d;
            spectrum._fill = null;
            spectrum._linePen = null;
            spectrum._glowPen = null;
            spectrum._area = null;
        }));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private const int Size = 2048;           // FFT window (~46 ms at 44.1 kHz)
    private const double Lag = .25;          // the device plays about this far behind what it has been handed
    private const double LowHz = 40, HighHz = 16000;
    private const double FloorDb = -60, RangeDb = 44;
    private const double BarWidth = 5, BarGap = 5, MaxHeightShare = .38;
    private const double LineSpacing = 40; // horizontal distance between the wave's control points

    private readonly float[] _samples = new float[Size];
    private readonly double[] _re = new double[Size], _im = new double[Size], _window = new double[Size];
    private readonly int[] _reverse = new int[Size];
    private double[] _levels = Array.Empty<double>();
    internal enum SpectrumStyle { Bars, Line }

    private SpectrumStyle _style;
    private bool _active, _hooked, _capturing;
    private double _presence; // 0–1, eases the whole spectrum in and out
    private TimeSpan? _lastFrame;
    private LinearGradientBrush? _fill;
    private Pen? _linePen, _glowPen;
    private LinearGradientBrush? _area;

    public GlassAudioVisualizer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        for (int index = 0; index < Size; index++)
        {
            _window[index] = .5 - .5 * Math.Cos(2 * Math.PI * index / (Size - 1));
            int reversed = 0;
            for (int bit = 0, value = index; bit < 11; bit++, value >>= 1) reversed = (reversed << 1) | (value & 1);
            _reverse[index] = reversed;
        }
        SetResourceReference(ForegroundProperty, "GlassTextBrush");
        IsVisibleChanged += (_, _) => UpdateHook();
        Unloaded += (_, _) => Unhook();
        Loaded += (_, _) => UpdateHook();
    }

    internal void Update(bool enabled, bool playing, SpectrumStyle style)
    {
        if (_style != style) { _style = style; InvalidateVisual(); }
        bool active = enabled && playing;
        Visibility visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_active == active && Visibility == visibility) return;
        _active = active;
        Visibility = visibility;
        if (!enabled) { _presence = 0; Array.Clear(_levels); }
        UpdateHook();
    }

    private void UpdateHook()
    {
        bool visible = IsLoaded && IsVisible && MotionPreferences.AllowsAnimation;
        SetCapture(visible && _active);
        bool want = visible && (_active || _presence > .001);
        if (want && !_hooked) { _hooked = true; _lastFrame = null; CompositionTarget.Rendering += OnFrame; }
        else if (!want) Unhook();
    }

    private void Unhook()
    {
        SetCapture(false);
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void SetCapture(bool capture)
    {
        if (_capturing == capture) return;
        _capturing = capture;
        ModMusic.MusicSamples.CaptureEnabled = capture;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame) return;
        if (_lastFrame == frame.RenderingTime) return; // Rendering can fire more than once per frame
        double dt = _lastFrame is { } previous ? Math.Clamp((frame.RenderingTime - previous).TotalSeconds, 0, .1) : 1 / 60d;
        _lastFrame = frame.RenderingTime;

        int count = BarCount(ActualWidth); // one band per bar (the line samples the same bands)
        if (_levels.Length != count) _levels = new double[count];
        _presence += ((_active ? 1 : 0) - _presence) * (1 - Math.Exp(-dt / (_active ? .35 : .5)));
        bool fresh = _active && ModMusic.MusicSamples.Snapshot(_samples, Lag);
        if (fresh) Analyse(ModMusic.MusicSamples.SampleRate, dt);
        else for (int index = 0; index < count; index++) _levels[index] = Math.Max(0, _levels[index] - dt * 1.2);
        InvalidateVisual();
        if (!_active && _presence < .002) { _presence = 0; Array.Clear(_levels); Unhook(); }
    }

    private static int BarCount(double width) => Math.Clamp((int)((width + BarGap) / (BarWidth + BarGap)), 0, 200);

    // Hann-windowed FFT, then the strongest bin within each bar's logarithmic band, in decibels.
    private void Analyse(int sampleRate, double dt)
    {
        for (int index = 0; index < Size; index++) { _re[_reverse[index]] = _samples[index] * _window[index]; _im[_reverse[index]] = 0; }
        for (int span = 2; span <= Size; span <<= 1)
        {
            double angle = -2 * Math.PI / span;
            double stepRe = Math.Cos(angle), stepIm = Math.Sin(angle);
            for (int start = 0; start < Size; start += span)
            {
                double wRe = 1, wIm = 0;
                for (int k = 0; k < span / 2; k++)
                {
                    int even = start + k, odd = even + span / 2;
                    double tRe = wRe * _re[odd] - wIm * _im[odd], tIm = wRe * _im[odd] + wIm * _re[odd];
                    _re[odd] = _re[even] - tRe; _im[odd] = _im[even] - tIm;
                    _re[even] += tRe; _im[even] += tIm;
                    double nextRe = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe; wRe = nextRe;
                }
            }
        }

        int count = _levels.Length;
        double binHz = (double)sampleRate / Size, top = Math.Min(HighHz, sampleRate / 2d);
        double attack = 1 - Math.Exp(-dt / .045), release = dt * 1.1;
        for (int bar = 0; bar < count; bar++)
        {
            double fromHz = LowHz * Math.Pow(top / LowHz, (double)bar / count);
            double toHz = LowHz * Math.Pow(top / LowHz, (bar + 1d) / count);
            int from = Math.Max(1, (int)Math.Floor(fromHz / binHz)), to = Math.Max(from, (int)Math.Ceiling(toHz / binHz));
            double peak = 0;
            for (int bin = from; bin <= to && bin < Size / 2; bin++) peak = Math.Max(peak, _re[bin] * _re[bin] + _im[bin] * _im[bin]);
            double amplitude = Math.Sqrt(peak) * 4 / Size; // a full-scale sine reads 1
            double centre = Math.Sqrt(fromHz * toHz);
            double db = 20 * Math.Log10(amplitude + 1e-9) + 3 * Math.Log2(centre / 1000); // treble tilt: music falls off upwards
            double target = Math.Clamp((db - FloorDb) / RangeDb, 0, 1);
            target = Math.Pow(target, 1.15); // keep quiet bands low, let loud ones reach high
            _levels[bar] = target > _levels[bar] ? _levels[bar] + (target - _levels[bar]) * attack : Math.Max(target, _levels[bar] - release);
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        int bands = _levels.Length, count = BarCount(ActualWidth);
        double width = ActualWidth, height = ActualHeight;
        if (bands == 0 || count == 0 || _presence <= .001 || width <= 0 || height <= 0) return;
        double maxHeight = height * MaxHeightShare;
        if (_style == SpectrumStyle.Line)
        {
            DrawLine(context, count, width, height, maxHeight);
            return;
        }
        // The gaps stretch a little so the first and last bars meet the left and right edges exactly.
        double gap = count > 1 ? (width - count * BarWidth) / (count - 1) : 0;
        double left = count > 1 ? 0 : (width - BarWidth) / 2, bottom = height;
        _fill ??= Gradient();
        _fill.StartPoint = new Point(0, bottom - maxHeight);
        _fill.EndPoint = new Point(0, bottom);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext stream = geometry.Open())
        {
            double radius = BarWidth / 2;
            for (int bar = 0; bar < count; bar++)
            {
                double level = LevelAt(bar, count);
                if (level < .015) continue; // silence draws nothing
                double barHeight = BarWidth + level * (maxHeight - BarWidth);
                double x = left + bar * (BarWidth + gap), y = bottom - barHeight;
                // Rounded top, square foot standing on the edge.
                stream.BeginFigure(new Point(x, y + radius), true, true);
                stream.ArcTo(new Point(x + BarWidth, y + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                stream.LineTo(new Point(x + BarWidth, bottom), true, false);
                stream.LineTo(new Point(x, bottom), true, false);
            }
        }
        geometry.Freeze();
        context.PushOpacity(_presence);
        context.DrawGeometry(_fill, null, geometry);
        context.Pop();
    }

    // Level of bar `index` of `count` (bass at index 0); the outermost few bars taper so the row melts into the corners.
    private double LevelAt(int index, int count)
    {
        int band = Math.Clamp((int)((double)index / count * _levels.Length), 0, _levels.Length - 1);
        double edge = Math.Min(index + 1, count - index) / Math.Min(5d, count / 2d);
        return _levels[band] * _presence * Math.Min(1, .55 + .45 * edge);
    }

    // Level at a horizontal fraction (0 = left / bass, 1 = right / treble), weighted towards the loudest band it spans.
    private double LevelAtFraction(double from, double to)
    {
        int length = _levels.Length;
        int first = Math.Clamp((int)(from * length), 0, length - 1), last = Math.Clamp((int)Math.Ceiling(to * length) - 1, first, length - 1);
        double peak = 0, sum = 0;
        for (int band = first; band <= last; band++) { peak = Math.Max(peak, _levels[band]); sum += _levels[band]; }
        double level = .6 * peak + .4 * sum / (last - first + 1);
        double edge = Math.Min(from + to, 2 - from - to) / 2 / .06; // taper the outer 6 %
        return level * _presence * Math.Min(1, .5 + .5 * edge);
    }

    // A smooth wave (Catmull-Rom through evenly spaced points) with a filled body fading upwards, a soft glow and a crisp line.
    private void DrawLine(DrawingContext context, int count, double width, double height, double maxHeight)
    {
        int points = Math.Max(8, (int)Math.Round(width / LineSpacing) + 1);
        double step = width / (points - 1), baseline = height - 2;
        var samples = new Point[points];
        bool any = false;
        for (int index = 0; index < points; index++)
        {
            double from = Math.Max(0, (index - .5) / (points - 1)), to = Math.Min(1, (index + .5) / (points - 1));
            double level = LevelAtFraction(from, to);
            any |= level >= .01;
            samples[index] = new Point(index * step, baseline - level * Math.Max(0, maxHeight - 2));
        }
        if (!any) return;

        var wave = new StreamGeometry();
        var body = new StreamGeometry();
        using (StreamGeometryContext line = wave.Open())
        using (StreamGeometryContext fill = body.Open())
        {
            line.BeginFigure(samples[0], false, false);
            fill.BeginFigure(new Point(0, height), true, true);
            fill.LineTo(samples[0], false, false);
            for (int index = 0; index < points - 1; index++)
            {
                Point p0 = samples[Math.Max(0, index - 1)], p1 = samples[index], p2 = samples[index + 1], p3 = samples[Math.Min(points - 1, index + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                line.BezierTo(c1, c2, p2, true, true);
                fill.BezierTo(c1, c2, p2, false, true);
            }
            fill.LineTo(new Point(width, height), false, false);
        }
        wave.Freeze();
        body.Freeze();

        Color color = Foreground is SolidColorBrush solid ? solid.Color : Colors.White;
        _area ??= new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, GradientStops = {
            new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0),
            new GradientStop(Color.FromArgb(78, color.R, color.G, color.B), .65),
            new GradientStop(Color.FromArgb(120, color.R, color.G, color.B), 1) } };
        _area.StartPoint = new Point(0, height - maxHeight);
        _area.EndPoint = new Point(0, height);
        if (_glowPen is null)
        {
            _glowPen = new Pen(new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _glowPen.Freeze();
        }
        if (_linePen is null)
        {
            _linePen = new Pen(new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _linePen.Freeze();
        }
        context.PushOpacity(_presence);
        context.DrawGeometry(_area, null, body);
        context.DrawGeometry(null, _glowPen, wave);
        context.DrawGeometry(null, _linePen, wave);
        context.Pop();
    }

    // Theme text colour: faint at the top of the tallest bar, firmer at the base.
    private LinearGradientBrush Gradient()
    {
        Color color = Foreground is SolidColorBrush solid ? solid.Color : Colors.White;
        return new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(18, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(70, color.R, color.G, color.B), .65),
                new GradientStop(Color.FromArgb(105, color.R, color.G, color.B), 1)
            }
        };
    }
}
