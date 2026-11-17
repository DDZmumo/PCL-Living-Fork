using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcInstanceRelocationKind { Rename, Move }
public enum MmcLifecycleCheckpoint { IntentPrepared, MarkerWritten, StructurePrepared, CommitDecided, BeforeDirectoryMove, DirectoryMoved, BeforeFinalization, Completed, BeforeDecision }
public sealed record MmcInstanceRelocationRequest(string SourceRoot, string TargetRoot, Guid ExpectedInstanceId, long ExpectedGeneration,
    string ExpectedPrimarySha256, MmcInstanceRelocationKind Kind = MmcInstanceRelocationKind.Move) {
    public ImmutableArray<MmcRuntimeEnvironment> LegacyEnvironmentCandidates { get; init; } = [];
    public Action<MmcLifecycleCheckpoint, string>? Checkpoint { get; init; }
}
public sealed record MmcInstanceDeleteRequest(string SourceRoot, Guid ExpectedInstanceId, long ExpectedGeneration, string ExpectedPrimarySha256) {
    public Action<MmcLifecycleCheckpoint, string>? Checkpoint { get; init; }
}
public sealed record MmcInstanceLifecycleResult(MmcInstanceBackendStatus Status, string? OperationDirectory = null, string? CurrentRoot = null,
    MmcInstanceDefinition? Definition = null, bool Quarantined = false, string? Failure = null) {
    public bool PurgeDecided { get; init; }
    public bool Purged { get; init; }
}
public sealed class MmcLifecycleInterruptionException(string message) : Exception(message);

/// <summary>Fixed authority observed while the original relocation producer still owns its operation.</summary>
public sealed class MmcInstanceRelocationRecovery {
    public string OperationDirectory { get; }
    public string IntentSha256 { get; }
    internal MmcInstanceRelocationRequest Selection { get; }
    internal MmcInstanceRelocationRecovery(string directory, string hash, MmcInstanceRelocationRequest selection) {
        OperationDirectory = directory; IntentSha256 = hash; Selection = selection with { Checkpoint = null };
    }
}
