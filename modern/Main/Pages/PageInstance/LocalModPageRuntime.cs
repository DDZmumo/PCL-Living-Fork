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
using PCLCS;
using global::PCLLF.Presentation.Mods;
using NativePage = global::PCLLF.Presentation.Mods.LegacyModPage<global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyLocalModItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModNet.NetFile, global::PCL.MyIconButton, global::PCL.ModDependencyImpactView.Item>;
using NativeHost = global::PCLLF.Presentation.Mods.LegacyModPageHost<global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyLocalModItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModNet.NetFile, global::PCL.MyIconButton, global::PCL.ModDependencyImpactView.Item>;
using ThrottleDebounce;

namespace PCL
{

    // 原模型、Loader、依赖后端与主窗口的真实 typed 接线；不建立平行资源模型或调度器。
    internal sealed class LocalModPageRuntime
    {
        internal static global::PCLLF.Presentation.Mods.LegacyModPageHost<global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyLocalModItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModNet.NetFile, global::PCL.MyIconButton, global::PCL.ModDependencyImpactView.Item> CreateHost(global::PCL.PageInstanceMod Owner)
        {
            return new global::PCLLF.Presentation.Mods.LegacyModPageHost<global::PCL.LocalResourceFile, global::PCL.ResourceProject, global::PCL.ResourceVersion, global::PCL.MyLocalModItem, global::PCL.ModMinecraft.McInstance, global::PCL.ModNet.NetFile, global::PCL.MyIconButton, global::PCL.ModDependencyImpactView.Item>()
            {
                Items = new Func<global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, global::PCL.MyLocalModItem>>(() => Owner.ModItems),
                Selected = new Func<global::System.Collections.Generic.List<global::System.String>>(() => Owner.SelectedMods),
                SetSelected = new Action<global::System.Collections.Generic.List<global::System.String>>(Value => Owner.SelectedMods = Value),
                Output = new Func<global::System.Collections.Generic.List<global::PCL.LocalResourceFile>>(() => global::PCL.LocalResourceLoaders.LocalResourceLoader.Output),
                UpdatingFolders = new Func<global::System.Collections.Generic.List<global::System.String>>(() => global::PCL.PageInstanceMod.UpdatingInstanceModFolders),
                Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                GlobalInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                AnyLocked = () => ModMinecraft.McInstanceLock.IsAnyLocked,
                ResetIndexLookups = ModDependencyAutoFill.ResetIndexLookups,
                RebuildIndex = RebuildIndex,
                RefreshLoaderWarning = (Item,Target) => Item.RefreshLoaderWarning(System.IO.Path.Combine(Target.PathIndie,"mods"),ModDependencyInspector.ContextForInstance(Target)),
                GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathIndie),
                InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.Name),
                Modable = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => Value.Modable),
                Locked = global::PCL.ModMinecraft.McInstanceLock.IsLocked,
                LastWasDetails = new Func<global::System.Boolean>(() => (global::PCL.ModMain.FrmMain.PageLast.Page == global::PCL.FormMain.PageType.ResourceDetail)),
                IsCurrentPage = new Func<global::System.Boolean>(() => Object.ReferenceEquals((global::PCL.ModMain.FrmMain.PageRight), Owner)),
                CtrlDown = new Func<global::System.Boolean>(() => global::PCL.My.MyWpfExtension.Computer.Keyboard.CtrlKeyDown),
                ShiftDown = new Func<global::System.Boolean>(() => global::PCL.My.MyWpfExtension.Computer.Keyboard.ShiftKeyDown),
                IsInstanceSetup = new Func<global::System.Boolean>(() => ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup)),
                IsInstanceSelect = new Func<global::System.Boolean>(() => ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSelect)),
                IsModPage = new Func<global::System.Boolean>(() => (((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup) && (global::PCL.ModMain.FrmMain.PageCurrentSub == global::PCL.FormMain.PageSubType.InstanceMod))),
                ModpackMode = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                HistoryBusy = new Func<global::System.Boolean>(() => global::PCL.EnvironmentHistoryUi.IsBusy),
                AnimationControl = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
                SetAnimationControl = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled = Value),
                ChangeAnimationControl = new Action<global::System.Int32>(Delta => global::PCL.ModAnimation.AniControlEnabled += Delta),
                LoaderRun = new Func<global::System.Int32, global::System.Boolean>(Type => global::PCL.ModLoader.LoaderFolderRun(global::PCL.LocalResourceLoaders.LocalResourceLoader, global::PCL.PageInstanceLeft.Instance.PathIndie + @"mods\", (global::PCL.ModLoader.LoaderFolderRunType)(Type))),
                ForceFolderLoader = new Action<global::System.String>(Folder => global::PCL.ModLoader.LoaderFolderRun(global::PCL.LocalResourceLoaders.LocalResourceLoader, Folder, global::PCL.ModLoader.LoaderFolderRunType.ForceRun)),
                LoaderState = new Func<global::System.Int32>(() => (global::System.Int32)global::PCL.LocalResourceLoaders.LocalResourceLoader.State),
                ApplyMode = global::PCL.ModModpackMode.ApplyInstanceVisibility,
                BindMainKey = new Action(() => Owner.BindMainKey()),
                AdjustFilterMargins = new Action(() => Owner.AdjustFilterMargins()),
                InitializeLoader = new Action(() => Owner.InitializeNativeLoader()),
                ClearProjectCache = new Action(() => global::PCL.ResourceProject.Cache.Clear()),
                ClearVersionCache = new Action(() => global::PCL.ResourceVersion.ProjectFilesCache.Clear()),
                SelectModPage = new Action(() => global::PCL.ModMain.FrmInstanceLeft.ItemMod.Checked = true),
                ReloadCurrent = new Action<global::System.Boolean>(Force => global::PCL.LocalModPageRuntime.ReloadCurrent(Force)),
                TempPath = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
                Hint = global::PCL.LocalModPageRuntime.ShowHint,
                ConfirmMode = ActionName => global::PCL.ModModpackMode.ConfirmModAction(ActionName),
                ReadBoolean = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)),
                WriteSetting = new Action<global::System.String, global::System.Object>((Key, Value) => global::PCL.Settings.Set(Key, Value)),
                SelectInstallFiles = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::MeloongCore.Wpf.Dialogs.SelectFile("选择要安装的 Mod", true, filter: new[] { (new[] { "jar", "litemod", "disabled", "old" }, "Mod 文件") })),
                Explorer = global::PCL.ModBase.OpenExplorer,
                Website = global::PCL.ModBase.OpenWebsite,
                Message = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Caption, Button1, Button2, Warning) => global::PCL.ModMain.MyMsgBox(Text, Caption, Button1, Button2, IsWarn: Warning)),
                ContentMessage = new Action<global::System.String, global::System.String, global::System.Func<global::System.Windows.FrameworkElement>>((Text, Caption, Content) => global::PCL.ModMain.MyMsgBoxContent(Text, Caption, Content)),
                ImpactConfirm = new Func<global::System.String, global::System.String, global::System.Collections.Generic.List<global::PCL.ModDependencyImpactView.Item>, global::System.Int32>((Text, ActionName, Items) => global::PCL.ModMain.MyMsgBoxConfirmContent(Text, "模组依赖影响", "继续" + ActionName, () => new global::PCL.ModDependencyImpactView(Items, ActionName))),
                InspectImpacts = global::PCL.LocalModPageRuntime.InspectImpacts,
                InspectDetails = global::PCL.LocalModPageRuntime.InspectDetails,
                Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                Background = new Action<global::System.Action, global::System.String, global::System.Threading.ThreadPriority>((Work, Name, Priority) => global::PCL.ModBase.RunInNewThread(Work, Name, Priority)),
                NewItem = new Func<global::PCL.LocalResourceFile, global::System.Boolean, global::PCL.MyLocalModItem>((Entry, Selected) => Owner.CreateItem(Entry, Selected)),
                BindUpdate = global::PCL.LocalModPageRuntime.BindUpdate,
                Entry = new Func<global::PCL.MyLocalModItem, global::PCL.LocalResourceFile>(Item => Item.Entry),
                Checked = new Func<global::PCL.MyLocalModItem, global::System.Boolean>(Item => Item.Checked),
                SetChecked = new Action<global::PCL.MyLocalModItem, global::System.Boolean>((Item, Value) => Item.Checked = Value),
                BindRowEvents = new Action<global::PCL.MyLocalModItem>(Item => Owner.BindRowEvents(Item)),
                CreateButton = global::PCL.LocalModPageRuntime.CreateButton,
                SetButtons = new Action<global::PCL.MyLocalModItem, global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>>((Item, Buttons) => Item.Buttons = Buttons),
                OpenLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonOpen),
                InfoLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonInfo),
                DeleteLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonDelete),
                StopLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonStop),
                CheckLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonCheck),
                ListLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonList),
                ResolveItem = new Func<global::System.Object, global::PCL.MyLocalModItem>(Sender => ((global::PCL.MyLocalModItem)(((Sender is global::PCL.MyIconButton) ? (((global::PCL.MyIconButton)(Sender)).Tag) : (Sender))))),
                NewMod = new Func<global::System.String, global::PCL.LocalResourceFile>(Address => new global::PCL.LocalResourceFile(Address)),
                NavigateDownload = global::PCL.LocalModPageRuntime.NavigateDownload,
                NavigateDetails = global::PCL.LocalModPageRuntime.NavigateDetails,
                ToNetFile = new Func<global::PCL.ResourceVersion, global::System.String, global::PCL.ModNet.NetFile>((Value, Address) => Value.ToNetFile(Address, global::PCL.ResourceVersion.DownloadReason.Update, global::PCL.PageInstanceLeft.Instance.Version.VanillaName, global::PCL.PageInstanceLeft.Instance.Version.ModLoaders)),
                CreateUpdateJob = global::PCL.LocalModPageRuntime.CreateUpdateJob,
                CaptureHistory = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Threading.CancellationToken>((Instance, Caption, Token) => global::PCL.EnvironmentHistory.CaptureInstance(Instance, Caption, Token)),
                RefreshDownloadButton = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownloadButton = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                FormatSize = global::MeloongCore.StringUtils.FormatByteSize
            };
        }

        private static LegacyModDependencyIndexResult RebuildIndex(ModMinecraft.McInstance Target, Action<int,int> Progress) {
            var report = ModDependencyOnline.RebuildWithOnline(System.IO.Path.Combine(Target.PathIndie,"mods"),ModDependencyInspector.ContextForInstance(Target),Options:new ModDependencyOnline.LookupOptions {Progress=Progress});
            return new LegacyModDependencyIndexResult {IndexReadFailed=()=>report.IndexReadFailed,IndexPersisted=()=>report.IndexPersisted,OnlineIncomplete=()=>report.OnlineIncomplete};
        }
        private static void ShowHint(global::System.String Text, global::System.Int32 Kind, global::System.Boolean WriteLog)
        {
            var Color = ((Kind) == (1)) ? (global::PCL.ModMain.HintType.Red) : ((((Kind) == (2)) ? (global::PCL.ModMain.HintType.Green) : ((((Kind) == (3)) ? (global::PCL.ModMain.HintType.Blue) : (global::PCL.ModMain.HintType.Blue)))));
            global::PCL.ModMain.Hint(Text, Color, Log: WriteLog);
        }
        private static void ReloadCurrent(global::System.Boolean Force)
        {
            if (global::PCL.ModMain.FrmInstanceMod is not null)
                global::PCL.ModMain.FrmInstanceMod.ReloadModList(Force);
        }
        private static void BindUpdate(global::PCL.LocalResourceFile Entry, global::PCL.MyLocalModItem Item)
        {
            Entry.OnUpdate += _ => global::PCL.ModBase.RunInUi(Item.Refresh);
        }
        private static global::PCL.MyIconButton CreateButton(global::System.Double Scale, global::System.String Icon, global::System.String Description, global::PCL.MyLocalModItem Item, global::System.Action<global::System.Object> Work)
        {
            var Button = new global::PCL.MyIconButton() { LogoScale = Scale, Logo = Icon, Tag = Item, ToolTip = Description };
            Button.Click += (Sender, Args) => Work(Sender);
            return Button;
        }
        private static global::System.Collections.Generic.List<global::PCL.ModDependencyImpactView.Item> InspectImpacts(global::PCL.ModMinecraft.McInstance Target, global::System.String[] Candidates)
        {
            var Report = global::PCL.ModDependencyInspector.InspectForImpact(global::System.IO.Path.Combine(Target.PathIndie, "mods"), global::PCL.ModDependencyInspector.ContextForInstance(Target));
            return global::PCL.ModDependencyImpactView.Collect(Report, Candidates);
        }
        private static global::PCLLF.Presentation.Mods.LegacyModDependencyDetails InspectDetails(global::PCL.ModMinecraft.McInstance Target, global::PCL.LocalResourceFile Entry)
        {
            var Report = global::PCL.ModDependencyOnline.InspectWithOnline(global::System.IO.Path.Combine(Target.PathIndie, "mods"), global::PCL.ModDependencyInspector.ContextForInstance(Target), Options: new global::PCL.ModDependencyOnline.LookupOptions() { TargetFile = Entry.File.FullName, ComparePlatformDependencies = true, MaxSearchFiles = 0 });
            return new global::PCLLF.Presentation.Mods.LegacyModDependencyDetails()
            {
                Caption = Report.DescribeFile(Entry.File.FullName),
                HasMod = new Func<global::System.Boolean>(() => Report.Mods.Any(Item => global::PCL.ModDependencyInspector.SameFilePath(Item.RootPath, Entry.File.FullName))),
                CreateView = new Func<global::System.Windows.FrameworkElement>(() => new global::PCL.ModDependencyGraphView(Report, Entry.File.FullName))
            };
        }
        private static void NavigateDownload(global::PCL.ModMinecraft.McInstance Instance, global::System.String Search)
        {
            global::PCL.PageResource.TargetInstance = Instance;
            global::PCL.PageResource.TargetName = Search;
            global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, global::PCL.FormMain.PageSubType.DownloadMod);
        }
        private static void NavigateDetails(global::PCL.ResourceProject Project)
        {
            global::PCL.ModMain.FrmMain.PageChange(new global::PCL.FormMain.PageStackData()
            {
                Page = global::PCL.FormMain.PageType.ResourceDetail,
                Additional = new object[] { Project, new global::System.Collections.Generic.List<global::System.String>(), global::PCL.PageInstanceLeft.Instance.Version.VanillaName, global::PCL.PageInstanceLeft.Instance.Version.ModLoaders, global::PCLCS.ResourceTypes.Mod }
            });
        }
        private static global::PCLLF.Presentation.Mods.LegacyModUpdateJob CreateUpdateJob(global::System.Func<global::System.String> Name, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::System.Double Weight, global::System.Action Replace)
        {
            var Loaders = new global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase>() { new global::PCL.ModNet.LoaderDownload("下载新版 Mod 文件", (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files) { ProgressWeight = Weight }, new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>("替换旧版 Mod 文件", _ => Replace()) };
            var Loader = new global::PCL.ModLoader.LoaderCombo<global::System.Collections.Generic.IEnumerable<global::PCL.LocalResourceFile>>(Name(), (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Loaders);
            var Port = new global::PCLLF.Presentation.Mods.LegacyModUpdateJob()
            {
                State = new Func<global::System.Int32>(() => (global::System.Int32)Loader.State),
                Error = new Func<global::System.Exception>(() => Loader.Error),
                Start = new Action(() => Loader.Start()),
                AddTaskbar = new Action(() => global::PCL.ModLoader.LoaderTaskbarAdd(Loader))
            };
            Port.SetOnStateChanged = new Action<global::System.Action<global::PCLLF.Presentation.Mods.LegacyModUpdateJob>>(Work => Loader.OnStateChanged = new Action<global::PCL.ModLoader.LoaderBase>(_ => Work(Port)));
            return Port;
        }
    }
}
