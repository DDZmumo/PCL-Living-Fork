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
using InstallKind = global::PCLCS.DownloadInstallComponent;
using ModifyCore = global::PCLCS.DownloadModifyPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading, global::PCL.ModAnimation.AniData>;
using ThrottleDebounce;

namespace PCL
{
    // LF's original XAML, virtual dispatch, loaders and fields. All workflows and comparison decisions are C#.
    public partial class PageDownloadModify : global::PCL.PageDownloadInstall
    {
        private global::PCL.ModLoader.LoaderTask<global::PCL.ModMinecraft.McInstance, global::System.Object> InitLoader;
        private global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>> ForgeVersionLoader;
        private global::PCLCS.McComponents InitialTarget;
        private global::System.Boolean IsVanillaVersionReliable;
        private global::System.String OriginalName;
        private global::System.String CustomLogo;
        private readonly global::System.Collections.Generic.Dictionary<global::PCL.MyLoading, global::System.String> PendingComponents;
        private global::System.Boolean IsRestoringComponents;
        internal global::System.Windows.Controls.WrapPanel PackUpdateButtons;
        internal global::System.Windows.Controls.TextBlock PackUpdateName;
        internal global::System.Windows.Controls.TextBlock PackUpdateInfo;
        private global::PCLCS.DownloadModifyPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading, global::PCL.ModAnimation.AniData> ModifyController;
        private global::PCLCS.DownloadModifyPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading, global::PCL.ModAnimation.AniData> ModifyService
        {
            get
            {
                if (this.ModifyController is null)
                    this.ModifyController = new global::PCLCS.DownloadModifyPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading, global::PCL.ModAnimation.AniData>(this.CreateModifyHost(), this.CreateModifyAnimations());
                return this.ModifyController;
            }
        }

        public PageDownloadModify()
        {
            __LfAttachOwnHandles();
            this.PendingComponents = new global::System.Collections.Generic.Dictionary<global::PCL.MyLoading, global::System.String>();
        }

        private global::PCLCS.DownloadModifyPageHost<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModMinecraft.McInstance, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading> CreateModifyHost()
        {
            return new global::PCLCS.DownloadModifyPageHost<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModMinecraft.McInstance, global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading>()
            {
                Selection = new global::PCL.PageDownloadModify.SelectionAdapter(this),
                State = new global::PCL.PageDownloadModify.StateAdapter(this),
                View = this.CreateModifyView,
                Comparison = this.CreateComparison,
                ComparisonCard = new Func<global::System.Windows.Controls.Grid, global::PCLCS.DownloadModifyComparison>(Panel => global::PCL.PageDownloadModify.NativeCard((global::PCL.MyCard)Panel.Parent)),
                PackButtons = new Func<global::System.Windows.Controls.WrapPanel>(() => this.PackUpdateButtons),
                PackName = new Func<global::System.Windows.Controls.TextBlock>(() => this.PackUpdateName),
                PackInfo = new Func<global::System.Windows.Controls.TextBlock>(() => this.PackUpdateInfo),
                SetPackButtons = new Action<global::System.Windows.Controls.WrapPanel>(Value => this.PackUpdateButtons = Value),
                SetPackName = new Action<global::System.Windows.Controls.TextBlock>(Value => this.PackUpdateName = Value),
                SetPackInfo = new Action<global::System.Windows.Controls.TextBlock>(Value => this.PackUpdateInfo = Value),
                Card = new Func<global::System.String, global::System.Windows.Thickness, global::System.Windows.Controls.Panel>((Title, Margin) => new global::PCL.MyCard() { Title = Title, Margin = Margin }),
                UpdateButton = global::PCL.PageDownloadModify.CreateUpdateButton,
                StartPackUpdate = new Action<global::System.Boolean>(Online => global::PCL.ModpackUpdateFlow.Start(global::PCL.PageInstanceLeft.Instance, Online)),
                OpenPackBackups = new Action(() => global::PCL.ModpackUpdateFlow.OpenBackups(global::PCL.PageInstanceLeft.Instance)),
                BaseControlInit = this.ControlInit,
                InitializeLoader = this.InitializeNativeLoader,
                Restart = new Action(() => this.PageLoaderRestart()),
                Cancel = new Action(() => this.InitLoader.Cancel()),
                BaseClearSelected = this.ClearSelected,
                BaseReloadSelected = this.InvokeBaseReload,
                BaseShowMinecraftList = this.InvokeBaseShowMinecraft,
                ChangeMinecraftInPlace = this.ChangeNativeMinecraftInPlace,
                BasePrepareSelection = this.PrepareSelectPage,
                BaseReloadLists = this.ReloadComponentLists,
                BaseLogo = this.InvokeBaseLogo,
                SelectName = this.GetSelectName,
                Components = this.GetSelectedComponents,
                SelectMinecraft = new Action<global::Newtonsoft.Json.Linq.JObject>(Version => this.MinecraftSelected(global::PCL.ModDownloadLib.McDownloadListItem(Version, (global::PCL.MyListItem.ClickEventHandler)null, false).Init(), (global::System.Windows.Input.MouseButtonEventArgs)null)),
                MinecraftRow = new Func<global::Newtonsoft.Json.Linq.JObject, global::System.Windows.FrameworkElement>(Version => global::PCL.ModDownloadLib.McDownloadListItem(Version, (global::PCL.MyListItem.ClickEventHandler)null, false).Init()),
                RowTitle = new Func<global::System.Windows.FrameworkElement, global::System.String>(Row => ((global::PCL.MyListItem)Row).Title),
                RowLogo = new Func<global::System.Windows.FrameworkElement, global::System.String>(Row => ((global::PCL.MyListItem)Row).Logo),
                InitInput = new Func<global::PCL.ModMinecraft.McInstance>(() => this.InitLoader.Input),
                UiWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(() => Work())),
                DetectPack = new Func<global::System.String, global::Newtonsoft.Json.Linq.JObject>(Path => global::PCL.ModpackUpdate.Detect(Path)),
                NewInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Path => new global::PCL.ModMinecraft.McInstance(Path)),
                CatalogLoaders = new Func<global::System.Collections.Generic.Dictionary<global::System.String, global::PCL.ModLoader.LoaderBase>>(() => new global::System.Collections.Generic.Dictionary<global::System.String, global::PCL.ModLoader.LoaderBase>() { { "Minecraft", global::PCL.ModDownload.DlClientListLoader }, { "OptiFine", global::PCL.ModDownload.DlOptiFineListLoader }, { "LiteLoader", global::PCL.ModDownload.DlLiteLoaderListLoader }, { "NeoForge", global::PCL.ModDownload.DlNeoForgeListLoader }, { "Fabric", global::PCL.ModDownload.DlFabricListLoader }, { "Fabric API", global::PCL.ModDownload.DlFabricApiLoader }, { "OptiFabric", global::PCL.ModDownload.DlOptiFabricLoader } }),
                CreateForgeLoader = new Func<global::System.String, global::PCL.ModLoader.LoaderBase>(Version => new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>("DlForgeVersion " + Version, global::PCL.ModDownload.DlForgeVersionMain)),
                States = new global::PCLCS.MinecraftInstallStates()
                {
                    Waiting = (global::System.Int32)global::PCL.ModBase.LoadState.Waiting,
                    Loading = (global::System.Int32)global::PCL.ModBase.LoadState.Loading,
                    Finished = (global::System.Int32)global::PCL.ModBase.LoadState.Finished,
                    Failed = (global::System.Int32)global::PCL.ModBase.LoadState.Failed,
                    Canceled = (global::System.Int32)global::PCL.ModBase.LoadState.Canceled
                },
                StartLoader = new Action<global::PCL.ModLoader.LoaderBase, global::System.Object, global::System.Boolean>((Loader, Input, Force) => Loader.Start(Input, Force)),
                LoaderState = new Func<global::PCL.ModLoader.LoaderBase, global::System.Int32>(Loader => (global::System.Int32)Loader.State),
                LoaderError = new Func<global::PCL.ModLoader.LoaderBase, global::System.Exception>(Loader => Loader.Error),
                LoaderProgress = new Func<global::PCL.ModLoader.LoaderBase, global::System.Double>(Loader => Loader.Progress),
                ForgeInput = new Func<global::PCL.ModLoader.LoaderBase, global::System.String>(Loader => ((global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>)Loader).Input),
                ForgeOutput = new Func<global::PCL.ModLoader.LoaderBase, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>(Loader => ((global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>)Loader).Output),
                BindForge = new Action<global::PCL.ModLoader.LoaderBase>(Loader => this.LoadForge.State = Loader),
                SwapRestoreParent = new Action<global::System.Windows.FrameworkElement, global::System.Boolean>((Button, Swapped) => ((global::PCL.MyCard)Button.Parent).IsSwapped = Swapped),
                LoadingControl = this.ReadLoading,
                IsLoader = new Func<global::PCL.MyLoading, global::System.Boolean>(Loading => Loading.State.IsLoader),
                ReadLoader = new Func<global::PCL.MyLoading, global::PCL.ModLoader.LoaderBase>(Loading => (global::PCL.ModLoader.LoaderBase)Loading.State),
                ComponentError = this.ReadComponentError,
                ClientOutput = new Func<global::Newtonsoft.Json.Linq.JObject>(() => global::PCL.ModDownload.DlClientListLoader.Output.Value),
                FabricOutput = new Func<global::Newtonsoft.Json.Linq.JObject>(() => global::PCL.ModDownload.DlFabricListLoader.Output.Value),
                OptiFineHashes = this.ReadOptiFineHashes,
                OptiFineOutput = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlOptiFineListEntry>>(() => global::PCL.ModDownload.DlOptiFineListLoader.Output.Value),
                NeoForgeOutput = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlNeoForgeListEntry>>(() => global::PCL.ModDownload.DlNeoForgeListLoader.Output.Value),
                LiteLoaderOutput = new Func<global::System.Collections.Generic.List<global::PCL.ModDownload.DlLiteLoaderListEntry>>(() => global::PCL.ModDownload.DlLiteLoaderListLoader.Output.Value),
                FabricApiOutput = new Func<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>(() => global::PCL.ModDownload.DlFabricApiLoader.Output),
                OptiFabricOutput = new Func<global::System.Collections.Generic.List<global::PCL.ResourceVersion>>(() => global::PCL.ModDownload.DlOptiFabricLoader.Output),
                OptiFineMirrorFinished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlOptiFineListBmclapiLoader.State == global::PCL.ModBase.LoadState.Finished)),
                WaitOptiFineMirror = new Action(() => global::PCL.ModDownload.DlOptiFineListBmclapiLoader.WaitForExit()),
                CopyOptiFineMirror = new Action(() => global::PCL.ModDownload.DlOptiFineListLoader.Output = global::PCL.ModDownload.DlOptiFineListBmclapiLoader.Output),
                LocalSha1 = new Func<global::System.IO.FileInfo, global::System.String>(File => new global::PCL.LocalResourceFile(File).ModrinthHash),
                ChooseMod = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String, global::System.Int32?>((Names, Title) => global::PCL.ModMain.MyMsgBoxSelect(Names.Select(Name => ((global::PCL.IMyRadio)(new global::PCL.MyRadioBox() { Text = Name }))), Title)),
                ReadIniBoolean = new Func<global::System.String, global::System.String, global::System.Boolean>((File, Key) => Conversions.ToBoolean(global::PCL.ModBase.ReadIni(File, Key, Conversions.ToString(false)))),
                ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                CanChangeMinecraft = global::PCL.ModModpackMode.CanChangeMinecraft,
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                Gray4 = new Func<global::System.Windows.Media.Brush>(() => global::PCL.ModSecret.ColorGray4),
                Hint = global::PCL.PageDownloadModify.NativeHint,
                Message = new Func<global::PCLCS.SelectionPageMessage, global::System.Int32>(Request => global::PCL.ModMain.MyMsgBox(Request.Text, Request.Title, Request.Button1, Request.Button2, Request.Button3, Request.IsWarn)),
                Modify = new Func<global::PCL.ModDownloadLib.McInstallRequest, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Request, Instance) => global::PCL.ModDownloadLib.McModificationStart(Request, Instance)),
                InstancePage = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SetInstancePage = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                SetSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft.McInstanceSelected = Value),
                ReloadJava = global::PCL.PageInstanceLeft.ReloadCurrentJava,
                RefreshInstances = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\")),
                PageBack = new Action(() => global::PCL.ModMain.FrmMain.PageBack())
            };
        }

        private global::PCLCS.DownloadModifyPageView CreateModifyView()
        {
            return new global::PCLCS.DownloadModifyPageView()
            {
                Select = PanSelect,
                SelectNameCard = CardSelectName,
                AllBack = PanAllBack,
                Back = PanBack,
                Minecraft = PanMinecraft,
                BackButton = BtnBack,
                Logo = ImgLogo,
                ChangeMinecraft = PanMinecraftChange,
                Start = BtnStart,
                NameRestore = BtnOriginalNameRestore,
                Name = TextSelectName,
                NameLabel = LabSelectName,
                MinecraftVersion = LabMinecraftVersion,
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                MinecraftOldLogo = new Action<global::System.String>(Value => this.ImgMinecraftOld.Source = Value),
                MinecraftNewLogo = new Action<global::System.String>(Value => this.ImgMinecraftNew.Source = Value),
                NameValidated = new Func<global::System.Boolean>(() => this.TextSelectName.IsValidated),
                ValidateName = new Action<global::System.String>(Ignore => this.TextSelectName.ValidateRules = new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { new global::PCL.ValidateFolderName(global::PCL.ModMinecraft.McFolderSelected + "versions", IgnoreList: new global::System.Collections.Generic.List<global::System.String>() { Ignore }) }),
                StartText = new Action<global::System.String>(Value => this.BtnStart.Text = Value),
                SetStartLogo = new Action(() => this.BtnStart.Logo = global::PCL.ModMain.FrmInstanceLeft.ItemModify.Logo)
            };
        }

        private global::PCLCS.DownloadModifyComparison CreateComparison(global::System.Int32 Kind)
        {
            switch (Kind)
            {
                case -(1):
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardMinecraft, this.PanMinecraftCompare, this.PanMinecraftInfo, this.BtnMinecraftRestore, this.LabMinecraftVersion, this.LabMinecraftOld);
                }

                case 0:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardOptiFine, this.PanOptiFineCompare, this.PanOptiFineInfo, this.BtnOptiFineRestore, this.LabOptiFine, this.LabOptiFineOld, this.BtnOptiFineClear);
                }

                case 1:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardLiteLoader, this.PanLiteLoaderCompare, this.PanLiteLoaderInfo, this.BtnLiteLoaderRestore, this.LabLiteLoader, this.LabLiteLoaderOld, this.BtnLiteLoaderClear);
                }

                case 2:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardForge, this.PanForgeCompare, this.PanForgeInfo, this.BtnForgeRestore, this.LabForge, this.LabForgeOld, this.BtnForgeClear);
                }

                case 3:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardNeoForge, this.PanNeoForgeCompare, this.PanNeoForgeInfo, this.BtnNeoForgeRestore, this.LabNeoForge, this.LabNeoForgeOld, this.BtnNeoForgeClear);
                }

                case 4:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardFabric, this.PanFabricCompare, this.PanFabricInfo, this.BtnFabricRestore, this.LabFabric, this.LabFabricOld, this.BtnFabricClear);
                }

                case 5:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardFabricApi, this.PanFabricApiCompare, this.PanFabricApiInfo, this.BtnFabricApiRestore, this.LabFabricApi, this.LabFabricApiOld, this.BtnFabricApiClear);
                }

                case 6:
                {
                    return global::PCL.PageDownloadModify.NativeComparison(this.CardOptiFabric, this.PanOptiFabricCompare, this.PanOptiFabricInfo, this.BtnOptiFabricRestore, this.LabOptiFabric, this.LabOptiFabricOld, this.BtnOptiFabricClear);
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }

        private static global::PCLCS.DownloadModifyComparison NativeCard(global::PCL.MyCard Card)
        {
            return new global::PCLCS.DownloadModifyComparison()
            {
                Card = Card,
                MainSwap = new Func<global::System.Windows.FrameworkElement>(() => Card.MainSwap),
                SetSwapped = new Action<global::System.Boolean>(Value => Card.IsSwapped = Value)
            };
        }

        private static global::PCLCS.DownloadModifyComparison NativeComparison(global::PCL.MyCard Card, global::System.Windows.Controls.Grid Compare, global::System.Windows.FrameworkElement Current, global::PCL.MyIconButton Restore, global::System.Windows.Controls.TextBlock Label, global::System.Windows.Controls.TextBlock OldLabel, global::System.Windows.FrameworkElement Clear = null)
        {
            var View = global::PCL.PageDownloadModify.NativeCard(Card);
            View.Compare = Compare;
            View.Current = Current;
            View.Restore = Restore;
            View.Label = Label;
            View.OldLabel = OldLabel;
            View.Clear = Clear;
            return View;
        }

        private static global::System.Windows.FrameworkElement CreateUpdateButton(global::System.String Text, global::System.Action Click)
        {
            var Button = new global::PCL.MyButton()
            {
                Text = Text,
                MinWidth = 140d,
                Height = 35d,
                Margin = new global::System.Windows.Thickness(0d, 13d, 20d, 0d),
                Padding = new global::System.Windows.Thickness(13d, 0d, 13d, 0d)
            };
            Button.Click += (_, __) => Click();
            return Button;
        }

        private void InitializeNativeLoader()
        {
            this.InitLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModMinecraft.McInstance, global::System.Object>("初始化版本修改", this.LoadInstance);
            this.PageLoaderInit(this.LoadMinecraft, this.PanLoad, this.PanAllBack, (global::System.Windows.FrameworkElement)null, this.InitLoader, this.LoadInstanceFinished, () => global::PCL.PageInstanceLeft.Instance);
        }

        private static global::PCLCS.DownloadModifyTask<global::PCL.ModMinecraft.McInstance> WrapTask(global::PCL.ModLoader.LoaderTask<global::PCL.ModMinecraft.McInstance, global::System.Object> Task)
        {
            return new global::PCLCS.DownloadModifyTask<global::PCL.ModMinecraft.McInstance>()
            {
                Input = new Func<global::PCL.ModMinecraft.McInstance>(() => Task.Input),
                Output = new Func<global::System.Object>(() => Task.Output),
                SetOutput = new Action<global::System.Object>(Value => Task.Output = Value),
                Canceled = new Func<global::System.Boolean>(() => Task.IsCanceled),
                ForceRestart = new Func<global::System.Boolean>(() => Task.IsForceRestarting),
                Progress = new Action<global::System.Double>(Value => Task.Progress = Value)
            };
        }

        private global::PCL.MyLoading ReadLoading(global::PCLCS.DownloadInstallComponent Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.DownloadInstallComponent.OptiFine:
                {
                    return this.LoadOptiFine;
                }

                case global::PCLCS.DownloadInstallComponent.LiteLoader:
                {
                    return this.LoadLiteLoader;
                }

                case global::PCLCS.DownloadInstallComponent.Forge:
                {
                    return this.LoadForge;
                }

                case global::PCLCS.DownloadInstallComponent.NeoForge:
                {
                    return this.LoadNeoForge;
                }

                case global::PCLCS.DownloadInstallComponent.Fabric:
                {
                    return this.LoadFabric;
                }

                case global::PCLCS.DownloadInstallComponent.FabricApi:
                {
                    return this.LoadFabricApi;
                }

                case global::PCLCS.DownloadInstallComponent.OptiFabric:
                {
                    return this.LoadOptiFabric;
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }

        private global::System.String ReadComponentError(global::PCLCS.DownloadInstallComponent Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.DownloadInstallComponent.OptiFine:
                {
                    return this.LoadOptiFineGetError();
                }

                case global::PCLCS.DownloadInstallComponent.LiteLoader:
                {
                    return this.LoadLiteLoaderGetError();
                }

                case global::PCLCS.DownloadInstallComponent.Forge:
                {
                    return this.LoadForgeGetError();
                }

                case global::PCLCS.DownloadInstallComponent.NeoForge:
                {
                    return this.LoadNeoForgeGetError();
                }

                case global::PCLCS.DownloadInstallComponent.Fabric:
                {
                    return this.LoadFabricGetError();
                }

                case global::PCLCS.DownloadInstallComponent.FabricApi:
                {
                    return this.LoadFabricApiGetError();
                }

                case global::PCLCS.DownloadInstallComponent.OptiFabric:
                {
                    return this.LoadOptiFabricGetError();
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }

        private global::Newtonsoft.Json.Linq.JObject _ReadOptiFineHashes_Hashes = (global::Newtonsoft.Json.Linq.JObject)global::MeloongCore.FileUtils.ReadAsJson(@"Minecraft\OptiFineHashes.json", @type: typeof(global::PCLCS.Resource));
        private global::Newtonsoft.Json.Linq.JObject ReadOptiFineHashes()
        {
            return _ReadOptiFineHashes_Hashes;
        }

        private void InvokeBaseReload()
        {
            base.ReloadSelected();
        }

        private void InvokeBaseShowMinecraft()
        {
            base.ShowMinecraftVersionList();
        }

        private global::System.String InvokeBaseLogo()
        {
            return base.GetSelectLogo();
        }

        private static void NativeHint(global::System.String Text, global::PCLCS.SelectionPageHint Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.SelectionPageHint.Red:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red);
                    break;
                }

                case global::PCLCS.SelectionPageHint.Blue:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Blue);
                    break;
                }

                default:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green);
                    break;
                }
            }
        }

        public override void LoaderInit() // 父级 Handles Me.Initialized
        {
            this.ModifyService.LoaderInit();
        }

        public override void Init() // 父级 Handles Me.Loaded
        {
            this.ModifyService.Init();
        }

        private void PageUnloaded()
        {
            this.ModifyService.Unloaded();
        }

        private void LoadInstance(global::PCL.ModLoader.LoaderTask<global::PCL.ModMinecraft.McInstance, global::System.Object> Task)
        {
            this.ModifyService.LoadInstance(global::PCL.PageDownloadModify.WrapTask(Task));
        }

        private (global::System.IO.FileInfo File, global::System.String Sha1, T Version)? SelectMod<T>(global::System.String ComponentName, global::System.Collections.Generic.List<(global::System.IO.FileInfo File, global::System.String Sha1, T Version)> Matches)
        {
            return this.ModifyService.SelectMod(ComponentName, Matches);
        }

        private void LoadInstanceFinished(global::PCL.ModLoader.LoaderTask<global::PCL.ModMinecraft.McInstance, global::System.Object> Task)
        {
            this.ModifyService.LoadInstanceFinished(global::PCL.PageDownloadModify.WrapTask(Task));
        }

        public override global::System.String GetSelectLogo()
        {
            return this.ModifyService.GetSelectLogo();
        }

        internal bool ChooseMinecraftInPlace(JObject version) => ModifyService.ChooseMinecraftInPlace(version);

        public override void ShowMinecraftVersionList()
        {
            this.ModifyService.ShowMinecraftVersionList();
        }

        public override void ReloadSelected()
        {
            this.ModifyService.ReloadSelected();
        }

        internal void RefreshModpackRestrictions()
        {
            this.ModifyService.RefreshModpackRestrictions();
        }

        private void RefreshComparisons()
        {
            this.ModifyService.RefreshComparisons();
        }

        private void ShowComparison(global::System.Windows.Controls.Grid ComparePanel, global::System.Windows.FrameworkElement CurrentPanel, global::System.String OldVersion, global::System.String NewVersion, global::System.String NewText, global::PCL.MyIconButton RestoreButton, global::System.Boolean CanRestore, global::System.Windows.FrameworkElement ClearButton = null)
        {
            this.ModifyService.ShowComparison(ComparePanel, CurrentPanel, OldVersion, NewVersion, NewText, RestoreButton, CanRestore, ClearButton);
        }

        private void RestoreComponent(global::System.Object sender, global::System.EventArgs e)
        {
            this.ModifyService.RestoreComponent((global::PCL.MyIconButton)sender);
        }

        private void CardMinecraft_MouseLeftButtonUp(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.ModifyService.MinecraftClick(() => e.Handled = true);
        }

        private void UpdateStartButton()
        {
            this.ModifyService.UpdateStartButton();
        }

        private void UpdateOriginalNameVisibility()
        {
            this.ModifyService.UpdateOriginalNameVisibility();
        }

        private void BtnOriginalNameRestore_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.ModifyService.RestoreOriginalName();
        }

        public override void StartInstall(global::PCL.ModDownloadLib.McInstallRequest Request)
        {
            this.ModifyService.StartInstall(Request);
        }

        internal void ShowPackInfo(global::Newtonsoft.Json.Linq.JObject Data)
        {
            this.ModifyService.ShowPackInfo(Data);
        }

        private global::PCLCS.DownloadModifyAnimation<global::PCL.ModAnimation.AniData> CreateModifyAnimations()
        {
            return new global::PCLCS.DownloadModifyAnimation<global::PCL.ModAnimation.AniData>()
            {
                Native = global::PCL.PageDownloadModify.CreateAnimations(),
                Double = new Func<global::System.Action<global::System.Double>, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Setter, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaDouble(Value => Setter(Conversions.ToDouble(Value)), Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadModify.ReadEase(Ease)))
            };
        }

        private static global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData> CreateAnimations()
        {
            return new global::PCLCS.DownloadPageAnimationHost<global::PCL.ModAnimation.AniData>()
            {
                Disabled = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                SetDisabled = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled = Value),
                Opacity = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaOpacity(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadModify.ReadEase(Ease))),
                TranslateX = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateX(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadModify.ReadEase(Ease))),
                TranslateY = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => global::PCL.ModAnimation.AaTranslateY(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadModify.ReadEase(Ease))),
                Scale = new Func<global::System.Windows.FrameworkElement, global::System.Double, global::System.Double, global::System.Int32, global::PCLCS.DownloadPageEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease, After) => global::PCL.ModAnimation.AaScaleTransform(Target, Delta, (global::System.Int32)Math.Round(Duration), Delay, global::PCL.PageDownloadModify.ReadEase(Ease), After)),
                Color = new Func<global::System.Windows.FrameworkElement, global::System.Windows.DependencyProperty, global::System.String, global::System.Int32, global::System.Int32, global::PCLCS.DownloadPageEase, global::PCL.ModAnimation.AniData>((Target, Property, Color, Duration, Delay, Ease) => global::PCL.ModAnimation.AaColor(Target, Property, Color, Duration, Delay, global::PCL.PageDownloadModify.ReadEase(Ease))),
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

        private sealed class StateAdapter : global::PCLCS.DownloadModifyPageState<global::PCL.ModLoader.LoaderBase, global::PCL.MyLoading>
        {
            private readonly global::PCL.PageDownloadModify Owner;
            public StateAdapter(global::PCL.PageDownloadModify Owner)
            {
                this.Owner = Owner;
            }

            public override global::PCL.ModLoader.LoaderBase ForgeVersionLoader
            {
                get
                {
                    return this.Owner.ForgeVersionLoader;
                }

                set
                {
                    this.Owner.ForgeVersionLoader = (global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>)value;
                }
            }

            public override global::PCLCS.McComponents InitialTarget
            {
                get
                {
                    return this.Owner.InitialTarget;
                }

                set
                {
                    this.Owner.InitialTarget = value;
                }
            }

            public override bool IsVanillaVersionReliable
            {
                get
                {
                    return this.Owner.IsVanillaVersionReliable;
                }

                set
                {
                    this.Owner.IsVanillaVersionReliable = value;
                }
            }

            public override string OriginalName
            {
                get
                {
                    return this.Owner.OriginalName;
                }

                set
                {
                    this.Owner.OriginalName = value;
                }
            }

            public override string CustomLogo
            {
                get
                {
                    return this.Owner.CustomLogo;
                }

                set
                {
                    this.Owner.CustomLogo = value;
                }
            }

            public override bool IsRestoringComponents
            {
                get
                {
                    return this.Owner.IsRestoringComponents;
                }

                set
                {
                    this.Owner.IsRestoringComponents = value;
                }
            }

            public override global::System.Collections.Generic.Dictionary<global::PCL.MyLoading, global::System.String> PendingComponents
            {
                get
                {
                    return this.Owner.PendingComponents;
                }
            }
        }

        private sealed class SelectionAdapter : global::PCLCS.DownloadInstallSelection<global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion>
        {
            private readonly global::PCL.PageDownloadModify Owner;
            public SelectionAdapter(global::PCL.PageDownloadModify Owner)
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
