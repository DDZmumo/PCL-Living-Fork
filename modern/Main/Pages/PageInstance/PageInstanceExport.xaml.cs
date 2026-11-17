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
using global::System.Windows.Documents;
using System.Windows.Documents;
using System.Windows.Input;
using global::System.Windows.Media;
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
using ExportController = global::PCLCS.ModpackExportPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.MyCheckBox, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.ModLoader.LoaderCombo<global::System.String>>;
using ExportPipeline = global::PCLCS.ModpackExportPipeline<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion>;
using ExportRequest = global::PCLCS.ModpackExportRequest<global::PCL.ModMinecraft.McInstance>;
using ThrottleDebounce;

namespace PCL
{
    public class ExportOption : global::PCLCS.ModpackExportOptionData
    {
    }

    public partial class PageInstanceExport : global::PCL.IRefreshable
    {
        private global::System.String CurrentInstance;
        private global::System.String[] SubOptionBlackList;
        private global::System.Collections.Generic.List<global::System.String> _RulesOverrides;
        private global::System.Collections.Generic.List<global::System.String> ExtraFiles;
        private global::System.String ConfigPackPath;
        private const global::System.String Sperator = "==============================================================";
        private global::PCLCS.ModpackExportPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.MyCheckBox, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.ModLoader.LoaderCombo<global::System.String>> PageController;
        private readonly global::PCLCS.ModpackExportPipeline<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion> Pipeline;
        private global::PCLCS.ModpackExportPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.MyCheckBox, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.ModLoader.LoaderCombo<global::System.String>> Controller
        {
            get
            {
                if (this.IsMmcExport) throw new global::System.InvalidOperationException("MMC 导出必须使用专用后端。");
                if (this.PageController is null)
                    this.PageController = new global::PCLCS.ModpackExportPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.MyCheckBox, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.ModLoader.LoaderCombo<global::System.String>>(this.CreateView(), this.CreateState(), this.CreateHost());
                return this.PageController;
            }
        }

        public PageInstanceExport() : this(global::PCL.PageInstanceLeft.Instance, false, global::System.Guid.Empty)
        {
        }

        internal PageInstanceExport(global::PCL.ModMinecraft.McInstance owner, global::System.Guid expectedInstanceId = default)
            : this(owner, true, expectedInstanceId)
        {
        }

        private PageInstanceExport(global::PCL.ModMinecraft.McInstance owner, global::System.Boolean managed, global::System.Guid expectedInstanceId)
        {
            this.CaptureExportOwner(owner, managed, expectedInstanceId);
            __LfAttachOwnHandles();
            this.CurrentInstance = "";
            this.SubOptionBlackList = new[]
            {
                "Quark Programmer Art.zip",
                "+ EuphoriaPatches_",
                "PCL2 Skin.zip"
            };
            this._RulesOverrides = (global::System.Collections.Generic.List<global::System.String>)null;
            this.ExtraFiles = (global::System.Collections.Generic.List<global::System.String>)null;
            this.ConfigPackPath = (global::System.String)null;
            if (!this.IsMmcExport)
                this.Pipeline = new global::PCLCS.ModpackExportPipeline<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion>(global::PCL.PageInstanceExport.CreatePipelineHost());
            this.InitializeComponent();
            if (this.IsMmcExport) this.InitializeMmcExport();
        }

        private global::PCLCS.ModpackExportPageView<global::PCL.MyCheckBox> CreateView()
        {
            return new global::PCLCS.ModpackExportPageView<global::PCL.MyCheckBox>()
            {
                Name = TextExportName,
                Version = TextExportVersion,
                NameHint = new Func<global::System.String>(() => this.TextExportName.HintText),
                SetNameHint = new Action<global::System.String>(Value => this.TextExportName.HintText = Value),
                SetVersionHint = new Action<global::System.String>(Value => this.TextExportVersion.HintText = Value),
                Basic = CheckOptionsBasic,
                Java = CheckOptionsJava,
                Launcher = CheckOptionsPcl,
                Custom = CheckOptionsPclCustom,
                ModpackMode = CheckOptionsModpackMode,
                Isolation = CheckAdvancedVersionIsolation,
                IncludeAssets = CheckAdvancedInclude,
                Modrinth = CheckAdvancedModrinth,
                ShaderSettings = CheckOptionsShaderPackSettings,
                Options = PanOptions,
                ResourcePacks = PanOptionsResourcePacks,
                Saves = PanOptionsSaves,
                Shaders = PanOptionsShaderPacks,
                OverrideCancel = BtnOverrideCancel,
                CardOptions = CardOptions,
                HintOptiFine = HintOptiFine,
                HintJava = HintJava,
                HintModrinth = HintModrinthFormat,
                Export = BtnExport,
                CardInlines = new Func<global::System.Windows.Documents.InlineCollection>(() => this.CardOptions.Inlines),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                Checked = new Func<global::PCL.MyCheckBox, global::System.Boolean>(Check => Check.Checked),
                SetChecked = new Action<global::PCL.MyCheckBox, global::System.Boolean>((Check, Value) => Check.Checked = Value),
                Inlines = new Func<global::PCL.MyCheckBox, global::System.Windows.Documents.InlineCollection>(Check => Check.Inlines),
                CreateCheck = new Func<global::PCLCS.ModpackExportOptionData, global::PCL.MyCheckBox>(Data => new global::PCL.MyCheckBox() { Tag = Data }),
                Gray = new Func<global::System.Windows.Media.Brush>(() => ((global::System.Windows.Media.Brush)(global::PCL.ModSecret.ColorGray5))),
                SetJavaHint = new Action<global::System.String, global::System.Boolean>((Text, Red) =>
                {
                    this.HintJava.Theme = (Red) ? (global::PCL.MyHint.Themes.Red) : (global::PCL.MyHint.Themes.Yellow);
                    this.HintJava.Text = Text;
                })
            };
        }

        private global::PCLCS.ModpackExportPageState CreateState()
        {
            return new global::PCLCS.ModpackExportPageState()
            {
                CurrentInstance = new Func<global::System.String>(() => this.CurrentInstance),
                SetCurrentInstance = new Action<global::System.String>(Value => this.CurrentInstance = Value),
                BlackList = new Func<global::System.String[]>(() => this.SubOptionBlackList),
                Rules = new Func<global::System.Collections.Generic.List<global::System.String>>(() => this._RulesOverrides),
                SetRules = new Action<global::System.Collections.Generic.List<global::System.String>>(Value => this._RulesOverrides = Value),
                Extra = new Func<global::System.Collections.Generic.List<global::System.String>>(() => this.ExtraFiles),
                SetExtra = new Action<global::System.Collections.Generic.List<global::System.String>>(Value => this.ExtraFiles = Value),
                PackPath = new Func<global::System.String>(() => this.ConfigPackPath),
                SetPackPath = new Action<global::System.String>(Value => this.ConfigPackPath = Value)
            };
        }

        private global::PCLCS.ModpackExportPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.ModLoader.LoaderCombo<global::System.String>> CreateHost()
        {
            return new global::PCLCS.ModpackExportPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ExportOption, global::PCL.ModLoader.LoaderCombo<global::System.String>>()
            {
                Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => this.ExportOwner ?? global::PCL.PageInstanceLeft.Instance),
                Name = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name),
                VersionFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                GameRoot = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathIndie),
                VersionDescription = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.VersionDisplayName()),
                Version = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.McVersion>(Instance => Instance.Version),
                Modable = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Modable),
                JavaType = new Func<global::PCL.ModMinecraft.McInstance, global::System.Int32>(Instance => global::PCL.Settings.Get<int>("VersionArgumentJavaV2", Instance)),
                JavaWorker = new Func<global::MeloongCore.RedoableWorker<global::PCLCS.Java>>(() => global::PCL.PageInstanceLeft.CurrentJavaWorker),
                Ui = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(() => Work())),
                AdjustAnimation = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled += Value),
                Animation = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                ReleaseBuild = new Func<global::System.Boolean>(() => (global::PCL.ModBase.BuildType == global::PCL.ModBase.BuildTypes.Release)),
                ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                SetHelpEvent = new Action<global::System.String>(Value => global::PCL.CustomEventService.SetEventData(this.BtnAdvancedHelp, Value)),
                CreateOption = new Func<global::PCL.ExportOption>(() => new global::PCL.ExportOption()),
                TaskNames = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModLoader.LoaderTaskbar.Select(Loader => Loader.Name)),
                ShowTasks = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.TaskManager)),
                TempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
                ReadConfigPath = new Func<global::System.String>(() => global::PCL.Settings.Get<string>("CacheExportConfig")),
                WriteConfigPath = new Action<global::System.String>(Value => global::PCL.Settings.Set("CacheExportConfig", Value)),
                SaveFile = new Func<global::System.String, global::System.String, global::System.String, global::System.Collections.Generic.List<(global::System.String Extension, global::System.String Display)>, global::System.String>((Title, Name, Initial, Filters) => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name, Initial, Filters)),
                SelectConfig = new Func<global::System.String, global::System.String, global::System.String>((Title, Initial) => global::MeloongCore.Wpf.Dialogs.SelectFile(Title, false, Initial, new[] { (new[] { "txt" }, "整合包导出配置") }).FirstOrDefault()),
                Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Int32>((Caption, Title, Button1, Button2) => global::PCL.ModMain.MyMsgBox(Caption, Title, Button1, Button2)),
                ErrorDisplay = new Func<global::System.Exception, global::System.String>(ErrorValue => ErrorValue.GetDisplay(true)),
                Info = new Action<global::System.String>(Message => global::MeloongCore.Logger.Info(Message)),
                HintGreen = new Action<global::System.String>(Message => global::PCL.ModMain.Hint(Message, global::PCL.ModMain.HintType.Green)),
                Explorer = new Action<global::System.String>(FileName => global::PCL.ModBase.OpenExplorer(FileName)),
                Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message)),
                ErrorAlert = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Alert)),
                ErrorToast = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Toast)),
                CreateCombo = this.CreateExportLoader,
                StartCombo = new Action<global::PCL.ModLoader.LoaderCombo<global::System.String>>(Loader => Loader.Start()),
                AddTaskbar = new Action<global::PCL.ModLoader.LoaderCombo<global::System.String>>(Loader => global::PCL.ModLoader.LoaderTaskbarAdd(Loader)),
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                QuoteLeft = new Func<global::System.Char>(() => global::PCL.ModBase.vbLQ),
                QuoteRight = new Func<global::System.Char>(() => global::PCL.ModBase.vbRQ)
            };
        }

        private static global::PCLCS.ModpackExportHost<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion> CreatePipelineHost()
        {
            return new global::PCLCS.ModpackExportHost<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion>()
            {
                VersionFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                Info = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Info),
                Version = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.McVersion>(Instance => Instance.Version),
                SelectedMinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                Executable = new Func<global::System.String>(() => global::PCL.ModBase.PathExe),
                ExecutableName = new Func<global::System.String>(() => global::PCL.ModBase.LauncherExecutableName),
                BaseFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.Base),
                CreateLocal = new Func<global::System.String, global::PCL.LocalResourceFile>(FileName => new global::PCL.LocalResourceFile(FileName)),
                SelectJava = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider, global::PCLCS.Java>((Instance, Prompt, Token, Progress) => global::PCL.ModJava.SelectOrDownloadJava(Instance, Prompt, Token, Progress)),
                WriteBoolIni = new Action<global::System.String, global::System.String, global::System.Boolean>((FileName, Key, Value) => global::PCL.ModBase.WriteIni(FileName, Key, Conversions.ToString(Value))),
                ConfigureIsolation = new Action<global::System.String, global::System.Boolean, global::System.Collections.Generic.List<global::System.String>>((Folder, Enabled, Dirs) => global::PCL.ExportedVersionIsolation.ConfigureExport(Folder, Enabled, Dirs)),
                ConfigureMode = new Action<global::System.String, global::System.Boolean, global::System.Boolean>((Folder, Include, Enabled) => global::PCL.ModModpackMode.ConfigureExport(Folder, Include, Enabled)),
                Request = new Func<global::System.String, global::System.Net.Http.HttpMethod, global::System.String, global::System.String, global::Newtonsoft.Json.Linq.JToken>((Url, Method, Body, ContentType) => global::PCL.ModDownload.DlModRequest(Url, Method, Body, ContentType)),
                StartThread = new Func<global::System.Action, global::System.String, global::System.Threading.Thread>((Work, Name) => global::PCL.ModBase.RunInNewThread(() => Work(), Name)),
                Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Int32>((Caption, Title, Button1, Button2) => global::PCL.ModMain.MyMsgBox(Caption, Title, Button1, Button2)),
                InfoLog = new Action<global::System.String>(Message => global::MeloongCore.Logger.Info(Message)),
                HintRed = new Action<global::System.String>(Message => global::PCL.ModMain.Hint(Message, global::PCL.ModMain.HintType.Red)),
                Explorer = new Action<global::System.String>(FileName => global::PCL.ModBase.OpenExplorer(FileName)),
                Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message))
            };
        }

        // 三个原类型 Loader，只把实际 Input/Output/取消/进度面适配到 C#。
        private global::PCL.ModLoader.LoaderCombo<global::System.String> CreateExportLoader(global::PCLCS.ModpackExportRequest<global::PCL.ModMinecraft.McInstance> Request)
        {
            var Copy = new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Collections.Generic.List<global::PCL.LocalResourceFile>>(Request.CopyStepName, Loader => this.Pipeline.Copy(Request, new global::PCLCS.ModpackExportCopyTask<global::PCL.LocalResourceFile>() { Output = new Func<global::System.Collections.Generic.List<global::PCL.LocalResourceFile>>(() => Loader.Output), SetOutput = new Action<global::System.Collections.Generic.List<global::PCL.LocalResourceFile>>(Value => Loader.Output = Value), Progress = new Func<global::System.Double>(() => Loader.Progress), SetProgress = new Action<global::System.Double>(Value => Loader.Progress = Value), Cancellation = new Func<global::System.Threading.CancellationToken>(() => Loader.CreateCancellationToken()), ProgressProvider = new Func<global::System.Double, global::System.Double, global::MeloongCore.ProgressProvider>((Start, Finish) => Loader.CreateSyncProgressProvider(Start, Finish)) }))
            {
                ProgressWeight = Request.CopyWeight
            };
            var Online = new global::PCL.ModLoader.LoaderTask<global::System.Collections.Generic.List<global::PCL.LocalResourceFile>, global::System.Collections.Generic.Dictionary<global::PCL.LocalResourceFile, global::System.Collections.Generic.List<global::System.String>>>(Request.OnlineStepName, Loader => this.Pipeline.Online(Request, new global::PCLCS.ModpackExportOnlineTask<global::PCL.LocalResourceFile>() { Input = new Func<global::System.Collections.Generic.List<global::PCL.LocalResourceFile>>(() => Loader.Input), Output = new Func<global::System.Collections.Generic.Dictionary<global::PCL.LocalResourceFile, global::System.Collections.Generic.List<global::System.String>>>(() => Loader.Output), SetOutput = new Action<global::System.Collections.Generic.Dictionary<global::PCL.LocalResourceFile, global::System.Collections.Generic.List<global::System.String>>>(Value => Loader.Output = Value), IsCanceled = new Func<global::System.Boolean>(() => Loader.IsCanceled), Progress = new Func<global::System.Double>(() => Loader.Progress), SetProgress = new Action<global::System.Double>(Value => Loader.Progress = Value) }))
            {
                Show = Request.ShowOnline,
                ProgressWeight = Request.OnlineWeight
            };
            var Archive = new global::PCL.ModLoader.LoaderTask<global::System.Collections.Generic.Dictionary<global::PCL.LocalResourceFile, global::System.Collections.Generic.List<global::System.String>>, global::System.Int32>(Request.ArchiveStepName, Loader => this.Pipeline.Archive(Request, new global::PCLCS.ModpackExportArchiveTask<global::PCL.LocalResourceFile>() { Input = new Func<global::System.Collections.Generic.Dictionary<global::PCL.LocalResourceFile, global::System.Collections.Generic.List<global::System.String>>>(() => Loader.Input), SetProgress = new Action<global::System.Double>(Value => Loader.Progress = Value) }))
            {
                ProgressWeight = Request.ArchiveWeight
            };
            return new global::PCL.ModLoader.LoaderCombo<global::System.String>(Request.LoaderName, new global::PCL.ModLoader.LoaderBase[] { Copy, Online, Archive })
            {
                OnStateChanged = global::PCL.ModDownloadLib.LoaderStateChangedHintOnly
            };
        }

        private void PageInstanceExport_Initialized(global::System.Object sender, global::System.EventArgs e)
        {
            if (!this.IsMmcExport) this.Controller.Initialized();
        }

        private void PageInstanceExport_Loaded()
        {
            if (this.IsMmcExport) this.RefreshMmcExport();
            else if (this.CanUseOrdinaryExport()) this.Controller.Loaded();
        }

        public void RefreshAll()
        {
            if (this.IsMmcExport) this.RefreshMmcExport();
            else if (this.CanUseOrdinaryExport()) this.Controller.RefreshAll();
        }

        void global::PCL.IRefreshable.Refresh() => this.RefreshAll();
        private void ReloadAllSubOptions()
        {
            this.Controller.ReloadAllSubOptions();
        }

        private void ReloadSubOptions(global::System.Windows.Controls.StackPanel Panel, global::System.Boolean AcceptCompressedFile, global::System.Boolean AcceptFolder, params global::System.String[] Folders)
        {
            this.Controller.ReloadSubOptions(Panel, AcceptCompressedFile, AcceptFolder, Folders);
        }

        private void RefreshAllOptionsUI()
        {
            this.Controller.RefreshOptions();
        }

        private global::System.Collections.Generic.IEnumerable<global::System.String> StandardizeLines(global::System.Collections.Generic.IEnumerable<global::System.String> Raw, global::System.Boolean AddSuffixStarToFolderPath)
        {
            return global::PCLCS.ModpackExportPipeline<global::PCL.ModMinecraft.McInstance, global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion>.StandardizeLines(Raw, AddSuffixStarToFolderPath);
        }

        private global::System.Collections.Generic.IEnumerable<global::PCL.MyCheckBox> GetAllOptions(global::System.Boolean IncludeHidden)
        {
            return this.Controller.AllOptions(IncludeHidden);
        }

        private global::PCL.ExportOption GetExportOption(global::PCL.MyCheckBox CheckBox)
        {
            return this.Controller.GetOption(CheckBox);
        }

        private global::System.Collections.Generic.List<global::System.String> RulesOverrides
        {
            get
            {
                return this.Controller.RulesOverrides;
            }

            set
            {
                this.Controller.RulesOverrides = value;
            }
        }

        private global::System.Collections.Generic.IEnumerable<global::System.String> GetAllRules()
        {
            return this.Controller.Rules();
        }

        private global::System.Collections.Generic.IEnumerable<global::System.String> GetExtraFileLines()
        {
            return this.Controller.ExtraFiles();
        }

        private void ResetConfigOverrides()
        {
            this.Controller.ResetConfigOverrides();
        }

        private void CardOptions_MouseLeftButtonDown()
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.OptionsMouseDown();
        }

        private void ExportConfig()
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.ExportConfig();
        }

        private void ImportConfig()
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.ImportConfig();
        }

        private void StartExport()
        {
            if (this.IsMmcExport) this.StartMmcExport();
            else if (this.CanUseOrdinaryExport()) this.Controller.StartExport();
        }

        private void RefreshJavaInfo()
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.RefreshJavaInfo();
        }

        private void TextExportName_GotFocus()
        {
            if (this.IsMmcExport)
            {
                if (this.TextExportName.Text == "") this.TextExportName.Text = this.TextExportName.HintText;
            }
            else if (this.CanUseOrdinaryExport()) this.Controller.NameGotFocus();
        }

        private void CheckAdvancedInclude_Change(global::System.Object sender, global::System.Boolean user)
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.IncludeChanged();
        }

        private void CheckAdvancedModrinth_Change(global::System.Object sender, global::System.Boolean user)
        {
            if (!this.IsMmcExport && this.CanUseOrdinaryExport()) this.Controller.ModrinthChanged();
        }
    }
}
