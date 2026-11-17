using System;
using System.IO;
using System.Linq;
using System.Threading;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public sealed class MmcPackageUpdateServices {
    public MmcMetadataStore Metadata { get; init; }
    public MmcArtifactCacheRoots CacheRoots { get; init; }
    public Action<ModLoader.LoaderCombo<string>> RegisterTask { get; init; }
    public Action<MmcInstancePackageUpdateResult> Published { get; init; }
    public Action<ModNet.LoaderDownload> DownloadCreated { get; init; }
    public Action<MmcInstanceBackendProgress> ProgressObserved { get; init; }
    public Action<MmcTransactionCheckpoint, int> TransactionObserved { get; init; }
    public Action<MmcInstancePackageUpdateResult> Discarded { get; init; }
}

internal static partial class MmcPackageUpdateUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Func<ModMinecraft.McInstance, MmcPackageUpdateServices> ServicesFactory { get; set; }
    internal static Func<string> SelectArchive { get; set; }
    internal static Func<MmcInstancePackageUpdatePreview, MmcInstancePackageUpdateChoices> SelectChoices { get; set; }
    // Same result values as the native dialog: 1 commits, 2 cancels, 3 edits.
    internal static Func<MmcInstancePackageUpdatePreview, int> ConfirmPreview { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }

    internal static void Prompt(ModMinecraft.McInstance instance) => PromptCaptured(instance, null);
    internal static ModLoader.LoaderCombo<string> PromptCaptured(ModMinecraft.McInstance instance, MmcPlatformInstanceSelection expected, Func<bool> isCurrent = null) {
        ArgumentNullException.ThrowIfNull(instance);
        try {
            if (HasActiveOperation) throw new IOException("另一项 MMC 整合包更新尚未完成。");
            if (isCurrent is not null && !isCurrent()) throw new IOException("当前页面对应的实例已变更，请重新进入实例设置。");
            var source = ReadCapturedSource(instance, expected);
            expected ??= Selection(source);
            string archive = SelectArchive is not null ? SelectArchive() : MeloongCore.Wpf.Dialogs.SelectFile(
                "选择新版 MMC / Prism 整合包", false, filter: new[] { (new[] { "zip" }, "MMC / Prism 整合包") }).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(archive)) {
                if (isCurrent is not null && !isCurrent()) throw new IOException("当前页面对应的实例已变更，请重新进入实例设置。");
                return StartCaptured(instance, archive, expected);
            }
        } catch (Exception error) { Report(error, "无法开始 MMC 整合包更新"); }
        return null;
    }

    internal static ModLoader.LoaderCombo<string> Start(ModMinecraft.McInstance instance, string archivePath) => StartCaptured(instance, archivePath, null);
    internal static ModLoader.LoaderCombo<string> StartCaptured(ModMinecraft.McInstance instance, string archivePath, MmcPlatformInstanceSelection expected) {
        var source = ReadCapturedSource(instance, expected);
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再更新。");
        string archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive)) throw new IOException("所选整合包文件不存在。");
        var services = ServicesFactory?.Invoke(instance) ?? CreateServices(source.Paths.InstanceRoot);
        return StartPrepared(instance, source, services, "读取新版 MMC 整合包", (backend, task, token) => {
            var environment = ResolveEnvironment(archive, services.Metadata, task, token);
            var request = new MmcInstancePackageUpdateRequest(source.Paths.InstanceRoot, source.Definition.InstanceId,
                source.Definition.Generation, source.PrimarySha256, archive, environment);
            task.Name = "准备新版 MMC 整合包";
            return backend.PrepareAsync(request, token, new Progress(task, services.ProgressObserved)).GetAwaiter().GetResult();
        });
    }

    private static ModLoader.LoaderCombo<string> StartPrepared(ModMinecraft.McInstance instance, MmcInstanceInspection source,
        MmcPackageUpdateServices services, string initialStage,
        Func<MmcInstancePackageUpdateBackend<ModNet.NetFile>, ModLoader.LoaderTask<string, MmcInstancePackageUpdateResult>, CancellationToken,
            MmcInstancePackageUpdatePreviewResult> prepare) {
        if (services.Metadata is null || services.CacheRoots is null) throw new IOException("MMC 更新缺少下载和元数据服务。");
        // Read settings before Commit takes configuration quiescence. The backup
        // callback itself only uses the supplied game root and captured retention.
        int retention = Settings.Get<int>("VersionWorldBackupRetention", Instance: instance);
        var fence = AcquireOperationFence();
        var worker = new MmcOperationWorker<string, MmcInstancePackageUpdateResult>(fence, initialStage, task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var token = linked.Token;
            var backend = new MmcInstancePackageUpdateBackend<ModNet.NetFile>(services.Metadata, services.CacheRoots, MmcDownloads.BuildFile,
                (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, onLoaderCreated: services.DownloadCreated),
                (gameRoot, cancellation) => WorldBackups.CaptureFolder(gameRoot, "整合包更新前", retention, cancellation));
            OwnedCandidate owned = null;
            bool discard = false, published = false;
            try {
                var prepared = prepare(backend, task, token);
                owned = OwnedCandidate.FromResult(source.Paths.InstanceRoot, prepared);
                if (prepared.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                var preview = RequirePreview(prepared);
                for (;;) {
                    token.ThrowIfCancellationRequested(); task.Name = "等待选择 MMC 更新差异";
                    // Prepare/Select have returned and released all job, admission and
                    // configuration leases before either dialog is dispatched.
                    var choices = ModBase.RunInUiWait(() => {
                        token.ThrowIfCancellationRequested();
                        return SelectChoices is not null ? SelectChoices(preview) : Choose(preview);
                    });
                    if (choices is null) { task.Cancel(); throw new OperationCanceledException(token); }
                    token.ThrowIfCancellationRequested(); task.Name = "重新核对所选更新内容";
                    var selected = backend.SelectAsync(preview, preview.PreviewSha256, choices, token).GetAwaiter().GetResult();
                    if (selected.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                    preview = RequirePreview(selected);
                    token.ThrowIfCancellationRequested(); task.Name = "等待确认 MMC 更新结果";
                    int decision = ModBase.RunInUiWait(() => {
                        token.ThrowIfCancellationRequested();
                        return ConfirmPreview is not null ? ConfirmPreview(preview) : Confirm(preview);
                    });
                    token.ThrowIfCancellationRequested();
                    if (decision == 3) continue;
                    if (decision != 1) { task.Cancel(); throw new OperationCanceledException(token); }
                    // Even a presentation hook cannot acknowledge definite errors or
                    // silently supply missing component/file/dependency choices.
                    if (!preview.Errors.IsEmpty || preview.RequiresChoices)
                        throw new IOException("所选更新内容仍有未解决的选择或依赖错误，请重新预览。\n" + string.Join("\n", preview.Errors));
                    task.Name = preview.ChangesMinecraftVersion ? "备份存档并更新 MMC 环境" : "更新 MMC 整合包";
                    var result = backend.CommitAsync(new(preview, preview.PreviewSha256) {
                        TransactionCheckpoint = (phase, index) => {
                            services.TransactionObserved?.Invoke(phase, index);
                            if (phase == MmcTransactionCheckpoint.BeforeDecision) fence.EnterDecision(token);
                        }
                    }, token).GetAwaiter().GetResult(); task.Output = result;
                    if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                    if (result.Status != MmcInstanceBackendStatus.Succeeded)
                        throw new IOException("MMC 更新尚未完成，原配置及恢复现场已保留：" + result.Failure);
                    published = discard = true;
                    break;
                }
            } catch (OperationCanceledException) {
                discard = true;
                throw;
            } catch (Exception error) when (owned is not null) {
                // Failed publication may need its source candidate for recovery.
                throw new IOException(error.Message + "\n准备目录已保留：" + owned.Root, error);
            } finally {
                if (discard && owned is not null) {
                    task.Name = published ? "清理本次更新准备目录" : "结束本次 MMC 更新";
                    // RunBatchAsync drains the real downloader before it returns;
                    // DiscardAsync also drains actual settings providers before gates.
                    var cleanup = backend.DiscardAsync(owned.Root, owned.OperationId, CancellationToken.None).GetAwaiter().GetResult();
                    services.Discarded?.Invoke(cleanup);
                    if (cleanup.Status != MmcInstanceBackendStatus.Succeeded)
                        Report(new IOException(cleanup.Failure + "\n保留目录：" + owned.Root), "MMC 准备目录清理未完成");
                }
            }
            if (services.Published is not null) services.Published(task.Output);
            else ModBase.RunInUi(() => {
                // A completed background task never navigates a newly opened page.
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                ModMain.Hint("MMC 整合包已更新，可在环境记录中撤回本次更新。", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "更新 MMC 整合包：" + instance.Name, [worker]);
        try {
            if (services.RegisterTask is not null) services.RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(source.Paths.InstanceRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }

    internal static MmcOperationCancellationFence AcquireOperationFence() {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 来源核实或整合包更新尚未完成。");
        return new(() => Interlocked.Exchange(ref busy, 0));
    }
    internal static MmcInstanceInspection ReadManagedSource(ModMinecraft.McInstance instance) => ReadSource(instance);
    internal static MmcInstanceInspection ReadCapturedSource(ModMinecraft.McInstance instance, MmcPlatformInstanceSelection expected) {
        var source = ReadSource(instance);
        if (expected is not null && (!MeloongCore.PathUtils.IsSame(source.Paths.InstanceRoot, expected.VersionRoot) ||
            source.Definition.InstanceId != expected.ExpectedInstanceId || source.Definition.Generation != expected.ExpectedGeneration || source.PrimarySha256 != expected.ExpectedPrimarySha256))
            throw new IOException("实例状态已变化，请刷新整合包更新页面后重新选择。");
        return source;
    }
    internal static MmcPlatformInstanceSelection Selection(MmcInstanceInspection source) =>
        new(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256);

    private static MmcRuntimeEnvironment ResolveEnvironment(string archive, MmcMetadataStore metadata,
        ModLoader.LoaderTask<string, MmcInstancePackageUpdateResult> task, CancellationToken token) {
        using var source = MmcArchiveSourceReader.ReadAsync(archive, cancellationToken: token).GetAwaiter().GetResult();
        task.Name = "解析新版整合包的 Java 要求";
        var resolved = MmcMetadataResolution.ResolveAsync(source.Package, metadata, cancellationToken: token).GetAwaiter().GetResult();
        if (!resolved.Resolution.IsComplete || resolved.Resolution.Profile is null)
            throw new IOException("无法解析新版 MMC 组件：" + string.Join("；", resolved.Resolution.Diagnostics.Select(value => value.Message)));
        return MmcInstances.SelectJavaAsync(source.Package, resolved.Resolution.Profile, token).GetAwaiter().GetResult();
    }
    private static MmcInstancePackageUpdatePreview RequirePreview(MmcInstancePackageUpdatePreviewResult result) =>
        result.Status == MmcInstanceBackendStatus.Succeeded && result.Preview is not null ? result.Preview :
        throw new IOException("MMC 更新预览未完成：" + result.Failure + (result.WorkingRoot is null ? "" : "\n准备目录已保留：" + result.WorkingRoot));
    private static MmcInstancePackageUpdateChoices Choose(MmcInstancePackageUpdatePreview preview) {
        var view = new MmcPackageUpdatePreviewView(preview, editable: true);
        return ModMain.MyMsgBoxConfirmContent("选择个人修改、组件覆盖和禁用状态。下一步会重新核对这些选择。", "选择 MMC 更新内容", "重新计算预览", () => view, IsWarn: false) == 1
            ? view.ReadChoices() : null;
    }
    private static int Confirm(MmcInstancePackageUpdatePreview preview) {
        var view = new MmcPackageUpdatePreviewView(preview, editable: false);
        if (!preview.Errors.IsEmpty || preview.RequiresChoices) {
            view.SetCanConfirm(true); // This button only returns to editing; it cannot commit.
            return ModMain.MyMsgBoxConfirmContent("所选内容还有错误或未确认的依赖，无法更新。请修改选择后重新核对。", "MMC 更新尚不能提交", "修改选择", () => view, IsWarn: false) == 1 ? 3 : 2;
        }
        return ModMain.MyMsgBoxConfirmContent("请确认重新核对后的最终结果。更新前会保存环境记录；Minecraft 版本变化时，还会先完成已有存档的备份。",
            "确认 MMC 更新结果", "按以上结果更新", () => view, ThirdText: "修改选择", IsWarn: true);
    }
    private static MmcInstanceInspection ReadSource(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance); instance.Config.AssertAttached(); var source = MmcInstanceInspector.Read(instance.PathVersion);
        if (!source.IsConsistent || source.Paths is null || source.Definition is null || source.PrimarySha256 is null ||
            instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
            throw new IOException(source.Error ?? "实例状态已变化，请重新打开版本页面。");
        return source;
    }
    private static MmcPackageUpdateServices CreateServices(string root) {
        string game = Path.GetDirectoryName(Path.GetDirectoryName(root))!, cache = Path.Combine(game, "PCL", "MmcCache");
        return new() { Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
            CacheRoots = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(cache, "sources"), Path.Combine(game, "versions")) };
    }
    private static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
    private sealed record OwnedCandidate(string Root, Guid OperationId) {
        internal static OwnedCandidate FromResult(string versionRoot, MmcInstancePackageUpdatePreviewResult result) {
            string root = result.Preview?.WorkingRoot ?? result.WorkingRoot;
            if (root is null) return null;
            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out var id)) throw new IOException("更新准备目录没有有效的操作身份。");
            string expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(versionRoot))!, "PCL", "MmcPackageUpdates", id.ToString("N"));
            if (!MeloongCore.PathUtils.IsSame(root, expected) || result.Preview is { } preview && preview.OperationId != id)
                throw new IOException("更新准备目录不属于本次操作。");
            return new(root, id);
        }
    }
    private sealed class Progress(ModLoader.LoaderTask<string, MmcInstancePackageUpdateResult> task, Action<MmcInstanceBackendProgress> observed) : IProgress<MmcInstanceBackendProgress> {
        public void Report(MmcInstanceBackendProgress value) {
            if (value.TotalBytes > 0) task.Progress = Math.Min(0.9, (double)value.CompletedBytes / value.TotalBytes * 0.9);
            observed?.Invoke(value);
        }
    }
}
