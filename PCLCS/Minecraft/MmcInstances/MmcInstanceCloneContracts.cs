using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcInstanceCloneOptions(bool CopySaves = false, bool CopyScreenshots = false, bool CopyServers = false);
public sealed record MmcInstanceCloneRequest(string SourceRoot, string TargetRoot, string TargetName, Guid ExpectedSourceInstanceId,
    long ExpectedSourceGeneration, string ExpectedSourcePrimarySha256, MmcInstanceCloneOptions Options) {
    public ImmutableArray<MmcRuntimeEnvironment> LegacyEnvironmentCandidates { get; init; } = [];
    public Action<MmcInstanceCloneCheckpoint, int>? Checkpoint { get; init; }
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
}
public enum MmcInstanceCloneCheckpoint { SnapshotPrepared, FileCopied, BeforeTransaction, BeforePublication }
public sealed record MmcInstanceCloneProgress(string Phase, int CompletedFiles = 0, int TotalFiles = 0, long CompletedBytes = 0, long TotalBytes = 0);
public sealed record MmcInstanceCloneResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstancePaths? Paths = null, string? WorkingRoot = null,
    int FileCount = 0, long Bytes = 0, string? Failure = null);
