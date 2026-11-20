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
    private static string? _path;
    private static bool _paused;

    internal static bool IsVideo(string? path) => !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The brush of the video currently playing (null when no video background is active).</summary>
    internal static ImageBrush? Brush { get; private set; }

    /// <summary>The live video frame source of the active video background.</summary>
    internal static ImageSource? Source => Brush?.ImageSource;
    internal static ImageSource? FrostedSource => _synchronized?.FrostedSource;

    /// <summary>Starts <paramref name="path"/> as the background video (stopping the previous one) and returns its brush.</summary>
    internal static ImageBrush Create(string path, bool useSynchronizedFrames = false)
    {
        if (Brush is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return Brush;
        Stop();
        _path = path;
        if (useSynchronizedFrames) {
            _synchronized = new VideoWallpaperPlayback(path, Application.Current.Dispatcher, _paused, OnSynchronizedPlaybackFailed);
            Brush = new ImageBrush(_synchronized.Source);
        } else Brush = new ImageBrush(StartMediaPlayer(path));
        return Brush;
    }

    internal static void SetFrostViewport(double width, double height, bool stretch, double blur) =>
        _synchronized?.SetFrostViewport(width, height, stretch, blur);

    private static void OnSynchronizedPlaybackFailed(VideoWallpaperPlayback playback, Exception error) {
        if (!ReferenceEquals(_synchronized, playback) || Brush is null || _path is not { } path) return;
        _synchronized = null;
        playback.Dispose();
        Logger.Info(error, "视频壁纸改用兼容播放器");
        try {
            Brush.ImageSource = StartMediaPlayer(path);
            ModMain.FrmMain?.RefreshGlassShell();
        } catch (Exception fallbackError) { Logger.Warn(fallbackError, "播放视频背景失败"); }
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
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; if (!_paused) player.Play(); };
        player.MediaFailed += (_, args) => Logger.Warn(args.ErrorException, "播放视频背景失败：" + path);
        player.Open(new Uri(Path.GetFullPath(path)));
        if (!_paused) player.Play();
        _player = player;
        return new DrawingImage(drawing);
    }

    /// <summary>Stops and releases the background video.</summary>
    internal static void Stop()
    {
        _synchronized?.Dispose();
        _synchronized = null;
        _player?.Close();
        _player = null;
        _path = null;
        Brush = null;
    }

    /// <summary>Pauses (minimised window) or resumes the background video.</summary>
    internal static void SetPaused(bool paused)
    {
        _paused = paused;
        _synchronized?.SetPaused(paused);
        if (_player is null) return;
        if (paused) _player.Pause(); else _player.Play();
    }
}
