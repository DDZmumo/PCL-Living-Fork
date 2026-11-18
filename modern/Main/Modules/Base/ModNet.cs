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
using global::System.Net.Sockets;
using System.Runtime.CompilerServices;
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
using global::PCLCS.Network;
using NetFileCore = global::PCLCS.Network.LegacyNetFile<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>;
using NetManagerCore = global::PCLCS.Network.LegacyNetManager<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>;
using NetworkCore = global::PCLCS.Network.LegacyNetworkRuntime<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>;
using ThrottleDebounce;

namespace PCL
{

    // 原模块、旧嵌套类型/枚举/字段身份和真实宿主边界；完整网络/下载业务在 C#。
    public static class ModNet
    {
        public static global::System.Int64 NetTaskSpeedLimitLow = (256L * (1024L));
        public static global::System.Int64 NetTaskSpeedLimitLeft = (global::System.Int64)(-(1));
        public static global::System.Int32 NetTaskThreadCount = 0;

        private readonly static global::PCLCS.Network.LegacyNetworkHost<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase> NativeHost = new global::PCLCS.Network.LegacyNetworkHost<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase>()
        {
            Clock = new Func<global::System.Int64>(() => global::PCL.ModBase.GetTimeMs()),
            NewUuid = new Func<global::System.Int32>(() => global::PCL.ModBase.GetUuid()),
            RandomInteger = new Func<global::System.Int32, global::System.Int32, global::System.Int32>((Min, Max) => global::PCL.ModBase.RandomInteger(Min, Max)),
            InUi = new Func<global::System.Boolean>(() => global::PCL.ModBase.RunInUi()),
            IsProgramEnding = new Func<global::System.Boolean>(() => global::PCL.ModBase.IsProgramEnding),
            TempFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
            RequestTempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
            AppData = new Func<global::System.String>(() => global::MeloongCore.Paths.AppData),
            IntegerSetting = new Func<global::System.String, global::System.Int32>(Key => global::PCL.Settings.Get<int>(Key)),
            BooleanSetting = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)),
            SignCdn = global::PCL.ModSecret.SecretCdnSign,
            SignHeaders = global::PCL.ModSecret.SecretHeadersSign,
            WrapRedirects = new Func<global::System.Net.Http.HttpMessageHandler, global::System.Net.Http.HttpMessageHandler>(Transport => new global::PCL.SafeRedirectHandler(Transport)),
            CreateCachedClient = global::PCL.ModNet.CreateNativeCachedClient,
            Waiter = new global::PCL.ModNet.NativeTaskWaiter(),
            RunNewThread = new Func<global::System.Action, global::System.String, global::System.Threading.ThreadPriority, global::System.Threading.Thread>((Work, Name, Priority) => global::PCL.ModBase.RunInNewThread(Work, Name, Priority)),
            MinecraftFolders = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModMinecraft.McFolderList.Select(Folder => Folder.Location)),
            Taskbar = new Func<global::MeloongCore.ConcurrentList<global::PCL.ModLoader.LoaderBase>>(() => global::PCL.ModLoader.LoaderTaskbar),
            Manager = new Func<global::PCL.ModNet.NetManagerClass>(() => global::PCL.ModNet.NetManager),
            CreateFile = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String, global::MeloongCore.FileChecker, global::System.Boolean, global::PCL.ModNet.NetFile>((Urls, Path, Check, Browser) => new global::PCL.ModNet.NetFile(Urls, Path, Check, Browser)),
            CreateDownload = new Func<global::System.String, global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>, global::PCL.ModNet.LoaderDownload>((Name, Files) => new global::PCL.ModNet.LoaderDownload(Name, Files)),
            SpeedLimitLow = new Func<global::System.Int64>(() => global::PCL.ModNet.NetTaskSpeedLimitLow),
            SetSpeedLimitLow = new Action<global::System.Int64>(Value => global::PCL.ModNet.NetTaskSpeedLimitLow = Value),
            SpeedLimitLeft = new Func<global::System.Int64>(() => global::PCL.ModNet.NetTaskSpeedLimitLeft),
            AddSpeedLimitLeft = new Func<global::System.Int64, global::System.Int64>(Delta => global::System.Threading.Interlocked.Add(ref global::PCL.ModNet.NetTaskSpeedLimitLeft, Delta)),
            ThreadCount = new Func<global::System.Int32>(() => global::PCL.ModNet.NetTaskThreadCount),
            IncrementThreadCount = new Func<global::System.Int32>(() => global::System.Threading.Interlocked.Increment(ref global::PCL.ModNet.NetTaskThreadCount)),
            DecrementThreadCount = new Func<global::System.Int32>(() => global::System.Threading.Interlocked.Decrement(ref global::PCL.ModNet.NetTaskThreadCount)),
            AllowCredentialHeaders = new Func<global::PCL.ModNet.NetFile, global::System.Boolean>(File => File.AllowCredentialHeaders),
            CanceledMergeFinished = new Func<global::PCL.ModNet.NetFile, global::System.Action>(File => File.CanceledMergeFinished),
            AcceptLoginCertificate = global::PCL.LoginCertificatePolicy.Accept
        };
        private readonly static global::PCLCS.Network.LegacyNetworkRuntime<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState> NativeRuntime = new global::PCLCS.Network.LegacyNetworkRuntime<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>(global::PCL.ModNet.NativeHost);
        public static global::PCL.ModNet.NetManagerClass NetManager = new global::PCL.ModNet.NetManagerClass();

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static global::System.Net.Http.HttpClient CreateNativeCachedClient(global::System.String Path, global::System.Net.Http.HttpMessageHandler Transport)
        {
            return global::CacheCow.Client.ClientExtensions.CreateClient(new global::CacheCow.Client.FileCacheStore.FileStore(Path), Transport);
        }
        internal static global::System.Net.Http.HttpClient StandardMetadataClient => NativeRuntime.GetStandardRequestClient();
        internal static global::PCLCS.MmcPlatformOriginCatalog CreateMmcPlatformOriginCatalog() => NativeRuntime.CreateMmcPlatformOriginCatalog();
        private sealed class NativeTaskWaiter : global::PCLCS.Network.ILegacyNetworkTaskWaiter
        {
            public T Result<T>(global::System.Threading.Tasks.Task<T> Work, global::System.Threading.CancellationTokenSource Source, global::System.Int32 Timeout)
            {
                return global::PCL.ModBase.GetResultWithTimeout(Work, Source, Timeout);
            }
            public void Result(global::System.Threading.Tasks.Task Work, global::System.Threading.CancellationTokenSource Source, global::System.Int32 Timeout)
            {
                global::PCL.ModBase.GetResultWithTimeout(Work, Source, Timeout);
            }
        }

        public static int NetTaskThreadLimit
        {
            get
            {
                return global::PCL.ModNet.NativeRuntime.NetTaskThreadLimit;
            }
        }
        public static long NetTaskSpeedLimitHigh
        {
            get
            {
                return global::PCL.ModNet.NativeRuntime.NetTaskSpeedLimitHigh;
            }
        }
        public static void UpdateNetTaskSpeedLimitHigh(global::System.Int32 Value)
        {
            global::PCL.ModNet.NativeRuntime.UpdateNetTaskSpeedLimitHigh(Value);
        }
        public static global::System.String NetRequestByClient(global::System.String Url, global::System.Net.Http.HttpMethod Method = null, global::System.Object Content = null, global::System.String ContentType = null, global::System.String Accept = "*/*", global::System.Int32 Timeout = 25000, global::System.String[,] Headers = null, global::System.Boolean RequireJson = false, global::System.Text.Encoding Encoding = null, global::System.Boolean SimulateBrowserHeaders = false, global::System.Boolean MakeLog = true)
        {
            return global::PCL.ModNet.NativeRuntime.NetRequestByClient(Url, Method, Content, ContentType, Accept, Timeout, Headers, RequireJson, Encoding, SimulateBrowserHeaders, MakeLog);
        }
        public static global::System.String NetRequestByClientRetry(global::System.String Url, global::System.Net.Http.HttpMethod Method = null, global::System.String BackupUrl = null, global::System.Object Content = null, global::System.String ContentType = null, global::System.String Accept = "*/*", global::System.String[,] Headers = null, global::System.Boolean RequireJson = false, global::System.Text.Encoding Encoding = null, global::System.Boolean SimulateBrowserHeaders = false)
        {
            return global::PCL.ModNet.NativeRuntime.NetRequestByClientRetry(Url, Method, BackupUrl, Content, ContentType, Accept, Headers, RequireJson, Encoding, SimulateBrowserHeaders);
        }
        public static global::System.String NetRequestByClientMultiple(global::System.String Url, global::System.Net.Http.HttpMethod Method = null, global::System.Int32 ThreadCount = 3, global::System.Object Content = null, global::System.String ContentType = null, global::System.String Accept = "*/*", global::System.String[,] Headers = null, global::System.Boolean RequireJson = false, global::System.Int32 Timeout = 25000, global::System.Text.Encoding Encoding = null, global::System.Boolean SimulateBrowserHeaders = false)
        {
            return global::PCL.ModNet.NativeRuntime.NetRequestByClientMultiple(Url, Method, ThreadCount, Content, ContentType, Accept, Headers, RequireJson, Timeout, Encoding, SimulateBrowserHeaders);
        }
        public static global::System.String NetRequestByLoader(global::System.String Url, global::System.Int32 Timeout = 45000, global::System.Boolean IsJson = false, global::System.Boolean SimulateBrowserHeaders = false)
        {
            return global::PCL.ModNet.NativeRuntime.NetRequestByLoader(Url, Timeout, IsJson, SimulateBrowserHeaders);
        }
        public static global::System.String NetRequestByLoader(global::System.Collections.Generic.IEnumerable<global::System.String> Urls, global::System.Int32 Timeout = 45000, global::System.Boolean IsJson = false, global::System.Boolean SimulateBrowserHeaders = false)
        {
            return global::PCL.ModNet.NativeRuntime.NetRequestByLoader(Urls, Timeout, IsJson, SimulateBrowserHeaders);
        }
        public static void NetDownloadByClient(global::System.String Url, global::System.String LocalPath, global::System.Boolean SimulateBrowserHeaders = false)
        {
            global::PCL.ModNet.NativeRuntime.NetDownloadByClient(Url, LocalPath, SimulateBrowserHeaders);
        }
        public static void NetDownloadByLoader(global::System.String Url, global::System.String LocalPath, global::PCL.ModLoader.LoaderBase LoaderToSyncProgress = null, global::MeloongCore.FileChecker Check = null, global::System.Boolean SimulateBrowserHeaders = false)
        {
            global::PCL.ModNet.NativeRuntime.NetDownloadByLoader(Url, LocalPath, LoaderToSyncProgress, Check, SimulateBrowserHeaders);
        }
        public static void NetDownloadByLoader(global::System.Collections.Generic.IEnumerable<global::System.String> Urls, global::System.String LocalPath, global::PCL.ModLoader.LoaderBase LoaderToSyncProgress = null, global::MeloongCore.FileChecker Check = null, global::System.Boolean SimulateBrowserHeaders = false)
        {
            global::PCL.ModNet.NativeRuntime.NetDownloadByLoader(Urls, LocalPath, LoaderToSyncProgress, Check, SimulateBrowserHeaders);
        }
        private static global::System.Byte[] SendRequest(global::System.String Url, global::System.Net.Http.HttpMethod Method, global::System.Object Content = null, global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Headers = null, global::System.Boolean SimulateBrowserHeaders = false, global::System.Int32 Timeout = 25000, global::System.Text.Encoding Encoding = null)
        {
            return global::PCL.ModNet.NativeRuntime.SendRequest(Url, Method, Content, Headers, SimulateBrowserHeaders, Timeout, Encoding);
        }
        public static global::System.Boolean HasDownloadingTask(global::System.Boolean IgnoreCustomDownload = false)
        {
            return global::PCL.ModNet.NativeRuntime.HasDownloadingTask(IgnoreCustomDownload);
        }
        public static global::System.Int32 FindFreePort()
        {
            return global::PCL.ModNet.NativeRuntime.FindFreePort();
        }
        public static global::System.Collections.Generic.List<global::System.Int32> GetUsedPorts()
        {
            return global::PCL.ModNet.NativeRuntime.GetUsedPorts();
        }
        public static global::System.Collections.Generic.List<global::System.Int32> FindFreePorts(global::System.Int32 ConsecutiveCount, params global::System.Int32[] ExtraBlackLists)
        {
            return global::PCL.ModNet.NativeRuntime.FindFreePorts(ConsecutiveCount, ExtraBlackLists);
        }
        public static global::System.Int32 Ping(global::System.String Ip, global::System.Int32 Timeout = 10000, global::System.Boolean MakeLog = true)
        {
            return global::PCL.ModNet.NativeRuntime.Ping(Ip, Timeout, MakeLog);
        }
        public static void ShouldValidateSslCertificateOnLogin(global::System.Boolean Enabled)
        {
            global::PCL.ModNet.NativeRuntime.ShouldValidateSslCertificateOnLogin(Enabled);
        }

        public class NetSource : global::PCLCS.Network.LegacyNetSourceData<global::PCL.ModNet.NetThread>
        {
        }
        public enum NetState
        {
            WaitingToCheck = -(1),
            WaitingToDownload = 0,
            Connecting = 1,
            Reading = 2,
            Downloading = 3,
            Merging = 4,
            Finished = 5,
            Canceled = 6
        }
        public enum NetPreDownloadBehaviour
        {
            HintWhileExists,
            ExitWhileExistsOrDownloading,
            IgnoreCheck
        }
        public class NetThread : global::PCLCS.Network.LegacyNetThreadData<global::PCL.ModNet.NetFile, global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetState>
        {
            public NetThread() : base(() => global::PCL.ModBase.GetTimeMs(), global::PCL.ModNet.NetState.WaitingToDownload)
            {
            }
            public static bool operator ==(global::PCL.ModNet.NetThread Left, global::PCL.ModNet.NetThread Right)
            {
                return global::System.Collections.Generic.EqualityComparer<global::PCL.ModNet.NetThread>.Default.Equals(Left, Right);
            }
            public static bool operator !=(global::PCL.ModNet.NetThread Left, global::PCL.ModNet.NetThread Right)
            {
                return !((Left) == (Right));
            }
        }
        public class NetFile : global::PCLCS.Network.LegacyNetFile<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>
        {
            internal global::System.Action CanceledMergeFinished;
            internal global::System.Boolean AllowCredentialHeaders = true;
            public NetFile(global::System.Collections.Generic.IEnumerable<global::System.String> Urls, global::System.String LocalPath, global::MeloongCore.FileChecker Checker = null, global::System.Boolean SimulateBrowserHeaders = false) : base(global::PCL.ModNet.NativeRuntime, Urls, LocalPath, Checker, SimulateBrowserHeaders)
            {
            }
        }
        public class LoaderDownload : global::PCL.ModLoader.LoaderBase, global::PCLCS.Network.ILegacyNetworkDownload<global::PCL.ModNet.NetFile>
        {
            public global::System.Collections.Concurrent.ConcurrentQueue<global::PCL.ModNet.NetFile> Files;
            private readonly global::PCLCS.Network.LegacyNetworkDownloadRecord NativeData = new global::PCLCS.Network.LegacyNetworkDownloadRecord();
            private global::System.Collections.Concurrent.ConcurrentQueue<global::PCL.ModNet.NetFile> CoreFiles
            {
                get
                {
                    return this.Files;
                }
                set
                {
                    this.Files = value;
                }
            }

            global::System.Collections.Concurrent.ConcurrentQueue<global::PCL.ModNet.NetFile> global::PCLCS.Network.ILegacyNetworkDownload<global::PCL.ModNet.NetFile>.CoreFiles { get => this.CoreFiles; set => this.CoreFiles = value; }
            private global::PCLCS.Network.LegacyNetworkDownloadRecord CoreRecord
            {
                get
                {
                    return this.NativeData;
                }
            }

            global::PCLCS.Network.LegacyNetworkDownloadRecord global::PCLCS.Network.ILegacyNetworkDownload<global::PCL.ModNet.NetFile>.CoreRecord { get => this.CoreRecord; }
            public LoaderDownload(global::System.String Name, global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile> FileTasks)
            {
                global::PCL.ModNet.NativeRuntime.InitializeDownload(this, Name, FileTasks);
            }
            public override double Progress
            {
                get
                {
                    return global::PCL.ModNet.NativeRuntime.DownloadProgress(this);
                }
                set
                {
                    global::PCLCS.Network.LegacyNetworkRuntime<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>.RejectDownloadProgress(value);
                }
            }
            public int FailCount
            {
                get
                {
                    return global::PCL.ModNet.NativeRuntime.DownloadFailCount(this);
                }
                set
                {
                    global::PCL.ModNet.NativeRuntime.SetDownloadFailCount(this, value);
                }
            }
            private void CorePreviewFinish()
            {
                this.RaisePreviewFinish();
            }

            void global::PCLCS.Network.ILegacyNetworkDownload<global::PCL.ModNet.NetFile>.CorePreviewFinish() => this.CorePreviewFinish();
            public void RefreshStat()
            {
                global::PCL.ModNet.NativeRuntime.RefreshDownloadStat(this);
            }
            public override void Start(global::System.Object Input = null, global::System.Boolean IsForceRestart = false)
            {
                global::PCL.ModNet.NativeRuntime.StartDownload(this, Input, IsForceRestart);
            }
            private void CheckExistingFiles(global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::System.Collections.Generic.List<global::System.String> FolderList)
            {
                global::PCL.ModNet.NativeRuntime.CheckExistingFiles(this, Files, FolderList);
            }
            private global::System.String CheckExistingFile(global::System.Collections.Generic.List<global::System.String> FolderList, global::System.Collections.Generic.List<global::System.String> VersionFolders, global::PCL.ModNet.NetFile File)
            {
                return global::PCL.ModNet.NativeRuntime.CheckExistingFile(this, FolderList, VersionFolders, File);
            }
            public void OnFileFinish(global::PCL.ModNet.NetFile File)
            {
                global::PCL.ModNet.NativeRuntime.OnDownloadFileFinish(this, File);
            }
            public void OnFinish()
            {
                global::PCL.ModNet.NativeRuntime.OnDownloadFinish(this);
            }
            public void OnFileFail(global::PCL.ModNet.NetFile File)
            {
                global::PCL.ModNet.NativeRuntime.OnDownloadFileFail(this, File);
            }
            public override void Failed(global::System.Exception Ex)
            {
                global::PCL.ModNet.NativeRuntime.FailDownload(this, Ex);
            }
            public void OnFail(global::System.Collections.Generic.List<global::System.Exception> ExList)
            {
                global::PCL.ModNet.NativeRuntime.OnDownloadFail(this, ExList);
            }
            public override void Cancel()
            {
                global::PCL.ModNet.NativeRuntime.CancelDownload(this);
            }
        }
        public class NetManagerClass : global::PCLCS.Network.LegacyNetManager<global::PCL.ModNet.NetSource, global::PCL.ModNet.NetThread, global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModNet.NetManagerClass, global::PCL.ModLoader.LoaderBase, global::PCL.ModNet.NetState, global::PCL.ModBase.LoadState, global::PCL.MyLoading.MyLoadingState>
        {
            public NetManagerClass() : base(global::PCL.ModNet.NativeRuntime)
            {
            }
        }
    }
}
