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
using ThrottleDebounce;

namespace PCLCS.Network;
public sealed partial class LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TDownload : TLoader, ILegacyNetworkDownload<TFile> where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState> where TNetState : struct, Enum where TLoadState : struct, Enum
{
    public readonly LegacyNetworkHost<TFile, TDownload, TManager, TLoader> Host;
    internal readonly HttpClient ThreadClient;
    private HttpClient? requestClient;
    private readonly object requestClientLock = new();
    private long? speedLimitHigh;
    public LegacyNetworkRuntime(LegacyNetworkHost<TFile, TDownload, TManager, TLoader> host)
    {
        Host = host;
        // Eager like LF's module ThreadClient, with the same cookies default.
        ThreadClient = new HttpClient(host.WrapRedirects(CreateTransport(false)));
    }

    private static HttpClientHandler CreateTransport(bool disableCookies) {
        var transport = new HttpClientHandler {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
        };
        if (disableCookies) transport.UseCookies = false;
#if !NETFRAMEWORK
        // .NET10 no longer applies SPM's callback to HttpClient automatically.
        // Invoke that same single delegate; LF's original policy accepts HTTPS Uri.
        transport.ServerCertificateCustomValidationCallback = (request, certificate, chain, failure) => {
#pragma warning disable SYSLIB0014 // Read the original LF callback as policy storage only.
            RemoteCertificateValidationCallback? policy = ServicePointManager.ServerCertificateValidationCallback;
#pragma warning restore SYSLIB0014
            return policy is null ? failure == SslPolicyErrors.None : policy(request.RequestUri!, certificate, chain, failure);
        };
#endif
        return transport;
    }
    public int NetTaskThreadLimit => checked(Host.IntegerSetting("ToolDownloadThread") + 1);

    public long NetTaskSpeedLimitHigh
    {
        get
        {
            if (speedLimitHigh is null)
            {
                int setting = Host.IntegerSetting("ToolDownloadSpeed");
                if (setting <= 14)
                    speedLimitHigh = Convert.ToInt64(checked(setting + 1) * 0.1 * 1024 * 1024L);
                else if (setting <= 31)
                    speedLimitHigh = Convert.ToInt64(checked(setting - 11) * 0.5 * 1024 * 1024L);
                else if (setting <= 41)
                    speedLimitHigh = checked((setting - 21) * 1024 * 1024L);
                else
                    speedLimitHigh = -1;
            }

            return speedLimitHigh.Value;
        }
    }

    public void UpdateNetTaskSpeedLimitHigh(int Value) => speedLimitHigh = null;
    public string NetRequestByClientRetry(string Url, HttpMethod? Method = null, string? BackupUrl = null, object? Content = null, string? ContentType = null, string? Accept = "*/*", string[, ]? Headers = null, bool RequireJson = false, Encoding? Encoding = null, bool SimulateBrowserHeaders = false)
    {
        int retryCount = 0;
        Exception? retryException = null;
        long startTime = Host.Clock();
        // LF accepts BackupUrl but does not read it. Do not add fallback routing.
        while (true)
        {
            try
            {
                switch (retryCount)
                {
                    case 0:
                        return NetRequestByClient(Url, Method, Content, ContentType, Accept, 10000, Headers, RequireJson, Encoding, SimulateBrowserHeaders);
                    case 1:
                        Thread.Sleep(500);
                        return NetRequestByClient(Url, Method, Content, ContentType, Accept, 30000, Headers, RequireJson, Encoding, SimulateBrowserHeaders);
                    default:
                        if (checked(Host.Clock() - startTime) > 5500)
                        {
                            Thread.Sleep(500);
                            return NetRequestByClient(Url, Method, Content, ContentType, Accept, 4000, Headers, RequireJson, Encoding, SimulateBrowserHeaders);
                        }

                        throw retryException!;
                }
            }
            catch (Exception error)
            {
                error.ThrowIfCanceled();
                if (error is HttpRequestCodeException code)
                {
                    if (code.StatusCode == HttpStatusCode.Forbidden || code.StatusCode == HttpStatusCode.NotFound)
                        throw;
                    if ((int)code.StatusCode == 429)
                        Thread.Sleep(10000);
                }

                if (retryCount == 0)
                {
                    retryException = error;
                    retryCount++;
                }
                else if (retryCount == 1)
                    retryCount++;
                else
                    throw;
            }
        }
    }

    public string NetRequestByClientMultiple(string Url, HttpMethod? Method = null, int ThreadCount = 3, object? Content = null, string? ContentType = null, string? Accept = "*/*", string[, ]? Headers = null, bool RequireJson = false, int Timeout = 25000, Encoding? Encoding = null, bool SimulateBrowserHeaders = false)
    {
        var threads = new List<Thread>();
        string? requestResult = null;
        Exception? requestError = null;
        int failCount = 0;
        void InterruptAll()
        {
            try
            {
                foreach (Thread thread in threads)
                    if (thread.IsAlive)
                        thread.Interrupt();
            }
            catch
            {
            }
        }

        for (int i = 1; i <= ThreadCount; i = checked(i + 1))
        {
            var thread = new Thread(() =>
            {
                try
                {
                    requestResult = NetRequestByClient(Url, Method, Content, ContentType, Accept, Timeout, Headers, RequireJson, Encoding, SimulateBrowserHeaders);
                }
                catch (Exception error)
                {
                    failCount = checked(failCount + 1);
                    requestError = error;
                }
            });
            thread.Start();
            threads.Add(thread);
            Thread.Sleep(checked(i * 250));
            if (requestResult is not null)
            {
                InterruptAll();
                return requestResult;
            }
        }

        while (true)
        {
            if (requestResult is not null)
            {
                InterruptAll();
                return requestResult;
            }

            if (failCount == ThreadCount)
            {
                InterruptAll();
                throw requestError!;
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>Launcher-owned cached transport with its existing redirect/TLS policy and cookies disabled. Do not dispose or mutate defaults.</summary>
    public HttpClient GetStandardRequestClient() {
        Directory.CreateDirectory(Host.TempFolder() + "Cache\\Http\\");
        lock (requestClientLock) {
            return requestClient ??= Host.CreateCachedClient(Host.TempFolder() + "Cache/Http/", Host.WrapRedirects(CreateTransport(true)));
        }
    }

    public byte[] SendRequest(string Url, HttpMethod Method, object? Content = null, Dictionary<string, string>? Headers = null, bool SimulateBrowserHeaders = false, int Timeout = 25000, Encoding? Encoding = null)
    {
        if (Host.InUi() && !Url.Contains("//127."))
            throw new Exception("在 UI 线程执行了网络请求");
        HttpRequestMessage? request = null;
        CancellationTokenSource? cancelToken = null;
        HttpResponseMessage? response = null;
        try
        {
            HttpClient standardClient = GetStandardRequestClient();

            Url = Host.SignCdn(Url);
            request = new HttpRequestMessage(Method, Url);
            if (Content is not null && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                if (Content is HttpContent httpContent)
                    request.Content = httpContent.Clone();
                else if (Content is byte[] bytes)
                    request.Content = new ByteArrayContent(bytes);
                else
                    request.Content = new ByteArrayContent((Encoding ?? System.Text.Encoding.UTF8).GetBytes(Content.ToString()!));
            }

            foreach (KeyValuePair<string, string> header in Headers ?? new Dictionary<string, string>())
            {
                if (header.Key.Lower() == "content-type")
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                else
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            Host.SignHeaders(Url, ref request, SimulateBrowserHeaders, true);
            Retrier.Attempt(delay: _ => TimeSpan.FromMilliseconds(200), isRetryAllowed: error => error is FileNotFoundException || error is IOException, action: attempt =>
            {
                cancelToken?.Dispose();
                cancelToken = new CancellationTokenSource(Timeout);
                HttpRequestMessage clone = request.Clone();
                try
                {
                    response = Host.Waiter.Result(standardClient.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancelToken.Token), cancelToken, Timeout);
                }
                finally
                {
                    clone.Dispose();
                }
            });
            byte[] responseBytes;
            cancelToken!.Dispose();
            cancelToken = new CancellationTokenSource(Timeout);
            using (Stream responseStream = Host.Waiter.Result(response!.Content.ReadAsStreamAsync(), cancelToken, Timeout))
            {
                using var output = new MemoryStream();
                Host.Waiter.Result(responseStream.CopyToAsync(output, 81920, cancelToken.Token), cancelToken, Timeout);
                responseBytes = output.ToArray();
            }

            if (!response.IsSuccessStatusCode)
            {
                string responseMessage = responseBytes.GetString(Encoding);
                throw new HttpRequestCodeException($"错误码 {response.StatusCode} ({(int)response.StatusCode})，{Method}，{Url}" + (string.IsNullOrEmpty(responseMessage) ? "" : "\r\n" + responseMessage), response.StatusCode, responseMessage);
            }

            return responseBytes;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new WebException($"连接服务器超时，请稍后再试，或使用 VPN 改善网络环境（{Method}, {Url}）", WebExceptionStatus.Timeout);
        }
        catch (Exception error)
        {
            if (error is not TaskCanceledException)
                error.ThrowIfCanceled();
            if (error.IsBadNetwork())
                throw new WebException($"网络请求失败，请稍后再试，或使用 VPN 改善网络环境（{Method}, {Url}）", WebExceptionStatus.Timeout);
            throw new Exception($"网络请求出现意外异常（{Method}, {Url}）", error);
        }
        finally
        {
            request?.Dispose();
            cancelToken?.Dispose();
            response?.Dispose();
        }
    }

    public bool HasDownloadingTask(bool IgnoreCustomDownload = false) => Host.Taskbar().Any(task => task.Show && Convert.ToInt32(task.State) == 1 && (!IgnoreCustomDownload || !task.Name.ToString().Contains("自定义下载")));
    public void ShouldValidateSslCertificateOnLogin(bool Enabled)
    {
        // Preserve LF's existing callback for its remaining legacy login callers.
        // New HttpClient/TLS policy is a separate host boundary, not an implicit replacement.
#pragma warning disable SYSLIB0014
        ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, failure) => Host.AcceptLoginCertificate(sender, failure, Enabled);
#pragma warning restore SYSLIB0014
    }

    public string NetRequestByClient(string Url, HttpMethod? Method = null, object? Content = null, string? ContentType = null, string? Accept = "*/*", int Timeout = 25000, string[, ]? Headers = null, bool RequireJson = false, Encoding? Encoding = null, bool SimulateBrowserHeaders = false, bool MakeLog = true)
    {
        if (Method is null)
            Method = HttpMethod.Get;
        if (MakeLog)
            Logger.Info($"发起网络请求（{Method.Method}，{Url}），最大超时 {Timeout}");
        try
        {
            var HeaderDictionary = (Headers ?? (new string[, ]
            {
            }

            )).ToDictionary();
            if (ContentType is not null)
                HeaderDictionary["Content-Type"] = ContentType;
            if (Accept is not null)
                HeaderDictionary["Accept"] = Accept;
            string Result = SendRequest(Url, Method, Content, HeaderDictionary, Timeout: Timeout, Encoding: Encoding, SimulateBrowserHeaders: SimulateBrowserHeaders).GetString(Encoding);
            // 检查结果是否为完整 JSON（在 #6683 中可能返回空，在被 GFW 截断时可能不完整）
            // 在此处检查并报错，可以让 NetRequestByClientRetry 等方法进行重试
            if (RequireJson)
            {
                Result = Result.Trim(("\r\n" + " ").ToCharArray()); // Authlib-Injector 的结果会在末尾附带一个空行
                if (!(Result.StartsWithF("{") && Result.EndsWithF("}")) && !(Result.StartsWithF("[") && Result.EndsWithF("]")))
                {
                    throw new FormatException("返回结果并非 JSON 格式：" + (Result.Length > 2000 ? Result.Substring(0, 1000) + "..." + Result.Substring(Result.Length - 1000) : Result));
                }
            }

            return Result;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (MakeLog)
                Logger.Warn(ex, $"网络请求失败（{Method.Method}，{Url}）");
            throw;
        }
    }

    public string NetRequestByLoader(string Url, int Timeout = 45000, bool IsJson = false, bool SimulateBrowserHeaders = false)
    {
        return NetRequestByLoader(new[] { Url }, Timeout, IsJson, SimulateBrowserHeaders);
    }

    public string NetRequestByLoader(IEnumerable<string> Urls, int Timeout = 45000, bool IsJson = false, bool SimulateBrowserHeaders = false)
    {
        string Temp = Host.RequestTempFolder() + "download.txt";
        var NewTask = Host.CreateDownload("源码获取 " + Host.NewUuid() + "#", new List<TFile>() { Host.CreateFile(Urls, Temp, new FileChecker() { IsJson = IsJson }, SimulateBrowserHeaders) });
        try
        {
            NewTask.WaitForExitTime(Timeout, TimeoutMessage: "连接服务器超时（第一下载源：" + Urls.First() + "）");
            string Result = FileUtils.ReadAsString(Temp);
            FileUtils.Delete(Temp);
            return Result;
        }
        finally
        {
            NewTask.Cancel();
        }
    }

    public void NetDownloadByClient(string Url, string LocalPath, bool SimulateBrowserHeaders = false)
    {
        Logger.Info($"通过网络请求直接下载小文件：{Url} → {LocalPath}");
        int RetryCount = 0;
        Retry:
            ;
        try
        {
            FileUtils.Write(LocalPath, SendRequest(Url, HttpMethod.Get, SimulateBrowserHeaders: SimulateBrowserHeaders));
        }
        catch (Exception ex)
        {
            FileUtils.Delete(LocalPath);
            ex.ThrowIfCanceled();
            if (RetryCount < 2)
            {
                RetryCount += 1;
                goto Retry;
            }

            throw new HttpRequestException($"通过网络请求直接下载小文件失败（{LocalPath}）", ex);
        }
    }

    public void NetDownloadByLoader(string Url, string LocalPath, TLoader? LoaderToSyncProgress = null, FileChecker? Check = null, bool SimulateBrowserHeaders = false)
    {
        NetDownloadByLoader(new[] { Url }, LocalPath, LoaderToSyncProgress, Check, SimulateBrowserHeaders);
    }

    public void NetDownloadByLoader(IEnumerable<string> Urls, string LocalPath, TLoader? LoaderToSyncProgress = null, FileChecker? Check = null, bool SimulateBrowserHeaders = false)
    {
        var NewTask = Host.CreateDownload($"文件下载 {Host.NewUuid()}#", new List<TFile>() { Host.CreateFile(Urls, LocalPath, Check, SimulateBrowserHeaders) });
        try
        {
            NewTask.WaitForExit(LoaderToSyncProgress: LoaderToSyncProgress);
        }
        catch (Exception ex)
        {
            throw new WebException($"多线程直接下载文件失败（第一下载源：" + Urls.First() + "）", ex);
        }
        finally
        {
            NewTask.Cancel();
        }
    }

    public int FindFreePort()
    {
        var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        int port = ((IPEndPoint)Listener.LocalEndpoint).Port;
        Listener.Stop();
        return port;
    }

    public List<int> GetUsedPorts()
    {
        var IPProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
        var UsedPorts = new List<int>();
        UsedPorts.AddRange(IPProperties.GetActiveTcpListeners().Select(ep => ep.Port));
        UsedPorts.AddRange(IPProperties.GetActiveUdpListeners().Select(ep => ep.Port));
        UsedPorts.AddRange(IPProperties.GetActiveTcpConnections().Select(conn => conn.LocalEndPoint.Port));
        return UsedPorts.Distinct().ToList();
    }

    public List<int> FindFreePorts(int ConsecutiveCount, params int[] ExtraBlackLists)
    {
        var UsedPorts = GetUsedPorts().Concat(ExtraBlackLists);
        for (int port = 12000 + Host.RandomInteger(0, 1000), loopTo = 65000 - ConsecutiveCount; port <= loopTo; port++)
        {
            var Range = Enumerable.Range(port, ConsecutiveCount);
            if (!Range.Any(p => UsedPorts.Contains(p)))
                return Range.ToList();
        }

        throw new Exception("未能找到可用的端口！");
    }

    public int Ping(string Ip, int Timeout = 10000, bool MakeLog = true)
    {
        System.Net.NetworkInformation.PingReply PingResult;
        try
        {
            PingResult = new System.Net.NetworkInformation.Ping().Send(Ip);
        }
        catch (Exception ex)
        {
            if (MakeLog)
                Logger.Info($"Ping {Ip} 失败：{ex.Message}");
            return -1;
        }

        if (PingResult.Status == System.Net.NetworkInformation.IPStatus.Success)
        {
            if (MakeLog)
                Logger.Info($"Ping {Ip} 结束：{PingResult.RoundtripTime}ms");
            return (int)PingResult.RoundtripTime;
        }
        else
        {
            if (MakeLog)
                Logger.Info($"Ping {Ip} 失败");
            return -1;
        }
    }
}
