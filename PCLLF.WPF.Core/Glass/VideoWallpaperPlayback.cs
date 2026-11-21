// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Buffers;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace PCLLF.Wpf.Glass;

/// <summary>Presents the visible video and its shared frosted texture together from one decoded frame.</summary>
public sealed class VideoWallpaperPlayback : IDisposable {
    private const int BufferedFrameLimit = 4;
    private static readonly Duration NonBlockingLock = new(TimeSpan.Zero);
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action<VideoWallpaperPlayback, Exception> _failed;
    private readonly Action _present;
    private readonly ImageDrawing _picture = new(), _frost = new();
    private readonly Queue<ScheduledFrame> _buffer = new(BufferedFrameLimit);
    private WriteableBitmap? _pictureBitmap, _frostBitmap;
    private FramePair? _pending;
    private bool _paused, _disposed, _queued, _decodeFailed, _presenterFinished;
    private volatile bool _navigationActive;
    private FrostParameters _parameters = new(0, 0, false, 24);

    private sealed record FrostParameters(double Width, double Height, bool Stretch, double Radius);
    private readonly record struct ScheduledFrame(long Offset, FramePair Pair);
    private sealed record FramePair(MediaFoundationVideoReader.Frame Color, byte[] Frost, int Width, int Height) {
        internal void Release() {
            ArrayPool<byte>.Shared.Return(Color.Pixels);
            ArrayPool<byte>.Shared.Return(Frost);
        }
    }

    public ImageSource Source { get; }
    public ImageSource FrostedSource { get; }

    public VideoWallpaperPlayback(string path, Dispatcher dispatcher, bool paused, Action<VideoWallpaperPlayback, Exception> failed) {
        _dispatcher = dispatcher;
        _failed = failed;
        _present = Present;
        _paused = paused;
        if (!paused) _clock.Start();
        Source = new DrawingImage(_picture);
        FrostedSource = new DrawingImage(_frost);
        WallpaperBlurCache.RegisterVideoFrame(FrostedSource);
        var decoder = new Thread(() => Decode(path)) { IsBackground = true, Name = "视频壁纸解码" };
        var presenter = new Thread(ScheduleFrames) { IsBackground = true, Name = "视频壁纸播放" };
        presenter.Start();
        decoder.Start();
    }

    public void SetFrostViewport(double width, double height, bool stretch, double backgroundBlur) =>
        Volatile.Write(ref _parameters, new(width, height, stretch, Math.Sqrt(24 * 24 + backgroundBlur * backgroundBlur)));

    public void SetNavigationActive(bool active) => _navigationActive = active;

    // Compare source timestamps so 24/25/30 fps clips keep all their frames. Allow for rounded media timestamps.
    internal static bool ShouldPrepareFrame(bool navigating, long offset, long? previous) =>
        !navigating || previous is null || offset - previous.Value >= TimeSpan.TicksPerSecond / 30 - TimeSpan.TicksPerMillisecond;

    public void SetPaused(bool paused) {
        lock (_gate) {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            if (paused) { _clock.Stop(); ReleasePending(); }
            else _clock.Start();
            SignalStateChange();
        }
    }

    private void Decode(string path) {
        try {
            if (!WaitForDecodeSlot()) return;
            using var reader = new MediaFoundationVideoReader(path);
            long? firstTimestamp = null;
            long? lastPreparedOffset = null;
            long loopOffset = 0, previousOffset = 0, frameDuration = TimeSpan.TicksPerSecond / 60;
            while (WaitForDecodeSlot()) {
                if (!reader.Read(out var decoded)) {
                    if (firstTimestamp is null) throw new InvalidDataException("视频没有可播放的画面。");
                    reader.Rewind();
                    loopOffset = previousOffset + frameDuration;
                    firstTimestamp = null;
                    continue;
                }
                if (decoded is not { } color) continue;
                byte[]? blurPixels = null;
                bool transferred = false;
                try {
                    bool first = firstTimestamp is null;
                    long startTimestamp = firstTimestamp ??= color.Timestamp;
                    long offset = loopOffset + Math.Max(0, color.Timestamp - startTimestamp);
                    if (!first && offset > previousOffset) frameDuration = offset - previousOffset;
                    previousOffset = offset;
                    // While pages cross-fade, avoid blurring and uploading every frame of a high-rate video.
                    // Scheduling still uses the original media timeline, and both textures always advance together.
                    if (!ShouldPrepareFrame(_navigationActive, offset, lastPreparedOffset)) continue;
                    lastPreparedOffset = offset;
                    double scale = Math.Min(1, Math.Min(320d / color.Width, 240d / color.Height));
                    int width = Math.Max(1, (int)Math.Round(color.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(color.Height * scale));
                    blurPixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
                    MakeFrost(color, blurPixels, width, height);
                    lock (_gate) {
                        if (_disposed || _decodeFailed) return;
                        _buffer.Enqueue(new(offset, new FramePair(color, blurPixels, width, height)));
                        transferred = true;
                        Monitor.PulseAll(_gate);
                    }
                } finally {
                    if (!transferred) {
                        ArrayPool<byte>.Shared.Return(color.Pixels);
                        if (blurPixels is not null) ArrayPool<byte>.Shared.Return(blurPixels);
                    }
                }
            }
        } catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or NotSupportedException or
            DllNotFoundException or EntryPointNotFoundException or ArgumentException or InvalidOperationException or OverflowException) {
            PostFailure(error);
        }
    }

    private bool WaitForDecodeSlot() {
        lock (_gate) {
            while (!_disposed && !_decodeFailed && (_paused || _buffer.Count >= BufferedFrameLimit)) Monitor.Wait(_gate);
            return !_disposed && !_decodeFailed;
        }
    }

    private bool TakeBufferedFrame(bool first, out ScheduledFrame frame) {
        lock (_gate) {
            int required = first ? BufferedFrameLimit : 1;
            while (!_disposed && !_decodeFailed && (_paused || _buffer.Count < required)) Monitor.Wait(_gate);
            if (_disposed || _decodeFailed) { frame = default; return false; }
            frame = _buffer.Dequeue();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    private void ScheduleFrames() {
        try {
            using var timer = new PlaybackTimer(_wake);
            long? start = null;
            int consecutiveLateFrames = 0;
            while (TakeBufferedFrame(start is null, out var video)) {
                bool transferred = false;
                try {
                    start ??= PlaybackTicks();
                    long due = start.Value + video.Offset;
                    if (!WaitUntil(due, timer)) return;
                    // Skip occasional late frames. If decoding cannot catch the old clock, resync
                    // after a short burst so the wallpaper keeps updating as a paired texture.
                    long now = PlaybackTicks();
                    if (now - due > TimeSpan.TicksPerMillisecond * 50)
                    {
                        if (++consecutiveLateFrames < 3) continue;
                        start = now - video.Offset;
                    }
                    consecutiveLateFrames = 0;
                    transferred = Enqueue(video.Pair);
                } finally { if (!transferred) video.Pair.Release(); }
            }
        } catch (Exception error) when (error is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException) {
            PostFailure(error);
        } finally {
            lock (_gate) { _presenterFinished = true; _wake.Dispose(); }
        }
    }

    private void MakeFrost(MediaFoundationVideoReader.Frame color, byte[] pixels, int width, int height) {
        // Sample four points per output pixel before blurring to keep fine moving details from shimmering.
        for (int y = 0; y < height; y++) {
            int topRow = ((4 * y + 1) * color.Height / (4 * height)) * color.Width * 4;
            int bottomRow = ((4 * y + 3) * color.Height / (4 * height)) * color.Width * 4;
            for (int x = 0; x < width; x++) {
                int left = ((4 * x + 1) * color.Width / (4 * width)) * 4;
                int right = ((4 * x + 3) * color.Width / (4 * width)) * 4;
                int destination = (y * width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                    pixels[destination + channel] = (byte)((color.Pixels[topRow + left + channel] + color.Pixels[topRow + right + channel] +
                        color.Pixels[bottomRow + left + channel] + color.Pixels[bottomRow + right + channel] + 2) / 4);
                pixels[destination + 3] = 255;
            }
        }
        var options = Volatile.Read(ref _parameters);
        double displayX = (options.Width > 1 ? options.Width : color.Width) / width;
        double displayY = (options.Height > 1 ? options.Height : color.Height) / height;
        if (!options.Stretch) displayX = displayY = Math.Max(displayX, displayY);
        WallpaperBlurCache.BlurPbgra32(pixels, width, height, options.Radius / 3 / displayX, options.Radius / 3 / displayY);
    }

    private long PlaybackTicks() { lock (_gate) return _clock.Elapsed.Ticks; }

    private bool WaitUntil(long due, PlaybackTimer timer) {
        while (true) {
            long remaining;
            lock (_gate) {
                if (_disposed || _decodeFailed) return false;
                remaining = _paused ? long.MaxValue : due - _clock.Elapsed.Ticks;
                if (remaining <= 0) return true;
            }
            timer.Wait(remaining);
        }
    }

    private bool Enqueue(FramePair pair) {
        lock (_gate) {
            if (_disposed || _paused || _decodeFailed || _dispatcher.HasShutdownStarted) return false;
            if (!_queued) {
                if (_dispatcher.BeginInvoke(DispatcherPriority.Render, _present).Status == DispatcherOperationStatus.Aborted) return false;
                _queued = true;
            }
            ReleasePending();
            _pending = pair;
            return true;
        }
    }

    private void Present() {
        FramePair? pair;
        lock (_gate) { pair = _pending; _pending = null; _queued = false; }
        if (pair is null) return;
        try {
            var color = pair.Color;
            WriteableBitmap picture = BitmapFor(_pictureBitmap, color.Width, color.Height, PixelFormats.Bgr32);
            WriteableBitmap frost = BitmapFor(_frostBitmap, pair.Width, pair.Height, PixelFormats.Pbgra32);
            // If WPF is still using either buffer, keep both textures on the previous video frame.
            if (!picture.TryLock(NonBlockingLock)) return;
            try {
                if (!frost.TryLock(NonBlockingLock)) return;
                try {
                    picture.WritePixels(new Int32Rect(0, 0, color.Width, color.Height), color.Pixels, color.Width * 4, 0);
                    frost.WritePixels(new Int32Rect(0, 0, pair.Width, pair.Height), pair.Frost, pair.Width * 4, 0);
                } finally { frost.Unlock(); }
            } finally { picture.Unlock(); }
            if (!ReferenceEquals(_pictureBitmap, picture)) {
                if (_pictureBitmap is null) Logger.Info($"视频壁纸同步播放已就绪（{color.Width}×{color.Height}）");
                _pictureBitmap = picture;
                _picture.Rect = new Rect(0, 0, color.Width, color.Height);
                _picture.ImageSource = picture;
                // Both drawings use the same intrinsic aspect ratio, including at fractional DPI.
                _frost.Rect = _picture.Rect;
            }
            if (!ReferenceEquals(_frostBitmap, frost)) {
                _frostBitmap = frost;
                _frost.ImageSource = frost;
            }
        } catch (Exception error) when (error is InvalidOperationException or ArgumentException or COMException) { Fail(error); }
        finally { pair.Release(); }
    }

    private static WriteableBitmap BitmapFor(WriteableBitmap? existing, int width, int height, PixelFormat format) =>
        existing is not null && existing.PixelWidth == width && existing.PixelHeight == height
        ? existing : new WriteableBitmap(width, height, 96, 96, format, null);

    private void Fail(Exception error) {
        lock (_gate) {
            if (_disposed || _decodeFailed) return;
            _decodeFailed = true;
            ReleasePending();
            ReleaseBuffer();
            SignalStateChange();
        }
        _failed(this, error);
    }

    private void ReleasePending() { _pending?.Release(); _pending = null; }

    private void ReleaseBuffer() { while (_buffer.TryDequeue(out var frame)) frame.Pair.Release(); }

    private void SignalStateChange() {
        Monitor.PulseAll(_gate);
        if (!_presenterFinished) _wake.Set();
    }

    private void PostFailure(Exception error) {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Fail(error)));
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            _clock.Stop();
            ReleasePending();
            ReleaseBuffer();
            SignalStateChange();
        }
    }

    // A per-player timer avoids changing the process-wide timer resolution for the rest of the launcher.
    private sealed class PlaybackTimer : WaitHandle {
        private readonly AutoResetEvent _control;
        private readonly WaitHandle[]? _handles;
        internal PlaybackTimer(AutoResetEvent control) {
            _control = control;
            const uint highResolution = 0x2, synchronizeAndModify = 0x00100002;
            SafeWaitHandle = CreateWaitableTimerExW(0, 0, highResolution, synchronizeAndModify);
            if (!SafeWaitHandle.IsInvalid) _handles = [control, this];
        }

        internal void Wait(long ticks) {
            if (ticks == long.MaxValue) { _control.WaitOne(); return; }
            long due = -ticks;
            if (_handles is not null && SetWaitableTimer(SafeWaitHandle, ref due, 0, 0, 0, false)) {
                WaitAny(_handles);
                return;
            }
            _control.WaitOne((int)Math.Clamp((ticks - 1) / TimeSpan.TicksPerMillisecond + 1, 1, int.MaxValue));
        }

        [DllImport("kernel32", ExactSpelling = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
        [DllImport("kernel32", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
