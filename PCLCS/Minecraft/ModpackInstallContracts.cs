using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>Every operation addresses the existing live LoaderTask; input, output and progress have no mirrors.</summary>
public sealed class ModpackInstallTask<TInput, TOutput> {
    public Func<TInput> Input = null!;
    public Action<TOutput> Output = null!;
    public Func<CancellationToken> Cancellation = null!;
    public Func<double, ProgressProvider> ProgressProvider = null!;
    public Func<double> Progress = null!;
    public Action<double> SetProgress = null!;
    public Action Cancel = null!;
}
public sealed class ModpackNameValidator {
    public Func<string?, string> Validate = null!;
    public Func<string?> Prompt = null!;
}
public sealed class ModpackMessage {
    public string Text = null!, Title = "提示", Button1 = "确定", Button2 = "";
    public bool IsWarn, ForceWait;
}
/// <summary>Original source and selected host context. Component/patch semantics are never flattened here.</summary>
public sealed record ModpackMmcInstallRequest(string ArchivePath, string MinecraftFolder, string? InstanceName = null,
    string? LogoPath = null, JObject? PlatformSource = null);
/// <summary>Actual LF models, loader constructors and platform primitives. All pack decisions and workloads are C#.</summary>
public sealed class ModpackInstallHost<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine>
    where TLoader : class where TNetFile : class where TVersion : ResourceVersionModelData
    where TProject : ResourceProjectModelData where TIsolation : InstanceCloneExportChoiceData
    where TOptiFine : MinecraftOptiFineEntryData {
    public Func<bool> PackMode = null!;
    public Func<string> BaseFolder = null!, MinecraftFolder = null!, RequestTempFolder = null!;
    public Func<string?> SelectPackFile = null!, SelectInstallFolder = null!;
    public Action<Action> RunInThread = null!, RunInUi = null!;
    public Action<Exception, string> Alert = null!;
    public Action<string, bool> Hint = null!;
    public Func<ModpackMessage, int> Message = null!;
    public Func<string, ModpackNameValidator> NameValidator = null!;
    public Action<string, string, object> WriteIni = null!;
    public Action<string> ClearIniCache = null!;
    public Func<string, string, string?, string?> ReadIni = null!;
    public Func<string, string, bool, bool> ReadIniBoolean = null!;
    public Func<string, string> SettingString = null!;
    public Func<ZipArchive, string, TIsolation?> ReadIsolation = null!;
    public Func<string, string, TIsolation?, string> GameFolder = null!;
    public Action<string, TIsolation> ConfigureIsolation = null!;
    public Action<string, string, string, TIsolation> CopyIsolatedOverrides = null!;
    public Func<string, string, string, TIsolation, string> OverrideTarget = null!;
    public Func<string, HttpMethod?, string?, string?, JToken?> ModRequest = null!;
    public Func<IEnumerable<string>, IEnumerable<TProject>> CurseForgeProjects = null!;
    public Func<JToken, ResourceTypes, TVersion> ParseVersion = null!;
    public Func<TVersion, string, string, ModLoaders, TNetFile> VersionToNetFile = null!;
    public Func<IEnumerable<string>, string, string, TNetFile> NetFile = null!;
    public Func<string, IEnumerable<string>> CurseForgeUrls = null!;
    public Func<List<string>, string, ModLoaders, IEnumerable<string>> TrackUrls = null!;
    public Func<string, string> ModMirror = null!;
    public Func<string, Action<ModpackInstallTask<string, int>>, MinecraftDownloadLoaderSettings, TLoader> TextIntTask = null!;
    public Func<string, Action<ModpackInstallTask<int, JArray>>, MinecraftDownloadLoaderSettings, TLoader> IntJsonTask = null!;
    public Func<string, Action<ModpackInstallTask<JArray, List<TNetFile>>>, MinecraftDownloadLoaderSettings, TLoader> JsonFilesTask = null!;
    public Func<string, Action<ModpackInstallTask<string, string>>, MinecraftDownloadLoaderSettings, TLoader> TextTextTask = null!;
    public Func<string, List<TLoader>, MinecraftDownloadLoaderSettings, TLoader> TextCombo = null!, IntCombo = null!;
    public Func<string, List<TNetFile>, MinecraftDownloadLoaderSettings, TLoader> Download = null!;
    public Func<TLoader, double> Weight = null!;
    public Func<string, string, McComponents, List<TLoader>> InstallGame = null!;
    // Production Main supplies this; null exists only for explicitly retained legacy host/oracle tests.
    public Func<ModpackMmcInstallRequest, TLoader>? InstallManagedMmc;
    public Func<IEnumerable<string>> TaskbarNames = null!;
    public Action<TLoader> AttachInstallState = null!, AddTaskbar = null!;
    public Action<TLoader, string> Start = null!;
    public Action RefreshDownload = null!, RibbleDownload = null!;
    public Action<string, string, JObject?> RecordInstalled = null!;
    public Func<bool> OptiFineFinished = null!;
    public Action WaitOptiFine = null!;
    public Func<IEnumerable<TOptiFine>> OptiFineEntries = null!;
    public Action<string> OpenExplorer = null!;
    public Action<string, string> StartProcess = null!;
    public Action<bool> EndProgram = null!;
    public Action<string, string, bool> AddFolder = null!;
    public Action SelectInstancePage = null!;
    public Action<int> Sleep = null!;
}
