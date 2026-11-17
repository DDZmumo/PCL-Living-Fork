using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>A complete read-only preview of one live managed instance; it grants no deletion authority.</summary>
public sealed class MmcInstancePermanentDeletePreview {
    public string SourceRoot { get; }
    public Guid InstanceId { get; }
    public long Generation { get; }
    public string PrimarySha256 { get; }
    public string SnapshotFingerprint { get; }
    public ImmutableArray<MmcInstancePurgeEntry> Entries { get; }
    public long TotalBytes => Entries.Sum(entry => entry.SizeBytes);
    internal MmcLiveDeleteSnapshot Snapshot { get; }
    internal MmcInstancePermanentDeletePreview(MmcLiveDeleteSnapshot snapshot, string fingerprint) {
        Snapshot = snapshot; SnapshotFingerprint = fingerprint; SourceRoot = snapshot.SourceRoot;
        InstanceId = snapshot.InstanceId; Generation = snapshot.Generation; PrimarySha256 = snapshot.PrimarySha256; Entries = snapshot.Entries;
    }
}

public sealed record MmcInstancePermanentDeleteRequest(MmcInstancePermanentDeletePreview Preview, string ConfirmedPreviewFingerprint) {
    public Action<MmcInstancePurgeCheckpoint, string?>? Checkpoint { get; init; }
}
internal sealed record MmcLiveDeleteSnapshot(string SourceRoot, Guid InstanceId, long Generation, string PrimarySha256,
    string GameRootLeaf, ImmutableArray<MmcInstancePurgeEntry> Entries);
