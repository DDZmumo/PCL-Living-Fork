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
public abstract class LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> : ILegacyNetFileTiming where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TDownload : TLoader, ILegacyNetworkDownload<TFile> where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState> where TNetState : struct, Enum where TLoadState : struct, Enum
{
    protected readonly LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> Runtime;
    private LegacyNetworkHost<TFile, TDownload, TManager, TLoader> Host => Runtime.Host;
    private TFile Self => (TFile)this;
    private TManager NetManager => Host.Manager();
    private HttpClient ThreadClient => Runtime.ThreadClient;
    private int NetTaskThreadCount => Host.ThreadCount();
    private int NetTaskThreadLimit => Runtime.NetTaskThreadLimit;
    private long NetTaskSpeedLimitHigh => Runtime.NetTaskSpeedLimitHigh;
    private long NetTaskSpeedLimitLeft => Host.SpeedLimitLeft();

    private static int N(TNetState value) => Convert.ToInt32(value);
    private static TNetState NV(int value) => (TNetState)Enum.ToObject(typeof(TNetState), value);
    long ILegacyNetFileTiming.CoreFileSize => FileSize;

    bool ILegacyNetFileTiming.CoreUnknownSize => IsUnknownSize;

    object ILegacyNetFileTiming.CoreLockChain => LockChain;

    public override int GetHashCode() => base.GetHashCode();
    private sealed class RangeNotSupportedException : HttpRequestException
    {
        internal RangeNotSupportedException(string message) : base(message)
        {
            checked
            {
            }
        }
    }

    // LF can cancel a prebuilt metadata placeholder before Manager.Start.
    // Only a real registration owns a count, bound to that manager instance.
    // Terminal transitions detach the exact lease under LockState; an older
    // Reset/Cancel tail must never release a later registration of this file.
    private sealed class ManagerCountLease(TManager owner)
    {
        private int released;
        internal void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Interlocked.Decrement(ref owner.FileRemain);
        }
    }
    private ManagerCountLease? managerCountLease;
    internal void RegisterManagerCount(TManager owner)
    {
        lock (LockState)
        {
            if (managerCountLease is not null)
                return;
            Interlocked.Increment(ref owner.FileRemain);
            managerCountLease = new ManagerCountLease(owner);
        }
    }
    // Called only from the original terminal state LockState critical sections.
    private ManagerCountLease? DetachManagerCount()
    {
        ManagerCountLease? lease = managerCountLease;
        managerCountLease = null;
        return lease;
    }

    public ConcurrentList<TDownload> Tasks = new ConcurrentList<TDownload>();
    public List<TSource> Sources = null !;
    private int FirstThreadSource = 0;
    public ConcurrentList<TSource> SourcesOnce = new ConcurrentList<TSource>();
    private bool Retried = false;
    private TSource GetSource(int Id = 0)
    {
        checked
        {
            if (Id >= Sources.Count || Id < 0)
                Id = 0;
            lock (LockSource)
            {
                if (HasAvailableSource(false))
                {
                    // 存在多线程可用源
                    var CurrentSource = Sources[Id];
                    while (CurrentSource.IsFailed)
                    {
                        Id += 1;
                        if (Id >= Sources.Count)
                            Id = 0;
                        CurrentSource = Sources[Id];
                    }

                    return CurrentSource;
                }
                else if (SourcesOnce.Any())
                {
                    // 仅存在单线程可用源
                    return SourcesOnce.First();
                }
                else
                {
                    // 没有可用源
                    return null !;
                }
            }
        }
    }

    public bool HasAvailableSource(bool AllowOnceSource = true)
    {
        checked
        {
            lock (LockSource)
            {
                if (Sources.Any(s => !s.IsFailed))
                    return true; // 存在多线程可用源
                if (AllowOnceSource && SourcesOnce.Any())
                    return true; // 存在单线程可用源
            }

            return false;
        }
    }

    public string LocalPath = null !;
    public string LocalName = null !;
    public TNetState State = (TNetState)Enum.ToObject(typeof(TNetState), -1);
    public List<Exception> Ex = new List<Exception>();
    internal readonly LegacyNetworkIoCounter IoWork = new();
    public int ActiveIoProducerCount => IoWork.Active;
    /// <summary>Passive phase observation inside the transfer I/O lease; subscriber failures do not change merge policy.</summary>
    public event Action? MergeStarting;
    /// <summary>Passive observation under the same lock used for share attachment and last-owner sealing.</summary>
    public event Action? LastOwnerCanceling;
    private long maximumSizeBytes = long.MaxValue;
    /// <summary>Optional caller bound; ordinary downloads retain their existing unlimited default.</summary>
    public long MaximumSizeBytes {
        get => Interlocked.Read(ref maximumSizeBytes);
        set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); Interlocked.Exchange(ref maximumSizeBytes, value); }
    }
    internal IDisposable? EnterCopyIo() { lock (LockState) return IoWork.Enter(); }
    internal bool CanShareWith(TFile requested) {
        if (MaximumSizeBytes > requested.MaximumSizeBytes ||
            (Host.AllowCredentialHeaders?.Invoke(Self) ?? true) != (Host.AllowCredentialHeaders?.Invoke(requested) ?? true)) return false;
        if (requested.Check is null) return true;
        if (Check is null) return false;
        if (requested.Check.Hash is not null && !string.Equals(Check.Hash, requested.Check.Hash, StringComparison.OrdinalIgnoreCase)) return false;
        if (requested.Check.ActualSize >= 0 && Check.ActualSize != requested.Check.ActualSize) return false;
        if (requested.Check.MinSize >= 0 && Check.MinSize < requested.Check.MinSize && Check.ActualSize < requested.Check.MinSize) return false;
        return !requested.Check.IsJson || Check.IsJson;
    }
    public TThread Threads = null !;
    public long FileSize = -2;
    public bool IsUnknownSize = false;
    public bool IsNoSplit
    {
        get
        {
            checked
            {
                return IsUnknownSize || FileSize < 1024 * 1024; // 小于 1MB 的文件不分割
            }
        }
    }

    private MemoryStream Cache = null !;
    public long DownloadDone = 0L;
    public FileChecker Check;
    public bool SimulateBrowserHeaders;
    private long SpeedLastTime;
    private long SpeedLastDone = 0L;
    public long Speed
    {
        get
        {
            checked
            {
                if (Host.Clock() - SpeedLastTime > 200L)
                {
                    long DeltaTime = Host.Clock() - SpeedLastTime;
                    _Speed = (long)Math.Round((DownloadDone - SpeedLastDone) / (DeltaTime / 1000d));
                    SpeedLastDone = DownloadDone;
                    SpeedLastTime += DeltaTime;
                }

                return _Speed;
            }
        }
    }

    private long _Speed = 0L;
    public bool IsCopy = false;
    public double Progress
    {
        get
        {
            checked
            {
                switch (N(State))
                {
                    case -1:
                    {
                        return 0d;
                    }

                    case 0:
                    {
                        return 0.01d;
                    }

                    case 1:
                    {
                        return 0.02d;
                    }

                    case 2:
                    {
                        return 0.04d;
                    }

                    case 3:
                    {
                        // 正在下载中，对应 5% ~ 98%
                        double OriginalProgress = IsUnknownSize ? 0.5d : DownloadDone / (double)Math.Max(FileSize, 1L);
                        OriginalProgress = 1d - Math.Pow(1d - OriginalProgress, 0.9d);
                        return OriginalProgress * 0.93d + 0.05d;
                    }

                    case 4:
                    {
                        return 0.99d;
                    }

                    case 5:
                    case 6:
                    {
                        return 1d;
                    }

                    default:
                    {
                        throw new ArgumentOutOfRangeException("文件状态未知：" + N(State).ToString());
                    }
                }
            }
        }
    }

    private int ConnectCount = 0;
    private long ConnectTime = 0L;
    private int ConnectAverage
    {
        get
        {
            checked
            {
                int CurrentConnectCount = ConnectCount; // 防止竞态条件
                return (int)Math.Round(CurrentConnectCount == 0 ? -1 : ConnectTime / (double)CurrentConnectCount);
            }
        }
    }

    private const long FilePieceLimit = 262144L;
    public readonly object LockState = new object ();
    public readonly object LockChain = new object ();
    public readonly object LockSource = new object ();
    public readonly int Uuid;
    public override bool Equals(object? obj)
    {
        checked
        {
            TFile? @file = obj as TFile;
            return @file is not null && Uuid == @file.Uuid;
        }
    }

    protected LegacyNetFile(LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> runtime, IEnumerable<string> Urls, string LocalPath, FileChecker Checker = null !, bool SimulateBrowserHeaders = false)
    {
        checked
        {
            Runtime = runtime;
            SpeedLastTime = Host.Clock();
            Uuid = Host.NewUuid();
            var Sources = new List<TSource>();
            int Count = 0;
            Urls = Urls.Distinct().ToArray();
            foreach (string Source in Urls)
            {
                Sources.Add(new TSource() { FailCount = 0, Url = Host.SignCdn(MeloongCore.Extensions.StringExtensions.ReplaceLineEndings(Source, "").Trim()), Id = Count, IsFailed = false, Ex = null ! });
                Count += 1;
            }

            this.Sources = Sources;
            this.LocalPath = LocalPath;
            Check = Checker;
            this.SimulateBrowserHeaders = SimulateBrowserHeaders;
            LocalName = PathUtils.GetLastPart(LocalPath);
        }
    }

    public TThread TryBeginThread()
    {
        checked
        {
            IDisposable? pendingIo = null;
            bool pendingThreadCount = false;
            try
            {
                long StartPosition;
                TSource StartSource = null !;
                System.Threading.Thread Th;
                TThread ThreadInfo;
                // 条件检测
                lock (LockSource)
                {
                    if (NetTaskThreadCount >= NetTaskThreadLimit || !HasAvailableSource() || IsNoSplit && Threads is not null && N(Threads.State) != 6)
                        return null !;
                    if (N(State) >= 4 || N(State) == -1)
                        return null !;
                    lock (LockState)
                    {
                        if (N(State) < 1)
                            State = NV(1);
                    }

                    // 初始化参数
                    // 获取线程起点与下载源
                    // 不分割
                    bool useSingleThread = IsNoSplit;
                    if (!useSingleThread && !HasAvailableSource(false))
                    {
                        if (SourcesOnce.First().SingleThread is not null && N(SourcesOnce.First().SingleThread.State) != 6)
                            return null !;
                        useSingleThread = true;
                    }

                    if (useSingleThread)
                    {
                        Reset();
                        lock (LockState) {
                            if (IoWork.IsSealed || N(State) >= 4) return null !;
                            State = NV(2);
                        }
                    }
                }

                lock (LockChain)
                {
                    // 首个开始点
                    if (Threads is null)
                    {
                        StartPosition = 0L;
                        StartSource = GetSource(FirstThreadSource);
                        FirstThreadSource = StartSource.Id + 1;
                        goto StartThread;
                    }

                    // 寻找失败点
                    foreach (TThread Thread in Threads)
                    {
                        if (N(Thread.State) == 6 && Thread.DownloadUndone > 0L)
                        {
                            StartPosition = Thread.DownloadStart + Thread.DownloadDone;
                            StartSource = GetSource(Thread.Source.Id + 1);
                            goto StartThread;
                        }
                    }

                    // 是否禁用多线程，以及规定碎片大小
                    var Source = GetSource();
                    if (Source is null)
                        return null !;
                    string TargetUrl = Source.Url;
                    if (TargetUrl.Contains("pcl2-server") || TargetUrl.Contains("meloong.com") || TargetUrl.Contains("bmclapi") || TargetUrl.Contains("github.com") || TargetUrl.Contains("optifine.net") || TargetUrl.Contains("momot.rs"))
                        return null !;
                    // 寻找最大碎片
                    // FUTURE: 下载引擎重做，计算下载源平均链接时间和线程下载速度，按最高时间节省来开启多线程
                    var FilePieceMax = Threads;
                    foreach (TThread Thread in Threads)
                    {
                        if (Thread.DownloadUndone > FilePieceMax.DownloadUndone)
                            FilePieceMax = Thread;
                    }

                    if (FilePieceMax is null || FilePieceMax.DownloadUndone < FilePieceLimit)
                        return null !;
                    StartPosition = (long)Math.Round(FilePieceMax.DownloadEnd - FilePieceMax.DownloadUndone * 0.4d);
                    StartSource = GetSource();
                    // 开始线程
                    StartThread:
                        ;
                    if (StartPosition > FileSize && FileSize >= 0L && !IsUnknownSize || StartPosition < 0L || StartSource == null)
                        return null !;
                    // 构建线程
                    int ThreadUuid = Host.NewUuid();
                    if (!Tasks.Any())
                        return null !; // 由于中断，已没有可用任务
                    Th = new System.Threading.Thread(value => this.Thread((TThread)value!))
                    {
                        Name = $"下载 {Tasks[0].Uuid}/{Uuid}/{ThreadUuid}#",
                        Priority = ThreadPriority.BelowNormal
                    };
                    ThreadInfo = new TThread()
                    {
                        Uuid = ThreadUuid,
                        DownloadStart = StartPosition,
                        Thread = Th,
                        Source = StartSource,
                        Task = Self,
                        State = NV(0)
                    };
                    // 链表处理
                    if (ThreadInfo.IsFirstThread || Threads is null)
                    {
                        Threads = ThreadInfo;
                    }
                    else
                    {
                        var CurrentChain = Threads;
                        while (CurrentChain.DownloadEnd <= StartPosition)
                            CurrentChain = CurrentChain.NextThread;
                        ThreadInfo.NextThread = CurrentChain.NextThread;
                        CurrentChain.NextThread = ThreadInfo;
                    }
                }

                // 开始线程
                lock (LockState) {
                    if (N(State) >= 4 || !Tasks.Any() || (pendingIo = IoWork.Enter()) is null) return null !;
                    ThreadInfo.IoLease = pendingIo;
                }
                Host.IncrementThreadCount();
                pendingThreadCount = true;
                lock (LockSource)
                {
                    if (!HasAvailableSource(false))
                        SourcesOnce.First().SingleThread = ThreadInfo;
                }

                Th.Start(ThreadInfo);
                pendingIo = null; // the started transfer releases after cleanup and Merge
                pendingThreadCount = false;
                return ThreadInfo;
            }
            catch (Exception ex)
            {
                pendingIo?.Dispose();
                if (pendingThreadCount) Host.DecrementThreadCount();
                Fail(new Exception($"尝试开始下载线程失败（{LocalName}）", ex));
                return null !;
            }
        }
    }

    private void Thread(TThread Th)
    {
        checked
        {
            Logger.Log($"{LocalName}：开始，起始点 {Th.DownloadStart}，{Th.Source.Url}", Th.DownloadStart == 0L ? LogLevel.Info : LogLevel.Trace);
            Stream ResultStream = null !;
            HttpRequestMessage HttpRequest = null !;
            HttpResponseMessage Response = null !;
            Stream ResponseStream = null !;
            CancellationTokenSource CancelToken = null !;
            bool ReachedEnd = false;
            long ContentLength = 0;
            void RejectUnsupportedRange()
            {
                lock (LockSource)
                {
                    if (SourcesOnce.Contains(Th.Source))
                        return;
                }

                throw new RangeNotSupportedException($"该下载源不支持分段下载：Range 起始于 {Th.DownloadStart}，预期 ContentLength 为 {FileSize - Th.DownloadStart}，返回 ContentLength 为 {ContentLength}，总文件大小 {FileSize}");
            }

            // 部分下载源真的特别慢，并且只需要一个请求，例如 Ping 为 20s，如果增长太慢，就会造成类似 2.5s 5s 7.5s 10s 12.5s... 的极大延迟
            // 延迟过长会导致某些特别慢的链接迟迟不被掐死
            int Timeout = Math.Min(Math.Max(ConnectAverage, 15000) * (1 + Th.Source.FailCount), 30000);
            Th.State = NV(1);
            try
            {
                int HttpDataCount = 0;
                if (SourcesOnce.Contains(Th.Source) && !EqualityComparer<TThread>.Default.Equals(Th, Th.Source.SingleThread))
                    goto SourceBreak;
                HttpRequest = new HttpRequestMessage(HttpMethod.Get, Th.Source.Url);
                Host.SignHeaders(Th.Source.Url, ref HttpRequest, SimulateBrowserHeaders, Host.AllowCredentialHeaders(Self));
                CancelToken = new CancellationTokenSource(Timeout);
                if (!Th.IsFirstThread)
                    HttpRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(Th.DownloadStart, default);
                Response = Host.Waiter.Result(ThreadClient.SendAsync(HttpRequest, HttpCompletionOption.ResponseHeadersRead, CancelToken.Token), CancelToken, Timeout);
                if (!Response.IsSuccessStatusCode) // 状态码检查
                {
                    throw new HttpRequestCodeException($"错误码 {Response.StatusCode} ({(int)Response.StatusCode})，{Th.Source.Url}", Response.StatusCode, null);
                }

                if (N(State) == 6)
                    goto SourceBreak; // 快速中断
                if ((Response.RequestMessage!.RequestUri!.ToString() ?? "") != (Th.Source.Url ?? ""))
                    Logger.Trace(() => $"{LocalName}：重定向至 {Response.RequestMessage!.RequestUri!}");
                // 文件大小校验
                ContentLength = Response.Content.Headers.ContentLength.GetValueOrDefault(-1);
                if (Th.IsFirstThread && ContentLength > MaximumSizeBytes) {
                    Fail(new IOException("文件超过调用方允许的最大大小。"));
                    goto SourceBreak;
                }
                if (ContentLength < 0L)
                {
                    if (FileSize > 1L)
                    {
                        if (Th.DownloadStart == 0L)
                        {
                            Logger.Info($"{LocalName}：文件大小未知，但已从其他下载源获取，不作处理");
                        }
                        else
                        {
                            Logger.Info($"{LocalName}：ContentLength 返回了 {ContentLength}，无法确定是否支持分段下载，视作不支持");
                            RejectUnsupportedRange();
                            goto SourceBreak;
                        }
                    }
                    else
                    {
                        FileSize = -1;
                        IsUnknownSize = true;
                        Logger.Info($"{LocalName}：文件大小未知");
                    }
                }
                else if (Th.IsFirstThread)
                {
                    if (Check is not null)
                    {
                        if (ContentLength < Check.MinSize && Check.MinSize > 0L)
                        {
                            throw new Exception($"文件大小不足，获取结果为 {ContentLength}，要求至少为 {Check.MinSize}。");
                        }

                        if (ContentLength != Check.ActualSize && Check.ActualSize > 0L)
                        {
                            throw new Exception($"文件大小不一致，获取结果为 {ContentLength}，要求必须为 {Check.ActualSize}。");
                        }
                    }

                    FileSize = ContentLength;
                    IsUnknownSize = false;
                    Logger.Info($"{LocalName}：文件大小 {ContentLength}（{StringUtils.FormatByteSize(ContentLength)}）");
                    // 若文件大小大于 50 M，进行剩余磁盘空间校验
                    if (ContentLength > 50 * 1024 * 1024)
                    {
                        foreach (DriveInfo Drive in DriveInfo.GetDrives())
                        {
                            string DriveName = Drive.Name.First().ToString();
                            double RequiredSpace = (Host.TempFolder().StartsWithF(DriveName) ? ContentLength * 1.1d : 0d) + (LocalPath.StartsWithF(DriveName) ? ContentLength + 5 * 1024 * 1024 : 0L);
                            if (RequiredSpace > 0d && Drive.TotalFreeSpace < RequiredSpace)
                            {
                                throw new Exception(DriveName + " 盘空间不足，无法进行下载。" + "\r\n" + "需要至少 " + StringUtils.FormatByteSize((long)Math.Round(RequiredSpace)) + " 空间，但当前仅剩余 " + StringUtils.FormatByteSize(Drive.TotalFreeSpace) + "。" + (Host.TempFolder().StartsWithF(DriveName) ? "\r\n" + "\r\n" + "下载时需要与文件同等大小的空间存放缓存，你可以在设置中调整缓存文件夹的位置。" : ""));
                            }
                        }
                    }
                }
                else if (FileSize < 0L)
                {
                    throw new Exception("非首线程运行时，尚未获取文件大小");
                }
                else if (Th.DownloadStart > 0L && ContentLength >= FileSize)
                {
                    RejectUnsupportedRange();
                    goto SourceBreak;
                }
                else if (!(FileSize - Th.DownloadStart == ContentLength))
                {
                    throw new RangeNotSupportedException($"获取到的分段大小不一致：Range 起始于 {Th.DownloadStart}，预期 ContentLength 为 {FileSize - Th.DownloadStart}，返回 ContentLength 为 {ContentLength}，总文件大小 {FileSize}");
                }

                // Log($"[Download] {LocalName} {Info.Uuid}#：通过大小检查，文件大小 {FileSize}，起始点 {Info.DownloadStart}，ContentLength {ContentLength}")
                Th.State = NV(2);
                lock (LockState)
                {
                    if (N(State) < 2)
                        State = NV(2);
                }

                // 创建缓存文件
                if (IsNoSplit)
                {
                    Th.Temp = null !;
                    Cache = new MemoryStream();
                    ResultStream = Cache;
                }
                else
                {
                    Th.Temp = $@"{Host.TempFolder()}Download\{Uuid}_{Th.Uuid}_{Host.RandomInteger(0, 999999)}.tmp";
                    ResultStream = FileUtils.CreateAsStream(Th.Temp);
                }

                // 开始下载
                CancelToken.Dispose();
                CancelToken = new CancellationTokenSource(); // 重置超时，不再让 CancellationToken 在固定时间后触发（#8488）
                ResponseStream = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                if (Host.BooleanSetting("SystemDebugDelay"))
                    System.Threading.Thread.Sleep(Host.RandomInteger(50, 3000));
                byte[] ResponseBytes = new byte[16385];
                HttpDataCount = Host.Waiter.Result(ResponseStream.ReadAsync(ResponseBytes, 0, 16384, CancelToken.Token), CancelToken, Timeout);
                while ((IsUnknownSize || Th.DownloadUndone > 0L) && HttpDataCount > 0 && !Host.IsProgramEnding() && N(State) < 4 && (!Th.Source.IsFailed || EqualityComparer<TThread>.Default.Equals(Th.Source.SingleThread, Th))) // 判断是否下载完成
                {
                    // 限速
                    while (NetTaskSpeedLimitHigh > 0L && NetTaskSpeedLimitLeft <= 0L)
                        System.Threading.Thread.Sleep(16);
                    int RealDataCount = (int)(IsUnknownSize ? HttpDataCount : Math.Min(HttpDataCount, Th.DownloadUndone));
                    if (RealDataCount > MaximumSizeBytes - Interlocked.Read(ref DownloadDone)) {
                        Fail(new IOException("文件实际下载内容超过调用方允许的最大大小。"));
                        goto SourceBreak;
                    }
                    if (NetTaskSpeedLimitHigh > 0L)
                        Host.AddSpeedLimitLeft(-RealDataCount);
                    long DeltaTime = Host.Clock() - Th.LastReceiveTime;
                    if (DeltaTime > 100000000L)
                        DeltaTime = 1L; // 避免时间刻反转导致出现极大值
                    if (RealDataCount > 0)
                    {
                        // 有数据
                        if (Th.DownloadDone == 0L)
                        {
                            // 第一次接受到数据
                            Th.State = NV(3);
                            lock (LockState)
                            {
                                if (N(State) < 3)
                                    State = NV(3);
                            }

                            Interlocked.Increment(ref ConnectCount);
                            Interlocked.Add(ref ConnectTime, Host.Clock() - Th.InitTime);
                        }

                        Th.Source.FailCount = 0;
                        foreach (var Task in Tasks)
                            Task.FailCount = 0;
                        Interlocked.Add(ref NetManager.DownloadDone, RealDataCount);
                        Interlocked.Add(ref DownloadDone, RealDataCount);
                        Th.DownloadDone += RealDataCount;
                        ResultStream.Write(ResponseBytes, 0, RealDataCount);
                        if (ResultStream is System.IO.FileStream) FileTaskProgress.RecordWrite(RealDataCount);
                        // 已完成
                        if (Th.DownloadUndone == 0L && !IsUnknownSize)
                            break;
                        // 检查速度是否过慢
                        if (Th.LastReceiveTime > 0L && DeltaTime > 5000L && DeltaTime > RealDataCount && Th.Source.SingleThread is null) // 数据包间隔大于 5s，且速度小于 1K/s
                        // 且并非单线程下载
                        {
                            throw new TimeoutException("由于速度过慢断开链接，下载 " + RealDataCount + " B，消耗 " + DeltaTime + " ms。");
                        }

                        Th.LastReceiveTime = Host.Clock();
                    }
                    else if (Th.LastReceiveTime > 0L && DeltaTime > Timeout)
                    {
                        // 无数据，且已超时
                        throw new TimeoutException("操作超时，无数据。");
                    }

                    HttpDataCount = Host.Waiter.Result(ResponseStream.ReadAsync(ResponseBytes, 0, 16384, CancelToken.Token), CancelToken, Timeout);
                }

                SourceBreak:
                    ;
                if (N(State) == 6 || Th.Source.IsFailed && !EqualityComparer<TThread>.Default.Equals(Th.Source.SingleThread, Th) || Th.DownloadUndone > 0L && !IsUnknownSize)
                {
                    // 被外部中断
                    Th.State = NV(6);
                    Logger.Info($"{LocalName}：中断");
                }
                else if (HttpDataCount == 0 && Th.DownloadUndone > 0L && !IsUnknownSize)
                {
                    // 服务器无返回数据
                    throw new Exception($"返回的 ContentLength 过多：ContentLength 为 {ContentLength}，但获取到的总数据量仅为 {Th.DownloadDone}（全文件总数据量 {DownloadDone}）");
                }
                else
                {
                    // 本线程完成
                    ReachedEnd = HttpDataCount == 0;
                    Th.State = NV(5);
                    Logger.Trace(() => $"{LocalName}：完成，已下载 {Th.DownloadDone}（{Th.DownloadStart}~{Th.DownloadEnd}）");
                }
            }
            catch (Exception ex)
            {
                if (ex.IsBadNetwork() || ex is TaskCanceledException)
                {
                    Logger.Info(new TimeoutException($"{LocalName}：超时（{Timeout}ms），{ex.Message}"));
                }
                else
                {
                    Logger.Info(ex, $"{LocalName}：出错");
                }

                SourceFail(Th, ex, false);
            }
            finally
            {
                try {
                // 释放资源
                HttpRequest?.Dispose();
                Response?.Dispose();
                ResponseStream?.Dispose();
                CancelToken?.Dispose();
                if (!IsNoSplit)
                    ResultStream?.Dispose();
                // 改变计数
                Host.DecrementThreadCount();
                // 合并
                if ((FileSize >= 0L && DownloadDone >= FileSize || FileSize == -1 && ReachedEnd && N(Th.State) == 5) && N(State) < 4 && N(Th.State) != 6)
                    Merge(Th);
                } finally { Th.IoLease?.Dispose(); }
            }
        }
    }

    private void SourceFail(TThread Th, Exception ex, bool IsMergeFailure)
    {
        checked
        {
            // 状态变更
            Interlocked.Increment(ref Th.Source.FailCount);
            foreach (var Task in Tasks)
            {
                int arglocation = Task.FailCount;
                Interlocked.Increment(ref arglocation);
                Task.FailCount = arglocation;
            }

            Th.State = NV(6);
            Th.Source.Ex = ex;
            // 根据情况判断，是否在多线程下禁用下载源（连续错误过多，或不支持断点续传）
            bool IsRangeNotSupported = ex is RangeNotSupportedException || ex.Message.Contains("(416)");
            if (IsMergeFailure || IsRangeNotSupported || ex.Message.Contains("(502)") || ex.Message.Contains("(404)") || ex.Message.Contains("未能解析") || ex.Message.Contains("无返回数据") || ex.Message.Contains("空间不足") || (ex.Message.Contains("(403)") || ex.Message.Contains("(429)")) && !Th.Source.Url.Contains("bmclapi") || Th.Source.FailCount >= NetTaskThreadLimit.Clamp(5, 30) && DownloadDone < 1L || Th.Source.FailCount > NetTaskThreadLimit + 2) // BMCLAPI 的部分源在高频率请求下会返回 403/429，所以不应因此禁用下载源
            {
                // 当一个下载源有多个线程在下载时，只选择其中一个线程进行后续处理
                bool IsThisFail = false;
                lock (LockSource)
                {
                    if (!Th.Source.IsFailed || EqualityComparer<TThread>.Default.Equals(Th.Source.SingleThread, Th))
                    {
                        IsThisFail = true;
                        Th.Source.IsFailed = true;
                    }
                }

                // ……后续处理
                if (IsThisFail)
                {
                    Logger.Info($"{LocalName}：下载源被禁用（{Th.Source.Id}，Range 问题：{IsRangeNotSupported}）：{Th.Source.Url}");
                    Logger.Warn(ex, $"{(SourcesOnce.FirstOrDefault()?.SingleThread is null ? "" : "单线程")}下载源 {Th.Source.Id} 已被禁用");
                    lock (LockSource)
                        SourcesOnce.Remove(Th.Source);
                    if (ex.Message.Contains("空间不足"))
                    {
                        // 硬盘空间不足：强制失败
                        Fail(ex);
                    }
                    else if (HasAvailableSource() && !IsMergeFailure)
                    {
                    }
                    // 当前源失败，但还有下载源：正常地继续执行
                    else if (!Retried)
                    {
                        // 合并失败或首次下载失败，未重试：将所有下载源重新标记为不允许断点续传的下载源，逐个重新尝试下载
                        // 若所有源均不支持 Range，也会走到这里重试
                        if (!IsRangeNotSupported)
                            Logger.Warn($"{LocalName}：文件下载失败，正在自动重试……");
                        Retried = true;
                        lock (LockSource)
                        {
                            SourcesOnce.Clear();
                            foreach (var Source in Sources)
                            {
                                SourcesOnce.Add(Source);
                                Source.IsFailed = true;
                            }
                        }

                        Reset();
                        lock (LockState)
                            if (!IoWork.IsSealed) State = NV(0);
                    }
                    else if (HasAvailableSource() && IsMergeFailure)
                    {
                        // 合并失败且单个源失败：继续下一个源
                        Reset();
                        lock (LockState)
                            if (!IoWork.IsSealed) State = NV(0);
                    }
                    else
                    {
                        // 失败
                        Logger.Info($"{LocalName}：已无可用下载源，下载失败");
                        Exception ExampleEx = null !;
                        lock (LockSource)
                        {
                            foreach (TSource Source in Sources)
                            {
                                Logger.Info($"已禁用的下载源：{Source.Url}");
                                if (Source.Ex is not null)
                                {
                                    ExampleEx = Source.Ex;
                                    Logger.Warn(Source.Ex, "下载源禁用原因");
                                }
                            }
                        }

                        Fail(ExampleEx);
                    }
                }
            }

            // 清理当前已下载的内容
            if (FileSize == -2)
                Reset();
        }
    }

    private void Reset()
    {
        checked
        {
            FileSize = -2;
            Cache?.Dispose();
            Cache = null !;
            lock (LockChain)
                Threads = null !;
            Interlocked.Add(ref NetManager.DownloadDone, -DownloadDone);
            DownloadDone = 0L;
            SpeedLastDone = 0L;
        }
    }

    // 最终收束事件
    private void Merge(TThread Th)
    {
        checked
        {
            // 状态判断
            lock (LockState)
            {
                if (N(State) < 4)
                {
                    State = NV(4);
                }
                else
                {
                    return;
                }
            }

            int RetryCount = 0;
            try
            {
                try { MergeStarting?.Invoke(); } catch (Exception observer) { Logger.Warn(observer, "下载合并阶段观察回调失败"); }
                while (true)
                {
                    try
                    {
                        // 创建文件夹
                        DirectoryUtils.Create(PathUtils.RemoveLastPart(LocalPath));
                        FileUtils.Delete(LocalPath);
                        lock (LockChain)
                        {
                            // 合并文件
                            if (IsNoSplit)
                            {
                                // 仅有一个线程，从内存中输出
                                Logger.Trace(() => $"{LocalName}：下载结束，从内存输出文件");
                                FileUtils.Write(LocalPath, Cache);
                            }
                            else if (Threads.DownloadDone == DownloadDone && Threads.Temp is not null)
                            {
                                // 仅有一个文件，直接复制
                                Logger.Trace(() => $"{LocalName}：下载结束，仅有一个文件，无需合并");
                                FileUtils.Copy(Threads.Temp, LocalPath);
                            }
                            else
                            {
                                // 有多个线程，合并
                                Logger.Trace(() => $"{LocalName}：下载结束，开始合并文件");
                                using (var MergeFile = FileUtils.CreateAsStream(LocalPath))
                                {
                                    using (var AddWriter = new BinaryWriter(MergeFile))
                                    {
                                        foreach (TThread Thread in Threads)
                                        {
                                            if (Thread.DownloadDone == 0L || Thread.Temp is null)
                                                continue;
                                            using (var fs = FileUtils.ReadAsStream(Thread.Temp))
                                            {
                                                using (var TempReader = new BinaryReader(fs))
                                                {
                                                    AddWriter.Write(TempReader.ReadBytes((int)Thread.DownloadDone));
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            // 检查文件
                            string? CheckResult = Check?.Check(LocalPath);
                            if (CheckResult is null && !IsUnknownSize && Check is not null)
                            {
                                if (Check.ActualSize == -1)
                                {
                                    CheckResult = new FileChecker()
                                    {
                                        ActualSize = FileSize
                                    }.Check(LocalPath); // 不修改原始的 Checker，以免污染原始实例
                                }
                                else if (Check.ActualSize != FileSize)
                                {
                                    CheckResult = $"文件大小不一致：任务校验要求为 {Check.ActualSize}，请求结果为 {FileSize}";
                                }
                            }

                            if (CheckResult is not null)
                            {
                                Logger.Info($"{LocalName} 文件校验失败，下载线程细节：");
                                foreach (TThread T in Threads)
                                    Logger.Info($"- {T.Uuid}#，状态 {T.State}，范围 {T.DownloadStart}~{T.DownloadEnd}，已下载 {T.DownloadDone}，未下载 {T.DownloadUndone}");
                                throw new Exception(CheckResult);
                            }

                            // 后处理
                            if (!IsNoSplit)
                            {
                                foreach (TThread Thread in Threads)
                                {
                                    if (Thread.Temp is not null)
                                        FileUtils.Delete(Thread.Temp);
                                }
                            }

                            Finish();
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        RetryCount += 1;
                        if (N(State) > 4)
                            return; // 通常是因为在合并时被中断了
                        Logger.Warn(ex, $"合并文件出错，第 {RetryCount} 次尝试（{LocalName}）");
                        // 重新尝试合并
                        if (RetryCount < 3)
                        {
                            System.Threading.Thread.Sleep(500 * RetryCount);
                            continue;
                        }

                        // 失败，禁用当前下载源并重启下载
                        try
                        {
                            FileUtils.Delete(LocalPath);
                        }
                        catch (Exception)
                        {
                            Logger.Warn(ex, $"删除合并失败的文件出错（{LocalPath}）");
                        }

                        SourceFail(Th, ex, true);
                        break;
                    }
                }
            }
            finally
            {
                Cache?.Dispose();
                if (N(State) == 6 && Host.CanceledMergeFinished(Self)is not null)
                {
                    try
                    {
                        Host.CanceledMergeFinished(Self)!.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "清理已取消下载的暂存文件失败");
                    }
                }
            }
        }
    }

    private void Fail(Exception RaiseEx = null !)
    {
        checked
        {
            ManagerCountLease? lease;
            lock (LockState)
            {
                if (N(State) >= 5)
                    return;
                if (RaiseEx is not null)
                    Ex.Add(RaiseEx);
                // 凉凉
                Logger.Info($"{LocalName}：已失败，当前状态 {State}");
                State = NV(6);
                IoWork.Seal();
                lease = DetachManagerCount();
            }

            CancelInternal(lease);
            foreach (var Task in Tasks)
                Task.OnFileFail(Self);
        }
    }

    public void Cancel(TDownload CanceledTask)
    {
        checked
        {
            // 从特定任务中移除，如果它还属于其他任务，则继续下载
            ManagerCountLease? lease;
            lock (LockState)
            {
                Tasks.Remove(CanceledTask);
                if (Tasks.Any()) return;
                if (N(State) >= 5)
                    return;
                try { LastOwnerCanceling?.Invoke(); } catch (Exception observer) { Logger.Warn(observer, "下载归属取消观察回调失败"); }
                Logger.Info($"{LocalName}：已中断，当前状态 {State}");
                State = NV(6);
                IoWork.Seal();
                lease = DetachManagerCount();
            }

            CancelInternal(lease);
        }
    }

    private void CancelInternal(ManagerCountLease? lease)
    {
        checked
        {
            // Original caller-scoped On Error Resume Next: a failure of Reset
            // resumes at the decrement, rather than inside the Reset procedure.
            try
            {
                Reset();
            }
            catch
            {
            }

            try
            {
                lease?.Release();
            }
            catch
            {
            }
        }
    }

    // 状态改变接口
    public void Finish(bool PrintLog = true)
    {
        checked
        {
            ManagerCountLease? lease;
            lock (LockState)
            {
                if (N(State) >= 5)
                    return;
                State = NV(5);
                IoWork.Seal();
                lease = DetachManagerCount();
            }

            lease?.Release();
            if (PrintLog)
                Logger.Info($"{LocalName}：已完成");
            foreach (var Task in Tasks)
                Task.OnFileFinish(Self);
        }
    }
}
