using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>Omitted fields retain the current override. Reset removes all user overrides for UID.</summary>
public sealed record MmcInstanceComponentEdit(string Uid, bool? Enabled = null, string? ExactVersion = null,
    string? PatchContentUtf8 = null, bool Reset = false);
/// <summary>A user-selected immutable-byte source. No current unknown library is implicitly adopted.</summary>
public sealed record MmcInstanceLocalLibraryInput(string FileName, string AbsolutePath, string ExpectedSha256);
public sealed record MmcInstanceDefinitionChangeRequest(string VersionRoot, Guid ExpectedInstanceId, long ExpectedGeneration,
    string ExpectedPrimarySha256, MmcRuntimeEnvironment Environment, ImmutableArray<MmcInstanceComponentEdit> Edits) {
    public ImmutableArray<MmcInstanceLocalLibraryInput> LocalInputs { get; init; } = [];
}
public sealed record MmcInstanceDefinitionDifference(string Field, string Before, string After);
public sealed class MmcInstanceDefinitionChangePreview {
    public MmcInstanceDefinitionChangeRequest Selection { get; }
    public string CandidateSha256 { get; }
    public ImmutableArray<MmcInstanceDefinitionDifference> Differences { get; }
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; }
    internal ImmutableArray<MmcComponentOverride> Overrides { get; }
    internal ImmutableArray<MmcMetadataSnapshot> Snapshots { get; }
    internal ImmutableArray<MmcDefinitionChangeLocalFact> LocalFacts { get; }
    internal MmcInstanceDefinition Source { get; }
    internal ImmutableDictionary<string, ImmutableArray<byte>> SourceObjects { get; }
    internal ImmutableArray<MmcDefinitionChangeSourceCopy> SourceCopies { get; }
    internal MmcInstanceDefinitionChangePreview(MmcInstanceDefinitionChangeRequest selection, string hash,
        ImmutableArray<MmcInstanceDefinitionDifference> differences, ImmutableArray<MmcDiagnostic> diagnostics,
        ImmutableArray<MmcComponentOverride> overrides, ImmutableArray<MmcMetadataSnapshot> snapshots,
        ImmutableArray<MmcDefinitionChangeLocalFact> localFacts, MmcInstanceDefinition source, ImmutableDictionary<string, ImmutableArray<byte>> objects,
        ImmutableArray<MmcDefinitionChangeSourceCopy> copies) {
        Selection = selection; CandidateSha256 = hash; Differences = differences; Diagnostics = diagnostics;
        Overrides = overrides; Snapshots = snapshots; LocalFacts = localFacts; Source = source; SourceObjects = objects;
        SourceCopies = copies;
    }
}
internal sealed record MmcDefinitionChangeLocalFact(string FileName, string AbsolutePath, string Sha256, long SizeBytes, string Sha1);
internal sealed record MmcDefinitionChangeSourceCopy(string AbsolutePath, string TargetRelativePath, string Sha256, long SizeBytes);
public sealed record MmcInstanceDefinitionChangePreviewResult(MmcInstanceBackendStatus Status, MmcInstanceDefinitionChangePreview? Preview = null,
    string? Failure = null, ImmutableArray<MmcDiagnostic> Diagnostics = default) {
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; init; } = Diagnostics.IsDefault ? [] : Diagnostics;
}
public sealed record MmcInstanceDefinitionChangeCommitRequest(MmcInstanceDefinitionChangePreview Preview, string ExpectedCandidateSha256) {
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
    public Action? BeforeTransaction { get; init; }
    /// <summary>Runs after final source/world validation, immediately before the transaction's durable decision.</summary>
    public Action? BeforeCommitDecision { get; init; }
}
public sealed record MmcInstanceDefinitionChangeResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstanceHistoryPoint? UndoPoint = null, string? Failure = null) {
    public ImmutableArray<string> WorldBackupIds { get; init; } = [];
}
