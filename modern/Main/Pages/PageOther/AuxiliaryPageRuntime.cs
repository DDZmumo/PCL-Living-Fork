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
using global::PCLCS;
using PCLCS;
using global::PCLLF.Presentation.Controls;
using ThrottleDebounce;

namespace PCL
{

    // Actual LF model/platform types and live original field access only.
    internal sealed class AuxiliaryPageRuntime
    {
        internal static readonly global::PCLCS.LegacyAuxiliaryBasicHost Basic = new global::PCLCS.LegacyAuxiliaryBasicHost()
        {
            Common = global::PCL.SetupPageRuntime.Common,
            LauncherVersion = new Func<global::System.String>(() => global::PCL.ModBase.VersionBaseName),
            CheckUpdate = global::PCL.ModSecret.UpdateCheckByButton
        };
        internal static global::PCLCS.LegacyAuxiliaryNavigationHost NavigationHost(global::System.Func<global::System.Int32> ReadId, global::System.Action<global::System.Int32> WriteId)
        {
            return new global::PCLCS.LegacyAuxiliaryNavigationHost()
            {
                Common = global::PCL.SetupPageRuntime.Common,
                HiddenForceShow = new Func<global::System.Boolean>(() => global::PCL.PageSetupUI.HiddenForceShow),
                ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                LegacyLink = global::PCL.AuxiliaryPageRuntime.LegacyLinkEnabled,
                HiddenRefresh = global::PCL.PageSetupUI.HiddenRefresh,
                ApplyModpackVisibility = () => global::PCL.ModModpackMode.ApplyVisibility(),
                RefreshHelp = global::PCL.AuxiliaryPageRuntime.RefreshHelp,
                NormalizeSetup = new Func<global::System.Int32, global::System.Int32>(ID => (global::System.Int32)global::PCL.ModModpackMode.NormalizeSetupPage((global::PCL.FormMain.PageSubType)(ID))),
                LauncherSettingsPage = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModModpackMode.LauncherSettingsPage()),
                PageId = ReadId,
                SetPageId = WriteId,
                ParseTag = new Func<global::System.Object, global::System.Int32>(Value => (global::System.Int32)Math.Round(global::PCL.ModBase.Val(Value))),
                FormatNumber = new Func<global::System.Int32, global::System.String>(ID => ((global::System.Int32)((global::PCL.FormMain.PageSubType)(ID))).ToString()),
                FormatEnum = new Func<global::System.Int32, global::System.String>(ID => ((global::PCL.FormMain.PageSubType)(ID)).ToString()),
                Target = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmMain.PageRight),
                SetTarget = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Target => global::PCL.ModMain.FrmMain.PageRight = (global::PCL.MyPageRight)Target),
                MainRight = new Func<global::System.Windows.Controls.Border>(() => global::PCL.ModMain.FrmMain.PanMainRight),
                Launch = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmSetupLaunch), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmSetupLaunch = (global::PCL.PageSetupLaunch)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageSetupLaunch()), Reset = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => ((global::PCL.PageSetupLaunch)Page).Reset()) },
                UI = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmSetupUI), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmSetupUI = (global::PCL.PageSetupUI)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageSetupUI()), Reset = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => ((global::PCL.PageSetupUI)Page).Reset()) },
                Link = global::PCL.AuxiliaryPageRuntime.LinkSlot(),
                Update = new LegacyAuxiliaryPageSlot { Read = () => ModMain.FrmSetupUpdate, Write = page => ModMain.FrmSetupUpdate = (PageSetupUpdate)page, Create = () => new PageSetupUpdate() },
                Personal = new LegacyAuxiliaryPageSlot { Read = () => ModMain.FrmSetupPersonal, Write = page => ModMain.FrmSetupPersonal = (PageSetupPersonal)page, Create = () => new PageSetupPersonal() },
                System = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmSetupSystem), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmSetupSystem = (global::PCL.PageSetupSystem)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageSetupSystem()), Reset = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => ((global::PCL.PageSetupSystem)Page).Reset()) },
                Help = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmOtherHelp), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmOtherHelp = (global::PCL.PageOtherHelp)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageOtherHelp()) },
                About = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmOtherAbout), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmOtherAbout = (global::PCL.PageOtherAbout)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageOtherAbout()) },
                Test = new global::PCLCS.LegacyAuxiliaryPageSlot() { Read = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => global::PCL.ModMain.FrmOtherTest), Write = new Action<global::PCLLF.Presentation.Controls.MyPageRightView>(Page => global::PCL.ModMain.FrmOtherTest = (global::PCL.PageOtherTest)Page), Create = new Func<global::PCLLF.Presentation.Controls.MyPageRightView>(() => new global::PCL.PageOtherTest()) }
            };
        }
        private static global::System.Boolean LegacyLinkEnabled()
        {
#if LEGACY_LINK
            return true;
#else
            return false;
#endif
        }
        private static global::PCLCS.LegacyAuxiliaryPageSlot LinkSlot()
        {
#if LEGACY_LINK
            return new global::PCLCS.LegacyAuxiliaryPageSlot {
                Read = () => global::PCL.ModMain.FrmSetupLink,
                Write = page => global::PCL.ModMain.FrmSetupLink = (global::PCL.PageSetupLink)page,
                Create = () => new global::PCL.PageSetupLink(),
                Reset = page => ((global::PCL.PageSetupLink)page).Reset()
            };
#else
            return null;
#endif
        }
        internal static void RefreshHelp()
        {
            global::PCL.Settings.Set("SystemHelpVersion", 0);
            global::PCL.ModMain.FrmOtherHelp.PageLoaderRestart();
            global::PCL.ModMain.FrmOtherHelp.SearchBox.Text = "";
        }
        internal static readonly global::PCLCS.LegacyAuxiliaryHelpRuntime<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType> Help = new global::PCLCS.LegacyAuxiliaryHelpRuntime<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType>(new global::PCLCS.LegacyAuxiliaryHelpHost<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType>()
        {
            Common = global::PCL.SetupPageRuntime.Common,
            Release = new Func<global::System.Boolean>(() => (global::PCL.ModBase.BuildType == global::PCL.ModBase.BuildTypes.Release)),
            Debug = new Func<global::System.Boolean>(() => global::PCL.ModBase.ModeDebug),
            LoaderFinished = new Func<global::System.Boolean>(() => (global::PCL.ModMain.HelpLoader.State == global::PCL.ModBase.LoadState.Finished)),
            WaitForLoader = new Action(() => global::PCL.ModMain.HelpLoader.WaitForExit(global::PCL.ModBase.GetUuid())),
            RefreshHelp = global::PCL.AuxiliaryPageRuntime.RefreshHelp,
            Entries = new Func<global::System.Collections.Generic.List<global::PCL.ModMain.HelpEntry>>(() => global::PCL.ModMain.HelpLoader.Output),
            Types = new Func<global::PCL.ModMain.HelpEntry, global::System.Collections.Generic.List<global::System.String>>(Entry => Entry.Types),
            ShowPublic = new Func<global::PCL.ModMain.HelpEntry, global::System.Boolean>(Entry => Entry.ShowInPublic),
            ShowSnapshot = new Func<global::PCL.ModMain.HelpEntry, global::System.Boolean>(Entry => Entry.ShowInSnapshot),
            ShowSearch = new Func<global::PCL.ModMain.HelpEntry, global::System.Boolean>(Entry => Entry.ShowInSearch),
            IsEvent = new Func<global::PCL.ModMain.HelpEntry, global::System.Boolean>(Entry => Entry.IsEvent),
            Title = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.Title),
            Description = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.Desc),
            Search = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.Search),
            RawPath = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.RawPath),
            Xaml = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.XamlContent),
            EventType = new Func<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType>(Entry => Entry.EventType),
            EventData = new Func<global::PCL.ModMain.HelpEntry, global::System.String>(Entry => Entry.EventData),
            RaiseEvent = new Action<global::PCL.CustomEvent.EventType, global::System.String>((Type, Data) => global::PCL.CustomEvent.Raise(Type, Data)),
            NewEntry = new Func<global::System.String, global::PCL.ModMain.HelpEntry>(Path => new global::PCL.ModMain.HelpEntry(Path)),
            ToItem = new Func<global::PCL.ModMain.HelpEntry, global::PCLLF.Presentation.Controls.MyListItemView>(Entry => Entry.ToListItem()),
            NewCard = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => new global::PCL.MyCard()),
            InitializeLoader = global::PCL.AuxiliaryPageRuntime.InitializeHelpLoader,
            NewDetailController = global::PCL.AuxiliaryPageRuntime.NewHelpDetail,
            NavigateDetail = global::PCL.AuxiliaryPageRuntime.NavigateHelpDetail,
            ReplaceArguments = new Func<global::System.String, global::System.Func<global::System.String, global::System.String>, global::System.String>((Content, Escape) => global::PCL.ModMain.ArgumentReplace(Content, Escape)),
            ParseXaml = new Func<global::System.String, global::System.Windows.UIElement>(Content => (global::System.Windows.UIElement)global::PCL.ModBase.GetObjectFromXML(Content))
        });
        private static void InitializeHelpLoader(global::PCLLF.Presentation.Controls.MyPageRightView Page, global::PCLLF.Presentation.Controls.MyLoadingView Loading, global::System.Windows.FrameworkElement LoaderPanel, global::System.Windows.FrameworkElement ContentPanel, global::System.Action<global::System.Collections.Generic.List<global::PCL.ModMain.HelpEntry>> Finished)
        {
            ((global::PCL.MyPageRight)Page).PageLoaderInit((global::PCL.MyLoading)Loading, LoaderPanel, ContentPanel, (global::System.Windows.FrameworkElement)null, global::PCL.ModMain.HelpLoader, Loader => Finished(Loader.Output));
        }
        private static global::PCLCS.LegacyAuxiliaryHelpDetailController<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType> NewHelpDetail()
        {
            var Page = new global::PCL.PageOtherHelpDetail();
            return Page.Controller;
        }
        private static void NavigateHelpDetail(global::PCL.ModMain.HelpEntry Entry, global::PCLCS.LegacyAuxiliaryHelpDetailController<global::PCL.ModMain.HelpEntry, global::PCL.CustomEvent.EventType> Controller)
        {
            global::PCL.ModMain.FrmMain.PageChange(new global::PCL.FormMain.PageStackData() { Page = global::PCL.FormMain.PageType.HelpDetail, Additional = new object[] { Entry, ((global::PCL.PageOtherHelpDetail)Controller.View.Page()) } });
        }
        internal static readonly global::PCLCS.LegacyToolboxPageRuntime<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>> Tools = new global::PCLCS.LegacyToolboxPageRuntime<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(new global::PCLCS.LegacyToolboxPageHost<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>()
        {
            Common = global::PCL.SetupPageRuntime.Common,
            CurrentPage = new Func<global::PCLCS.LegacyToolboxPageController<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>>(() => global::PCL.ModMain.FrmOtherTest is null ? (global::PCLCS.LegacyToolboxPageController<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>)(null) : (global::PCL.ModMain.FrmOtherTest.Controller)),
            HasMain = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmMain is not null),
            ExtraDownload = new Func<global::PCLLF.Presentation.Controls.MyExtraButtonView>(() => global::PCL.ModMain.FrmMain.BtnExtraDownload),
            HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft),
            LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
            HasDownloadingTask = () => global::PCL.ModNet.HasDownloadingTask(),
            HasGameFolders = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McFolderList.Any()),
            HasAdminRole = global::MeloongCore.WindowsUtils.HasAdminRole,
            StartFolderLoader = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.Start()),
            WaitFolderLoader = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.WaitForExit()),
            OptimizeNative = global::PCL.NativeMemoryOptimizer.Run,
            GameFolders = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModMinecraft.McFolderList.Select(Item => Item.Location)),
            AvailableMemory = new Func<global::System.Int64>(() => (global::System.Int64)global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory),
            RunElevated = global::PCL.ModBase.RunAsAdmin,
            AcquireCleanup = global::PCL.PageOtherTest.AcquireCleanup,
            AcquireMemory = global::PCL.PageOtherTest.AcquireMemory,
            ReadCleanup = global::PCL.PageOtherTest.ReadCleanup,
            ReleaseCleanup = global::PCL.PageOtherTest.ReleaseCleanup,
            ReleaseMemory = global::PCL.PageOtherTest.ReleaseMemory,
            NewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)),
            RandomOne = new Func<global::System.Collections.Generic.ICollection<global::System.String>, global::System.String>(Values => global::PCL.ModBase.RandomOne(Values)),
            SaveFile = new Func<global::System.String, global::System.String, global::System.String>((Title, Name) => global::PCL.ModBase.RunInUiWait(() => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name))),
            SelectFolder = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFolder("选择目标文件夹", false).FirstOrDefault()),
            StartProcess = new Action<global::System.String>(Path => global::PCL.ModBase.StartProcess(Path)),
            ValidateFolder = new Func<global::System.String, global::System.String>(Path => (new global::PCL.ValidateFolderPath(false)).Validate(Path)),
            AddValidation = new Action<global::PCLLF.Presentation.Controls.MyTextBoxView, global::System.Func<global::System.String, global::System.String>>((TextBox, Rule) => ((global::PCL.MyTextBox)TextBox).ValidateRules.Add(new global::PCL.ValidateFunc(Rule))),
            AddFolderValidation = new Action<global::PCLLF.Presentation.Controls.MyTextBoxView>(TextBox => ((global::PCL.MyTextBox)TextBox).ValidateRules.Add(new global::PCL.ValidateFolderPath(false))),
            CreateSession = new Func<global::System.String, global::System.String, global::PCLCS.LegacyToolboxDownloadSession>((Folder, Name) => global::PCL.ToolboxDownloadSession.Create(Folder, Name)),
            NewFile = new Func<global::System.String, global::System.String, global::System.Int64, global::System.Boolean, global::PCL.ModNet.NetFile>((Url, Path, Minimum, Browser) => new global::PCL.ModNet.NetFile(new[] { Url }, Path, new global::MeloongCore.FileChecker() { MinSize = Minimum }, SimulateBrowserHeaders: Browser)),
            SetCredentialHeaders = new Action<global::PCL.ModNet.NetFile, global::System.Boolean>((File, Allow) => File.AllowCredentialHeaders = Allow),
            SetMergeFinished = new Action<global::PCL.ModNet.NetFile, global::System.Action>((File, Callback) => File.CanceledMergeFinished = Callback),
            NewDownload = new Func<global::System.String, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>, global::PCL.ModNet.LoaderDownload>((Name, Files) => new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files)),
            NewSave = new Func<global::System.String, global::System.Action<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>>, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>>((Name, Work) => new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>(Name, Task => Work(Task))),
            SaveCanceled = new Func<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::System.Boolean>(Task => Task.IsCanceled),
            SaveWeight = new Action<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::System.Double>((Task, Weight) => Task.ProgressWeight = Weight),
            NewCombo = new Func<global::System.String, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>((Name, Download, Save) => new global::PCL.ModLoader.LoaderCombo<global::System.Int32>(Name, new global::PCL.ModLoader.LoaderBase[] { Download, Save })),
            ComboState = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Int32>(Loader => (global::System.Int32)Loader.State),
            ComboName = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.String>(Loader => Loader.Name),
            ComboError = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Exception>(Loader => Loader.Error),
            SetStateCallback = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>>((Loader, Callback) => Loader.OnStateChanged = new Action<global::PCL.ModLoader.LoaderBase>(Changed => Callback((global::PCL.ModLoader.LoaderCombo<global::System.Int32>)Changed))),
            StartCombo = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(Loader => Loader.Start()),
            AddTaskbar = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(Loader => global::PCL.ModLoader.LoaderTaskbarAdd(Loader)),
            NewUuid = global::PCL.ModBase.GetUuid
        });
    }
}
