using System;
using System.IO;
using System.Linq;
using System.Threading;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
internal static class MmcDeleteUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Action<MmcInstanceLifecycleResult> Published { get; set; }
    // Button 1 is Cancel, button 2 is the explicitly destructive choice.
    internal static Func<string, string, bool, int> Confirm { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static Action<MmcLifecycleCheckpoint, string> CheckpointObserved { get; set; }
    internal static Action<MmcInstancePurgeCheckpoint, string> PermanentDeleteCheckpointObserved { get; set; }
    internal static MmcOperationCancellationFence AcquireOperationFence() {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 删除任务尚未完成。");
        return new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
    }

    internal static void PromptDelete(ModMinecraft.McInstance instance, bool permanentRequested, Action completed = null) {
        ArgumentNullException.ThrowIfNull(instance);
        string requestedRoot = instance.PathVersion;
        ModBase.RunInNewThread(() => {
            try {
                instance.Config.AssertAttached();
                if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("实例正在运行或被其他任务占用，不能永久删除。");
                var source = MmcInstanceInspector.Read(requestedRoot);
                if (!source.IsConsistent || source.Paths is null || source.Definition is null || source.PrimarySha256 is null ||
                    instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
                    throw new IOException(source.Error ?? "无法确认当前 MMC 实例，或存在未完成操作。已停止删除，请先完成或恢复相关任务。");
                var preview = new MmcInstanceLifecycleBackend().PreviewPermanentDeleteAsync(new(source.Paths.InstanceRoot,
                    source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256)).GetAwaiter().GetResult();
                string text = "永久删除 MMC 实例“" + Path.GetFileName(preview.SourceRoot) + "”？\n\n实例目录：" + preview.SourceRoot +
                    "\n将彻底删除整个实例，以及其中的所有存档、模组、配置、截图和实例私有文件。\n共 " +
                    preview.Entries.Count(entry => !entry.Directory) + " 个文件，" + preview.TotalBytes.ToString("N0") + " 字节。\n\n" +
                    "此操作不可恢复，不会进入回收站。请先备份需要保留的数据。";
                // No admission lease, configuration pause or object pin survives into this dialog.
                ModBase.RunInUi(() => {
                    try {
                        int choice = Confirm is not null ? Confirm(text, "警告：永久删除 MMC 实例", true)
                            : ModMain.MyMsgBox(text, "警告：永久删除 MMC 实例", "取消", "永久删除", IsWarn: true, HighLight: false);
                        if (choice != 2) return;
                        StartDeleteSnapshot(instance, preview, completed);
                    } catch (Exception error) { Report(error, "开始永久删除 MMC 实例失败"); }
                });
            } catch (Exception error) { Report(error, "无法永久删除 MMC 实例"); }
        }, "Preview permanent MMC deletion");
    }

    private static ModLoader.LoaderCombo<string> StartDeleteSnapshot(ModMinecraft.McInstance owner, MmcInstancePermanentDeletePreview preview, Action completed) {
        owner.Config.AssertAttached();
        var source = MmcInstanceInspector.Read(preview.SourceRoot);
        if (!MeloongCore.PathUtils.IsSame(owner.PathVersion, preview.SourceRoot) || ModMinecraft.McInstanceLock.IsLocked(owner) ||
            owner.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || owner.Config.ContextStamp.InstanceId != preview.InstanceId ||
            !source.IsConsistent || source.Definition?.InstanceId != preview.InstanceId || source.Definition.Generation != preview.Generation || source.PrimarySha256 != preview.PrimarySha256)
            throw new IOException("确认期间实例身份、位置或状态发生变化，已停止永久删除。请重新核对后再操作。");
        var fence = AcquireOperationFence();
        var worker = new MmcOperationWorker<string, MmcInstanceLifecycleResult>(fence, "永久删除 MMC 实例", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var result = new MmcInstanceLifecycleBackend().PermanentlyDeleteAsync(new(preview, preview.SnapshotFingerprint) {
                Checkpoint = (phase, path) => {
                    PermanentDeleteCheckpointObserved?.Invoke(phase, path);
                    if (phase == MmcInstancePurgeCheckpoint.BeforeDecision) {
                        CheckpointObserved?.Invoke(MmcLifecycleCheckpoint.BeforeDecision, preview.SourceRoot);
                        fence.EnterDecision(linked.Token);
                    } else if (phase == MmcInstancePurgeCheckpoint.CommitDecided) {
                        CheckpointObserved?.Invoke(MmcLifecycleCheckpoint.CommitDecided, preview.SourceRoot);
                    } else if (phase == MmcInstancePurgeCheckpoint.Completed) {
                        CheckpointObserved?.Invoke(MmcLifecycleCheckpoint.Completed, preview.SourceRoot);
                    }
                }
            }, linked.Token).GetAwaiter().GetResult();
            task.Output = result;
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(linked.Token);
            if (result.Status != MmcInstanceBackendStatus.Succeeded || !result.Purged)
                throw new IOException((result.PurgeDecided ? "永久删除已提交，收尾遇到问题：" : "永久删除未提交，实例内容已保留：") + result.Failure);
            if (Published is not null) Published(result);
            else ModBase.RunInUi(() => {
                if (ModMinecraft.McInstanceSelected is { } selected && MeloongCore.PathUtils.IsSame(selected.PathVersion, preview.SourceRoot) &&
                    selected.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed && selected.Config.ContextStamp.InstanceId == preview.InstanceId)
                    ModMinecraft.McInstanceSelected = null;
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                completed?.Invoke();
                ModMain.Hint("MMC 实例已永久删除。", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "永久删除 MMC 实例：" + Path.GetFileName(preview.SourceRoot), [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(preview.SourceRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }

    private static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
}
