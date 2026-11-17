using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed partial class MmcInstanceLifecycleBackend {
    public Task<MmcInstancePurgePreview> PreviewPurgeAsync(MmcInstancePurgePreviewRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => PreviewPurge(request, cancellationToken), CancellationToken.None);
    public Task<MmcInstancePurgeResult> PurgeAsync(MmcInstancePurgeRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Purge(request, cancellationToken), CancellationToken.None);
    public Task<MmcInstancePurgeResult> RecoverPurgeAsync(string operationDirectory) => Task.Run(() => {
        MmcInstancePurgeJournal? purge = null;
        try {
            var lifecycle = MmcInstanceLifecycleJournal.Read(operationDirectory);
            using var access = new MmcLifecycleAccess(lifecycle.Intent.SourceRoot, lifecycle.Intent.TargetRoot, lifecycle.Intent.InstanceId);
            purge = MmcInstancePurgeJournal.Read(lifecycle);
            return RecoverPurge(purge, access);
        } catch (Exception error) when (Failure(error)) { return PurgeFailure(operationDirectory, purge, error); }
    }, CancellationToken.None);

    private static MmcInstancePurgePreview PreviewPurge(MmcInstancePurgePreviewRequest request, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        var journal = MmcInstanceLifecycleJournal.Read(request.OperationDirectory); var intent = journal.Intent;
        if (request.ExpectedInstanceId != intent.InstanceId || request.ExpectedGeneration != intent.SourceGeneration || request.ExpectedPrimarySha256 != intent.SourcePrimarySha256)
            throw new IOException("Permanent cleanup preview belongs to a different instance, generation or primary snapshot.");
        MmcLifecycleAccess.SameVolume(intent.SourceRoot, intent.TargetRoot);
        using var access = new MmcLifecycleAccess(intent.SourceRoot, intent.TargetRoot, intent.InstanceId, token);
        using var pins = PinLifecycleAuthority(journal);
        var snapshot = MmcInstancePurgeSnapshot.Capture(journal, access, token);
        return new(snapshot, MmcInstancePurgeSnapshot.Fingerprint(snapshot));
    }
    private static MmcInstancePurgeResult Purge(MmcInstancePurgeRequest request, CancellationToken external) {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Preview);
        string directory = request.Preview.OperationDirectory; MmcInstancePurgeJournal? purge = null; bool commitRecorded = false;
        using var fence = new MmcLifecycleCancellationFence(external);
        try {
            if (request.Intent != MmcInstancePurgeIntent.PermanentlyDeleteQuarantinedInstance ||
                request.ConfirmedPreviewFingerprint != request.Preview.SnapshotFingerprint)
                throw new IOException("Permanent cleanup requires explicit confirmation of this exact complete preview.");
            var lifecycle = MmcInstanceLifecycleJournal.Read(directory); var intent = lifecycle.Intent;
            MmcLifecycleAccess.SameVolume(intent.SourceRoot, intent.TargetRoot);
            using var access = new MmcLifecycleAccess(intent.SourceRoot, intent.TargetRoot, intent.InstanceId, fence.Token);
            using var pins = PinLifecycleAuthority(lifecycle);
            var fresh = MmcInstancePurgeSnapshot.Capture(lifecycle, access, fence.Token);
            if (MmcInstancePurgeSnapshot.Fingerprint(fresh) != request.Preview.SnapshotFingerprint)
                throw new IOException("Quarantine files, directories or owner facts changed; reopen and confirm a new permanent cleanup preview.");
            // Prove local transactional writer support before making an irreversible outer
            // decision. This enlists only the primary, changes no bytes and rolls back on close.
            using (var capability = new MmcPurgeNtfsTransaction()) using (var writer = capability.OpenWriter(
                Path.Combine(fresh.TargetRoot, "PCL", "InstanceDefinition.json"), false)) {
                writer.Verify(fresh.Entries.Single(entry => entry.RelativePath == MmcInstancePurgeSnapshot.Primary), fence.Token);
                writer.AssertDefaultDataStreamOnly();
            }
            purge = MmcInstancePurgeJournal.Prepare(lifecycle, request.Preview);
            using var planPin = MmcPurgeHandle.Open(Path.Combine(directory, MmcInstancePurgeJournal.PlanName), false);
            request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.PlanPrepared, null);
            fence.Token.ThrowIfCancellationRequested();
            // The caller may have paused at PlanPrepared. Revalidate the entire namespace,
            // including empty directories, before an irreversible decision is recorded.
            request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeDecision, null);
            if (MmcInstancePurgeSnapshot.Fingerprint(MmcInstancePurgeSnapshot.Capture(lifecycle, access, fence.Token)) != request.Preview.SnapshotFingerprint)
                throw new IOException("Quarantine changed after permanent cleanup preparation; its bytes were preserved.");
            request.BeforeCommitDecision?.Invoke();
            fence.EnterDecision(); purge.Decide(); commitRecorded = true;
            using var decidedPins = purge.PinAuthority();
            request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.CommitDecided, null);
            return PurgeForward(purge, access, request.Checkpoint);
        } catch (MmcLifecycleInterruptionException error) { return PurgeFailure(directory, purge, error, commitRecorded); }
        catch (Exception error) when (error is OperationCanceledException || Failure(error)) {
            bool decided = commitRecorded || MmcInstancePurgeJournal.HasDecision(directory);
            if (purge is not null && !decided) {
                try {
                    using var access = new MmcLifecycleAccess(purge.Lifecycle.Intent.SourceRoot, purge.Lifecycle.Intent.TargetRoot, purge.Lifecycle.Intent.InstanceId);
                    using var pins = new MmcPurgeDirectoryPins(directory); purge.AbandonPreparation();
                } catch (Exception cleanup) when (Failure(cleanup)) { return PurgeFailure(directory, purge, new IOException(error.Message + " " + cleanup.Message)); }
            }
            if (!decided) return new(error is OperationCanceledException ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.Rejected,
                directory, request.Preview.QuarantineRoot, false, false, [], error.Message);
            return PurgeFailure(directory, purge, error, commitRecorded);
        }
    }
    private static MmcInstancePurgeResult RecoverPurge(MmcInstancePurgeJournal purge, MmcLifecycleAccess access) {
        if (!purge.IsDecided()) {
            using var pins = new MmcPurgeDirectoryPins(purge.DirectoryPath); purge.AbandonPreparation();
            return new(MmcInstanceBackendStatus.Cancelled, purge.DirectoryPath, purge.Plan.Snapshot.TargetRoot, false, false, []);
        }
        return PurgeForward(purge, access);
    }
    private static MmcInstancePurgeResult PurgeForward(MmcInstancePurgeJournal purge, MmcLifecycleAccess access,
        Action<MmcInstancePurgeCheckpoint, string?>? checkpoint = null) {
        using var authority = purge.PinAuthority(); var snapshot = purge.Plan.Snapshot;
        string root = snapshot.TargetRoot; var paths = MmcInstancePaths.Resolve(root, snapshot.GameRootLeaf);
        if (purge.IsCompleted()) {
            if (MmcInstancePaths.Attributes(root) is not null) throw new IOException("A completed cleanup path was recreated; its new content was preserved.");
            return new(MmcInstanceBackendStatus.Succeeded, purge.DirectoryPath, null, true, true, []);
        }
        if (MmcInstancePaths.Attributes(root) is not null) {
            using var tree = new MmcPurgeDirectoryPins(root);
            tree.Last.Verify(purge.RootEntry, CancellationToken.None);
            AssertPurgeIdle(purge, access, paths);
            VerifyRemainingNamespace(purge, paths, tree.Last);
            foreach (var entry in snapshot.Entries.Where(entry => !entry.Directory).OrderBy(entry =>
                entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative ? 2 : entry.RelativePath == MmcInstancePurgeSnapshot.Primary ? 1 : 0)
                .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)) {
                DeletePlanned(entry);
            }
            foreach (var entry in snapshot.Entries.Where(entry => entry.Directory && entry.RelativePath.Length != 0)
                .OrderByDescending(entry => entry.RelativePath.Count(character => character == '/')).ThenByDescending(entry => entry.RelativePath, StringComparer.Ordinal)) {
                DeletePlanned(entry);
            }
            AssertPurgeIdle(purge, access, paths);
            // Release only our root read pin before opening the transactional writer. The
            // same writer validates identity/streams and stages only this empty directory.
            tree.ReleaseLast();
            using (var transaction = new MmcPurgeNtfsTransaction()) {
                using (var finalRoot = transaction.OpenWriter(root, true)) {
                    finalRoot.Verify(purge.RootEntry, CancellationToken.None); finalRoot.AssertDefaultDataStreamOnly();
                    checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeEntryDelete, "");
                    if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Unknown or newly created quarantine entries remain; none were recursively removed.");
                    finalRoot.StageTransactedDelete();
                }
                checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryStaged, "");
                transaction.Commit();
            }
            checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryDeleted, "");
        }
        checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeCompletion, null);
        if (MmcInstancePaths.Attributes(root) is not null) throw new IOException("Quarantine was recreated before cleanup completion; its contents were preserved.");
        // Uses the existing no-recreation guard and detaches every old provider only after
        // the entire approved private namespace has actually disappeared.
        MmcConfigurationCoordinator.DetachDeletedRoot(root);
        purge.Complete(); checkpoint?.Invoke(MmcInstancePurgeCheckpoint.Completed, null);
        return new(MmcInstanceBackendStatus.Succeeded, purge.DirectoryPath, null, true, true, []);

        void DeletePlanned(MmcInstancePurgeEntry entry) {
            string target = MmcInstancePurgeSnapshot.Target(paths, entry.RelativePath);
            if (MmcInstancePaths.Attributes(target) is null) return;
            AssertPurgeIdle(purge, access, paths);
            using var parents = new MmcPurgeDirectoryPins(Path.GetDirectoryName(target)!, alreadyPinnedRoot: root);
            using var transaction = new MmcPurgeNtfsTransaction();
            using (var pin = transaction.OpenWriter(target, entry.Directory)) {
                pin.Verify(entry, CancellationToken.None); pin.AssertDefaultDataStreamOnly();
                checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeEntryDelete, entry.RelativePath);
                // The native transaction excludes every stream, not just the default data
                // stream. A new directory child instead makes empty disposition fail.
                AssertPurgeIdle(purge, access, paths, entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative ? pin : null);
                if (entry.Directory && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Unplanned content remains in a quarantine directory: " + entry.RelativePath);
                pin.StageTransactedDelete();
            }
            checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryStaged, entry.RelativePath);
            transaction.Commit(); checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryDeleted, entry.RelativePath);
        }
    }
    private static void AssertPurgeIdle(MmcInstancePurgeJournal purge, MmcLifecycleAccess access, MmcInstancePaths paths, MmcPurgeHandle? pinnedMarker = null) {
        using var permit = purge.Enter(access.Leases[paths.InstanceRoot], pinnedMarker);
        MmcInstancePurgeSnapshot.AssertIdle(access.Leases[paths.InstanceRoot], paths);
    }
    private static void VerifyRemainingNamespace(MmcInstancePurgeJournal purge, MmcInstancePaths paths, MmcPurgeHandle root) {
        var expected = purge.Plan.Snapshot.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in MmcInstancePurgeSnapshot.CaptureTree(paths, root, CancellationToken.None))
            if (!expected.TryGetValue(entry.RelativePath, out var old) || old != entry)
                throw new IOException("New, changed, foreign or linked quarantine content was preserved: " + entry.RelativePath);
    }
    private static IDisposable PinLifecycleAuthority(MmcInstanceLifecycleJournal journal) => new MmcPurgeLifecyclePins(journal);
    private sealed class MmcPurgeLifecyclePins : IDisposable {
        private readonly List<IDisposable> pins = [];
        internal MmcPurgeLifecyclePins(MmcInstanceLifecycleJournal journal) {
            try {
                pins.Add(new MmcPurgeDirectoryPins(journal.DirectoryPath));
                foreach (string name in new[] { "intent.record", "CommitDecided.record", "completed.record" }) pins.Add(MmcPurgeHandle.Open(Path.Combine(journal.DirectoryPath, name), false));
                if (MmcInstanceLifecycleJournal.Read(journal.DirectoryPath).IntentSha256 != journal.IntentSha256) throw new IOException("Lifecycle authority changed while it was pinned.");
            } catch { Dispose(); throw; }
        }
        public void Dispose() { for (int index = pins.Count - 1; index >= 0; index--) pins[index].Dispose(); pins.Clear(); }
    }
    private static MmcInstancePurgeResult PurgeFailure(string directory, MmcInstancePurgeJournal? purge, Exception error, bool commitRecorded = false) {
        bool decided = commitRecorded || MmcInstancePurgeJournal.HasDecision(directory); string? root = purge?.Plan.Snapshot.TargetRoot;
        var remaining = ImmutableArray.CreateBuilder<string>();
        if (purge is not null) foreach (var entry in purge.Plan.Snapshot.Entries) {
            try { if (MmcInstancePaths.Attributes(entry.RelativePath.Length == 0 ? root! : Path.Combine(root!, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))) is not null) remaining.Add(entry.RelativePath); }
            catch (Exception observation) when (Failure(observation)) { remaining.Add(entry.RelativePath); }
        }
        // Include unknown additions as remaining content as well. This is bounded read-only
        // reporting and never follows a link, even when the recovery plan itself was blocked.
        if (root is not null && MmcInstancePaths.Attributes(root) is not null) {
            try {
                using var rootPin = new MmcPurgeDirectoryPins(root); int count = 1; long characters = 0; Walk(root, "", 0);
                void Walk(string path, string relative, int depth) {
                    if (count >= MmcInstancePurgeSnapshot.MaximumEntries || depth > 64) return;
                    remaining.Add(relative);
                    foreach (string child in Directory.EnumerateFileSystemEntries(path).Take(MmcInstancePurgeSnapshot.MaximumEntries - Math.Min(count, MmcInstancePurgeSnapshot.MaximumEntries))) {
                        string name = relative.Length == 0 ? Path.GetFileName(child) : relative + "/" + Path.GetFileName(child);
                        if (++count > MmcInstancePurgeSnapshot.MaximumEntries || (characters += name.Length) > 4L * 1024 * 1024) return;
                        remaining.Add(name); var attributes = MmcInstancePaths.Attributes(child);
                        if (attributes is null || (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                        using var pin = MmcPurgeHandle.Open(child, true); Walk(child, name, depth + 1);
                    }
                }
            } catch (Exception observation) when (Failure(observation)) { /* Preserve all paths and the original failure. */ }
        }
        return new(MmcInstanceBackendStatus.NeedsRecovery, directory, root, decided, false,
            remaining.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Take(MmcInstancePurgeSnapshot.MaximumEntries).ToImmutableArray(), error.Message);
    }
    private static MmcInstanceLifecycleResult LifecyclePurgeResult(MmcInstancePurgeResult result) =>
        new(result.Status, result.OperationDirectory, result.QuarantineRoot, Quarantined: false, Failure: result.Failure) { PurgeDecided = result.DecisionRecorded, Purged = result.Completed };
}
