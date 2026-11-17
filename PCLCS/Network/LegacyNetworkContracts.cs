using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS.Loaders;

namespace PCLCS.Network;
// Behavior source: LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// Modules/Base/ModNet.vb (repository LICENCE); original PCL base
// 5ef81e490ae46b1100d61e33d0183094c7a80ca8. CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Modules/Network/{Facade/ModNet,
// Http/Requester,Loaders/LoaderDownload}.cs were reviewed (CE Plain Craft Launcher 2/LICENCE). Their async/adaptive engine, cancellation,
// schema and cache are not imported. LF real Thread/range/merge semantics remain.
public delegate void LegacyNetworkSignHeaders(string url, ref HttpRequestMessage request, bool simulateBrowserHeaders, bool includeCredentials);
public interface ILegacyNetworkTaskWaiter
{
    T Result<T>(Task<T> task, CancellationTokenSource source, int timeout);
    void Result(Task task, CancellationTokenSource source, int timeout);
}

public interface ILegacyNetFileTiming
{
    long CoreFileSize { get; }

    bool CoreUnknownSize { get; }

    object CoreLockChain { get; }
}

public interface ILegacyNetworkDownload<TFile>
{
    ConcurrentQueue<TFile> CoreFiles { get; set; }

    LegacyNetworkDownloadRecord CoreRecord { get; }

    int FailCount { get; set; }

    void CorePreviewFinish();
    void RefreshStat();
    void OnFileFinish(TFile file);
    void OnFileFail(TFile file);
    void OnFail(List<Exception> errors);
}

public sealed class LegacyNetworkDownloadRecord
{
    internal readonly LegacyNetworkIoCounter IoWork = new();
    public int ActiveIoProducerCount => IoWork.Active;
    internal int FileRemain;
    internal readonly object FileRemainLock = new();
    internal double Progress;
    internal int FailCount;
    // Original instance Static locals, confirmed in LF CLR4 metadata. Never global.
    internal ConcurrentDictionary<string, long>? CandidateSizes;
    internal readonly object CandidateInitialization = new();
}

public sealed class LegacyNetworkHost<TFile, TDownload, TManager, TLoader>
{
    public Func<long> Clock = null !;
    public Func<int> NewUuid = null !;
    public Func<int, int, int> RandomInteger = null !;
    public Func<bool> InUi = null !, IsProgramEnding = null !;
    public Func<string> TempFolder = null !, RequestTempFolder = null !, AppData = null !;
    public Func<string, int> IntegerSetting = null !;
    public Func<string, bool> BooleanSetting = null !;
    public Func<string, string> SignCdn = null !;
    public LegacyNetworkSignHeaders SignHeaders = null !;
    public Func<HttpMessageHandler, HttpMessageHandler> WrapRedirects = null !;
    // A lazy library factory; the actual pinned CacheCow client/store is retained.
    public Func<string, HttpMessageHandler, HttpClient> CreateCachedClient = null !;
    public ILegacyNetworkTaskWaiter Waiter = null !;
    public Func<Action, string, ThreadPriority, Thread> RunNewThread = null !;
    public Func<IEnumerable<string>> MinecraftFolders = null !;
    public Func<ConcurrentList<TLoader>> Taskbar = null !;
    public Func<TManager> Manager = null !;
    public Func<IEnumerable<string>, string, FileChecker?, bool, TFile> CreateFile = null !;
    public Func<string, IEnumerable<TFile>, TDownload> CreateDownload = null !;
    // Module fields retain their original ByRef identity; these are actual
    // atomic primitive operations on those fields, never copied counters.
    public Func<long> SpeedLimitLow = null !, SpeedLimitLeft = null !;
    public Action<long> SetSpeedLimitLow = null !;
    public Func<long, long> AddSpeedLimitLeft = null !;
    public Func<int> ThreadCount = null !;
    public Func<int> IncrementThreadCount = null !, DecrementThreadCount = null !;
    public Func<TFile, bool> AllowCredentialHeaders = null !;
    public Func<TFile, Action?> CanceledMergeFinished = null !;
    public Func<object, SslPolicyErrors, bool, bool> AcceptLoginCertificate = null !;
}
