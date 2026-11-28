// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCL;

internal sealed partial class LauncherHomepage(Panel outlet, Action scrollToTop) : IDisposable
{
    private readonly object _refreshLock = new();
    private ModLoader.LoaderTask<(string Address, bool ApplyImmediately, bool Force, int SettingsRevision), int>? _online;
    private ulong? _loadedHash;
    private string? _selectedAddress;
    private int _refreshRevision;
    private int _settingsRevision;
    private int _forceRefresh;
    private volatile bool _disposed;
    private const string CacheUrl = "CacheSavedPageUrl";
    private const string CacheVersion = "CacheSavedPageVersion";
    private static string CachePath => ModBase.PathTemp + @"Cache\Custom.xaml";

    internal void Refresh()
    {
        int revision = Interlocked.Increment(ref _refreshRevision);
        ModBase.RunInThread(() =>
        {
            try
            {
                lock (_refreshLock)
                {
                    if (!_disposed && revision == Volatile.Read(ref _refreshRevision)) RefreshCore(revision);
                }
            }
            catch (Exception error)
            {
                Logger.Error(error, "加载 PCL 主页自定义信息失败", ModBase.ModeDebug ? LogBehavior.Alert : LogBehavior.Toast);
            }
        });
    }

    private void RefreshCore(int revision)
    {
        bool force = Interlocked.Exchange(ref _forceRefresh, 0) != 0;
        int settingsRevision = Volatile.Read(ref _settingsRevision);
        string? content = null, url = null;
        switch (Settings.Get<int>("UiCustomType"))
        {
            case 1:
                Logger.Info("主页自定义数据来源：本地文件");
                string file = Paths.Base + @"PCL\Custom.xaml";
                if (File.Exists(file)) content = LauncherContentDownload.ReadFile(file, LauncherContentDownload.MarkupBytes);
                break;
            case 2:
                url = Settings.Get<string>("UiCustomNet");
                break;
            case 3:
                (content, url) = Preset(Settings.Get<int>("UiCustomPreset"));
                break;
        }

        url = string.IsNullOrWhiteSpace(url) ? null : LauncherMarkup.ExpandPublicTokens(url, Uri.EscapeDataString);
        Volatile.Write(ref _selectedAddress, url);
        if (!string.IsNullOrWhiteSpace(url))
        {
            _online ??= new("下载主页", Download) { ReloadTimeout = 10 * 60 * 1000, InterruptOnCancel = false };
            if (string.Equals(url, Settings.Get<string>(CacheUrl), StringComparison.Ordinal) && File.Exists(CachePath))
            {
                try {
                    content = LauncherContentDownload.ReadFile(CachePath, LauncherContentDownload.MarkupBytes);
                    LauncherMarkup.PrepareFragment(content);
                    Logger.Info("主页自定义数据来源：联网缓存文件");
                } catch (Exception error) {
                    content = null;
                    Logger.Warn(error, "忽略不可用的主页缓存");
                }
            }
            bool needsContent = content is null;
            if (needsContent) ModMain.Hint("正在加载主页……");
            Show(revision, content);
            _online.Start((url, needsContent || force, needsContent || force, settingsRevision), IsForceRestart: force);
            return;
        }

        Show(revision, content);
    }

    private void Show(int revision, string? content) => ModBase.RunInUi(() =>
    {
        if (revision == Volatile.Read(ref _refreshRevision)) LoadContent(content);
    });

    private void Download(ModLoader.LoaderTask<(string Address, bool ApplyImmediately, bool Force, int SettingsRevision), int> task)
    {
        Thread worker = Thread.CurrentThread;
        var (address, applyImmediately, force, settingsRevision) = task.Input;
        bool IsCurrent() => !task.IsCanceledWithThread(worker) && IsSelectedAddress(address, settingsRevision);
        if (string.IsNullOrEmpty(address) || !IsCurrent()) return;
        try
        {
            var page = new Uri(address, UriKind.Absolute);
            if (!LauncherContentDownload.IsHttps(page)) throw new InvalidDataException("联网主页需要使用 HTTPS 地址。");
            var versionAddress = new UriBuilder(page) { Path = page.AbsolutePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? page.AbsolutePath + ".ini" : page.AbsolutePath.TrimEnd('/') + "/version" };
            CancellationToken cancellation = task.CreateCancellationToken();
            string version = "";
            try
            {
                version = LauncherContentDownload.FetchTextAsync(versionAddress.Uri.AbsoluteUri, LauncherContentDownload.VersionBytes, cancellation).GetAwaiter().GetResult();
                if (version.Length > 1000) throw new InvalidOperationException($"获取的主页版本过长（{version.Length} 字符）");
                string current = Settings.Get<string>(CacheVersion);
                if (!force && !string.IsNullOrEmpty(version) && version == current && string.Equals(address, Settings.Get<string>(CacheUrl), StringComparison.Ordinal))
                {
                    Logger.Info("当前缓存的主页已为最新");
                    return;
                }
            }
            catch (Exception error) when (!error.IsCanceled())
            {
                version = "";
                Logger.Warn(error, "联网获取主页版本失败，将直接下载");
            }
            if (!IsCurrent()) return;
            string content = LauncherContentDownload.FetchTextAsync(address, LauncherContentDownload.MarkupBytes, cancellation).GetAwaiter().GetResult();
            var prepared = LauncherMarkup.PrepareFragment(content);
            ulong hash = content.GetStableHashCode();
            if (!ModBase.RunInUiWait(() => {
                if (!IsCurrent()) return false;
                if (_loadedHash != hash) {
                    UIElement view = LauncherMarkup.LoadFragment(prepared);
                    if (!IsCurrent()) return false;
                    if (applyImmediately) Display(view, hash);
                }
                return true;
            })) return;

            string cache = CachePath;
            string staged = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                File.WriteAllText(staged, content);
                lock (_refreshLock) {
                    if (!IsCurrent()) return;
                    File.Move(staged, cache, overwrite: true);
                    Settings.Set(CacheUrl, address);
                    Settings.Set(CacheVersion, version);
                }
            } finally {
                File.Delete(staged);
            }
            Logger.Info($"已联网下载并验证主页，内容长度：{content.Length}");
        }
        catch (Exception error)
        {
            if (!error.IsCanceled() && IsCurrent())
                Logger.Error(error, "下载或验证主页失败", LogBehavior.Toast);
        }
    }

    private bool IsSelectedAddress(string address, int settingsRevision) => !_disposed &&
        settingsRevision == Volatile.Read(ref _settingsRevision) &&
        string.Equals(Volatile.Read(ref _selectedAddress), address, StringComparison.Ordinal);

    internal void InvalidateSettings() => Invalidate(forceRefresh: false);

    private void Invalidate(bool forceRefresh)
    {
        lock (_refreshLock)
        {
            // Reject both render callbacks and downloads started for the previous settings, even after returning to a URL.
            Interlocked.Increment(ref _refreshRevision);
            Interlocked.Increment(ref _settingsRevision);
            Volatile.Write(ref _selectedAddress, null);
            _online?.Cancel();
            if (!forceRefresh) return;
            _loadedHash = null;
            Interlocked.Exchange(ref _forceRefresh, 1);
        }
    }

    internal void ForceRefresh()
    {
        Logger.Info("要求强制刷新主页");
        Invalidate(forceRefresh: true);
        if (ModMain.FrmMain.PageCurrent.Page == FormMain.PageType.Launch)
        {
            scrollToTop();
            Refresh();
        }
        else ModMain.FrmMain.PageChange(FormMain.PageType.Launch);
    }

    internal void LoadContent(string? content)
    {
        if (_disposed) return;
        try
        {
            ulong hash = (content ?? "").GetStableHashCode();
            if (_loadedHash == hash) return;
            if (string.IsNullOrWhiteSpace(content)) { Display(null, hash); return; }

            var timer = Stopwatch.StartNew();
            Logger.Info($"实例化：加载主页 UI 开始，内容长度：{content.Length}");
            Display(LauncherMarkup.ParseFragment(content), hash);
            Logger.Info($"实例化：加载主页 UI 完成，耗时 {timer.ElapsedMilliseconds}ms");
            if (timer.ElapsedMilliseconds > 3000)
                ModMain.Hint($"主页加载过于缓慢（花费了 {timer.Elapsed.TotalSeconds:F1} 秒），请向主页作者反馈此问题，或暂时停止使用该主页");
        }
        catch (Exception error)
        {
            ReportLoadFailure(error);
        }
    }

    private void Display(UIElement? view, ulong hash)
    {
        outlet.Children.Clear();
        if (view is not null) outlet.Children.Add(view);
        _loadedHash = hash;
    }

    internal bool HandleDispatcherException(Exception error)
    {
        if (error is not XamlParseException) return false;
        LoadContent(null);
        ReportLoadFailure(error);
        return true;
    }

    private void ReportLoadFailure(Exception error)
    {
        if (ModBase.ModeDebug || Settings.Get<int>("UiCustomType") == 1)
        {
            Logger.Warn(error, "加载主页失败");
            string detail = error is InvalidDataException or UnauthorizedAccessException ? error.Message :
                "主页内容编写有误，请根据下列错误信息进行检查：\r\n" + error.GetDisplay(false);
            if (ModMain.MyMsgBox(detail, "加载主页失败", "重试", "取消") == 1) ForceRefresh();
        }
        else Logger.Error(error, "加载主页失败", LogBehavior.Toast);
    }

    public void Dispose()
    {
        _disposed = true;
        _online?.Cancel();
    }
}
