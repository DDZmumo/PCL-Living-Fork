using System.Text;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>Read-only list classification. It never authorizes a write or recovery.</summary>
public static class MmcInstanceLifecycleVisibility {
    public static bool IsCompletedQuarantine(string versionRoot) {
        // The prefix is only an inexpensive prefilter. Every ownership and
        // completion fact below must match before a directory is hidden.
        if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(versionRoot)).StartsWith(".pcllf-mmc-quarantine-", StringComparison.Ordinal)) return false;
        try {
            var paths = MmcInstancePaths.Resolve(versionRoot);
            string markerPath = paths.ResolveRelative(MmcPathRole.Instance, MmcInstanceLifecycleJournal.MarkerRelative);
            using FileStream stream = new(markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 8192) return false;
            using MemoryStream memory = new(); byte[] buffer = new byte[1024]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) {
                if (memory.Length + count > 8192) return false;
                memory.Write(buffer, 0, count);
            }
            using var strict = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(memory.ToArray()));
            var marker = JsonSerializer.Deserialize<MmcLifecycleMarker>(strict.RootElement.GetRawText());
            if (marker is null || marker.SchemaVersion != 1 || marker.OperationId == Guid.Empty || marker.InstanceId == Guid.Empty) return false;
            var journal = MmcInstanceLifecycleJournal.Read(marker.OperationDirectory);
            var intent = journal.Intent;
            if (intent.Kind != "Delete" || intent.OperationId != marker.OperationId || intent.InstanceId != marker.InstanceId ||
                !string.Equals(intent.TargetRoot, paths.InstanceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                Path.GetFileName(paths.InstanceRoot) != ".pcllf-mmc-quarantine-" + intent.OperationId.ToString("N") ||
                !journal.Owns(paths.InstanceRoot) || !journal.HasDeleteDecision() || journal.Completion()?.Disposition != "Quarantined" ||
                journal.HasRestoreDecision() || journal.IsRestored()) return false;
            if (MmcInstancePurgeJournal.HasDecision(journal.DirectoryPath)) return false;
            foreach (string role in new[] { "PCL/MmcTransactions", "PCL/MmcSessions", "PCL/MmcCommands" }) {
                string pending = paths.ResolveRelative(MmcPathRole.Instance, role);
                if (MmcInstancePaths.Attributes(pending) is not null && (!Directory.Exists(pending) || Directory.EnumerateFileSystemEntries(pending).Any())) return false;
            }
            if (MmcInstancePaths.Attributes(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json")) is not null) return false;
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            return primary.Sha256 == intent.SourcePrimarySha256 && primary.Definition.InstanceId == intent.InstanceId &&
                primary.Definition.Generation == intent.SourceGeneration;
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or JsonException or DecoderFallbackException) {
            return false; // Unknown/damaged/pending instances remain visible for diagnosis.
        }
    }
}
