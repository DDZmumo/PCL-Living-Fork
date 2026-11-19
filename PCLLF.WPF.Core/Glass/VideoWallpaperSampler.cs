// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>Shares one pre-blurred video frame across all glass surfaces and presents it on the WPF render clock.</summary>
public sealed class VideoWallpaperSampler : IDisposable {
    private const double CardBlur = 24;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    private static readonly TimeSpan FrameTolerance = TimeSpan.FromMilliseconds(1);
    private readonly Dispatcher _dispatcher;
    private readonly Action _capture;
    private readonly DrawingVisual _visual = new();
    private readonly Action<ImageSource?> _publish;
    private Image? _host;
    private ImageSource? _video;
    private RenderTargetBitmap? _raster;
    private WriteableBitmap? _frame;
    private DispatcherOperation? _scheduledCapture;
    private Sample? _ready;
    private TimeSpan? _lastRender;
    private TimeSpan _nextCapture;
    private long _generation;
    private bool _paused = true;
    private bool _subscribed, _capturing, _disposed, _reportedError;
    private readonly record struct Sample(byte[] Pixels, int Width, int Height);

    public VideoWallpaperSampler(Dispatcher dispatcher, Action<ImageSource?> publish) {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _dispatcher = dispatcher;
        _capture = Capture;
    }

    public void SetHost(Image host) {
        if (!ReferenceEquals(_video, host.Source) || !ReferenceEquals(_host, host)) {
            DiscardPendingFrames();
            if (_host is not null) _host.IsVisibleChanged -= OnHostVisibilityChanged;
            _host = host;
            _host.IsVisibleChanged += OnHostVisibilityChanged;
            _video = host.Source;
            _raster = null;
            _frame = null;
            _reportedError = false;
        }
        _publish(_frame);
        UpdateSubscription();
    }

    public void SetPaused(bool paused) {
        if (_disposed || _paused == paused) return;
        _paused = paused;
        UpdateSubscription();
    }

    private void OnHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSubscription();

    private void UpdateSubscription() {
        bool active = !_disposed && !_paused && _host?.IsVisible == true && _video is not null;
        if (active == _subscribed) return;
        _subscribed = active;
        if (active) CompositionTarget.Rendering += OnRendering;
        else {
            CompositionTarget.Rendering -= OnRendering;
            DiscardPendingFrames();
        }
    }

    private void OnRendering(object? sender, EventArgs args) {
        if (!_subscribed || args is not RenderingEventArgs rendering || _lastRender == rendering.RenderingTime) return;
        TimeSpan now = rendering.RenderingTime;
        _lastRender = now;
        try { PresentReadyFrame(); }
        catch (Exception error) when (Recoverable(error)) {
            DiscardPendingFrames();
            ReportError(error);
            return;
        }
        if (_ready is not null || _capturing || _scheduledCapture is not null || now + FrameTolerance < _nextCapture ||
            SystemParameters.HighContrast || !GlassSystemPreferences.TransparencyEnabled) return;
        _nextCapture = _nextCapture == default || now - _nextCapture >= FrameInterval
            ? now + FrameInterval : _nextCapture + FrameInterval;
        // Read back after the render callback. The worker's result is presented on a later render tick.
        _scheduledCapture = _dispatcher.BeginInvoke(DispatcherPriority.Input, _capture);
    }

    private async void Capture() {
        _scheduledCapture = null;
        if (_disposed || _paused || _capturing || SystemParameters.HighContrast || !GlassSystemPreferences.TransparencyEnabled ||
            _host is not { IsVisible: true, ActualWidth: > 1, ActualHeight: > 1 } host ||
            _video is not { Width: > 0, Height: > 0 } video) return;

        _capturing = true;
        long generation = _generation;
        byte[]? pixels = null;
        try {
            // Keep the decoder's aspect ratio and leave cropping to the same Stretch as the visible video.
            double scale = Math.Min(1, Math.Min(320 / video.Width, 240 / video.Height));
            int width = Math.Max(1, (int)Math.Round(video.Width * scale)), height = Math.Max(1, (int)Math.Round(video.Height * scale));
            double displayScaleX = host.ActualWidth / width, displayScaleY = host.ActualHeight / height;
            if (host.Stretch == Stretch.UniformToFill) displayScaleX = displayScaleY = Math.Max(displayScaleX, displayScaleY);
            double extraBlur = host.Effect is BlurEffect blur ? blur.Radius : 0;
            double radius = Math.Sqrt(CardBlur * CardBlur + extraBlur * extraBlur);
            double sigmaX = radius / 3 / displayScaleX, sigmaY = radius / 3 / displayScaleY;
            pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
            int stride = width * 4;
            if (_raster is null || _raster.PixelWidth != width || _raster.PixelHeight != height) {
                _raster = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                using DrawingContext drawing = _visual.RenderOpen();
                drawing.DrawImage(video, new Rect(0, 0, width, height));
            } else _raster.Clear();
            _raster.Render(_visual);
            _raster.CopyPixels(pixels, stride, 0);
            // MediaPlayer may briefly expose a transparent texture while opening or seeking back to the loop start.
            if (!HasVideoPixels(pixels, width, height)) return;

            await Task.Run(() => WallpaperBlurCache.BlurPbgra32(pixels, width, height, sigmaX, sigmaY));
            if (_disposed || generation != _generation || !ReferenceEquals(host.Source, video)) return;
            _ready = new Sample(pixels, width, height);
            pixels = null;
        } catch (Exception error) when (Recoverable(error)) {
            ReportError(error);
        } finally {
            if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
            _capturing = false;
        }
    }

    private static bool Recoverable(Exception error) => error is InvalidOperationException or ArgumentException or NotSupportedException or COMException;

    private void ReportError(Exception error) {
        if (_reportedError || _disposed) return;
        _reportedError = true;
        Logger.Warn(error, "更新视频壁纸模糊采样失败");
    }

    private static bool HasVideoPixels(byte[] pixels, int width, int height) =>
        pixels[(height / 2 * width + width / 2) * 4 + 3] != 0 &&
        pixels[(height / 4 * width + width / 4) * 4 + 3] != 0 &&
        pixels[((height - 1 - height / 4) * width + width - 1 - width / 4) * 4 + 3] != 0;

    private void PresentReadyFrame() {
        if (_ready is not { } sample) return;
        var frame = _frame;
        bool replace = frame is null || frame.PixelWidth != sample.Width || frame.PixelHeight != sample.Height;
        if (replace) frame = new WriteableBitmap(sample.Width, sample.Height, 96, 96, PixelFormats.Pbgra32, null);
        if (!frame!.TryLock(new Duration(TimeSpan.Zero))) return; // try this same frame on the next render tick
        _ready = null;
        try { frame.WritePixels(new Int32Rect(0, 0, sample.Width, sample.Height), sample.Pixels, sample.Width * 4, 0); }
        finally { frame.Unlock(); ArrayPool<byte>.Shared.Return(sample.Pixels); }
        if (replace) {
            WallpaperBlurCache.RegisterVideoFrame(frame);
            _frame = frame;
            _publish(frame);
        }
    }

    private void DiscardPendingFrames() {
        _generation++;
        _lastRender = null;
        _nextCapture = default;
        _scheduledCapture?.Abort();
        _scheduledCapture = null;
        if (_ready is { } ready) ArrayPool<byte>.Shared.Return(ready.Pixels);
        _ready = null;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        UpdateSubscription();
        if (_host is not null) _host.IsVisibleChanged -= OnHostVisibilityChanged;
        _host = null;
        _video = null;
        _raster = null;
        _frame = null;
        using (_visual.RenderOpen()) { }
    }
}
