using System.Text;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>Read-only list filtering for an exact private import reservation, never an action authorization.</summary>
public static class MmcImportVisibility {
    public static bool IsPrivateReservation(string versionRoot) {
        const string prefix = ".pcllf-mmc-import-";
        string leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(versionRoot));
        if (!leaf.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(leaf[prefix.Length..], "N", out Guid id) || id == Guid.Empty) return false;
        try {
            string root = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(versionRoot));
            if (!Directory.Exists(root)) return false;
            byte[] marker = ReadSmall(Path.Combine(root, "PCL", "MmcImportPending.json"));
            using var document = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(marker));
            var json = document.RootElement;
            if (!json.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !schema.TryGetInt32(out int version) || version != 1 || !Guid.TryParse(json.GetProperty("InstanceId").GetString(), out Guid instance) || instance == Guid.Empty) return false;
            string? package = json.GetProperty("PackageSha256").GetString(), temporary = json.GetProperty("TemporaryRoot").GetString(), final = json.GetProperty("FinalRoot").GetString();
            if (!MmcDurableFiles.IsHash(package) || temporary != root || final is null ||
                MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(final)) != final || Path.GetDirectoryName(final) != Path.GetDirectoryName(root) || final == root) return false;
            byte[] ignore = ReadSmall(Path.Combine(root, ".pclignore"));
            if (!ignore.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("PCLLF.MmcImport/v1\n" + instance.ToString("D") + "\n" + package))) return false;
            string store = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, "PCL", MmcImportOperation.StoreName);
            MmcInstancePaths.EnsureNoLinks(store);
            if (!Directory.Exists(store)) return false;
            string markerHash = MmcDurableFiles.Hash(marker), ignoreHash = MmcDurableFiles.Hash(ignore);
            // A prefix and two self-asserted marker files cannot hide an ordinary instance.
            // Require the independent immutable reservation in this Minecraft root's store.
            foreach (string directory in Directory.EnumerateDirectories(store).Take(4096)) {
                try {
                    var operation = MmcImportOperation.Read(directory); var owner = operation.Owner;
                    if (owner.TemporaryRoot != root || owner.FinalRoot != final || owner.InstanceId != instance || owner.PackageSha256 != package ||
                        owner.MarkerSha256 != markerHash || owner.IgnoreSha256 != ignoreHash || operation.Completed()) continue;
                    return ReadSmall(Path.Combine(root, "PCL", "MmcImportPending.json")).AsSpan().SequenceEqual(marker) &&
                        ReadSmall(Path.Combine(root, ".pclignore")).AsSpan().SequenceEqual(ignore);
                } catch (Exception error) when (Expected(error)) { }
            }
        } catch (Exception error) when (Expected(error)) { }
        return false; // Unknown or changed ownership remains visible for diagnosis.
    }
    private static byte[] ReadSmall(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 8192) throw new IOException("Import visibility marker exceeds its budget.");
        using MemoryStream output = new(); byte[] buffer = new byte[1024]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { if (output.Length + count > 8192) throw new IOException("Import marker grew beyond its budget."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException or DecoderFallbackException;
}
