using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using NAudio;
using NAudio.Wave;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Shell;

public enum MusicPlaybackState { Stop, Play, Pause }

/// <summary>LF's public fields remain the single authoritative state, read at their original call sites.</summary>
public sealed class MusicHost {
    public Func<List<string>?> All = null!, Waiting = null!;
    public Action<List<string>?> SetAll = null!, SetWaiting = null!;
    public Func<object?> Playback = null!;
    public Action<object?> SetPlayback = null!;
    public Func<string> BasePath = null!;
    public Func<string, bool> BooleanSetting = null!;
    public Func<string, int> IntegerSetting = null!;
    public Func<int, int, int> RandomInteger = null!;
    public Action<Action> Ui = null!, Thread = null!;
    public Action<Action, string, ThreadPriority> StartThread = null!;
    public Action RefreshSettings = null!;
    public Action<string, int, bool> Hint = null!;
}

// LF ModMusic.vb a3d54c9a898f79609b8715c0c4e4e06937ab974b (LF LICENCE).
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModMusic.cs was compared;
// LF's MediaFoundation fallback, volume timing, long device-failure wait and queue rules remain LF.
public sealed class MusicRuntime {
    private readonly MusicHost host;
    private string current = "";
    public MusicRuntime(MusicHost host) { this.host = host; }
    /// <summary>Name of the track in use (no extension), empty before anything has played.</summary>
    public string CurrentName => string.IsNullOrEmpty(current) ? "" : PathUtils.GetFileNameWithoutExtension(current);
    /// <summary>Playback position of the track in use, 0–1.</summary>
    public double Progress { get; private set; }
    /// <summary>Number of tracks found in PCL\Musics.</summary>
    public int Count => host.All()?.Count ?? 0;
    /// <summary>All tracks (full paths) in their folder order; empty before the list has been read.</summary>
    public IReadOnlyList<string> Tracks => host.All() is { } all ? all.ToArray() : Array.Empty<string>();
    /// <summary>Full path of the track in use (empty before anything has played).</summary>
    public string CurrentPath => current;

    // The reader of the track in use, kept as object so no member touched before playback starts imports NAudio.
    private object? activeReader;
    // The first automatic start can be held (the glass shell holds it until its startup splash has finished).
    private readonly object startGate = new();
    private bool startHeld, startPending;
    /// <summary>Holds the first automatic start (the track loads paused) until <see cref="ReleaseStart"/>.</summary>
    public void HoldStart() { lock (startGate) startHeld = true; }
    /// <summary>Releases a held first start: a track that was held back starts playing now.</summary>
    public void ReleaseStart() {
        object? playback;
        lock (startGate) {
            if (!startHeld) return;
            startHeld = false;
            if (!startPending) return;
            startPending = false;
            playback = host.Playback();
        }
        if (playback is not null && PlaybackStateOf(playback) == MusicPlaybackState.Pause) PlayPlayback(playback);
        RefreshUi();
    }
    /// <summary>The most recent samples sent to the audio device (mono), for visualisation.</summary>
    public MusicSampleTap Samples { get; } = new();
    private readonly List<string> history = new();

    /// <summary>Elapsed and total time of the track in use (zero while nothing is loaded).</summary>
    public TimeSpan Position => activeReader is { } reader ? ReaderPosition(reader) : TimeSpan.Zero;
    public TimeSpan Duration => activeReader is { } reader ? ReaderDuration(reader) : TimeSpan.Zero;
    [MethodImpl(MethodImplOptions.NoInlining)] private static TimeSpan ReaderPosition(object reader) => ((WaveStream)reader).CurrentTime;
    [MethodImpl(MethodImplOptions.NoInlining)] private static TimeSpan ReaderDuration(object reader) => ((WaveStream)reader).TotalTime;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void ReaderSeek(object reader, double fraction) {
        var stream = (WaveStream)reader;
        stream.CurrentTime = TimeSpan.FromMilliseconds(stream.TotalTime.TotalMilliseconds * Math.Clamp(fraction, 0, 1));
    }

    /// <summary>Moves the track in use to <paramref name="fraction"/> of its length.</summary>
    public void Seek(double fraction) {
        if (activeReader is not { } reader) return;
        try { ReaderSeek(reader, fraction); Progress = Math.Clamp(fraction, 0, 1); }
        catch (Exception ex) { Logger.Warn(ex, "调整音乐播放进度失败"); }
    }

    /// <summary>Like a player's "previous": back to the start after the first seconds, otherwise the track before.</summary>
    public void ControlPrevious() {
        if (Position.TotalSeconds > 3 || history.Count == 0) { Seek(0); if (State == MusicPlaybackState.Pause) Resume(); return; }
        string previous = history[^1];
        history.RemoveAt(history.Count - 1);
        if (!File.Exists(previous)) { ControlPrevious(); return; }
        if (!string.IsNullOrEmpty(current)) Waiting.Insert(0, current);
        StartPlay(previous, remember: false);
        Hint("正在播放：" + PathUtils.GetLastPart(previous), 1);
        RefreshUi();
    }

    /// <summary>Plays <paramref name="path"/> from the list right away.</summary>
    public void PlayTrack(string path) {
        if (string.IsNullOrEmpty(path) || host.All() is null || !All.Contains(path)) return;
        Waiting.Remove(path);
        StartPlay(path);
        RefreshUi();
    }

    /// <summary>Re-deals the queue after the shuffle setting changed (the track in use keeps playing).</summary>
    public void Reshuffle() {
        if (host.All() is null) return;
        ListInit(false, current);
        Waiting.Remove(current);
    }
    private List<string> All => host.All()!;
    private List<string> Waiting => host.Waiting()!;
    private void Hint(string text, int type, bool log = true) => host.Hint(text, type, log);

    private void ListInit(bool forceReload, string? preventFirst = null) {
        if (forceReload) host.SetAll(null);
        try {
            if (host.All() is null) {
                host.SetAll(new List<string>());
                DirectoryUtils.Create(host.BasePath() + "PCL\\Musics\\");
                foreach (string file in DirectoryUtils.EnumerateFiles(host.BasePath() + "PCL\\Musics\\", true)) {
                    if (new[] { "ini", "jpg", "txt", "cfg", "lrc", "db", "png" }.Contains(PathUtils.GetExtension(file))) continue;
                    All.Add(file);
                }
            }
            host.SetWaiting(host.BooleanSetting("UiMusicRandom")
                ? LegacyBaseOperations.Shuffle(new List<string>(All), host.RandomInteger).ToList()
                : new List<string>(All));
            // VB binary string equality treats Nothing as ""; preventFirst itself must still be non-null.
            if (preventFirst is not null && (Waiting.FirstOrDefault() ?? "") == preventFirst) {
                Waiting.RemoveAt(0); Waiting.Add(preventFirst);
            }
        } catch (Exception ex) { Logger.Error(ex, "初始化音乐列表失败"); }
    }
    private string? Dequeue() {
        string? result;
        if (host.All() is null || !All.Any() || !Waiting.Any()) ListInit(false);
        if (Waiting.Any()) { result = Waiting[0]; Waiting.RemoveAt(0); }
        else result = null;
        if (!Waiting.Any()) ListInit(false, result);
        return result;
    }
    private void RefreshUi() => host.Ui(() => {
        try {
            host.RefreshSettings();
        } catch (Exception ex) { Logger.Error(ex, "刷新背景音乐 UI 失败"); }
    });

    public void ControlPause() {
        if (host.Playback() is null) Hint("音乐播放尚未开始！", 2);
        else {
            switch (State) {
                case MusicPlaybackState.Pause: Resume(); break;
                case MusicPlaybackState.Play: Pause(); break;
                default: Logger.Warn("音乐目前为停止状态，已强制尝试开始播放"); RefreshPlay(false); break;
            }
        }
    }
    public void ControlNext() {
        if (host.All().IsSingle()) {
            StartPlay(current);
            Hint("重新播放：" + PathUtils.GetLastPart(current), 1);
        } else {
            string? address = Dequeue();
            if (address is null) Hint("没有可以播放的音乐！", 2);
            else { StartPlay(address); Hint("正在播放：" + PathUtils.GetLastPart(address), 1); }
        }
        RefreshUi();
    }

    // Keep the null check outside every method whose JIT imports NAudio metadata.
    // Constructing a runtime or observing its initial stopped state must not load NAudio.dll.
    public MusicPlaybackState State {
        get { return host.Playback() is null ? MusicPlaybackState.Stop : PlaybackStateOf(host.Playback()!); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MusicPlaybackState PlaybackStateOf(object playback) => ((WaveOutEvent)playback).PlaybackState switch {
        PlaybackState.Stopped => MusicPlaybackState.Stop, PlaybackState.Paused => MusicPlaybackState.Pause, _ => MusicPlaybackState.Play
    };

    public void RefreshPlay(bool showHint, bool isFirstLoad = false) {
        try {
            ListInit(true);
            if (!All.Any()) {
                if (host.Playback() is null) { if (showHint) Hint("未检测到可用的背景音乐！", 2); }
                else { host.SetPlayback(null); if (showHint) Hint("背景音乐已清除！", 1); }
            } else {
                string? address = Dequeue();
                if (address is null) { if (showHint) Hint("没有可以播放的音乐！", 2); }
                else {
                    try {
                        StartPlay(address, isFirstLoad);
                        if (showHint) Hint("背景音乐已刷新：" + PathUtils.GetLastPart(address), 1, false);
                    } catch { }
                }
            }
            RefreshUi();
        } catch (Exception ex) { Logger.Error(ex, "刷新背景音乐播放失败"); }
    }
    private void StartPlay(string? address, bool isFirstLoad = false, bool remember = true) {
        if (address is null) return;
        Logger.Info($"播放开始：{address}");
        // The track left behind becomes "previous" (a short history, like a player's back button).
        if (remember && !string.IsNullOrEmpty(current) && current != address) {
            history.Add(current);
            if (history.Count > 50) history.RemoveAt(0);
        }
        current = address;
        Progress = 0;
        host.StartThread(() => Loop(isFirstLoad), "Music", ThreadPriority.BelowNormal);
    }
    public bool Pause() {
        if (State != MusicPlaybackState.Play) { Logger.Info($"无需暂停播放，当前状态为 {State}"); return false; }
        host.Thread(() => {
            Logger.Info("已暂停播放");
            object? playback = host.Playback();
            if (playback is not null) PausePlayback(playback);
            RefreshUi();
        });
        return true;
    }
    public bool Resume() {
        if (State == MusicPlaybackState.Play || !All.Any()) { Logger.Info($"无需继续播放，当前状态为 {State}"); return false; }
        host.Thread(() => {
            Logger.Info("已恢复播放");
            try { object? playback = host.Playback(); if (playback is not null) PlayPlayback(playback); }
            catch {
                object? playback = host.Playback(); if (playback is not null) StopPlayback(playback);
                playback = host.Playback(); if (playback is not null) PlayPlayback(playback);
            }
            RefreshUi();
        });
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void PausePlayback(object playback) => ((WaveOutEvent)playback).Pause();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void PlayPlayback(object playback) => ((WaveOutEvent)playback).Play();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void StopPlayback(object playback) => ((WaveOutEvent)playback).Stop();

    // Passes samples through unchanged, copying them into the tap (only constructed inside Loop, after NAudio is loaded).
    private sealed class TapProvider : ISampleProvider {
        private readonly ISampleProvider source;
        private readonly MusicSampleTap tap;
        private readonly int generation;
        public TapProvider(ISampleProvider source, MusicSampleTap tap, int generation) { this.source = source; this.tap = tap; this.generation = generation; }
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) {
            int read = source.Read(buffer, offset, count);
            // A player that is shutting down may still pull a buffer; its generation keeps those samples out of the new track.
            if (read > 0) tap.Write(generation, buffer, offset, read, source.WaveFormat.Channels);
            return read;
        }
    }
    // Frames the device has actually played (its position counts bytes of the stream it was given).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PlayedFrames(object playback, int blockAlign) => ((WaveOutEvent)playback).GetPosition() / Math.Max(1, blockAlign);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Loop(bool isFirstLoad = false) {
        WaveOutEvent? wave = null;
        WaveStream? reader = null;
        try {
            wave = new WaveOutEvent(); host.SetPlayback(wave); wave.DeviceNumber = -1;
            try { reader = new AudioFileReader(current); }
            catch (Exception ex) {
                Logger.Warn(ex, "使用 AudioFileReader 加载音频文件失败，换用 MediaFoundationReader 重试");
                reader = new MediaFoundationReader(current);
            }
            activeReader = reader;
            // Same stream, passed through a tap that keeps the latest samples for the visualiser.
            ISampleProvider source = reader.ToSampleProvider();
            int generation = Samples.Reset(source.WaveFormat.SampleRate);
            wave.Init(new TapProvider(source, Samples, generation), false);
            // The visualiser reads the samples at the device's playhead, not a guess of how far it lags behind.
            WaveOutEvent device = wave;
            int blockAlign = device.OutputWaveFormat.BlockAlign;
            Samples.SetClock(generation, () => PlayedFrames(device, blockAlign));
            wave.Play();
            if (isFirstLoad && !host.BooleanSetting("UiMusicAuto")) wave.Pause();
            else if (isFirstLoad) lock (startGate) { if (startHeld) { wave.Pause(); startPending = true; } }
            RefreshUi();
            int previousVolume = 0;
            while (wave.Equals(host.Playback()) && wave.PlaybackState != PlaybackState.Stopped) {
                if (host.IntegerSetting("UiMusicVolume") != previousVolume) {
                    previousVolume = host.IntegerSetting("UiMusicVolume");
#pragma warning disable CS0612, CS0618
                    wave.Volume = (float)(previousVolume / 1000.0);
#pragma warning restore CS0612, CS0618
                }
                double percent = reader.CurrentTime.TotalMilliseconds / reader.TotalTime.TotalMilliseconds;
                double progress = double.IsFinite(percent) ? Math.Clamp(percent, 0, 1) : 0;
                Progress = progress;
                Thread.Sleep(100);
            }
            if (wave.PlaybackState == PlaybackState.Stopped && All.Any()) StartPlay(Dequeue());
        } catch (Exception ex) {
            Logger.Warn(ex, $"播放音乐出现内部错误（{current}）");
            if (ex is MmException && ex.Message.Contains("AlreadyAllocated")) {
                Hint("你的音频设备正被其他程序占用。请在关闭占用的程序后重启 PCL，才能恢复音乐播放功能！", 2);
                Thread.Sleep(1000000000);
            }
            if (ex is MmException && (ex.Message.Contains("NoDriver") || ex.Message.Contains("BadDeviceId"))) {
                Hint("由于音频设备变更，音乐播放功能在重启 PCL 后才能恢复！", 2);
                Thread.Sleep(1000000000);
            }
            if (!new[] { "wav", "mp3", "flac" }.Contains(PathUtils.GetExtension(current)) || ex.Message.Contains("0xC00D36C4"))
                Hint("播放音乐失败（" + PathUtils.GetLastPart(current) + "）：PCL 可能不支持此音乐格式，请将格式转换为 .wav、.mp3 或 .flac 后再试", 2);
            else Logger.Error(ex, $"播放音乐失败（{PathUtils.GetLastPart(current)}）", LogBehavior.Toast);
            All.Remove(current); Waiting.Remove(current); RefreshUi();
            Thread.Sleep(2000);
            if (ex is FileNotFoundException) RefreshPlay(true, isFirstLoad);
            else StartPlay(Dequeue(), isFirstLoad);
        } finally {
            if (ReferenceEquals(activeReader, reader)) activeReader = null;
            if (wave is not null) wave.Dispose();
            if (reader is not null) reader.Dispose();
            RefreshUi();
        }
    }
}

/// <summary>
/// A ring of the latest mono samples handed to the audio device, read at the device's playhead. Written on the audio
/// thread, read on the UI thread. Free of NAudio types so the UI can hold it before any playback starts.
/// </summary>
public sealed class MusicSampleTap {
    private readonly float[] ring = new float[1 << 16]; // ~1.4 s at 44.1 kHz: device buffer plus the longest analysis window
    private readonly object gate = new();
    // Frames handed to the device since the track started; counted even while nobody captures, so it stays aligned
    // with the device position. Ring contents are only valid from `capturedFrom` on.
    private long handed, capturedFrom;
    private int generation;
    private Func<long>? clock;
    private volatile bool captureEnabled;

    public bool CaptureEnabled {
        get => captureEnabled;
        set {
            if (captureEnabled == value) return;
            lock (gate) {
                captureEnabled = value;
                capturedFrom = handed;
            }
        }
    }

    /// <summary>Sample rate of the track in use.</summary>
    public int SampleRate { get; private set; } = 44100;

    /// <summary>Starts a new track; returns its generation, which tags its writes and clock.</summary>
    internal int Reset(int sampleRate) {
        lock (gate) {
            Array.Clear(ring); handed = 0; capturedFrom = 0; clock = null; generation++;
            SampleRate = sampleRate > 0 ? sampleRate : 44100;
            return generation;
        }
    }

    /// <summary>Gives the frames the device has played for the track of <paramref name="owner"/>.</summary>
    internal void SetClock(int owner, Func<long> playedFrames) {
        lock (gate) { if (owner == generation) clock = playedFrames; }
    }

    internal void Write(int owner, float[] buffer, int offset, int count, int channels) {
        channels = Math.Max(1, channels);
        lock (gate) {
            if (owner != generation) return;
            int frames = count / channels;
            if (!captureEnabled) { handed += frames; return; }
            int mask = ring.Length - 1;
            for (int index = offset; index + channels <= offset + count; index += channels) {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++) sum += buffer[index + channel];
                ring[(int)(handed++ & mask)] = sum / channels;
            }
        }
    }

    /// <summary>
    /// Copies the <paramref name="destination"/>.Length samples that end <paramref name="lead"/> samples after the
    /// device's playhead (the sound being heard now), so a window can be centred on it. Without a device clock it falls
    /// back to <paramref name="lagSeconds"/> behind the newest sample. False while those samples are not available.
    /// </summary>
    public bool Snapshot(float[] destination, double lagSeconds, int lead = 0) {
        Func<long>? played;
        lock (gate) { if (!captureEnabled) return false; played = clock; }
        long playhead = -1;
        if (played is not null) {
            try { playhead = played(); } catch { playhead = -1; } // the device may be closing between tracks
        }
        lock (gate) {
            if (!captureEnabled) return false;
            long heard = playhead >= 0 ? playhead : handed - (long)(Math.Max(0, lagSeconds) * SampleRate);
            long end = Math.Min(heard + Math.Max(0, lead), handed);
            int length = destination.Length;
            if (length > ring.Length || end - length < capturedFrom || handed - (end - length) > ring.Length) return false;
            int mask = ring.Length - 1;
            for (int index = 0; index < length; index++) destination[index] = ring[(int)((end - length + index) & mask)];
            return true;
        }
    }
}
