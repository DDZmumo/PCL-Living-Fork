using System;
using System.IO;
using System.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcConfigurationPaths {
    internal static string LogicalRoot(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    internal static string PhysicalRoot(string value) => MmcCanonicalPaths.ForTrustedRoot(LogicalRoot(value));
    internal static string Target(string physicalRoot, string relative) {
        string root = LogicalRoot(physicalRoot);
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("配置路径超出受信任实例根。");
        string? current = target;
        while (current is not null && !string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            var attributes = MmcInstancePaths.Attributes(current);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("实例配置根以下不能包含链接或重解析点。");
            if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0 && current != target) throw new IOException("配置父目录被普通文件占用。");
            current = Path.GetDirectoryName(current);
        }
        return target;
    }
    internal static MmcManagementResult ReadManagement(string logicalRoot, string physicalRoot) {
        if (!string.Equals(PhysicalRoot(logicalRoot), physicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(MmcManagementState.ManagedMmcRepairRequired, null, false, "", [new("ConfigurationRootMoved", "实例目录的实际位置已变化，请重新建立实例上下文。")]);
        var snapshot = MmcManagementProbe.CaptureAtPhysicalRoot(physicalRoot, Path.GetFileName(LogicalRoot(logicalRoot)));
        if (snapshot.ConfigHint.Error is not null) {
            try {
                var config = MmcConfigurationCoordinator.ReadConfig(physicalRoot);
                var hint = (config[MmcManagementReader.ConfigManifestPropertyName] as Newtonsoft.Json.Linq.JObject)?[MmcManagementReader.HintPropertyName];
                snapshot = snapshot with { ConfigHint = hint is null ? MmcManagementDocument.Missing : new(true, hint.ToString(Newtonsoft.Json.Formatting.None)) };
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
        }
        if (snapshot.VersionHint.Error is not null) {
            try {
                var version = MmcConfigurationCoordinator.ReadJsonObject(physicalRoot, Path.GetFileName(LogicalRoot(logicalRoot)) + ".json");
                var hint = version[MmcManagementReader.HintPropertyName];
                snapshot = snapshot with { VersionHint = hint is null ? MmcManagementDocument.Missing : new(true, hint.ToString(Newtonsoft.Json.Formatting.None)) };
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
        }
        var result = MmcManagementReader.Evaluate(snapshot);
        if (result.State == MmcManagementState.Legacy) return result;
        try { MmcInstancePaths.Resolve(logicalRoot); return result; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) {
            var state = result.State is MmcManagementState.PendingTransaction or MmcManagementState.ManagedUnsupported ? result.State : MmcManagementState.ManagedMmcRepairRequired;
            return result with { State = state, Envelope = null, ObservationComplete = false,
                Diagnostics = Array.AsReadOnly(result.Diagnostics.Concat([new MmcManagementDiagnostic("ManagedConfigurationPathRejected", "MMC 管理路径不符合严格布局或链接边界。")]).ToArray()) };
        }
    }
}
