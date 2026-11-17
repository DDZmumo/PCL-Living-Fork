using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
// This is a read-only envelope gate, not the Core definition/compiler validator.
public enum MmcManagementState {
    Legacy, PendingTransaction, ManagedMmc, ManagedMmcRepairRequired, ManagedUnsupported
}

public enum MmcManagementEvidenceOrigin { InstalledInstance, PackageContent }

/// <summary>Exists means the management document itself is known to exist, including unreadable files.</summary>
public sealed record MmcManagementDocument(bool Exists, string? Json = null, string? Error = null) {
    public static MmcManagementDocument Missing { get; } = new(false);
}

/// <summary>Raw, immutable observations. Config/version inputs are extracted ownership hints, not whole files.</summary>
/// <param name="HasSourceRecords">Includes recognized LF MMC source, override, artifact and runtime-store remnants.</param>
public sealed record MmcManagementSnapshot(
    MmcManagementDocument Definition,
    MmcManagementDocument ConfigHint,
    MmcManagementDocument VersionHint,
    bool HasSourceRecords = false,
    bool HasPendingTransaction = false,
    bool ObservationComplete = true,
    MmcManagementEvidenceOrigin Origin = MmcManagementEvidenceOrigin.InstalledInstance);

public sealed record MmcManagementEnvelope(int SchemaVersion, string ManagementKind, string FormatProfile,
    Guid InstanceId, long Generation, string? GameRootLeaf);

public sealed record MmcManagementDiagnostic(string Code, string Message);

public sealed record MmcManagementResult(MmcManagementState State, MmcManagementEnvelope? Envelope,
    bool ObservationComplete, string EvidenceFingerprint, IReadOnlyList<MmcManagementDiagnostic> Diagnostics) {
    /// <summary>A partial/unreadable observation never grants access to ordinary writers.</summary>
    public bool CanUseLegacyBackend => State == MmcManagementState.Legacy && ObservationComplete;
}

/// <summary>
/// Recognizes LF-owned on-disk envelopes before settings providers with side effects are constructed.
/// ManagedMmc establishes ownership only: full definition, artifacts, transaction/session and capability
/// checks are still mandatory. Markers are consistency evidence, not cryptographic authentication.
/// </summary>
public static class MmcManagementReader {
    public const int SupportedSchemaVersion = 1;
    public const string SupportedFormatProfile = "prism-v1";
    public const string HintPropertyName = "pcllfManagement";
    public const string ConfigManifestPropertyName = "MinecraftInstallManifest";
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;

    public static MmcManagementResult Evaluate(MmcManagementSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Definition);
        ArgumentNullException.ThrowIfNull(snapshot.ConfigHint);
        ArgumentNullException.ThrowIfNull(snapshot.VersionHint);
        List<MmcManagementDiagnostic> diagnostics = [];
        MmcManagementDocument[] documents = [snapshot.Definition, snapshot.ConfigHint, snapshot.VersionHint];
        bool complete = snapshot.ObservationComplete && documents.All(d => d.Error is null);
        string fingerprint = Fingerprint(snapshot);
        MmcManagementResult Result(MmcManagementState state, MmcManagementEnvelope? envelope = null) =>
            new(state, envelope, complete, fingerprint, Array.AsReadOnly(diagnostics.ToArray()));
        foreach (MmcManagementDocument document in documents.Where(d => d.Error is not null))
            diagnostics.Add(new("ObservationFailed", document.Error!));
        if (snapshot.HasPendingTransaction) {
            diagnostics.Add(new("PendingTransaction", "存在待确认或待恢复的 MMC 事务，须先恢复再选择后端。"));
            return Result(MmcManagementState.PendingTransaction);
        }
        bool hasEvidence = snapshot.HasSourceRecords || documents.Any(d => d.Exists);
        if (!hasEvidence) {
            if (!complete) diagnostics.Add(new("IncompleteObservation", "没有可确认的管理记录，但读取不完整，不能据此授权普通写入。"));
            return Result(MmcManagementState.Legacy);
        }
        if (snapshot.Origin != MmcManagementEvidenceOrigin.InstalledInstance) {
            diagnostics.Add(new("UnadoptedPackageMetadata", "包内的 PCLLF 标记不能直接采纳为已安装实例的身份。"));
            return Result(MmcManagementState.ManagedMmcRepairRequired);
        }
        if (!complete) return Result(MmcManagementState.ManagedMmcRepairRequired);

        (MmcManagementEnvelope? Envelope, bool Unsupported, string? Error) definition = Parse(snapshot.Definition, true);
        var config = Parse(snapshot.ConfigHint, false);
        var version = Parse(snapshot.VersionHint, false);
        if (definition.Unsupported || config.Unsupported || version.Unsupported) {
            diagnostics.Add(new("UnsupportedManagementEnvelope", "管理记录使用了当前版本不支持的 kind、schema、profile 或布局版本。"));
            return Result(MmcManagementState.ManagedUnsupported);
        }
        foreach (var item in new[] { definition, config, version })
            if (item.Error is not null) diagnostics.Add(new("InvalidManagementEnvelope", item.Error));
        if (diagnostics.Count != 0) return Result(MmcManagementState.ManagedMmcRepairRequired);
        MmcManagementEnvelope expected = definition.Envelope!;
        if (!SameIdentity(expected, config.Envelope!) || !SameIdentity(expected, version.Envelope!)) {
            diagnostics.Add(new("ManagementProjectionMismatch", "定义与配置/版本 JSON 的身份、schema、profile 或代数提示不一致。"));
            return Result(MmcManagementState.ManagedMmcRepairRequired);
        }
        return Result(MmcManagementState.ManagedMmc, expected);
    }

    private static bool SameIdentity(MmcManagementEnvelope first, MmcManagementEnvelope second) =>
        first.SchemaVersion == second.SchemaVersion && first.ManagementKind == second.ManagementKind &&
        first.FormatProfile == second.FormatProfile && first.InstanceId == second.InstanceId && first.Generation == second.Generation;

    private static (MmcManagementEnvelope? Envelope, bool Unsupported, string? Error) Parse(MmcManagementDocument input, bool definition) {
        if (!input.Exists) return (null, false, definition ? "缺少实例定义。" : "缺少管理投影提示。");
        if (input.Json is null) return (null, false, "管理记录不可读取。");
        try {
            using JsonDocument json = ParseJson(input.Json);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("管理记录必须是 JSON 对象。");
            int schema = RequiredInt(root, "schemaVersion");
            string kind = RequiredString(root, "managementKind");
            string profile = definition ? RequiredString(root, "formatProfile") :
                root.TryGetProperty("formatProfile", out JsonElement rawProfile) ? StringValue(rawProfile, "formatProfile") : SupportedFormatProfile;
            if (schema != SupportedSchemaVersion || kind != "Mmc" || profile != SupportedFormatProfile) return (null, true, null);
            string rawId = RequiredString(root, "instanceId");
            if (!Guid.TryParseExact(rawId, "D", out Guid id) || id == Guid.Empty) throw new InvalidDataException("实例 ID 必须是非空的 D 格式 GUID。");
            if (!root.TryGetProperty("generation", out JsonElement rawGeneration) || !rawGeneration.TryGetInt64(out long generation) || generation < 1)
                throw new InvalidDataException("管理代数必须是正整数。");
            string? leaf = null;
            if (definition) {
                if (!root.TryGetProperty("layout", out JsonElement layout) || layout.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("实例定义缺少布局。");
                leaf = RequiredString(layout, "gameRootLeaf");
                if (leaf is not ("minecraft" or ".minecraft")) throw new InvalidDataException("游戏目录叶名不受支持。");
                if (layout.TryGetProperty("layoutVersion", out JsonElement rawLayout) &&
                    (!rawLayout.TryGetInt32(out int layoutVersion) || layoutVersion != 1)) return (null, true, null);
            }
            return (new(schema, kind, profile, id, generation, leaf), false, null);
        } catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException) {
            return (null, false, ex.Message);
        }
    }

    internal static JsonDocument ParseJson(string text) {
        if (Encoding.UTF8.GetByteCount(text) > MaximumDocumentBytes) throw new InvalidDataException("管理记录超过读取预算。");
        JsonDocument document = JsonDocument.Parse(text.AsMemory(), new JsonDocumentOptions { MaxDepth = 64 });
        try { RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) {
                if (!names.Add(property.Name)) throw new InvalidDataException("管理 JSON 含有重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static int RequiredInt(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) && item.TryGetInt32(out int result) ? result :
            throw new InvalidDataException("管理记录缺少整数字段：" + key);
    private static string RequiredString(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) ? StringValue(item, key) :
            throw new InvalidDataException("管理记录缺少字符串字段：" + key);
    private static string StringValue(JsonElement value, string key) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ?
        value.GetString()! : throw new InvalidDataException("管理记录字段必须是非空字符串：" + key);

    private static string Fingerprint(MmcManagementSnapshot snapshot) {
        // Length-prefixed fields avoid concatenation ambiguity. Only ownership inputs enter this cache key.
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        void Integer(int value) {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value); hash.AppendData(bytes);
        }
        void Text(string? value) {
            Integer(value is null ? -1 : Encoding.UTF8.GetByteCount(value));
            if (value is null) return;
            Encoder encoder = Encoding.UTF8.GetEncoder();
            ReadOnlySpan<char> remaining = value.AsSpan();
            bool done;
            do {
                encoder.Convert(remaining, buffer, true, out int charsUsed, out int bytesUsed, out done);
                hash.AppendData(buffer.AsSpan(0, bytesUsed)); remaining = remaining[charsUsed..];
            } while (!done);
        }
        Integer(1); Integer((int)snapshot.Origin); Integer(snapshot.HasSourceRecords ? 1 : 0);
        Integer(snapshot.HasPendingTransaction ? 1 : 0); Integer(snapshot.ObservationComplete ? 1 : 0);
        foreach (var document in new[] { snapshot.Definition, snapshot.ConfigHint, snapshot.VersionHint }) {
            Integer(document.Exists ? 1 : 0); Text(document.Json); Text(document.Error);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
