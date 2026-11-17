using System;
using System.IO;
using System.Threading;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
internal static class MmcCloneUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Action<MmcInstanceCloneResult> Published { get; set; }

    internal static ModLoader.LoaderCombo<string> Start(ModMinecraft.McInstance instance, string name, InstanceCloneOptions options) {
        ArgumentNullException.ThrowIfNull(instance);
        var source = MmcInstanceInspector.Read(instance.PathVersion);
        if (!source.IsConsistent || source.Definition is null || source.Paths is null || source.PrimarySha256 is null)
            throw new IOException(source.Error ?? "MMC 实例需要先修复，再创建副本。");
        string parent = Path.GetDirectoryName(source.Paths.InstanceRoot)!;
        string error = new ValidateFolderName(parent).Validate(name);
        if (!string.IsNullOrEmpty(error)) throw new IOException("副本名称无效：" + error);
        if (Interlocked.CompareExchange(ref InstanceClonerUi.Service.BusyCount, 1, 0) != 0)
            throw new IOException("另一项克隆或迁移任务尚未完成。");
        Interlocked.Exchange(ref busy, 1);
        var fence = new MmcOperationCancellationFence(() => {
            Interlocked.Exchange(ref busy, 0);Interlocked.Exchange(ref InstanceClonerUi.Service.BusyCount, 0);
            ModBase.RunInUi(InstanceClonerUi.ManagedChanged);
        });
        var request = new MmcInstanceCloneRequest(source.Paths.InstanceRoot, Path.Combine(parent, name), name,
            source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256,
            new(options?.CopySaves == true, options?.CopyScreenshots == true, options?.CopyServers == true));
        var worker = new MmcOperationWorker<string, MmcInstanceCloneResult>(fence, "创建 MMC 独立副本", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var result = new MmcInstanceCloneBackend().CloneAsync(request with {
                TransactionCheckpoint = (phase, _) => { if (phase == MmcTransactionCheckpoint.BeforeDecision) fence.EnterDecision(linked.Token); }
            }, linked.Token, new Progress(task)).GetAwaiter().GetResult();
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(linked.Token);
            if (result.Status != MmcInstanceBackendStatus.Succeeded || result.Paths is null)
                throw new IOException("MMC 副本未完成创建，现场已保留：" + result.Failure);
            task.Output = result;
            if (Published is not null) Published(result);
            else ModBase.RunInUi(() => {
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                ModMain.Hint("已创建独立副本：" + name, ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "克隆 MMC 实例：" + name, [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(source.Paths.InstanceRoot);
            return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }
    private sealed class Progress(ModLoader.LoaderTask<string, MmcInstanceCloneResult> task) : IProgress<MmcInstanceCloneProgress> {
        public void Report(MmcInstanceCloneProgress value) {
            task.Name = value.Phase == "copying-clone-files" ? "复制实例文件" : "保存 MMC 副本";
            if (value.TotalFiles > 0) task.Progress = Math.Min(0.99, (double)value.CompletedFiles / value.TotalFiles);
        }
    }
}
