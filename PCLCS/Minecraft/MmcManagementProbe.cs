using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>
/// Bounded, read-only observations of an already-installed version. Never constructs Configs or a
/// settings provider. Snapshot checks do not replace the cross-process operation/transaction lease.
/// </summary>
public static class MmcManagementProbe {
    // These are LF-owned stores, not loose MMC package metadata. Even derived
    // runtime leftovers remain ownership evidence when the definition is lost.
    private static readonly string[] ReservedStores = ["MmcSource", "MmcOverrides", "MmcArtifacts", "MmcRuntime", "MmcTransactionHistory", "MmcSessions", "MmcSessionHistory", "MmcLifecycle", "MmcHistory", "MmcDefinitionChanges", "MmcPackageUpdateReceipts", "MmcProjectionBackups"];
    private static readonly string[] PendingNames = ["MmcImportPending.json", "MmcLifecyclePending.json", "MmcLegacyAdoptionPending.json"];
    private static readonly string[] ReceiptNames = ["MmcImportPayload.json", "MmcClonePrepared.json"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static MmcManagementResult Read(string versionRoot) => MmcManagementReader.Evaluate(Capture(versionRoot));

    public static MmcManagementSnapshot Capture(string versionRoot) {
        string root;
        try { root = MmcInstancePaths.ValidateVersionRoot(versionRoot); }
        catch (Exception ex) when (IsObservationFailure(ex)) {
            // Unknown root access must not turn into permission for legacy writers or claim MMC ownership.
            return new(new(false, Error: ex.Message), MmcManagementDocument.Missing, MmcManagementDocument.Missing,
                ObservationComplete: false);
        }
        return CaptureAtPhysicalRoot(root, Path.GetFileName(root));
    }
    // Configuration callers resolve/trust their ordinary root first. The public MMC entry above
    // retains strict layout validation; a logical alias supplies the version JSON leaf explicitly.
    internal static MmcManagementSnapshot CaptureAtPhysicalRoot(string root, string logicalVersionName) {
        string pcl = Path.Combine(root, "PCL");
        MmcManagementDocument definition = ReadDocument(Path.Combine(pcl, "InstanceDefinition.json"));
        MmcManagementDocument configHint = ReadHint(Path.Combine(pcl, "config.json"), true);
        MmcManagementDocument versionHint = ReadHint(Path.Combine(root, logicalVersionName + ".json"), false);
        bool sources = false, pending = false, complete = true;
        foreach (string pendingName in PendingNames) try {
            string import = Path.Combine(pcl, pendingName);
            MmcInstancePaths.EnsureNoLinks(pcl);
            if (MmcInstancePaths.Attributes(import) is not null) { sources = true; pending = true; }
            MmcInstancePaths.EnsureNoLinks(import);
        } catch (Exception error) when (IsObservationFailure(error)) { complete = false; }
        foreach (string store in ReservedStores) {
            try {
                string storePath = Path.Combine(pcl, store);
                MmcInstancePaths.EnsureNoLinks(pcl);
                FileAttributes? attributes = MmcInstancePaths.Attributes(storePath);
                sources |= attributes is not null;
                MmcInstancePaths.EnsureNoLinks(storePath);
                if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0) complete = false;
            } catch (Exception ex) when (IsObservationFailure(ex)) {
                complete = false;
            }
        }
        foreach (string receiptName in ReceiptNames) try {
            string payload = Path.Combine(pcl, receiptName);
            MmcInstancePaths.EnsureNoLinks(pcl);
            FileAttributes? attributes = MmcInstancePaths.Attributes(payload);
            sources |= attributes is not null;
            MmcInstancePaths.EnsureNoLinks(payload);
            if (attributes is not null && (attributes.Value & FileAttributes.Directory) != 0) complete = false;
        } catch (Exception error) when (IsObservationFailure(error)) { complete = false; }
        try {
            string transactions = Path.Combine(pcl, "MmcTransactions");
            MmcInstancePaths.EnsureNoLinks(pcl);
            FileAttributes? attributes = MmcInstancePaths.Attributes(transactions);
            if (attributes is not null) sources = true;
            MmcInstancePaths.EnsureNoLinks(transactions);
            if (attributes is not null) {
                sources = true;
                // A future recovery service may recognize finalized journals. Until then every retained
                // entry is deliberately pending; this probe never interprets an unknown journal as done.
                pending |= (attributes.Value & FileAttributes.Directory) == 0 || Directory.EnumerateFileSystemEntries(transactions).Any();
            }
        } catch (Exception ex) when (IsObservationFailure(ex)) { complete = false; }
        return new(definition, configHint, versionHint, sources, pending, complete);
    }

    private static MmcManagementDocument ReadDocument(string path) {
        bool exists = false;
        try {
            MmcInstancePaths.EnsureNoLinks(Path.GetDirectoryName(path)!);
            FileAttributes? attributes = MmcInstancePaths.Attributes(path);
            if (attributes is null) return MmcManagementDocument.Missing;
            exists = true;
            MmcInstancePaths.EnsureNoLinks(path);
            if ((attributes.Value & FileAttributes.Directory) != 0) throw new IOException("管理文件被目录占用。");
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MmcManagementReader.MaximumDocumentBytes) throw new InvalidDataException("管理文件超过读取预算。");
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) != 0) {
                if (buffer.Length + read > MmcManagementReader.MaximumDocumentBytes) throw new InvalidDataException("管理文件读取期间超过预算。");
                buffer.Write(chunk, 0, read);
            }
            string content = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            if (content.Length > 0 && content[0] == '\uFEFF') content = content[1..];
            return new(true, content);
        } catch (Exception ex) when (IsObservationFailure(ex)) {
            return new(exists, Error: ex.Message);
        }
    }

    private static MmcManagementDocument ReadHint(string path, bool config) {
        MmcManagementDocument file = ReadDocument(path);
        if (file.Error is not null) return new(false, Error: file.Error);
        if (!file.Exists) return MmcManagementDocument.Missing;
        bool knownHint = false;
        try {
            // JsonDocument preserves duplicates during enumeration. Establish positive hint evidence
            // before the strict pass so duplicate ownership keys cannot disguise a managed instance.
            using (JsonDocument probe = JsonDocument.Parse(file.Json!.AsMemory(), new JsonDocumentOptions { MaxDepth = 64 })) {
                if (probe.RootElement.ValueKind == JsonValueKind.Object) {
                    knownHint = config ? probe.RootElement.EnumerateObject().Any(property =>
                        property.Name == MmcManagementReader.ConfigManifestPropertyName && property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.EnumerateObject().Any(item => item.Name == MmcManagementReader.HintPropertyName)) :
                        probe.RootElement.EnumerateObject().Any(property => property.Name == MmcManagementReader.HintPropertyName);
                }
            }
            using JsonDocument json = MmcManagementReader.ParseJson(file.Json!);
            JsonElement owner = json.RootElement;
            if (owner.ValueKind != JsonValueKind.Object) throw new InvalidDataException("配置/版本 JSON 必须是对象。");
            if (config) {
                if (!owner.TryGetProperty(MmcManagementReader.ConfigManifestPropertyName, out owner)) return MmcManagementDocument.Missing;
                if (owner.ValueKind != JsonValueKind.Object) return MmcManagementDocument.Missing;
            }
            return owner.TryGetProperty(MmcManagementReader.HintPropertyName, out JsonElement hint)
                ? new(true, hint.GetRawText()) : MmcManagementDocument.Missing;
        } catch (Exception ex) when (IsObservationFailure(ex)) {
            // A broken ordinary JSON file is not itself proof of MMC origin. Keep observation incomplete.
            return new(knownHint, Error: ex.Message);
        }
    }

    private static bool IsObservationFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or JsonException or DecoderFallbackException;
}
