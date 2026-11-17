using System;
using System.IO;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed record MmcLegacyCapability(bool Allowed, MmcManagementState ManagementState, string? RefusalReason);

/// <summary>
/// Fresh dispatch for legacy lifecycle operations. This is a capability refusal, not a write
/// lease: the specialized MMC backend remains responsible for its own transactions and sessions.
/// Loose package metadata does not establish LF management ownership.
/// </summary>
public static class MmcInstanceCapabilities {
    public static MmcLegacyCapability LegacyOperation(string instanceRoot, string operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        // The inspector uses the existing trusted ordinary logical/physical root contract. Do not
        // apply MMC's versions-only layout restriction to historical temporary/custom roots.
        var inspection = MmcInstanceInspector.Read(instanceRoot);
        if (inspection.Management.CanUseLegacyBackend) return new(true, inspection.Management.State, null);
        string reason = inspection.Management.State == MmcManagementState.Legacy
            ? "无法完整确认实例管理记录，已停止" + operation + "。请检查访问权限并恢复管理记录后重试。"
            : "此实例由 MMC 后端管理，普通版本的" + operation + "入口尚未适配；请使用已支持的 MMC 操作。";
        if (!string.IsNullOrWhiteSpace(inspection.Error)) reason += " " + inspection.Error;
        return new(false, inspection.Management.State, reason);
    }

    public static void RequireLegacy(string instanceRoot, string operation) {
        var capability = LegacyOperation(instanceRoot, operation);
        if (!capability.Allowed) throw new InvalidOperationException(capability.RefusalReason);
    }

    /// <summary>Protect direct old file helpers when the destination is below a versions instance.</summary>
    public static void RequireLegacyContainingInstance(string path, string operation) {
        string absolute = Path.GetFullPath(path);
        MmcInstancePackageUpdateCapabilities.AssertOutsideStore(absolute, operation);
        var attributes = MmcInstancePaths.Attributes(absolute);
        // Existing ordinary root junctions remain supported, while an alias to a descendant
        // of a managed instance must not hide the actual owning versions directory.
        string? current = attributes is not null && (attributes.Value & FileAttributes.Directory) == 0
            ? Path.Combine(MmcConfigurationPaths.PhysicalRoot(Path.GetDirectoryName(absolute)!),Path.GetFileName(absolute))
            : MmcConfigurationPaths.PhysicalRoot(absolute);
        while (!string.IsNullOrEmpty(current)) {
            string? parent = Path.GetDirectoryName(current);
            if (parent is not null && string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase)) {
                RequireLegacy(current, operation);
            }
            current = parent;
        }
    }

    /// <summary>Old whole-folder destructive operations cannot discard any managed child.</summary>
    public static void RequireLegacyGameFolder(string gameRoot, string operation) {
        string physical = MmcConfigurationPaths.PhysicalRoot(gameRoot);
        RequireLegacyContainingInstance(physical, operation);
        RequireLegacy(physical, operation);
        MmcInstancePackageUpdateCapabilities.AssertFolderHasNoCandidates(physical, operation);
        MmcLegacyAdoptionCapabilities.RequireLegacyGameFolderSafe(physical, operation);
        MmcImportRecovery.RequireLegacyGameFolderSafe(physical, operation);
        string versions = MmcConfigurationPaths.Target(physical, "versions");
        var attributes = MmcInstancePaths.Attributes(versions);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.Directory) == 0) throw new IOException("游戏版本目录被普通文件占用，已停止" + operation + "。");
        int count = 0;
        foreach (string instance in Directory.EnumerateDirectories(versions)) {
            if (++count > 100000) throw new IOException("游戏文件夹实例数量超过检查预算，已停止" + operation + "。");
            RequireLegacy(instance, operation);
        }
    }
}
