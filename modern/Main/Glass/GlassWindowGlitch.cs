// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// A short "broken window" easter egg: the picture splits into red and cyan ghosts, bands of it tear sideways, noise,
/// dead blocks and scanlines flicker over it and the window shakes, in bursts that fade out.
/// </summary>
/// <remarks>
/// Cheap by design: the interface is captured once when the effect starts and every torn band is cut from that still
/// (no live re-rendering of the interface per band). All shapes come from a fixed pool that is only moved, resized and
/// shown or hidden; nothing is allocated while it plays. Purely visual: it never takes input, and any failure ends it
/// at once and leaves the interface as it was.
/// </remarks>
internal sealed class GlassWindowGlitch
{
    private const int BandCount = 8, LineCount = 5, BlockCount = 6;
    private static readonly SolidColorBrush MagentaBrush = Frozen(Color.FromRgb(255, 0, 170)), CyanBrush = Frozen(Color.FromRgb(0, 230, 255));
    private static readonly SolidColorBrush WhiteBrush = Frozen(Colors.White), BlackBrush = Frozen(Colors.Black);

    private readonly Panel _host;
    private readonly FrameworkElement _source;
    private readonly Canvas _layer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly TranslateTransform _shake = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Random _random = new();
    private readonly Rectangle[] _bands = new Rectangle[BandCount], _fringes = new Rectangle[BandCount];
    private readonly RectangleGeometry[] _bandClips = new RectangleGeometry[BandCount];
    private readonly Rectangle[] _lines = new Rectangle[LineCount], _blocks = new Rectangle[BlockCount];
    private readonly ImageBrush _redBrush = new() { Stretch = Stretch.Fill }, _cyanBrush = new() { Stretch = Stretch.Fill };
    private readonly Rectangle _red = new(), _cyan = new(), _scanlines = new(), _blackout = new() { Fill = BlackBrush }, _flash = new() { Fill = WhiteBrush };
    private TimeSpan _duration;
    private Point _sourceOffset;
    private BitmapCacheBrush? _live;
    private bool _hasStill, _restoreTransform;
    private Transform? _formerTransform;

    /// <param name="host">The panel the effect is drawn in (above everything else in it).</param>
    /// <param name="source">The interface that tears; a sibling of the effect layer inside <paramref name="host"/>.</param>
    internal GlassWindowGlitch(Panel host, FrameworkElement source)
    {
        _host = host;
        _source = source;
        _timer = new DispatcherTimer(DispatcherPriority.Render, host.Dispatcher);
        _timer.Tick += (_, _) => Step();
        Panel.SetZIndex(_layer, int.MaxValue);
        Grid.SetRowSpan(_layer, 64);
        Grid.SetColumnSpan(_layer, 64);
        // Back to front: colour ghosts, torn bands with their fringes, noise, scanlines, then whole-frame drops.
        _red.Fill = _redBrush;
        _cyan.Fill = _cyanBrush;
        Pool(_red);
        Pool(_cyan);
        for (int index = 0; index < BandCount; index++)
        {
            _fringes[index] = Pool(new Rectangle());
            _bandClips[index] = new RectangleGeometry();
            _bands[index] = Pool(new Rectangle { Clip = _bandClips[index] });
        }
        for (int index = 0; index < LineCount; index++) _lines[index] = Pool(new Rectangle());
        for (int index = 0; index < BlockCount; index++) _blocks[index] = Pool(new Rectangle());
        _scanlines.Fill = ScanlineBrush();
        Pool(_scanlines);
        Pool(_blackout);
        Pool(_flash);
    }

    internal bool IsPlaying { get; private set; }

    internal void Play(TimeSpan duration)
    {
        if (IsPlaying || _host.ActualWidth <= 0 || _host.ActualHeight <= 0 || _source.ActualWidth <= 0) return;
        try
        {
            IsPlaying = true;
            _duration = duration;
            _sourceOffset = _source.TranslatePoint(new Point(0, 0), _host);
            // Torn bands show the live interface through one GPU-cached brush, each band clipped to its own strip.
            _live = new BitmapCacheBrush(_source) { AutoLayoutContent = false };
            foreach (Rectangle band in _bands) band.Fill = _live;
            _hasStill = Capture();
            _host.Children.Add(_layer);
            _restoreTransform = _source.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue;
            _formerTransform = _source.RenderTransform;
            _source.RenderTransform = _shake;
            _clock.Restart();
            Step();
        }
        catch (Exception)
        {
            Stop();
        }
    }

    internal void Stop()
    {
        _timer.Stop();
        if (!IsPlaying) return;
        IsPlaying = false;
        try
        {
            HideAll();
            _host.Children.Remove(_layer);
            _shake.X = _shake.Y = 0;
            if (_restoreTransform) _source.RenderTransform = _formerTransform;
            else _source.ClearValue(UIElement.RenderTransformProperty);
            // Let the cache and stills go; the next run makes them afresh.
            foreach (Rectangle band in _bands) band.Fill = null;
            if (_live is not null) _live.Target = null;
            _live = null;
            _redBrush.ImageSource = _cyanBrush.ImageSource = null;
        }
        catch (Exception) { }
    }

    // Red and cyan copies of the interface for the colour ghosts (bright parts only, so dark areas stay clear). They are
    // faint and offset, so a small still is enough: rendering one is done in software, and its cost grows with its size.
    private bool Capture()
    {
        try
        {
            double width = _source.ActualWidth, height = _source.ActualHeight;
            const double scale = .3;
            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(_source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, width, height));
            var half = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            half.Render(drawing);
            int pixelsWide = half.PixelWidth, pixelsHigh = half.PixelHeight, stride = pixelsWide * 4;
            var pixels = new byte[stride * pixelsHigh];
            half.CopyPixels(pixels, stride, 0);
            var red = new byte[pixels.Length];
            var cyan = new byte[pixels.Length];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                // Premultiplied BGRA: each ghost keeps one side of the colour, its opacity following that side's brightness.
                byte blue = pixels[index], green = pixels[index + 1], r = pixels[index + 2];
                red[index + 2] = red[index + 3] = r;
                byte other = Math.Max(blue, green);
                cyan[index] = blue; cyan[index + 1] = green; cyan[index + 3] = other;
            }
            _redBrush.ImageSource = Frozen(BitmapSource.Create(pixelsWide, pixelsHigh, half.DpiX, half.DpiY, PixelFormats.Pbgra32, null, red, stride));
            _cyanBrush.ImageSource = Frozen(BitmapSource.Create(pixelsWide, pixelsHigh, half.DpiX, half.DpiY, PixelFormats.Pbgra32, null, cyan, stride));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Step()
    {
        _timer.Stop();
        if (!IsPlaying) return;
        try
        {
            double progress = _clock.Elapsed.TotalMilliseconds / Math.Max(1, _duration.TotalMilliseconds);
            if (progress >= 1) { Stop(); return; }
            // Full strength for most of the run, then the bursts calm down.
            double strength = progress < .7 ? 1 : 1 - (progress - .7) / .3;
            bool broken = _random.NextDouble() < .55 + .3 * strength;
            HideAll();
            _shake.X = broken ? Signed() * 11 * strength : 0;
            _shake.Y = broken && _random.NextDouble() < .18 ? Signed() * 3 * strength : 0;
            if (broken) Draw(strength);
            _timer.Interval = TimeSpan.FromMilliseconds(broken ? _random.Next(30, 72) : _random.Next(20, 50));
            _timer.Start();
        }
        catch (Exception)
        {
            Stop();
        }
    }

    private void Draw(double strength)
    {
        double width = _host.ActualWidth, height = _host.ActualHeight;
        if (_hasStill)
        {
            // The picture splits: a red ghost one way, a cyan ghost the other.
            double split = 2 + _random.NextDouble() * 6 * strength;
            Show(_red, _sourceOffset.X + split, _sourceOffset.Y, _source.ActualWidth, _source.ActualHeight, .22 + .2 * strength);
            Show(_cyan, _sourceOffset.X - split, _sourceOffset.Y, _source.ActualWidth, _source.ActualHeight, .22 + .2 * strength);
        }
        if (_live is not null)
        {
            // Torn bands: strips of the interface thrown sideways; some only part of the width, now and then a tall one.
            int bands = _random.Next(2, 7);
            for (int index = 0; index < bands; index++)
            {
                bool tall = _random.NextDouble() < .06;
                double bandHeight = tall ? height * (.25 + .3 * _random.NextDouble()) : _random.Next(4, Math.Max(6, (int)(height * .16)));
                double top = _random.NextDouble() * Math.Max(1, height - bandHeight);
                bool partial = _random.NextDouble() < .35;
                double bandWidth = partial ? width * (.2 + .45 * _random.NextDouble()) : width;
                double left = partial ? _random.NextDouble() * (width - bandWidth) : 0;
                double shift = Signed() * (10 + 45 * strength) * (tall ? .5 : 1);
                // The band is the whole interface moved sideways, clipped in its own coordinates to the strip.
                _bandClips[index].Rect = new Rect(left - _sourceOffset.X, top - _sourceOffset.Y, bandWidth, bandHeight);
                Show(_bands[index], _sourceOffset.X + shift, _sourceOffset.Y, _source.ActualWidth, _source.ActualHeight, 1);
                if (_random.NextDouble() < .5)
                {
                    _fringes[index].Fill = _random.NextDouble() < .5 ? MagentaBrush : CyanBrush;
                    Show(_fringes[index], left + shift + Signed() * 5, top, bandWidth, bandHeight, .16 + .16 * strength);
                }
            }
        }
        // Hairline noise across the window.
        for (int index = _random.Next(1, LineCount + 1) - 1; index >= 0; index--)
        {
            _lines[index].Fill = _random.NextDouble() < .5 ? WhiteBrush : BlackBrush;
            Show(_lines[index], 0, _random.NextDouble() * height, width, _random.Next(1, 3), .25 + .3 * strength);
        }
        // Dead blocks.
        for (int index = _random.Next(0, 5) - 1; index >= 0; index--)
        {
            _blocks[index].Fill = _random.Next(3) switch { 0 => MagentaBrush, 1 => CyanBrush, _ => BlackBrush };
            Show(_blocks[index], _random.NextDouble() * width, _random.NextDouble() * height, _random.Next(16, 100), _random.Next(4, 16), .35 * strength + .1);
        }
        Show(_scanlines, 0, 0, width, height, .55 * strength + .05);
        // The signal drops for a frame, or (rarely) overloads white.
        double drop = _random.NextDouble();
        if (drop < .08 * strength) Show(_blackout, 0, 0, width, height, .35);
        else if (drop < .1 * strength) Show(_flash, 0, 0, width, height, .15);
    }

    private Rectangle Pool(Rectangle shape)
    {
        shape.Visibility = Visibility.Collapsed;
        shape.IsHitTestVisible = false;
        _layer.Children.Add(shape);
        return shape;
    }

    private static void Show(Rectangle shape, double left, double top, double width, double height, double opacity)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
        shape.Opacity = Math.Clamp(opacity, 0, 1);
        shape.Visibility = Visibility.Visible;
    }

    private void HideAll()
    {
        foreach (UIElement child in _layer.Children) child.Visibility = Visibility.Collapsed;
    }

    private double Signed() => _random.NextDouble() * 2 - 1;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static BitmapSource Frozen(BitmapSource bitmap)
    {
        bitmap.Freeze();
        return bitmap;
    }

    // One dark line every three pixels.
    private static DrawingBrush ScanlineBrush()
    {
        var line = new GeometryDrawing(Frozen(Color.FromArgb(70, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 0, 4, 1)));
        var gap = new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 4, 3)));
        var brush = new DrawingBrush(new DrawingGroup { Children = { gap, line } })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, 3),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
