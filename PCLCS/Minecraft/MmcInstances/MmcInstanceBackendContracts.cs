using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public enum MmcInstanceBackendStatus { Succeeded, Cancelled, Rejected, NeedsRecovery }
public sealed record MmcInstanceImportRequest(string ArchivePath, string VersionRoot, string InstanceName, MmcRuntimeEnvironment Environment) {
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
    public Action<MmcInitialPayloadCheckpoint, int>? PrivatePayloadCheckpoint { get; init; }
    public Action<MmcInstancePurgeCheckpoint, string?>? AutomaticCleanupCheckpoint { get; init; }
}
public sealed record MmcInstanceRepairRequest(string VersionRoot, MmcRuntimeEnvironment Environment, Guid? ExpectedInstanceId = null,
    bool AllowRebuildDivergedProjection = false) {
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
    public long? ExpectedGeneration { get; init; }
    public string? ExpectedPrimarySha256 { get; init; }
    public MmcInstanceRepairSelection? StrictSelection { get; init; }
}
public sealed record MmcInstanceBackendProgress(string Phase, long CompletedBytes = 0, long TotalBytes = 0) {
    public int? CompletedFiles { get; init; }
    public int? TotalFiles { get; init; }
}
/// <summary>Explicit adoption of another variant declared by the same preserved package sources.</summary>
public sealed record MmcInstanceAdoptVariantRequest(string VersionRoot, MmcRuntimeEnvironment Environment,
    Guid ExpectedInstanceId, long ExpectedGeneration, string ExpectedPrimarySha256) {
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
}
public sealed record MmcInstanceBackendResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstancePaths? Paths = null, MmcConfiguration? PackageConfiguration = null,
    string? WorkingRoot = null, ImmutableArray<MmcDiagnostic> Diagnostics = default, string? Failure = null) {
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; init; } = Diagnostics.IsDefault ? [] : Diagnostics;
    internal MmcProducerFileProof? ProducedFiles { get; init; }
}
internal sealed record MmcProducerFileProof(string Root, Guid InstanceId, string PrimarySha256, ImmutableDictionary<string, string> Files, ImmutableHashSet<string> Directories);
