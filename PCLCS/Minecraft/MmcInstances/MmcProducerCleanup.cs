using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

internal sealed record MmcProducerCleanupIntent(int SchemaVersion, string Root, string OwnerSha256, ImmutableArray<MmcInstancePurgeEntry> Entries);
internal sealed record MmcProducerCleanupCompletion(int SchemaVersion, string IntentSha256);

// Only current-producer expected bytes grant cleanup. Discovery supplies identity/stream
// observations, never permission to delete a newly discovered file.
internal static class MmcProducerCleanup {
    internal static string RecordFileHash<T>(T value) {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        return MmcDurableFiles.Hash(Encoding.ASCII.GetBytes("PCLLF-MMC-RECORD-1\n" + MmcDurableFiles.Hash(payload) + "\n").Concat(payload).ToArray());
    }
    internal static void Discard(string root, string gameLeaf, IReadOnlyDictionary<string, string> expectedFiles, string operationDirectory, string ownerSha256,
        Action<MmcInstancePurgeCheckpoint, string?>? checkpoint = null, IReadOnlySet<string>? expectedDirectories = null) {
        if (!MmcDurableFiles.IsHash(ownerSha256)) throw new IOException("Automatic cleanup has no exact external producer owner.");
        var paths = MmcInstancePaths.Resolve(root, gameLeaf);
        using var operationPins = new MmcPurgeDirectoryPins(operationDirectory);
        using var rootPins = new MmcPurgeDirectoryPins(root);
        var entries = MmcInstancePurgeSnapshot.CaptureTree(paths, rootPins.Last, CancellationToken.None);
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase) { "", "PCL" };
        if (expectedDirectories is not null) foreach (string relative in expectedDirectories) {
            // The empty path denotes only the exact root already resolved and pinned above.
            if (relative.Length != 0) _ = paths.ResolveRelative(MmcPathRole.Instance, relative);
            directories.Add(relative);
        }
        foreach (string relative in expectedFiles.Keys) {
            _ = paths.ResolveRelative(MmcPathRole.Instance, relative);
            for (int index = relative.LastIndexOf('/'); index > 0; index = relative.LastIndexOf('/', index - 1)) directories.Add(relative[..index]);
        }
        foreach (var entry in entries) {
            if (entry.Directory ? !directories.Contains(entry.RelativePath)
                : !expectedFiles.TryGetValue(entry.RelativePath, out var hash) || entry.Sha256 != hash)
                throw new IOException("Automatic cleanup found changed or foreign bytes, directories or ownership; all current data is preserved: " + entry.RelativePath);
        }
        checkpoint?.Invoke(MmcInstancePurgeCheckpoint.PlanPrepared, null);
        var intent = new MmcProducerCleanupIntent(1, paths.InstanceRoot, ownerSha256, entries);
        string intentHash = MmcDurableFiles.WriteRecord(Path.Combine(operationDirectory, "automatic-cleanup.intent.record"), intent);
        // Keep all transaction objects outside the disappearing private root. Only
        // known transaction outputs may be copied; unknown entries already failed above.
        foreach (var entry in entries.Where(entry => !entry.Directory && entry.RelativePath.StartsWith("PCL/MmcTransactionHistory/", StringComparison.Ordinal))) {
            string source = MmcInstancePurgeSnapshot.Target(paths, entry.RelativePath);
            using var pin = MmcPurgeHandle.Open(source, false); pin.Verify(entry, CancellationToken.None); pin.AssertDefaultDataStreamOnly();
            string relative = entry.RelativePath["PCL/MmcTransactionHistory/".Length..];
            string retained = Path.Combine(operationDirectory, "rolled-back-transactions", relative.Replace('/', Path.DirectorySeparatorChar));
            MmcInstancePaths.EnsureNoLinks(retained); MmcArtifactIo.Directory(Path.GetDirectoryName(retained)!);
            MmcDurableFiles.CopyNew(source, retained, entry.Sha256!);
        }
        // The transaction pins/enlists each same file identity and its complete stream
        // set. Root and its ownership marker disappear together at the single commit.
        rootPins.ReleaseLast();
        using (var transaction = new MmcPurgeNtfsTransaction()) {
            foreach (var entry in entries.Where(entry => !entry.Directory).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)) Stage(entry);
            foreach (var entry in entries.Where(entry => entry.Directory).OrderByDescending(entry => entry.RelativePath.Count(character => character == '/'))
                .ThenByDescending(entry => entry.RelativePath.Length)) Stage(entry);
            checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeDecision, null);
            transaction.Commit();
            void Stage(MmcInstancePurgeEntry entry) {
                using (var writer = transaction.OpenWriter(MmcInstancePurgeSnapshot.Target(paths, entry.RelativePath), entry.Directory)) {
                    writer.Verify(entry, CancellationToken.None); writer.AssertDefaultDataStreamOnly();
                    checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeEntryDelete, entry.RelativePath);
                    writer.StageTransactedDelete();
                }
                checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryStaged, entry.RelativePath);
            }
        }
        MmcDurableFiles.WriteRecord(Path.Combine(operationDirectory, "automatic-cleanup.completed.record"), new MmcProducerCleanupCompletion(1, intentHash));
    }
    internal static string PreserveFailure(string operationDirectory, Exception error) {
        try {
            MmcInstancePaths.EnsureNoLinks(operationDirectory);
            string path = Path.Combine(operationDirectory, "automatic-cleanup-error-" + Guid.NewGuid().ToString("N") + ".log");
            string detail = error.ToString(); if (detail.Length > 65536) detail = detail[..65536];
            MmcDurableFiles.WriteNew(path, Encoding.UTF8.GetBytes(detail));
            return "\n内部诊断：" + path;
        } catch (Exception diagnosticFailure) when (diagnosticFailure is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return ""; }
    }
}

public sealed partial class MmcOwnedImportReservation {
    internal MmcProducerFileProof CommittedProductProof(MmcInstancePaths paths, MmcInstanceDefinition definition) {
        if (producerTransactionId == Guid.Empty || definition.InstanceId != InstanceId || definition.Generation != 1) throw new IOException("Initial import has no exact committed producer transaction.");
        RecordJournalProducts(paths, producerTransactionId, true);
        return new(paths.InstanceRoot, InstanceId, MmcDurableFiles.Hash(Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(definition))), cleanupFiles.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase), cleanupDirectories.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));
    }
    internal void RecordRolledBackProducts(MmcInstancePaths paths, Guid transactionId) => RecordJournalProducts(paths, transactionId, false);
    private void RecordJournalProducts(MmcInstancePaths paths, Guid transactionId, bool committed) {
        string prefix = "PCL/MmcTransactionHistory/" + transactionId.ToString("N") + "/";
        var prepared = MmcDurableFiles.ReadRecord<MmcPreparedJournal>(paths.ResolveRelative(MmcPathRole.Instance, prefix + "prepared.record"));
        var journal = prepared.Value;
        if (journal.SchemaVersion != 1 || journal.TransactionId != transactionId || journal.InstanceId != InstanceId || journal.CanonicalRoot != TemporaryRoot ||
            journal.ExpectedGeneration != 0 || journal.NewGeneration != 1 || journal.Mutations is null || journal.Mutations.Length > 100000)
            throw new IOException("Initial import product proof belongs to another transaction.");
        cleanupFiles[prefix + "prepared.record"] = MmcProducerCleanup.RecordFileHash(journal);
        foreach (var phase in committed ? new[] { "Pending", "CommitDecided", "FinalizedCommitted" } : new[] { "Pending", "FinalizedAborted" }) {
            string name = phase.StartsWith("Finalized", StringComparison.Ordinal) ? "Finalized" : phase;
            var marker = new MmcTransactionMarker(1, transactionId, InstanceId, prepared.Hash, phase);
            if (MmcDurableFiles.ReadRecord<MmcTransactionMarker>(paths.ResolveRelative(MmcPathRole.Instance, prefix + name + ".record")).Value != marker)
                throw new IOException("Initial import product proof has a changed transaction decision.");
            cleanupFiles[prefix + name + ".record"] = MmcProducerCleanup.RecordFileHash(marker);
        }
        for (int index = 0; index < journal.Mutations.Length; index++) {
            var mutation = journal.Mutations[index]; _ = paths.ResolveRelative(MmcPathRole.Instance, mutation.RelativePath);
            if (mutation.OldSha256 is not null) cleanupFiles[prefix + "old-" + index + ".bin"] = mutation.OldSha256;
            if (mutation.NewSha256 is { } hash) {
                if (!MmcDurableFiles.IsHash(hash)) throw new IOException("Initial import product has an invalid expected digest.");
                if (committed) cleanupFiles[mutation.RelativePath] = hash;
                cleanupFiles[prefix + "new-" + index + ".bin"] = hash;
            }
        }
    }
}
