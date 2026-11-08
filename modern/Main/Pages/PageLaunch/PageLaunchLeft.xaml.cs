using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using global::System.Windows.Threading;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using global::PCLLF.Presentation.Launch;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageLaunchLeft
    {
        private readonly global::PCLLF.Presentation.Launch.LegacyLaunchLeft<global::PCL.ModMinecraft.McInstance, global::PCL.ModAnimation.AniData> Native;
        public PageLaunchLeft()
        {
            __LfAttachOwnHandles();
            this.Native = new global::PCLLF.Presentation.Launch.LegacyLaunchLeft<global::PCL.ModMinecraft.McInstance, global::PCL.ModAnimation.AniData>(this.CreateView(), this.CreateHost(), global::PCL.LaunchPageRuntime.Animations());
            this.InitializeComponent();
            CheckMoreSettings.Checked = Settings.Get<bool>("UiLaunchMoreSettings");
            CheckMoreSettings.Change += (_, user) => {
                if (user) ModMain.FrmLaunchRight?.RefreshMoreSettings();
            };
        }

        private global::PCLLF.Presentation.Launch.LegacyLaunchLeftView CreateView()
        {
            return new global::PCLLF.Presentation.Launch.LegacyLaunchLeftView()
            {
                AprilPosition = new Func<global::System.Windows.Media.TranslateTransform>(() => this.AprilPosTrans),
                Input = new Func<global::System.Windows.FrameworkElement>(() => this.PanInput),
                Launching = new Func<global::System.Windows.FrameworkElement>(() => this.PanLaunching),
                LaunchingHint = new Func<global::System.Windows.FrameworkElement>(() => this.PanLaunchingHint),
                LaunchingInfo = new Func<global::System.Windows.FrameworkElement>(() => this.PanLaunchingInfo),
                LoginPanel = new Func<global::System.Windows.FrameworkElement>(() => this.PanLogin),
                LoginChildren = new Func<global::System.Windows.Controls.Panel>(() => this.PanLogin),
                Method = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingMethod),
                Name = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingName),
                Stage = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingStage),
                Title = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingTitle),
                Progress = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingProgress),
                ProgressLeft = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingProgressLeft),
                Download = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingDownload),
                DownloadLeft = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingDownloadLeft),
                Hint = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabLaunchingHint),
                SingleTypeLabel = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabTypeOne),
                FinishedColumn = new Func<global::System.Windows.Controls.ColumnDefinition>(() => this.ProgressLaunchingFinished),
                UnfinishedColumn = new Func<global::System.Windows.Controls.ColumnDefinition>(() => this.ProgressLaunchingUnfinished),
                TypePanel = new Func<global::System.Windows.FrameworkElement>(() => this.PanType),
                SingleTypePanel = new Func<global::System.Windows.FrameworkElement>(() => this.PanTypeOne),
                MicrosoftRadio = new Func<global::System.Windows.FrameworkElement>(() => this.RadioLoginType5),
                OfflineRadio = new Func<global::System.Windows.FrameworkElement>(() => this.RadioLoginType0),
                SingleTypePath = new Func<global::System.Windows.Shapes.Path>(() => this.PathTypeOne),
                MicrosoftChecked = new Func<global::System.Boolean>(() => this.RadioLoginType5.Checked),
                LaunchButtonEnabled = new Func<global::System.Boolean>(() => this.BtnLaunch.IsEnabled),
                LaunchButtonLoaded = new Func<global::System.Boolean>(() => this.BtnLaunch.IsLoaded),
                LaunchButtonText = new Func<global::System.String>(() => this.BtnLaunch.Text),
                CheckNamedRadio = this.CheckNamedRadio,
                RequireNamedRadio = new Action<global::System.String>(Name => ((global::PCL.MyRadioButton)(this.FindName(Name))).Checked = true),
                SetLoadingRun = new Action<global::System.Boolean>(Value => this.LoadLaunching.State.LoadingState = (Value) ? (global::PCL.MyLoading.MyLoadingState.Run) : (global::PCL.MyLoading.MyLoadingState.Stop)),
                RenderDispatchWait = new Action<global::System.Action>(Work => this.Dispatcher.Invoke(Work, global::System.Windows.Threading.DispatcherPriority.Render))
            };
        }

        private global::PCLLF.Presentation.Launch.LegacyLaunchLeftHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            global::PCLLF.Presentation.Launch.LegacyLaunchLeftHost<global::PCL.ModMinecraft.McInstance> @init = new global::PCLLF.Presentation.Launch.LegacyLaunchLeftHost<global::PCL.ModMinecraft.McInstance>();
            return (@init.ReadString = new Func<global::System.String, global::System.String>(Key => global::PCL.Settings.Get<string>(Key)), @init.ReadInteger = new Func<global::System.String, global::System.Int32>(Key => global::PCL.Settings.Get<int>(Key)), @init.ReadBoolean = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)), @init.ReadInstanceInteger = new Func<global::System.String, global::System.Int32>(Key => global::PCL.Settings.Get<int>(Key, Instance: global::PCL.ModMinecraft.McInstanceSelected)), @init.ReadInstanceString = new Func<global::System.String, global::System.String>(Key => global::PCL.Settings.Get<string>(Key, Instance: global::PCL.ModMinecraft.McInstanceSelected)), @init.WriteSetting = new Action<global::System.String, global::System.Object>((Key, Value) => global::PCL.Settings.Set(Key, Value)), @init.ReadIniVersion = new Func<global::System.String, global::System.String>(Address => global::PCL.ModBase.ReadIni(Address, "Version")), @init.LoginType = new Func<global::System.Int32>(() => (global::System.Int32)global::PCL.Settings.Get<global::PCL.ModLaunch.McLoginType>("LoginType")), @init.SetLoginType = new Action<global::System.Int32>(Value => global::PCL.Settings.Set("LoginType", (global::PCL.ModLaunch.McLoginType)(Value))), @init.ChangeAnimationControl = new Action<global::System.Int32>(Delta => global::PCL.ModAnimation.AniControlEnabled += Delta), @init.Selected = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected), @init.SetSelected = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft.McInstanceSelected = Value), @init.SetSetupInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value), @init.CreateInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Value => new global::PCL.ModMinecraft.McInstance(Value)), @init.CheckInstance = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => Value.Check()), @init.LoadInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => Value.Load()), @init.InstancePath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathVersion), @init.InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.Name), @init.InstanceLogo = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.Logo), @init.Instances = new Func<global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>>(() => global::PCL.ModMinecraft.McInstanceList.Values), @init.Folder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected), @init.SetFolder = new Action<global::System.String>(Value => global::PCL.ModMinecraft.McFolderSelected = Value), @init.FirstFolderLocation = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderList.First().Location), @init.BasePath = new Func<global::System.String>(() => global::MeloongCore.Paths.Base), @init.IsParentPath = new Func<global::System.String, global::System.Boolean>(Address => global::MeloongCore.PathUtils.IsParentOf(global::PCL.ModMinecraft.McFolderSelected, Address)), @init.WaitForInstanceFolder = new Action<global::System.String>(Address => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, Address, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\", WaitForExit: true)), @init.InstanceListState = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchPanelState>(() => ((global::PCLLF.Presentation.Launch.LegacyLaunchPanelState)(((global::System.Int32)global::PCL.ModMinecraft.McInstanceListLoader.State)))), @init.FolderListState = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchPanelState>(() => ((global::PCLLF.Presentation.Launch.LegacyLaunchPanelState)(((global::System.Int32)global::PCL.ModMinecraft.McFolderListLoader.State)))), @init.LaunchState = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchPanelState>(() => ((global::PCLLF.Presentation.Launch.LegacyLaunchPanelState)(((global::System.Int32)global::PCL.ModLaunch.McLaunchLoader.State)))), @init.WaitForFolders = new Action<global::System.Boolean>(Force => global::PCL.ModMinecraft.McFolderListLoader.WaitForExit(IsForceRestart: Force)), @init.CreateCurrentFolder = new Action(() => global::PCL.PageSelectLeft.CreateMcFolderInCurrentPath()), @init.InstallModpack = new Func<global::System.String, global::PCLLF.Presentation.Launch.LegacyLaunchJobPort>(Address => global::PCL.LaunchPageRuntime.Job(global::PCL.ModModpack.ModpackInstall(Address))), @init.BindListStates = this.BindListStates, @init.Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)), @init.RunInThread = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInThread(Work)), @init.RunInNewThread = new Action<global::System.Action, global::System.String, global::System.Threading.ThreadPriority>((Work, Name, Priority) => global::PCL.ModBase.RunInNewThread(Work, Name, Priority)), @init.RandomInteger = new Func<global::System.Int32, global::System.Int32, global::System.Int32>((Minimum, Maximum) => global::PCL.ModBase.RandomInteger(Minimum, Maximum)), @init.LoginAble = global::PCL.ModLaunch.McLoginAble, @init.StartAutoLogin = new Action(() => global::PCL.ModLaunch.McLoginLoader.Start()), @init.CommandLine = new Func<global::System.String>(() => global::System.Environment.CommandLine), @init.RandomHint = global::PCL.PageOtherTest.GetRandomHint, @init.ReadPage = global::PCL.LaunchPageRuntime.ReadPage, @init.CreatePage = global::PCL.LaunchPageRuntime.CreatePage, @init.StorePage = global::PCL.LaunchPageRuntime.StorePage, @init.ReloadPage = new Action<global::System.Windows.FrameworkElement, global::System.Boolean>((Page, KeepInput) => global::Microsoft.VisualBasic.CompilerServices.NewLateBinding.LateCall(Page, null, "Reload", new object[] { KeepInput }, null, null, null, true)), @init.ShieldLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonShield), @init.OfflineLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonOffline), @init.CardLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonCard), @init.ExportScriptSafe = new Func<global::System.Boolean>(() => global::PCL.ModLaunch.CurrentLaunchOptions?.SaveBatch is not null), @init.ExportScript = new Func<global::System.Boolean>(() => global::PCL.ModLaunch.CurrentLaunchOptions.SaveBatch is not null), @init.ResetLaunchProcessAndWatcher = global::PCL.PageLaunchLeft.ResetLaunchProcessAndWatcher, @init.RightPageAllowsLaunch = new Func<global::System.Boolean>(() => ((global::PCL.ModMain.FrmMain.PageRight is null || (global::PCL.ModMain.FrmMain.PageRight.PageState == global::PCL.MyPageRight.PageStates.ContentStay)) || (global::PCL.ModMain.FrmMain.PageRight.PageState == global::PCL.MyPageRight.PageStates.ContentEnter))), @init.AprilEnabled = new Func<global::System.Boolean>(() => global::PCL.ModMain.IsAprilEnabled), @init.AprilGiveUp = new Func<global::System.Boolean>(() => global::PCL.ModMain.IsAprilGiveup), @init.SetAprilGiveUp = new Action<global::System.Boolean>(Value => global::PCL.ModMain.IsAprilGiveup = Value), @init.ResetGlobalAprilTransforms = global::PCL.PageLaunchLeft.ResetGlobalAprilTransforms, @init.RefreshAprilButton = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraApril.ShowRefresh()), @init.ModpackMode = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init.HiddenForceShow = new Func<global::System.Boolean>(() => global::PCL.PageSetupUI.HiddenForceShow), @init.SelectedInstanceLocked = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceLock.BlocksNavigation(global::PCL.ModMinecraft.McInstanceSelected)), @init.AllowsInstance = global::PCL.ModModpackMode.AllowsInstance, @init.ApplyModpackLayout = new Action(() => global::PCL.ModModpackMode.ApplyLaunchLayout(global::PCL.ModMain.FrmLaunchLeft)), @init.GlobalButtons = new global::PCLLF.Presentation.Launch.LegacyLaunchButtonView() { Text = new Action<global::System.String>(Value => global::PCL.ModMain.FrmLaunchLeft.BtnLaunch.Text = Value), VersionText = new Action<global::System.String>(Value => global::PCL.ModMain.FrmLaunchLeft.LabVersion.Text = Value), EnableLaunch = new Action<global::System.Boolean>(Value => global::PCL.ModMain.FrmLaunchLeft.BtnLaunch.IsEnabled = Value), EnableVersion = new Action<global::System.Boolean>(Value => global::PCL.ModMain.FrmLaunchLeft.BtnVersion.IsEnabled = Value), VersionVisibility = new Action<global::System.Windows.Visibility>(Value => global::PCL.ModMain.FrmLaunchLeft.BtnVersion.Visibility = Value), MoreVisibility = new Action<global::System.Windows.Visibility>(Value => global::PCL.ModMain.FrmLaunchLeft.BtnMore.Visibility = Value), ReadVersionVisibility = new Func<global::System.Windows.Visibility>(() => global::PCL.ModMain.FrmLaunchLeft.BtnVersion.Visibility) }, @init.HintError = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red)), @init.LaunchLog = new Action<global::System.String>(Text => global::PCL.ModLaunch.McLaunchLog(Text)), @init.StartGame = new Action(() => global::PCL.ModLaunch.McLaunchStart()), @init.NavigateInstances = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSelect)), @init.NavigateDownloadInstall = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, global::PCL.FormMain.PageSubType.DownloadInstall)), @init.NavigateInstanceSetup = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, (global::PCL.FormMain.PageSubType)0)), @init.TestNavigate = global::PCL.PageLaunchLeft.TestNavigate, @init.EndProgramForce = ReturnCode => global::PCL.FormMain.EndProgramForce((global::PCL.ModBase.ProcessReturnValues)ReturnCode), @init.HasRealLaunch = new Func<global::System.Boolean>(() => global::PCL.ModLaunch.McLaunchLoaderReal is not null), @init.HasWatcher = new Func<global::System.Boolean>(() => global::PCL.ModLaunch.McLaunchWatcher is not null), @init.RealLaunch = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchJobPort>(() => global::PCL.LaunchPageRuntime.Job(global::PCL.ModLaunch.McLaunchLoaderReal, () => global::PCL.ModLaunch.McLaunchLoaderReal.GetLoaderList(false))), @init.KillWatcher = new Action(() => global::PCL.ModLaunch.McLaunchWatcher.Kill()), @init.LaunchProcess = new Func<global::System.Diagnostics.Process>(() => global::PCL.ModLaunch.McLaunchProcess), @init.NetSpeed = new Func<global::System.Int64>(() => global::PCL.ModNet.NetManager.Speed), @init.NetTasks = new Func<global::System.Collections.Generic.IEnumerable<global::PCLLF.Presentation.Launch.LegacyLaunchItemPort>>(() => global::PCL.ModNet.NetManager.Tasks.Select(Value => global::PCL.LaunchPageRuntime.Item(Value))), @init.FormatByteSize = global::MeloongCore.StringUtils.FormatByteSize, @init).@init;
        }

        private void BindListStates(global::System.Action Work)
        {
            global::PCL.ModMinecraft.McInstanceListLoader.LoadingStateChanged += (NewState, OldState) => Work();
            global::PCL.ModMinecraft.McFolderListLoader.LoadingStateChanged += (NewState, OldState) => Work();
        }

        private void CheckNamedRadio(global::System.String Name)
        {
            global::PCL.MyRadioButton Control = (global::PCL.MyRadioButton)this.FindName(Name);
            if (Control is not null)
                Control.Checked = true;
        }

        private static void ResetLaunchProcessAndWatcher()
        {
            global::PCL.ModLaunch.McLaunchProcess = (global::System.Diagnostics.Process)null;
            global::PCL.ModLaunch.McLaunchWatcher = (global::PCL.ModWatcher.Watcher)null;
        }

        private static void ResetGlobalAprilTransforms()
        {
            global::PCL.ModMain.FrmLaunchLeft.AprilScaleTrans.ScaleX = 1d;
            global::PCL.ModMain.FrmLaunchLeft.AprilScaleTrans.ScaleY = 1d;
            global::PCL.ModMain.FrmLaunchLeft.AprilPosTrans.X = 0d;
            global::PCL.ModMain.FrmLaunchLeft.AprilPosTrans.Y = 0d;
        }

        private static void TestNavigate(global::System.Int32 Kind, global::System.Int32 SubKind)
        {
            switch (Kind)
            {
                case 0:
                {
                    if (((SubKind) == (0)))
                    {
                        global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download);
                    }
                    else
                    {
                        global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, global::PCL.FormMain.PageSubType.DownloadMod);
                    }

                    break;
                }

                case 1:
                {
                    global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Setup);
                    break;
                }

                case 2:
                {
                    global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Other);
                    break;
                }
            }
        }

        public void PageLaunchLeft_Loaded()
        {
            CheckMoreSettings.Checked = Settings.Get<bool>("UiLaunchMoreSettings");
            this.Native.Loaded();
        }

        public void PageChangeToLaunching()
        {
            this.Native.PageChangeToLaunching();
        }

        public void PageChangeToLogin()
        {
            this.Native.PageChangeToLogin();
        }

        public void RefreshPage(global::System.Boolean KeepInput, global::System.Boolean Anim)
        {
            this.Native.RefreshPage(KeepInput, Anim);
        }

        private void RadioLoginType_Change(global::System.Object sender, global::System.Boolean raiseByMouse)
        {
            this.Native.RadioChanged(raiseByMouse);
        }

        public static global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> SkinMs = new global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>("Loader Skin Ms", global::PCL.PageLaunchLeft.SkinMsLoad, global::PCL.PageLaunchLeft.SkinMsInput, global::System.Threading.ThreadPriority.AboveNormal);
        private static (global::System.String, global::System.String) SkinMsInput()
        {
            return global::PCL.LaunchPageRuntime.Skins.Input("Ms");
        }

        private static void SkinMsLoad(global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> Data)
        {
            global::PCL.LaunchPageRuntime.Skins.Official(Data, "Ms");
        }

        public static global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> SkinLegacy = new global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>("Loader Skin Legacy", global::PCL.PageLaunchLeft.SkinLegacyLoad, global::PCL.PageLaunchLeft.SkinLegacyInput, global::System.Threading.ThreadPriority.AboveNormal);
        private static (global::System.String, global::System.String) SkinLegacyInput()
        {
            return global::PCL.LaunchPageRuntime.Skins.Input("Legacy");
        }

        private static void SkinLegacyLoad(global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> Data)
        {
            global::PCL.LaunchPageRuntime.Skins.Offline(Data);
        }

        public static global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> SkinNide = new global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>("Loader Skin Nide", global::PCL.PageLaunchLeft.SkinNideLoad, global::PCL.PageLaunchLeft.SkinNideInput, global::System.Threading.ThreadPriority.AboveNormal);
        private static (global::System.String, global::System.String) SkinNideInput()
        {
            return global::PCL.LaunchPageRuntime.Skins.Input("Nide");
        }

        private static void SkinNideLoad(global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> Data)
        {
            global::PCL.LaunchPageRuntime.Skins.Official(Data, "Nide");
        }

        public static global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> SkinAuth = new global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>("Loader Skin Auth", global::PCL.PageLaunchLeft.SkinAuthLoad, global::PCL.PageLaunchLeft.SkinAuthInput, global::System.Threading.ThreadPriority.AboveNormal);
        private static (global::System.String, global::System.String) SkinAuthInput()
        {
            return global::PCL.LaunchPageRuntime.Skins.Input("Auth");
        }

        private static void SkinAuthLoad(global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String> Data)
        {
            global::PCL.LaunchPageRuntime.Skins.Official(Data, "Auth");
        }

        // 必须位于四个真实加载器之后，保留 LF 的静态初始化顺序与列表身份。
        public static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>> SkinLoaders = new global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>>()
        {
            global::PCL.PageLaunchLeft.SkinMs,
            global::PCL.PageLaunchLeft.SkinLegacy,
            global::PCL.PageLaunchLeft.SkinNide,
            global::PCL.PageLaunchLeft.SkinAuth
        };
        private void BtnVersion_Click()
        {
            this.Native.VersionClick();
        }

        public void LaunchButtonClick()
        {
            this.Native.LaunchButtonClick();
        }

        public void RefreshButtonsUI()
        {
            this.Native.RefreshButtonsUI();
        }

        public void BtnCancel_Click()
        {
            this.Native.CancelClick();
        }

        private void BtnMore_Click()
        {
            this.Native.MoreClick();
        }

        public void LaunchingRefresh()
        {
            this.Native.LaunchingRefresh();
        }

        private void PanLaunchingInfo_SizeChangedW(global::System.Object sender, global::System.Windows.SizeChangedEventArgs e)
        {
            this.Native.WidthChanged(e);
        }

        private void PanLaunchingInfo_SizeChangedH(global::System.Object sender, global::System.Windows.SizeChangedEventArgs e)
        {
            this.Native.HeightChanged(e);
        }
    }
}
