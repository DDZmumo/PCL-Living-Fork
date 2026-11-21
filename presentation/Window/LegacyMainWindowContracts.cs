using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Presentation.Animation;
using PCLCS.Startup;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Windowing;

// LF FormMain.xaml.vb at 07dd3503b38b39095c8b92b40ae0454907387f9f is the behavior source.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8; CE stable 1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/FormMain.xaml.cs is a same-origin reference, not the product/window baseline.
public enum LegacyMainHint { Default, Red, Green }
public sealed class LegacyMainMessage {
    public string Text = null!, Title = "提示", Button1 = "确定", Button2 = "", Button3 = "";
    public Action? ThirdAction;
}
public sealed class LegacyMainExtraButton {
    public Action<Func<bool>> SetShowCheck = null!;
    public Action ShowRefresh = null!;
    public Func<bool> Show = null!;
}
public sealed class LegacyMainDialog {
    public bool IsInputOrSelect;
    public Func<Visibility> SecondVisibility = null!, ThirdVisibility = null!;
    public Action First = null!, Second = null!, Third = null!;
}
public sealed class LegacyMainLeftPage {
    public MyPageLeftView Element = null!;
    public Func<int> PageId = null!;
    public Func<int, MyPageRightView> PageGet = null!;
    public Func<int, MyListItemView> Item = null!;
    public Action<int> SyncSelection = null!;
}
public sealed class LegacyMainLogoSettings {
    public FrameworkElement CheckLeft = null!, Text = null!, Change = null!;
    public Action ForceResize = null!;
}
public sealed class LegacyMainDragHelperPort {
    public Action<HwndSource> SetSource = null!;
    public Action AddHook = null!, RemoveHook = null!;
    public Action<Action> ConnectDrop = null!;
    public Func<string[]> Files = null!;
}
/// <summary>Lazy getters retain the real BAML field identities before and after InitializeComponent.</summary>
public sealed class LegacyMainWindowView {
    public Func<bool>? UsesGlassShell;
    /// <summary>True while a shell page covers the launch page (the Enter-to-launch shortcut is then suppressed).</summary>
    public Func<bool>? SuppressesLaunchKey;
    public Window Window = null!;
    public Func<Grid> PanBack = null!, PanForm = null!, PanMain = null!, PanTitle = null!, PanTitleMain = null!, PanTitleInner = null!, PanMsg = null!;
    public Func<Border> BorderForm = null!, PanMainLeft = null!, PanMainRight = null!;
    public Func<RectangleGeometry> RectForm = null!;
    public Func<Rectangle> RectLeftBackground = null!, RectLeftShadow = null!;
    public Func<Canvas> ImgBack = null!;
    public Func<StackPanel> PanTitleSelect = null!;
    public Func<TextBlock> LabTitleInner = null!, LabTitleLogo = null!;
    public Func<System.Windows.Shapes.Path> ShapeTitleLogo = null!;
    public Func<MyImageView> ImageTitleLogo = null!;
    public Func<TranslateTransform> TransformPos = null!;
    public Func<RotateTransform> TransformRotate = null!;
    public Func<int, UIElement> Resizer = null!;
    public Func<LegacyMainExtraButton> ExtraDownload = null!, ExtraBack = null!, ExtraApril = null!, ExtraShutdown = null!;
}
/// <summary>The factories return the original LF animation struct and easing identities into the same real AniGroups.</summary>
public sealed class LegacyMainAnimations<TAnimation> where TAnimation : struct {
    public Func<object, double, int, int, AnimationEase?, bool, TAnimation> Opacity = null!, X = null!, Width = null!;
    public Func<Action<double>, double, int, int, AnimationEase?, bool, TAnimation> Double = null!;
    public Func<Action, int, bool, TAnimation> Code = null!;
    public Func<AnimationEasePower, AnimationEase> OutFluent = null!, InFluent = null!, OutBack = null!, InoutFluent = null!;
    public Action<TAnimation[], string, bool> Start = null!;
    public Action<string> Stop = null!;
    public Func<double> Speed = null!;
}
/// <summary>Original field stores and existing module/page/platform primitives. No navigation, shutdown or settings policy is delegated.</summary>
public sealed class LegacyMainWindowHost<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    public Func<int> AnimationSuppression = null!;
    public Action<int> WriteAnimationSuppression = null!;
    public Func<int, string> DescribeSubType = null!;
    public HwndSourceHook WindowHook = null!;
    public DragEventHandler DragHandler = null!;
    public Func<bool> Loaded = null!, SizeSaveable = null!, Hidden = null!, ChangingPage = null!;
    public Action<bool> WriteLoaded = null!, WriteSizeSaveable = null!, WriteHidden = null!, WriteChangingPage = null!, WriteSystemTimeChanged = null!;
    public Func<TStack> CurrentPage = null!, LastPage = null!;
    public Action<TStack> WriteCurrentPage = null!, WriteLastPage = null!;
    public Func<List<TStack>> PageStack = null!;
    public Func<MyPageLeftView> CurrentLeft = null!;
    public Action<MyPageLeftView> WriteLeft = null!;
    public Func<MyPageRightView> CurrentRight = null!;
    public Action<MyPageRightView> WriteRight = null!;
    public Action AssignGlobalWindow = null!, CreateLaunchLeft = null!, CreateLaunchRight = null!;
    public Func<string, int> ReadInteger = null!;
    public Func<string, string> ReadString = null!;
    public Func<string, bool> ReadBoolean = null!, HasSaved = null!;
    public Func<string, object?> DefaultValue = null!;
    public Action<string, object?> WriteSetting = null!;
    public Action<string> ResetSetting = null!;
    public Func<string> UpdateLogIdentity = null!;
    public Func<string, string, bool> HasIniKey = null!;
    public Action<int> ThemeRefresh = null!;
    public Action ThemeRefreshMain = null!, RefreshHidden = null!;
    public Action<bool, bool> RefreshBackground = null!, RefreshMusic = null!;
    public Func<Window, WindowResizer> CreateResizer = null!;
    public Func<double> WorkingAreaHeight = null!, WorkingAreaWidth = null!;
    public Func<double, double> PixelToWpf = null!;
    public Func<bool> HasStartWindow = null!, IsAdmin = null!, IsDebug = null!, PackEnabled = null!;
    public Action<TimeSpan> CloseStartWindow = null!;
    public Action<Window> ApplyPackVisibility = null!;
    public Func<int, bool> PackBlocksPage = null!;
    public Func<int, int> NormalizeSetupPage = null!, NormalizeInstancePage = null!;
    public Func<string, string> DisplayPackTitle = null!;
    public Func<LegacyMainLogoSettings?> LogoSettings = null!;
    public int BuildType, VersionCode;
    public Func<int> Dpi = null!;
    public bool LegacyLink;
    public Func<string> VersionBaseName = null!, VersionBranchMain = null!, LauncherName = null!, ReleasesUrl = null!, IssuesUrl = null!, TempFolder = null!, ExecutablePath = null!;
    public Func<bool, string, bool> ShowReleaseNotes = (_, _) => false;
    public Func<long> Clock = null!, StartTick = null!;
    public Action<long> WriteStartTick = null!;
    public Func<IntPtr> Handle = null!;
    public Action<IntPtr> WriteHandle = null!, SetForeground = null!;
    public Func<DateTime> ApplicationOpenTime = null!;
    public Action<Action> Ui = null!, UiWait = null!;
    public Action<Action, bool> UiPosted = null!;
    public Action<Action, string, ThreadPriority> NewThread = null!;
    public Func<int, int, int> RandomInteger = null!;
    public Action<string, LegacyMainHint> Hint = null!;
    public Func<LegacyMainMessage, int> Message = null!;
    public Action<string> OpenWebsite = null!;
    public Action StartAnimationLoop = null!, StartMainTimer = null!, JavaInitialize = null!, ClearTaskTemp = null!, CheckUpdates = null!, StartFolderLoader = null!, StartClientList = null!;
    public Func<LegacyMainDragHelperPort> CreateDragHelper = null!;
    public Func<LegacyMainDialog> FirstDialog = null!;
    public Func<int> GlobalMessageCount = null!;
    public Action<int> GlobalNavigate = null!;
    public Action<System.Windows.Input.MouseEventArgs> WriteLastMouseArg = null!;
    public Func<bool> WaitingMessages = null!, IsLaunching = null!, HiddenVersions = null!, HiddenForceShow = null!;
    public Action<bool> WriteHiddenVersions = null!, WriteHiddenForceShow = null!;
    public Action CancelLaunch = null!, LaunchButton = null!, ReloadCurrentJava = null!, ReloadMods = null!, ExitInstallSelection = null!;
    public Func<bool> IsInstallSelection = null!;
    public Action<bool> ReloadInstanceList = null!;
    public Func<FrameworkElement, bool> CanRefresh = null!;
    public Action<FrameworkElement> Refresh = null!;
    public Func<bool> AprilEnabled = null!, AprilGivenUp = null!;
    public Action<bool> WriteAprilGivenUp = null!;
    public Func<ScaleTransform> AprilScale = null!;
    public Func<TInstance?> SettingsInstance = null!, SelectedInstance = null!;
    public Func<TInstance, string> InstanceName = null!;
    public Func<TInstance?, string>? InstanceSettingsTitlePrefix;
    public Func<TInstance, bool> InstanceLocked = null!;
    public Func<TInstance?, bool> InstanceBlocksNavigation = null!;
    public Action<string, object?, TInstance> WriteInstanceSetting = null!;
    public bool UsesGlobalAccounts;
    public Func<string, string?> ValidateHttp = null!;
    public Action ReloadInstanceSetup = null!, RefreshLaunchPage = null!, RefreshCustomHome = null!;
    public Func<List<string>, bool> InstallMods = null!;
    public Action<string> InstallModpack = null!;
    public Func<LegacyMainCrashAnalyzer> CreateCrashAnalyzer = null!;
    public Action<int> EnsureLeftPage = null!, EnsureRightPage = null!;
    public Func<int, LegacyMainLeftPage> LeftPage = null!;
    public Func<int, MyPageRightView> RightPage = null!;
    public Func<MyPageLeftView> NewBlankLeft = null!;
    public Func<object, string> HelpTitle = null!;
    public Func<bool> HasDownloadingTask = null!, HasLaunchOperation = null!, HasRunningMinecraft = null!;
    public Func<bool> HasBlockingFileOperation = () => false;
    public Func<IEnumerable<Action>> TaskbarCancels = null!, WatcherKills = null!;
    public Action CancelLaunchOperation = null!, MusicPause = null!, MusicNext = null!;
    public Func<bool, bool, bool> LinkTryExit = null!;
    public Func<MySliderView?> DragControl = null!;
    public Action ClearDragControl = null!;
    public Action ForceExit = null!;
    public LegacyMainAnimations<TAnimation> Animations = null!;
}
public sealed class LegacyMainCrashAnalyzer {
    public Action<string> Import = null!;
    public Func<bool> Prepare = null!;
    public Action Analyze = null!;
    public Action<bool, List<string>> Output = null!;
}
