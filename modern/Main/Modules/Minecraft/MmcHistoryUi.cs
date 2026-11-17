using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
internal static class MmcHistoryUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Action<MmcInstanceHistoryCaptureResult> Captured { get; set; }
    internal static Action<MmcInstanceHistoryRestoreResult> Restored { get; set; }
    internal static Action<MmcInstanceHistoryCheckpoint> CaptureCheckpointObserved { get; set; }
    internal static Action<MmcTransactionCheckpoint, int> CheckpointObserved { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static Func<string> CaptureLabel { get; set; }
    internal static Func<MmcInstanceHistoryPoint, bool, bool> ConfirmRestore { get; set; }
    internal static Action<string, string> ShowDetails { get; set; }
    internal static event Action<string> Changed;
    internal static event Action<string, MmcInstanceHistoryCaptureResult> CaptureCompleted;
    internal static event Action<string, MmcInstanceHistoryRestoreResult> RestoreCompleted;

    internal static string RequestCaptureLabel() => CaptureLabel is not null ? CaptureLabel()
        : ModMain.MyMsgBoxInput("新建 MMC 环境记录", "为当前环境填写备注。", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

    internal static bool RequestRestoreConfirmation(MmcInstanceHistoryPoint point, bool undo = false) => ConfirmRestore is not null
        ? ConfirmRestore(point, undo)
        : ModMain.MyMsgBox((undo ? "撤回上次还原，恢复环境记录“" : "还原环境记录“") + point.Label + "”？\n当前存档、截图、地图标记和实例偏好会保留。\n操作前会自动保存当前环境，供以后撤回。",
            undo ? "撤回上次 MMC 环境还原" : "确认还原 MMC 环境", undo ? "撤回还原" : "还原环境", "取消", IsWarn: true) == 1;

    internal static void DisplayDetails(string text, string title) {
        if (ShowDetails is not null) ShowDetails(text, title);
        else ModMain.MyMsgBox(text, title);
    }

    internal static void Prompt(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);
        ModBase.RunInNewThread(() => {
            try {
                var source = ReadSource(instance);
                var points = new MmcInstanceHistoryBackend().ListAsync(source.Paths.InstanceRoot).GetAwaiter().GetResult();
                ModBase.RunInUi(() => {
                    try {
                        int choice = ModMain.MyMsgBox("环境记录保存组件、补丁、运行库和环境文件。还原会先自动保存当前环境；当前存档、截图及实例偏好会保留。",
                            "MMC 环境记录", "新建记录", "查看与还原", "取消", IsWarn: false);
                        if (choice == 1) {
                            string label = RequestCaptureLabel();
                            if (label is null) return;
                            StartCapture(new(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256, label));
                        } else if (choice == 2) SelectRestore(source, points);
                    } catch (Exception error) { Report(error, "无法开始 MMC 环境操作"); }
                });
            } catch (Exception error) { Report(error, "读取 MMC 环境记录失败"); }
        }, "Read MMC environment history");
    }

    private static void SelectRestore(MmcInstanceInspection source, ImmutableArray<MmcInstanceHistoryPoint> points) {
        if (points.IsEmpty) { ModMain.MyMsgBox("尚无可还原的 MMC 环境记录。", "MMC 环境记录"); return; }
        int? selected = ModMain.MyMsgBoxSelect(points.Select(point => (IMyRadio)new MyRadioBox {
            Text = point.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + point.Label,
            ToolTip = "环境文件：" + point.FileCount + "；记录代数：" + point.Generation
        }), "选择 MMC 环境记录", "下一步", "取消");
        if (selected is null || selected < 0 || selected >= points.Length) return;
        var chosen = points[selected.Value];
        if (!RequestRestoreConfirmation(chosen)) return;
        StartRestore(new(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256,
            chosen.PointId, chosen.ManifestSha256));
    }

    internal static ModLoader.LoaderCombo<string> StartCapture(MmcInstanceHistoryCaptureRequest request) => Start("记录 MMC 环境", request.VersionRoot, (task, token, fence) => {
        var result = new MmcInstanceHistoryBackend().CaptureAsync(request with { FullSafeEnvironment = true, Checkpoint = phase => {
            CaptureCheckpointObserved?.Invoke(phase); request.Checkpoint?.Invoke(phase);
            if (phase == MmcInstanceHistoryCheckpoint.SourcesCaptured) fence.EnterDecision(token);
        } }, token).GetAwaiter().GetResult(); task.Output = result;
        ModBase.RunInUi(() => CaptureCompleted?.Invoke(request.VersionRoot, result));
        if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new IOException("环境记录未完成：" + result.Failure);
        if (Captured is not null) Captured(result);
        else ModBase.RunInUi(() => ModMain.Hint("已记录当前 MMC 环境。", ModMain.HintType.Green));
    });

    internal static ModLoader.LoaderCombo<string> StartRestore(MmcInstanceHistoryRestoreRequest request) => Start("还原 MMC 环境", request.VersionRoot, (task, token, fence) => {
        var result = new MmcInstanceHistoryBackend().RestoreAsync(request with {
            TransactionCheckpoint = (phase, index) => {
                CheckpointObserved?.Invoke(phase, index); request.TransactionCheckpoint?.Invoke(phase, index);
                if (phase == MmcTransactionCheckpoint.BeforeDecision) fence.EnterDecision(token);
            }
        }, token).GetAwaiter().GetResult(); task.Output = result;
        ModBase.RunInUi(() => RestoreCompleted?.Invoke(request.VersionRoot, result));
        if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
        if (result.Status != MmcInstanceBackendStatus.Succeeded)
            throw new IOException("环境还原尚未完成，内容已保留：" + result.Failure);
        if (Restored is not null) Restored(result);
        else ModBase.RunInUi(() => {
            ModMinecraft.McInstanceListForceRefresh = true;
            ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
            ModMain.Hint("MMC 环境已还原，当前存档和偏好已保留。", ModMain.HintType.Green);
        });
    });

    private static ModLoader.LoaderCombo<string> Start(string name, string root,
        Action<ModLoader.LoaderTask<string, object>, CancellationToken, MmcOperationCancellationFence> work) {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 环境操作尚未完成。");
        var fence = new MmcOperationCancellationFence(() => {
            Interlocked.Exchange(ref busy, 0);
            ModBase.RunInUi(() => Changed?.Invoke(root));
        });
        var worker = new MmcOperationWorker<string, object>(fence, name, task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            work(task, linked.Token, fence);
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, name, [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(root); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }
    private static MmcInstanceInspection ReadSource(ModMinecraft.McInstance instance) {
        instance.Config.AssertAttached(); var source = MmcInstanceInspector.Read(instance.PathVersion);
        if (!source.IsConsistent || source.Paths is null || source.Definition is null || source.PrimarySha256 is null ||
            instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
            throw new IOException(source.Error ?? "实例状态已变化，请重新打开版本页面。");
        return source;
    }
    internal static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
}
