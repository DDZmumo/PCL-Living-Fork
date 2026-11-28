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

    // Two resolutions: a short window keeps the mids and treble quick, a long one separates the notes of the bass
    // (2048 samples resolve ~21 Hz, too coarse below ~200 Hz; 8192 resolve ~5 Hz). Both are centred on the playhead.
    private const int ShortSize = 2048, LongSize = 8192;
    private const double CrossLowHz = 180, CrossHighHz = 360; // long window below, short above, blended in between
    private const double Lag = .25;          // fallback only: the tap reads at the device playhead when it can
    private const double LowHz = 40, HighHz = 16000;
    private const double FloorDb = -60, RangeDb = 44;
    private const double BarWidth = 5, BarGap = 5, MaxHeightShare = .38;
    private const double LineSpacing = 40; // horizontal distance between the wave's control points

    private readonly float[] _samples = new float[LongSize];
    private readonly Spectrum _short = new(ShortSize), _long = new(LongSize);
    private double[] _levels = Array.Empty<double>();
    private Band[] _bands = Array.Empty<Band>();
    private int _bandRate;
    private bool _longDue = true;
    private double[] _wave = Array.Empty<double>();
    private Point[] _points = Array.Empty<Point>();
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
        bool fresh = _active && ModMusic.MusicSamples.Snapshot(_samples, Lag, LongSize / 2);
        if (fresh) Analyse(ModMusic.MusicSamples.SampleRate, dt);
        else {
            double fall = Math.Exp(-dt / .18);
            for (int index = 0; index < count; index++) _levels[index] *= fall;
        }
        InvalidateVisual();
        if (!_active && _presence < .002) { _presence = 0; Array.Clear(_levels); Unhook(); }
    }

    private static int BarCount(double width) => Math.Clamp((int)((width + BarGap) / (BarWidth + BarGap)), 0, 200);

    // One band per bar on a logarithmic (pitch) scale, with its window blend and treble tilt worked out once.
    private readonly record struct Band(double FromHz, double ToHz, double LongShare, double TiltDb);

    private void PrepareBands(int sampleRate)
    {
        int count = _levels.Length;
        if (_bands.Length == count && _bandRate == sampleRate) return;
        _bandRate = sampleRate;
        _bands = new Band[count];
        double top = Math.Min(HighHz, sampleRate / 2d);
        for (int bar = 0; bar < count; bar++)
        {
            double fromHz = LowHz * Math.Pow(top / LowHz, (double)bar / count), toHz = LowHz * Math.Pow(top / LowHz, (bar + 1d) / count);
            double centre = Math.Sqrt(fromHz * toHz);
            double share = Math.Clamp((CrossHighHz - centre) / (CrossHighHz - CrossLowHz), 0, 1);
            _bands[bar] = new Band(fromHz, toHz, share, 3 * Math.Log2(centre / 1000)); // treble tilt: music falls off upwards
        }
    }

    private void Analyse(int sampleRate, double dt)
    {
        PrepareBands(sampleRate);
        // The short window is the middle of the snapshot, so both windows sit on the playhead.
        _short.Transform(_samples, (LongSize - ShortSize) / 2, ShortSize / 2);
        // Bass moves slowly: the long transform runs every other frame and keeps only the bins below the crossover.
        if (_longDue) _long.Transform(_samples, 0, (int)(CrossHighHz * 1.5 * LongSize / sampleRate) + 2);
        _longDue = !_longDue;

        // Fast rise to follow onsets, a quick exponential fall so beats separate instead of smearing together.
        double attack = 1 - Math.Exp(-dt / .018), release = 1 - Math.Exp(-dt / .12);
        for (int bar = 0; bar < _levels.Length; bar++)
        {
            Band band = _bands[bar];
            double amplitude = band.LongShare >= 1 ? _long.Amplitude(band.FromHz, band.ToHz, sampleRate)
                : band.LongShare <= 0 ? _short.Amplitude(band.FromHz, band.ToHz, sampleRate)
                : band.LongShare * _long.Amplitude(band.FromHz, band.ToHz, sampleRate)
                    + (1 - band.LongShare) * _short.Amplitude(band.FromHz, band.ToHz, sampleRate);
            double db = 20 * Math.Log10(amplitude + 1e-9) + band.TiltDb;
            double target = Math.Clamp((db - FloorDb) / RangeDb, 0, 1);
            target = Math.Pow(target, 1.15); // keep quiet bands low, let loud ones reach high
            _levels[bar] += (target - _levels[bar]) * (target > _levels[bar] ? attack : release);
        }
    }

    /// <summary>A Hann-windowed radix-2 FFT of one size, keeping the power of each bin it was asked to resolve.</summary>
    private sealed class Spectrum
    {
        private readonly double[] _re, _im, _window, _power, _cos, _sin;
        private readonly int[] _reverse;
        private int _resolved;
        private readonly int _size;

        internal Spectrum(int size)
        {
            _size = size;
            _re = new double[size]; _im = new double[size]; _window = new double[size]; _power = new double[size / 2];
            _reverse = new int[size];
            // Twiddle factors looked up instead of rotated per butterfly.
            _cos = new double[size / 2]; _sin = new double[size / 2];
            for (int k = 0; k < size / 2; k++) { _cos[k] = Math.Cos(-2 * Math.PI * k / size); _sin[k] = Math.Sin(-2 * Math.PI * k / size); }
            int bits = (int)Math.Round(Math.Log2(size));
            for (int index = 0; index < size; index++)
            {
                _window[index] = .5 - .5 * Math.Cos(2 * Math.PI * index / (size - 1));
                int reversed = 0;
                for (int bit = 0, value = index; bit < bits; bit++, value >>= 1) reversed = (reversed << 1) | (value & 1);
                _reverse[index] = reversed;
            }
        }

        // Transforms samples[offset .. offset + size) and stores the power of the first `bins` bins.
        internal void Transform(float[] samples, int offset, int bins)
        {
            for (int index = 0; index < _size; index++) { _re[_reverse[index]] = samples[offset + index] * _window[index]; _im[_reverse[index]] = 0; }
            for (int span = 2; span <= _size; span <<= 1)
            {
                int half = span / 2, stride = _size / span;
                for (int start = 0; start < _size; start += span)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wRe = _cos[k * stride], wIm = _sin[k * stride];
                        int even = start + k, odd = even + half;
                        double tRe = wRe * _re[odd] - wIm * _im[odd], tIm = wRe * _im[odd] + wIm * _re[odd];
                        _re[odd] = _re[even] - tRe; _im[odd] = _im[even] - tIm;
                        _re[even] += tRe; _im[even] += tIm;
                    }
                }
            }
            _resolved = Math.Clamp(bins, 3, _power.Length);
            for (int bin = 0; bin < _resolved; bin++) _power[bin] = _re[bin] * _re[bin] + _im[bin] * _im[bin];
        }

        // Amplitude of the band (a full-scale sine reads 1). A band narrower than a bin reads the spectrum interpolated
        // at its centre, so neighbouring bass notes differ smoothly instead of sharing one bin.
        internal double Amplitude(double fromHz, double toHz, int sampleRate)
        {
            double binHz = (double)sampleRate / _size;
            double fromBin = fromHz / binHz, toBin = toHz / binHz;
            int last = _resolved - 1;
            double power;
            if (toBin - fromBin < 1)
            {
                double centre = Math.Sqrt(fromHz * toHz) / binHz;
                int low = Math.Clamp((int)centre, 1, last - 1);
                double t = Math.Clamp(centre - low, 0, 1);
                power = _power[low] * (1 - t) + _power[low + 1] * t;
            }
            else
            {
                // Blend the band's strongest bin with its mean power: peaks keep tones visible, the mean steadies noise.
                int from = Math.Clamp((int)fromBin, 1, last), to = Math.Clamp((int)Math.Ceiling(toBin), from, last);
                double peak = 0, sum = 0;
                for (int bin = from; bin <= to; bin++) { peak = Math.Max(peak, _power[bin]); sum += _power[bin]; }
                power = .6 * peak + .4 * sum / (to - from + 1);
            }
            return Math.Sqrt(power) * 4 / _size;
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
        if (_points.Length != points) { _points = new Point[points]; _wave = new double[points]; }
        Point[] samples = _points;
        double[] raw = _wave;
        bool any = false;
        for (int index = 0; index < points; index++)
        {
            double from = Math.Max(0, (index - .5) / (points - 1)), to = Math.Min(1, (index + .5) / (points - 1));
            raw[index] = LevelAtFraction(from, to);
        }
        for (int index = 0; index < points; index++)
        {
            double left = raw[Math.Max(0, index - 1)], right = raw[Math.Min(points - 1, index + 1)];
            double level = .25 * left + .5 * raw[index] + .25 * right;
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
