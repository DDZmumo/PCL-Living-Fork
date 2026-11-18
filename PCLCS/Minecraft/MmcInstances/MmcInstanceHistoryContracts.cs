using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcInstanceHistoryCaptureRequest(string VersionRoot, Guid ExpectedInstanceId,
    long ExpectedGeneration, string ExpectedPrimarySha256, string Label) {
    public ImmutableArray<MmcRuntimeEnvironment> LegacyEnvironmentCandidates { get; init; } = [];
    public Action<MmcInstanceHistoryCheckpoint>? Checkpoint { get; init; }
    /// <summary>Exact active-game file paths, including absent targets. Worlds/accounts/runtime paths are never accepted.</summary>
    public ImmutableArray<string> AdditionalEnvironmentPaths { get; init; } = [];
    public bool FullSafeEnvironment { get; init; }
}
public sealed record MmcInstanceHistoryRestoreRequest(string VersionRoot, Guid ExpectedInstanceId,
    long ExpectedGeneration, string ExpectedPrimarySha256, Guid PointId, string ExpectedPointManifestSha256) {
    public ImmutableArray<MmcRuntimeEnvironment> LegacyEnvironmentCandidates { get; init; } = [];
    public Action<MmcInstanceHistoryCheckpoint>? Checkpoint { get; init; }
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
}
public enum MmcInstanceHistoryCheckpoint { SourcesCaptured, PointPublished, BeforeRestorePreparation, BeforeRestoreTransaction, RestoreCompleted }
public sealed record MmcInstanceHistoryPoint(Guid PointId, string ManifestSha256, Guid InstanceId, long Generation,
    string PrimarySha256, int CoverageVersion, string Label, DateTime CreatedUtc, string GameRootLeaf, int FileCount, long TotalBytes) {
    public ImmutableArray<string> AdditionalEnvironmentPaths { get; init; } = [];
    public bool FullSafeEnvironment { get; init; }
}
public sealed record MmcInstanceHistoryCaptureResult(MmcInstanceBackendStatus Status, MmcInstanceHistoryPoint? Point = null, string? Failure = null);
public sealed record MmcInstanceHistoryRestoreResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstanceHistoryPoint? UndoPoint = null, string? Failure = null);

internal sealed record MmcHistoryFile(string RelativePath, string Sha256, long SizeBytes);
internal sealed record MmcHistoryManifest(int SchemaVersion, int CoverageVersion, Guid PointId, Guid InstanceId, long Generation,
    string PrimarySha256, string Label, DateTime CreatedUtc, string GameRootLeaf, ImmutableArray<MmcHistoryFile> SourceFiles,
    ImmutableArray<MmcHistoryFile> EnvironmentFiles) {
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MmcHistoryAdditionalCoverage? AdditionalCoverage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MmcHistoryFullSafeCoverage? FullSafeCoverage { get; init; }
}
internal sealed record MmcHistoryAdditionalCoverage(int FormatVersion, ImmutableArray<string> Paths);
internal sealed record MmcHistoryFullSafeCoverage(int FormatVersion, int PolicyVersion,
    ImmutableArray<string> ManagedPaths, ImmutableArray<string> Directories);
internal sealed record MmcHistoryIndex(int SchemaVersion, Guid PointId, Guid InstanceId, string ManifestSha256);
internal sealed record MmcHistoryLoadedPoint(MmcInstanceHistoryPoint Point, MmcHistoryManifest Manifest,
    MmcInstancePaths SourcePaths, MmcInstanceDefinition Definition, MmcSavedInstanceSources Saved, MmcInstancePaths StorePaths);
