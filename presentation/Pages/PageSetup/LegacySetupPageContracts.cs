using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Controls;

namespace PCLCS;

// LF Pages/PageSetup/PageSetup{UI,Launch,System}.xaml(.vb), rooted at
// f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 (repository LICENCE), is the source.
// PCL base 5ef81e490ae46b1100d61e33d0183094c7a80ca8 remains the upstream baseline.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 PageSetupUI/Launch.xaml.cs and
// LauncherMisc/Update pages were reviewed (their Plain Craft Launcher 2/LICENCE).
// Their Config.Profile, dark/video/theme, initial-RAM and new Java rules are not imported.
public enum LegacySetupHint { Blue, Green, Red }
public enum LegacySetupAnimationKind { GridWidth, Opacity, X }
public enum LegacySetupEase { Linear, OutFluentWeak, OutFluentStrong }
public readonly struct LegacySetupAnimation {
    public readonly DependencyObject Target;
    public readonly LegacySetupAnimationKind Kind;
    public readonly double Delta;
    public readonly int Duration;
    public readonly LegacySetupEase Ease;
    public LegacySetupAnimation(DependencyObject target, LegacySetupAnimationKind kind, double delta, int duration, LegacySetupEase ease = LegacySetupEase.Linear) {
        Target = target; Kind = kind; Delta = delta; Duration = duration; Ease = ease;
    }
}
public delegate int LegacySetupMessage(string text, string title = "提示", string button1 = "确定", string button2 = "", string button3 = "", bool warning = false);
public sealed class LegacySetupPageHost {
    public Func<string, object?> Read = null!;
    public Action<string, object?> Write = null!;
    public Action<string> Reset = null!;
    public Func<string, string?> Default = null!;
    public Action<DependencyObject> RefreshSettings = null!, ResetSettings = null!;
    public Func<int> AnimationSuppression = null!;
    public Action<int> AddAnimationSuppression = null!;
    public LegacySetupMessage Message = null!;
    public Action<string, LegacySetupHint, bool> Hint = null!;
    public Func<string> BaseFolder = null!, ImageRoot = null!, AppData = null!, UserDataFolder = null!;
    public Action<string> OpenExplorer = null!, OpenWebsite = null!, DeleteDirectory = null!;
    public Action<Action> InThread = null!, InUi = null!;
    public Action<int> Sleep = null!;
    public Action<string, string> ExtractResource = null!;
    public Func<string, LegacyBitmap> LoadBitmap = null!;
    public Func<string, ImageBrush>? LoadBackgroundBrush;
    public Func<string, string[], string, IEnumerable<string>> SelectImage = null!;
    public Action<IEnumerable<LegacySetupAnimation>, string> StartAnimations = null!;
    public Action<string> StopAnimation = null!;
    public Func<string, bool> AnimationRunning = null!;
}
