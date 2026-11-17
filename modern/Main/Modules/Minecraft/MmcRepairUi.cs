using System;
using System.IO;
using System.Threading;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public sealed class MmcRepairServices {
    public MmcMetadataStore Metadata { get; init; }
    public MmcArtifactCacheRoots CacheRoots { get; init; }
    public Action<ModLoader.LoaderCombo<string>> RegisterTask { get; init; }
    public Action<MmcInstanceBackendResult> Published { get; init; }
    public Action<ModNet.LoaderDownload> DownloadCreated { get; init; }
    public Action<MmcTransactionCheckpoint, int> TransactionObserved { get; init; }
    public Action<MmcInstanceBackendProgress> ProgressObserved { get; init; }
    public Func<MmcProjectionDivergence, bool> ConfirmProjectionRebuild { get; init; }
    public Action<Exception, string> ErrorObserved { get; init; }
}
internal static class MmcRepairUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Func<ModMinecraft.McInstance, MmcRepairServices> ServicesFactory { get; set; }
    internal static void Prompt(ModMinecraft.McInstance instance) {
        try { Start(instance); }
        catch (IOException original) {
            var stamp = instance.Config.ContextStamp;
            if (stamp.Kind != MmcConfigurationContextKind.Managed) throw;
            MmcInstanceRepairSelection divergence;
            try { divergence = MmcInstanceRepairSelections.CaptureDivergedProjection(instance.PathVersion, stamp.InstanceId); }
            catch { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw(); return; }
            if (HasActiveOperation) throw;
            var services = ServicesFactory?.Invoke(instance) ?? CreateServices(divergence.VersionRoot);
            // Inspect outside UI and outside all user dialogs. No changed JSON is
            // used as a new author patch; explicit reconstruction preserves its bytes.
            ModBase.RunInNewThread(() => {
                try {
                    var inspected = MmcInstanceRepairSelections.InspectDivergedProjectionAsync(instance.PathVersion, stamp.InstanceId).GetAwaiter().GetResult();
                    var capturedServices = services;
                    ModBase.RunInUi(() => {
                        try {
                            bool accepted = capturedServices.ConfirmProjectionRebuild is not null ? capturedServices.ConfirmProjectionRebuild(inspected) :
                                ModMain.MyMsgBoxConfirmContent("版本 JSON 已被外部修改。请先核对当前内容与保存的组件定义；确认后会保留当前文件副本，再从原定义重建。",
                                    "检查 MMC 版本配置变化", "保留副本并重建", () => new MmcProjectionReviewView(inspected), IsWarn: true) == 1;
                            if (accepted) StartRebuild(instance, inspected, capturedServices);
                        } catch (Exception error) { Report(capturedServices, error, "无法重建 MMC 版本配置"); }
                    });
                } catch { Report(services, original, "MMC 文件补全未开始"); }
            }, "Inspect changed MMC projection");
        }
    }
    internal static ModLoader.LoaderCombo<string> Start(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再补全文件。");
        var stamp = instance.Config.ContextStamp;
        if (stamp.Kind == MmcConfigurationContextKind.Unmanaged) throw new IOException("实例管理状态已变化，请重新打开版本页面。");
        var selected = MmcInstanceRepairSelections.Capture(instance.PathVersion,
            stamp.Kind == MmcConfigurationContextKind.Managed ? stamp.InstanceId : null,
            requireMissingProjection: stamp.Kind == MmcConfigurationContextKind.Unresolved);
        var services = ServicesFactory?.Invoke(instance) ?? CreateServices(selected.VersionRoot);
        return StartSelected(instance, selected, services, rebuild: false);
    }
    internal static ModLoader.LoaderCombo<string> StartRebuild(ModMinecraft.McInstance instance, MmcProjectionDivergence inspected, MmcRepairServices services) {
        if (inspected.Selection.DivergedProjectionSha256 != inspected.ObservedProjectionSha256 || inspected.Selection.MissingProjection ||
            !MeloongCore.PathUtils.IsSame(instance.PathVersion, inspected.Selection.VersionRoot) ||
            instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != inspected.Selection.InstanceId)
            throw new IOException("已检查的版本配置不属于当前实例。");
        return StartSelected(instance, inspected.Selection, services, rebuild: true);
    }
    private static ModLoader.LoaderCombo<string> StartSelected(ModMinecraft.McInstance instance, MmcInstanceRepairSelection selected, MmcRepairServices services, bool rebuild) {
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再补全文件。");
        if (services.Metadata is null || services.CacheRoots is null) throw new IOException("MMC 补全缺少下载和元数据服务。");
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 文件补全尚未完成。");
        var fence = new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
        var worker = new MmcOperationWorker<string, MmcInstanceBackendResult>(fence, "核对 MMC 已采用的运行环境", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var environment = MmcInstanceRepairSelections.ResolvePublishedEnvironmentAsync(selected, linked.Token).GetAwaiter().GetResult();
            var progress = new Progress(task, services.ProgressObserved);
            var backend = new MmcInstanceBackend<ModNet.NetFile>(services.Metadata, services.CacheRoots, MmcDownloads.BuildFile,
                (files, token) => MmcDownloads.RunBatchAsync(files, token, onLoaderCreated: services.DownloadCreated));
            var result = backend.RepairAsync(new(selected.VersionRoot, environment, selected.InstanceId, AllowRebuildDivergedProjection: rebuild) {
                ExpectedGeneration = selected.Generation, ExpectedPrimarySha256 = selected.PrimarySha256, StrictSelection = selected,
                TransactionCheckpoint = (phase, index) => {
                    services.TransactionObserved?.Invoke(phase, index);
                    if (phase == MmcTransactionCheckpoint.BeforeDecision) fence.EnterDecision(linked.Token);
                }
            }, linked.Token, progress).GetAwaiter().GetResult(); task.Output = result;
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(linked.Token);
            if (result.Status != MmcInstanceBackendStatus.Succeeded)
                throw new IOException("MMC 文件补全未完成，原配置及恢复现场已保留：" + result.Failure);
            if (services.Published is not null) services.Published(result);
            else ModBase.RunInUi(() => {
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                ModMain.Hint("MMC 文件补全成功。", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, instance.Name + " MMC 文件补全", [worker]);
        try {
            if (services.RegisterTask is not null) services.RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(selected.VersionRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }
    private static void Report(MmcRepairServices services, Exception error, string message) {
        if (services?.ErrorObserved is not null) services.ErrorObserved(error, message);
        else MeloongCore.Logger.Error(error, message, MeloongCore.LogBehavior.Alert);
    }
    private static MmcRepairServices CreateServices(string versionRoot) {
        string gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(versionRoot))!;
        string cache = Path.Combine(gameRoot, "PCL", "MmcCache");
        return new() { Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
            CacheRoots = new(Path.Combine(gameRoot, "libraries"), Path.Combine(gameRoot, "assets"), Path.Combine(cache, "sources"), Path.Combine(gameRoot, "versions")) };
    }
    private sealed class Progress(ModLoader.LoaderTask<string, MmcInstanceBackendResult> task, Action<MmcInstanceBackendProgress> observed) : IProgress<MmcInstanceBackendProgress> {
        public void Report(MmcInstanceBackendProgress value) {
            task.Name = value.Phase switch {
                "loading-immutable-sources" => "读取已保存的 MMC 组件与补丁", "prefetching-artifacts" => "补全 MMC 运行文件",
                "committing-instance" => "保存 MMC 补全结果", _ => "校验 MMC 运行文件"
            };
            if (value.TotalBytes > 0) task.Progress = Math.Min(0.99, (double)value.CompletedBytes / value.TotalBytes);
            observed?.Invoke(value);
        }
    }
}
