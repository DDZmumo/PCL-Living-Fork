using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using global::System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using global::System.IO.Compression;
using System.IO.Compression;
using System.Linq;
using System.Net;
using global::System.Net.Http;
using System.Net.Http;
using System.Text;
using global::System.Threading;
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
using InstallCore = global::PCLCS.ModpackInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ResourceProject, global::PCL.ExportedVersionIsolation.Choice, global::PCL.ModDownload.DlOptiFineListEntry>;
using ThrottleDebounce;

namespace PCL
{

    // LF 原 API、Loader/Dialogs/模型与平台原语接线；格式识别、计划与实际负载由 C# 执行。
    public static class ModModpack
    {
        private readonly static global::PCLCS.ModpackInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ResourceProject, global::PCL.ExportedVersionIsolation.Choice, global::PCL.ModDownload.DlOptiFineListEntry> Service = new global::PCLCS.ModpackInstallEngine<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ResourceProject, global::PCL.ExportedVersionIsolation.Choice, global::PCL.ModDownload.DlOptiFineListEntry>(global::PCL.ModModpack.CreateHost());

        private static global::PCLCS.ModpackInstallHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ResourceProject, global::PCL.ExportedVersionIsolation.Choice, global::PCL.ModDownload.DlOptiFineListEntry> CreateHost()
        {
            return new global::PCLCS.ModpackInstallHost<global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetFile, global::PCL.ResourceVersion, global::PCL.ResourceProject, global::PCL.ExportedVersionIsolation.Choice, global::PCL.ModDownload.DlOptiFineListEntry>()
            {
                PackMode = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                BaseFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.Base),
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                RequestTempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
                SelectPackFile = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFile("选择整合包压缩文件", false, filter: new[] { (new[] { "rar", "zip", "mrpack" }, "整合包文件") }).FirstOrDefault()),
                SelectInstallFolder = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFolder("选择安装目标（必须是一个空文件夹）", false).FirstOrDefault()),
                RunInThread = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInThread(Work)),
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                Alert = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Alert)),
                Hint = new Action<global::System.String, global::System.Boolean>((Message, Red) => global::PCL.ModMain.Hint(Message, (Red) ? (global::PCL.ModMain.HintType.Red) : (global::PCL.ModMain.HintType.Blue))),
                Message = new Func<global::PCLCS.ModpackMessage, global::System.Int32>(Message => global::PCL.ModMain.MyMsgBox(Message.Text, Message.Title, Message.Button1, Message.Button2, IsWarn: Message.IsWarn, ForceWait: Message.ForceWait)),
                NameValidator = global::PCL.ModModpack.CreateNameValidator,
                WriteIni = new Action<global::System.String, global::System.String, global::System.Object>((Name, Key, Value) => global::PCL.ModBase.WriteIni(Name, Key, Conversions.ToString(Value))),
                ClearIniCache = global::PCL.ModBase.IniClearCache,
                ReadIni = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((Name, Key, DefaultValue) => global::PCL.ModBase.ReadIni(Name, Key, DefaultValue)),
                ReadIniBoolean = new Func<global::System.String, global::System.String, global::System.Boolean, global::System.Boolean>((Name, Key, DefaultValue) => Conversions.ToBoolean(global::PCL.ModBase.ReadIni(Name, Key, Conversions.ToString(DefaultValue)))),
                SettingString = new Func<global::System.String, global::System.String>(Key => global::PCL.Settings.Get<string>(Key)),
                ReadIsolation = global::PCL.ExportedVersionIsolation.ReadArchiveChoice,
                GameFolder = global::PCL.ExportedVersionIsolation.GameFolder,
                ConfigureIsolation = global::PCL.ExportedVersionIsolation.ConfigureTarget,
                CopyIsolatedOverrides = global::PCL.ExportedVersionIsolation.CopyOverrides,
                OverrideTarget = global::PCL.ExportedVersionIsolation.OverrideTarget,
                ModRequest = new Func<global::System.String, global::System.Net.Http.HttpMethod, global::System.String, global::System.String, global::Newtonsoft.Json.Linq.JToken>((Url, Method, Content, ContentType) => global::PCL.ModDownload.DlModRequest(Url, Method, Content, ContentType)),
                CurseForgeProjects = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Collections.Generic.IEnumerable<global::PCL.ResourceProject>>(Ids => global::PCL.ResourceProject.FromProjectIds(Ids, global::PCLCS.ResourcePlatforms.CurseForge)),
                ParseVersion = new Func<global::Newtonsoft.Json.Linq.JToken, global::PCLCS.ResourceTypes, global::PCL.ResourceVersion>((Raw, Type) => global::PCL.ResourceVersion.FromPlatformJson((global::Newtonsoft.Json.Linq.JObject)Raw, Type)),
                VersionToNetFile = new Func<global::PCL.ResourceVersion, global::System.String, global::System.String, global::PCLCS.ModLoaders, global::PCL.ModNet.NetFile>((Version, Target, GameVersion, Loaders) => Version.ToNetFile(Target, global::PCL.ResourceVersion.DownloadReason.ModPack, GameVersion, Loaders)),
                NetFile = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String, global::System.String, global::PCL.ModNet.NetFile>((Urls, Target, Hash) => new global::PCL.ModNet.NetFile(Urls, Target, new global::MeloongCore.FileChecker() { Hash = Hash }, true)),
                CurseForgeUrls = global::PCL.ResourceVersion.ParseCurseForgeDownloadUrls,
                TrackUrls = new Func<global::System.Collections.Generic.List<global::System.String>, global::System.String, global::PCLCS.ModLoaders, global::System.Collections.Generic.IEnumerable<global::System.String>>((Urls, GameVersion, Loaders) => global::PCL.ResourceVersion.ParseModrinthTrackArguments(Urls, global::PCL.ResourceVersion.DownloadReason.ModPack, GameVersion, Loaders)),
                ModMirror = global::PCL.ModDownload.DlSourceModGet,
                TextIntTask = new Func<global::System.String, global::System.Action<global::PCLCS.ModpackInstallTask<global::System.String, global::System.Int32>>, global::PCLCS.MinecraftDownloadLoaderSettings, global::PCL.ModLoader.LoaderBase>((Name, Work, Options) => NewTask<string, int>(Name, Work, Options)),
                IntJsonTask = new Func<global::System.String, global::System.Action<global::PCLCS.ModpackInstallTask<global::System.Int32, global::Newtonsoft.Json.Linq.JArray>>, global::PCLCS.MinecraftDownloadLoaderSettings, global::PCL.ModLoader.LoaderBase>((Name, Work, Options) => NewTask<int, JArray>(Name, Work, Options)),
                JsonFilesTask = new Func<global::System.String, global::System.Action<global::PCLCS.ModpackInstallTask<global::Newtonsoft.Json.Linq.JArray, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>>>, global::PCLCS.MinecraftDownloadLoaderSettings, global::PCL.ModLoader.LoaderBase>((Name, Work, Options) => NewTask<JArray, List<global::PCL.ModNet.NetFile>>(Name, Work, Options)),
                TextTextTask = new Func<global::System.String, global::System.Action<global::PCLCS.ModpackInstallTask<global::System.String, global::System.String>>, global::PCLCS.MinecraftDownloadLoaderSettings, global::PCL.ModLoader.LoaderBase>((Name, Work, Options) => NewTask<string, string>(Name, Work, Options)),
                TextCombo = global::PCL.ModModpack.NewTextCombo,
                IntCombo = global::PCL.ModModpack.NewIntCombo,
                Download = global::PCL.ModModpack.NewDownload,
                Weight = new Func<global::PCL.ModLoader.LoaderBase, global::System.Double>(Loader => Loader.ProgressWeight),
                InstallGame = global::PCL.ModModpack.InstallGame,
                InstallManagedMmc = global::PCL.MmcInstances.StartImport,
                TaskbarNames = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModLoader.LoaderTaskbar.Select(Loader => Loader.Name)),
                AttachInstallState = new Action<global::PCL.ModLoader.LoaderBase>(Loader => Loader.OnStateChanged = global::PCL.ModDownloadLib.McInstallState),
                AddTaskbar = new Action<global::PCL.ModLoader.LoaderBase>(Loader => global::PCL.ModLoader.LoaderTaskbarAdd((global::PCL.ModLoader.LoaderCombo<global::System.String>)Loader)),
                Start = new Action<global::PCL.ModLoader.LoaderBase, global::System.String>((Loader, Folder) => Loader.Start(Folder)),
                RefreshDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.ShowRefresh()),
                RibbleDownload = new Action(() => global::PCL.ModMain.FrmMain.BtnExtraDownload.Ribble()),
                RecordInstalled = global::PCL.ModpackUpdate.RecordInstalled,
                OptiFineFinished = new Func<global::System.Boolean>(() => (global::PCL.ModDownload.DlOptiFineListBmclapiLoader.State == global::PCL.ModBase.LoadState.Finished)),
                WaitOptiFine = new Action(() => global::PCL.ModDownload.DlOptiFineListBmclapiLoader.WaitForExit()),
                OptiFineEntries = new Func<global::System.Collections.Generic.IEnumerable<global::PCL.ModDownload.DlOptiFineListEntry>>(() => global::PCL.ModDownload.DlOptiFineListBmclapiLoader.Output.Value),
                OpenExplorer = new Action<global::System.String>(Folder => global::PCL.ModBase.OpenExplorer(Folder)),
                StartProcess = new Action<global::System.String, global::System.String>((Name, Arguments) => global::PCL.ModBase.StartProcess(Name, Arguments)),
                EndProgram = new Action<global::System.Boolean>(Value => global::PCL.ModMain.FrmMain.EndProgram(Value)),
                AddFolder = new Action<global::System.String, global::System.String, global::System.Boolean>((Folder, Name, ShowHint) => global::PCL.PageSelectLeft.AddFolder(Folder, Name, ShowHint)),
                SelectInstancePage = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSelect)),
                Sleep = global::System.Threading.Thread.Sleep
            };
        }
        private static global::PCLCS.ModpackNameValidator CreateNameValidator(global::System.String Folder)
        {
            var Rule = new global::PCL.ValidateFolderName(Folder);
            return new global::PCLCS.ModpackNameValidator()
            {
                Validate = new Func<global::System.String, global::System.String>(Value => Rule.Validate(Value)),
                Prompt = new Func<global::System.String>(() => global::PCL.ModMain.MyMsgBoxInput("输入版本名称", "", "", new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { Rule }))
            };
        }
        private static global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> InstallGame(global::System.String Name, global::System.String Folder, global::PCLCS.McComponents Components)
        {
            return global::PCL.ModDownloadLib.McInstallLoadersGet(new global::PCL.ModDownloadLib.McInstallRequest() { NewInstanceName = Name, InstanceFolder = Folder, Components = Components });
        }
        private static global::PCL.ModLoader.LoaderBase ApplyOptions(global::PCL.ModLoader.LoaderBase Loader, global::PCLCS.MinecraftDownloadLoaderSettings Options)
        {
            if (Options.ProgressWeight.HasValue)
                Loader.ProgressWeight = Options.ProgressWeight.Value;
            if (Options.Block.HasValue)
                Loader.Block = Options.Block.Value;
            if (Options.Show.HasValue)
                Loader.Show = Options.Show.Value;
            return Loader;
        }
        private static global::PCL.ModLoader.LoaderBase NewTask<TInput, TOutput>(global::System.String Name, global::System.Action<global::PCLCS.ModpackInstallTask<TInput, TOutput>> Work, global::PCLCS.MinecraftDownloadLoaderSettings Options)
        {
            return global::PCL.ModModpack.ApplyOptions(new global::PCL.ModLoader.LoaderTask<TInput, TOutput>(Name, Task => Work(new global::PCLCS.ModpackInstallTask<TInput, TOutput>()
            {
                Input = new Func<TInput>(() => Task.Input),
                Output = new Action<TOutput>(Value => Task.Output = Value),
                Cancellation = new Func<global::System.Threading.CancellationToken>(() => Task.CreateCancellationToken()),
                ProgressProvider = new Func<global::System.Double, global::MeloongCore.ProgressProvider>(ToPercentage => Task.CreateSyncProgressProvider(ToPercentage: ToPercentage)),
                Progress = new Func<global::System.Double>(() => Task.Progress),
                SetProgress = new Action<global::System.Double>(Value => Task.Progress = Value),
                Cancel = Task.Cancel
            })), Options);
        }
        private static global::PCL.ModLoader.LoaderBase NewTextCombo(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> Loaders, global::PCLCS.MinecraftDownloadLoaderSettings Options)
        {
            return global::PCL.ModModpack.ApplyOptions(new global::PCL.ModLoader.LoaderCombo<global::System.String>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Loaders), Options);
        }
        private static global::PCL.ModLoader.LoaderBase NewIntCombo(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase> Loaders, global::PCLCS.MinecraftDownloadLoaderSettings Options)
        {
            return global::PCL.ModModpack.ApplyOptions(new global::PCL.ModLoader.LoaderCombo<global::System.Int32>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Loaders), Options);
        }
        private static global::PCL.ModLoader.LoaderBase NewDownload(global::System.String Name, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::PCLCS.MinecraftDownloadLoaderSettings Options)
        {
            return global::PCL.ModModpack.ApplyOptions(new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files), Options);
        }

        public static void ModpackInstall()
        {
            global::PCL.ModModpack.Service.ModpackInstall();
        }
        public static global::PCL.ModLoader.LoaderCombo<global::System.String> ModpackInstall(global::System.String File, global::System.String InstanceName = null, global::System.String Logo = null, global::Newtonsoft.Json.Linq.JObject Source = null)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.ModpackInstall(File, InstanceName, Logo, Source);
        }
        private static void ExtractModpackFiles(global::System.String InstallTemp, global::System.String FileAddress, global::System.Threading.CancellationToken c = default(global::System.Threading.CancellationToken), global::MeloongCore.ProgressProvider p = null)
        {
            global::PCL.ModModpack.Service.ExtractModpackFiles(InstallTemp, FileAddress, c, p);
        }
        private static void CopyOverrideDirectory(global::System.String OverridesFolder, global::System.String VersionFolder, global::PCL.ModLoader.LoaderBase Loader, global::PCL.ExportedVersionIsolation.Choice Isolation = null, global::System.String GameFolder = null)
        {
            global::PCL.ModModpack.Service.CopyOverrideDirectory(OverridesFolder, VersionFolder, Loader, Isolation, GameFolder);
        }
        private static void NotifyIncompatibleLoader(global::System.String LoaderName)
        {
            global::PCL.ModModpack.Service.NotifyIncompatibleLoader(LoaderName);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackCurseForge(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder, global::System.String InstanceName = null, global::System.String Logo = null, global::Newtonsoft.Json.Linq.JObject Source = null)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackCurseForge(FileAddress, Archive, ArchiveBaseFolder, InstanceName, Logo, Source);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackModrinth(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder, global::System.String InstanceName = null, global::System.String Logo = null, global::Newtonsoft.Json.Linq.JObject Source = null)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackModrinth(FileAddress, Archive, ArchiveBaseFolder, InstanceName, Logo, Source);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackHMCL(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackHMCL(FileAddress, Archive, ArchiveBaseFolder);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackMMC(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackMMC(FileAddress, Archive, ArchiveBaseFolder);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackMCBBS(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder, global::System.String InstanceName = null)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackMCBBS(FileAddress, Archive, ArchiveBaseFolder, InstanceName);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackLauncherPack(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive, global::System.String ArchiveBaseFolder)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackLauncherPack(FileAddress, Archive, ArchiveBaseFolder);
        }
        private static global::PCL.ModLoader.LoaderCombo<global::System.String> InstallPackCompress(global::System.String FileAddress, global::System.IO.Compression.ZipArchive Archive)
        {
            return (global::PCL.ModLoader.LoaderCombo<global::System.String>)global::PCL.ModModpack.Service.InstallPackCompress(FileAddress, Archive);
        }
    }
}
