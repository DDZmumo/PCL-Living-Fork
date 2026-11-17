using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcPurgePlan(int SchemaVersion, Guid PurgeId, string SnapshotFingerprint, MmcPurgeSnapshot Snapshot);
internal sealed record MmcPurgeDecision(int SchemaVersion, Guid PurgeId, Guid DeleteOperationId, string PlanSha256, string DeleteIntentSha256);
internal sealed record MmcPurgeCompletion(int SchemaVersion, Guid PurgeId, string PlanSha256, string Disposition);

internal sealed class MmcInstancePurgeJournal {
    internal const string PlanName = "purge-plan.record", DecisionName = "PurgeCommitDecided.record", CompletionName = "purged.record";
    internal MmcInstanceLifecycleJournal Lifecycle { get; }
    internal MmcPurgePlan Plan { get; }
    internal string PlanSha256 { get; }
    internal string DirectoryPath => Lifecycle.DirectoryPath;
    internal MmcInstancePurgeEntry RootEntry => Plan.Snapshot.Entries[0];
    private int authorityPins;
    private MmcInstancePurgeJournal(MmcInstanceLifecycleJournal lifecycle, MmcPurgePlan plan, string hash) { Lifecycle = lifecycle; Plan = plan; PlanSha256 = hash; }
    internal static bool HasPlan(string directory) => MmcInstancePaths.Attributes(Path.Combine(directory, PlanName)) is not null;
    internal static bool HasDecision(string directory) => MmcInstancePaths.Attributes(Path.Combine(directory, DecisionName)) is not null;
    internal static bool HasArtifacts(string directory) => HasPlan(directory) || HasDecision(directory) ||
        MmcInstancePaths.Attributes(Path.Combine(directory, CompletionName)) is not null;
    internal static MmcInstancePurgeJournal Prepare(MmcInstanceLifecycleJournal lifecycle, MmcInstancePurgePreview preview) {
        if (HasPlan(lifecycle.DirectoryPath) || HasDecision(lifecycle.DirectoryPath) || MmcInstancePaths.Attributes(Path.Combine(lifecycle.DirectoryPath, CompletionName)) is not null)
            throw new IOException("A permanent cleanup preparation already exists; recover that exact operation first.");
        var plan = new MmcPurgePlan(1, Guid.NewGuid(), preview.SnapshotFingerprint, preview.Snapshot);
        return new(lifecycle, plan, MmcDurableFiles.WriteRecord(Path.Combine(lifecycle.DirectoryPath, PlanName), plan));
    }
    internal static MmcInstancePurgeJournal Read(MmcInstanceLifecycleJournal lifecycle, CancellationToken cancellationToken = default) {
        var (plan, hash) = MmcDurableFiles.ReadRecord<MmcPurgePlan>(Path.Combine(lifecycle.DirectoryPath, PlanName), cancellationToken);
        if (plan.SchemaVersion != 1 || plan.PurgeId == Guid.Empty || plan.Snapshot is null ||
            plan.SnapshotFingerprint != MmcInstancePurgeSnapshot.Fingerprint(plan.Snapshot)) throw new IOException("Permanent cleanup plan binding is damaged.");
        MmcInstancePurgeSnapshot.Validate(plan.Snapshot, lifecycle);
        if (!lifecycle.HasDeleteDecision() || lifecycle.Completion()?.Disposition != "Quarantined" || lifecycle.HasRestoreDecision() || lifecycle.IsRestored() ||
            MmcInstancePaths.Attributes(Path.Combine(lifecycle.DirectoryPath, "restore.record")) is not null ||
            MmcInstancePurgeSnapshot.ObserveRecord(lifecycle, "completed.record") != plan.Snapshot.CompletionSha256 ||
            MmcInstancePurgeSnapshot.ObserveRecord(lifecycle, "CommitDecided.record") != plan.Snapshot.DeleteDecisionSha256)
            throw new IOException("Permanent cleanup lost its exact original delete authority; every remaining object was preserved.");
        return new(lifecycle, plan, hash);
    }
    internal void Decide() => MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, DecisionName), Decision());
    private MmcPurgeDecision Decision() => new(1, Plan.PurgeId, Lifecycle.Intent.OperationId, PlanSha256, Lifecycle.IntentSha256);
    internal bool IsDecided() {
        if (!HasDecision(DirectoryPath)) return false;
        if (MmcDurableFiles.ReadRecord<MmcPurgeDecision>(Path.Combine(DirectoryPath, DecisionName)).Value != Decision())
            throw new IOException("Permanent cleanup decision is damaged or belongs to another plan.");
        return true;
    }
    private MmcPurgeCompletion Completion() => new(1, Plan.PurgeId, PlanSha256, "Purged");
    internal bool IsCompleted() {
        string path = Path.Combine(DirectoryPath, CompletionName); if (MmcInstancePaths.Attributes(path) is null) return false;
        if (!IsDecided() || MmcDurableFiles.ReadRecord<MmcPurgeCompletion>(path).Value != Completion()) throw new IOException("Permanent cleanup completion is damaged.");
        return true;
    }
    internal void Complete() {
        string path = Path.Combine(DirectoryPath, CompletionName);
        if (MmcInstancePaths.Attributes(path) is null) MmcDurableFiles.WriteRecord(path, Completion()); else _ = IsCompleted();
    }
    internal void AbandonPreparation() {
        if (HasDecision(DirectoryPath)) throw new IOException("A decided permanent cleanup can only recover forward.");
        string path = Path.Combine(DirectoryPath, PlanName);
        if (MmcDurableFiles.ReadRecord<MmcPurgePlan>(path).Hash != PlanSha256) throw new IOException("A different cleanup preparation replaced this plan.");
        string expected = MmcDurableFiles.Observe(path) ?? throw new IOException("Cleanup preparation disappeared.");
        using var transaction = new MmcPurgeNtfsTransaction();
        using (var pin = transaction.OpenWriter(path, false)) {
            pin.AssertDefaultDataStreamOnly();
            if (pin.Size > MmcInstancePurgeSnapshot.MaximumRecordBytes + 128L || pin.Hash(CancellationToken.None) != expected || HasDecision(DirectoryPath))
                throw new IOException("Cleanup preparation changed or acquired an irreversible decision.");
            pin.StageTransactedDelete();
        }
        transaction.Commit();
    }
    internal IDisposable PinAuthority() => new AuthorityPins(this);
    private sealed class AuthorityPins : IDisposable {
        private MmcInstancePurgeJournal? owner;
        private readonly List<IDisposable> pins = [];
        internal AuthorityPins(MmcInstancePurgeJournal owner) {
            try {
                pins.Add(new MmcPurgeDirectoryPins(owner.DirectoryPath));
                foreach (string name in new[] { "intent.record", "completed.record", "CommitDecided.record", PlanName, DecisionName })
                    pins.Add(MmcPurgeHandle.Open(Path.Combine(owner.DirectoryPath, name), false));
                if (MmcInstancePaths.Attributes(Path.Combine(owner.DirectoryPath, CompletionName)) is not null)
                    pins.Add(MmcPurgeHandle.Open(Path.Combine(owner.DirectoryPath, CompletionName), false));
                if (Read(owner.Lifecycle).PlanSha256 != owner.PlanSha256 || !owner.IsDecided()) throw new IOException("Pinned cleanup authority no longer matches its decision.");
                this.owner = owner; owner.authorityPins++;
            } catch { Dispose(); throw; }
        }
        public void Dispose() {
            if (owner is not null) { owner.authorityPins--; owner = null; }
            for (int index = pins.Count - 1; index >= 0; index--) pins[index].Dispose(); pins.Clear();
        }
    }
    internal MmcPendingOperationPermit Enter(MmcAdmissionLease lease, MmcPurgeHandle? pinnedMarker = null) => MmcPendingOperationPermit.ForPurge(lease, this, pinnedMarker);
    internal ImmutableDictionary<string, string> ValidatePendingPermit(MmcAdmissionLease lease, MmcPurgeHandle? pinnedMarker) {
        lease.AssertHeld(); var snapshot = Plan.Snapshot;
        if (authorityPins == 0 || lease.InstanceId != snapshot.InstanceId || lease.VersionRoot != snapshot.TargetRoot)
            throw new IOException("Permanent cleanup permit has no exact same-owner durable decision.");
        var paths = MmcInstancePaths.Resolve(snapshot.TargetRoot, snapshot.GameRootLeaf);
        using (var root = MmcPurgeHandle.Open(paths.InstanceRoot, true)) root.Verify(RootEntry, CancellationToken.None);
        string markerPath = paths.ResolveRelative(MmcPathRole.Instance, MmcInstanceLifecycleJournal.MarkerRelative);
        if (pinnedMarker is not null) {
            if (!pinnedMarker.TransactedWriter || !MmcPurgeHandle.SamePath(pinnedMarker.Path, markerPath)) throw new IOException("Purge marker permit does not own its exact transactional writer.");
            pinnedMarker.Verify(snapshot.Entries.Single(entry => entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative), CancellationToken.None);
            pinnedMarker.AssertDefaultDataStreamOnly();
            return ImmutableDictionary<string, string>.Empty.Add(MmcInstanceLifecycleJournal.MarkerRelative, snapshot.MarkerSha256);
        }
        if (MmcInstancePaths.Attributes(markerPath) is null) return ImmutableDictionary<string, string>.Empty;
        // A marker's own verified DELETE handle can already be held by the caller. This
        // read pin still denies writers; the pending guard rereads its exact current path.
        using var marker = MmcPurgeHandle.Open(markerPath, false, allowDeleteSharing: true);
        marker.Verify(snapshot.Entries.Single(entry => entry.RelativePath == MmcInstanceLifecycleJournal.MarkerRelative), CancellationToken.None);
        return ImmutableDictionary<string, string>.Empty.Add(MmcInstanceLifecycleJournal.MarkerRelative, snapshot.MarkerSha256);
    }
}

internal sealed partial class MmcPendingOperationPermit {
    internal static MmcPendingOperationPermit ForPurge(MmcAdmissionLease lease, MmcInstancePurgeJournal journal, MmcPurgeHandle? pinnedMarker) => new(lease, journal.ValidatePendingPermit(lease, pinnedMarker));
}
