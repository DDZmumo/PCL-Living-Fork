using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public sealed class MmcLegacyAdoptionUiServices {
    public MmcMetadataStore Metadata { get; init; }
    public MmcArtifactCacheRoots CacheRoots { get; init; }
    public Func<ModMinecraft.McInstance, string> SelectArchive { get; init; }
    public Func<string, string, string> SelectName { get; init; }
    public Func<MmcLegacyAdoptionPreview, string, MmcLegacyAdoptionIntent?> ConfirmPreview { get; init; }
    public Action<MmcLegacyAdoptionCheckpoint, string> CheckpointObserved { get; init; }
    public Action<ModLoader.LoaderCombo<string>> RegisterTask { get; init; }
    public Action<MmcLegacyAdoptionResult> Published { get; init; }
    public Action<MmcLegacyAdoptionProgress> ProgressObserved { get; init; }
    public Action<ModNet.LoaderDownload> DownloadCreated { get; init; }
}

internal static class MmcLegacyAdoptionUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Func<string, MmcLegacyAdoptionUiServices> ServicesFactory { get; set; }
    internal static void PromptCreateCopy(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Unmanaged) throw new IOException("此入口仅用于主动选择未受 MMC 管理的旧实例；原目录不会被接管。");
        string minecraft = ModMinecraft.McFolderSelected;
        var services = ServicesFactory?.Invoke(minecraft) ?? Services(minecraft);
        string zip = services.SelectArchive is not null ? services.SelectArchive(instance) : MeloongCore.Wpf.Dialogs.SelectFile("选择对应原始 MMC ZIP", false,
            filter: new[] { (new[] { "zip" }, "MMC 原始整合包") }).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(zip)) return;
        string name = services.SelectName is not null ? services.SelectName(minecraft, instance.Name + "-MMC") : ModMain.MyMsgBoxInput("创建 MMC 管理副本（保留旧实例）", "选择新的版本名称。旧实例不改名、不接管。", instance.Name + "-MMC",
            new Collection<Validate> { new ValidateFolderName(Path.Combine(minecraft, "versions")) });
        if (string.IsNullOrWhiteSpace(name)) return;
        string error = new ValidateFolderName(Path.Combine(minecraft, "versions")).Validate(name);
        if (!string.IsNullOrEmpty(error)) throw new IOException(error);
        StartCreateCopy(instance, zip, minecraft, name, services);
    }
    internal static ModLoader.LoaderCombo<string> StartCreateCopy(ModMinecraft.McInstance instance, string zip, string minecraft, string name,
        MmcLegacyAdoptionUiServices services) {
        EnterBusy(); var fence = new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
        RedoableWorker sourceWorker = null;
        var worker = new MmcOperationWorker<string, MmcLegacyAdoptionResult>(fence, "读取旧实例与原包", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            task.Output = CreateAsync(instance, zip, minecraft, name, services, task, sourceWorker, fence, linked.Token).GetAwaiter().GetResult();
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "创建 MMC 管理副本：" + name, [worker]);
        sourceWorker = new RedoableWorker(LoaderFileOperation.CreateWorkload(combo, instance.PathVersion));
        return Register(combo, instance.PathVersion, services, fence, sourceWorker);
    }
    private static async Task<MmcLegacyAdoptionResult> CreateAsync(ModMinecraft.McInstance instance, string zip, string minecraft, string name,
        MmcLegacyAdoptionUiServices services, ModLoader.LoaderTask<string, MmcLegacyAdoptionResult> task, IWorker sourceWorker, MmcOperationCancellationFence fence, CancellationToken token) {
        MmcArchiveSource source = null; MmcLegacyAdoptionPreview preview = null;
        try {
            source = await MmcArchiveSourceReader.ReadAsync(zip, cancellationToken: token).ConfigureAwait(false);
            var resolved = await MmcMetadataResolution.ResolveAsync(source.Package, services.Metadata, cancellationToken: token).ConfigureAwait(false);
            if (!resolved.Resolution.IsComplete || resolved.Resolution.Profile is null) throw new IOException("无法完整解析所选原包组件。");
            var environment = await MmcInstances.SelectJavaAsync(source.Package, resolved.Resolution.Profile, token).ConfigureAwait(false);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathVersion));
            bool mc = Directory.Exists(Path.Combine(root, "minecraft")), dot = Directory.Exists(Path.Combine(root, ".minecraft"));
            if (mc && dot) throw new IOException("旧实例有两棵游戏树，不能猜测或合并。");
            string game = mc ? Path.Combine(root, "minecraft") : dot ? Path.Combine(root, ".minecraft") : root;
            var service = Backend(services, task, root, sourceWorker);
            var progress = new InlineProgress(services, task);
            preview = await service.PreviewPreparedAsync(source, new(root, game, zip, environment), resolved, token, progress).ConfigureAwait(false); source = null;
            var intent = ModBase.RunInUiWait(() => {
                if (!PathUtils.IsSame(minecraft, ModMinecraft.McFolderSelected)) { ModMain.Hint("游戏文件夹已切换，请重新打开管理副本预览。", ModMain.HintType.Blue); return (MmcLegacyAdoptionIntent?)null; }
                return services.ConfirmPreview is not null ? services.ConfirmPreview(preview, name) : Confirm(preview, name);
            });
            if (intent is null) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested(); string target = Path.Combine(minecraft, "versions", name);
            var result = await service.CreateManagedCopyAutomaticallyAsync(preview, new(target, name, preview.ConfirmationFingerprint(target, name, intent.Value), intent.Value) {
                Checkpoint = (phase, directory) => {
                    if (phase == MmcLegacyAdoptionCheckpoint.BeforeDecision) fence.EnterDecision(token);
                    services.CheckpointObserved?.Invoke(phase, directory);
                }
            }, token, progress).ConfigureAwait(false);
            task.Output = result;
            CheckResult(result, token); Publish(result, minecraft, services); return result;
        } finally { preview?.Dispose(); source?.Dispose(); }
    }
    private static MmcLegacyAdoptionIntent? Confirm(MmcLegacyAdoptionPreview preview, string name) {
        int choice = ModMain.MyMsgBoxConfirmContent("先核对旧/新组件及逐文件差异。将创建新的管理副本，旧实例保持原来的普通管理方式。", "旧 MMC 管理副本预览",
            preview.SourceCheck.HistoricalSourceVerified ? "验证来源并创建副本" : "查看来源问题", () => new MmcLegacyAdoptionPreviewView(preview, name), "取消", "以所选原包重建来源", WarningButton: 3);
        if (choice == 1 && preview.SourceCheck.HistoricalSourceVerified) return MmcLegacyAdoptionIntent.StrictVerified;
        if (choice == 1) { ModMain.MyMsgBox(preview.SourceCheck.Explanation, "严格来源验证不可用", IsWarn: true); return null; }
        if (choice != 3) return null;
        if (ModMain.MyMsgBox("明确以你本次选择的原 MMC ZIP 作为新来源创建管理副本？\n旧实例和当前数据保留；当前文件只作为个人快照。\n此操作不声称验证旧历史来源或运行兼容性。", "确认新来源重绑定", "确认按新来源创建副本", "取消", IsWarn: true) != 1) return null;
        return MmcLegacyAdoptionIntent.CreateCopyWithExplicitSourceRebinding;
    }
    private static MmcLegacyAdoptionService<ModNet.NetFile> Backend(MmcLegacyAdoptionUiServices services, ModLoader.LoaderTask<string, MmcLegacyAdoptionResult> worker, string sourceRoot, IWorker sourceWorker = null) => new(services.Metadata, services.CacheRoots,
        MmcDownloads.BuildFile, (files, token) => MmcDownloads.RunBatchAsync(files, token, worker.CreateSyncProgressProvider(), services.DownloadCreated), new() {
            ObserveActivity = (root, game) => {
                try { return ModWatcher.HasRunningMinecraft || QuickPlayWorlds.HasRunningSessions() || WorldBackups.IsBusy || WorldBackups.HasActiveGame(game) || ModLaunch.McLaunchLoader?.State == ModBase.LoadState.Loading || ModpackUpdate.Pending(root).Any()
                    ? MmcLegacyAdoptionActivity.Active : MmcLegacyAdoptionActivity.Idle; } catch { return MmcLegacyAdoptionActivity.Unknown; }
            }, AcquireLegacyPathLock = root => sourceRoot is not null && sourceWorker is not null && PathUtils.IsSame(root, sourceRoot) ? new SourceLease(sourceWorker, root) : throw new IOException("没有旧源路径修改租约。")
        });
    private static ModLoader.LoaderCombo<string> Register(ModLoader.LoaderCombo<string> combo, string input, MmcLegacyAdoptionUiServices services, MmcOperationCancellationFence fence, IWorker driver = null) {
        try { if (services.RegisterTask is not null) services.RegisterTask(combo); else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); } if (driver is null) combo.Start(input); else driver.Start(); return combo; }
        catch { fence.AbandonUnstarted(); throw; }
    }
    private static void CheckResult(MmcLegacyAdoptionResult result, CancellationToken token) {
        if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(result.Failure, token);
        if (result.Status != MmcInstanceBackendStatus.Succeeded || result.Paths is null) throw new IOException("管理副本未完成，旧实例保持原样：" + result.Failure + (result.OperationDirectory is null ? "" : "\n内部操作证据：" + result.OperationDirectory));
    }
    private static void Publish(MmcLegacyAdoptionResult result, string minecraft, MmcLegacyAdoptionUiServices services) {
        if (services.Published is not null) { services.Published(result); return; }
        ModBase.RunInUi(() => { ModMinecraft.McInstanceListForceRefresh = true; if (PathUtils.IsSame(minecraft, ModMinecraft.McFolderSelected)) ModMinecraft.McInstanceListLoader.Start(minecraft, IsForceRestart: true); ModMain.Hint("MMC 管理副本已创建，旧实例与私有备份保留。", ModMain.HintType.Green); });
    }
    private static void EnterBusy() { if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项旧 MMC 副本任务尚未结束。"); }
    private static MmcLegacyAdoptionUiServices Services(string minecraft) {
        string cache = Path.Combine(minecraft, "PCL", "MmcCache");
        return new() { Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")), CacheRoots = new(Path.Combine(minecraft, "libraries"), Path.Combine(minecraft, "assets"), Path.Combine(cache, "sources"), Path.Combine(minecraft, "versions")) };
    }
    private sealed class SourceLease : IDisposable {
        private readonly IWorker worker; private readonly string root;
        internal SourceLease(IWorker worker, string root) { this.worker = worker; this.root = root; if (!ModMinecraft.McInstanceLock.TryAcquire(worker, root)) throw new IOException("旧实例正在被其它任务使用。"); }
        public void Dispose() => ModMinecraft.McInstanceLock.Release(worker, root);
    }
    private sealed class InlineProgress(MmcLegacyAdoptionUiServices services, ModLoader.LoaderTask<string, MmcLegacyAdoptionResult> task) : IProgress<MmcLegacyAdoptionProgress> {
        public void Report(MmcLegacyAdoptionProgress value) { task.Name = value.Phase switch { "snapshot-legacy-files" => "校验旧实例快照", "compare-original-package" => "比较原包文件", "backup-legacy-snapshot" => "备份旧实例", "copy-personal-snapshot" => "复制个人数据", _ => "创建 MMC 管理环境" }; if (value.TotalBytes > 0) task.Progress = Math.Min(0.95, (double)value.CompletedBytes / value.TotalBytes); services.ProgressObserved?.Invoke(value); }
    }
}
