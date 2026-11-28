using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MeloongCore;
using PCLCS;
using PCLCS.Startup;
using PCLLF.Presentation.Animation;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Windowing;
using WindowCore = PCLLF.Presentation.Windowing.LegacyMainWindowController<PCL.FormMain.PageStackData, PCL.ModMinecraft.McInstance, PCL.ModAnimation.AniData>;
using WindowHost = PCLLF.Presentation.Windowing.LegacyMainWindowHost<PCL.FormMain.PageStackData, PCL.ModMinecraft.McInstance, PCL.ModAnimation.AniData>;

namespace PCL
{
    // Native facade of the LF window. The production WindowCore owns the complete C031 behavior.
    public partial class FormMain : Window
    {
        private bool IsWindowLoadFinished = false;
        private readonly WindowCore Controller;

        public FormMain() : this(null) { }

        // Configure existing platform ports before construction for an isolated native
        // window host. The normal launcher always uses the original ports unchanged.
        internal FormMain(Action<WindowHost> ConfigureHost)
        {
            // VB's instance initializer captured this; C# forbids that initializer, so create
            // the same lazy ports before the first original constructor operation instead.
            WindowHost Host = CreateWindowHost();
            ConfigureHost?.Invoke(Host);
            Controller = new WindowCore(CreateWindowView(), Host);
            Loaded += FormMain_Loaded;
            Closing += FormMain_Closing;
            SizeChanged += (_, _) => FormMain_SizeChanged();
            Loaded += (_, _) => FormMain_SizeChanged();
            KeyDown += FormMain_KeyDown;
            MouseDown += FormMain_MouseDown;
            Activated += (_, _) => FormMain_Activated();
            Drop += FrmMain_Drop;
            MouseMove += FormMain_MouseMove;
            Controller.ConstructBeforeXaml();
            InitializeComponent();
            PrepareGlassShell();
            Controller.ConstructAfterXaml();
            AttachGlassShell();
        }
        internal static void ToggleLauncherColorMode()
            => Settings.Set("UiLauncherColorMode", Settings.Get<int>("UiLauncherColorMode") == 1 ? 0 : 1);

        internal void ShowUpdateLog() => Controller.ShowUpdateLog();
        private void FormMain_Loaded(object sender, RoutedEventArgs e)
        {
            StartGlassLaunch();
            Controller.Loaded();
        }
        private void UpgradeSub(int LastVersionCode) => Controller.Upgrade(LastVersionCode);
        private void DowngradeSub(int LastVersionCode) => Controller.Downgrade(LastVersionCode);
        private void FormMain_Closing(object sender, CancelEventArgs e) { e.Cancel = true; EndProgram(true); }
        public void EndProgram(bool SendWarning) {
            if (MmcLaunch.KeepAliveWhileSessionsFinish()) { Hidden = true; return; }
            Controller.EndProgram(SendWarning);
        }
        private static bool IsLogShown = false;
        public static void EndProgramForce(ModBase.ProcessReturnValues ReturnCode = ModBase.ProcessReturnValues.Success)
            => LegacyMainTermination.EndProgramForce((int)ReturnCode, CreateTerminationHost());
        public bool IsSizeSaveable;
        private void FormMain_SizeChanged() => Controller.SizeChanged();
        public static void UpdateBackgroundAndTitleBar(object value) => ModMain.FrmMain?.UpdateBackgroundAndTitleBar();
        public void UpdateBackgroundAndTitleBar() => glassShell?.RefreshWallpaper(true);
        internal void UpdateTitleBar() => RefreshGlassShell();
        private void FormMain_KeyDown(object sender, KeyEventArgs e) => Controller.KeyDown(e);
        private void FormMain_MouseDown(object sender, MouseButtonEventArgs e) => Controller.MouseDown(e);
        private void TriggerPageBack() => Controller.TriggerPageBack();
        private void FormMain_Activated() => Controller.Activated();

        // The original uninitialized VB Static locals were per-window fields, without a
        // static-initialization lock. Pass those same two live slots to the controller.
        private IDataObject PrevDragData;
        private DragDropEffects PrevDragEffects;
        private void HandleDrag(object sender, DragEventArgs e)
            => Controller.HandleDrag(sender, e, ref PrevDragData, ref PrevDragEffects);
        private void FrmMain_Drop(object sender, DragEventArgs e) => Controller.Drop(sender, e);
        private void FileDrag(IEnumerable<string> FilePathList) => Controller.FileDrag(FilePathList);
        public bool IsSystemTimeChanged = false;
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            => Controller.WndProc(hwnd, msg, wParam, lParam, ref handled);
        private bool _Hidden = false;
        public bool Hidden { get => _Hidden; set => Controller.SetHidden(value); }
        public void ShowWindowToTop() => Controller.ShowWindowToTop();

        public enum PageType
        {
            Launch = 0, Download = 1, Link = 2, Setup = 3, Other = 4,
            InstanceSelect = 5, TaskManager = 6, InstanceSetup = 7, ResourceDetail = 8, HelpDetail = 9, Favorites = 10
        }
        public enum PageSubType
        {
            Default = 0, DownloadInstall = 0, DownloadMod = 2, DownloadPack = 3,
            DownloadDataPack = 4, DownloadResourcePack = 5, DownloadShader = 6, DownloadMap = 7,
            SetupLaunch = 0, SetupLink = 1, SetupUI = 2, SetupSystem = 3, SetupUpdate = 4, SetupPersonal = 5, SetupJava = 6, LinkMain = 0,
            OtherHelp = 0, OtherAbout = 1, OtherTest = 2, OtherSchematics = 3, InstanceOverall = 0,
            InstanceSetup = 1, InstanceModify = 2, InstanceMod = 3, InstanceModDisabled = 4,
            InstanceExport = 5, InstanceSaves = 6, InstanceScreenshots = 7,
            InstanceResourcePacks = 8, InstanceShaderPacks = 9, InstanceSchematics = 10,
            InstanceKeybindings = 11, InstanceRestorePoints = 12, InstanceBackups = 13, InstanceClone = 14, InstancePackUpdate = 15,
            InstancePersonal = 16, InstanceMmcPlatformOrigin = 17, InstanceMmcMove = 18, InstanceJava = 19
        }
        private string PageNameGet(PageStackData Stack) => Controller.PageNameGet(Stack);
        public void PageNameRefresh(PageStackData type) => glassShell?.RequestPageRefresh();
        public void PageNameRefresh() => glassShell?.RequestPageRefresh();
        public PageStackData PageCurrent = PageType.Launch;
        public PageStackData PageLast = PageType.Launch;
        public List<PageStackData> PageStack = new List<PageStackData>();

        public class PageStackData : ILegacyMainPageStack
        {
            public PageType Page;
            public object Additional;
            public override bool Equals(object other) => LegacyMainPageIdentity.Equals(this, other);
            // LF inherited object.GetHashCode despite value-like Equals. Preserve that
            // behavior explicitly instead of introducing a new navigation hashing policy.
            public override int GetHashCode() => base.GetHashCode();
            int ILegacyMainPageStack.PageCode { get => (int)Page; set => Page = (PageType)value; }
            object ILegacyMainPageStack.Additional => Additional;
            public static bool operator ==(PageStackData left, PageStackData right)
                => EqualityComparer<PageStackData>.Default.Equals(left, right);
            public static bool operator !=(PageStackData left, PageStackData right) => !(left == right);
            public static implicit operator PageStackData(PageType Value) => new PageStackData { Page = Value };
            public static implicit operator PageType(PageStackData Value) => Value.Page;
        }

        public FrameworkElement PageRight => GlassView?.PageContentHost.Content as FrameworkElement;
        public void DragStop() => ModBase.RunInUi(() => { if (Mouse.Captured is System.Windows.Controls.Primitives.Thumb) Mouse.Capture(null); });
        public void AprilGiveup() { Controller.AprilGiveup(); glassShell?.RequestPageRefresh(); }
        public bool BtnExtraApril_ShowCheck() => Controller.AprilShowCheck();
        public void BtnExtraShutdown_Click() => Controller.ShutdownClick();
        public bool BtnExtraShutdown_ShowCheck() => Controller.ShutdownShowCheck();
        public void BackToTop() => glassShell?.TryScrollProjectedPageToTop();
        internal bool GiveUpButtonShown => glassShell?.CanGiveUp == true;
        internal void PulseGiveUpButton() => glassShell?.PulseGiveUp();
        public MouseEventArgs lastMouseArg;
        private void FormMain_MouseMove(object sender, MouseEventArgs e) => Controller.MouseMove(e);

        private LegacyMainWindowView CreateWindowView() => new()
        {
            Window = this,
            RefreshDirectPage = TryRefreshDirectPage,
            HandleDirectMessageKey = key => glassShell?.HandleDirectMessageKey(key) == true,
            SuppressesLaunchKey = () => GlassAccountPageOpen,
            PlayClose = done => glassShell?.View.BeginFarewell(done) == true,
            Resizer = index => glassShell.ResizeGrip(index)
        };

        private WindowHost CreateWindowHost()
        {
            var Result = new WindowHost
            {
                AnimationSuppression = () => ModAnimation.AniControlEnabled,
                WriteAnimationSuppression = value => ModAnimation.AniControlEnabled = value,
                DescribeSubType = value => ((PageSubType)value).ToString(),
                Loaded = () => IsWindowLoadFinished, WriteLoaded = value => IsWindowLoadFinished = value,
                SizeSaveable = () => IsSizeSaveable, WriteSizeSaveable = value => IsSizeSaveable = value,
                Hidden = () => _Hidden, WriteHidden = value => _Hidden = value,
                WriteSystemTimeChanged = value => IsSystemTimeChanged = value,
                CurrentPage = () => PageCurrent, WriteCurrentPage = value => PageCurrent = value,
                LastPage = () => PageLast, WriteLastPage = value => PageLast = value, PageStack = () => PageStack,
                OpenSubPage = () => (int)PageCurrentSub, NavigateBack = PageBack,
                AssignGlobalWindow = () => ModMain.FrmMain = this,
                ReadInteger = key => Settings.Get<int>(key), ReadString = key => Settings.Get<string>(key),
                ReadBoolean = key => Settings.Get<bool>(key), HasSaved = key => Settings.HasSaved(key),
                DefaultValue = key => Settings.GetDefault(key),
                WriteSetting = (key, value) => Settings.Set(key,
                    key == "WindowWidth" && glassShell?.HasLeftExpansionOverhang == true
                        ? Math.Max(980, Convert.ToInt32(Math.Round(Convert.ToDouble(value) - GlassShellIntegration.LeftOverhangAllowance)))
                        : value),
                ResetSetting = key => Settings.Reset(key),
                UpdateLogIdentity = () => CurrentUpdateLogIdentity,
                HasIniKey = (file, key) => ModBase.HasIniKey(file, key), ThemeRefresh = value => ModSecret.ThemeRefresh(value),
                ThemeRefreshMain = RefreshGlassShell, RefreshHidden = RefreshGlassShell,
                RefreshBackground = (first, second) => LauncherAppearanceSettings.BackgroundRefresh(first, second), RefreshMusic = (first, second) => ModMusic.MusicRefreshPlay(first, second),
                CreateResizer = target => new MyResizer(target), WorkingAreaHeight = () => My.MyWpfExtension.Computer.Screen.WorkingArea.Height,
                WorkingAreaWidth = () => My.MyWpfExtension.Computer.Screen.WorkingArea.Width, PixelToWpf = value => ModBase.GetWPFSize(value),
                HasStartWindow = () => ModMain.FrmStart != null, CloseStartWindow = value => ModMain.FrmStart.Close(value),
                IsAdmin = () => WindowsUtils.HasAdminRole(), IsDebug = () => ModBase.ModeDebug,
                PackEnabled = () => ModModpackMode.Enabled, ApplyPackVisibility = target => ModModpackMode.ApplyVisibility((FormMain)target),
                PackBlocksPage = value => ModModpackMode.BlocksPage((PageType)value),
                NormalizeSetupPage = value => (int)ModModpackMode.NormalizeSetupPage((PageSubType)value),
                NormalizeInstancePage = value => (int)(GlassShellActive ? GlassInstanceNavigation.Normalize((PageSubType)value) :
                    ModModpackMode.NormalizeInstancePage((PageSubType)value)),
                DisplayPackTitle = value => ModModpackMode.DisplayTitle(value),
                BuildType = (int)ModBase.BuildType, VersionCode = ModBase.VersionCode, Dpi = () => ModBase.DPI,
                VersionBaseName = () => ModBase.VersionBaseName, VersionBranchMain = () => ModSecret.VersionBranchMain,
                LauncherName = () => ModBase.LauncherDisplayName, ReleasesUrl = () => ModBase.LauncherReleasesUrl, IssuesUrl = () => ModBase.LauncherIssuesUrl,
                ShowReleaseNotes = (automatic, summary) => ModLauncherUpdate.TryShowCurrentReleaseNotes(automatic, summary),
                TempFolder = () => ModBase.PathTemp, ExecutablePath = () => ModBase.PathExe,
                Clock = () => ModBase.GetTimeMs(), StartTick = () => ModBase.ApplicationStartTick, WriteStartTick = value => ModBase.ApplicationStartTick = value,
                Handle = () => ModBase.Handle, WriteHandle = value => ModBase.Handle = value, SetForeground = value => ModMain.SetForegroundWindow(value),
                ApplicationOpenTime = () => ModBase.ApplicationOpenTime,
                Ui = work => ModBase.RunInUi(work), UiWait = work => ModBase.RunInUiWait(work), UiPosted = (work, force) => ModBase.RunInUi(work, force),
                NewThread = (work, name, priority) => ModBase.RunInNewThread(work, name, priority),
                RandomInteger = (min, max) => ModBase.RandomInteger(min, max), Hint = MainHint,
                Message = value => ModMain.MyMsgBox(value.Text, value.Title, value.Button1, value.Button2, value.Button3, Button3Action: value.ThirdAction),
                OpenWebsite = value => ModBase.OpenWebsite(value),
                StartAnimationLoop = () => ModAnimation.AniStart(), StartMainTimer = () => ModMain.TimerMainStart(), JavaInitialize = () => ModJava.JavaInit(),
                ClearTaskTemp = () => ModMain.TryClearTaskTemp(), CheckUpdates = noticeClosed => ModLauncherUpdate.CheckForUpdates(true, noticeClosed),
                StartFolderLoader = () => ModMinecraft.McFolderListLoader.Start(0), StartClientList = () => ModDownload.DlClientListMojangLoader.Start(1),
                CreateDragHelper = CreateDragPort, WindowHook = WndProc, DragHandler = HandleDrag,
                GlobalMessageCount = () => GlassMessageActive ? 1 : 0,
                WaitingMessages = () => ModMain.WaitingMyMsgBox.Any(), GlobalNavigate = value => ModMain.FrmMain.PageChange((PageType)value),
                WriteLastMouseArg = value => lastMouseArg = value,
                IsLaunching = LauncherApplicationServices.IsLaunchActive,
                CancelLaunch = () => LauncherApplicationServices.Launch.RequestCancellation(),
                LaunchButton = () => glassShell.StartFromHome(),
                HiddenVersions = () => ShowHiddenInstances,
                WriteHiddenVersions = value => glassShell.ShowHiddenInstances = value,
                HiddenForceShow = () => LauncherAppearanceSettings.HiddenForceShow, WriteHiddenForceShow = value => LauncherAppearanceSettings.HiddenForceShow = value,
                ReloadInstanceList = ReloadInstanceList,
                ReloadCurrentJava = () => LauncherInstanceContext.ReloadCurrentJava(), ReloadMods = () => ModMain.FrmInstanceMod.ReloadModList(),
                ExitInstallSelection = () => ModMain.FrmDownloadInstall?.ExitSelectPage(), IsInstallSelection = () => ModMain.FrmDownloadInstall?.IsInSelectPage == true,
                CanRefresh = value => value is IRefreshable, Refresh = value => ((IRefreshable)value).Refresh(),
                AprilEnabled = () => ModMain.IsAprilEnabled, AprilGivenUp = () => ModMain.IsAprilGiveup, WriteAprilGivenUp = value => ModMain.IsAprilGiveup = value,
                AprilScale = () => GlassView?.AprilScale,
                SettingsInstance = () => LauncherInstanceContext.Instance, SelectedInstance = () => ModMinecraft.McInstanceSelected,
                InstanceName = value => value.Name,
                InstanceSettingsTitlePrefix = value => GlassShellActive && ReferenceEquals(LauncherInstanceContext.Instance, value)
                    ? GlassInstanceNavigation.Context.SettingsTitlePrefix
                    : LauncherInstanceContext.ObserveInstanceContext(value).SettingsTitlePrefix,
                InstanceLocked = value => ModMinecraft.McInstanceLock.IsLocked(value),
                InstanceBlocksNavigation = value => ModMinecraft.McInstanceLock.BlocksNavigation(value),
                WriteInstanceSetting = (key, value, target) => Settings.Set(key, value, Instance: target),
                UsesGlobalAccounts = true,
                RefreshLaunchPage = () => RefreshLaunchAccount(true, false), RefreshCustomHome = RefreshHomepage,
                InstallMods = value => PageInstanceMod.InstallMods(value), InstallModpack = value => ModModpack.ModpackInstall(value), CreateCrashAnalyzer = CrashPort,
                HelpTitle = value => value is ModMain.HelpEntry entry ? entry.Title : value?.ToString() ?? string.Empty,
                HasDownloadingTask = () => ModNet.HasDownloadingTask(), HasLaunchOperation = () => ModLaunch.McLaunchLoaderReal != null,
                HasBlockingFileOperation = () => InstanceDeletionOperation.HasActiveOperations || MmcOperationLoaders.HasBlockingCommit || MmcCloneUi.HasActiveOperation || MmcLifecycleUi.HasActiveOperation || MmcDeleteUi.HasActiveOperation || MmcExportUi.HasActiveOperation || MmcHistoryUi.HasActiveOperation || MmcRepairUi.HasActiveOperation || MmcLegacyAdoptionUi.HasActiveOperation || MmcPackageUpdateUi.HasActiveOperation || MmcComponentEditorUi.HasActiveOperation,
                HasRunningMinecraft = () => ModWatcher.HasRunningMinecraft,
                TaskbarCancels = () => ModLoader.LoaderTaskbar.Select(value => (Action)(() => value.Cancel())),
                WatcherKills = () => ModWatcher.McWatcherList.Select(value => (Action)(() => value.Kill())), CancelLaunchOperation = () => ModLaunch.McLaunchLoaderReal.Cancel(),
                MusicPause = () => ModMusic.MusicControlPause(), MusicNext = () => ModMusic.MusicControlNext(),
                ForceExit = () => EndProgramForce(), Animations = CreateMainAnimations()
            };
#if LEGACY_LINK
            Result.LegacyLink = true;
            Result.LinkTryExit = (force, silent) => ModMain.FrmLinkMain?.TryExit(force, silent) ?? false;
#endif
            return Result;
        }

        private LegacyMainAnimations<ModAnimation.AniData> CreateMainAnimations() => new LegacyMainAnimations<ModAnimation.AniData>
        {
            Opacity = (target, value, time, delay, ease, after) => ModAnimation.AaOpacity(target, value, time, delay, (ModAnimation.AniEase)ease, after),
            X = (target, value, time, delay, ease, after) => ModAnimation.AaX(target, value, time, delay, (ModAnimation.AniEase)ease, after),
            Width = (target, value, time, delay, ease, after) => ModAnimation.AaWidth(target, value, time, delay, (ModAnimation.AniEase)ease, after),
            Double = (work, value, time, delay, ease, after) => ModAnimation.AaDouble(delta => work(Microsoft.VisualBasic.CompilerServices.Conversions.ToDouble(delta)), value, time, delay, (ModAnimation.AniEase)ease, after),
            Code = (work, delay, after) => ModAnimation.AaCode(() => work(), delay, after),
            OutFluent = power => new ModAnimation.AniEaseOutFluent((ModAnimation.AniEasePower)(int)power),
            InFluent = power => new ModAnimation.AniEaseInFluent((ModAnimation.AniEasePower)(int)power),
            OutBack = power => new ModAnimation.AniEaseOutBack((ModAnimation.AniEasePower)(int)power),
            InoutFluent = power => new ModAnimation.AniEaseInoutFluent((ModAnimation.AniEasePower)(int)power),
            Start = (frames, name, refresh) => ModAnimation.AniStart(frames, name, refresh), Stop = name => ModAnimation.AniStop(name), Speed = () => ModAnimation.AniSpeed
        };

        internal static string CurrentUpdateLogIdentity => LegacyUpdateLogPrompt.BuildIdentity(
            ModBase.VersionBaseName, ModBase.ReleaseBaselineVersion, ModBase.ReleaseBaselineIsPrerelease,
            ModBase.VersionCode, (int)ModBase.BuildType);

        internal static bool IsCurrentUpdateLogSuppressed => !LegacyUpdateLogPrompt.ShouldShow(
            key => Settings.Get<string>(key), CurrentUpdateLogIdentity);

        internal static bool PersistUpdateLogSuppression(bool suppress)
        {
            try
            {
                string persisted = ModBase.UpdateIniValue("Setup", LegacyUpdateLogPrompt.Key,
                    old => LegacyUpdateLogPrompt.UpdateDismissal(old, CurrentUpdateLogIdentity, suppress));
                Settings.Entries[LegacyUpdateLogPrompt.Key].Set(persisted, null);
                Logger.Info($"已保存当前版本更新说明弹窗设置：{suppress}");
                return true;
            }
            catch (Exception error)
            {
                Logger.Error(error, "保存更新说明弹窗设置失败，未保留本次更改", LogBehavior.None);
                return false;
            }
        }

        private static LegacyMainTerminationHost CreateTerminationHost() => new LegacyMainTerminationHost
        {
            MarkEnding = () => {
                ModBase.IsProgramEnding = true;
                if (ModMain.FrmMain is { } main && main.Dispatcher.CheckAccess())
                    main.glassShell?.CloseMultiplayer();
            },
            DisableAnimations = () => ModAnimation.AniControlEnabled = checked(ModAnimation.AniControlEnabled + 1),
            Feedback = () => ModMain.FeedbackInfo(), OpenLog = () => ModBase.StartProcess(Paths.Base + "PCL\\Log1.txt"),
            SaveAll = () => { try { InstanceConfigProviders.SaveAll(); } finally { ConfigUtils.SaveAll(); } }, FlushLog = () => Logger.Instance.Flush(), Kill = () => Process.GetCurrentProcess().Kill(),
            LogShown = () => IsLogShown, WriteLogShown = value => IsLogShown = value, IssuesUrl = () => ModBase.LauncherIssuesUrl,
            Exit = value => Environment.Exit(value), DescribeCode = value => ((ModBase.ProcessReturnValues)value).ToString()
        };

        private static void MainHint(string Text, LegacyMainHint Kind)
        {
            switch (Kind)
            {
                case LegacyMainHint.Red: ModMain.Hint(Text, ModMain.HintType.Red); break;
                case LegacyMainHint.Green: ModMain.Hint(Text, ModMain.HintType.Green); break;
                default: ModMain.Hint(Text); break;
            }
        }
        private static void ReloadInstanceList(bool Force) => LauncherGameFolders.RefreshInstances(Force);
        private static LegacyMainCrashAnalyzer CrashPort()
        {
            var Analyzer = new CrashAnalyzer();
            return new LegacyMainCrashAnalyzer
            {
                Import = value => Analyzer.Import(value), Prepare = () => Analyzer.Prepare(), Analyze = () => Analyzer.Analyze(),
                Output = (manual, files) => Analyzer.Output(manual, files)
            };
        }
        private static LegacyMainDragHelperPort CreateDragPort()
        {
            var Helper = new DragHelper();
            return new LegacyMainDragHelperPort
            {
                SetSource = value => Helper.HwndIntPtrSource = value, AddHook = () => Helper.AddHook(), RemoveHook = () => Helper.RemoveDragHook(),
                Files = () => Helper.DropFilePaths, ConnectDrop = work => ConnectDrag(Helper, work)
            };
        }
        private static void ConnectDrag(DragHelper Helper, Action Work) => Helper.DragDrop += (sender, e) => Work();
    }
}
