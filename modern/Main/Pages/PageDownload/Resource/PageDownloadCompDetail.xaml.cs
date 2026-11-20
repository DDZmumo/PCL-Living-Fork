using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using global::System.Collections.ObjectModel;
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
using DetailCore = global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageDownloadResourceDetail
    {
        private static readonly ResourceDescriptionTranslation DescriptionTranslations = new();
        private CancellationTokenSource descriptionCancellation;
        private async void TranslateDescriptionClick(object sender, MouseButtonEventArgs args)
        {
            var project = Service.Project;
            if (project is null || string.IsNullOrWhiteSpace(project.Description)) { ModMain.Hint("此项目没有可翻译的简介。",ModMain.HintType.Blue);return; }
            descriptionCancellation?.Cancel();
            var cancellation = descriptionCancellation = new CancellationTokenSource();
            BtnIntroTranslate.IsEnabled=false;
            try {
                var result=await Task.Run(()=>DescriptionTranslations.Get(project.Platform,project.Id,project.Description,cancellation.Token));
                if(cancellation.IsCancellationRequested || !IsLoaded || !ReferenceEquals(project,Service.Project))return;
                if(result is null){ModMain.Hint("此项目暂时没有可用的中文简介。",ModMain.HintType.Blue);return;}
                string text="原文：\n"+project.Description+"\n\n中文译文（MCIM）：\n"+result.Translated;
                if(!result.IsCurrent)text+="\n\n译文基于较早的简介，请以当前原文为准。\n对应原文：\n"+result.Original;
                ModMain.MyMsgBox(text,"资源简介翻译");
            } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
            catch(Exception error){if(!cancellation.IsCancellationRequested&&IsLoaded&&ReferenceEquals(project,Service.Project)){Logger.Warn(error,"翻译资源简介失败");ModMain.Hint("简介翻译暂不可用，请稍后重试。",ModMain.HintType.Red);}}
            finally{if(ReferenceEquals(descriptionCancellation,cancellation)){BtnIntroTranslate.IsEnabled=true;descriptionCancellation=null;}cancellation.Dispose();}
        }
        public static global::System.Collections.Generic.Dictionary<global::PCLCS.ResourceTypes, global::System.String> CachedFolder = new global::System.Collections.Generic.Dictionary<global::PCLCS.ResourceTypes, global::System.String>();
        internal static string ChooseModDownloadFolder(ModMinecraft.McInstance Target, ModMinecraft.McInstance Selected, string Cached, Func<ModMinecraft.McInstance,bool> Suitable) => DetailCore.ChooseModDownloadFolder(Target,Selected,Cached,Suitable);
        private ModMinecraft.McInstance DownloadTargetInstance { get => Service.DownloadTargetInstance; set => Service.DownloadTargetInstance = value; }
        private static readonly global::PCL.PageDownloadResourceDetail.ResourceDetailLoaders NativeLoaders = new global::PCL.PageDownloadResourceDetail.ResourceDetailLoaders();
        private global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Collections.Generic.List<global::PCL.ResourceVersion>> ResourceFileLoader;
        private global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData> Controller;
        private global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData> Service
        {
            get
            {
                if (this.Controller is null)
                    this.Controller = new global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData>(this.CreateHost(), global::PCL.PageDownloadResourceDetail.CreateDownloadHost(), global::PCL.PageDownloadResourceDetail.CreateAnimations());
                return this.Controller;
            }
        }

        public PageDownloadResourceDetail()
        {
            __LfAttachOwnHandles();
            ResourceFileLoader = new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Collections.Generic.List<global::PCL.ResourceVersion>>("Resource File", Task => this.Service.LoadFiles(() => Task.IsCanceled, Value => Task.Output = Value));
            this.InitializeComponent();
            Unloaded += (_,_) => descriptionCancellation?.Cancel();
        }

        private global::PCLCS.ResourceDetailPageHost<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem> CreateHost()
        {
            return new global::PCLCS.ResourceDetailPageHost<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem>()
            {
                View = new Func<global::PCLCS.ResourceDetailPageView>(() => new global::PCLCS.ResourceDetailPageView() { Filter = PanFilter, Results = PanResults, Intro = PanIntro, FilterCard = CardFilter, Alternative = HintAlternative, Wiki = BtnIntroWiki, AlternativeText = new Action<global::System.String>(Value => this.HintAlternative.Text = Value), WebText = new Action<global::System.String>(Value => this.BtnIntroWeb.Text = Value), ScrollHome = new Action(() => this.PanBack.ScrollToHome()) }),
                Argument = new Func<global::System.Int32, global::System.Object>(Index => ((global::System.Object[])global::PCL.ModMain.FrmMain.PageCurrent.Additional)[Index]),
                Arguments = () => ModMain.FrmMain.PageCurrent.Additional as object[],
                SetArgument = new Action<global::System.Int32, global::System.Object>((Index, Value) => ((global::System.Object[])global::PCL.ModMain.FrmMain.PageCurrent.Additional)[Index] = Value),
                HasArguments = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmMain.PageCurrent.Additional is not null),
                Failed = new Func<global::System.Boolean>(() => (this.ResourceFileLoader.State == global::PCL.ModBase.LoadState.Failed)),
                Error = new Func<global::System.Exception>(() => this.ResourceFileLoader.Error),
                Files = new Func<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>(() => this.ResourceFileLoader.Output),
                ProjectFiles = new Func<global::System.String, global::PCLCS.ResourcePlatforms, global::System.Collections.Generic.List<global::PCL.ResourceVersion>>((Id, Platform) => global::PCL.ResourceVersion.FromProjectId(Id, Platform)),
                Initialize = new Action(() => this.PageLoaderInit(this.Load, this.PanLoad, this.PanMain, this.CardIntro, this.ResourceFileLoader, (_) => this.Load_OnFinish())),
                Restart = new Action<global::System.Boolean>(Force => this.PageLoaderRestart(IsForceRestart: Force)),
                FilterButton = global::PCL.PageDownloadResourceDetail.CreateFilterButton,
                FilterText = new Func<global::System.Windows.FrameworkElement, global::System.String>(Button => ((global::PCL.MyRadioButton)Button).Text),
                FilterChecked = new Action<global::System.Windows.FrameworkElement, global::System.Boolean>((Button, Value) => ((global::PCL.MyRadioButton)Button).Checked = Value),
                SwappedHeight = new Func<global::System.Double>(() => global::PCLLF.Presentation.Controls.MyCardView.SwapedHeight),
                Card = global::PCL.PageDownloadResourceDetail.CreateVersionCard,
                SetBuilder = (Card, Build) => Card.SetContentBuilder(Panel => Build((StackPanel)Panel)),
                CardTitle = new Func<global::PCL.MyCard, global::System.String>(Card => Card.Title),
                BuildCard = new Action<global::PCL.MyCard>(Card => Card.BuildContent()),
                SwapCard = new Action<global::PCL.MyCard, global::System.Boolean>((Card, Value) => Card.IsSwapped = Value),
                HasProject = new Func<global::System.String, global::System.Boolean>(Id => global::PCL.ResourceProject.Cache.ContainsKey(ResourceProjectKey.Get(Service.Project.Platform, Id))),
                CachedProject = new Func<global::System.String, global::PCL.ResourceProject>(Id => global::PCL.ResourceProject.Cache[ResourceProjectKey.Get(Service.Project.Platform, Id)]),
                ProjectRow = new Func<global::PCL.ResourceProject, global::System.Windows.FrameworkElement>(Project => Project.ToResourceItem(false, false)),
                YellowHint = new Func<global::System.String, global::System.Windows.Thickness, global::System.Windows.FrameworkElement>((Text, Margin) => new global::PCL.MyHint() { Text = Text, Theme = global::PCL.MyHint.Themes.Yellow, Margin = Margin }),
                IsVersionRow = new Func<global::System.Windows.FrameworkElement, global::System.Boolean>(Item => Item is global::PCL.MyListItem),
                VersionRow = global::PCL.PageDownloadResourceDetail.CreateVersionRow,
                Header = new Func<global::PCL.ResourceProject, global::PCL.MyResourceItem>(Project => Project.ToResourceItem(true, true).Init()),
                HeaderInteractive = new Action<global::PCL.MyResourceItem, global::System.Boolean>((Item, Value) => Item.CanInteraction = Value),
                HeaderLogo = new Func<global::PCL.MyResourceItem, global::System.String>(Item => Item.PathLogo.ActualSource),
                HeaderName = new Func<global::PCL.MyResourceItem, (global::System.String Title, global::System.String Raw)>(Item => (Item.LabTitle.Text, Item.LabTitleRaw.Text)),
                Website = new Action<global::System.String>(Url => global::PCL.ModBase.OpenWebsite(Url)),
                Clipboard = new Action<global::System.String>(Value => global::PCL.ModBase.ClipboardSet(Value))
            };
        }

        private static global::PCLCS.ResourceDetailDownloadHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ModMinecraft.McInstance> CreateDownloadHost()
        {
            return new global::PCLCS.ResourceDetailDownloadHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ModMinecraft.McInstance>()
            {
                Loaders = NativeLoaders,
                States = new global::PCLCS.MinecraftInstallStates()
                {
                    Waiting = (global::System.Int32)global::PCL.ModBase.LoadState.Waiting,
                    Loading = (global::System.Int32)global::PCL.ModBase.LoadState.Loading,
                    Finished = (global::System.Int32)global::PCL.ModBase.LoadState.Finished,
                    Failed = (global::System.Int32)global::PCL.ModBase.LoadState.Failed,
                    Canceled = (global::System.Int32)global::PCL.ModBase.LoadState.Canceled
                },
                CachedFolders = CachedFolder,
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                Instances = new Func<global::System.Collections.Generic.IEnumerable<global::PCL.ModMinecraft.McInstance>>(() => global::PCL.ModMinecraft.McInstanceList.Values.SelectMany(List => List)),
                InstanceListFinished = new Func<global::System.Boolean>(() => (global::PCL.ModMinecraft.McInstanceListLoader.State == global::PCL.ModBase.LoadState.Finished)),
                LoadInstanceList = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\", WaitForExit: true)),
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                TaskManager = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.TaskManager)),
                RefreshCurrentResources = global::PCL.PageDownloadResourceDetail.RefreshCurrentResources,
                TranslateSetting = new Func<global::System.Int32>(() => global::PCL.Settings.Get<int>("ToolDownloadTranslateV2")),
                NameValidator = global::PCL.PageDownloadResourceDetail.CreateNameValidator,
                NetFile = new Func<global::PCL.ResourceVersion, global::System.String, global::PCLCS.ResourceDownloadReason, global::System.String, global::PCLCS.ModLoaders, global::PCL.ModNet.NetFile>((Version, Target, Reason, GameVersion, Loaders) => Version.ToNetFile(Target, (global::PCL.ResourceVersion.DownloadReason)(((global::System.Int32)Reason)), GameVersion, Loaders)),
                InstallMod = ModDownloadInstaller.Start,
                InstallPack = new Action<global::System.String, global::System.String, global::System.String, global::Newtonsoft.Json.Linq.JObject>((Target, Name, Logo, Source) => global::PCL.ModModpack.ModpackInstall(Target, Name, Logo, Source)),
                MapDownloadFolder = global::PCL.MapDownloadInstaller.GetDownloadFolder,
                InstallMap = global::PCL.MapDownloadInstaller.Install,
                InterruptOnCancel = new Action<global::PCL.ModLoader.LoaderBase, global::System.Boolean>((Loader, Value) => ((global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.String>)Loader).InterruptOnCancel = Value),
                StateHint = global::PCL.ModDownloadLib.LoaderStateChangedHintOnly,
                ClearFailedFolder = new Action<global::PCL.ModLoader.LoaderBase>(Loader => global::PCL.ModDownloadLib.McInstallFailedClearFolder(Loader)),
                Hint = global::PCL.PageDownloadResourceDetail.DownloadHint,
                SaveFile = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.String>((Name, Folder, Extension, Description) => global::MeloongCore.Wpf.Dialogs.SaveFile("选择保存位置", Name, Folder, new[] { (Extension, Description) })),
                Explorer = new Action<global::System.String>(Target => global::PCL.ModBase.OpenExplorer(Target)),
                RunInNewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)),
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work))
            };
        }

        private static void DownloadHint(global::System.String Text, global::System.Int32 Kind)
        {
            global::PCL.ModMain.Hint(Text, ((Kind) == (1)) ? (global::PCL.ModMain.HintType.Red) : ((((Kind) == (2)) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Blue))));
        }

        private static void RefreshCurrentResources()
        {
            if (global::PCL.ModMain.FrmMain?.PageRight is global::PCL.PageInstanceResources)
                ((global::PCL.PageInstanceResources)global::PCL.ModMain.FrmMain.PageRight).RefreshResources();
        }

        private static global::PCLCS.DownloadNameValidator CreateNameValidator(global::System.String Folder)
        {
            var Rule = new global::PCL.ValidateFolderName(Folder);
            return new global::PCLCS.DownloadNameValidator()
            {
                Validate = new Func<global::System.String, global::System.String>(Name => Rule.Validate(Name)),
                Prompt = new Func<global::System.String, global::System.String>(DefaultInput => global::PCL.ModMain.MyMsgBoxInput("输入版本名称", "", DefaultInput, new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { Rule }))
            };
        }

        private static global::System.Windows.FrameworkElement CreateFilterButton(global::System.String Text, global::System.Action<global::System.String> Changed)
        {
            var Button = new global::PCL.MyRadioButton()
            {
                Text = Text,
                Margin = new global::System.Windows.Thickness(2d, 0d, 2d, 0d),
                ColorType = global::PCL.MyRadioButton.ColorState.Highlight
            };
            Button.LabText.Margin = new global::System.Windows.Thickness((global::System.Double)(-(2)), 0d, 10d, 0d);
            Button.Check += (global::System.Object Sender, global::System.Boolean User) => Changed(((global::PCL.MyRadioButton)Sender).Text);
            return Button;
        }

        private static global::PCL.MyCard CreateVersionCard(global::System.String Title, global::System.Windows.Controls.StackPanel Stack)
        {
            var Card = new global::PCL.MyCard()
            {
                Title = Title,
                Margin = new global::System.Windows.Thickness(0d, 0d, 0d, 15d)
            };
            Card.Children.Add(Stack);
            Card.SwapControl = Stack;
            return Card;
        }

        private static global::System.Windows.FrameworkElement CreateVersionRow(global::PCL.ResourceVersion Version, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs> Click, global::System.Action<global::System.Object, global::System.EventArgs> SaveAs, global::System.Boolean BadDisplay)
        {
            global::PCL.MyIconButton.ClickEventHandler Save = (global::PCL.MyIconButton.ClickEventHandler)null;
            if (SaveAs is not null)
                Save = new global::PCL.MyIconButton.ClickEventHandler((Sender, Args) => SaveAs(Sender, Args));
            return Version.ToListItem((Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args), Save, IsBadDisplay: BadDisplay);
        }

        private class CardSorter : global::PCLCS.ResourceFileCardSorter
        {
            public CardSorter(global::System.String Topmost = "") : base(Topmost)
            {
            }
        }

        /// <summary>The project shown on this page, for the glass presentation's read-only info panel.</summary>
        internal global::PCL.ResourceProject GlassProject => this.Controller is null ? null : this.Service.Project;

        private global::PCL.ResourceProject Project
        {
            get
            {
                return this.Service.Project;
            }

            set
            {
                this.Service.Project = value;
            }
        }

        private global::PCLCS.ResourceTypes TargetResourceType
        {
            get
            {
                return this.Service.TargetResourceType;
            }

            set
            {
                this.Service.TargetResourceType = value;
            }
        }

        private string TargetVersion
        {
            get
            {
                return this.Service.TargetVersion;
            }

            set
            {
                this.Service.TargetVersion = value;
            }
        }

        private global::PCLCS.ModLoaders TargetLoaders
        {
            get
            {
                return this.Service.TargetLoaders;
            }

            set
            {
                this.Service.TargetLoaders = value;
            }
        }

        public void LoadPageArguments()
        {
            this.Service.LoadPageArguments();
        }

        private void PageDownloadResourceDetail_Inited(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.Initialized();
        }

        private void Load_State(global::System.Object sender, global::PCL.MyLoading.MyLoadingState state, global::PCL.MyLoading.MyLoadingState oldState)
        {
            this.Service.LoadState();
        }

        private global::System.Collections.Generic.List<global::PCL.ResourceVersion> GetResults()
        {
            return this.Service.GetResults();
        }

        private void Load_OnFinish()
        {
            this.Service.LoadOnFinish();
        }

        private void UpdateFilterResult()
        {
            this.Service.UpdateFilterResult();
        }

        private void InitializeStackResourceVersions(global::System.Windows.Controls.StackPanel Stack)
        {
            this.Service.InitializeStackResourceVersions(Stack);
        }

        private global::System.String GetGroupedVersionName(global::System.String Name, global::System.Boolean GroupedByDrop, global::System.Boolean FoldOld)
        {
            return global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData>.GetGroupedVersionName(Name, GroupedByDrop, FoldOld);
        }

        public void Init()
        {
            this.Service.Init();
        }

        public void Install_Click(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            this.Service.InstallClick(sender, e);
        }

        public void Save_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.SaveClick(sender, e);
        }

        internal static global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.String> CreateMapInstallLoader(global::System.String Target, global::System.String SavesFolder)
        {
            return (global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.String>)global::PCLCS.ResourceDetailPageController<global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyCard, global::PCL.MyResourceItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModAnimation.AniData>.CreateMapInstallLoader(global::PCL.PageDownloadResourceDetail.CreateDownloadHost(), Target, SavesFolder);
        }

        private void BtnIntroWeb_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.IntroWeb();
        }

        private void BtnIntroWiki_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.IntroWiki();
        }

        private void BtnIntroCopy_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.IntroCopy();
        }

        private void HintAlternative_Click()
        {
            this.Service.AlternativeClick();
        }

        private class ResourceDetailLoaders : global::PCLCS.IMinecraftInstallLoaders<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile>
        {
            private static global::PCL.ModLoader.LoaderBase ApplyOptions(global::PCL.ModLoader.LoaderBase Loader, global::PCLCS.MinecraftDownloadLoaderSettings Options)
            {
                if (Options.ProgressWeight.HasValue)
                    Loader.ProgressWeight = Options.ProgressWeight.Value;
                if (Options.Show.HasValue)
                    Loader.Show = Options.Show.Value;
                if (Options.Block.HasValue)
                    Loader.Block = Options.Block.Value;
                return Loader;
            }

            public global::PCL.ModLoader.LoaderBase NewTask<TInput, TOutput>(global::System.String Name, global::System.Action<global::PCLCS.MinecraftInstallTask<TInput, TOutput, global::PCL.ModLoader.LoaderBase>> Work, global::PCLCS.MinecraftDownloadLoaderSettings Options)
            {
                return global::PCL.PageDownloadResourceDetail.ResourceDetailLoaders.ApplyOptions(new global::PCL.ModLoader.LoaderTask<TInput, TOutput>(Name, Task => Work(this.WrapTask<TInput, TOutput>(Task))), Options);
            }

            public global::PCLCS.MinecraftInstallTask<TInput, TOutput, global::PCL.ModLoader.LoaderBase> WrapTask<TInput, TOutput>(global::PCL.ModLoader.LoaderBase Loader)
            {
                global::PCL.ModLoader.LoaderTask<TInput, TOutput> Task = (global::PCL.ModLoader.LoaderTask<TInput, TOutput>)Loader;
                return new global::PCLCS.MinecraftInstallTask<TInput, TOutput, global::PCL.ModLoader.LoaderBase>()
                {
                    Native = Task,
                    ReadInput = new Func<TInput>(() => Task.Input),
                    ReadOutput = new Func<TOutput>(() => Task.Output),
                    WriteOutput = new Action<TOutput>(Value => Task.Output = Value),
                    ReadProgress = new Func<global::System.Double>(() => Task.Progress),
                    WriteProgress = new Action<global::System.Double>(Value => Task.Progress = Value),
                    ReadCanceled = new Func<global::System.Boolean>(() => Task.IsCanceled),
                    ReadCanceledState = new Func<global::System.Boolean>(() => (Task.State == global::PCL.ModBase.LoadState.Canceled)),
                    Cancellation = new Func<global::System.Threading.CancellationToken>(() => Task.CreateCancellationToken()),
                    ProgressProvider = new Func<global::System.Double, global::System.Double, global::MeloongCore.ProgressProvider>((FromPercentage, ToPercentage) => Task.CreateSyncProgressProvider(FromPercentage, ToPercentage)),
                    Cancel = Task.Cancel
                };
            }

            public global::PCL.ModLoader.LoaderBase Combo<TInput>(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> Steps, global::PCLCS.MinecraftDownloadLoaderSettings Options)
            {
                return global::PCL.PageDownloadResourceDetail.ResourceDetailLoaders.ApplyOptions(new global::PCL.ModLoader.LoaderCombo<TInput>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Steps), Options);
            }

            public global::PCL.ModLoader.LoaderBase Download(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::PCLCS.MinecraftDownloadLoaderSettings Options)
            {
                return global::PCL.PageDownloadResourceDetail.ResourceDetailLoaders.ApplyOptions(new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files), Options);
            }

            public void Start<TInput>(global::PCL.ModLoader.LoaderBase Loader, TInput Input)
            {
                ((global::PCL.ModLoader.LoaderCombo<TInput>)Loader).Start(Input);
            }

            public void AddTaskbar<TInput>(global::PCL.ModLoader.LoaderBase Loader)
            {
                global::PCL.ModLoader.LoaderTaskbarAdd((global::PCL.ModLoader.LoaderCombo<TInput>)Loader);
            }

            public void OnStateChanged(global::PCL.ModLoader.LoaderBase Loader, global::System.Action<global::PCL.ModLoader.LoaderBase> Changed)
            {
                Loader.OnStateChanged = Changed;
            }

            public global::System.String Name(global::PCL.ModLoader.LoaderBase Loader)
            {
                return Loader.Name;
            }

            public global::System.Int32 State(global::PCL.ModLoader.LoaderBase Loader)
            {
                return (global::System.Int32)Loader.State;
            }

            public global::System.Exception ErrorValue(global::PCL.ModLoader.LoaderBase Loader)
            {
                return Loader.Error;
            }

            global::System.Exception global::PCLCS.IMinecraftInstallLoaders<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile>.Error(global::PCL.ModLoader.LoaderBase Loader) => this.ErrorValue(Loader);
            public global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase> Children(global::PCL.ModLoader.LoaderBase Loader)
            {
                return (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)((global::PCL.ModLoader.LoaderCombo<global::System.String>)Loader).GetLoaderList();
            }

            public global::System.Collections.Generic.IEnumerable<global::System.String> TaskbarNames()
            {
                return global::PCL.ModLoader.LoaderTaskbar.Select(Loader => Loader.Name);
            }
        }

        private static global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData> CreateAnimations()
        {
            return new global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData>()
            {
                Disabled = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                SetDisabled = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled = Value),
                Opacity = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaOpacity(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadResourceDetail.ReadEase(Ease))),
                TranslateX = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateX(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadResourceDetail.ReadEase(Ease))),
                TranslateY = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateY(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadResourceDetail.ReadEase(Ease))),
                Scale = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease, After) => global::PCL.ModAnimation.AaScaleTransform(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadResourceDetail.ReadEase(Ease), After)),
                Color = new Func<global::System.Windows.FrameworkElement, global::System.Windows.DependencyProperty, global::System.String, global::System.Int32, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Property, Color, Duration, Delay, Ease) => global::PCL.ModAnimation.AaColor(Target, Property, Color, Duration, Delay, global::PCL.PageDownloadResourceDetail.ReadEase(Ease))),
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
    }
}
