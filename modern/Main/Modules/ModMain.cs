// LF C031b verified platform facade, translated to native C#; complete Shell business is reused unchanged.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Shell;

namespace PCL
{

    // LF 原类型、公开字段及真实平台/模型投影；全部队列处理、帮助、主时钟和系统业务在 C#。
    public static partial class ModMain
    {
        public enum HintType
        {
            Blue,
            Green,
            Red
        }
        public enum MyMsgBoxType
        {
            Text,
            Select,
            Input,
            Login
        }
        public class MyMsgBoxConverter : MainMessageData<MyMsgBoxType, Validate>
        {
        }

        private static ConcurrentQueue<MainHintData<HintType>> HintWaiting = new ConcurrentQueue<MainHintData<HintType>>();
        public static ConcurrentQueue<MyMsgBoxConverter> WaitingMyMsgBox = new ConcurrentQueue<MyMsgBoxConverter>();
        public static bool IsAprilGiveup = false;
        public static object DragControl = null;
        // 窗体声明
        public static FormMain FrmMain;
        public static SplashScreen FrmStart;

        // 页面声明（出于单元测试考虑，初始化页面已转入 FormMain 中）
        public static PageLaunchLeft FrmLaunchLeft;
        public static PageLaunchRight FrmLaunchRight;
        public static PageSelectLeft FrmSelectLeft;
        public static PageSelectRight FrmSelectRight;
        public static PageSpeedLeft FrmSpeedLeft;
        public static PageSpeedRight FrmSpeedRight;

        // 联机页面声明
#if LEGACY_LINK
        public static PageLinkMain FrmLinkMain;
#endif
        // 下载页面声明
        public static PageDownloadLeft FrmDownloadLeft;
        public static PageDownloadInstall FrmDownloadInstall;
        public static PageDownloadMod FrmDownloadMod;
        public static PageDownloadPack FrmDownloadPack;
        public static PageDownloadDataPack FrmDownloadDataPack;
        public static PageDownloadShader FrmDownloadShader;
        public static PageDownloadResourcePack FrmDownloadResourcePack;

        // 设置页面声明
        public static PageSetupLeft FrmSetupLeft;
        public static PageSetupLaunch FrmSetupLaunch;
        public static PageSetupUI FrmSetupUI;
        public static PageSetupSystem FrmSetupSystem;
        public static PageSetupUpdate FrmSetupUpdate;
        public static PageSetupPersonal FrmSetupPersonal;
#if LEGACY_LINK
        public static PageSetupLink FrmSetupLink;
#endif
        // 其他页面声明
        public static PageOtherLeft FrmOtherLeft;
        public static PageOtherHelp FrmOtherHelp;
        public static PageOtherAbout FrmOtherAbout;
        public static PageOtherTest FrmOtherTest;

        // 登录页面声明
        public static PageLoginLegacy FrmLoginLegacy;
        public static PageLoginNide FrmLoginNide;
        public static PageLoginNideSkin FrmLoginNideSkin;
        public static PageLoginAuth FrmLoginAuth;
        public static PageLoginAuthSkin FrmLoginAuthSkin;
        public static PageLoginMs FrmLoginMs;
        public static PageLoginMsSkin FrmLoginMsSkin;

        // 版本设置页面声明
        public static PageInstanceLeft FrmInstanceLeft;
        public static PageInstanceOverall FrmInstanceOverall;
        public static PageInstanceMod FrmInstanceMod;
        public static PageInstanceModDisabled FrmInstanceModDisabled;
        public static PageInstanceSetup FrmInstanceSetup;
        public static PageInstanceExport FrmInstanceExport;
        public static PageDownloadModify FrmDownloadModify;

        // 资源信息分页声明
        public static PageDownloadResourceDetail FrmDownloadResourceDetail;
        public static PageFavorites FrmFavorites;
        public static PageDownloadLeft FrmFavoritesLeft;

        private readonly static MainHintRuntime<HintType> NativeHints = new MainHintRuntime<HintType>(new MainHintHost<HintType>()
        {
            Queue = new Func<ConcurrentQueue<MainHintData<HintType>>>(() => HintWaiting),
            Panel = new Func<Panel>(() => FrmMain.PanHint),
            NewUuid = ModBase.GetUuid,
            Animations = ReadAnimations,
            IsAnimationRunning = ModAnimation.AniIsRun,
            Blue = HintType.Blue,
            Green = HintType.Green
        });

        // Same LF single queue/model and actual constructors, now ordinary C# initializers.
        private static readonly MainMessageRuntime<MyMsgBoxConverter, MyMsgBoxType, Validate> NativeMessages = new(new MainMessageHost<MyMsgBoxConverter, MyMsgBoxType> {
            Window = () => FrmMain, Panel = () => FrmMain?.PanMsg, DragStop = () => FrmMain.DragStop(), IsUiThread = () => ModBase.RunInUi(),
            Queue = () => WaitingMyMsgBox, Create = () => new MyMsgBoxConverter(),
            Text = MyMsgBoxType.Text, Select = MyMsgBoxType.Select, Input = MyMsgBoxType.Input, Login = MyMsgBoxType.Login,
            CreateInput = value => new MyMsgInput(value), CreateSelect = value => new MyMsgSelect(value),
            CreateText = value => new MyMsgText(value), CreateLogin = value => new MyMsgLogin(value)
        });
        private static readonly MainArgumentRuntime NativeArguments = new(new MainArgumentHost {
            VersionName = () => ModBase.VersionBaseName, VersionCode = () => ModBase.VersionCode,
            BuildTypeDisplay = () => ModBase.BuildTypeDisplay, BuildType = () => ModBase.BuildType.ToString(),
            VersionBranch = () => ModSecret.VersionBranchMain, Identify = () => ModBase.Identify,
            BasePath = () => Paths.Base, ExePath = () => ModBase.PathExe, TempPath = () => ModBase.PathTemp,
            RegistryFolder = () => ModSecret.RegFolder, JavaFolder = () => ModLaunch.McLaunchJavaSelected?.Folder,
            MinecraftFolder = () => ModMinecraft.McFolderSelected,
            InstanceLoaded = () => ModMinecraft.McInstanceSelected?.IsLoaded ?? false,
            InstancePath = () => ModMinecraft.McInstanceSelected.PathVersion, InstanceIndie = () => ModMinecraft.McInstanceSelected.PathIndie,
            InstanceName = () => ModMinecraft.McInstanceSelected.Name, VanillaName = () => ModMinecraft.McInstanceSelected.Version.VanillaName,
            InstanceLoaders = () => ModMinecraft.McInstanceSelected.Version.ModLoaders,
            LoaderVersion = loader => ModMinecraft.McInstanceSelected.Version.ModLoaderVersions[loader],
            LoginFinished = () => ModLaunch.McLoginLoader.State == ModBase.LoadState.Finished,
            LoginName = () => ModLaunch.McLoginLoader.Output.Name, LoginUuid = () => ModLaunch.McLoginLoader.Output.Uuid,
            LoginType = () => (int)ModLaunch.McLoginLoader.Input.Type,
            LegacyLogin = (int)ModLaunch.McLoginType.Legacy, MsLogin = (int)ModLaunch.McLoginType.Ms,
            NideLogin = (int)ModLaunch.McLoginType.Nide, AuthLogin = (int)ModLaunch.McLoginType.Auth,
            RandomHint = PageOtherTest.GetRandomHint, RandomCave = PageOtherTest.GetRandomCave,
            Setting = name => Settings.GetSafe(name, ModMinecraft.McInstanceSelected)
        });
        private readonly static MainHelpHost<CustomEvent.EventType> NativeHelpHost = CreateHelpHost();
        private readonly static MainHelpRuntime<HelpEntry, CustomEvent.EventType> NativeHelp = new MainHelpRuntime<HelpEntry, CustomEvent.EventType>(NativeHelpHost, Path => new HelpEntry(Path));
        public static ModLoader.LoaderTask<int, List<HelpEntry>> HelpLoader = new ModLoader.LoaderTask<int, List<HelpEntry>>("Help Page", HelpLoad, null, ThreadPriority.BelowNormal);
        private readonly static MainTimerRuntime NativeTimers = new MainTimerRuntime(new MainTimerHost()
        {
            HintTick = HintTick,
            MessageTick = MyMsgBoxTick,
            DragTick = new Action(() => FrmMain.DragTick()),
            TaskbarRefresh = ModLoader.LoaderTaskbarProgressRefresh,
            Ui = new Action<Action>(Work => ModBase.RunInUi(Work)),
            UiWait = new Action<Action>(Work => ModBase.RunInUiWait(Work)),
            StartThread = new Action<Action, string>((Work, Name) => ModBase.RunInNewThread(Work, Name)),
            Window = new Func<Window>(() => FrmMain),
            Hidden = new Func<bool>(() => FrmMain.Hidden),
            ShowAprilButton = new Func<bool>(() => FrmMain.BtnExtraApril_ShowCheck()),
            AprilRibble = new Action(() => FrmMain.BtnExtraApril.Ribble()),
            AprilYear = new Func<int>(() => Settings.Get<int>("AprilYear")),
            AprilGiveUp = new Func<bool>(() => IsAprilGiveup),
            HasLaunchPage = new Func<bool>(() => FrmLaunchLeft is not null),
            IsLaunchPage = new Func<bool>(() => FrmMain.PageCurrent == (FormMain.PageStackData)FormMain.PageType.Launch),
            AnimationControl = new Func<int>(() => ModAnimation.AniControlEnabled),
            LastMouse = new Func<MouseEventArgs>(() => FrmMain.lastMouseArg),
            LaunchButton = new Func<FrameworkElement>(() => FrmLaunchLeft.BtnLaunch),
            MainPanel = new Func<FrameworkElement>(() => FrmMain.PanMain),
            FormPanel = new Func<FrameworkElement>(() => FrmMain.PanForm),
            AprilPosition = new Func<TranslateTransform>(() => FrmLaunchLeft.AprilPosTrans),
            AprilScale = new Func<ScaleTransform>(() => FrmLaunchLeft.AprilScaleTrans),
            RandomInteger = ModBase.RandomInteger,
            RedHint = new Action<string>(Text => Hint(Text, HintType.Red))
        });
        private readonly static MainTaskTempRuntime NativeTaskTemp = new MainTaskTempRuntime(new MainTaskTempHost()
        {
            OsDrive = new Func<string>(() => ModBase.OsDrive),
            TempPath = new Func<string>(() => ModBase.PathTemp),
            NewUuid = ModBase.GetUuid,
            RandomInteger = ModBase.RandomInteger,
            CheckPermissionWithException = ModBase.CheckPermissionWithException,
            CheckPermission = ModBase.CheckPermission
        });
        private readonly static MainFeedbackRuntime NativeFeedback = new MainFeedbackRuntime(new MainFeedbackHost()
        {
            OsFullName = new Func<string>(() => My.MyWpfExtension.Computer.Info.OSFullName),
            AvailablePhysicalMemory = new Func<ulong>(() => My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory),
            TotalPhysicalMemory = new Func<ulong>(() => My.MyWpfExtension.Computer.Info.TotalPhysicalMemory),
            Dpi = new Func<int>(() => ModBase.DPI),
            MinecraftFolder = new Func<string>(() => ModMinecraft.McFolderSelected),
            BasePath = new Func<string>(() => Paths.Base),
            IssuesUrl = new Func<string>(() => ModBase.LauncherIssuesUrl),
            Message = new Func<string, string, string, string, int>((Text, Title, First, Second) => MyMsgBox(Text, Title, First, Second)),
            Explorer = new Action<string>(Address => ModBase.OpenExplorer(Address)),
            Website = new Action<string>(Address => ModBase.OpenWebsite(Address))
        });

        private static ControlAnimations ReadAnimations()
        {
            PresentationRuntime.EnsureConfigured();
            return ControlServices.Current.Animations;
        }
        private static MainHelpHost<CustomEvent.EventType> CreateHelpHost()
        {
            return new MainHelpHost<CustomEvent.EventType>()
            {
                ArgumentReplace = new Func<string, Func<string, string>, string>((Text, Escape) => ArgumentReplace(Text, Escape)),
                BasePath = new Func<string>(() => Paths.Base),
                TempPath = new Func<string>(() => ModBase.PathTemp),
                ImagePath = new Func<string>(() => ModBase.PathImage),
                VersionCode = new Func<int>(() => ModBase.VersionCode),
                HelpVersion = new Func<int>(() => Settings.Get<int>("SystemHelpVersion")),
                SetHelpVersion = new Action<int>(Value => Settings.Set("SystemHelpVersion", Value)),
                ExtractResources = new Action<string, string>((Path, Name) => ModBase.ExtractResources(Path, Name)),
                PopupEvent = CustomEvent.EventType.弹出窗口,
                CreateRow = new Func<MyListItemView>(() => new MyListItem()),
                ClearCustomEvent = new Action<MyListItemView>(Row =>
                    {
                        CustomEventService.SetEventType(Row, CustomEvent.EventType.None);
                        CustomEventService.SetEventData(Row, null);
                    }),
                ConnectClick = new Action<MyListItemView, Action<object>>((Row, Handler) => ((MyListItem)Row).Click += (Sender, Args) => Handler(Sender)),
                OpenEntry = new Action<object>(Entry => PageOtherHelp.OnItemClick((HelpEntry)Entry))
            };
        }
        public class HelpEntry : MainHelpEntryData<CustomEvent.EventType>
        {
            public HelpEntry(string FilePath) : base(FilePath, NativeHelpHost)
            {
            }
            public new MyListItem ToListItem()
            {
                return (MyListItem)base.ToListItem();
            }
            public MyListItem SetToListItem(MyListItem Item)
            {
                return (MyListItem)base.SetToListItem(Item);
            }
        }
        private static void HelpLoad(ModLoader.LoaderTask<int, List<HelpEntry>> Loader)
        {
            NativeHelp.Load(() => Loader.IsCanceled, Entries => Loader.Output = Entries);
        }
        public static void HelpTryExtract()
        {
            NativeHelp.TryExtract();
        }
        public static void Hint(string Text, HintType Type = HintType.Blue, bool Log = true)
        {
            NativeHints.Enqueue(Text, Type, Log);
        }
        private static void HintTick()
        {
            NativeHints.Tick();
        }
        private static void HideAllHint()
        {
            NativeHints.HideAll();
        }
        public static int MyMsgBox(string Caption, string Title = "提示", string Button1 = "确定", string Button2 = "", string Button3 = "", bool IsWarn = false, bool HighLight = true, bool ForceWait = false, Action Button1Action = null, Action Button2Action = null, Action Button3Action = null)
        {
            return ModMain.NativeMessages.Show(Caption, Title, Button1, Button2, Button3, IsWarn, HighLight, ForceWait, Button1Action, Button2Action, Button3Action);
        }
        public static string MyMsgBoxInput(string Title, string Text = null, string DefaultInput = null, Collection<Validate> ValidateRules = null, string HintText = "", string Button1 = "确定", string Button2 = "取消", bool IsWarn = false)
        {
            return ModMain.NativeMessages.Input(Title, Text, DefaultInput, ValidateRules, HintText, Button1, Button2, IsWarn);
        }
        public static int? MyMsgBoxSelect(IEnumerable<IMyRadio> Selections, string Title = "提示", string Button1 = "确定", string Button2 = "", bool IsWarn = false)
        {
            return ModMain.NativeMessages.Select(Selections, Title, Button1, Button2, IsWarn);
        }
        public static void MyMsgBoxTick()
        {
            ModMain.NativeMessages.Tick();
        }
        internal static void MyMsgBoxContent(string Caption, string Title, Func<FrameworkElement> Factory)
        {
            ModMain.NativeMessages.Content(Caption, Title, Factory);
        }
        internal static int MyMsgBoxConfirmContent(string Caption, string Title, string ConfirmText, Func<FrameworkElement> Factory, string CancelText = "取消", string ThirdText = "", int WarningButton = 1, bool IsWarn = true)
        {
            return ModMain.NativeMessages.ConfirmContent(Caption, Title, ConfirmText, Factory, CancelText, ThirdText, WarningButton, IsWarn);
        }
        public static bool IsAprilEnabled
        {
            get
            {
                return NativeTimers.IsAprilEnabled;
            }
        }
        private static void TimerFool()
        {
            NativeTimers.FoolTick();
        }
        private static void TimerMain()
        {
            NativeTimers.Tick();
        }
        public static void TimerMainStart()
        {
            NativeTimers.Start();
        }
        public static void ShowWindowToTop(IntPtr Handle)
        {
            MainWindowInterop.ShowToTop(Handle);
        }
        // Keep the actual original VB Declare managed/marshal ABI.
#pragma warning disable CS0618
        public static IntPtr FindWindow([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.VBByRefStr)] ref string ClassName, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.VBByRefStr)] ref string WindowName)
        {
            return MainWindowInterop.FindWindow(ref ClassName, ref WindowName);
        }
#pragma warning restore CS0618
        public static int SetForegroundWindow(IntPtr hWnd)
        {
            return MainWindowInterop.SetForegroundWindow(hWnd);
        }
        public static void SetGPUPreference(string Executeable)
        {
            MainWindowInterop.SetGpuPreference(Executeable);
        }
        public static string ArgumentReplace(string Text, Func<string, string> EscapeHandler = null, bool ReplaceTime = true)
        {
            return ModMain.NativeArguments.Replace(Text, EscapeHandler, ReplaceTime);
        }
        public static void TryClearTaskTemp()
        {
            NativeTaskTemp.TryClear();
        }
        public static string RequestTaskTempFolder(bool RequireNonSpace = false)
        {
            return NativeTaskTemp.Request(RequireNonSpace);
        }
        public static void Feedback(bool ShowMsgbox = true, bool ForceOpenLog = false)
        {
            NativeFeedback.Feedback(ShowMsgbox, ForceOpenLog);
        }
        public static void FeedbackInfo()
        {
            NativeFeedback.Info();
        }
    }
}
