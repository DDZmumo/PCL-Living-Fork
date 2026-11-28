// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using PCLCS;

namespace PCL;

internal static class JavaRuntimeDownloads
{
    private static readonly HttpClient client = CreateClient();
    private static readonly JavaRuntimeCatalog catalog = new(client);
    private static readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);

    internal static Task<IReadOnlyList<JavaRuntimePackage>> SearchAsync(int major, string architecture,
        string? distribution, string? packageType, CancellationToken token) =>
        catalog.SearchAsync(major, architecture, distribution, packageType, token);

    internal static bool IsManaged(Java java) => ManagedJavaRuntimeStore.IsManaged(ManagedJavaRuntimeStore.DefaultRoot, java);

    internal static void StartInstall(JavaRuntimePackage package)
    {
        string vendor = JavaRuntimeCatalog.Distributions.GetValueOrDefault(package.Distribution, package.Distribution);
        string key = "install:" + package.Id;
        var store = Store();
        if (!pending.TryAdd(key, 0)) { ModMain.Hint("这个 Java 任务已在进行中。"); return; }

        ManagedJavaRuntimeStore.PreparedInstall? prepared = null;
        var fence = new MmcOperationCancellationFence(() =>
        {
            try { prepared?.Dispose(); }
            finally { pending.TryRemove(key, out _); }
        }, workerCount: 2);
        var download = Worker(fence, "下载 Java 安装包", (work, token, _) =>
        {
            var archive = catalog.ResolveAsync(package, token).GetAwaiter().GetResult();
            prepared = store.PrepareInstallAsync(package, archive, token, value => work.Progress = value).GetAwaiter().GetResult();
        });
        download.ProgressWeight = 7;
        var install = Worker(fence, "校验并安装 Java", (work, token, commit) =>
            store.InstallPreparedAsync(prepared!, token, value => work.Progress = value, commit).GetAwaiter().GetResult());
        install.ProgressWeight = 3;
        StartTask(fence, $"下载并安装 Java {package.Version} · {vendor}", download, install);
    }

    internal static void StartUninstall(Java java)
    {
        if (!IsManaged(java)) return;
        if (!CanUninstall()) { ModMain.Hint("请在游戏退出、启动任务结束后再卸载 Java。"); return; }
        if (ModMain.MyMsgBox($"卸载 {java.Distribution ?? "Java"} {java.Version}？使用这套 Java 的实例需要重新选择。",
            "卸载 Java", "卸载", "取消", IsWarn: true) != 1) return;

        Queue("uninstall:" + java.JavaExePath, $"卸载 Java {java.Version}", (_, token, commit) =>
        {
            Store().UninstallAsync(java, CanUninstall, token, commit).GetAwaiter().GetResult();
        });
    }

    private static void Queue(string key, string name, Action<ModLoader.LoaderTask<int, int>, CancellationToken, Action<Action>> body)
    {
        if (!pending.TryAdd(key, 0)) { ModMain.Hint("这个 Java 任务已在进行中。"); return; }
        var fence = new MmcOperationCancellationFence(() => pending.TryRemove(key, out _));
        StartTask(fence, name, Worker(fence, name, body));
    }

    private static MmcOperationWorker<int, int> Worker(MmcOperationCancellationFence fence, string name,
        Action<ModLoader.LoaderTask<int, int>, CancellationToken, Action<Action>> body) =>
        new(fence, name, work =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(work.CreateCancellationToken(), fence.Token);
            body(work, linked.Token, action =>
            {
                fence.EnterDecision(linked.Token);
                action();
            });
            work.Output = work.Input;
        }) { InterruptOnCancel = false, Block = true };

    private static void StartTask(MmcOperationCancellationFence fence, string name, params ModLoader.LoaderBase[] workers)
    {
        var task = new MmcOperationCombo<int>(fence, name, workers) {
            OnStateChanged = loader =>
            {
                ModDownloadLib.LoaderStateChangedHintOnly(loader);
                if (loader.State is ModBase.LoadState.Finished or ModBase.LoadState.Failed or ModBase.LoadState.Canceled)
                    ModBase.RunInUi(ModJava.UpdateJavaLists);
            }
        };
        try
        {
            task.Start(1);
            ModLoader.LoaderTaskbarAdd(task);
            ModMain.FrmMain?.RefreshTaskButton(pulse: true);
        }
        catch
        {
            task.Cancel();
            fence.AbandonUnstarted();
            throw;
        }
    }

    private static ManagedJavaRuntimeStore Store() => new(ManagedJavaRuntimeStore.DefaultRoot, client, ModJava.MmcPins);

    private static bool CanUninstall() => !LauncherApplicationServices.IsLaunchActive() && !ModWatcher.HasRunningMinecraft;

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Living-Fork/1.0");
        return result;
    }
}
