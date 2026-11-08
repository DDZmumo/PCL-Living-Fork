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
            BtnTitleSelect5.Logo = ModBase.Logo.IconButtonLikeLine;
            ConfigureThemeToggle(BtnTitleTheme);
            ConfigureDeferredUpdateButton(BtnTitleUpdate, ModLauncherUpdate.ReopenDeferredUpdate);
            ConfigureTitleClip(PanTitle);
            ConfigureSidebarCard(SidebarCard, PanMainLeft);
            var titleStars = new ThemeStarfield(12);
            PanTitle.Children.Insert(1, titleStars);
            RegisterName("TitleStars", titleStars);
            Controller.ConstructAfterXaml();
        }
        private static void ConfigureThemeToggle(MyIconButton button)
        {
            button.Logo = ModBase.Logo.IconButtonTheme;
            void RefreshIcon()
            {
                button.ToolTip = ModTheme.IsDarkTheme ? "切换到浅色主题" : "切换到深色主题";
                System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
            }
            button.Click += (_, _) => {
                Settings.Set("UiLauncherColorMode", ModTheme.IsDarkTheme ? 0 : 1);
                if (ModMain.FrmSetupUI is not null) {
                    ModAnimation.AniControlEnabled++;
                    try { SettingService.RefreshSettings(ModMain.FrmSetupUI.ComboLauncherColorMode); }
                    finally { ModAnimation.AniControlEnabled--; }
                }
                RefreshIcon();
            };
            ModTheme.WatchTheme(button, RefreshIcon);
            RefreshIcon();
        }

        private static void ConfigureDeferredUpdateButton(MyIconButton button, Action open)
        {
            button.Logo = ModBase.Logo.IconButtonRefresh;
            button.Click += (_, _) => open();
            button.Loaded += (_, _) => RefreshDeferredUpdateButton(button);
            RefreshDeferredUpdateButton(button);
        }
        internal static void RefreshDeferredUpdateButton(MyIconButton button)
        {
            string tag = ModLauncherUpdate.DeferredUpdateTag();
            button.ToolTip = "有可用更新 " + tag + "，点击查看";
            button.Visibility = string.IsNullOrEmpty(tag) ? Visibility.Collapsed : Visibility.Visible;
        }
        private static void ConfigureTitleClip(FrameworkElement title)
        {
            var clip = new System.Windows.Media.RectangleGeometry { RadiusX = 6, RadiusY = 6 };
            title.Clip = clip;
            void Refresh() => clip.Rect = new Rect(new Point(), title.RenderSize);
            title.SizeChanged += (_, _) => Refresh();
            title.Loaded += (_, _) => Refresh();
            Refresh();
        }
        private static void ConfigureSidebarCard(Border sidebar, Border content)
        {
            void RefreshInset()
            {
                var inset = content.Child is FrameworkElement { Width: 0 } ? new Thickness(0) : new Thickness(8, 8, 0, 8);
                if (sidebar.Margin != inset) sidebar.Margin = inset;
            }
            EventHandler refresh = (_, _) => RefreshInset();
            sidebar.Loaded += (_, _) => { content.LayoutUpdated += refresh; RefreshInset(); };
            sidebar.Unloaded += (_, _) => content.LayoutUpdated -= refresh;
            RefreshInset();
        }


        internal void ShowUpdateLog() => Controller.ShowUpdateLog();
        private void FormMain_Loaded(object sender, RoutedEventArgs e) => Controller.Loaded();
        private void UpgradeSub(int LastVersionCode) => Controller.Upgrade(LastVersionCode);
        private void DowngradeSub(int LastVersionCode) => Controller.Downgrade(LastVersionCode);
        private void FormMain_Closing(object sender, CancelEventArgs e) => Controller.Closing(e);
        public void EndProgram(bool SendWarning) => Controller.EndProgram(SendWarning);
        private static bool IsLogShown = false;
        public static void EndProgramForce(ModBase.ProcessReturnValues ReturnCode = ModBase.ProcessReturnValues.Success)
            => LegacyMainTermination.EndProgramForce((int)ReturnCode, CreateTerminationHost());
        private void BtnTitleClose_Click(object sender, RoutedEventArgs e) => Controller.EndProgram(true);
        private void FormDragMove(object sender, MouseButtonEventArgs e) => Controller.DragMove(sender);
        public bool IsSizeSaveable = false;
        private void FormMain_SizeChanged() => Controller.SizeChanged();
        private void BtnTitleMin_Click() => Controller.Minimize();
        public static void UpdateBackgroundAndTitleBar(object Value)
            => LegacyMainWindowStatics.UpdateBackground(() => ModMain.FrmMain, () => ModMain.FrmMain.UpdateBackgroundAndTitleBar());
        public void UpdateBackgroundAndTitleBar() => Controller.UpdateBackgroundAndTitleBar();
        internal void UpdateTitleBar() => Controller.UpdateTitleBar();
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
            SetupLaunch = 0, SetupLink = 1, SetupUI = 2, SetupSystem = 3, SetupUpdate = 4, SetupPersonal = 5, LinkMain = 0,
            OtherHelp = 0, OtherAbout = 1, OtherTest = 2, InstanceOverall = 0,
            InstanceSetup = 1, InstanceModify = 2, InstanceMod = 3, InstanceModDisabled = 4,
            InstanceExport = 5, InstanceSaves = 6, InstanceScreenshots = 7,
            InstanceResourcePacks = 8, InstanceShaderPacks = 9, InstanceSchematics = 10,
            InstanceKeybindings = 11, InstanceRestorePoints = 12, InstanceBackups = 13, InstanceClone = 14, InstancePackUpdate = 15, InstancePersonal = 16
        }
        private string PageNameGet(PageStackData Stack) => Controller.PageNameGet(Stack);
        public void PageNameRefresh(PageStackData Type) => Controller.PageNameRefresh(Type);
        public void PageNameRefresh() => Controller.PageNameRefresh();
        public PageStackData PageCurrent = PageType.Launch;
        public PageStackData PageLast = PageType.Launch;
        public PageSubType PageCurrentSub => (PageSubType)Controller.PageCurrentSub;
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

        public MyPageLeft PageLeft;
        public MyPageRight PageRight;
        private bool IsChangingPage = false;
        public void PageChange(PageStackData Stack, PageSubType SubType = PageSubType.Default)
            => Controller.PageChange(Stack, (int)SubType);
        private void BtnTitleSelect_Click(MyRadioButton sender, bool raiseByMouse) => Controller.TitleSelect(sender);
        public void PageBack() => Controller.PageBack();
        private void PageChangeActual(PageStackData Stack, PageSubType SubType = (PageSubType)(-1))
            => Controller.PageChangeActual(Stack, (int)SubType);
        private void PageChangeAnim(FrameworkElement TargetLeft, FrameworkElement TargetRight)
            => Controller.PageChangeAnim(TargetLeft, TargetRight);
        private void PageChangeExit() => Controller.PageChangeExit();
        private void PanMainLeft_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ReflowSidebarCard(SidebarCard, e);
            Controller.LeftSizeChanged(e);
        }
        private static void ReflowSidebarCard(Border sidebar, SizeChangedEventArgs args)
        {
            if (args.WidthChanged) sidebar.InvalidateMeasure();
        }
        private void PanMainLeft_Resize(double NewWidth) => Controller.ResizeLeft(NewWidth);
        public void DragTick() => Controller.DragTick();
        public void DragDoing() => Controller.DragDoing();
        public void DragStop() => Controller.DragStop();
        private void BtnExtraMusic_Click(object sender, EventArgs e) => Controller.MusicClick();
        private void BtnExtraMusic_RightClick(object sender, EventArgs e) => Controller.MusicRightClick();
        private void BtnExtraDownload_Click(object sender, EventArgs e) => Controller.DownloadClick();
        private bool BtnExtraDownload_ShowCheck() => Controller.DownloadShowCheck();
        public void AprilGiveup() => Controller.AprilGiveup();
        public bool BtnExtraApril_ShowCheck() => Controller.AprilShowCheck();
        public void BtnExtraShutdown_Click() => Controller.ShutdownClick();
        public bool BtnExtraShutdown_ShowCheck() => Controller.ShutdownShowCheck();
        public void BackToTop() => Controller.BackToTop();
        private bool BtnExtraBack_ShowCheck() => Controller.BackShowCheck();
        private MyScrollViewer BtnExtraBack_GetRealChild() => (MyScrollViewer)Controller.RealScroll();
        public MouseEventArgs lastMouseArg = null;
        private void FormMain_MouseMove(object sender, MouseEventArgs e) => Controller.MouseMove(e);

        // These exact adapters are referenced by native XAML. Attaching at BAML Connect
        // retains the original VB WithEvents timing, including Checked initialization.
        private void BtnTitleMin_ClickEvent(object sender, MouseButtonEventArgs e) => BtnTitleMin_Click();
        private void PageBack_ClickEvent(object sender, MouseButtonEventArgs e) => PageBack();
        private void PanBack_MouseMoveEvent(object sender, MouseEventArgs e) => DragDoing();
        private void BtnTitleSelect_CheckEvent(object sender, bool user) => BtnTitleSelect_Click((MyRadioButton)sender, user);
        private void AprilGiveup_ClickEvent(object sender, MouseButtonEventArgs e) => AprilGiveup();
        private void BtnExtraShutdown_ClickEvent(object sender, MouseButtonEventArgs e) => BtnExtraShutdown_Click();
        private void BackToTop_ClickEvent(object sender, MouseButtonEventArgs e) => BackToTop();

        private LegacyMainWindowView CreateWindowView() => new LegacyMainWindowView
        {
            Window = this,
            PanBack = () => PanBack, PanForm = () => PanForm, PanMain = () => PanMain,
            PanTitle = () => PanTitle, PanTitleMain = () => PanTitleMain, PanTitleInner = () => PanTitleInner, PanMsg = () => PanMsg,
            BorderForm = () => BorderForm, PanMainLeft = () => PanMainLeft, PanMainRight = () => PanMainRight,
            RectForm = () => RectForm, RectLeftBackground = () => RectLeftBackground, RectLeftShadow = () => RectLeftShadow,
            ImgBack = () => ImgBack, PanTitleSelect = () => PanTitleSelect,
            LabTitleInner = () => LabTitleInner, LabTitleLogo = () => LabTitleLogo, ShapeTitleLogo = () => ShapeTitleLogo,
            ImageTitleLogo = () => ImageTitleLogo, TransformPos = () => TransformPos, TransformRotate = () => TransformRotate,
            Resizer = ResizerPart, ExtraDownload = () => ExtraPort(BtnExtraDownload), ExtraBack = () => ExtraPort(BtnExtraBack),
            ExtraApril = () => ExtraPort(BtnExtraApril), ExtraShutdown = () => ExtraPort(BtnExtraShutdown)
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
                ChangingPage = () => IsChangingPage, WriteChangingPage = value => IsChangingPage = value,
                WriteSystemTimeChanged = value => IsSystemTimeChanged = value,
                CurrentPage = () => PageCurrent, WriteCurrentPage = value => PageCurrent = value,
                LastPage = () => PageLast, WriteLastPage = value => PageLast = value, PageStack = () => PageStack,
                CurrentLeft = () => PageLeft, WriteLeft = value => PageLeft = (MyPageLeft)value,
                CurrentRight = () => PageRight, WriteRight = value => PageRight = (MyPageRight)value,
                AssignGlobalWindow = () => ModMain.FrmMain = this, CreateLaunchLeft = () => ModMain.FrmLaunchLeft = new PageLaunchLeft(),
                CreateLaunchRight = () => ModMain.FrmLaunchRight = new PageLaunchRight(),
                ReadInteger = key => Settings.Get<int>(key), ReadString = key => Settings.Get<string>(key),
                ReadBoolean = key => Settings.Get<bool>(key), HasSaved = key => Settings.HasSaved(key),
                DefaultValue = key => Settings.GetDefault(key), WriteSetting = (key, value) => Settings.Set(key, value), ResetSetting = key => Settings.Reset(key),
                UpdateLogIdentity = () => CurrentUpdateLogIdentity,
                HasIniKey = (file, key) => ModBase.HasIniKey(file, key), ThemeRefresh = value => ModSecret.ThemeRefresh(value),
                ThemeRefreshMain = () => ModSecret.ThemeRefreshMain(), RefreshHidden = () => PageSetupUI.HiddenRefresh(),
                RefreshBackground = (first, second) => PageSetupUI.BackgroundRefresh(first, second), RefreshMusic = (first, second) => ModMusic.MusicRefreshPlay(first, second),
                CreateResizer = target => new MyResizer(target), WorkingAreaHeight = () => My.MyWpfExtension.Computer.Screen.WorkingArea.Height,
                WorkingAreaWidth = () => My.MyWpfExtension.Computer.Screen.WorkingArea.Width, PixelToWpf = value => ModBase.GetWPFSize(value),
                HasStartWindow = () => ModMain.FrmStart != null, CloseStartWindow = value => ModMain.FrmStart.Close(value),
                IsAdmin = () => WindowsUtils.HasAdminRole(), IsDebug = () => ModBase.ModeDebug,
                PackEnabled = () => ModModpackMode.Enabled, ApplyPackVisibility = target => ModModpackMode.ApplyVisibility((FormMain)target),
                PackBlocksPage = value => ModModpackMode.BlocksPage((PageType)value),
                NormalizeSetupPage = value => (int)ModModpackMode.NormalizeSetupPage((PageSubType)value),
                NormalizeInstancePage = value => (int)ModModpackMode.NormalizeInstancePage((PageSubType)value),
                DisplayPackTitle = value => ModModpackMode.DisplayTitle(value), LogoSettings = LogoSettingsPort,
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
                ClearTaskTemp = () => ModMain.TryClearTaskTemp(), CheckUpdates = () => ModLauncherUpdate.CheckForUpdates(true),
                StartFolderLoader = () => ModMinecraft.McFolderListLoader.Start(0), StartClientList = () => ModDownload.DlClientListMojangLoader.Start(1),
                CreateDragHelper = CreateDragPort, WindowHook = WndProc, DragHandler = HandleDrag,
                FirstDialog = FirstDialogPort, GlobalMessageCount = () => ModMain.FrmMain.PanMsg.Children.Count,
                WaitingMessages = () => ModMain.WaitingMyMsgBox.Any(), GlobalNavigate = value => ModMain.FrmMain.PageChange((PageType)value),
                WriteLastMouseArg = value => lastMouseArg = value,
                IsLaunching = () => ModLaunch.McLaunchLoader.State == ModBase.LoadState.Loading, CancelLaunch = () => ModMain.FrmLaunchLeft.BtnCancel_Click(),
                LaunchButton = () => ModMain.FrmLaunchLeft.LaunchButtonClick(),
                HiddenVersions = () => ModMain.FrmSelectRight.ShowHidden, WriteHiddenVersions = value => ModMain.FrmSelectRight.ShowHidden = value,
                HiddenForceShow = () => PageSetupUI.HiddenForceShow, WriteHiddenForceShow = value => PageSetupUI.HiddenForceShow = value,
                ReloadInstanceList = ReloadInstanceList,
                ReloadCurrentJava = () => PageInstanceLeft.ReloadCurrentJava(), ReloadMods = () => ModMain.FrmInstanceMod.ReloadModList(),
                ExitInstallSelection = () => ModMain.FrmDownloadInstall.ExitSelectPage(), IsInstallSelection = () => ModMain.FrmDownloadInstall.IsInSelectPage,
                CanRefresh = value => value is IRefreshable, Refresh = value => ((IRefreshable)value).Refresh(),
                AprilEnabled = () => ModMain.IsAprilEnabled, AprilGivenUp = () => ModMain.IsAprilGiveup, WriteAprilGivenUp = value => ModMain.IsAprilGiveup = value,
                AprilScale = () => ModMain.FrmLaunchLeft.AprilScaleTrans,
                SettingsInstance = () => PageInstanceLeft.Instance, SelectedInstance = () => ModMinecraft.McInstanceSelected,
                InstanceName = value => value.Name, InstanceLocked = value => ModMinecraft.McInstanceLock.IsLocked(value),
                InstanceBlocksNavigation = value => ModMinecraft.McInstanceLock.BlocksNavigation(value),
                WriteInstanceSetting = (key, value, target) => Settings.Set(key, value, Instance: target),
                ValidateHttp = value => new ValidateHttp().Validate(value), ReloadInstanceSetup = () => ModMain.FrmInstanceSetup.Reload(),
                RefreshLaunchPage = () => ModMain.FrmLaunchLeft.RefreshPage(true, false), RefreshCustomHome = () => ModMain.FrmLaunchRight.ForceRefresh(),
                InstallMods = value => PageInstanceMod.InstallMods(value), InstallModpack = value => ModModpack.ModpackInstall(value), CreateCrashAnalyzer = CrashPort,
                EnsureLeftPage = EnsureLeftPage, EnsureRightPage = EnsureRightPage,
                LeftPage = LeftPagePort, RightPage = RightPageElement, NewBlankLeft = () => new MyPageLeft(),
                HelpTitle = value => ((ModMain.HelpEntry)value).Title,
                HasDownloadingTask = () => ModNet.HasDownloadingTask(), HasLaunchOperation = () => ModLaunch.McLaunchLoaderReal != null,
                HasBlockingFileOperation = () => InstanceDeletionOperation.HasActiveOperations,
                HasRunningMinecraft = () => ModWatcher.HasRunningMinecraft,
                TaskbarCancels = () => ModLoader.LoaderTaskbar.Select(value => (Action)(() => value.Cancel())),
                WatcherKills = () => ModWatcher.McWatcherList.Select(value => (Action)(() => value.Kill())), CancelLaunchOperation = () => ModLaunch.McLaunchLoaderReal.Cancel(),
                MusicPause = () => ModMusic.MusicControlPause(), MusicNext = () => ModMusic.MusicControlNext(),
                DragControl = () => (MySliderView)ModMain.DragControl, ClearDragControl = () => ModMain.DragControl = null,
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
            MarkEnding = () => ModBase.IsProgramEnding = true,
            DisableAnimations = () => ModAnimation.AniControlEnabled = checked(ModAnimation.AniControlEnabled + 1),
            Feedback = () => ModMain.FeedbackInfo(), OpenLog = () => ModBase.StartProcess(Paths.Base + "PCL\\Log1.txt"),
            SaveAll = () => ConfigUtils.SaveAll(), FlushLog = () => Logger.Instance.Flush(), Kill = () => Process.GetCurrentProcess().Kill(),
            LogShown = () => IsLogShown, WriteLogShown = value => IsLogShown = value, IssuesUrl = () => ModBase.LauncherIssuesUrl,
            Exit = value => Environment.Exit(value), DescribeCode = value => ((ModBase.ProcessReturnValues)value).ToString()
        };

        private UIElement ResizerPart(int Index)
        {
            switch (Index)
            {
                case 0: return ResizerB;
                case 1: return ResizerL;
                case 2: return ResizerLB;
                case 3: return ResizerLT;
                case 4: return ResizerR;
                case 5: return ResizerRB;
                case 6: return ResizerRT;
                default: return ResizerT;
            }
        }
        private static LegacyMainExtraButton ExtraPort(MyExtraButton Button) => new LegacyMainExtraButton
        {
            SetShowCheck = work => Button.ShowCheck = () => work(), ShowRefresh = () => Button.ShowRefresh(), Show = () => Button.Show
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
        private static void ReloadInstanceList(bool Force)
            => ModLoader.LoaderFolderRun(ModMinecraft.McInstanceListLoader, ModMinecraft.McFolderSelected,
                Force ? ModLoader.LoaderFolderRunType.ForceRun : ModLoader.LoaderFolderRunType.RunOnUpdated,
                MaxDepth: 1, ExtraPath: "versions\\");
        private static LegacyMainLogoSettings LogoSettingsPort()
        {
            if (ModMain.FrmSetupUI == null) return null;
            return new LegacyMainLogoSettings
            {
                CheckLeft = ModMain.FrmSetupUI.CheckLogoLeft, Text = ModMain.FrmSetupUI.PanLogoText, Change = ModMain.FrmSetupUI.PanLogoChange,
                ForceResize = () => ModMain.FrmSetupUI.CardLogo.TriggerForceResize()
            };
        }
        private static void EnsureLeftPage(int Page)
        {
            switch ((PageType)Page)
            {
                case PageType.Download: if (ModMain.FrmDownloadLeft == null) ModMain.FrmDownloadLeft = new PageDownloadLeft(); break;
                case PageType.Favorites:
                    if (ModMain.FrmFavorites == null) ModMain.FrmFavorites = new PageFavorites();
                    if (ModMain.FrmFavoritesLeft == null) ModMain.FrmFavoritesLeft = new PageDownloadLeft(ModMain.FrmFavorites);
                    break;
                case PageType.Setup: if (ModMain.FrmSetupLeft == null) ModMain.FrmSetupLeft = new PageSetupLeft(); break;
                case PageType.Other: if (ModMain.FrmOtherLeft == null) ModMain.FrmOtherLeft = new PageOtherLeft(); break;
                case PageType.InstanceSelect: if (ModMain.FrmSelectLeft == null) ModMain.FrmSelectLeft = new PageSelectLeft(); break;
                case PageType.TaskManager: if (ModMain.FrmSpeedLeft == null) ModMain.FrmSpeedLeft = new PageSpeedLeft(); break;
                case PageType.InstanceSetup: if (ModMain.FrmInstanceLeft == null) ModMain.FrmInstanceLeft = new PageInstanceLeft(); break;
            }
        }
        private static void EnsureRightPage(int Page)
        {
            switch ((PageType)Page)
            {
                case PageType.InstanceSelect: if (ModMain.FrmSelectRight == null) ModMain.FrmSelectRight = new PageSelectRight(); break;
                case PageType.TaskManager: if (ModMain.FrmSpeedRight == null) ModMain.FrmSpeedRight = new PageSpeedRight(); break;
                case PageType.ResourceDetail: if (ModMain.FrmDownloadResourceDetail == null) ModMain.FrmDownloadResourceDetail = new PageDownloadResourceDetail(); break;
                case PageType.Favorites: if (ModMain.FrmFavorites == null) ModMain.FrmFavorites = new PageFavorites(); break;
#if LEGACY_LINK
                case PageType.Link: if (ModMain.FrmLinkMain == null) ModMain.FrmLinkMain = new PageLinkMain(); break;
#endif
            }
        }
        private static LegacyMainLeftPage LeftPagePort(int Page)
        {
            switch ((PageType)Page)
            {
                case PageType.Launch: return new LegacyMainLeftPage { Element = ModMain.FrmLaunchLeft };
                case PageType.Favorites: return new LegacyMainLeftPage {
                    Element = ModMain.FrmFavoritesLeft, PageId = () => (int)ModMain.FrmFavoritesLeft.PageID,
                    PageGet = subType => (MyPageRightView)ModMain.FrmFavoritesLeft.PageGet((PageSubType)subType),
                    Item = subType => ModMain.FrmFavoritesLeft.NavigationItem((PageSubType)subType)
                };
                case PageType.Download: return new LegacyMainLeftPage
                {
                    Element = ModMain.FrmDownloadLeft, PageId = () => (int)ModMain.FrmDownloadLeft.PageID,
                    PageGet = subType => (MyPageRightView)ModMain.FrmDownloadLeft.PageGet((PageSubType)subType), Item = subType => (MyListItem)ModMain.FrmDownloadLeft.PanItem.Children[subType]
                };
                case PageType.Setup: return new LegacyMainLeftPage
                {
                    Element = ModMain.FrmSetupLeft, PageId = () => (int)ModMain.FrmSetupLeft.PageID,
                    PageGet = subType => (MyPageRightView)ModMain.FrmSetupLeft.PageGet((PageSubType)subType), Item = subType => (MyListItem)ModMain.FrmSetupLeft.PanItem.Children[subType]
                };
                case PageType.Other: return new LegacyMainLeftPage
                {
                    Element = ModMain.FrmOtherLeft, PageId = () => (int)ModMain.FrmOtherLeft.PageID,
                    PageGet = subType => (MyPageRightView)ModMain.FrmOtherLeft.PageGet((PageSubType)subType), Item = subType => (MyListItem)ModMain.FrmOtherLeft.PanItem.Children[subType]
                };
                case PageType.InstanceSelect: return new LegacyMainLeftPage { Element = ModMain.FrmSelectLeft };
                case PageType.TaskManager: return new LegacyMainLeftPage { Element = ModMain.FrmSpeedLeft };
                case PageType.InstanceSetup: return new LegacyMainLeftPage
                {
                    Element = ModMain.FrmInstanceLeft, PageId = () => (int)ModMain.FrmInstanceLeft.PageID,
                    PageGet = subType => (MyPageRightView)ModMain.FrmInstanceLeft.PageGet((PageSubType)subType), Item = subType => ModMain.FrmInstanceLeft.NavigationItem((PageSubType)subType),
                    SyncSelection = subType => ModMain.FrmInstanceLeft.SyncSelection((PageSubType)subType)
                };
                default: throw new InvalidOperationException("未知主窗左页：" + Page);
            }
        }
        private static MyPageRightView RightPageElement(int Page)
        {
            switch ((PageType)Page)
            {
                case PageType.Launch: return ModMain.FrmLaunchRight;
                case PageType.InstanceSelect: return ModMain.FrmSelectRight;
                case PageType.TaskManager: return ModMain.FrmSpeedRight;
                case PageType.ResourceDetail: return ModMain.FrmDownloadResourceDetail;
                case PageType.Favorites: return ModMain.FrmFavorites;
#if LEGACY_LINK
                case PageType.Link: return ModMain.FrmLinkMain;
#endif
                default: throw new InvalidOperationException("未知主窗右页：" + Page);
            }
        }
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
        private LegacyMainDialog FirstDialogPort()
        {
            var Row = PanMsg.Children[0];
            if (Row is MyMsgText text)
                return new LegacyMainDialog
                {
                    First = () => text.Btn1_Click(), Second = () => text.Btn2_Click(), Third = () => text.Btn3_Click(),
                    SecondVisibility = () => text.Btn2.Visibility, ThirdVisibility = () => text.Btn3.Visibility
                };
            if (Row is MyMsgSelect select)
                return new LegacyMainDialog
                {
                    IsInputOrSelect = true, First = () => select.Btn1_Click(), Second = () => select.Btn2_Click(),
                    SecondVisibility = () => select.Btn2.Visibility
                };
            if (Row is MyMsgInput input)
                return new LegacyMainDialog
                {
                    IsInputOrSelect = true, First = () => input.Btn1_Click(), Second = () => input.Btn2_Click(),
                    SecondVisibility = () => input.Btn2.Visibility
                };
            if (Row is MyMsgLogin login)
                return new LegacyMainDialog
                {
                    First = () => login.Btn1_Click(), Third = () => login.Btn3_Click(),
                    Second = () => Microsoft.VisualBasic.CompilerServices.NewLateBinding.LateCall(Row, null, "Btn2_Click", new object[] { }, null, null, null, true),
                    SecondVisibility = () => login.Btn2.Visibility, ThirdVisibility = () => login.Btn3.Visibility
                };
            throw new InvalidCastException("未知主窗弹窗类型：" + Row.GetType().FullName);
        }
    }
}
