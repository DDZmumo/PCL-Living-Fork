using System.Collections.Generic;
using MeloongCore;
using PCLLF.Presentation.Shell;

namespace PCL;

// LF C031b native facade. Original public fields remain the sole state; no NAudio-typed eager initializer.
public static class ModMusic {
    public static List<string> MusicWaitingList;
    public static List<string> MusicAllList;
    public static object MusicNAudio;
    public enum MusicStates { Stop, Play, Pause }
    private static readonly MusicRuntime Native = new(new MusicHost {
        All = () => MusicAllList, Waiting = () => MusicWaitingList,
        SetAll = value => MusicAllList = value, SetWaiting = value => MusicWaitingList = value,
        Playback = () => MusicNAudio, SetPlayback = value => MusicNAudio = value,
        BasePath = () => Paths.Base, BooleanSetting = name => Settings.Get<bool>(name), IntegerSetting = name => Settings.Get<int>(name),
        RandomInteger = ModBase.RandomInteger, Ui = work => ModBase.RunInUi(work), Thread = ModBase.RunInThread,
        StartThread = (work, name, priority) => ModBase.RunInNewThread(work, name, priority),
        RefreshSettings = () => ModMain.FrmMain?.RefreshGlassShell(),
        Hint = (text, type, log) => ModMain.Hint(text, (ModMain.HintType)type, log)
    });
    public static MusicStates MusicState => (MusicStates)(int)Native.State;
    public static string MusicCurrentName => Native.CurrentName;
    public static double MusicProgress => Native.Progress;
    /// <summary>Holds the first automatic start until <see cref="MusicReleaseStart"/> (the shell's splash).</summary>
    public static void MusicHoldStart() => Native.HoldStart();
    public static void MusicReleaseStart() => Native.ReleaseStart();
    /// <summary>The latest samples sent to the audio device (for the background visualiser).</summary>
    public static MusicSampleTap MusicSamples => Native.Samples;
    public static int MusicCount => Native.Count;
    public static IReadOnlyList<string> MusicTracks => Native.Tracks;
    public static string MusicCurrentPath => Native.CurrentPath;
    public static System.TimeSpan MusicPosition => Native.Position;
    public static System.TimeSpan MusicDuration => Native.Duration;
    public static void MusicSeek(double Fraction) => Native.Seek(Fraction);
    public static void MusicControlPrevious() => Native.ControlPrevious();
    public static void MusicPlayTrack(string Path) => Native.PlayTrack(Path);
    public static void MusicReshuffle() => Native.Reshuffle();
    public static void MusicControlPause() => Native.ControlPause();
    public static void MusicControlNext() => Native.ControlNext();
    public static void MusicRefreshPlay(bool ShowHint, bool IsFirstLoad = false) => Native.RefreshPlay(ShowHint, IsFirstLoad);
    public static bool MusicPause() => Native.Pause();
    public static bool MusicResume() => Native.Resume();
}
