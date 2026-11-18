using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed record MmcInstanceInspection(MmcManagementResult Management, MmcInstanceDefinition? Definition,
    MmcInstancePaths? Paths, string? ProjectionJson, McInstallManifest? Manifest,
    string? PrimarySha256, string? Error) {
    public bool IsLegacy => Management.State == MmcManagementState.Legacy;
    public bool IsConsistent => Management.State == MmcManagementState.ManagedMmc && Error is null &&
        Definition is not null && Paths is not null && ProjectionJson is not null && Manifest is not null;
}

/// <summary>Read-only backend dispatch facts. No settings provider is created and no repair is inferred.</summary>
public static class MmcInstanceInspector {
    public static MmcInstanceInspection Read(string versionRoot) {
        string logical = MmcConfigurationPaths.LogicalRoot(versionRoot);
        MmcManagementResult management;
        try {
            string physical = MmcConfigurationPaths.PhysicalRoot(logical);
            management = MmcConfigurationPaths.ReadManagement(logical, physical);
        } catch (Exception error) when (ReadFailure(error)) {
            management = new(MmcManagementState.Legacy, null, false, "", [new("IncompleteInstanceObservation", error.Message)]);
            return new(management, null, null, null, null, null, error.Message);
        }
        if (management.State == MmcManagementState.Legacy) return new(management, null, null, null, null, null, null);
        if (management.State is MmcManagementState.PendingTransaction or MmcManagementState.ManagedUnsupported)
            return new(management, null, null, null, null, null, Describe(management));
        MmcInstanceDefinition? definition = null;
        MmcInstancePaths? paths = null;
        string? primaryHash = null;
        try {
            paths = MmcInstancePaths.Resolve(logical);
            byte[] primary = ReadBounded(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json"));
            primaryHash = Convert.ToHexStringLower(SHA256.HashData(primary));
            var parsed = MmcDefinitionCodec.Read(new UTF8Encoding(false, true).GetString(primary));
            if (parsed.Status != MmcDefinitionReadStatus.Valid || parsed.Definition is null)
                throw new InvalidDataException("MMC 主定义无效或版本不受支持：" + string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
            definition = parsed.Definition;
            paths = MmcInstancePaths.Resolve(logical, definition.Layout.GameRootLeaf);
            if (management.State != MmcManagementState.ManagedMmc || management.Envelope is null ||
                management.Envelope.InstanceId != definition.InstanceId || management.Envelope.Generation != definition.Generation)
                throw new InvalidDataException(Describe(management));
            byte[] projection = ReadBounded(paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(logical) + ".json"));
            string projectionHash = Convert.ToHexStringLower(SHA256.HashData(projection));
            if (!projectionHash.Equals(definition.InstalledProjection!.OutputJsonSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MMC 兼容版本 JSON 已变化，请从保留来源修复。");
            string json = new UTF8Encoding(false, true).GetString(projection);
            JObject root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (root["mainClass"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)root["mainClass"]))
                throw new InvalidDataException("MMC 兼容投影缺少入口类。");
            JObject config = MmcConfigurationCoordinator.ReadConfig(logical);
            McInstallManifest? manifest = config["MinecraftInstallManifest"]?.ToObject<McInstallManifest>();
            if (manifest?.Components is null || manifest.Components.MinecraftVersion is null)
                throw new InvalidDataException("MMC 安装投影缺少可验证组件信息，请修复。");
            return new(management, definition, paths, json, manifest, primaryHash, null);
        } catch (Exception error) when (ReadFailure(error)) {
            return new(management with { State = MmcManagementState.ManagedMmcRepairRequired }, definition,
                paths, null, null, primaryHash, error.Message);
        }
    }

    public static void AssertLegacyOperationAllowed(string versionRoot, string operation) {
        MmcInstanceCapabilities.RequireLegacy(versionRoot, operation);
    }

    public static string ManagedGameRoot(string versionRoot) {
        var inspection = Read(versionRoot);
        if (!inspection.IsConsistent) throw new InvalidOperationException(inspection.Error ?? "MMC 环境尚未通过一致性校验。");
        return inspection.Paths!.GameRoot;
    }

    private static string Describe(MmcManagementResult management) =>
        "MMC 环境需要恢复或修复：" + string.Join("; ", management.Diagnostics.Select(d => d.Message));
    private static bool ReadFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or DecoderFallbackException or Newtonsoft.Json.JsonException or ArgumentException;
    private static byte[] ReadBounded(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > MmcManagementReader.MaximumDocumentBytes) throw new InvalidDataException("MMC 元数据超过读取上限。");
        using MemoryStream output = new();
        byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (output.Length + count > MmcManagementReader.MaximumDocumentBytes) throw new InvalidDataException("MMC 元数据读取期间超过上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
