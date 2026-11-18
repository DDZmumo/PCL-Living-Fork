using System;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum InstanceSidebarFamily {
    Legacy = 0,
    MmcConsistent = 1,
    MmcRepairRequired = 2,
    MmcPendingTransaction = 3,
    MmcUnsupported = 4,
    MmcUnavailable = 5
}

public sealed record InstanceSidebarState(
    InstanceSidebarFamily Family,
    MmcInstanceInspection? Inspection) {
    public bool IsMmcFamily => Family != InstanceSidebarFamily.Legacy;
    public bool IsConsistent => Family == InstanceSidebarFamily.MmcConsistent;
    public bool IsRepairRequired => Family == InstanceSidebarFamily.MmcRepairRequired;
    public bool IsPendingTransaction => Family == InstanceSidebarFamily.MmcPendingTransaction;
    public bool IsUnsupported => Family == InstanceSidebarFamily.MmcUnsupported;
    public bool IsUnavailable => Family == InstanceSidebarFamily.MmcUnavailable;
    public string SettingsTitlePrefix => IsMmcFamily ? "实例设置" : "版本设置";
    public string ConfigSectionTitle => IsMmcFamily ? "实例配置" : "版本配置";
    public string ToolsSectionTitle => IsMmcFamily ? "MMC 实例工具" : "版本工具";
    public string ModifyTitle => IsMmcFamily ? "组件与补丁" : "修改";
    public string HistoryTitle => IsMmcFamily ? "环境记录" : "还原点";
    public Guid InstanceId => Inspection?.Definition?.InstanceId ?? Inspection?.Management.Envelope?.InstanceId ?? Guid.Empty;
    public long Generation => Inspection?.Definition?.Generation ?? Inspection?.Management.Envelope?.Generation ?? 0;
}

public static class InstanceSidebarContext {
    public static InstanceSidebarState Observe(string? versionRoot, bool preserveMmcFamily = false) {
        if (string.IsNullOrWhiteSpace(versionRoot))
            return new(InstanceSidebarFamily.Legacy, null);
        MmcInstanceInspection inspection = MmcInstanceInspector.Read(versionRoot);
        InstanceSidebarFamily family = inspection.Management.State switch {
            MmcManagementState.ManagedMmc when inspection.IsConsistent => InstanceSidebarFamily.MmcConsistent,
            MmcManagementState.ManagedMmc => InstanceSidebarFamily.MmcRepairRequired,
            MmcManagementState.ManagedMmcRepairRequired => InstanceSidebarFamily.MmcRepairRequired,
            MmcManagementState.PendingTransaction => InstanceSidebarFamily.MmcPendingTransaction,
            MmcManagementState.ManagedUnsupported => InstanceSidebarFamily.MmcUnsupported,
            _ when preserveMmcFamily || !inspection.Management.ObservationComplete || inspection.Error is not null => InstanceSidebarFamily.MmcUnavailable,
            _ => InstanceSidebarFamily.Legacy
        };
        return new(family, inspection);
    }

    public static bool IsMmcToolRoute(int route) => route is 2 or 5 or 12 or 14 or 15 or 18 or 17;

    public static bool IsMmcOnlyRoute(int route) => route is 18 or 17;

    public static int NormalizeRoute(int route, bool isMmcFamily) =>
        route == 17 ? (isMmcFamily ? 15 : 0) : !isMmcFamily && IsMmcOnlyRoute(route) ? 0 : route;
}
