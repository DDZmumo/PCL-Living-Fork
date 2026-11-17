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
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using SetupController = global::PCLCS.InstanceSettingsController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.MyIconButton>;
using SetupHost = global::PCLCS.InstanceSettingsHost<global::PCL.ModMinecraft.McInstance>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceSetup
    {
        private global::System.Boolean LoadedFirst;
        private global::System.Int32 ComboServerLoginLast;
        private global::System.Int32 RamTextLeft;
        private global::System.Int32 RamTextRight;
        private readonly global::PCLCS.InstanceSettingsView<global::PCL.MyListItem, global::PCL.MyIconButton> SettingsView;
        private readonly global::PCLCS.InstanceRamDisplayView RamView;
        private global::PCLCS.InstanceRamDisplayController RamController;
        private global::PCLCS.InstanceSettingsController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.MyIconButton> PageController;
        private global::PCLCS.InstanceSettingsController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.MyIconButton> Controller
        {
            get
            {
                this.UpdateViews();
                if (this.PageController is null)
                {
                    this.RamController = new global::PCLCS.InstanceRamDisplayController(this.RamView, new global::PCLCS.InstanceRamDisplayHost() { Active = new Func<global::System.Boolean>(() => (((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup) && (global::PCL.ModMain.FrmInstanceLeft.PageID == global::PCL.FormMain.PageSubType.InstanceSetup))), Ram = new Func<global::System.Double>(() => global::PCL.PageInstanceSetup.GetRam(global::PCL.PageInstanceLeft.Instance)), TotalBytes = new Func<global::System.Double>(() => (global::System.Double)global::PCL.My.MyWpfExtension.Computer.Info.TotalPhysicalMemory), AvailableBytes = new Func<global::System.Double>(() => (global::System.Double)global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory), Animation = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled), IsAnimating = global::PCL.ModAnimation.AniIsRun, AnimateGrid = new Action<global::System.Double, global::System.Double, global::System.Double>((Used, Game, Empty) => global::PCL.ModAnimation.AniStart(new[] { global::PCL.ModAnimation.AaGridLengthWidth(this.ColumnRamUsed, Used, 800, Ease: new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Strong)), global::PCL.ModAnimation.AaGridLengthWidth(this.ColumnRamGame, Game, 800, Ease: new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Strong)), global::PCL.ModAnimation.AaGridLengthWidth(this.ColumnRamEmpty, Empty, 800, Ease: new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Strong)) }, "VersionSetup Ram Grid")), AnimateLeft = new Action<global::System.Double, global::System.Double, global::System.Double>((Used, Total, Title) => global::PCL.ModAnimation.AniStart(new[] { global::PCL.ModAnimation.AaOpacity(this.LabRamUsed, Used, 100), global::PCL.ModAnimation.AaOpacity(this.LabRamTotal, Total, 100), global::PCL.ModAnimation.AaOpacity(this.LabRamUsedTitle, Title, 100) }, "VersionSetup Ram TextLeft")), AnimateRight = new Action<global::System.Double, global::System.Double>((Game, Title) => global::PCL.ModAnimation.AniStart(new[] { global::PCL.ModAnimation.AaX(this.LabRamGame, Game, 100, Ease: new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Weak)), global::PCL.ModAnimation.AaX(this.LabRamGameTitle, Title, 100, Ease: new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Weak)) }, "VersionSetup Ram TextRight")) });
                    this.PageController = new global::PCLCS.InstanceSettingsController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.MyIconButton>(this.SettingsView, this.CreateHost(), this.RamController);
                }

                return this.PageController;
            }
        }

        public PageInstanceSetup()
        {
            __LfAttachOwnHandles();
            this.LoadedFirst = false;
            this.RamTextLeft = 2;
            this.RamTextRight = 1;
            this.SettingsView = new global::PCLCS.InstanceSettingsView<global::PCL.MyListItem, global::PCL.MyIconButton>();
            this.RamView = new global::PCLCS.InstanceRamDisplayView();
            this.InitializeComponent();
        }

        private void UpdateViews()
        {
            this.SettingsView.Isolation = this.ComboArgumentIndieV2;
            this.SettingsView.Login = this.ComboServerLogin;
            this.SettingsView.JavaKind = this.ComboArgumentJava;
            this.SettingsView.JavaSelect = this.ComboArgumentJavaSelect;
            this.SettingsView.ServerEnter = this.TextServerEnter;
            this.SettingsView.AuthServer = this.TextServerAuthServer;
            this.SettingsView.AuthRegister = this.TextServerAuthRegister;
            this.SettingsView.AuthName = this.TextServerAuthName;
            this.SettingsView.JavaRange = this.TextArgumentJavaRange;
            this.SettingsView.AdvanceRun = this.TextAdvanceRun;
            this.SettingsView.Slider = this.SliderRamCustom;
            this.SettingsView.JavaHint = this.HintArgumentJava;
            this.SettingsView.JavaSelectPanel = this.PanArgumentJavaSelect;
            this.SettingsView.JavaSearch = this.BtnArgumentJavaSearch;
            this.SettingsView.AdvanceRunWait = this.CheckAdvanceRunWait;
            this.SettingsView.JavaLabel = this.LabArgumentJava;
            this.SettingsView.NideControls = new global::System.Windows.FrameworkElement[]
            {
                this.LabServerNide,
                this.TextServerNide,
                this.PanServerNide
            };
            this.SettingsView.AuthControls = new global::System.Windows.FrameworkElement[]
            {
                this.LabServerAuthName,
                this.TextServerAuthName,
                this.LabServerAuthRegister,
                this.TextServerAuthRegister,
                this.LabServerAuthServer,
                this.TextServerAuthServer,
                this.BtnServerAuthLittle
            };
            this.SettingsView.ScrollHome = new Action(() => this.PanBack.ScrollToHome());
            this.SettingsView.ResizeServer = new Action(() => this.CardServer.TriggerForceResize());
            this.SettingsView.LoadedFirst = new Func<global::System.Boolean>(() => this.LoadedFirst);
            this.SettingsView.SetLoadedFirst = new Action<global::System.Boolean>(Value => this.LoadedFirst = Value);
            this.SettingsView.LoginLast = new Func<global::System.Int32>(() => this.ComboServerLoginLast);
            this.SettingsView.SetLoginLast = new Action<global::System.Int32>(Value => this.ComboServerLoginLast = Value);
            this.SettingsView.NideValidated = new Func<global::System.Boolean>(() => this.TextServerNide.IsValidated);
            this.SettingsView.AuthValidated = new Func<global::System.Boolean>(() => this.TextServerAuthServer.IsValidated);
            this.SettingsView.JavaRangeError = new Func<global::System.String>(() => this.TextArgumentJavaRange.ValidateResult);
            this.SettingsView.SetJavaHint = new Action<global::System.String, global::System.Int32>((Text, Theme) =>
            {
                this.HintArgumentJava.Text = Text;
                this.HintArgumentJava.Theme = ((global::PCL.MyHint.Themes)(Theme));
            });
            this.SettingsView.SetJavaRangeValidator = new Action<global::System.Func<global::System.String, global::System.String>>(ValidateText => this.TextArgumentJavaRange.ValidateRules = new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { new global::PCL.ValidateFunc(Text => ValidateText(Text)) });
            this.SettingsView.CreateJavaItem = new Func<global::PCLCS.Java, global::System.String, global::PCL.MyListItem>((Entry, Title) => new global::PCL.MyListItem() { FontSize = 13d, Height = 24d, IsScaleAnimationEnabled = false, Type = global::PCL.MyListItem.CheckType.Clickable, Tag = Entry, Title = Title });
            this.SettingsView.OnJavaClick = new Action<global::PCL.MyListItem, global::System.Action>((Item, Work) => Item.Click += (_, _) => Work());
            this.SettingsView.CreateJavaButton = new Func<global::System.Boolean, global::PCL.MyIconButton>(Open => (Open) ? (new global::PCL.MyIconButton() { Logo = global::PCL.ModBase.Logo.IconButtonOpen, LogoScale = 1.1d, Height = 24d, Width = 24d }) : (new global::PCL.MyIconButton() { Logo = global::PCL.ModBase.Logo.IconButtonStop, Height = 24d, Width = 24d }));
            this.SettingsView.OnButtonClick = new Action<global::PCL.MyIconButton, global::System.Action>((Button, Work) => Button.Click += (_, _) => Work());
            this.SettingsView.SetButtons = new Action<global::PCL.MyListItem, global::System.Collections.Generic.List<global::PCL.MyIconButton>>((Item, Buttons) => Item.Buttons = (global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>)Buttons);
            this.RamView.Game = this.LabRamGame;
            this.RamView.Used = this.LabRamUsed;
            this.RamView.Total = this.LabRamTotal;
            this.RamView.GameTitle = this.LabRamGameTitle;
            this.RamView.UsedTitle = this.LabRamUsedTitle;
            this.RamView.RectUsed = this.RectRamUsed;
            this.RamView.Display = this.PanRamDisplay;
            this.RamView.UsedColumn = this.ColumnRamUsed;
            this.RamView.GameColumn = this.ColumnRamGame;
            this.RamView.EmptyColumn = this.ColumnRamEmpty;
            this.RamView.LeftState = new Func<global::System.Int32>(() => this.RamTextLeft);
            this.RamView.SetLeftState = new Action<global::System.Int32>(Value => this.RamTextLeft = Value);
            this.RamView.RightState = new Func<global::System.Int32>(() => this.RamTextRight);
            this.RamView.SetRightState = new Action<global::System.Int32>(Value => this.RamTextRight = Value);
            this.RamView.SetSliderMax = new Action<global::System.Int32>(Value => this.SliderRamCustom.MaxValue = Value);
        }

        private global::PCLCS.InstanceSettingsHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            return new global::PCLCS.InstanceSettingsHost<global::PCL.ModMinecraft.McInstance>()
            {
                Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SetInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value),
                Integer = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Int32>((Key, Target) => global::PCL.Settings.Get<int>(Key, Instance: Target)),
                Boolean = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Key, Target) => global::PCL.Settings.Get<bool>(Key, Instance: Target)),
                HasSaved = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Key, Target) => global::PCL.Settings.HasSaved(Key, Target)),
                Set = new Action<global::System.String, global::System.Object, global::PCL.ModMinecraft.McInstance>((Key, Value, Target) => global::PCL.Settings.SetInstanceUserPreference(Key, Value, Target)),
                Reset = new Action<global::System.String, global::PCL.ModMinecraft.McInstance>((Key, Target) => global::PCL.Settings.ResetInstanceUserPreference(Key, Target)),
                SetForcedJava = global::PCL.MmcLaunchPreferences.SetForcedJava,
                ApplyVisibility = global::PCL.ModModpackMode.ApplyInstanceVisibility,
                RefreshSettings = new Action(() => global::PCL.SettingService.RefreshSettings(this)),
                ResetSettings = new Action(() => global::PCL.SettingService.ResetSettings(this)),
                Animation = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                AddAnimation = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled += Value),
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                JavaWorker = new Func<global::MeloongCore.RedoableWorker<global::PCLCS.Java>>(() => global::PCL.PageInstanceLeft.CurrentJavaWorker),
                JavaRefresh = new Func<global::MeloongCore.RedoableWorker>(() => global::PCL.ModJava.JavaListRefreshWorker),
                JavaRequirement = new Func<global::PCL.ModMinecraft.McInstance, global::MeloongCore.ValueRange<global::System.Version>>(Target => global::PCL.ModJava.GetJavaRequirement(Target).Range),
                ManagedJavaPreview = MmcJavaDiscovery.Preview,
                StartJavaPreview = (Target, Worker) => MmcJavaDiscovery.Start(Target, Worker),
                JavaPreviewIsCurrent = MmcJavaDiscovery.IsCurrentRequest,
                ReloadCurrentJava = global::PCL.PageInstanceLeft.ReloadCurrentJava,
                ImportJava = new Action(() => global::PCL.ModJava.ManuallyImportJava()),
                SearchJava = () => global::PCL.ModJava.ManuallySearchJava(),
                RemoveJava = global::PCL.ModJava.ManuallyRemoveJava,
                OpenInstanceFolder = global::PCL.PageInstanceOverall.OpenInstanceFolder,
                Explorer = new Action<global::System.String>(Path => global::PCL.ModBase.OpenExplorer(Path)),
                Info = new Action<global::System.String>(Text => global::MeloongCore.Logger.Info(Text)),
                HintGreen = new Action<global::System.String, global::System.Boolean>((Text, Log) => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green, Log)),
                Error = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text)),
                ErrorAlert = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text, global::MeloongCore.LogBehavior.Alert)),
                Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warn) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warn)),
                ClearInstanceCache = new Action(() => global::PCL.ModBase.WriteIni(global::PCL.ModMinecraft.McFolderSelected + "PCL.ini", "InstanceCache", "")),
                RefreshInstanceList = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\")),
                LoadInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Name => new global::PCL.ModMinecraft.McInstance(Name).Load()),
                SwitchGlobal = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Setup, global::PCL.FormMain.PageSubType.SetupLaunch)),
                LeftQuote = Conversions.ToString(global::PCL.ModBase.vbLQ),
                RightQuote = Conversions.ToString(global::PCL.ModBase.vbRQ)
            };
        }

        private void PageSetupSystem_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Controller.Loaded();
        }

        public void Reload()
        {
            this.Controller.Reload();
        }

        public void Reset()
        {
            this.Controller.Reset();
        }

        public static void OnVersionRamTypeChanged(global::System.Int32 Type)
        {
            global::PCL.ModMain.FrmInstanceSetup?.RamType(Type);
        }

        public void RamType(global::System.Int32 Type)
        {
            this.Controller.RamType(Type);
        }

        public void RefreshRam(global::System.Boolean ShowAnim)
        {
            var UnusedController = this.Controller;
            this.RamController.Refresh(ShowAnim);
        }

        private void RefreshRam()
        {
            this.RefreshRam(true);
        }

        private void RefreshRamText()
        {
            var UnusedController = this.Controller;
            this.RamController.RefreshText();
        }

        public static global::System.Double GetRam(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCLCS.InstanceRamPolicy.Get<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType>(Instance, (Key, Target) => global::PCL.Settings.Get<int>(Key, Instance: Target), () => (global::System.Double)global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory, Target => global::PCL.PageSetupLaunch.GetRam(Target, true));
        }

        public static void OnVersionServerLoginChanged(global::System.Int32 Type)
        {
            global::PCLCS.InstanceSettingsRules.ServerLoginChanged(() => global::PCL.ModMain.FrmInstanceSetup is not null, () => global::PCL.PageInstanceLeft.Instance, Target => global::PCL.PageInstanceLeft.Instance = Target, Name => new global::PCL.ModMinecraft.McInstance(Name).Load(), () => global::PCL.ModBase.WriteIni(global::PCL.ModMinecraft.McFolderSelected + "PCL.ini", "InstanceCache", ""), () => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\"), Target => Target.Name);
        }

        private void TextServerEnter_Change()
        {
            this.Controller.NormalizeServerPunctuation();
        }

        private void ComboServerLogin_Changed()
        {
            this.Controller.ServerLoginChanged();
        }

        private void UpdateServerLoginUI()
        {
            this.Controller.UpdateServerLoginUi();
        }

        private void BtnServerAuthLittle_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.SetLittleSkin();
        }

        private void ReloadCurrentJava()
        {
            this.Controller.ReloadCurrentJava();
        }

        private void UpdateJavaRelatedUi()
        {
            this.Controller.UpdateJavaRelatedUi();
        }

        private void _UpdateJavaRelatedUi()
        {
            this.Controller.UpdateJavaUi();
        }

        private void HintArgumentJava_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.JavaHintClick();
        }

        public void UpdateJavaList()
        {
            this.Controller.UpdateJavaList();
        }

        private void BtnAdvanceJavaSearch_Click()
        {
            this.Controller.SearchJava();
        }

        private void ComboArgumentIndieV2_SelectionChanged(global::System.Object sender, global::System.Windows.Controls.SelectionChangedEventArgs e)
        {
            this.Controller.IsolationChanged((global::System.Windows.Controls.ComboBox)(sender), e);
        }

        private void TextAdvanceRun_TextChanged(global::System.Object sender, global::System.Windows.Controls.TextChangedEventArgs e)
        {
            this.Controller.AdvanceRunChanged();
        }

        private void BtnSwitch_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.SwitchGlobal();
        }

        private void ReplaceEnter(global::PCL.MyTextBox sender, global::System.Windows.Controls.TextChangedEventArgs e)
        {
            global::PCLCS.InstanceSettingsController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.MyIconButton>.ReplaceEnter(sender);
        }
    }
}
