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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using InstallKind = global::PCLCS.DownloadInstallComponent;
using InstallCore = global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData>;
using ThrottleDebounce;

namespace PCL
{
    // LF XAML/event/loader/field adapter. Selection, compatibility, rendering and install flow are in InstallCore.
    public partial class PageDownloadInstall
    {
        public global::System.Boolean IsInSelectPage;
        public global::System.String VanillaName;
        public global::Newtonsoft.Json.Linq.JObject VanillaData;
        public global::System.String VanillaIcon;
        public global::PCL.ModDownload.DlOptiFineListEntry SelectedOptiFine;
        public global::PCL.ModDownload.DlLiteLoaderListEntry SelectedLiteLoader;
        public global::PCL.ModDownload.DlForgeVersionEntry SelectedForge;
        public global::PCL.ModDownload.DlNeoForgeListEntry SelectedNeoForge;
        public global::System.String SelectedFabric;
        public global::PCL.ResourceVersion SelectedFabricApi;
        public global::PCL.ResourceVersion SelectedOptiFabric;
        public global::System.Boolean IsSelectNameEdited;
        public global::Newtonsoft.Json.Linq.JArray MinecraftVersions;
        public static global::System.String VersionWaitingSelect = (global::System.String)null;
        public global::System.Boolean AutoSelectedFabricApi;
        public global::System.Boolean AutoSelectedOptiFabric;
        private global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData> Controller;
        private global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData> Service
        {
            get
            {
                if (this.Controller is null)
                    this.Controller = new global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData>(this.CreateHost(), global::PCL.PageDownloadInstall.CreateAnimations());
                return this.Controller;
            }
        }

        private int VanillaDrop
        {
            get
            {
                return this.Service.VanillaDrop;
            }
        }

        public PageDownloadInstall()
        {
            __LfAttachOwnHandles();
            this.IsInSelectPage = false;
            this.SelectedOptiFine = (global::PCL.ModDownload.DlOptiFineListEntry)null;
            this.SelectedLiteLoader = (global::PCL.ModDownload.DlLiteLoaderListEntry)null;
            this.SelectedForge = (global::PCL.ModDownload.DlForgeVersionEntry)null;
            this.SelectedNeoForge = (global::PCL.ModDownload.DlNeoForgeListEntry)null;
            this.SelectedFabric = (global::System.String)null;
            this.SelectedFabricApi = (global::PCL.ResourceVersion)null;
            this.SelectedOptiFabric = (global::PCL.ResourceVersion)null;
            this.IsSelectNameEdited = false;
            this.AutoSelectedFabricApi = false;
            this.AutoSelectedOptiFabric = false;
            this.InitializeComponent();
        }

        private global::PCLCS.DownloadInstallPageHost<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest> CreateHost()
        {
            return new global::PCLCS.DownloadInstallPageHost<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest>()
            {
                State = new global::PCL.PageDownloadInstall.SelectionAdapter(this),
                View = this.CreateView,
                Component = this.CreateComponent,
                Loading = this.CreateLoading,
                LoadingRun = (global::System.Int32)global::PCL.MyLoading.MyLoadingState.Run,
                LoadingError = (global::System.Int32)global::PCL.MyLoading.MyLoadingState.Error,
                LoadingUnloaded = (global::System.Int32)global::PCL.MyLoading.MyLoadingState.Unloaded,
                OptiFine = new global::PCLCS.DownloadInstallSource<global::System.Collections.Generic.List<global::PCL.ModDownload.DlOptiFineListEntry>>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlOptiFineListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlOptiFineListEntry>>(() => global::PCL.ModDownload.DlOptiFineListLoader.Output.Value),
                    Start = new Action(() => global::PCL.ModDownload.DlOptiFineListLoader.Start())
                },
                LiteLoader = new global::PCLCS.DownloadInstallSource<global::System.Collections.Generic.List<global::PCL.ModDownload.DlLiteLoaderListEntry>>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlLiteLoaderListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlLiteLoaderListEntry>>(() => global::PCL.ModDownload.DlLiteLoaderListLoader.Output.Value),
                    Start = new Action(() => global::PCL.ModDownload.DlLiteLoaderListLoader.Start())
                },
                NeoForge = new global::PCLCS.DownloadInstallSource<global::System.Collections.Generic.List<global::PCL.ModDownload.DlNeoForgeListEntry>>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlNeoForgeListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlNeoForgeListEntry>>(() => global::PCL.ModDownload.DlNeoForgeListLoader.Output.Value),
                    Start = new Action(() => global::PCL.ModDownload.DlNeoForgeListLoader.Start())
                },
                Fabric = new global::PCLCS.DownloadInstallSource<global::Newtonsoft.Json.Linq.JObject>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlFabricListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::Newtonsoft.Json.Linq.JObject>(() => global::PCL.ModDownload.DlFabricListLoader.Output.Value),
                    Start = new Action(() => global::PCL.ModDownload.DlFabricListLoader.Start())
                },
                FabricApi = new global::PCLCS.DownloadInstallSource<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlFabricApiLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>(() => global::PCL.ModDownload.DlFabricApiLoader.Output),
                    Start = new Action(() => global::PCL.ModDownload.DlFabricApiLoader.Start())
                },
                OptiFabric = new global::PCLCS.DownloadInstallSource<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>()
                {
                    Finished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlOptiFabricLoader.State == global::PCL.ModBase.LoadState.Finished)),
                    Output = new Func<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>(() => global::PCL.ModDownload.DlOptiFabricLoader.Output),
                    Start = new Action(() => global::PCL.ModDownload.DlOptiFabricLoader.Start())
                },
                Forge = this.CreateForgeSource,
                BindLoaders = this.BindNativeLoaders,
                RefreshRecommended = global::PCL.ModDownloadLib.McDownloadForgeRecommendedRefresh,
                InitializeLoader = new Action(() => this.PageLoaderInit(this.LoadMinecraft, this.PanLoad, this.PanAllBack, (global::System.Windows.FrameworkElement)null, global::PCL.ModDownload.DlClientListLoader, (_) => this.LoadMinecraft_OnFinish())),
                InitializeNameValidation = this.InitializeNativeValidation,
                SuppressStartAnimation = this.SuppressNativeStartAnimation,
                StartShow = new Action<global::System.Boolean>(Value => this.BtnStart.Show = Value),
                AnimationControls = new Func<global::System.Windows.FrameworkElement, global::System.Collections.Generic.IEnumerable<global::System.Windows.FrameworkElement>>(Parent => this.GetAllAnimControls(Parent)),
                StartForge = this.StartNativeForge,
                ClientFinished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlClientListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                ClientList = new Func<global::Newtonsoft.Json.Linq.JObject>(() => global::PCL.ModDownload.DlClientListLoader.Output.Value),
                FoolName = new Func<global::Newtonsoft.Json.Linq.JToken, global::System.String>(Value => global::PCL.ModMinecraft.GetMcFoolName((global::System.String)Value)),
                ReloadSelected = this.ReloadSelected,
                ShowMinecraftList = this.ShowMinecraftVersionList,
                SelectLogo = this.GetSelectLogo,
                SelectMinecraft = new Action<global::System.Windows.FrameworkElement, global::System.Windows.Input.MouseButtonEventArgs>((Row, Args) => this.MinecraftSelected((global::PCL.MyListItem)Row, Args)),
                MinecraftRow = global::PCL.PageDownloadInstall.CreateMinecraftRow,
                OptiFineRow = new Func<global::PCL.ModDownload.DlOptiFineListEntry, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.OptiFineDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                LiteLoaderRow = new Func<global::PCL.ModDownload.DlLiteLoaderListEntry, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.LiteLoaderDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                ForgeRow = new Func<global::PCL.ModDownload.DlForgeVersionEntry, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.ForgeDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                NeoForgeRow = new Func<global::PCL.ModDownload.DlNeoForgeListEntry, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.NeoForgeDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                FabricRow = new Func<global::Newtonsoft.Json.Linq.JObject, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.FabricDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args))),
                FabricApiRow = new Func<global::PCL.ResourceVersion, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.FabricApiDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args))),
                OptiFabricRow = new Func<global::PCL.ResourceVersion, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Entry, Click) => global::PCL.ModDownloadLib.OptiFabricDownloadListItem(Entry, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args))),
                ForgePreload = new Action<global::System.Windows.Controls.Panel, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>>((Panel, Entries, Click) => global::PCL.ModDownloadLib.ForgeDownloadListItemPreload((global::System.Windows.Controls.StackPanel)Panel, Entries, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                NeoForgePreload = new Action<global::System.Windows.Controls.Panel, global::System.Collections.Generic.List<global::PCL.ModDownload.DlNeoForgeListEntry>, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>>((Panel, Entries, Click) => global::PCL.ModDownloadLib.NeoForgeDownloadListItemPreload((global::System.Windows.Controls.StackPanel)Panel, Entries, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false)),
                FabricBuilder = this.BuildNativeFabric,
                BuildFabric = new Action(() => this.CardFabric.BuildContent()),
                Materialize = new Func<global::System.Windows.FrameworkElement, global::System.Windows.FrameworkElement>(Row => global::PCL.MyVirtualizingElement.TryInit(Row)),
                RowTitle = new Func<global::System.Windows.FrameworkElement, global::System.String>(Row => ((global::PCL.MyListItem)Row).Title),
                RowLogo = new Func<global::System.Windows.FrameworkElement, global::System.String>(Row => ((global::PCL.MyListItem)Row).Logo),
                MinecraftCard = global::PCL.PageDownloadInstall.CreateMinecraftCard,
                Gray1 = new Func<global::System.Windows.Media.Brush>(() => global::PCL.ModSecret.ColorGray1),
                Gray4 = new Func<global::System.Windows.Media.Brush>(() => global::PCL.ModSecret.ColorGray4),
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                IsolationSetting = new Func<global::System.Int32>(() => global::PCL.Settings.Get<int>("LaunchArgumentIndieV2")),
                Message = new Func<global::PCLCS.ModpackMessage, global::System.Int32>(Request => global::PCL.ModMain.MyMsgBox(Request.Text, Request.Title, Request.Button1, Request.Button2)),
                StartRequested = this.StartInstall,
                Install = global::PCL.ModDownloadLib.McInstallStart
            };
        }

        private global::PCLCS.DownloadInstallPageView CreateView()
        {
            return new global::PCLCS.DownloadInstallPageView()
            {
                Inner = PanInner,
                Back = PanBack,
                Start = BtnStart,
                NameHint = HintSelectName,
                Select = PanSelect,
                Minecraft = PanMinecraft,
                Name = TextSelectName,
                MinecraftVersion = LabMinecraftVersion,
                HintFabricApi = HintFabricAPI,
                HintOptiFabric = HintOptiFabric,
                HintOptiFabricOld = HintOptiFabricOld,
                HintModOptiFine = HintModOptiFine,
                Logo = new Action<global::System.String>(Value => this.ImgLogo.Source = Value),
                MinecraftLogo = new Action<global::System.String>(Value => this.ImgMinecraft.Source = Value),
                NameHintText = new Action<global::System.String>(Value => this.HintSelectName.Text = Value),
                NameValidated = new Func<global::System.Boolean>(() => this.TextSelectName.IsValidated),
                NameValidationResult = new Func<global::System.String>(() => this.TextSelectName.ValidateResult),
                ValidateName = new Action(() => this.TextSelectName.Validate()),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome())
            };
        }

        private global::PCLCS.DownloadInstallComponentView CreateComponent(global::PCLCS.DownloadInstallComponent Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.DownloadInstallComponent.OptiFine:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardOptiFine, this.PanOptiFineInfo, this.PanOptiFine, this.BtnOptiFineClear, this.ImgOptiFine, this.LabOptiFine);
                }

                case global::PCLCS.DownloadInstallComponent.LiteLoader:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardLiteLoader, this.PanLiteLoaderInfo, this.PanLiteLoader, this.BtnLiteLoaderClear, this.ImgLiteLoader, this.LabLiteLoader);
                }

                case global::PCLCS.DownloadInstallComponent.Forge:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardForge, this.PanForgeInfo, this.PanForge, this.BtnForgeClear, this.ImgForge, this.LabForge);
                }

                case global::PCLCS.DownloadInstallComponent.NeoForge:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardNeoForge, this.PanNeoForgeInfo, this.PanNeoForge, this.BtnNeoForgeClear, this.ImgNeoForge, this.LabNeoForge);
                }

                case global::PCLCS.DownloadInstallComponent.Fabric:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardFabric, this.PanFabricInfo, this.PanFabric, this.BtnFabricClear, this.ImgFabric, this.LabFabric);
                }

                case global::PCLCS.DownloadInstallComponent.FabricApi:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardFabricApi, this.PanFabricApiInfo, this.PanFabricApi, this.BtnFabricApiClear, this.ImgFabricApi, this.LabFabricApi);
                }

                case global::PCLCS.DownloadInstallComponent.OptiFabric:
                {
                    return global::PCL.PageDownloadInstall.NativeComponent(this.CardOptiFabric, this.PanOptiFabricInfo, this.PanOptiFabric, this.BtnOptiFabricClear, this.ImgOptiFabric, this.LabOptiFabric);
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }

        private static global::PCLCS.DownloadInstallComponentView NativeComponent(global::PCL.MyCard Card, global::System.Windows.Controls.Grid Info, global::System.Windows.Controls.StackPanel Versions, global::System.Windows.FrameworkElement Clear, global::System.Windows.FrameworkElement Image, global::System.Windows.Controls.TextBlock Label)
        {
            return new global::PCLCS.DownloadInstallComponentView()
            {
                Card = Card,
                MainSwap = new Func<global::System.Windows.FrameworkElement>(() => Card.MainSwap),
                Info = Info,
                Versions = Versions,
                Clear = Clear,
                Image = Image,
                Label = Label,
                IsSwapped = new Func<global::System.Boolean>(() => Card.IsSwapped),
                SetSwapped = new Action<global::System.Boolean>(Value => Card.IsSwapped = Value)
            };
        }

        private global::PCLCS.DownloadInstallLoadingView CreateLoading(global::PCLCS.DownloadInstallComponent Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.DownloadInstallComponent.OptiFine:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadOptiFine);
                }

                case global::PCLCS.DownloadInstallComponent.LiteLoader:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadLiteLoader);
                }

                case global::PCLCS.DownloadInstallComponent.Forge:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadForge);
                }

                case global::PCLCS.DownloadInstallComponent.NeoForge:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadNeoForge);
                }

                case global::PCLCS.DownloadInstallComponent.Fabric:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadFabric);
                }

                case global::PCLCS.DownloadInstallComponent.FabricApi:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadFabricApi);
                }

                case global::PCLCS.DownloadInstallComponent.OptiFabric:
                {
                    return global::PCL.PageDownloadInstall.NativeLoading(this.LoadOptiFabric);
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }

        private static global::PCLCS.DownloadInstallLoadingView NativeLoading(global::PCL.MyLoading Loader)
        {
            if (Loader is null)
                return (global::PCLCS.DownloadInstallLoadingView)null;
            return new global::PCLCS.DownloadInstallLoadingView()
            {
                IsLoader = new Func<global::System.Boolean>(() => Loader.State.IsLoader),
                LoadingState = new Func<global::System.Int32>(() => (global::System.Int32)Loader.State.LoadingState),
                Error = new Func<global::System.Exception>(() => ((global::PCL.ModLoader.LoaderBase)Loader.State).Error)
            };
        }

        private global::PCLCS.DownloadInstallForgeSource<global::PCL.ModDownload.DlForgeVersionEntry> CreateForgeSource()
        {
            global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>> Loader = (global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>)this.LoadForge.State;
            return new global::PCLCS.DownloadInstallForgeSource<global::PCL.ModDownload.DlForgeVersionEntry>()
            {
                Input = new Func<global::System.String>(() => Loader.Input),
                Output = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>(() => Loader.Output),
                Finished = new Func<global::System.Boolean>(() => (Loader.State == global::PCL.ModBase.LoadState.Finished))
            };
        }

        private void BindNativeLoaders()
        {
            this.LoadOptiFine.State = global::PCL.ModDownload.DlOptiFineListLoader;
            this.LoadLiteLoader.State = global::PCL.ModDownload.DlLiteLoaderListLoader;
            this.LoadFabric.State = global::PCL.ModDownload.DlFabricListLoader;
            this.LoadFabricApi.State = global::PCL.ModDownload.DlFabricApiLoader;
            this.LoadNeoForge.State = global::PCL.ModDownload.DlNeoForgeListLoader;
            this.LoadOptiFabric.State = global::PCL.ModDownload.DlOptiFabricLoader;
        }

        private void InitializeNativeValidation()
        {
            this.TextSelectName.ValidateRules = new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>()
            {
                new global::PCL.ValidateFolderName(global::PCL.ModMinecraft.McFolderSelected + "versions")
            };
            this.TextSelectName.Validate();
        }

        private void SuppressNativeStartAnimation(global::System.Boolean Value)
        {
            if (Value)
            {
                this.DisabledPageAnimControls.Add(this.BtnStart);
            }
            else
            {
                this.DisabledPageAnimControls.Remove(this.BtnStart);
            }
        }

        private void StartNativeForge(global::System.String Version)
        {
            var Loader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>("DlForgeVersion " + Version, global::PCL.ModDownload.DlForgeVersionMain);
            this.LoadForge.State = Loader;
            Loader.Start(Version);
        }

        private static global::System.Windows.FrameworkElement CreateMinecraftRow(global::Newtonsoft.Json.Linq.JObject Entry, global::System.Action<global::System.Windows.FrameworkElement, global::System.Windows.Input.MouseButtonEventArgs> Click)
        {
            global::PCL.MyListItem.ClickEventHandler Callback = (global::PCL.MyListItem.ClickEventHandler)null;
            if (Click is not null)
                Callback = new global::PCL.MyListItem.ClickEventHandler((Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args));
            return global::PCL.ModDownloadLib.McDownloadListItem(Entry, Callback, false);
        }

        private void BuildNativeFabric(global::System.Collections.Generic.IEnumerable<global::Newtonsoft.Json.Linq.JObject> Versions, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs> Click)
        {
            this.CardFabric.SwapControl = this.PanFabric;
            this.CardFabric.SetContentBuilder(Versions, Version => global::PCL.ModDownloadLib.FabricDownloadListItem(Version, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args)));
        }

        private static global::System.Windows.FrameworkElement CreateMinecraftCard(global::System.String Title, global::System.Windows.Controls.Panel Parent, global::System.Collections.Generic.List<global::Newtonsoft.Json.Linq.JObject> Versions, global::System.Action<global::System.Windows.FrameworkElement, global::System.Windows.Input.MouseButtonEventArgs> Click, global::System.Boolean Latest)
        {
            var Card = new global::PCL.MyCard()
            {
                Title = Title,
                Margin = (Latest) ? (new global::System.Windows.Thickness(0d, 15d, 0d, 15d)) : (new global::System.Windows.Thickness(0d, 0d, 0d, 15d))
            };
            var Stack = new global::System.Windows.Controls.StackPanel()
            {
                Margin = new global::System.Windows.Thickness(20d, (global::System.Double)global::PCLLF.Presentation.Controls.MyCardView.SwapedHeight, 18d, 0d),
                VerticalAlignment = global::System.Windows.VerticalAlignment.Top,
                RenderTransform = new global::System.Windows.Media.TranslateTransform(0d, 0d)
            };
            Card.Children.Add(Stack);
            Card.SwapControl = Stack;
            Card.SetContentBuilder(Versions, Version => global::PCL.ModDownloadLib.McDownloadListItem(Version, (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), false));
            if (Latest)
            {
                Card.BuildContent();
            }
            else
            {
                Card.IsSwapped = true;
            }

            return Card;
        }

        public void ControlInit()
        {
            this.Service.ControlInit();
        }

        public virtual void LoaderInit()
        {
            this.Service.LoaderInit();
        }

        public virtual void Init()
        {
            this.Service.Init();
        }

        public void PrepareSelectPage()
        {
            this.Service.PrepareSelectPage();
        }

        private void EnterSelectPage()
        {
            this.Service.EnterSelectPage();
        }

        public void ReloadComponentLists()
        {
            this.Service.ReloadComponentLists();
        }

        public void ExitSelectPage()
        {
            this.Service.ExitSelectPage();
        }

        protected void ChangeNativeMinecraftInPlace(FrameworkElement row) => Service.ChangeMinecraftInPlace(row);

        public virtual void MinecraftSelected(global::PCL.MyListItem sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Service.MinecraftSelected(sender, e);
        }

        public virtual void ReloadSelected()
        {
            this.Service.ReloadSelected();
        }

        public void ClearSelected()
        {
            this.Service.ClearSelected();
        }

        private void SetPanelVisibility(global::System.Windows.Controls.Grid Panel, global::System.Boolean IsVisible)
        {
            this.Service.SetPanelVisibility(Panel, IsVisible);
        }

        public virtual global::System.String GetSelectLogo()
        {
            return this.Service.GetSelectLogo();
        }

        public global::System.String GetSelectName()
        {
            return this.Service.GetSelectName();
        }

        private void SelectNameUpdate()
        {
            this.Service.SelectNameUpdate();
        }

        private void TextSelectName_TextChanged(global::System.Object sender, global::System.Windows.Controls.TextChangedEventArgs e)
        {
            this.Service.NameTextChanged();
        }

        private void TextSelectName_ValidateChanged(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.NameValidateChanged();
        }

        private void LoadMinecraft_OnFinish()
        {
            this.Service.LoadMinecraftOnFinish();
        }

        public virtual void ShowMinecraftVersionList()
        {
            this.Service.ShowMinecraftVersionList();
        }

        private void TrySelectWaitingVersion()
        {
            this.Service.TrySelectWaitingVersion();
        }

        public global::System.String LoadOptiFineGetError()
        {
            return this.Service.LoadOptiFineGetError();
        }

        public static global::System.Boolean IsOptiFineSuitForForge(global::PCL.ModDownload.DlOptiFineListEntry OptiFine, global::PCL.ModDownload.DlForgeVersionEntry Forge)
        {
            return global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData>.IsOptiFineSuitForForge(OptiFine, Forge);
        }

        private void CardOptiFine_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.OptiFine, () => e.Handled = true);
        }

        private void OptiFine_Loaded()
        {
            this.Service.OptiFineLoaded();
        }

        private void OptiFine_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.OptiFineSelected(sender, e);
        }

        private void OptiFine_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.OptiFineClear();
        }

        public global::System.String LoadLiteLoaderGetError()
        {
            return this.Service.LoadLiteLoaderGetError();
        }

        private void CardLiteLoader_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.LiteLoader, () => e.Handled = true);
        }

        private void LiteLoader_Loaded()
        {
            this.Service.LiteLoaderLoaded();
        }

        private void LiteLoader_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.LiteLoaderSelected(sender, e);
        }

        private void LiteLoader_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.LiteLoaderClear();
        }

        public global::System.String LoadForgeGetError()
        {
            return this.Service.LoadForgeGetError();
        }

        private void CardForge_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.Forge, () => e.Handled = true);
        }

        private void Forge_Loaded()
        {
            this.Service.ForgeLoaded();
        }

        private void Forge_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.ForgeSelected(sender, e);
        }

        private void Forge_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.ForgeClear();
        }

        public global::System.String LoadNeoForgeGetError()
        {
            return this.Service.LoadNeoForgeGetError();
        }

        private void CardNeoForge_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.NeoForge, () => e.Handled = true);
        }

        private void NeoForge_Loaded()
        {
            this.Service.NeoForgeLoaded();
        }

        private void NeoForge_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.NeoForgeSelected(sender, e);
        }

        private void NeoForge_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.NeoForgeClear();
        }

        public global::System.String LoadFabricGetError()
        {
            return this.Service.LoadFabricGetError();
        }

        private void CardFabric_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.Fabric, () => e.Handled = true);
        }

        private void Fabric_Loaded()
        {
            this.Service.FabricLoaded();
        }

        public void Fabric_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.FabricSelected(sender, e);
        }

        private void Fabric_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.FabricClear();
        }

        public static global::System.Boolean IsFabricApiCompatible(global::PCL.ResourceVersion FabricApi, global::System.String MinecraftVersion)
        {
            return global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData>.IsFabricApiCompatible(FabricApi, MinecraftVersion);
        }

        public global::System.String LoadFabricApiGetError()
        {
            return this.Service.LoadFabricApiGetError();
        }

        private void CardFabricApi_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.FabricApi, () => e.Handled = true);
        }

        private void FabricApi_Loaded()
        {
            this.Service.FabricApiLoaded();
        }

        private void FabricApi_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.FabricApiSelected(sender, e);
        }

        private void FabricApi_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.FabricApiClear();
        }

        public static global::System.Boolean IsOptiFabricCompatible(global::PCL.ResourceVersion ModFile, global::System.String MinecraftVersion)
        {
            return global::PCLCS.DownloadInstallPageController<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModAnimation.AniData>.IsOptiFabricCompatible(ModFile, MinecraftVersion);
        }

        public global::System.String LoadOptiFabricGetError()
        {
            return this.Service.LoadOptiFabricGetError();
        }

        private void CardOptiFabric_PreviewSwap(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Service.PreviewSwap(global::PCLCS.DownloadInstallComponent.OptiFabric, () => e.Handled = true);
        }

        private void OptiFabric_Loaded()
        {
            this.Service.OptiFabricLoaded();
        }

        private void OptiFabric_Selected(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.OptiFabricSelected(sender, e);
        }

        private void OptiFabric_Clear(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.OptiFabricClear();
        }

        public global::PCLCS.McComponents GetSelectedComponents()
        {
            return this.Service.GetSelectedComponents();
        }

        private void TextSelectName_KeyDown(global::System.Object sender, global::System.Windows.Input.KeyEventArgs e)
        {
            this.Service.NameKeyDown(e.Key);
        }

        private void BtnStart_Click()
        {
            this.Service.StartClick();
        }

        public virtual void StartInstall(global::PCL.ModDownloadLib.McInstallRequest Request)
        {
            this.Service.StartInstall(Request);
        }

        private global::System.String GetLoaderError(global::PCL.MyLoading Loader)
        {
            return this.Service.GetLoaderError(global::PCL.PageDownloadInstall.NativeLoading(Loader));
        }

        private static global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData> CreateAnimations()
        {
            return new global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData>()
            {
                Disabled = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                SetDisabled = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled = Value),
                Opacity = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaOpacity(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadInstall.ReadEase(Ease))),
                TranslateX = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateX(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadInstall.ReadEase(Ease))),
                TranslateY = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateY(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadInstall.ReadEase(Ease))),
                Scale = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease, After) => global::PCL.ModAnimation.AaScaleTransform(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadInstall.ReadEase(Ease), After)),
                Color = new Func<global::System.Windows.FrameworkElement, global::System.Windows.DependencyProperty, global::System.String, global::System.Int32, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Property, Color, Duration, Delay, Ease) => global::PCL.ModAnimation.AaColor(Target, Property, Color, Duration, Delay, global::PCL.PageDownloadInstall.ReadEase(Ease))),
                Code = new Func<global::System.Action, global::System.Int32, global::System.Boolean, global::PCL.ModAnimation.AniData>((Work, Delay, After) => global::PCL.ModAnimation.AaCode(() => Work(), Delay, After)),
                Start = new Action<global::System.Collections.Generic.IEnumerable<global::PCL.ModAnimation.AniData>, global::System.String, global::System.Boolean>((Plan, Name, Refresh) => global::PCL.ModAnimation.AniStart(Plan.ToList(), Name, Refresh)),
                Stop = global::PCL.ModAnimation.AniStop
            };
        }

        private static global::PCL.ModAnimation.AniEase ReadEase(global::PCLCS.DownloadPageEase Value)
        {
            switch (Value)
            {
                case global::PCLCS.DownloadPageEase.OutFluent:
                {
                    return new global::PCL.ModAnimation.AniEaseOutFluent();
                }

                case global::PCLCS.DownloadPageEase.OutFluentExtraStrong:
                {
                    return new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.ExtraStrong);
                }

                default:
                {
                    return (global::PCL.ModAnimation.AniEase)null;
                }
            }
        }

        private sealed class SelectionAdapter : global::PCLCS.DownloadInstallSelection<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion>
        {
            private readonly global::PCL.PageDownloadInstall Owner;
            public SelectionAdapter(global::PCL.PageDownloadInstall Owner)
            {
                this.Owner = Owner;
            }

            public override bool IsInSelectPage
            {
                get
                {
                    return this.Owner.IsInSelectPage;
                }

                set
                {
                    this.Owner.IsInSelectPage = value;
                }
            }

            public override string VanillaName
            {
                get
                {
                    return this.Owner.VanillaName;
                }

                set
                {
                    this.Owner.VanillaName = value;
                }
            }

            public override global::Newtonsoft.Json.Linq.JObject VanillaData
            {
                get
                {
                    return this.Owner.VanillaData;
                }

                set
                {
                    this.Owner.VanillaData = value;
                }
            }

            public override string VanillaIcon
            {
                get
                {
                    return this.Owner.VanillaIcon;
                }

                set
                {
                    this.Owner.VanillaIcon = value;
                }
            }

            public override global::PCL.ModDownload.DlOptiFineListEntry SelectedOptiFine
            {
                get
                {
                    return this.Owner.SelectedOptiFine;
                }

                set
                {
                    this.Owner.SelectedOptiFine = value;
                }
            }

            public override global::PCL.ModDownload.DlLiteLoaderListEntry SelectedLiteLoader
            {
                get
                {
                    return this.Owner.SelectedLiteLoader;
                }

                set
                {
                    this.Owner.SelectedLiteLoader = value;
                }
            }

            public override global::PCL.ModDownload.DlForgeVersionEntry SelectedForge
            {
                get
                {
                    return this.Owner.SelectedForge;
                }

                set
                {
                    this.Owner.SelectedForge = value;
                }
            }

            public override global::PCL.ModDownload.DlNeoForgeListEntry SelectedNeoForge
            {
                get
                {
                    return this.Owner.SelectedNeoForge;
                }

                set
                {
                    this.Owner.SelectedNeoForge = value;
                }
            }

            public override string SelectedFabric
            {
                get
                {
                    return this.Owner.SelectedFabric;
                }

                set
                {
                    this.Owner.SelectedFabric = value;
                }
            }

            public override global::PCL.ResourceVersion SelectedFabricApi
            {
                get
                {
                    return this.Owner.SelectedFabricApi;
                }

                set
                {
                    this.Owner.SelectedFabricApi = value;
                }
            }

            public override global::PCL.ResourceVersion SelectedOptiFabric
            {
                get
                {
                    return this.Owner.SelectedOptiFabric;
                }

                set
                {
                    this.Owner.SelectedOptiFabric = value;
                }
            }

            public override bool IsSelectNameEdited
            {
                get
                {
                    return this.Owner.IsSelectNameEdited;
                }

                set
                {
                    this.Owner.IsSelectNameEdited = value;
                }
            }

            public override global::Newtonsoft.Json.Linq.JArray MinecraftVersions
            {
                get
                {
                    return this.Owner.MinecraftVersions;
                }

                set
                {
                    this.Owner.MinecraftVersions = value;
                }
            }

            public override string VersionWaitingSelect
            {
                get
                {
                    return global::PCL.PageDownloadInstall.VersionWaitingSelect;
                }

                set
                {
                    global::PCL.PageDownloadInstall.VersionWaitingSelect = value;
                }
            }

            public override bool AutoSelectedFabricApi
            {
                get
                {
                    return this.Owner.AutoSelectedFabricApi;
                }

                set
                {
                    this.Owner.AutoSelectedFabricApi = value;
                }
            }

            public override bool AutoSelectedOptiFabric
            {
                get
                {
                    return this.Owner.AutoSelectedOptiFabric;
                }

                set
                {
                    this.Owner.AutoSelectedOptiFabric = value;
                }
            }
        }
    }
}
