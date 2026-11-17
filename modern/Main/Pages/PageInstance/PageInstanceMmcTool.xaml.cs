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
            case FormMain.PageSubType.InstanceClone:
                LabToolDescription.Text = "请输入新实例名称并选择需要一并复制的用户数据目录。";
                if (string.IsNullOrWhiteSpace(TextCloneName.Text))
                    TextCloneName.Text = _capturedOwner.Name + "-副本";
                ValidateCloneNameInput(showWhenEmpty: false);
                break;
            case FormMain.PageSubType.InstanceMmcMove:
                LabToolDescription.Text = "当前目录位置：" + _capturedRoot;
                break;
        }
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
