using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
internal static class MmcLifecycleUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Action<MmcInstanceLifecycleResult> Published { get; set; }
    internal static Func<string, string, string> SelectRenameName { get; set; }
    internal static Func<string[], string> SelectMoveFolder { get; set; }
    internal static Func<string, string, bool> ConfirmMove { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static Action<MmcLifecycleCheckpoint> CheckpointObserved { get; set; }
    internal static Action<string> AutomaticRecoveryObserved { get; set; }

    internal static void PromptRename(ModMinecraft.McInstance instance, Action<ModMinecraft.McInstance> completed) {
        var source = ReadSource(instance);
        string parent = Path.GetDirectoryName(source.Paths.InstanceRoot)!;
        string name = SelectRenameName is not null ? SelectRenameName(instance.Name, parent) : ModMain.MyMsgBoxInput("重命名版本", "", instance.Name,
            new Collection<Validate> { new ValidateFolderName(Path.GetDirectoryName(source.Paths.InstanceRoot),
                IgnoreList: [instance.Name]) });
        if (string.IsNullOrWhiteSpace(name) || name == instance.Name) return;
        StartRenameSnapshot(instance, name, source, completed);
    }

    internal static ModLoader.LoaderCombo<string> StartRename(ModMinecraft.McInstance instance, string name,
        Action<ModMinecraft.McInstance> completed = null) => StartRenameSnapshot(instance, name, ReadSource(instance), completed);

    private static ModLoader.LoaderCombo<string> StartRenameSnapshot(ModMinecraft.McInstance instance, string name,
        MmcInstanceInspection source, Action<ModMinecraft.McInstance> completed) {
        string parent = Path.GetDirectoryName(source.Paths.InstanceRoot)!;
        string error = new ValidateFolderName(parent).Validate(name);
        if (!string.IsNullOrEmpty(error)) throw new IOException("版本名称无效：" + error);
        var request = new MmcInstanceRelocationRequest(source.Paths.InstanceRoot, Path.Combine(parent, name),
            source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256, MmcInstanceRelocationKind.Rename);
        return StartRelocation(instance, request, completed);
    }

    internal static void PromptMove(ModMinecraft.McInstance instance, Action<ModMinecraft.McInstance> completed = null) {
        ArgumentNullException.ThrowIfNull(instance);
        // Folder selection and confirmation never retain a backend lease.
        var folders = ModMinecraft.McFolderList.ToArray();
        ModBase.RunInNewThread(() => {
            try {
                var source = ReadSource(instance);
                string currentFolder = Path.GetDirectoryName(Path.GetDirectoryName(source.Paths.InstanceRoot))!;
                var available = folders.Where(folder => !MeloongCore.PathUtils.IsSame(folder.Location, currentFolder)).ToArray();
                ModBase.RunInUi(() => {
                    try {
                        if (available.Length == 0) { ModMain.MyMsgBox("请先在版本选择页添加另一个游戏文件夹。MMC 实例当前支持移动到同一卷中的游戏文件夹。", "移动 MMC 实例"); return; }
                        string folder;
                        if (SelectMoveFolder is not null) folder = SelectMoveFolder(available.Select(value => value.Location).ToArray());
                        else {
                            int? index = ModMain.SelectChoice(available.Select(value => new DialogChoice(value.Name, value.Location)),
                                "选择移动目标游戏文件夹", "下一步", "取消");
                            if (index is null || index < 0 || index >= available.Length) return;
                            folder = available[index.Value].Location;
                        }
                        if (string.IsNullOrWhiteSpace(folder)) return;
                        string target = MoveTarget(source, folder);
                        bool accepted = ConfirmMove is not null ? ConfirmMove(source.Paths.InstanceRoot, target) :
                            ModMain.MyMsgBox("将实例“" + instance.Name + "”及其存档、模组和配置移动到：\n" + target +
                                "\n\n保留实例身份，已有同名内容不会覆盖。仅支持同卷移动。", "移动 MMC 实例", "移动", "取消", IsWarn: false) == 1;
                        if (accepted) StartRelocation(instance, new(source.Paths.InstanceRoot, target, source.Definition.InstanceId,
                            source.Definition.Generation, source.PrimarySha256, MmcInstanceRelocationKind.Move), completed);
                    } catch (Exception error) { Report(error, "无法开始移动 MMC 实例"); }
                });
            } catch (Exception error) { Report(error, "无法读取 MMC 移动来源"); }
        }, "Preview MMC move");
    }

    internal static ModLoader.LoaderCombo<string> StartMove(ModMinecraft.McInstance instance, string minecraftFolder,
        Action<ModMinecraft.McInstance> completed = null) {
        var source = ReadSource(instance);
        return StartRelocation(instance, new(source.Paths.InstanceRoot, MoveTarget(source, minecraftFolder), source.Definition.InstanceId,
            source.Definition.Generation, source.PrimarySha256, MmcInstanceRelocationKind.Move), completed);
    }
    private static string MoveTarget(MmcInstanceInspection source, string minecraftFolder) {
        string root = Path.GetFullPath(minecraftFolder);
        if (!Directory.Exists(root)) throw new IOException("目标游戏文件夹不存在。");
        // The public path resolver performs the host's directory-role and full
        // ancestor checks; UI must not duplicate its internal validation rules.
        return MmcInstancePaths.Resolve(Path.Combine(root, "versions", Path.GetFileName(source.Paths.InstanceRoot))).InstanceRoot;
    }
    private static ModLoader.LoaderCombo<string> StartRelocation(ModMinecraft.McInstance instance,
        MmcInstanceRelocationRequest request, Action<ModMinecraft.McInstance> completed) {
        bool move = request.Kind == MmcInstanceRelocationKind.Move;
        string action = move ? "移动" : "重命名";
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 实例管理任务尚未完成。");
        var fence = new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
        var worker = new MmcOperationWorker<string, MmcInstanceLifecycleResult>(fence, action + " MMC 实例", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            MmcInstanceRelocationRecovery recovery = null;
            var backend = new MmcInstanceLifecycleBackend();
            var result = backend.RelocateAsync(request with {
                Checkpoint = (phase, directory) => {
                    if (phase == MmcLifecycleCheckpoint.IntentPrepared) recovery = MmcInstanceLifecycleBackend.CaptureRelocationRecovery(request, directory);
                    CheckpointObserved?.Invoke(phase);
                    if (phase == MmcLifecycleCheckpoint.BeforeDecision) fence.EnterDecision(linked.Token);
                }
            }, linked.Token).GetAwaiter().GetResult();
            // RelocateAsync has returned: every artifact producer, admission lease and
            // configuration pause from that invocation is gone before any replay starts.
            if (result.Status == MmcInstanceBackendStatus.NeedsRecovery && recovery is not null &&
                string.Equals(result.OperationDirectory, recovery.OperationDirectory, StringComparison.OrdinalIgnoreCase) &&
                MeloongCore.PathUtils.IsSame(instance.PathVersion, request.SourceRoot) &&
                instance.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed && instance.Config.ContextStamp.InstanceId == request.ExpectedInstanceId) {
                AutomaticRecoveryObserved?.Invoke(recovery.OperationDirectory);
                var recovered = backend.CompleteRelocationFailureAsync(recovery).GetAwaiter().GetResult();
                result = recovered.Status == MmcInstanceBackendStatus.Cancelled ? recovered with { Failure = result.Failure } : recovered;
            }
            task.Output = result;
            if (result.Status == MmcInstanceBackendStatus.Cancelled) {
                if (!linked.IsCancellationRequested && result.Failure is not null) throw new IOException(action + "未提交，已自动撤回，原实例已保留：" + result.Failure);
                throw new OperationCanceledException(linked.Token);
            }
            if (result.Status != MmcInstanceBackendStatus.Succeeded || result.CurrentRoot is null)
                throw new IOException(action + "未完成，自动收尾未能核实安全状态，现场已保留：" + result.Failure);
            if (Published is not null) Published(result);
            else ModBase.RunInUi(() => {
                var renamed = new ModMinecraft.McInstance(result.CurrentRoot).Load();
                if (ModMinecraft.McInstanceSelected is { } selected && MeloongCore.PathUtils.IsSame(selected.PathVersion, instance.PathVersion) &&
                    selected.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed && selected.Config.ContextStamp.InstanceId == request.ExpectedInstanceId)
                    ModMinecraft.McInstanceSelected = move ? null : renamed;
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                completed?.Invoke(renamed);
                ModMain.Hint(action + "成功！", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, action + " MMC 实例：" + instance.Name, [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.RefreshTaskButton()); }
            combo.Start(request.SourceRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }

    private static MmcInstanceInspection ReadSource(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);
        instance.Config.AssertAttached();
        var source = MmcInstanceInspector.Read(instance.PathVersion);
        if (!source.IsConsistent || source.Definition is null || source.Paths is null || source.PrimarySha256 is null ||
            instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
            throw new IOException(source.Error ?? "MMC 实例状态已变化或需要先修复，请重新打开版本页面。");
        return source;
    }
    private static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
}
