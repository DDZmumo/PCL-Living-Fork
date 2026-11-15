using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawPath = System.Windows.Shapes.Path;

namespace PCLLF.Presentation.Launch;

// Behavior source: current LF PageLaunchLeft/Right and LF f4aa0fae lineage, LICENCE.
// Upstream PCL5ef81e490ae46b1100d61e33d0183094c7a80ca8 was compared (+5/-5 and
// +150/-5 LF changes); CEstable1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/Pages/PageLaunch/PageLaunch{Left,Right}.xaml.cs was reviewed
// under that application's LICENCE. LF skin/provider/cancel/home presets and cache
// policies remain; no CE profile/localization/source replacement/live watcher.


public enum LegacyLaunchPanelState { Waiting, Loading, Finished, Failed, Canceled }
public enum LegacyLoginPageKind { None, Legacy, Nide, NideSkin, Auth, AuthSkin, Ms, MsSkin }
public sealed class LegacyLaunchItemPort {
    public Func<LegacyLaunchPanelState> State = null!;
    public Func<string> Name = null!;
    public Func<LegacyLaunchItemPort?> Parent = null!;
}
public sealed class LegacyLaunchJobPort {
    public Func<LegacyLaunchPanelState> State = null!;
    public Func<double> Progress = null!;
    public Func<IEnumerable<LegacyLaunchItemPort>> Items = null!;
    public Action<bool> Wait = null!;
    public Action Cancel = null!;
}
public sealed class LegacyLaunchLeftView {
    public Func<TranslateTransform> AprilPosition = null!;
    public Func<FrameworkElement> Input = null!, Launching = null!, LaunchingHint = null!, LaunchingInfo = null!, LoginPanel = null!;
    public Func<TextBlock> Method = null!, Name = null!, Stage = null!, Title = null!, Progress = null!, ProgressLeft = null!, Download = null!, DownloadLeft = null!, Hint = null!, SingleTypeLabel = null!;
    public Func<ColumnDefinition> FinishedColumn = null!, UnfinishedColumn = null!;
    public Func<Panel> LoginChildren = null!;
    public Func<FrameworkElement> TypePanel = null!, SingleTypePanel = null!, MicrosoftRadio = null!, OfflineRadio = null!;
    public Func<DrawPath> SingleTypePath = null!;
    public Func<bool> MicrosoftChecked = null!, LaunchButtonEnabled = null!, LaunchButtonLoaded = null!;
    public Func<string> LaunchButtonText = null!;
    public Action<string> CheckNamedRadio = null!, RequireNamedRadio = null!;
    public Action<bool> SetLoadingRun = null!;
    public Action<Action> RenderDispatchWait = null!;
}
public sealed class LegacyLaunchButtonView {
    public Action<string> Text = null!, VersionText = null!;
    public Action<bool> EnableLaunch = null!, EnableVersion = null!;
    public Action<Visibility> VersionVisibility = null!, MoreVisibility = null!;
    public Func<Visibility> ReadVersionVisibility = null!;
}
public sealed class LegacyLaunchLeftHost<TInstance> where TInstance : class {
    public Func<string, string> ReadString = null!, ReadIniVersion = null!;
    public Func<string, int> ReadInteger = null!, ReadInstanceInteger = null!;
    public Func<string, bool> ReadBoolean = null!;
    public Func<string, string> ReadInstanceString = null!;
    public Action<string, object> WriteSetting = null!;
    public Func<int> LoginType = null!;
    public Action<int> SetLoginType = null!, ChangeAnimationControl = null!;
    public Func<TInstance?> Selected = null!;
    public Action<TInstance?> SetSelected = null!, SetSetupInstance = null!;
    public Func<string, TInstance> CreateInstance = null!;
    public Func<TInstance, bool> CheckInstance = null!;
    public Action<TInstance> LoadInstance = null!;
    public Func<TInstance, string> InstancePath = null!, InstanceName = null!, InstanceLogo = null!;
    public Func<IEnumerable<List<TInstance>>> Instances = null!;
    public Func<string> Folder = null!, FirstFolderLocation = null!, BasePath = null!;
    public Action<string> SetFolder = null!;
    public Func<string, bool> IsParentPath = null!;
    public Action<string> WaitForInstanceFolder = null!;
    public Func<LegacyLaunchPanelState> InstanceListState = null!, FolderListState = null!, LaunchState = null!;
    public Action<bool> WaitForFolders = null!;
    public Action CreateCurrentFolder = null!;
    public Func<string, LegacyLaunchJobPort?> InstallModpack = null!;
    public Action<Action> BindListStates = null!, Dispatch = null!, RunInThread = null!;
    public Action<Action, string, ThreadPriority> RunInNewThread = null!;
    public Func<int, int, int> RandomInteger = null!;
    public Func<string> LoginAble = null!, CommandLine = null!, RandomHint = null!;
    public Action StartAutoLogin = null!;
    public Func<LegacyLoginPageKind, FrameworkElement?> ReadPage = null!;
    public Func<LegacyLoginPageKind, FrameworkElement> CreatePage = null!;
    public Action<LegacyLoginPageKind, FrameworkElement> StorePage = null!;
    public Action<FrameworkElement?, bool> ReloadPage = null!;
    public Func<string> ShieldLogo = null!, OfflineLogo = null!, CardLogo = null!;
    public Func<bool> ExportScriptSafe = null!, ExportScript = null!;
    public Action ResetLaunchProcessAndWatcher = null!;
    public Func<bool> RightPageAllowsLaunch = null!, AprilEnabled = null!, AprilGiveUp = null!;
    public Action<bool> SetAprilGiveUp = null!;
    public Action ResetGlobalAprilTransforms = null!, RefreshAprilButton = null!;
    public Func<bool> ModpackMode = null!, HiddenForceShow = null!, SelectedInstanceLocked = null!;
    public Func<string, bool> AllowsInstance = null!;
    public Action ApplyModpackLayout = null!;
    public LegacyLaunchButtonView GlobalButtons = null!;
    public Action<string> HintError = null!, LaunchLog = null!;
    public Action StartGame = null!, NavigateInstances = null!, NavigateDownloadInstall = null!, NavigateInstanceSetup = null!;
    public Action<int, int> TestNavigate = null!;
    public Action<int> EndProgramForce = null!;
    public Func<bool> HasRealLaunch = null!, HasWatcher = null!;
    public Func<LegacyLaunchJobPort> RealLaunch = null!;
    public Action KillWatcher = null!;
    public Action? CancelLaunch;
    public Func<Process?> LaunchProcess = null!;
    public Func<long> NetSpeed = null!;
    public Func<IEnumerable<LegacyLaunchItemPort>> NetTasks = null!;
    public Func<long, string> FormatByteSize = null!;
}
