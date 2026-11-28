using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using HistoryHost = PCLCS.EnvironmentHistoryPageHost<PCL.ModMinecraft.McInstance>;

namespace PCL;

// MMC supplies data and commands to the original restore-point page and controller.
// The fixed page identity is never replaced by a later directory observation.
public partial class PageInstanceRestorePoints {
    private readonly ModMinecraft.McInstance MmcOwner;
    private readonly string MmcRoot;
    private readonly Guid MmcInstanceId;
    private int MmcRevision;
    private bool MmcBound, MmcMayRead, MmcStateWritable, MmcReadReady;
    private long MmcLoadedGeneration;
    private string MmcLoadedPrimary = "";
    private InstanceSidebarState MmcState;
    private MmcInstanceHistoryPoint MmcUndoPoint;

    internal bool IsMmcHistory { get; }
    internal ModMinecraft.McInstance CapturedInstance => MmcOwner;

    private sealed record HistoryPointsRequest(string Root, int Revision = 0, Guid InstanceId = default,
        long Generation = 0, string Primary = "", bool CanRead = true, ModMinecraft.McInstance Owner = null);
    private sealed record HistoryPointsResult(HistoryPointsRequest Request, List<JObject> Points);

    private static Guid CaptureMmcInstanceId(ModMinecraft.McInstance owner, Guid? expected) {
        if (expected.HasValue) return expected.Value;
        try {
            var stamp = owner.Config.ContextStamp;
            if (stamp.Kind == MmcConfigurationContextKind.Managed) return stamp.InstanceId;
        } catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        return InstanceSidebarContext.Observe(owner.PathVersion, preserveMmcFamily: true).InstanceId;
    }

    private void InitMmcHistory() {
        BtnCreate.Content = "创建环境记录";
        CardRetention.Visibility = Visibility.Collapsed;
        FilterHealthy.Visibility = Visibility.Collapsed;
        BtnDelete.Visibility = Visibility.Collapsed;
        BtnMmcUndo.Visibility = Visibility.Visible;
        BtnMmcUndo.Click += (_, _) => UndoMmcRestore();
        HintMmcHistory.Visibility = Visibility.Visible;
        RefreshMmcState();
    }

    private HistoryHost CreateMmcHost() => new() {
        CurrentInstance = () => IsCurrentMmcOwner() ? MmcOwner : null,
        VersionPath = _ => MmcRoot,
        AutoCleanup = _ => false,
        Retention = _ => 10,
        SetAutoCleanup = (_, _) => throw new InvalidOperationException("MMC 环境记录暂不支持自动清理。"),
        SetRetention = (_, _) => throw new InvalidOperationException("MMC 环境记录暂不支持自动清理。"),
        StartLoader = StartMmcRead,
        CancelLoader = () => PointsLoader.Cancel(),
        LoaderFailed = () => PointsLoader.State == ModBase.LoadState.Failed,
        AnimationEnabled = () => ModAnimation.AniControlEnabled == 0,
        BindChanged = BindChanged,
        Start = StartMmcAction,
        HintError = text => ModMain.Hint(text, ModMain.HintType.Red),
        OpenFolder = _ => OpenMmcHistoryFolder(),
        ShowDetails = (text, title) => ModMain.MyMsgBox(text, title),
        PointButtons = MmcPointButtons,
        CanRestoreSelection = CanWriteMmcHistory,
        SelectionText = count => $"已选择 {count} 个环境记录",
        EmptyText = any => any ? "没有找到符合条件的环境记录。" : MmcMayRead
            ? "暂无 MMC 环境记录。可以手动创建；还原前也会自动保存当前环境。"
            : "当前环境记录不可用。请核对实例状态后重新打开此页面。"
    };

    private bool IsCurrentMmcOwner() => ReferenceEquals(LauncherInstanceContext.Instance, MmcOwner)
        && MmcOwner is not null && PathUtils.IsSame(MmcOwner.PathVersion, MmcRoot);

    private bool MmcAttachmentMatches() {
        try {
            var stamp = MmcOwner.Config.ContextStamp;
            return stamp.Kind == MmcConfigurationContextKind.Managed && stamp.InstanceId == MmcInstanceId;
        } catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { return false; }
    }

    private bool RefreshMmcState() {
        MmcMayRead = MmcStateWritable = false;
        string reason;
        var theme = DialogTone.Warning;
        if (!IsCurrentMmcOwner()) {
            reason = "当前页面对应的实例已变更，请重新进入实例设置。此页面不会操作其他实例。";
            theme = DialogTone.Error;
        } else if (MmcInstanceId == Guid.Empty) {
            reason = "打开页面时未能确认 MMC 实例身份，当前为只读状态。请恢复管理记录后重新选择实例。";
        } else {
            MmcState = InstanceSidebarContext.Observe(MmcRoot, preserveMmcFamily: true);
            if (MmcState.InstanceId != MmcInstanceId) {
                reason = MmcState.IsPendingTransaction
                    ? "检测到未完成事务，无法核实原 MMC 实例身份。当前不读取环境记录，也不能修改；请等待操作完成或先恢复事务。"
                    : MmcState.IsUnsupported
                    ? "MMC 管理格式不受当前版本支持，无法核实原实例身份。请保留实例目录并使用兼容版本处理。"
                    : MmcState.InstanceId == Guid.Empty
                    ? "无法确认原 MMC 实例的管理记录，已停止环境操作。请恢复记录后重新选择实例。"
                    : "此目录中的实例已被替换。当前页面仍属于原实例，刷新不会接管新实例；请重新选择实例。";
                theme = MmcState.IsPendingTransaction ? DialogTone.Warning : DialogTone.Error;
            } else {
                MmcMayRead = true;
                MmcStateWritable = MmcState.IsConsistent && MmcAttachmentMatches() && !string.IsNullOrEmpty(MmcState.Inspection?.PrimarySha256);
                if (MmcReadReady && (MmcLoadedGeneration != MmcState.Generation ||
                    !string.Equals(MmcLoadedPrimary, MmcState.Inspection?.PrimarySha256, StringComparison.OrdinalIgnoreCase))) MmcReadReady = false;
                if (MmcStateWritable) {
                    reason = "环境记录保存组件、补丁、运行库和环境文件。还原前自动保存当前环境；当前存档、截图、地图标记及实例偏好会保留。";
                    theme = DialogTone.Information;
                } else if (MmcState.IsConsistent) {
                    reason = "此页面绑定的配置身份已失效，环境记录仅供查看。请重新选择实例后再操作。";
                    theme = DialogTone.Error;
                } else if (MmcState.IsPendingTransaction) {
                    reason = "检测到未完成事务，环境记录仅供查看。请等待操作完成或先恢复事务，再重新检查。";
                } else if (MmcState.IsRepairRequired) {
                    reason = "MMC 配置校验未通过，环境记录仅供查看。请先在“组件与补丁”中修复 MMC 实例，再重新检查。";
                    theme = DialogTone.Error;
                } else if (MmcState.IsUnsupported) {
                    reason = "MMC 管理格式不受当前版本支持，环境记录仅供查看。请保留实例目录并使用兼容版本处理。";
                } else {
                    reason = "无法完整确认 MMC 管理信息，当前只允许查看已核实的环境记录。请恢复管理记录后重新检查。";
                }
            }
        }
        GlassDialogControls.UpdateNotice(HintMmcHistory, reason, theme);
        UpdateMmcCommands();
        return IsCurrentMmcOwner();
    }

    private bool CanWriteMmcHistory() => MmcReadReady && MmcStateWritable && MmcMayRead
        && IsCurrentMmcOwner() && !MmcHistoryUi.HasActiveOperation;

    private void UpdateMmcCommands() {
        bool canWrite = CanWriteMmcHistory();
        BtnCreate.IsEnabled = canWrite;
        BtnFolder.IsEnabled = MmcMayRead && IsCurrentMmcOwner();
        BtnMmcUndo.IsEnabled = canWrite && MmcUndoPoint is not null;
        BtnRestore.IsEnabled = canWrite && Controller.SelectedIds.Count == 1;
        foreach (GlassHistoryRow row in PanEntries.Children.OfType<GlassHistoryRow>())
            if (row.Actions.Children.OfType<Button>().FirstOrDefault() is Button restore) restore.IsEnabled = canWrite;
    }

    private void StartMmcRead(string _) {
        RefreshMmcState();
        int revision = Interlocked.Increment(ref MmcRevision);
        MmcReadReady = false;
        UpdateMmcCommands();
        var request = new HistoryPointsRequest(MmcRoot, revision, MmcInstanceId, MmcState?.Generation ?? 0,
            MmcState?.Inspection?.PrimarySha256 ?? "", MmcMayRead, MmcOwner);
        PointsLoader.Start(request, IsForceRestart: true);
    }

    private void InvalidateMmcRead() {
        Interlocked.Increment(ref MmcRevision);
        MmcReadReady = false;
        UpdateMmcCommands();
    }

    private static bool MatchesMmcRead(HistoryPointsRequest request, InstanceSidebarState state) =>
        request.InstanceId != Guid.Empty && state.InstanceId == request.InstanceId && state.Generation == request.Generation
        && string.Equals(state.Inspection?.PrimarySha256 ?? "", request.Primary, StringComparison.OrdinalIgnoreCase);

    private List<JObject> ReadMmcPoints(HistoryPointsRequest request, CancellationToken token) {
        if (!request.CanRead) return new List<JObject>();
        token.ThrowIfCancellationRequested();
        if (!MatchesMmcRead(request, InstanceSidebarContext.Observe(request.Root, preserveMmcFamily: true)))
            throw new IOException("实例状态已变化，请重新检查环境记录。");
        var points = new MmcInstanceHistoryBackend().ListAsync(request.Root, token).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (!MatchesMmcRead(request, InstanceSidebarContext.Observe(request.Root, preserveMmcFamily: true)) ||
            points.Any(point => point.InstanceId != request.InstanceId))
            throw new IOException("环境记录所属实例发生变化，已停止读取；请重新选择实例。");
        return points.OrderByDescending(point => point.CreatedUtc).Select(point => new JObject {
            ["id"] = point.PointId.ToString("N"), ["label"] = point.Label, ["created"] = point.CreatedUtc,
            ["description"] = $"环境记录 · 第 {point.Generation} 代 · {point.FileCount} 个文件 · {point.TotalBytes:N0} 字节",
            ["healthy"] = false, ["mmcPoint"] = JToken.FromObject(point)
        }).ToList();
    }

    private bool AcceptMmcPoints(HistoryPointsResult result) {
        if (result is null || result.Request.Revision != Volatile.Read(ref MmcRevision) ||
            !ReferenceEquals(result.Request.Owner, MmcOwner) || !IsCurrentMmcOwner() || !PathUtils.IsSame(result.Request.Root, MmcRoot)) return false;
        RefreshMmcState();
        if (result.Request.CanRead && !MatchesMmcRead(result.Request, MmcState)) {
            MmcReadReady = false;
            Controller.Entries.Clear();
            Controller.SelectedIds.Clear();
            Controller.RenderList();
            UpdateMmcCommands();
            return false;
        }
        MmcLoadedGeneration = result.Request.Generation;
        MmcLoadedPrimary = result.Request.Primary;
        MmcReadReady = result.Request.CanRead;
        UpdateMmcCommands();
        return true;
    }

    private MmcInstanceInspection RequireMmcWriteSource() {
        if (!CanWriteMmcHistory()) throw new IOException("当前 MMC 环境不可修改，请重新检查状态或重新选择实例。");
        MmcOwner.Config.AssertAttached();
        var stamp = MmcOwner.Config.ContextStamp;
        var source = MmcInstanceInspector.Read(MmcRoot);
        if (stamp.Kind != MmcConfigurationContextKind.Managed || stamp.InstanceId != MmcInstanceId ||
            !source.IsConsistent || source.Definition?.InstanceId != MmcInstanceId || source.Definition.Generation != MmcLoadedGeneration ||
            !string.Equals(source.PrimarySha256, MmcLoadedPrimary, StringComparison.OrdinalIgnoreCase))
            throw new IOException("实例环境已变化，请重新检查后再操作。当前选择未被替换。");
        return source;
    }

    private void StartMmcAction(ModMinecraft.McInstance owner, bool restore, string pointId, IEnumerable<string> deleteIds) {
        try {
            if (!ReferenceEquals(owner, MmcOwner)) throw new IOException("当前操作不属于此页面绑定的 MMC 实例。");
            if (deleteIds is not null) throw new IOException("MMC 环境记录暂不支持删除；现有记录已保留。");
            if (restore) {
                JObject entry = Controller.Entries.SingleOrDefault(point => string.Equals(point["id"]?.ToString(), pointId, StringComparison.Ordinal));
                if (entry is null) throw new IOException("所选环境记录不在当前列表中，请重新检查。");
                StartMmcRestore(ReadMmcPoint(entry), false);
            } else {
                var source = RequireMmcWriteSource();
                string label = MmcHistoryUi.RequestCaptureLabel();
                if (label is null) return;
                if (!IsCurrentMmcOwner()) throw new IOException("填写备注期间当前实例已变更，请重新打开实例设置。");
                MmcHistoryUi.StartCapture(new(MmcRoot, MmcInstanceId, source.Definition.Generation, source.PrimarySha256, label));
                UpdateMmcCommands();
            }
        } catch (Exception error) { MmcHistoryUi.Report(error, "无法开始 MMC 环境操作"); RefreshMmcState(); }
    }

    private static MmcInstanceHistoryPoint ReadMmcPoint(JObject point) => point["mmcPoint"]?.ToObject<MmcInstanceHistoryPoint>()
        ?? throw new IOException("环境记录元数据无效，请重新检查。");

    private void StartMmcRestore(MmcInstanceHistoryPoint point, bool undo) {
        var source = RequireMmcWriteSource();
        if (point.InstanceId != MmcInstanceId) throw new IOException("所选环境记录属于其他实例。");
        var request = new MmcInstanceHistoryRestoreRequest(MmcRoot, MmcInstanceId, source.Definition.Generation,
            source.PrimarySha256, point.PointId, point.ManifestSha256);
        if (!MmcHistoryUi.RequestRestoreConfirmation(point, undo)) return;
        if (!IsCurrentMmcOwner()) throw new IOException("确认期间当前实例已变更，请重新打开实例设置。");
        MmcHistoryUi.StartRestore(request);
        UpdateMmcCommands();
    }

    private void UndoMmcRestore() {
        try { if (MmcUndoPoint is not null) StartMmcRestore(MmcUndoPoint, true); }
        catch (Exception error) { MmcHistoryUi.Report(error, "无法撤回 MMC 环境还原"); RefreshMmcState(); }
    }

    private List<EnvironmentHistoryPageIcon> MmcPointButtons(JObject entry) {
        var point = ReadMmcPoint(entry);
        var restore = CreateIcon(ModBase.Logo.IconButtonRefresh, 0.85, "还原此环境记录，保留当前玩家存档与实例偏好");
        restore.Element.IsEnabled = CanWriteMmcHistory();
        restore.Subscribe(() => StartMmcAction(MmcOwner, true, point.PointId.ToString("N"), null));
        var details = CreateIcon(ModBase.Logo.IconButtonInfo, null, "环境记录详情");
        details.Subscribe(() => MmcHistoryUi.DisplayDetails(point.Label + "\n" + point.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
            $"\n记录代数：{point.Generation}\n环境文件：{point.FileCount} 个\n大小：{point.TotalBytes:N0} 字节\n" +
            (point.FullSafeEnvironment ? "记录组件、补丁、运行库与安全环境文件。" : "此记录仅覆盖其声明的组件、运行库和环境文件。") +
            "\n还原会自动保存当前环境；当前存档、截图、地图标记和实例偏好会保留。", "MMC 环境记录详情"));
        var open = CreateIcon(ModBase.Logo.IconButtonOpen, 1.05, "打开环境记录文件夹");
        open.Subscribe(() => OpenMmcHistoryFolder(point.PointId));
        return new List<EnvironmentHistoryPageIcon> { restore, details, open };
    }

    private void OpenMmcHistoryFolder(Guid? pointId = null) {
        try {
            RefreshMmcState();
            if (!MmcMayRead) throw new IOException("无法确认此环境记录所属实例，请重新选择实例。");
            string relative = "PCL/MmcHistory" + (pointId.HasValue ? "/points/" + pointId.Value.ToString("N") : "");
            string folder = MmcState.Inspection.Paths.ResolveRelative(MmcPathRole.Instance, relative);
            ModBase.OpenExplorer(Directory.Exists(folder) ? folder : MmcRoot);
        } catch (Exception error) { MmcHistoryUi.Report(error, "无法打开 MMC 环境记录目录"); }
    }

    private void BindMmcChanged(bool attach) {
        if (attach == MmcBound) return;
        MmcBound = attach;
        if (attach) {
            MmcHistoryUi.Changed += MmcHistoryChanged;
            MmcHistoryUi.CaptureCompleted += MmcCaptureCompleted;
            MmcHistoryUi.RestoreCompleted += MmcRestoreCompleted;
        } else {
            MmcHistoryUi.Changed -= MmcHistoryChanged;
            MmcHistoryUi.CaptureCompleted -= MmcCaptureCompleted;
            MmcHistoryUi.RestoreCompleted -= MmcRestoreCompleted;
        }
    }

    private bool IsMmcEventTarget(string root) => MmcBound && IsCurrentMmcOwner() && PathUtils.IsSame(root, MmcRoot);
    private void MmcHistoryChanged(string root) { if (IsMmcEventTarget(root)) Refresh(); }
    private void MmcCaptureCompleted(string root, MmcInstanceHistoryCaptureResult result) {
        if (IsMmcEventTarget(root) && result.Status == MmcInstanceBackendStatus.Succeeded && result.Point?.InstanceId == MmcInstanceId)
            Controller.FocusPoint(result.Point.PointId.ToString("N"));
    }
    private void MmcRestoreCompleted(string root, MmcInstanceHistoryRestoreResult result) {
        if (!IsMmcEventTarget(root) || result.Status != MmcInstanceBackendStatus.Succeeded || result.Definition?.InstanceId != MmcInstanceId) return;
        MmcUndoPoint = result.UndoPoint;
        if (MmcUndoPoint is not null) Controller.FocusPoint(MmcUndoPoint.PointId.ToString("N"));
    }
}
