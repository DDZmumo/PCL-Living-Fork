using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcExportInactiveTree { RejectNonempty, Exclude }
public sealed record MmcInstanceExportOptions(bool IncludeSaves = false, bool IncludeScreenshots = false, bool IncludeServers = false) {
    public MmcExportInactiveTree InactiveGameTree { get; init; } = MmcExportInactiveTree.RejectNonempty;
    public bool DiscardMachineJavaSelection { get; init; }
    public bool IncludeUnrecognizedGameFiles { get; init; }
    public int? PortableMinimumMemoryMiB { get; init; }
    public int? PortableMaximumMemoryMiB { get; init; }
}
public sealed record MmcInstanceExportRequest(string SourceRoot, string OutputZipPath, string DisplayName,
    Guid ExpectedSourceInstanceId, long ExpectedSourceGeneration, string ExpectedSourcePrimarySha256,
    MmcInstanceExportOptions Options) {
    public string? ExpectedPreviewFingerprint { get; init; }
    public ImmutableArray<MmcRuntimeEnvironment> LegacyEnvironmentCandidates { get; init; } = [];
    public Action<MmcInstanceExportCheckpoint, int>? Checkpoint { get; init; }
}
public enum MmcInstanceExportCheckpoint { SnapshotPrepared, EntryWritten, BeforeSourceRevalidation, BeforePublication }
public sealed record MmcInstanceExportProgress(string Phase, int CompletedFiles = 0, int TotalFiles = 0, long CompletedBytes = 0, long TotalBytes = 0);
public sealed record MmcInstanceExportPreview(Guid SourceInstanceId, long SourceGeneration, string SourcePrimarySha256,
    string Fingerprint, string GameRootLeaf, int EntryCount, long UncompressedBytes,
    ImmutableArray<string> Transformations);
public enum MmcInstanceExportStatus { Completed, Rejected, Cancelled, Failed }
public sealed record MmcInstanceExportResult(MmcInstanceExportStatus Status, string? OutputZipPath = null,
    string? ZipSha256 = null, MmcInstanceExportPreview? Preview = null, string? RetainedPartialFile = null, string? Failure = null);
