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
public abstract class LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TDownload : TLoader, ILegacyNetworkDownload<TFile> where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState> where TNetState : struct, Enum where TLoadState : struct, Enum
{
    private readonly LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> Runtime;
    private LegacyNetworkHost<TFile, TDownload, TManager, TLoader> Host => Runtime.Host;

    protected LegacyNetManager(LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> runtime)
    {
        checked
        {
            Runtime = runtime;
            Uuid = Host.NewUuid();
        }
    }

    private static int N(TNetState value) => Convert.ToInt32(value);
    private long NetTaskSpeedLimitLow { get => Host.SpeedLimitLow(); set => Host.SetSpeedLimitLow(value); }
    private long NetTaskSpeedLimitHigh => Runtime.NetTaskSpeedLimitHigh;
    private int NetTaskThreadCount => Host.ThreadCount();
    private int NetTaskThreadLimit => Runtime.NetTaskThreadLimit;

    private Thread RunInNewThread(Action work, string name, ThreadPriority priority = ThreadPriority.Normal) => Host.RunNewThread(work, name, priority);
    // Instance Static locals, confirmed against actual LF CLR4 field attributes.
    private List<long>? speedLast;
    private List<long> _RefreshStat_SpeedLast => speedLast ??= new List<long>();

    private long _RefreshStat_SpeedLastDone;
    private bool _StartManager_IsStarted;
    public ConcurrentDictionary<string, TFile> AllFiles = new ConcurrentDictionary<string, TFile>();
    public ConcurrentBag<TDownload> Tasks = new ConcurrentBag<TDownload>();
    public long DownloadDone = 0L;
    public int FileRemain = 0;
    // 这些属性由 RefreshStat 刷新
    public long Speed = 0L;
    public readonly int Uuid;
    private void RefreshStat()
    {
        checked
        {
            try
            {
                long DeltaTime = Host.Clock() - RefreshStatLast;
                if (DeltaTime == 0L)
                    return;
                RefreshStatLast += DeltaTime;
                double ActualSpeed = Math.Max(0d, (DownloadDone - _RefreshStat_SpeedLastDone) / (DeltaTime / 1000d));
                _RefreshStat_SpeedLast.Insert(0, (long)Math.Round(ActualSpeed));
                if (_RefreshStat_SpeedLast.Count >= 31)
                    _RefreshStat_SpeedLast.RemoveAt(30);
                _RefreshStat_SpeedLastDone = DownloadDone;
                // 计算用于显示的速度
                long SpeedSum = 0L;
                long SpeedDiv = 0L;
                int Weight = _RefreshStat_SpeedLast.Count;
                foreach (var SpeedRecord in _RefreshStat_SpeedLast)
                {
                    SpeedSum += SpeedRecord * Weight;
                    SpeedDiv += Weight;
                    Weight -= 1;
                }

                Speed = (long)Math.Round(SpeedDiv > 0L ? SpeedSum / (double)SpeedDiv : 0d);
                // 计算新的速度下限
                long Limit = 0L;
                if (_RefreshStat_SpeedLast.Count >= 10)
                    Limit = (long)Math.Round(_RefreshStat_SpeedLast.Take(10).Average() * 0.85d); // 取近 1 秒的平均速度的 85%
                if (Limit > NetTaskSpeedLimitLow)
                {
                    NetTaskSpeedLimitLow = Limit;
                    Logger.Info($"速度下限已提升到 {StringUtils.FormatByteSize(Limit)}");
                }

                foreach (var Task in Tasks)
                    Task.RefreshStat();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "刷新下载公开属性失败");
            }
        }
    }

    private long RefreshStatLast;
    private void StartManager()
    {
        checked
        {
            if (_StartManager_IsStarted)
                return;
            _StartManager_IsStarted = true;
            // 获取文件列表
            // 为等待中的文件开始线程
            // 为进行中的文件追加线程
            // 线程种类计数
            // 新增线程
            void ThreadStarter(int Id)
            {
                try
                {
                    while (true)
                    {
                        Thread.Sleep(20);
                        var WaitingFiles = new List<TFile>();
                        var OngoingFiles = new List<TFile>();
                        if (Id == 0 && FileRemain == 0)
                            AllFiles.Clear();
                        foreach (TFile File in AllFiles.Values)
                        {
                            if (File.Uuid % 2 == Id)
                                continue;
                            if (N(File.State) == 0)
                            {
                                WaitingFiles.Add(File);
                            }
                            else if (N(File.State) < 4 && N(File.State) >= 0)
                            {
                                OngoingFiles.Add(File);
                            }
                        }

                        bool continueWhile = false;
                        foreach (TFile File in WaitingFiles)
                        {
                            if (NetTaskThreadCount >= NetTaskThreadLimit)
                            {
                                continueWhile = true;
                                break;
                            }

                            var NewThread = File.TryBeginThread();
                            if (NewThread is not null && NewThread.Source.Url.Contains("bmclapi"))
                                Thread.Sleep(100);
                        }

                        if (continueWhile)
                        {
                            continue;
                        }

                        if (Speed >= NetTaskSpeedLimitLow)
                            continue;
                        bool continueWhile1 = false;
                        foreach (TFile File in OngoingFiles)
                        {
                            if (NetTaskThreadCount >= NetTaskThreadLimit)
                            {
                                continueWhile1 = true;
                                break;
                            }

                            int PreparingCount = 0;
                            int DownloadingCount = 0;
                            if (File.Threads is not null)
                            {
                                foreach (TThread Thread in File.Threads.ToList())
                                {
                                    if (N(Thread.State) < 3)
                                    {
                                        PreparingCount += 1;
                                    }
                                    else if (N(Thread.State) == 3)
                                    {
                                        DownloadingCount += 1;
                                    }
                                }
                            }

                            if (PreparingCount > DownloadingCount)
                                continue;
                            var NewThread = File.TryBeginThread();
                            if (NewThread is not null && NewThread.Source.Url.Contains("bmclapi"))
                                Thread.Sleep(100);
                        }

                        if (continueWhile1)
                        {
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"下载管理启动线程 {Id} 出错", LogBehavior.AlertThenCrash);
                }
            }; // 0 或 1
            // 若已完成，则清空
            // 最大线程数检查
            // 减少 BMCLAPI 请求频率
            // 下载速度足够，无需新增
            // 最大线程数检查
            // 准备中的线程已多于下载中的线程，不再新增
            // 减少 BMCLAPI 请求频率
            RunInNewThread(() => ThreadStarter(0), "NetManager ThreadStarter 0");
            RunInNewThread(() => ThreadStarter(1), "NetManager ThreadStarter 1");
            // 增加限速余量
            // 刷新公开属性
            // 等待 100 ms
            RunInNewThread(() =>
            {
                try
                {
                    long NextTick = Host.Clock();
                    while (true)
                    {
                        if (NetTaskSpeedLimitHigh > 0L)
                            Host.AddSpeedLimitLeft(Convert.ToInt64(NetTaskSpeedLimitHigh / 10d));
                        RefreshStat();
                        NextTick += 100L;
                        long SleepTime = NextTick - Host.Clock();
                        if (SleepTime > 0L)
                        {
                            Thread.Sleep((int)SleepTime);
                        }
                        else
                        {
                            NextTick = Host.Clock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "下载管理刷新线程出错", LogBehavior.AlertThenCrash);
                }
            }, "NetManager StatRefresher"); // 超时，直接追帧，不等待
        }
    }

    private object DownloadCacheLock = new object ();
    private readonly object RegistrationLock = new();
    private bool IsDownloadCacheCleared = false;
    public List<TFile> Start(TDownload Task)
    {
        checked
        {
            StartManager();
            // 清理缓存
            lock (DownloadCacheLock) // 防止同时开启多个下载任务时重复清理
            {
                if (!IsDownloadCacheCleared)
                {
                    try
                    {
                        Logger.Info("开始清理下载缓存");
                        DirectoryUtils.Delete(Host.TempFolder() + "Download");
                        Logger.Info("下载缓存已清理");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "清理下载缓存失败");
                    }

                    IsDownloadCacheCleared = true;
                }
            }

            // CancelDownload uses this same owner lock for its terminal state.
            // Keep cache IO outside it; only real registration is serialized.
            lock (Task.LockState)
            {
                if (Convert.ToInt32(Task.State) >= 2)
                    return new List<TFile>();
                // 添加每个文件
                var NewFiles = new List<TFile>();
                TFile[] inputs = Task.CoreFiles.ToArray();
                Task.CoreFiles = new ConcurrentQueue<TFile>();
                lock (RegistrationLock)
                foreach (var inputFile in inputs)
                {
                    TFile File = inputFile;
                    TFile? OngoingFile = null;
                    bool attached = false;
                    if (AllFiles.TryGetValue(File.LocalPath, out OngoingFile)) {
                        lock (OngoingFile.LockState) {
                            if (N(OngoingFile.State) < 5 && !OngoingFile.IoWork.IsSealed) {
                                if (!OngoingFile.CanShareWith(File)) throw new InvalidOperationException("An active download at this target has incompatible size, credential or checker constraints.");
                                OngoingFile.Tasks.Add(Task); File = OngoingFile; attached = true;
                            } else if (OngoingFile.IoWork.Active != 0) throw new InvalidOperationException("The previous target writer is still draining; retry after its completion barrier.");
                        }
                    }
                    if (!attached)
                    {
                        lock (File.LockState) {
                            if (File.IoWork.IsSealed) throw new InvalidOperationException("NetFile cannot be reused across terminal operations.");
                            AllFiles[File.LocalPath] = File;
                            NewFiles.Add(File); File.RegisterManagerCount((TManager)this); File.Tasks.Add(Task);
                        }
                    }
                    Task.CoreFiles.Enqueue(File);
                }
                Tasks.Add(Task);
                return NewFiles;
            }
        }
    }
}
