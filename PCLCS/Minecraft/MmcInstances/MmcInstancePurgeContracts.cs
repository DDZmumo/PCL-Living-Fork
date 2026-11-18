using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcInstancePurgeIntent { PermanentlyDeleteQuarantinedInstance = 1 }
public enum MmcInstancePurgeRole { InstancePrivate, InstanceMetadata, SavedSource, LocalLibraries, Runtime, Game, InactiveGame }
public enum MmcInstancePurgeCheckpoint { PlanPrepared, BeforeDecision, CommitDecided, BeforeEntryDelete, EntryDeleted, BeforeCompletion, Completed, EntryStaged }
public sealed record MmcInstancePurgeEntry(string RelativePath, MmcInstancePurgeRole Role, bool Directory,
    string FileIdentity, long SizeBytes, string? Sha256);
public sealed record MmcInstancePurgePreviewRequest(string OperationDirectory, Guid ExpectedInstanceId,
    long ExpectedGeneration, string ExpectedPrimarySha256);

/// <summary>A bounded, complete snapshot of one already quarantined instance. It grants no deletion authority.</summary>
public sealed class MmcInstancePurgePreview {
    public string MinecraftRoot { get; }
    public string OperationDirectory { get; }
    public string SourceRoot { get; }
    public string QuarantineRoot { get; }
    public Guid InstanceId { get; }
    public long Generation { get; }
    public string PrimarySha256 { get; }
    public string SnapshotFingerprint { get; }
    public ImmutableArray<MmcInstancePurgeEntry> Entries { get; }
    public long TotalBytes { get; }
    internal MmcPurgeSnapshot Snapshot { get; }
    internal MmcInstancePurgePreview(MmcPurgeSnapshot snapshot, string fingerprint) {
        Snapshot = snapshot; SnapshotFingerprint = fingerprint; MinecraftRoot = snapshot.MinecraftRoot;
        OperationDirectory = snapshot.OperationDirectory; SourceRoot = snapshot.SourceRoot; QuarantineRoot = snapshot.TargetRoot;
        InstanceId = snapshot.InstanceId; Generation = snapshot.Generation; PrimarySha256 = snapshot.PrimarySha256;
        Entries = snapshot.Entries; TotalBytes = snapshot.Entries.Sum(entry => entry.SizeBytes);
    }
}
public sealed record MmcInstancePurgeRequest(MmcInstancePurgePreview Preview, string ConfirmedPreviewFingerprint,
    MmcInstancePurgeIntent Intent) {
    public Action<MmcInstancePurgeCheckpoint, string?>? Checkpoint { get; init; }
    /// <summary>Runs after the final namespace check, immediately before recording the irreversible cleanup decision.</summary>
    public Action? BeforeCommitDecision { get; init; }
}
public sealed record MmcInstancePurgeResult(MmcInstanceBackendStatus Status, string OperationDirectory,
    string? QuarantineRoot, bool DecisionRecorded, bool Completed, ImmutableArray<string> RemainingPaths, string? Failure = null);
