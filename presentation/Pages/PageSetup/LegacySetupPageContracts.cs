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
public enum LegacySetupShellElement {
    TitlePanel, TitleDownload, TitleLink, TitleSetup, TitleOther,
    ModifyInstance, SetupLaunch, SetupUI, SetupLink, SetupSystem, SetupNavigation,
    OtherHelp, OtherAbout, OtherTest, OtherNavigation, TitleFavorites
}
public enum LegacySetupShellPart { InstanceLeft, SetupLeft, OtherLeft }
public enum LegacySetupShellPage { Launch, InstanceSelect, InstanceSetup }
public sealed class LegacySetupUiHost {
    public LegacySetupPageHost Common = null!;
    public Func<LegacyPageSetupUIController?> CurrentUI = null!;
    public Func<bool> ReadHiddenForceShow = null!;
    public Action<bool> WriteHiddenForceShow = null!;
    public Func<bool> HasMain = null!;
    public Func<Panel> MainPanel = null!;
    public Func<Panel> MainBackground = null!;
    public Func<MyImageView> TitleLogo = null!;
    public Action<Panel, Panel> ApplyBackground = null!;
    public Action<Panel> UseDefaultBackground = null!, MarkCustomBackground = null!;
    public Action UpdateBackgroundAndTitle = null!;
    public Func<IList<string>, string> RandomPicture = null!;
    public Func<List<string>> MusicAll = null!;
    public Action ClearMusicPlayer = null!;
    public Action<List<string>> AssignWaitingMusic = null!, AssignAllMusic = null!;
    public Action<bool> RefreshMusic = null!;
    public Action RefreshHome = null!;
    public Action<DependencyObject, bool> SetHomeHintEvent = null!;
    public Action<DependencyObject, string> SetHomeHintEventData = null!;
    public Func<LegacySetupShellElement, FrameworkElement> Shell = null!;
    public Func<LegacySetupShellPart, bool> HasShell = null!;
    public Func<LegacySetupShellPage, bool> IsPage = null!;
    public Action RefreshLaunchButtons = null!, RefreshEmptyDownload = null!, RefreshDisabledModsDownload = null!, ApplyModpackVisibility = null!;
    public Func<bool> HasDisabledModsPage = null!;
}
public sealed class LegacySetupLaunchHost<TInstance, TSkin> where TInstance : class where TSkin : struct {
    public LegacySetupPageHost Common = null!;
    public Func<LegacyPageSetupLaunchController<TInstance, TSkin>?> CurrentLaunch = null!;
    public Func<TInstance?> SelectedInstance = null!;
    public Func<TInstance, string> InstanceName = null!, InstanceIndiePath = null!;
    public Func<TInstance, bool> InstanceLoaded = null!, InstanceModable = null!, InstanceOptiFine = null!;
    public Action<TInstance> LoadInstance = null!, AssignSetupInstance = null!, ImportPersonalOptions = null!;
    public Action ShowInstanceSetup = null!;
    public Func<bool> PersonalProfileExists = null!, IsCurrentRamPage = null!;
    public Action ApplyInstanceVisibility = null!;
    public Func<ulong> AvailableMemory = null!, TotalMemory = null!;
    public Func<TSkin> SelectSkin = null!;
    public Func<TSkin, bool> SkinValid = null!, SkinSlim = null!;
    public Func<TSkin, string> SkinPath = null!;
    public Action<bool> StartLegacySkin = null!;
    public Action SaveSkin = null!, RefreshSkinCache = null!;
    public Action StartJavaRefresh = null!, SearchJava = null!, ImportJava = null!, UpdateJavaLists = null!;
    public Action<Java> RemoveJava = null!;
    public Func<bool> JavaRefreshRunning = null!;
    public Func<MyListItemView> NewJavaItem = null!;
    public Func<MyIconButtonView> NewJavaButton = null!;
    public Action<MyListItemView, Action> ConnectItemClick = null!;
    public Action<MyIconButtonView, Action> ConnectIconClick = null!;
    public Action<MyListItemView, IEnumerable<MyIconButtonView>> SetJavaButtons = null!;
    public string JavaUpLogo = null!, JavaDownLogo = null!, JavaDeleteLogo = null!, JavaOpenLogo = null!;
    public Func<bool> HasAdminRole = null!;
}
public sealed class LegacySetupSystemHost {
    public LegacySetupPageHost Common = null!;
    public Func<bool> ModpackEnabled = null!, CanEnableModpack = null!;
    public Action<MyCheckBoxView, bool> ToggleModpack = null!;
    public Func<string> LauncherDisplayName = null!, LauncherVersion = null!;
    public Action CheckLauncherUpdate = null!;
    public Func<int> PreferredUpdateChannel = null!;
    public Func<int, Func<bool>, bool> ChangeUpdateChannel = null!;
}
internal static class LegacySetupText {
    internal static string? String(object? value) {
#if !NETFRAMEWORK
        if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number))
            return LegacyPackMetadataNumberFormatting.Format(number, 15, CultureInfo.CurrentCulture);
#endif
        return Conversions.ToString(value);
    }
    internal static string Number(double value) => String(value)!;
}
public sealed class LegacyPageSetupUIView {
    public Func<DependencyObject> Page = null!;
    public Func<ScrollViewer> PanBack = null!;
    public Func<StackPanel> PanMain = null!;
    public Func<MyCardView> CardLauncher = null!;
    public Func<MySliderView> SliderLauncherTransparent = null!, SliderPanelOpacity = null!;
    public Func<Border> PanOfficialTheme = null!;
    public Func<MyButtonView> BtnOfficialTheme = null!;
    public Func<MyCardView> CardBackground = null!;
    public Func<MyComboBoxView> ComboBackgroundMode = null!;
    public Func<Grid> PanBackgroundSuit = null!;
    public Func<Grid> PanBackgroundOpacity = null!;
    public Func<MySliderView> SliderBackgroundOpacity = null!;
    public Func<Grid> PanBackgroundBlur = null!;
    public Func<MySliderView> SliderBackgroundBlur = null!;
    public Func<MyCheckBoxView> CheckBackgroundColorful = null!;
    public Func<MyButtonView> BtnBackgroundOpen = null!;
    public Func<MyButtonView> BtnBackgroundRefresh = null!;
    public Func<MyButtonView> BtnBackgroundClear = null!;
    public Func<MyCardView> CardMusic = null!;
    public Func<Grid> PanMusicVolume = null!;
    public Func<MySliderView> SliderMusicVolume = null!;
    public Func<StackPanel> PanMusicDetail = null!;
    public Func<MyCheckBoxView> CheckMusicRandom = null!;
    public Func<MyCheckBoxView> CheckMusicAuto = null!;
    public Func<MyCheckBoxView> CheckMusicStart = null!;
    public Func<MyCheckBoxView> CheckMusicStop = null!;
    public Func<MyButtonView> BtnMusicOpen = null!;
    public Func<MyButtonView> BtnMusicRefresh = null!;
    public Func<MyButtonView> BtnMusicClear = null!;
    public Func<MyCardView> CardLogo = null!;
    public Func<MyRadioBoxView> RadioLogoType0 = null!;
    public Func<MyRadioBoxView> RadioLogoType1 = null!;
    public Func<MyRadioBoxView> RadioLogoType2 = null!;
    public Func<MyRadioBoxView> RadioLogoType3 = null!;
    public Func<MyCheckBoxView> CheckLogoLeft = null!;
    public Func<Grid> PanLogoText = null!;
    public Func<MyTextBoxView> TextLogoText = null!;
    public Func<Grid> PanLogoChange = null!;
    public Func<MyButtonView> BtnLogoChange = null!;
    public Func<MyButtonView> BtnLogoDelete = null!;
    public Func<MyCardView> CardCustom = null!;
    public Func<MyRadioBoxView> RadioCustomType0 = null!;
    public Func<MyRadioBoxView> RadioCustomType3 = null!;
    public Func<MyRadioBoxView> RadioCustomType1 = null!;
    public Func<MyRadioBoxView> RadioCustomType2 = null!;
    public Func<MyHintView> HintCustom = null!;
    public Func<MyHintView> HintCustomWarn = null!;
    public Func<WrapPanel> PanCustomLocal = null!;
    public Func<MyButtonView> BtnCustomRefresh = null!;
    public Func<MyButtonView> BtnCustomFile = null!;
    public Func<MyButtonView> BtnCustomTutorial = null!;
    public Func<MyButtonView> BtnCustomFolder = null!;
    public Func<Grid> PanCustomNet = null!;
    public Func<MyTextBoxView> TextCustomNet = null!;
    public Func<Grid> PanCustomPreset = null!;
    public Func<MyCheckBoxView> CheckRecentWorlds = null!;
    public Func<MyCardView> CardSwitch = null!;
    public Func<MyCheckBoxView> CheckHiddenPageDownload = null!;
    public Func<MyCheckBoxView> CheckHiddenPageLink = null!;
    public Func<MyCheckBoxView> CheckHiddenPageSetup = null!;
    public Func<MyCheckBoxView> CheckHiddenPageOther = null!;
    public Func<MyCheckBoxView> CheckHiddenSetupLaunch = null!;
    public Func<MyCheckBoxView> CheckHiddenSetupLink = null!;
    public Func<MyCheckBoxView> CheckHiddenSetupUI = null!;
    public Func<MyCheckBoxView> CheckHiddenSetupSystem = null!;
    public Func<MyCheckBoxView> CheckHiddenOtherHelp = null!;
    public Func<MyCheckBoxView> CheckHiddenOtherAbout = null!;
    public Func<MyCheckBoxView> CheckHiddenOtherTest = null!;
    public Func<MyCheckBoxView> CheckLauncherEmail = null!;
    public Func<MyCheckBoxView> CheckHiddenFunctionSelect = null!;
    public Func<MyCheckBoxView> CheckHiddenFunctionModUpdate = null!;
    public Func<MyCheckBoxView> CheckHiddenFunctionHidden = null!;
    public Func<MyCheckBoxView> CheckHiddenFunctionModify = null!;
}

public sealed class LegacyPageSetupLaunchView {
    public Func<DependencyObject> Page = null!;
    public Func<ScrollViewer> PanBack = null!;
    public Func<StackPanel> PanMain = null!;
    public Func<MyCheckBoxView> CheckPersonalOptionsSync = null!;
    public Func<MyButtonView> BtnImportPersonalOptions = null!;
    public Func<MyCardView> CardArgument = null!;
    public Func<TextBlock> LabArgumentIndie = null!;
    public Func<MyComboBoxView> ComboArgumentIndieV2 = null!;
    public Func<MyComboBoxView> ComboArgumentVisibie = null!;
    public Func<Grid> PanArgumentWindow = null!;
    public Func<MyComboBoxView> ComboArgumentWindowType = null!;
    public Func<MyTextBoxView> TextArgumentWindowWidth = null!;
    public Func<TextBlock> LabArgumentWindowMiddle = null!;
    public Func<MyTextBoxView> TextArgumentWindowHeight = null!;
    public Func<MyRadioBoxView> RadioRamType0 = null!;
    public Func<MyRadioBoxView> RadioRamType1 = null!;
    public Func<MySliderView> SliderRamCustom = null!;
    public Func<MyCheckBoxView> CheckArgumentRam = null!;
    public Func<Grid> PanRamDisplay = null!;
    public Func<ColumnDefinition> ColumnRamUsed = null!;
    public Func<ColumnDefinition> ColumnRamGame = null!;
    public Func<ColumnDefinition> ColumnRamEmpty = null!;
    public Func<Rectangle> RectRamUsed = null!;
    public Func<Rectangle> RectRamGame = null!;
    public Func<Rectangle> RectRamEmpty = null!;
    public Func<TextBlock> LabRamUsedTitle = null!;
    public Func<TextBlock> LabRamGameTitle = null!;
    public Func<TextBlock> LabRamUsed = null!;
    public Func<TextBlock> LabRamTotal = null!;
    public Func<TextBlock> LabRamGame = null!;
    public Func<MyCardView> CardSkin = null!;
    public Func<MyRadioBoxView> RadioSkinType0 = null!;
    public Func<MyRadioBoxView> RadioSkinType1 = null!;
    public Func<MyRadioBoxView> RadioSkinType2 = null!;
    public Func<MyRadioBoxView> RadioSkinType3 = null!;
    public Func<MyRadioBoxView> RadioSkinType4 = null!;
    public Func<Grid> PanSkinID = null!;
    public Func<MyButtonView> BtnSkinSave = null!;
    public Func<MyButtonView> BtnSkinCache = null!;
    public Func<Grid> PanSkinChange = null!;
    public Func<MyButtonView> BtnSkinChange = null!;
    public Func<MyButtonView> BtnSkinDelete = null!;
    public Func<MyIconButtonView> BtnAdvanceJvmReset = null!;
    public Func<MyTextBoxView> TextAdvanceJvm = null!;
    public Func<MyTextBoxView> TextAdvanceGame = null!;
    public Func<MyTextBoxView> TextAdvanceRun = null!;
    public Func<MyCheckBoxView> CheckAdvanceRunWait = null!;
    public Func<Grid> PanAdvanceJava = null!;
    public Func<MyComboBoxView> ComboAdvanceJava = null!;
    public Func<TextBlock> LabAdvanceJava = null!;
    public Func<MyIconButtonView> BtnAdvanceJavaSearch = null!;
    public Func<FrameworkElement> BtnSwitch = null!;
}

public sealed class LegacyPageSetupSystemView {
    public bool UpdatesOnSeparatePage;
    public Func<MyComboBoxView> ComboLauncherUpdateSource = null!, ComboLauncherUpdateChannel = null!;
    public Func<Func<bool>> ConfirmPrereleaseUpdates = null!;
    public Func<DependencyObject> Page = null!;
    public Func<ScrollViewer> PanBack = null!;
    public Func<StackPanel> PanMain = null!;
    public Func<MyComboBoxView> ComboDownloadSource = null!;
    public Func<MyComboBoxView> ComboDownloadVersion = null!;
    public Func<MySliderView> SliderDownloadThread = null!;
    public Func<MySliderView> SliderDownloadSpeed = null!;
    public Func<MyComboBoxView> ComboDownloadMod = null!;
    public Func<MyComboBoxView> ComboDownloadTranslateV2 = null!;
    public Func<MyComboBoxView> ComboModLocalNameStyle = null!;
    public Func<MyCheckBoxView> CheckHelpChinese = null!;
    public Func<TextBlock> LabLauncherVersion = null!;
    public Func<MyComboBoxView> ComboSystemActivity = null!;
    public Func<MyTextBoxView> TextSystemCache = null!;
    public Func<MyButtonView> BtnSystemUpdate = null!;
    public Func<MyButtonView> BtnSystemSettingExp = null!;
    public Func<MyButtonView> BtnSystemSettingImp = null!;
    public Func<TextBlock> LabModpackMode = null!;
    public Func<MyCheckBoxView> CheckModpackMode = null!;
    public Func<MyCardView> CardDebug = null!;
    public Func<Grid> PanDebugAnim = null!;
    public Func<MySliderView> SliderDebugAnim = null!;
    public Func<MyCheckBoxView> CheckDebugSkipCopy = null!;
    public Func<MyCheckBoxView> CheckDebugMode = null!;
    public Func<MyCheckBoxView> CheckDebugDelay = null!;
}
