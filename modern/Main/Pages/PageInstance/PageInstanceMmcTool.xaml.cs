using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public partial class PageInstanceMmcTool : MyPageRight, IRefreshable
{
    private readonly FormMain.PageSubType _route;
    private readonly ModMinecraft.McInstance _capturedOwner;
    private readonly string _capturedRoot;
    private readonly Guid _capturedInstanceId;
    private long _capturedGeneration;
    private string _capturedPrimarySha256 = "";
    private bool _hadConsistentBaseline;
    private int _refreshRevision;

    internal FormMain.PageSubType SubRoute => _route;
    internal ModMinecraft.McInstance CapturedInstance => _capturedOwner;

    public PageInstanceMmcTool() : this(FormMain.PageSubType.InstanceOverall, PageInstanceLeft.Instance)
    {
    }

    internal PageInstanceMmcTool(FormMain.PageSubType route, ModMinecraft.McInstance owner)
    {
        _route = route;
        _capturedOwner = owner ?? throw new ArgumentNullException(nameof(owner));
        _capturedRoot = owner.PathVersion ?? "";
        var attached = owner.Config.ContextStamp;
        _capturedInstanceId = attached.Kind == MmcConfigurationContextKind.Managed
            ? attached.InstanceId : PageInstanceLeft.ObserveInstanceContext(owner).InstanceId;
        InitializeComponent();
        TextCloneName.TextChanged += (_, _) => ValidateCloneNameInput(showWhenEmpty: false);
        RefreshState(updateBaselineIdentity: true);
    }

    public void Refresh()
    {
        RefreshState(updateBaselineIdentity: true);
        PageInstanceLeft.SyncInstanceContextIfLoaded();
    }

    internal void RefreshState(bool updateBaselineIdentity)
    {
        int revision = Interlocked.Increment(ref _refreshRevision);
        var activeOwner = PageInstanceLeft.Instance;
        bool ownerMatches = activeOwner is not null
            && ReferenceEquals(activeOwner, _capturedOwner)
            && PathUtils.IsSame(activeOwner.PathVersion, _capturedRoot);

        ApplyRouteMetadata();

        if (!ownerMatches)
        {
            ApplyBlockedState(
                hintTheme: MyHint.Themes.Red,
                hintText: "当前页面对应的实例已变更，请切换或重新进入当前实例设置。",
                recoveryMessage: "为避免对错误实例执行操作，当前页面已切换为只读保护状态。",
                allowRepair: false);
            return;
        }

        var state = PageInstanceLeft.ObserveInstanceContext(_capturedOwner);
        if (state.InstanceId != Guid.Empty && _capturedInstanceId != Guid.Empty && state.InstanceId != _capturedInstanceId)
        {
            ApplyBlockedState(MyHint.Themes.Red, "此目录中的实例已被替换，请重新选择实例。",
                "当前页面仍属于原实例，刷新不会接管另一个实例。请返回实例选择页后重新打开。", false);
            return;
        }
        if (updateBaselineIdentity)
        {
            _capturedGeneration = state.Generation;
            _capturedPrimarySha256 = state.Inspection?.PrimarySha256 ?? "";
            _hadConsistentBaseline = state.IsConsistent && !string.IsNullOrEmpty(_capturedPrimarySha256);
        }

        ApplyInstanceSummary(state);

        if (!state.IsMmcFamily)
        {
            ApplyBlockedState(
                hintTheme: MyHint.Themes.Red,
                hintText: "当前实例不再处于 MMC 管理状态，请刷新后查看。",
                recoveryMessage: "已检测到实例管理方式发生变化，此页面上的 MMC 操作已停用。",
                allowRepair: false);
            return;
        }

        if (!state.IsConsistent)
        {
            if (state.IsRepairRequired)
            {
                ApplyBlockedState(
                    hintTheme: MyHint.Themes.Red,
                    hintText: "实例核心配置校验未通过，当前仅可浏览或执行修复，修改操作已禁用。",
                    recoveryMessage: "检测到 MMC 实例投影或配置文件异常。请在核对状态后点击下方“修复 MMC 实例”进行恢复。",
                    allowRepair: true);
            }
            else if (state.IsPendingTransaction)
            {
                ApplyBlockedState(
                    hintTheme: MyHint.Themes.Yellow,
                    hintText: "检测到未完成的操作事务，当前实例处于只读保护状态，暂不允许继续修改。",
                    recoveryMessage: "为保护实例数据完整，在事务完成或恢复前不能修改组件、更新整合包或导出环境。请稍后点击“重新检查状态”。",
                    allowRepair: false);
            }
            else if (state.IsUnsupported)
            {
                ApplyBlockedState(
                    hintTheme: MyHint.Themes.Yellow,
                    hintText: "当前实例使用的 MMC 管理格式版本高于本启动器支持范围，已禁用修改操作。",
                    recoveryMessage: "请保留当前实例目录并使用兼容版本处理，或仅只读浏览存档与模组。",
                    allowRepair: false);
            }
            else
            {
                ApplyBlockedState(MyHint.Themes.Yellow,
                    "暂时无法确认实例管理信息，当前操作已停用。",
                    "请保留实例目录，确认文件可读取后重新检查状态；若管理记录已丢失，请返回实例选择页检查恢复选项。", false);
            }
            return;
        }

        CardRecovery.Visibility = Visibility.Collapsed;
        BtnRepairInstance.Visibility = Visibility.Collapsed;
        BtnPrimaryAction.IsEnabled = true;
        PanCloneOptions.Visibility = _route == FormMain.PageSubType.InstanceClone ? Visibility.Visible : Visibility.Collapsed;
        PanCloneOptions.IsEnabled = _route == FormMain.PageSubType.InstanceClone;

        ApplyConsistentRouteContent(state, revision);
    }

    private void ApplyRouteMetadata()
    {
        switch (_route)
        {
            case FormMain.PageSubType.InstanceModify:
                CardAction.Title = "MMC 组件与补丁";
                BtnPrimaryAction.Text = "管理组件与补丁";
                HintStatus.Visibility = Visibility.Collapsed;
                break;
            case FormMain.PageSubType.InstancePackUpdate:
                CardAction.Title = "从 MMC 整合包更新";
                BtnPrimaryAction.Text = "选择新整合包更新";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "更新前将对比新版整合包与当前环境差异，你的存档、截图及个人偏好设置会自动保留。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            case FormMain.PageSubType.InstanceExport:
                CardAction.Title = "导出 MMC 整合包";
                BtnPrimaryAction.Text = "配置并导出整合包";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "按 MMC / Prism 格式打包组件、模组与配置文件。存档、截图和服务器列表默认不打包。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            case FormMain.PageSubType.InstanceRestorePoints:
                CardAction.Title = "MMC 环境记录";
                BtnPrimaryAction.Text = "查看或创建环境记录";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "环境记录会保存当前组件、补丁与配置文件快照；还原前会自动备份当前环境，不影响存档与截图。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            case FormMain.PageSubType.InstanceClone:
                CardAction.Title = "克隆 MMC 实例";
                BtnPrimaryAction.Text = "创建独立副本";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "将该 MMC 实例完整复制为独立管理的新实例，不会改动原实例内容。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            case FormMain.PageSubType.InstanceMmcMove:
                CardAction.Title = "移动 MMC 实例";
                BtnPrimaryAction.Text = "移动实例目录";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "将实例移动到同一磁盘卷中已添加的游戏文件夹，保留实例名称、组件配置与存档。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            case FormMain.PageSubType.InstanceMmcPlatformOrigin:
                CardAction.Title = "MMC 平台来源与更新";
                BtnPrimaryAction.Text = "检查来源与在线更新";
                HintStatus.Theme = MyHint.Themes.Blue;
                HintStatus.Text = "核实最初导入的原整合包来源平台后，可在线检测并选择同一项目的发布版本更新。";
                HintStatus.Visibility = Visibility.Visible;
                break;
            default:
                CardAction.Title = "MMC 实例工具";
                BtnPrimaryAction.Text = "打开工具";
                HintStatus.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ApplyInstanceSummary(InstanceSidebarState state)
    {
        string summary = "当前实例：" + _capturedOwner.Name;
        var manifest = state.Inspection?.Manifest;
        if (state.IsConsistent && manifest?.Components is not null)
        {
            var described = InstalledComponentSummary.Describe(_capturedOwner.Version, manifest.Components);
            if (described.Count > 0)
                summary += "（" + string.Join(" · ", described.Select(item => item.Name + " " + item.Version)) + "）";
        }
        LabInstanceInfo.Text = summary;
    }

    private void ApplyBlockedState(MyHint.Themes hintTheme, string hintText, string recoveryMessage, bool allowRepair)
    {
        HintStatus.Theme = hintTheme;
        HintStatus.Text = hintText;
        HintStatus.Visibility = Visibility.Visible;
        BtnPrimaryAction.IsEnabled = false;
        PanCloneOptions.Visibility = Visibility.Collapsed;
        PanCloneOptions.IsEnabled = false;
        LabCloneValidation.Visibility = Visibility.Collapsed;
        LabToolDescription.Text = hintText;
        CardRecovery.Visibility = Visibility.Visible;
        LabRecoveryMessage.Text = recoveryMessage;
        BtnRepairInstance.Visibility = allowRepair ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyConsistentRouteContent(InstanceSidebarState state, int revision)
    {
        switch (_route)
        {
            case FormMain.PageSubType.InstanceModify:
                int overrideCount = state.Inspection?.Definition?.ComponentOverrides.Length ?? 0;
                LabToolDescription.Text = overrideCount > 0
                    ? $"当前已有 {overrideCount} 项托管组件覆盖或自定义补丁。点击下方按钮查看、调整组件版本或管理补丁。"
                    : "点击下方按钮打开 MMC 组件与补丁管理器，查看完整组件栈并调整支持的版本、加载器或补丁。";
                break;
            case FormMain.PageSubType.InstancePackUpdate:
                string originName = state.Inspection?.Definition?.Origin?.OriginalName;
                LabToolDescription.Text = string.IsNullOrWhiteSpace(originName)
                    ? "选择新的 MMC / Prism 整合包 ZIP，预览文件与组件变更并安全更新当前实例。"
                    : $"当前记录的原整合包名称：{originName}。选择新版 MMC / Prism 整合包 ZIP 后可预览差异并升级。";
                break;
            case FormMain.PageSubType.InstanceExport:
                LabToolDescription.Text = "可自定义需要携带的目录与可选内容，并生成标准 MMC / Prism 格式 ZIP 整合包。";
                break;
            case FormMain.PageSubType.InstanceRestorePoints:
                LabToolDescription.Text = "正在读取当前实例的环境记录摘要……";
                LoadHistorySummaryAsync(revision, _capturedOwner, _capturedRoot, _capturedInstanceId, _capturedGeneration, _capturedPrimarySha256);
                break;
            case FormMain.PageSubType.InstanceClone:
                LabToolDescription.Text = "请输入新实例名称并选择需要一并复制的用户数据目录。";
                if (string.IsNullOrWhiteSpace(TextCloneName.Text))
                    TextCloneName.Text = _capturedOwner.Name + "-副本";
                ValidateCloneNameInput(showWhenEmpty: false);
                break;
            case FormMain.PageSubType.InstanceMmcMove:
                LabToolDescription.Text = "当前目录位置：" + _capturedRoot;
                break;
            case FormMain.PageSubType.InstanceMmcPlatformOrigin:
                LabToolDescription.Text = "正在读取平台来源核实信息……";
                LoadPlatformOriginSummaryAsync(revision, state, _capturedOwner, _capturedRoot);
                break;
        }
    }

    private void LoadHistorySummaryAsync(int revision, ModMinecraft.McInstance owner, string root, Guid instanceId, long generation, string primarySha256)
    {
        ModBase.RunInNewThread(() =>
        {
            string message;
            try
            {
                var inspection = MmcInstanceInspector.Read(root);
                if (!inspection.IsConsistent || inspection.Paths is null || inspection.Definition is null ||
                    inspection.Definition.InstanceId != instanceId ||
                    inspection.Definition.Generation != generation ||
                    !string.Equals(inspection.PrimarySha256, primarySha256, StringComparison.OrdinalIgnoreCase))
                {
                    message = "实例状态发生变化，点击“刷新状态”后即可查看最新环境记录。";
                }
                else
                {
                    var points = new MmcInstanceHistoryBackend().ListAsync(inspection.Paths.InstanceRoot).GetAwaiter().GetResult();
                    if (points.IsEmpty)
                        message = "目前还没有保存任何 MMC 环境记录。点击下方按钮可以立即创建当前环境快照。";
                    else
                    {
                        var latest = points.OrderByDescending(p => p.CreatedUtc).First();
                        message = $"已保存 {points.Length} 个环境记录。最近一次记录：{latest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {latest.Label}";
                    }
                }
            }
            catch
            {
                message = "暂时无法读取环境记录详情，点击下方按钮仍可尝试管理环境记录。";
            }

            ModBase.RunInUi(() =>
            {
                if (!IsCurrentAsyncTarget(revision, owner, root)) return;
                LabToolDescription.Text = message;
            });
        }, "Load MMC Tool History Summary");
    }

    private void LoadPlatformOriginSummaryAsync(int revision, InstanceSidebarState state, ModMinecraft.McInstance owner, string root)
    {
        Guid capturedId = state.InstanceId;
        long capturedGeneration = state.Generation;
        string capturedPrimarySha256 = state.Inspection?.PrimarySha256 ?? "";
        string instanceRoot = state.Inspection?.Paths?.InstanceRoot ?? "";

        ModBase.RunInNewThread(() =>
        {
            string message;
            try
            {
                if (string.IsNullOrEmpty(instanceRoot) || capturedId == Guid.Empty || string.IsNullOrEmpty(capturedPrimarySha256))
                {
                    message = "尚未核实当前实例的平台来源信息。";
                }
                else
                {
                    var verified = MmcInstancePlatformOriginBackend.ReadVerified(new(instanceRoot, capturedId, capturedGeneration, capturedPrimarySha256));
                    if (verified is not null)
                    {
                        message = $"已核实来源平台：{verified.Identity.Platform} · 文件 {verified.SelectedFile.FileName}。可直接检查同一项目的在线更新。";
                    }
                    else if (state.Inspection?.Definition?.Origin?.PlatformProjectIdentity is { } recorded)
                    {
                        message = $"已记录平台信息：{recorded.Platform}（尚未完成原整合包核实）。点击下方按钮可核对原整合包。";
                    }
                    else if (!string.IsNullOrWhiteSpace(state.Inspection?.Definition?.Origin?.OriginalName))
                    {
                        message = $"原整合包名称：{state.Inspection.Definition.Origin.OriginalName}（尚未绑定平台来源）。点击下方按钮进行来源核实。";
                    }
                    else
                    {
                        message = "尚未关联平台来源。提供原始整合包与平台项目信息并核实后，即可启用在线更新检测。";
                    }
                }
            }
            catch
            {
                message = "暂时无法读取平台来源信息，点击下方按钮以查看详情或重新核实。";
            }

            ModBase.RunInUi(() =>
            {
                if (!IsCurrentAsyncTarget(revision, owner, root)) return;
                LabToolDescription.Text = message;
            });
        }, "Load MMC Tool Platform Origin Summary");
    }

    private bool IsCurrentAsyncTarget(int revision, ModMinecraft.McInstance owner, string root)
    {
        return _refreshRevision == revision
            && PageInstanceLeft.Instance is { } active
            && ReferenceEquals(active, owner)
            && ReferenceEquals(_capturedOwner, owner)
            && PathUtils.IsSame(active.PathVersion, root);
    }

    private bool ValidateCloneNameInput(bool showWhenEmpty)
    {
        if (_route != FormMain.PageSubType.InstanceClone) return true;
        string candidate = (TextCloneName.Text ?? "").Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            if (showWhenEmpty)
            {
                LabCloneValidation.Text = "请输入新实例名称。";
                LabCloneValidation.Visibility = Visibility.Visible;
            }
            else
            {
                LabCloneValidation.Visibility = Visibility.Collapsed;
            }
            return false;
        }
        try
        {
            var inspected = MmcInstanceInspector.Read(_capturedRoot);
            string instanceRoot = inspected.Paths?.InstanceRoot ?? Path.TrimEndingDirectorySeparator(_capturedRoot);
            string parent = Path.GetDirectoryName(instanceRoot) ?? "";
            string error = new ValidateFolderName(parent).Validate(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                LabCloneValidation.Text = error;
                LabCloneValidation.Visibility = Visibility.Visible;
                return false;
            }
        }
        catch (Exception ex)
        {
            LabCloneValidation.Text = ex.Message;
            LabCloneValidation.Visibility = Visibility.Visible;
            return false;
        }

        LabCloneValidation.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool TryVerifyOwnerForAction(bool requireConsistent, out ModMinecraft.McInstance owner, out InstanceSidebarState freshState)
    {
        owner = _capturedOwner;
        freshState = PageInstanceLeft.ObserveInstanceContext(_capturedOwner);
        var active = PageInstanceLeft.Instance;
        if (active is null || !ReferenceEquals(active, _capturedOwner) || !PathUtils.IsSame(active.PathVersion, _capturedRoot))
        {
            RefreshState(updateBaselineIdentity: false);
            ModMain.Hint("当前页面对应的实例已切换，已阻止操作，请重新选择对应实例。", ModMain.HintType.Red);
            return false;
        }

        if (!freshState.IsMmcFamily)
        {
            PageInstanceLeft.SyncInstanceContextIfLoaded();
            ModMain.Hint("该实例当前不再处于 MMC 管理状态，已阻止执行 MMC 操作。", ModMain.HintType.Red);
            return false;
        }

        if (_capturedInstanceId == Guid.Empty ||
            _capturedOwner.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed ||
            _capturedOwner.Config.ContextStamp.InstanceId != _capturedInstanceId ||
            (freshState.InstanceId != Guid.Empty && freshState.InstanceId != _capturedInstanceId))
        {
            RefreshState(updateBaselineIdentity: false);
            ModMain.Hint("实例身份已变化或尚未确认，请重新选择实例后再操作。", ModMain.HintType.Red);
            return false;
        }

        if (requireConsistent)
        {
            if (!freshState.IsConsistent || freshState.Inspection?.Definition is null ||
                freshState.Inspection.Paths is null || string.IsNullOrEmpty(freshState.Inspection.PrimarySha256))
            {
                RefreshState(updateBaselineIdentity: false);
                ModMain.Hint("该 MMC 实例需要先恢复或修复，当前不能执行此操作。", ModMain.HintType.Red);
                return false;
            }

            if (_hadConsistentBaseline &&
                (freshState.InstanceId != _capturedInstanceId ||
                 freshState.Generation != _capturedGeneration ||
                 !string.Equals(freshState.Inspection.PrimarySha256, _capturedPrimarySha256, StringComparison.OrdinalIgnoreCase)))
            {
                RefreshState(updateBaselineIdentity: true);
                ModMain.Hint("实例配置刚刚发生了变化，已自动刷新状态，请确认后再试。", ModMain.HintType.Red);
                return false;
            }
        }
        else if (_capturedInstanceId != Guid.Empty && freshState.InstanceId != Guid.Empty &&
                 freshState.InstanceId != _capturedInstanceId)
        {
            RefreshState(updateBaselineIdentity: false);
            ModMain.Hint("实例身份不一致，已阻止修复操作。", ModMain.HintType.Red);
            return false;
        }

        return true;
    }

    internal bool ExecutePrimaryAction()
    {
        if (!TryVerifyOwnerForAction(requireConsistent: true, out var owner, out _)) return false;
        try
        {
            switch (_route)
            {
                case FormMain.PageSubType.InstanceModify:
                    MmcComponentEditorUi.Prompt(owner);
                    return true;
                case FormMain.PageSubType.InstancePackUpdate:
                    MmcPackageUpdateUi.Prompt(owner);
                    return true;
                case FormMain.PageSubType.InstanceExport:
                    MmcExportUi.Prompt(owner);
                    return true;
                case FormMain.PageSubType.InstanceRestorePoints:
                    MmcHistoryUi.Prompt(owner);
                    return true;
                case FormMain.PageSubType.InstanceClone:
                    if (!ValidateCloneNameInput(showWhenEmpty: true)) return false;
                    string targetName = TextCloneName.Text.Trim();
                    MmcCloneUi.Start(owner, targetName, new InstanceCloneOptions
                    {
                        CopySaves = CheckCopySaves.Checked == true,
                        CopyScreenshots = CheckCopyScreenshots.Checked == true,
                        CopyServers = CheckCopyServers.Checked == true
                    });
                    return true;
                case FormMain.PageSubType.InstanceMmcMove:
                    MmcLifecycleUi.PromptMove(owner, _ =>
                    {
                        if (ReferenceEquals(PageInstanceLeft.Instance, owner) && ReferenceEquals(ModMain.FrmMain?.PageRight, this))
                            ModMain.FrmMain.PageBack();
                    });
                    return true;
                case FormMain.PageSubType.InstanceMmcPlatformOrigin:
                    MmcPlatformOriginUi.Prompt(owner, ModNet.CreateMmcPlatformOriginCatalog());
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception error)
        {
            if (_route == FormMain.PageSubType.InstanceClone)
            {
                LabCloneValidation.Text = error.Message;
                LabCloneValidation.Visibility = Visibility.Visible;
            }
            Logger.Error(error, "无法执行 MMC 实例操作", LogBehavior.Alert);
            return false;
        }
    }

    internal bool ExecuteRepairAction()
    {
        if (!TryVerifyOwnerForAction(requireConsistent: false, out var owner, out var state)) return false;
        if (!state.IsRepairRequired)
        {
            ModMain.Hint("当前状态暂不支持通过此处自动修复，请先核实状态。", ModMain.HintType.Red);
            return false;
        }
        try
        {
            MmcRepairUi.Prompt(owner);
            return true;
        }
        catch (Exception error)
        {
            Logger.Error(error, "无法开始修复 MMC 实例", LogBehavior.Alert);
            return false;
        }
    }

    private void BtnPrimaryAction_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ExecutePrimaryAction();

    private void BtnRepairInstance_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ExecuteRepairAction();

    private void BtnRefreshTool_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Refresh();
}
