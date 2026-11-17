using System;
using System.Collections;
using System.Collections.Concurrent;
using global::System.Collections.Generic;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using global::System.Linq;
using System.Linq;
using System.Net;
using global::System.Net.Http;
using System.Net.Http;
using System.Text;
using global::System.Threading;
using System.Threading;
using System.Threading.Tasks;
using global::System.Windows;
using System.Windows;
using global::System.Windows.Controls;
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
using InstallCore = global::PCLCS.MinecraftInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ModMinecraft.McLibEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest>;
using MenuCore = global::PCLCS.MinecraftInstallMenuController<global::PCL.MyListItem, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem.ClickEventHandler, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion>;
using ThrottleDebounce;

namespace PCL
{

    // LF 原公开类型与 WPF/Loader/平台适配；完整安装、进程、合并/修改、清理与菜单规则在 C# 中。
    public static class ModDownloadLib
    {
        public class McInstallRequest : global::PCLCS.MinecraftInstallRequestData<global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ResourceVersion, global::PCL.ModDownload.DlLiteLoaderListEntry>
        {
        }
        private readonly static global::PCL.ModDownloadLib.InstallLoaderFactory NativeLoaders = new global::PCL.ModDownloadLib.InstallLoaderFactory();
        private readonly static global::PCLCS.MinecraftInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ModMinecraft.McLibEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest> Service = new global::PCLCS.MinecraftInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ModMinecraft.McLibEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest>(global::PCL.ModDownloadLib.CreateHost());
        private readonly static global::PCLCS.MinecraftInstallMenuController<global::PCL.MyListItem, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem.ClickEventHandler, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion> Menus = new global::PCLCS.MinecraftInstallMenuController<global::PCL.MyListItem, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem.ClickEventHandler, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ResourceVersion>(global::PCL.ModDownloadLib.CreateMenuHost());

        private static global::PCLCS.MinecraftInstallHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ResourceVersion, global::PCL.ModMinecraft.McLibEntry> CreateHost()
        {
            return new global::PCLCS.MinecraftInstallHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ResourceVersion, global::PCL.ModMinecraft.McLibEntry>()
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
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                TempFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
                PureFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathPure.Value),
                RequestTempFolder = new Func<global::System.Boolean, global::System.String>(Require => global::PCL.ModMain.RequestTaskTempFolder(Require)),
                NewInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Folder => new global::PCL.ModMinecraft.McInstance(Folder)),
                LibraryFiles = global::PCL.ModMinecraft.McLibNetFilesFromInstance,
                AssetFiles = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean, global::PCL.ModLoader.LoaderBase, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>>((Instance, Hash, Loader) => global::PCL.ModMinecraft.McAssetsFixList(Instance, Hash, ref Loader)),
                AssetIndex = global::PCL.ModDownload.DlClientAssetIndexGet,
                ClientJar = global::PCL.ModDownload.DlClientJarGet,
                ClientJsonUrl = new Func<global::System.String, global::System.String>(Id => Conversions.ToString(global::PCL.ModDownload.DlClientListGet(Id))),
                LauncherUrls = global::PCL.ModDownload.DlSourceLauncherOrMetaGet,
                NetFile = global::PCL.ModDownloadLib.NewInstallFile,
                LocalPath = new Func<global::PCL.ModNet.NetFile, global::System.String>(File => File.LocalPath),
                ResourceNetFile = new Func<global::PCL.ResourceVersion, global::System.String, global::System.String, global::PCLCS.ModLoaders, global::PCL.ModNet.NetFile>((Version, Target, GameVersion, Loaders) => Version.ToNetFile(Target, global::PCL.ResourceVersion.DownloadReason.Dependency, GameVersion, Loaders)),
                RetryRequest = new Func<global::PCLCS.MinecraftDownloadRequest, global::System.String>(Request => global::PCL.ModNet.NetRequestByClientRetry(Request.Url, Request.Method, Content: Request.Content, ContentType: Request.ContentType, Accept: Request.Accept, RequireJson: Request.RequireJson, Encoding: Request.Encoding, SimulateBrowserHeaders: Request.SimulateBrowserHeaders)),
                LoaderRequest = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Boolean, global::System.String>((Urls, Json) => global::PCL.ModNet.NetRequestByLoader(Urls, IsJson: Json)),
                NeoForge = new Func<global::System.String, global::PCL.ModDownload.DlNeoForgeListEntry>(Name => new global::PCL.ModDownload.DlNeoForgeListEntry(Name)),
                ForgeList = global::PCL.ModDownloadLib.ReadForgeList,
                ReadLibraryList = new Func<global::Newtonsoft.Json.Linq.JObject, global::System.Boolean, global::System.String, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McLibEntry>>((Json, Keep, Folder) => global::PCL.ModMinecraft.McLibListGetWithJson(Json, Keep, Folder)),
                LibraryNetFiles = global::PCL.ModMinecraft.McLibNetFilesFromTokens,
                LibraryPath = new Func<global::System.String, global::System.String, global::System.String>((Original, Folder) => global::PCL.ModMinecraft.McLibGet(Original, CustomMcFolder: Folder)),
                CreateLauncherProfiles = global::PCL.ModMinecraft.McFolderLauncherProfilesJsonCreate,
                SelectJava = new Func<global::MeloongCore.ValueRange<global::System.Version>, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider, global::PCLCS.Java>((Range, Token, Progress) => global::PCL.ModJava.SelectOrDownloadJava(Range, true, Token, Progress)),
                BooleanSetting = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)),
                ExtractResources = new Action<global::System.String, global::System.String>((Name, ResourceName) => global::PCL.ModBase.ExtractResources(Name, ResourceName)),
                RunInThread = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInThread(Work)),
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                RunInUiWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)),
                RunInNewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)),
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                RefreshInstanceList = new Action(() => global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, global::PCL.ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\")),
                Hint = global::PCL.ModDownloadLib.InstallHint,
                Message = new Func<global::PCLCS.ModpackMessage, global::System.Int32>(Message => global::PCL.ModMain.MyMsgBox(Message.Text, Message.Title, Message.Button1, Message.Button2, IsWarn: Message.IsWarn, ForceWait: Message.ForceWait)),
                SaveFile = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((Name, Extension, Description) => global::MeloongCore.Wpf.Dialogs.SaveFile("选择保存位置", Name, filter: new[] { (Extension, Description) })),
                SelectFolder = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFolder("选择目标文件夹", false).FirstOrDefault()),
                OpenWebsite = new Action<global::System.String>(Url => global::PCL.ModBase.OpenWebsite(Url)),
                WriteIni = new Action<global::System.String, global::System.String, global::System.Object>((Name, Key, Value) => global::PCL.ModBase.WriteIni(Name, Key, Conversions.ToString(Value))),
                NativesFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => global::PCL.ModLaunch.GetNativesFolder(Instance)),
                FileWorkload = new Func<global::PCL.ModLoader.LoaderBase, global::System.String, global::System.Action>((Loader, Input) => global::PCL.LoaderFileOperation.CreateWorkload((global::PCL.ModLoader.LoaderCombo<global::System.String>)Loader, Input)),
                TryAcquire = global::PCL.ModMinecraft.McInstanceLock.TryAcquire,
                InstancePage = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                SetInstancePage = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.PageInstanceLeft.Instance = Instance),
                SetSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.ModMinecraft.McInstanceSelected = Instance),
                BeforeMinecraftChange = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.String, global::System.Threading.CancellationToken>((Instance, Before, After, Token) => global::PCL.WorldBackups.BeforeMinecraftChange(Instance, Before, After, Token)),
                CaptureEnvironment = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Threading.CancellationToken>((Instance, Reason, Token) => global::PCL.EnvironmentHistory.CaptureAutomatic(Instance, Reason, Token)),
                CleanupState = new Func<global::System.Object, global::System.Int32>(Loader => Conversions.ToInteger(((dynamic)Loader).State)),
                CleanupInput = new Func<global::System.Object, global::System.String>(Loader => Conversions.ToString(((dynamic)Loader).Input)),
                CleanupTasks = InstallCleanupTasks
            };
        }
        private static IEnumerable<object> InstallCleanupTasks(object loader)
        {
            yield return loader;
            Type type = loader.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ModLoader.LoaderCombo<>)) yield break;
            // LF GetLoaderList(false) restores RequireShow=true when it recurses.
            // Cleanup needs the actual hidden marker task at every nesting level.
            foreach (ModLoader.LoaderBase child in (IEnumerable<ModLoader.LoaderBase>)((dynamic)loader).Loaders)
                foreach (object nested in InstallCleanupTasks(child)) yield return nested;
        }
        private static void InstallHint(global::System.String Text, global::System.Int32 Kind)
        {
            global::PCL.ModMain.Hint(Text, ((Kind) == (1)) ? (global::PCL.ModMain.HintType.Red) : ((((Kind) == (2)) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Blue))));
        }
        private static global::PCL.ModNet.NetFile NewInstallFile(global::System.Collections.Generic.IEnumerable<global::System.String> Urls, global::System.String Name, global::PCLCS.MinecraftDownloadCheck Checker)
        {
            return new global::PCL.ModNet.NetFile(Urls, Name, Checker is null ? (global::MeloongCore.FileChecker)(null) : (new global::MeloongCore.FileChecker() { MinSize = Checker.MinSize, ActualSize = Checker.ActualSize, Hash = Checker.Hash, IsJson = Checker.IsJson }));
        }
        private static global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry> ReadForgeList(global::System.String Inherit)
        {
            var Loader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry>>("McDownloadForgeLoader " + Inherit, global::PCL.ModDownload.DlForgeVersionMain);
            Loader.WaitForExit(Inherit);
            return Loader.Output;
        }

        private class InstallLoaderFactory : global::PCLCS.IMinecraftInstallLoaders<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile>
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
                return global::PCL.ModDownloadLib.InstallLoaderFactory.ApplyOptions(new global::PCL.ModLoader.LoaderTask<TInput, TOutput>(Name, Task => Work(this.WrapTask<TInput, TOutput>(Task))), Options);
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
                return global::PCL.ModDownloadLib.InstallLoaderFactory.ApplyOptions(new global::PCL.ModLoader.LoaderCombo<TInput>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Steps), Options);
            }
            public global::PCL.ModLoader.LoaderBase Download(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::PCLCS.MinecraftDownloadLoaderSettings Options)
            {
                return global::PCL.ModDownloadLib.InstallLoaderFactory.ApplyOptions(new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files), Options);
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

        private static global::PCLCS.MinecraftInstallMenuHost<global::PCL.MyListItem, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem.ClickEventHandler, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlLiteLoaderListEntry> CreateMenuHost()
        {
            return new global::PCLCS.MinecraftInstallMenuHost<global::PCL.MyListItem, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem.ClickEventHandler, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlLiteLoaderListEntry>()
            {
                Virtualize = new Func<global::System.Func<global::PCL.MyListItem>, global::System.Double, global::PCL.MyVirtualizingElement<global::PCL.MyListItem>>((Work, Height) => new global::PCL.MyVirtualizingElement<global::PCL.MyListItem>(() => Work()) { Height = Height }),
                CreateItem = global::PCL.ModDownloadLib.CreateMenuItem,
                Initialize = new Func<global::PCL.MyVirtualizingElement<global::PCL.MyListItem>, global::PCL.MyListItem>(Item => Item.Init()),
                Title = new Func<global::PCL.MyListItem, global::System.String>(Item => Item.Title),
                Info = new Func<global::PCL.MyListItem, global::System.String>(Item => Item.Info),
                SetInfo = new Action<global::PCL.MyListItem, global::System.String>((Item, Value) => Item.Info = Value),
                SetButtons = global::PCL.ModDownloadLib.SetMenuButtons,
                ImageRoot = new Func<global::System.String>(() => global::PCL.ModBase.PathImage),
                DebugMode = new Func<global::System.Boolean>(() => global::PCL.ModBase.ModeDebug),
                OpenWebsite = new Action<global::System.String>(Url => global::PCL.ModBase.OpenWebsite(Url)),
                SaveOptiFine = global::PCL.ModDownloadLib.Service.McDownloadOptiFineSave,
                SaveForgelike = global::PCL.ModDownloadLib.Service.McDownloadForgelikeSave,
                SaveLiteLoader = global::PCL.ModDownloadLib.Service.McDownloadLiteLoaderSave,
                SaveMinecraft = global::PCL.ModDownloadLib.Service.SaveMinecraft
            };
        }
        private static global::PCL.MyListItem CreateMenuItem(global::PCLCS.MinecraftInstallListItem Model, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Action<global::System.Object, global::System.EventArgs> Content)
        {
            var Item = new global::PCL.MyListItem()
            {
                Logo = Model.Logo,
                SnapsToDevicePixels = true,
                Title = Model.Title,
                Height = 42d,
                Type = global::PCL.MyListItem.CheckType.Clickable,
                Tag = Model.Tag,
                Info = Model.Info
            };
            Item.Click += OnClick;
            if (Content is not null)
                Item.ContentHandler = new Action<global::PCL.MyListItem, global::System.EventArgs>((Sender, Args) => Content(Sender, Args));
            return Item;
        }
        private static void SetMenuButtons(global::System.Object Sender, global::System.Collections.Generic.List<global::PCLCS.MinecraftInstallMenuButton> Entries)
        {
            var Buttons = new global::System.Collections.Generic.List<global::PCL.MyIconButton>();
            foreach (var Entry in Entries)
            {
                var Current = Entry;
                global::System.String Icon;
                switch (Entry.Icon)
                {
                    case global::PCLCS.MinecraftInstallMenuIcon.Save:
                        {
                            Icon = global::PCL.ModBase.Logo.IconButtonSave;
                            break;
                        }
                    case global::PCLCS.MinecraftInstallMenuIcon.Info:
                        {
                            Icon = global::PCL.ModBase.Logo.IconButtonInfo;
                            break;
                        }
                    case global::PCLCS.MinecraftInstallMenuIcon.Server:
                        {
                            Icon = global::PCL.ModBase.Logo.IconButtonServer;
                            break;
                        }

                    default:
                        {
                            Icon = global::PCL.ModBase.Logo.IconButtonList;
                            break;
                        }
                }
                var Button = new global::PCL.MyIconButton() { Logo = Icon, ToolTip = Entry.ToolTip, Tag = Entry.Tag };
                if (Entry.LogoScale.HasValue)
                    Button.LogoScale = Entry.LogoScale.Value;
                Button.Click += (ButtonSender, Args) => Current.Click(ButtonSender, (global::System.Windows.RoutedEventArgs)Args);
                Buttons.Add(Button);
            } ((global::PCL.MyListItem)Sender).Buttons = (global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>)Buttons;
        }

        private const global::System.String McDownloadClientLibName = "下载原版支持库文件";
        private const global::System.String McDownloadClientJsonName = "下载原版 json 文件";
        private static object InstallSyncLock
        {
            get
            {
                return global::PCL.ModDownloadLib.Service.InstallSyncLock;
            }
            set
            {
                global::PCL.ModDownloadLib.Service.InstallSyncLock = value;
            }
        }
        private static object VanillaSyncLock
        {
            get
            {
                return global::PCL.ModDownloadLib.Service.VanillaSyncLock;
            }
            set
            {
                global::PCL.ModDownloadLib.Service.VanillaSyncLock = value;
            }
        }
        private static bool IsForgeRecommendedRefreshed
        {
            get
            {
                return global::PCL.ModDownloadLib.Service.IsForgeRecommendedRefreshed;
            }
            set
            {
                global::PCL.ModDownloadLib.Service.IsForgeRecommendedRefreshed = value;
            }
        }

        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadClientLoader(global::System.String Id, global::System.String JsonUrl, global::System.String VersionFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadClientLoader(Id, JsonUrl, VersionFolder);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> McDownloadListItem(global::Newtonsoft.Json.Linq.JObject Entry, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            return global::PCL.ModDownloadLib.Menus.McDownloadListItem(Entry, OnClick, IsSaveOnly);
        }
        private static void McDownloadSaveMenuBuild(global::System.Object sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.McDownloadSaveMenuBuild(sender, e);
        }
        private static void McDownloadMenuBuild(global::System.Object sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.McDownloadMenuBuild(sender, e);
        }
        private static void McDownloadMenuLog(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.McDownloadMenuLog(sender, e);
        }
        private static void McDownloadMenuSaveServer(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.McDownloadMenuSaveServer(sender, e);
        }
        public static void McDownloadMenuSave(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.McDownloadMenuSave(sender, e);
        }
        public static void McUpdateLogShow(global::Newtonsoft.Json.Linq.JToken InstanceJson)
        {
            global::PCL.ModDownloadLib.Menus.McUpdateLogShow(InstanceJson);
        }
        private static void McDownloadOptiFineSave(global::PCL.ModDownload.DlOptiFineListEntry DownloadInfo)
        {
            global::PCL.ModDownloadLib.Service.McDownloadOptiFineSave(DownloadInfo);
        }
        private static void McDownloadOptiFineInstall(global::System.String BaseMcFolderHome, global::System.String Target, global::PCL.ModLoader.LoaderTask<global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>, global::System.Boolean> Task, global::System.Boolean UseJavaWrapper)
        {
            global::PCL.ModDownloadLib.Service.McDownloadOptiFineInstall(BaseMcFolderHome, Target, global::PCL.ModDownloadLib.NativeLoaders.WrapTask<List<global::PCL.ModNet.NetFile>, bool>(Task), UseJavaWrapper);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadOptiFineLoader(global::PCL.ModDownload.DlOptiFineListEntry DownloadInfo, global::System.String McFolder, global::PCL.ModLoader.LoaderCombo<global::System.String> ClientDownloadLoader, global::System.String ClientFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadOptiFineLoader(DownloadInfo, McFolder, ClientDownloadLoader, ClientFolder);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadOptiFineSaveLoader(global::PCL.ModDownload.DlOptiFineListEntry DownloadInfo, global::System.String TargetFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadOptiFineSaveLoader(DownloadInfo, TargetFolder);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> OptiFineDownloadListItem(global::PCL.ModDownload.DlOptiFineListEntry Entry, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            return global::PCL.ModDownloadLib.Menus.OptiFineDownloadListItem(Entry, OnClick, IsSaveOnly);
        }
        private static void OptiFineSaveContMenuBuild(global::System.Object sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.OptiFineSaveContMenuBuild(sender, e);
        }
        private static void OptiFineContMenuBuild(global::System.Object sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.OptiFineContMenuBuild(sender, e);
        }
        private static void OptiFineLog_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.OptiFineLog_Click(sender, e);
        }
        public static void OptiFineSave_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.OptiFineSave_Click(sender, e);
        }
        private static void McDownloadLiteLoaderSave(global::PCL.ModDownload.DlLiteLoaderListEntry DownloadInfo)
        {
            global::PCL.ModDownloadLib.Service.McDownloadLiteLoaderSave(DownloadInfo);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadLiteLoaderLoader(global::PCL.ModDownload.DlLiteLoaderListEntry DownloadInfo, global::System.String McFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadLiteLoaderLoader(DownloadInfo, McFolder);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> LiteLoaderDownloadListItem(global::PCL.ModDownload.DlLiteLoaderListEntry Entry, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            return global::PCL.ModDownloadLib.Menus.LiteLoaderDownloadListItem(Entry, OnClick, IsSaveOnly);
        }
        private static void LiteLoaderSaveContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.LiteLoaderSaveContMenuBuild(sender, e);
        }
        private static void LiteLoaderContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.LiteLoaderContMenuBuild(sender, e);
        }
        private static void LiteLoaderAll_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.LiteLoaderAll_Click(sender, e);
        }
        public static void LiteLoaderSave_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.LiteLoaderSave_Click(sender, e);
        }
        public static void McDownloadForgelikeSave(global::PCL.ModDownload.DlForgelikeEntry Info)
        {
            global::PCL.ModDownloadLib.Service.McDownloadForgelikeSave(Info);
        }
        private static void ForgelikeInjector(global::System.String Target, global::PCL.ModLoader.LoaderTask<global::System.Boolean, global::System.Boolean> Task, global::System.String McFolder, global::System.Boolean UseJavaWrapper, global::System.Boolean IsNeoForge)
        {
            global::PCL.ModDownloadLib.Service.ForgelikeInjector(Target, global::PCL.ModDownloadLib.NativeLoaders.WrapTask<bool, bool>(Task), McFolder, UseJavaWrapper, IsNeoForge);
        }
        private static void ForgelikeInjectorLine(global::System.String Content, global::PCL.ModLoader.LoaderTask<global::System.Boolean, global::System.Boolean> Task)
        {
            global::PCL.ModDownloadLib.Service.ForgelikeInjectorLine(Content, global::PCL.ModDownloadLib.NativeLoaders.WrapTask<bool, bool>(Task));
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadForgelikeLoader(global::System.Boolean IsNeoForge, global::System.String LoaderVersion, global::System.String NewInstanceName, global::System.String Inherit, global::PCL.ModDownload.DlForgelikeEntry Info, global::System.String McFolder, global::PCL.ModLoader.LoaderCombo<global::System.String> ClientDownloadLoader, global::System.String ClientFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadForgelikeLoader(IsNeoForge, LoaderVersion, NewInstanceName, Inherit, Info, McFolder, ClientDownloadLoader, ClientFolder);
        }
        public static void ForgeDownloadListItemPreload(global::System.Windows.Controls.StackPanel Stack, global::System.Collections.Generic.List<global::PCL.ModDownload.DlForgeVersionEntry> Entries, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            global::PCL.ModDownloadLib.Menus.ForgeDownloadListItemPreload(Stack, Entries, OnClick, IsSaveOnly);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> ForgeDownloadListItem(global::PCL.ModDownload.DlForgeVersionEntry Entry, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            return global::PCL.ModDownloadLib.Menus.ForgeDownloadListItem(Entry, OnClick, IsSaveOnly);
        }
        private static void ForgeContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.ForgeContMenuBuild(sender, e);
        }
        private static void ForgeSaveContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.ForgeSaveContMenuBuild(sender, e);
        }
        private static void ForgeLog_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.ForgeLog_Click(sender, e);
        }
        public static void ForgeSave_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.ForgeSave_Click(sender, e);
        }
        public static void McDownloadForgeRecommendedRefresh()
        {
            global::PCL.ModDownloadLib.Service.McDownloadForgeRecommendedRefresh();
        }
        public static global::System.String McDownloadForgeRecommendedGet(global::System.String McVersionName)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadForgeRecommendedGet(McVersionName);
        }
        public static void NeoForgeDownloadListItemPreload(global::System.Windows.Controls.StackPanel Stack, global::System.Collections.Generic.List<global::PCL.ModDownload.DlNeoForgeListEntry> Entries, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            global::PCL.ModDownloadLib.Menus.NeoForgeDownloadListItemPreload(Stack, Entries, OnClick, IsSaveOnly);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> NeoForgeDownloadListItem(global::PCL.ModDownload.DlNeoForgeListEntry Info, global::PCL.MyListItem.ClickEventHandler OnClick, global::System.Boolean IsSaveOnly)
        {
            return global::PCL.ModDownloadLib.Menus.NeoForgeDownloadListItem(Info, OnClick, IsSaveOnly);
        }
        private static void NeoForgeContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.NeoForgeContMenuBuild(sender, e);
        }
        private static void NeoForgeSaveContMenuBuild(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.NeoForgeSaveContMenuBuild(sender, e);
        }
        private static void NeoForgeLog_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.NeoForgeLog_Click(sender, e);
        }
        public static void NeoForgeSave_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            global::PCL.ModDownloadLib.Menus.NeoForgeSave_Click(sender, e);
        }
        public static void McDownloadFabricLoaderSave(global::Newtonsoft.Json.Linq.JObject DownloadInfo)
        {
            global::PCL.ModDownloadLib.Service.McDownloadFabricLoaderSave(DownloadInfo);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadFabricLoader(global::System.String FabricVersion, global::System.String MinecraftName, global::System.String McFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadFabricLoader(FabricVersion, MinecraftName, McFolder);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> FabricDownloadListItem(global::Newtonsoft.Json.Linq.JObject Entry, global::PCL.MyListItem.ClickEventHandler OnClick)
        {
            return global::PCL.ModDownloadLib.Menus.FabricDownloadListItem(Entry, OnClick);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> FabricApiDownloadListItem(global::PCL.ResourceVersion Entry, global::PCL.MyListItem.ClickEventHandler OnClick)
        {
            return global::PCL.ModDownloadLib.Menus.FabricApiDownloadListItem(Entry, OnClick);
        }
        public static global::PCL.MyVirtualizingElement<global::PCL.MyListItem> OptiFabricDownloadListItem(global::PCL.ResourceVersion Entry, global::PCL.MyListItem.ClickEventHandler OnClick)
        {
            return global::PCL.ModDownloadLib.Menus.OptiFabricDownloadListItem(Entry, OnClick);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McInstallLoadersGet(global::PCL.ModDownloadLib.McInstallRequest Request)
        {
            return global::PCL.ModDownloadLib.Service.McInstallLoadersGet(Request);
        }
        private static void MergeJson(global::System.String VersionFolder, global::System.String OptiFineFolder, global::System.Boolean OptiFineAsMod, global::System.String ForgeFolder, global::System.String NeoForgeFolder, global::System.String FabricFolder, global::System.String LiteLoaderFolder)
        {
            global::PCL.ModDownloadLib.Service.MergeJson(VersionFolder, OptiFineFolder, OptiFineAsMod, ForgeFolder, NeoForgeFolder, FabricFolder, LiteLoaderFolder);
        }
        public static global::System.Boolean McInstallStart(global::PCL.ModDownloadLib.McInstallRequest Request)
        {
            return global::PCL.ModDownloadLib.Service.McInstallStart(Request);
        }
        public static global::System.Boolean McModificationStart(global::PCL.ModDownloadLib.McInstallRequest Request, global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.ModDownloadLib.Service.McModificationStart(Request, Instance);
        }
        public static void McInstallCommit(global::PCL.ModMinecraft.McInstance Instance, global::PCLCS.McInstallManifest NewManifest, global::System.String BuildFolder, global::System.String TargetFolder)
        {
            global::PCL.ModDownloadLib.Service.McInstallCommit(Instance, NewManifest, BuildFolder, TargetFolder);
        }
        public static void LoaderStateChangedHintOnly(global::PCL.ModLoader.LoaderBase Loader)
        {
            global::PCL.ModDownloadLib.Service.LoaderStateChangedHintOnly(Loader);
        }
        public static void McInstallState(global::PCL.ModLoader.LoaderBase Loader)
        {
            global::PCL.ModDownloadLib.Service.McInstallState(Loader);
        }
        public static void McInstallFailedClearFolder(global::System.Object Loader)
        {
            global::PCL.ModDownloadLib.Service.McInstallFailedClearFolder(Loader);
        }
    }
}
