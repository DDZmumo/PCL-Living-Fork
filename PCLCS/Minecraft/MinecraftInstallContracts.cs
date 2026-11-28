using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>Single field storage closed over the original LF model classes.</summary>
public class MinecraftInstallRequestData<TForge, TNeo, TResource, TLite>
    where TForge : class where TNeo : class where TResource : class where TLite : class {
    public string NewInstanceName = null!, InstanceFolder = null!;
    public McComponents Components = null!;
    public string? MinecraftJson;
    public TForge? ForgeEntry;
    public TNeo? NeoForgeEntry;
    public TResource? FabricApi, OptiFabric;
    public TLite? LiteLoaderEntry;
    public bool IsIntermediateOutput;
    public string? MinecraftFolder;
}
public sealed record MinecraftInstallPlan<TLoader>(TLoader PrepareDirectory, List<TLoader> GameLoaders, TLoader? FinishDirectory)
    where TLoader : class;

/// <summary>Live accessors for the native LoaderTask. No input/output/progress/state is copied.</summary>
public sealed class MinecraftInstallTask<TInput, TOutput, TLoader> {
    public TLoader Native = default!;
    public Func<TInput> ReadInput = null!;
    public Func<TOutput> ReadOutput = null!;
    public Action<TOutput> WriteOutput = null!;
    public Func<double> ReadProgress = null!;
    public Action<double> WriteProgress = null!;
    public Func<bool> ReadCanceled = null!, ReadCanceledState = null!;
    public Func<CancellationToken> Cancellation = null!;
    public Func<double, double, ProgressProvider> ProgressProvider = null!;
    public Action Cancel = null!;
    public TInput Input => ReadInput();
    public TOutput Output { get => ReadOutput(); set => WriteOutput(value); }
    public double Progress { get => ReadProgress(); set => WriteProgress(value); }
    public bool IsCanceled => ReadCanceled();
    public bool IsCanceledState => ReadCanceledState();
}
public sealed class MinecraftInstallStates {
    public int Waiting, Loading, Finished, Failed, Canceled;
}
/// <summary>Generic construction maps directly to the real LF LoaderTask/Combo/Download implementation.</summary>
public interface IMinecraftInstallLoaders<TLoader, TNetFile> {
    TLoader NewTask<TInput, TOutput>(string name, Action<MinecraftInstallTask<TInput, TOutput, TLoader>> work, MinecraftDownloadLoaderSettings options);
    MinecraftInstallTask<TInput, TOutput, TLoader> WrapTask<TInput, TOutput>(TLoader loader);
    TLoader Combo<TInput>(string name, List<TLoader> steps, MinecraftDownloadLoaderSettings options);
    TLoader Download(string name, List<TNetFile> files, MinecraftDownloadLoaderSettings options);
    void Start<TInput>(TLoader loader, TInput input);
    void AddTaskbar<TInput>(TLoader loader);
    void OnStateChanged(TLoader loader, Action<TLoader> changed);
    string Name(TLoader loader);
    int State(TLoader loader);
    Exception? Error(TLoader loader);
    IEnumerable<TLoader> Children(TLoader loader);
    IEnumerable<string> TaskbarNames();
}
public sealed class MinecraftInstallHost<TLoader, TNetFile, TInstance, TForge, TNeo, TResource, TLib>
    where TLoader : class where TNetFile : class where TInstance : class where TLib : LegacyMinecraftLibrary {
    public IMinecraftInstallLoaders<TLoader, TNetFile> Loaders = null!;
    public MinecraftInstallStates States = null!;
    public Func<string> MinecraftFolder = null!, TempFolder = null!, PureFolder = null!;
    public Func<bool, string> RequestTempFolder = null!;
    public Func<string, TInstance> NewInstance = null!;
    public Func<TInstance, List<TNetFile>> LibraryFiles = null!;
    public Func<TInstance, bool, TLoader, List<TNetFile>> AssetFiles = null!;
    public Func<TInstance, TNetFile?> AssetIndex = null!;
    public Func<TInstance, bool, TNetFile?> ClientJar = null!;
    public Func<string, string?> ClientJsonUrl = null!;
    public Func<string?, IEnumerable<string>> LauncherUrls = null!;
    public Func<IEnumerable<string>, string, MinecraftDownloadCheck?, TNetFile> NetFile = null!;
    public Func<TNetFile, string> LocalPath = null!;
    public Func<TResource, string, string, ModLoaders, TNetFile> ResourceNetFile = null!;
    public Func<MinecraftDownloadRequest, string> RetryRequest = null!;
    public Func<IEnumerable<string>, bool, string> LoaderRequest = null!;
    public Func<string, TNeo> NeoForge = null!;
    public Func<string, List<TForge>> ForgeList = null!;
    public Func<JObject, bool, string, List<TLib>> ReadLibraryList = null!;
    public Func<List<TLib>, string, List<TNetFile>> LibraryNetFiles = null!;
    public Func<string, string, string> LibraryPath = null!;
    public Action<string> CreateLauncherProfiles = null!;
    public Func<ValueRange<Version>, CancellationToken, ProgressProvider, Java?> SelectJava = null!;
    public Func<string, bool> BooleanSetting = null!;
    public Action<string, string> ExtractResources = null!;
    public Action<Action> RunInThread = null!, RunInUi = null!, RunInUiWait = null!;
    public Action<Action, string> RunInNewThread = null!;
    public Action RefreshDownload = null!, RibbleDownload = null!, RefreshInstanceList = null!;
    public Action<string, int> Hint = null!;
    public Func<ModpackMessage, int> Message = null!;
    public Func<string, string, string, string?> SaveFile = null!;
    public Func<string?> SelectFolder = null!;
    public Action<string> OpenWebsite = null!;
    public Action<string, string, object> WriteIni = null!;
    public Func<TInstance, string> NativesFolder = null!;
    public Func<TLoader, string, Action> FileWorkload = null!;
    public Func<IWorker, string, bool> TryAcquire = null!;
    public Func<TInstance?> InstancePage = null!, SelectedInstance = null!;
    public Action<TInstance> SetInstancePage = null!, SetSelectedInstance = null!;
    public Action<TInstance, string, string, CancellationToken> BeforeMinecraftChange = null!;
    public Action<TInstance, string, CancellationToken> CaptureEnvironment = null!;
    // The original untyped public cleanup API accepts the same native loaders.
    public Func<object, int> CleanupState = null!;
    public Func<object, string> CleanupInput = null!;
    public Func<object, IEnumerable<object>> CleanupTasks = _ => Array.Empty<object>();
    public Action<IReadOnlyList<object>> DrainOwnedInstall = _ => throw new InvalidOperationException("普通安装缺少文件工作清理入口。");
}
