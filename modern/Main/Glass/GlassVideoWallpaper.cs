// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>
/// Owns the selected muted video and the playback implementation used by the current shell.
/// </summary>
internal static class GlassVideoWallpaper
{
    private static readonly string[] Extensions = { ".mp4", ".webm", ".wmv", ".mov", ".m4v", ".avi", ".mkv" };
    private static MediaPlayer? _player;
    private static VideoWallpaperPlayback? _synchronized;
    private static LiveVideoFrost? _mediaFrost;
    private static bool _requestedSynchronized;
    private static DispatcherTimer? _firstFrameWatch;
    private static string? _path;
    private static bool _paused;

    internal static bool IsVideo(string? path) => !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The brush of the video currently playing (null when no video background is active).</summary>
    internal static ImageBrush? Brush { get; private set; }

    /// <summary>The live video frame source of the active video background.</summary>
    internal static ImageSource? Source => Brush?.ImageSource;
    internal static ImageSource? FrostedSource => _mediaFrost?.Source ?? _synchronized?.FrostedSource;

    /// <summary>Starts <paramref name="path"/> as the background video (stopping the previous one) and returns its brush.</summary>
    internal static ImageBrush Create(string path, bool useSynchronizedFrames = false)
    {
        bool same = Brush is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        if (same && (!useSynchronizedFrames || _requestedSynchronized)) return Brush!;
        if (!same) Stop();
        _path = path;
        if (useSynchronizedFrames) {
            _requestedSynchronized = true;
            _synchronized = new VideoWallpaperPlayback(path, Application.Current.Dispatcher, _paused, OnSynchronizedPlaybackFailed);
            _synchronized.FirstFramePresented += OnFirstFramePresented;
            Brush ??= new ImageBrush(_synchronized.Source);
            WatchFirstFrame();
        } else Brush = new ImageBrush(StartMediaPlayer(path));
        return Brush;
    }

    internal static void SetFrostViewport(double width, double height, bool stretch, double blur) {
        _synchronized?.SetFrostViewport(width, height, stretch, blur);
        _mediaFrost?.SetViewport(width, height, stretch, blur);
    }

    internal static void SetNavigationActive(bool active) => _synchronized?.SetNavigationActive(active);

    private static void OnFirstFramePresented(object? sender, EventArgs args) {
        if (sender is not VideoWallpaperPlayback playback || !ReferenceEquals(playback, _synchronized) || Brush is null) return;
        playback.FirstFramePresented -= OnFirstFramePresented;
        StopFirstFrameWatch();
        if (_player is not { } temporary) return;
        _player = null;
        _mediaFrost = null;
        Brush.ImageSource = playback.Source;
        ModMain.FrmMain?.RefreshGlassShell();
        temporary.Close();
    }

    private static void OnSynchronizedPlaybackFailed(VideoWallpaperPlayback playback, Exception error) {
        if (!ReferenceEquals(_synchronized, playback) || Brush is null || _path is not { } path) return;
        StopFirstFrameWatch();
        _synchronized = null;
        playback.FirstFramePresented -= OnFirstFramePresented;
        playback.Dispose();
        Logger.Info(error, "视频壁纸改用兼容播放器");
        if (_player is not null) return;
        try {
            Brush.ImageSource = StartMediaPlayer(path);
            ModMain.FrmMain?.RefreshGlassShell();
        } catch (Exception fallbackError) { Logger.Warn(fallbackError, "播放视频背景失败"); }
    }

    private static void WatchFirstFrame() {
        StopFirstFrameWatch();
        if (_paused || _player is not null || _synchronized is not { } playback || playback.Source.Width > 0) return;
        var watch = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2.5) };
        _firstFrameWatch = watch;
        watch.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_firstFrameWatch, watch)) return;
            StopFirstFrameWatch();
            if (_paused || !ReferenceEquals(_synchronized, playback) || playback.Source.Width > 0 || _path is not { } path || Brush is null) return;
            try {
                Brush.ImageSource = StartMediaPlayer(path);
                ModMain.FrmMain?.RefreshGlassShell();
            } catch (Exception error) { Logger.Warn(error, "临时显示视频壁纸失败"); }
        };
        watch.Start();
    }

    private static void StopFirstFrameWatch() {
        _firstFrameWatch?.Stop();
        _firstFrameWatch = null;
    }

    private static ImageSource StartMediaPlayer(string path) {
        var player = new MediaPlayer { IsMuted = true, Volume = 0 };
        // A 16:9 placeholder until the video reports its own size.
        var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 1920, 1080) };
        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
                drawing.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);
        };
        player.MediaEnded += (_, _) => { if (ReferenceEquals(player, _player)) { player.Position = TimeSpan.Zero; if (!_paused) player.Play(); } };
        player.MediaFailed += (_, args) => Logger.Warn(args.ErrorException, "播放视频背景失败：" + path);
        player.Open(new Uri(Path.GetFullPath(path)));
        if (!_paused) player.Play();
        _player = player;
        var source = new DrawingImage(drawing);
        _mediaFrost = new LiveVideoFrost(source);
        return source;
    }

    /// <summary>Stops and releases the background video.</summary>
    internal static void Stop()
    {
        StopFirstFrameWatch();
        if (_synchronized is not null) _synchronized.FirstFramePresented -= OnFirstFramePresented;
        _synchronized?.Dispose();
        _synchronized = null;
        _player?.Close();
        _player = null;
        _mediaFrost = null;
        _requestedSynchronized = false;
        _path = null;
        Brush = null;
    }

    /// <summary>Holds the displayed video frame or resumes playback.</summary>
    internal static void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        _synchronized?.SetPaused(paused);
        WatchFirstFrame();
        if (_player is null) return;
        if (paused) _player.Pause(); else _player.Play();
    }
}
