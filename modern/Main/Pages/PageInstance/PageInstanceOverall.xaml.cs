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
using OverallController = global::PCLCS.InstanceOverallPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>>;
using OverallHost = global::PCLCS.InstanceOverallPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceOverall
    {
        private global::System.Boolean IsLoad;
        public global::PCL.MyListItem InstanceHeaderItem;
        private global::System.String OriginalHeaderInfo;
        private readonly global::PCLCS.InstanceOverallPageView<global::PCL.MyListItem> OverallView;
        private global::PCLCS.InstanceOverallPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>> PageController;
        private global::PCLCS.InstanceOverallPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>> Controller
        {
            get
            {
                this.OverallView.DisplayType = this.ComboDisplayType;
                this.OverallView.DisplayLogo = this.ComboDisplayLogo;
                this.OverallView.CustomLogo = this.ItemDisplayLogoCustom;
                this.OverallView.Delete = this.BtnManageDelete;
                this.OverallView.Star = this.BtnDisplayStar;
                this.OverallView.FolderMods = this.BtnFolderMods;
                this.OverallView.Installed = this.PanInstalledComponents;
                this.OverallView.HeaderPanel = this.PanDisplayItem;
                this.OverallView.ScrollHome = new Action(() => this.PanBack.ScrollToHome());
                this.OverallView.ResizeDisplay = new Action(() => this.PanDisplay.TriggerForceResize());
                this.OverallView.SetStarText = new Action<global::System.String>(Text => this.BtnDisplayStar.Text = Text);
                this.OverallView.Header = new Func<global::PCL.MyListItem>(() => this.InstanceHeaderItem);
                this.OverallView.SetHeader = new Action<global::PCL.MyListItem>(Value => this.InstanceHeaderItem = Value);
                this.OverallView.HeaderInfo = new Func<global::PCL.MyListItem, global::System.String>(Item => Item.Info);
                this.OverallView.SetHeaderInfo = new Action<global::PCL.MyListItem, global::System.String>((Item, Text) => Item.Info = Text);
                this.OverallView.IsLoaded = new Func<global::System.Boolean>(() => this.IsLoad);
                this.OverallView.SetLoaded = new Action<global::System.Boolean>(Value => this.IsLoad = Value);
                this.OverallView.OriginalHeaderInfo = new Func<global::System.String>(() => this.OriginalHeaderInfo);
                this.OverallView.SetOriginalHeaderInfo = new Action<global::System.String>(Value => this.OriginalHeaderInfo = Value);
                if (this.PageController is null)
                    this.PageController = new global::PCLCS.InstanceOverallPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>>(this.OverallView, this.CreateHost(), Value => global::PCL.ModAnimation.AniControlEnabled += Value, () => global::PCL.ModAnimation.AniControlEnabled);
                return this.PageController;
            }
        }

        private global::PCLCS.InstanceOverallPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>> CreateHost()
        {
            return new global::PCLCS.InstanceOverallPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.MyListItem, global::PCL.ModLoader.LoaderCombo<global::System.String>>()
            {
                Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SetInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
                Shift = new Func<global::System.Boolean>(() => global::PCL.My.MyWpfExtension.Computer.Keyboard.ShiftKeyDown),
                LegacyLogin = new Func<global::System.Boolean>(() => (global::PCL.Settings.Get<global::PCL.ModLaunch.McLoginType>("LoginType") == global::PCL.ModLaunch.McLoginType.Legacy)),
                HideHintSeen = new Func<global::System.Boolean>(() => global::PCL.Settings.Get<bool>("HintHide")),
                MarkHideHint = new Action(() => global::PCL.Settings.Set("HintHide", true)),
                ForceListRefresh = new Action(() => global::PCL.ModMinecraft.McInstanceListForceRefresh = true),
                Locked = global::PCL.ModMinecraft.McInstanceLock.IsLocked,
                IgnoreFileCheck = Target => Microsoft.VisualBasic.CompilerServices.Conversions.ToBoolean(global::PCL.ModMinecraft.ShouldIgnoreFileCheck(Target)),
                CreateHeader = new Func<global::PCL.ModMinecraft.McInstance, global::PCL.MyListItem>(Target => Target.ToListItem().Init()),
                RefreshInstalled = this.RefreshInstalledComponents,
                RefreshModDisabled = new Action(() => global::PCL.ModMain.FrmInstanceLeft.RefreshModDisabled()),
                RefreshPageName = new Action(() => global::PCL.ModMain.FrmMain.PageNameRefresh()),
                RefreshInstanceList = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\")),
                LoadInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Name => new global::PCL.ModMinecraft.McInstance(Name).Load()),
                Rename = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::PCL.ModMinecraft.McInstance>((Target, Name) => Target.Rename(Name)),
                BeginManagedRename = Target => MmcLifecycleUi.PromptRename(Target, renamed => {
                    if (!ReferenceEquals(PageInstanceLeft.Instance, Target)) return;
                    PageInstanceLeft.Instance = renamed; Controller.Reload();
                }),
                BeginManagedRepair = MmcRepairUi.Prompt,
                ReadIni = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((Path, Key, DefaultValue) => global::PCL.ModBase.ReadIni(Path, Key, DefaultValue)),
                WriteIni = new Action<global::System.String, global::System.String, global::System.String>((Path, Key, Value) => global::PCL.ModBase.WriteIni(Path, Key, Value)),
                ReadLogo = Path => global::PCL.MinecraftLogoCompatibility.ReadVersionLogo(Path),
                StorageLogo = global::PCL.MinecraftLogoCompatibility.ToStorageUri,
                WriteLogo = global::PCL.MinecraftLogoCompatibility.WriteVersionLogo,
                SelectImage = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFile("选择图片", false, filter: new[] { (new[] { "png", "jpeg", "jpg", "gif", "webp" }, "常用图片文件") }).FirstOrDefault()),
                InputDescription = new Func<global::System.String, global::System.String, global::System.String>((Title, OldValue) => global::PCL.ModMain.MyMsgBoxInput(Title, "修改版本的描述文本，留空则使用 PCL 的默认描述。", OldValue, new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>(), "默认描述")),
                InputName = new Func<global::System.String, global::System.String, global::System.String>((Name, Folder) => global::PCL.ModMain.MyMsgBoxInput("重命名版本", "", Name, new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { new global::PCL.ValidateFolderName(Folder, IgnoreList: new global::System.Collections.Generic.List<global::System.String>() { Name }) })),
                SaveScript = new Func<global::System.String, global::System.String, global::System.String>((Title, FileName) => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, FileName, filter: new[] { ("bat", "批处理文件") })),
                Confirm = new Func<global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, Warn) => global::PCL.ModMain.MyMsgBox(Text, Title, Button2: "取消", IsWarn: Warn)),
                LaunchScript = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Boolean>((Target, Path) => global::PCL.ModLaunch.McLaunchStart(new global::PCL.ModLaunch.McLaunchOptions() { SaveBatch = Path, Instance = Target })),
                TaskNames = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModLoader.LoaderTaskbar.Select(Loader => Loader.Name)),
                CreateFixLoader = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::PCL.ModLoader.LoaderCombo<global::System.String>>((Target, Name) => new global::PCL.ModLoader.LoaderCombo<global::System.String>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)global::PCL.ModDownload.DlClientFix(Target, true, false))),
                HookState = new Action<global::PCL.ModLoader.LoaderCombo<global::System.String>, global::System.Action>((Loader, Work) => Loader.OnStateChanged = (_) => Work()),
                LoaderState = new Func<global::PCL.ModLoader.LoaderCombo<global::System.String>, global::System.Int32>(Loader => (global::System.Int32)Loader.State),
                Finished = (global::System.Int32)global::PCL.ModBase.LoadState.Finished,
                Failed = (global::System.Int32)global::PCL.ModBase.LoadState.Failed,
                Canceled = (global::System.Int32)global::PCL.ModBase.LoadState.Canceled,
                LoaderName = new Func<global::PCL.ModLoader.LoaderCombo<global::System.String>, global::System.String>(Loader => Loader.Name),
                LoaderError = new Func<global::PCL.ModLoader.LoaderCombo<global::System.String>, global::System.String>(Loader => Loader.Error.GetDisplay(false)),
                StartLoader = new Action<global::PCL.ModLoader.LoaderCombo<global::System.String>, global::System.String>((Loader, Input) => Loader.Start(Input)),
                TaskbarAdd = global::PCL.ModLoader.LoaderTaskbarAdd,
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                PageBack = new Action(() => global::PCL.ModMain.FrmMain.PageBack()),
                Hint = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text)),
                HintRed = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red)),
                HintGreen = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green)),
                HintBlue = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Blue)),
                Explorer = new Action<global::System.String>(Path => global::PCL.ModBase.OpenExplorer(Path)),
                Error = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text)),
                ErrorAlert = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text, global::MeloongCore.LogBehavior.Alert)),
                Warn = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Warn(Err, Text)),
                DeleteConfirmed = PageSelectRight.DeleteInstanceInBackground,
                BeginManagedDelete = MmcDeleteUi.PromptDelete,
                IsCurrentPage = () => ReferenceEquals(ModMain.FrmMain?.PageRight, this)
            };
        }

        private void PageSetupLaunch_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Controller.Loaded();
            RefreshMmcActions();
        }

        internal void RefreshModpackVisibility()
        {
            this.Controller.RefreshModpackVisibility();
            RefreshMmcActions();
        }

        private void Reload()
        {
            this.Controller.Reload();
            RefreshMmcActions();
        }

        private void ComboDisplayType_SelectionChanged(global::System.Object sender, global::System.Windows.Controls.SelectionChangedEventArgs e)
        {
            this.Controller.DisplayTypeChanged();
        }

        private void BtnDisplayDesc_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Description();
        }

        private void BtnDisplayRename_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Rename();
        }

        private void ComboDisplayLogo_SelectionChanged()
        {
            this.Controller.LogoChanged();
        }

        private void BtnDisplayStar_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Star();
        }

        private void BtnFolderVersion_Click()
        {
            global::PCL.PageInstanceOverall.OpenInstanceFolder(global::PCL.PageInstanceLeft.Instance);
        }

        public static void OpenInstanceFolder(global::PCL.ModMinecraft.McInstance Instance)
        {
            global::PCL.ModBase.OpenExplorer(Instance.PathVersion);
        }

        private void BtnFolderSaves_Click()
        {
            this.Controller.OpenGameFolder("saves");
        }

        private void BtnFolderMods_Click()
        {
            this.Controller.OpenGameFolder("mods");
        }

        private void BtnFolderScreen_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.OpenGameFolder("screenshots");
        }

        private void BtnManageScript_Click()
        {
            this.Controller.Script();
        }

        private void BtnManageCheck_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Repair();
        }

        private void BtnManageDelete_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Delete();
        }
    }
}
