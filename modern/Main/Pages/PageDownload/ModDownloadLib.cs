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
        internal static IMinecraftInstallLoaders<ModLoader.LoaderBase, ModNet.NetFile> SharedInstallLoaders => NativeLoaders;
        private readonly static global::PCLCS.MinecraftInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ModMinecraft.McLibEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest> Service = new global::PCLCS.MinecraftInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModDownload.DlOptiFineListEntry, global::PCL.ModDownload.DlForgelikeEntry, global::PCL.ModDownload.DlForgeVersionEntry, global::PCL.ModDownload.DlNeoForgeListEntry, global::PCL.ModDownload.DlLiteLoaderListEntry, global::PCL.ModMinecraft.McLibEntry, global::PCL.ResourceVersion, global::PCL.ModDownloadLib.McInstallRequest>(global::PCL.ModDownloadLib.CreateHost());

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
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.RefreshTaskButton()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.RefreshTaskButton(pulse: true)),
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
                InstancePage = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.LauncherInstanceContext.Instance),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                SetInstancePage = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.LauncherInstanceContext.Instance = Instance),
                SetSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.ModMinecraft.McInstanceSelected = Instance),
                BeforeMinecraftChange = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.String, global::System.Threading.CancellationToken>((Instance, Before, After, Token) => global::PCL.WorldBackups.BeforeMinecraftChange(Instance, Before, After, Token)),
                CaptureEnvironment = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Threading.CancellationToken>((Instance, Reason, Token) => global::PCL.EnvironmentHistory.CaptureAutomatic(Instance, Reason, Token)),
                CleanupState = new Func<global::System.Object, global::System.Int32>(Loader => Conversions.ToInteger(((dynamic)Loader).State)),
                CleanupInput = new Func<global::System.Object, global::System.String>(Loader => Conversions.ToString(((dynamic)Loader).Input)),
                CleanupTasks = InstallCleanupTasks,
                DrainOwnedInstall = DrainOwnedInstall
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

        private static void DrainOwnedInstall(IReadOnlyList<object> tasks)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var loaders = tasks.OfType<ModLoader.LoaderBase>().ToArray();
            while (loaders.Any(loader => loader.State == ModBase.LoadState.Loading || loader.HasActiveWorkload == true)) {
                timeout.Token.ThrowIfCancellationRequested();
                Thread.Sleep(20);
            }
            var downloads = loaders.OfType<ModNet.LoaderDownload>().Where(loader => loader.State != ModBase.LoadState.Waiting);
            System.Threading.Tasks.Task.WhenAll(downloads.Select(MmcDownloads.DrainAsync)).WaitAsync(timeout.Token).GetAwaiter().GetResult();
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

        internal static MinecraftInstallMenuController<GlassInstallRow, GlassInstallRow, Action<GlassInstallRow>,
            ModDownload.DlOptiFineListEntry, ModDownload.DlForgelikeEntry, ModDownload.DlForgeVersionEntry,
            ModDownload.DlNeoForgeListEntry, ModDownload.DlLiteLoaderListEntry, ResourceVersion> InstallRows { get; } =
            GlassInstallRow.CreateMenus(Service.McDownloadOptiFineSave, Service.McDownloadForgelikeSave,
                Service.McDownloadLiteLoaderSave, Service.SaveMinecraft);

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
        public static void McUpdateLogShow(global::Newtonsoft.Json.Linq.JToken InstanceJson)
        {
            InstallRows.McUpdateLogShow(InstanceJson);
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
        private static void McDownloadLiteLoaderSave(global::PCL.ModDownload.DlLiteLoaderListEntry DownloadInfo)
        {
            global::PCL.ModDownloadLib.Service.McDownloadLiteLoaderSave(DownloadInfo);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadLiteLoaderLoader(global::PCL.ModDownload.DlLiteLoaderListEntry DownloadInfo, global::System.String McFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadLiteLoaderLoader(DownloadInfo, McFolder);
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
        public static void McDownloadForgeRecommendedRefresh()
        {
            global::PCL.ModDownloadLib.Service.McDownloadForgeRecommendedRefresh();
        }
        public static global::System.String McDownloadForgeRecommendedGet(global::System.String McVersionName)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadForgeRecommendedGet(McVersionName);
        }
        public static void McDownloadFabricLoaderSave(global::Newtonsoft.Json.Linq.JObject DownloadInfo)
        {
            global::PCL.ModDownloadLib.Service.McDownloadFabricLoaderSave(DownloadInfo);
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McDownloadFabricLoader(global::System.String FabricVersion, global::System.String MinecraftName, global::System.String McFolder)
        {
            return global::PCL.ModDownloadLib.Service.McDownloadFabricLoader(FabricVersion, MinecraftName, McFolder);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> McInstallLoadersGet(global::PCL.ModDownloadLib.McInstallRequest Request)
        {
            return global::PCL.ModDownloadLib.Service.McInstallLoadersGet(Request);
        }

        internal static MinecraftInstallPlan<ModLoader.LoaderBase> CreateModrinthGamePlan(string name, string folder, McComponents components)
            => Service.CreateModrinthGamePlan(new McInstallRequest { NewInstanceName = name, InstanceFolder = folder, Components = components });
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
