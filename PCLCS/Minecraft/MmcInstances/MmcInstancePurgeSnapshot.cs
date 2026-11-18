using System.Collections.Immutable;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcPurgeSnapshot(int SchemaVersion, string MinecraftRoot, string OperationDirectory,
    Guid DeleteOperationId, string SourceRoot, string TargetRoot, Guid InstanceId, long Generation, string PrimarySha256,
    string GameRootLeaf, string IntentSha256, string CompletionSha256, string DeleteDecisionSha256, string MarkerSha256,
    ImmutableArray<MmcInstancePurgeEntry> Entries);

internal static class MmcInstancePurgeSnapshot {
    internal const int MaximumEntries = 65536;
    internal const long MaximumBytes = 512L * 1024 * 1024 * 1024;
    internal const int MaximumRecordBytes = 16 * 1024 * 1024;
    internal const string Primary = "PCL/InstanceDefinition.json";
    internal static string Fingerprint(MmcPurgeSnapshot snapshot) => MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot));
    internal static MmcPurgeSnapshot Capture(MmcInstanceLifecycleJournal journal, MmcLifecycleAccess access, CancellationToken token) {
        var intent = journal.Intent;
        if (intent.Kind != "Delete" || !journal.HasDeleteDecision() || journal.Completion()?.Disposition != "Quarantined" ||
            journal.HasRestoreDecision() || journal.IsRestored() ||
            MmcInstancePaths.Attributes(Path.Combine(journal.DirectoryPath, "restore.record")) is not null ||
            MmcInstancePurgeJournal.HasDecision(journal.DirectoryPath) ||
            MmcInstancePaths.Attributes(Path.Combine(journal.DirectoryPath, MmcInstancePurgeJournal.CompletionName)) is not null ||
            MmcInstancePaths.Attributes(intent.SourceRoot) is not null || !journal.Owns(intent.TargetRoot) ||
            Path.GetFileName(intent.TargetRoot) != ".pcllf-mmc-quarantine-" + intent.OperationId.ToString("N"))
            throw new IOException("Only an exact, completed, unrestored owned quarantine supports permanent cleanup.");
        var paths = MmcInstancePaths.Resolve(intent.TargetRoot, intent.GameRootLeaf);
        using var root = new MmcPurgeDirectoryPins(paths.InstanceRoot);
        using (journal.Enter(access.Leases[intent.TargetRoot])) {
            AssertIdle(access.Leases[intent.TargetRoot], paths);
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            if (primary.Sha256 != intent.SourcePrimarySha256 || primary.Definition.InstanceId != intent.InstanceId || primary.Definition.Generation != intent.SourceGeneration)
                throw new IOException("Quarantine identity, generation or primary bytes changed.");
        }
        var state = access.Quiet[intent.TargetRoot].State;
        lock (state.Sync) if (state.Patches.Count != 0 || state.IniPatches.Count != 0 || state.Inflight != 0)
            throw new IOException("Quarantine has queued or active preference producers; preserve it until they are resolved.");
        var entries = CaptureTree(paths, root.Last, token);
        var marker = entries.SingleOrDefault(entry => entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative && !entry.Directory)
            ?? throw new IOException("Quarantine owner marker is missing from its complete inventory.");
        var snapshot = new MmcPurgeSnapshot(1, Path.GetDirectoryName(Path.GetDirectoryName(intent.SourceRoot)!)!, journal.DirectoryPath,
            intent.OperationId, intent.SourceRoot, intent.TargetRoot, intent.InstanceId, intent.SourceGeneration, intent.SourcePrimarySha256,
            intent.GameRootLeaf, journal.IntentSha256, ObserveRecord(journal, "completed.record"), ObserveRecord(journal, "CommitDecided.record"), marker.Sha256!, entries);
        Validate(snapshot, journal); return snapshot;
    }
    internal static string ObserveRecord(MmcInstanceLifecycleJournal journal, string name) =>
        MmcDurableFiles.Observe(Path.Combine(journal.DirectoryPath, name)) ?? throw new IOException("Required lifecycle authority record disappeared.");
    internal static void AssertIdle(MmcAdmissionLease lease, MmcInstancePaths paths) {
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        string staging = paths.ResolveRelative(MmcPathRole.Runtime, ".staging");
        if (MmcInstancePaths.Attributes(staging) is not null && (!Directory.Exists(staging) || Directory.EnumerateFileSystemEntries(staging).Any()))
            throw new IOException("A prepared producer still owns a quarantine workspace.");
    }
    internal static ImmutableArray<MmcInstancePurgeEntry> CaptureTree(MmcInstancePaths paths, MmcPurgeHandle root, CancellationToken token,
        IReadOnlySet<string>? verifiedHistoryPrimaries = null) {
        var entries = ImmutableArray.CreateBuilder<MmcInstancePurgeEntry>();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase); long bytes = 0, characters = 0;
        root.AssertDefaultDataStreamOnly(); Add("", root, null); Walk("", 0);
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        void Walk(string relative, int depth) {
            token.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("Quarantine inventory exceeds its directory depth budget.");
            string directory = Target(paths, relative);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested(); string child = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
                string checkedPath = Target(paths, child); var attributes = MmcInstancePaths.Attributes(checkedPath) ?? throw new IOException("Quarantine inventory changed during discovery.");
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                using var pin = MmcPurgeHandle.Open(checkedPath, isDirectory);
                pin.AssertDefaultDataStreamOnly();
                if (child != Primary && child.EndsWith("/" + Primary, StringComparison.OrdinalIgnoreCase) && !(verifiedHistoryPrimaries?.Contains(child) ?? false) ||
                    child != MmcInstanceLifecycleJournal.MarkerRelative && child.EndsWith("/" + MmcInstanceLifecycleJournal.MarkerRelative, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A nested foreign instance owner was found; all of its bytes were preserved.");
                Add(child, pin, isDirectory ? null : pin.Hash(token));
                if (isDirectory) Walk(child, depth + 1);
            }
        }
        void Add(string relative, MmcPurgeHandle pin, string? hash) {
            if (!names.Add(relative) || entries.Count >= MaximumEntries || (characters += relative.Length) > 4L * 1024 * 1024 ||
                (bytes = checked(bytes + pin.Size)) > MaximumBytes) throw new IOException("Quarantine inventory exceeds its path, entry or byte budget.");
            entries.Add(new(relative, Role(relative, paths.GameRootLeaf), pin.Directory, pin.Identity, pin.Size, hash));
        }
    }
    internal static string Target(MmcInstancePaths paths, string relative) => relative.Length == 0 ? paths.InstanceRoot : paths.ResolveRelative(MmcPathRole.Instance, relative);
    internal static MmcInstancePurgeRole Role(string path, string gameLeaf) {
        static bool Under(string path, string prefix) => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        if (Under(path, gameLeaf)) return MmcInstancePurgeRole.Game;
        if (Under(path, gameLeaf == "minecraft" ? ".minecraft" : "minecraft")) return MmcInstancePurgeRole.InactiveGame;
        if (Under(path, "libraries")) return MmcInstancePurgeRole.LocalLibraries;
        if (Under(path, "PCL/MmcRuntime")) return MmcInstancePurgeRole.Runtime;
        if (Under(path, "PCL/MmcSource") || Under(path, "PCL/MmcOverrides") || Under(path, "PCL/MmcArtifacts")) return MmcInstancePurgeRole.SavedSource;
        return Under(path, "PCL") ? MmcInstancePurgeRole.InstanceMetadata : MmcInstancePurgeRole.InstancePrivate;
    }
    internal static void Validate(MmcPurgeSnapshot value, MmcInstanceLifecycleJournal journal) {
        var intent = journal.Intent;
        if (value.SchemaVersion != 1 || intent.Kind != "Delete" || value.DeleteOperationId != intent.OperationId ||
            value.OperationDirectory != journal.DirectoryPath || value.IntentSha256 != journal.IntentSha256 || value.InstanceId != intent.InstanceId ||
            value.Generation != intent.SourceGeneration || value.PrimarySha256 != intent.SourcePrimarySha256 || value.SourceRoot != intent.SourceRoot ||
            value.TargetRoot != intent.TargetRoot || value.GameRootLeaf != intent.GameRootLeaf ||
            value.MinecraftRoot != Path.GetDirectoryName(Path.GetDirectoryName(intent.SourceRoot)!) ||
            !MmcDurableFiles.IsHash(value.CompletionSha256) || !MmcDurableFiles.IsHash(value.DeleteDecisionSha256) || !MmcDurableFiles.IsHash(value.MarkerSha256) ||
            Path.GetFileName(value.TargetRoot) != ".pcllf-mmc-quarantine-" + intent.OperationId.ToString("N") ||
            value.Entries.IsDefaultOrEmpty || value.Entries.Length > MaximumEntries || value.Entries.Any(entry => entry is null))
            throw new IOException("Permanent cleanup plan has invalid or foreign lifecycle authority.");
        // Do not consult a partly deleted instance for plan validation. The immutable plan,
        // original delete records and the purge decision are the recovery authority.
        var paths = MmcInstancePaths.Resolve(value.TargetRoot, value.GameRootLeaf);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase); long total = 0, characters = 0;
        var directories = value.Entries.Where(entry => entry.Directory).Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.Entries) {
            if (entry is null || entry.RelativePath is null || entry.RelativePath.Contains('\\') || entry.RelativePath.Split('/').Length > 65 ||
                !names.Add(entry.RelativePath) || !MmcPurgeHandle.IsIdentity(entry.FileIdentity) || entry.Role != Role(entry.RelativePath, value.GameRootLeaf) ||
                entry.SizeBytes < 0 || entry.Directory && (entry.SizeBytes != 0 || entry.Sha256 is not null) || !entry.Directory && !MmcDurableFiles.IsHash(entry.Sha256) ||
                (total = checked(total + entry.SizeBytes)) > MaximumBytes || (characters += entry.RelativePath.Length) > 4L * 1024 * 1024)
                throw new IOException("Permanent cleanup inventory is damaged or exceeds its budget.");
            _ = Target(paths, entry.RelativePath);
            if (entry.RelativePath.Length != 0 && !directories.Contains(entry.RelativePath.Contains('/') ? entry.RelativePath[..entry.RelativePath.LastIndexOf('/')] : ""))
                throw new IOException("Permanent cleanup inventory has an unowned parent directory.");
        }
        if (!directories.Contains("") || value.Entries.SingleOrDefault(entry => entry.RelativePath == Primary) is not { Directory: false } primary || primary.Sha256 != value.PrimarySha256 ||
            value.Entries.SingleOrDefault(entry => entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative) is not { Directory: false } marker || marker.Sha256 != value.MarkerSha256)
            throw new IOException("Permanent cleanup inventory lacks the exact root, primary and ownership marker.");
        if (!value.Entries.Select(entry => entry.RelativePath).SequenceEqual(value.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)))
            throw new IOException("Permanent cleanup inventory order is not canonical.");
    }
}
