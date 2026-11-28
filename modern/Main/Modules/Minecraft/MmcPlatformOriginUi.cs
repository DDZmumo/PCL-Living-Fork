using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host UI; repository LICENCE applies. The supplied catalog must come from the official metadata bridge.
internal static class MmcPlatformOriginUi {
    internal static Func<ImmutableArray<MmcPlatformReleaseDescriptor>, string, string, MmcPlatformReleaseDescriptor> SelectRelease { get; set; }
    internal static Func<MmcPlatformOriginPreview, int> ConfirmOrigin { get; set; }
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Action<MmcPlatformOriginResult> Published { get; set; }
    internal static Action<MmcTransactionCheckpoint, int> TransactionObserved { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static Func<MmcInstanceOrigin, MmcPlatformOriginInput> ReadOriginInput { get; set; }

    internal static void Prompt(ModMinecraft.McInstance instance, MmcPlatformOriginCatalog catalog) {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(catalog);
        ModBase.RunInNewThread(() => {
            try {
                if (MmcPackageUpdateUi.HasActiveOperation) throw new IOException("另一项 MMC 来源核实或整合包更新尚未完成。");
                var source = MmcPackageUpdateUi.ReadManagedSource(instance);
                var verified = MmcInstancePlatformOriginBackend.ReadVerified(new(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256));
                ModBase.RunInUi(() => {
                    try {
                        var view = new MmcPlatformOriginStatusView(source.Definition.Origin, verified);
                        int choice = ModMain.MyMsgBoxConfirmContent(verified is null ? "核实最初导入的原包后，可选择同一平台项目的在线更新。" : "请选择后续操作。在线更新将下载你明确选择的发布文件，并显示与当前实例的差异。",
                            "MMC 平台来源", verified is null ? "核实平台来源" : "选择在线更新", () => view,
                            CancelText: "关闭", ThirdText: verified is null ? "" : "重新核实来源", IsWarn: false);
                        if (choice == 1) {
                            if (verified is null) PromptBinding(instance, catalog); else MmcPackageUpdateUi.StartOnline(instance, catalog);
                        } else if (choice == 3 && verified is not null) PromptBinding(instance, catalog);
                    } catch (Exception error) { Report(error, "无法开始 MMC 平台操作"); }
                });
            } catch (Exception error) { Report(error, "无法读取 MMC 平台来源"); }
        }, "Read MMC platform origin");
    }
    internal static void PromptBinding(ModMinecraft.McInstance instance, MmcPlatformOriginCatalog catalog) => PromptBindingCaptured(instance, catalog, null);
    internal static ModLoader.LoaderCombo<string> PromptBindingCaptured(ModMinecraft.McInstance instance, MmcPlatformOriginCatalog catalog, MmcPlatformInstanceSelection expected, Func<bool> isCurrent = null) {
        try {
            if (isCurrent is not null && !isCurrent()) throw new IOException("当前页面对应的实例已变更，请重新进入实例设置。");
            var source = MmcPackageUpdateUi.ReadCapturedSource(instance, expected);
            expected ??= MmcPackageUpdateUi.Selection(source);
            MmcPlatformOriginInput input;
            if (ReadOriginInput is not null) input = ReadOriginInput(source.Definition.Origin);
            else {
                var view = new MmcPlatformOriginInputView(source.Definition.Origin);
                if (ModMain.MyMsgBoxConfirmContent("选择原 ZIP，并填写明确的平台项目和精确发布 ID。", "核实 MMC 平台来源", "读取发布文件", () => view, IsWarn: false) != 1) return null;
                input = view.ReadInput();
            }
            if (input is not null) {
                if (isCurrent is not null && !isCurrent()) throw new IOException("当前页面对应的实例已变更，请重新进入实例设置。");
                return StartBindingCaptured(instance, input.OriginalZip, input.Platform, input.ProjectId, input.VersionId, catalog, expected);
            }
        } catch (Exception error) { Report(error, "无法开始核实 MMC 平台来源"); }
        return null;
    }
    internal static ModLoader.LoaderCombo<string> StartBinding(ModMinecraft.McInstance instance, string originalZip,
        ResourcePlatforms platform, string projectId, string versionId, MmcPlatformOriginCatalog catalog) =>
        StartBindingCaptured(instance, originalZip, platform, projectId, versionId, catalog, null);
    internal static ModLoader.LoaderCombo<string> StartBindingCaptured(ModMinecraft.McInstance instance, string originalZip,
        ResourcePlatforms platform, string projectId, string versionId, MmcPlatformOriginCatalog catalog, MmcPlatformInstanceSelection expected) {
        ArgumentNullException.ThrowIfNull(catalog); var source = MmcPackageUpdateUi.ReadCapturedSource(instance, expected);
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再核实来源。");
        string archive = Path.GetFullPath(originalZip); if (!File.Exists(archive)) throw new IOException("所选原整合包文件不存在。");
        var selection = new MmcPlatformInstanceSelection(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256);
        var fence = MmcPackageUpdateUi.AcquireOperationFence();
        var worker = new MmcOperationWorker<string, MmcPlatformOriginResult>(fence, "读取精确平台发布文件", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token); var token = linked.Token;
            var backend = new MmcInstancePlatformOriginBackend(catalog);
            var releases = catalog.GetReleaseAsync(platform, projectId, versionId, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested(); task.Name = "等待选择原包对应的发布文件";
            var release = ModBase.RunInUiWait(() => { token.ThrowIfCancellationRequested(); return ChooseRelease(releases, "选择原包对应的发布文件", null); });
            if (release is null) { task.Cancel(); throw new OperationCanceledException(token); }
            task.Name = "核对原包内容与平台发布文件";
            var prepared = backend.PreviewAsync(new(selection, archive, release), token).GetAwaiter().GetResult();
            if (prepared.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
            if (prepared.Status != MmcInstanceBackendStatus.Succeeded || prepared.Preview is null) throw new IOException("平台来源核实未完成：" + prepared.Failure);
            token.ThrowIfCancellationRequested(); task.Name = "等待确认已核实的平台来源";
            int confirmed = ModBase.RunInUiWait(() => {
                token.ThrowIfCancellationRequested(); return ConfirmOrigin is not null ? ConfirmOrigin(prepared.Preview) :
                    ModMain.MyMsgBoxConfirmContent("请确认已核实的项目、发布文件和原包。保存前会创建环境记录。", "确认 MMC 平台来源", "保存已核实来源",
                        () => new MmcPlatformOriginConfirmView(prepared.Preview), IsWarn: false);
            });
            token.ThrowIfCancellationRequested();
            if (confirmed != 1) { task.Cancel(); throw new OperationCanceledException(token); }
            task.Name = "保存已核实的平台来源";
            var result = backend.CommitAsync(new(prepared.Preview, prepared.Preview.CandidateSha256) { TransactionCheckpoint = (phase, index) => {
                TransactionObserved?.Invoke(phase, index); if (phase == MmcTransactionCheckpoint.BeforeDecision) fence.EnterDecision(token);
            } }, token).GetAwaiter().GetResult(); task.Output = result;
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
            if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new IOException("平台来源尚未保存，当前环境与恢复记录已保留：" + result.Failure);
            if (Published is not null) Published(result);
            else ModBase.RunInUi(() => {
                ModMinecraft.McInstanceListForceRefresh = true; ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                ModMain.Hint("已保存核实过的平台来源，可从该项目选择在线更新。", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "核实 MMC 平台来源：" + instance.Name, [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.RefreshTaskButton()); }
            combo.Start(source.Paths.InstanceRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }
    internal static MmcPlatformReleaseDescriptor ChooseRelease(ImmutableArray<MmcPlatformReleaseDescriptor> releases, string title, string currentVersion) {
        var selected = SelectRelease is not null ? SelectRelease(releases, title, currentVersion) : Choose();
        if (selected is not null && !releases.Contains(selected)) throw new IOException("所选发布文件不属于刚刚核对的项目列表，请重新选择。");
        return selected;
        MmcPlatformReleaseDescriptor Choose() {
            var view = new MmcPlatformReleaseSelectView(releases, title, currentVersion);
            return ModMain.MyMsgBoxConfirmContent("请明确选择要使用的发布文件。文件内容还会再次核对；普通 mrpack 或 CurseForge 格式不会作为 MMC 包导入。", title,
                "选择此发布文件", () => view, IsWarn: false) == 1 ? view.Selected : null;
        }
    }
    private static void Report(Exception error, string caption) {
        if (ErrorObserved is not null) ErrorObserved(error, caption); else MeloongCore.Logger.Error(error, caption, MeloongCore.LogBehavior.Alert);
    }
}
