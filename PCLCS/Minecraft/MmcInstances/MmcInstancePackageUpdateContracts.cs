using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcInstancePackageUpdateRequest(string VersionRoot, Guid ExpectedInstanceId, long ExpectedGeneration,
    string ExpectedPrimarySha256, string ArchivePath, MmcRuntimeEnvironment Environment) {
    // Only the platform service adapter can supply this. Public archive/manifest declarations never grant it.
    internal MmcVerifiedPackageOrigin? VerifiedOrigin { get; init; }
}
public sealed record MmcPackageComponentDecision(string Uid, bool KeepUserOverride);
public sealed record MmcPackageFileDecision(string ChangeId, PackUpdateChoice Choice, bool KeepDisabled = true);
public sealed record MmcInstancePackageUpdateChoices(ImmutableArray<MmcPackageComponentDecision> Components,
    ImmutableArray<MmcPackageFileDecision> Files, bool AllowUnverifiedDependencies = false) {
    public static MmcInstancePackageUpdateChoices Default { get; } = new([], []);
}
public sealed record MmcPackageComponentDifference(string Uid, string Before, string AuthorAfter,
    bool HasUserOverride, bool Conflict, bool Removed, bool? KeepUserOverride);
public sealed record MmcPackageFileDifference(string ChangeId, string RelativePath, string? LocalRelative,
    string? BeforeHash, string? AuthorHash, PackUpdateOwnership Ownership, bool Conflict, bool CanChoose,
    bool ApplyAuthor, PackUpdateChoice Choice, bool KeepDisabled, string? Reason);
public sealed class MmcInstancePackageUpdatePreview {
    public MmcInstancePackageUpdateRequest Selection { get; }
    public MmcInstancePackageUpdateChoices Choices { get; }
    public string PreviewSha256 { get; }
    public string PackageSha256 { get; }
    /// <summary>Display-only descriptor; authority remains the fixed internal service proof.</summary>
    public MmcPlatformReleaseDescriptor? PlatformRelease => Selection.VerifiedOrigin?.Release;
    public Guid OperationId => Job.Owner.OperationId;
    public string WorkingRoot => Job.Root;
    public ImmutableArray<MmcPackageComponentDifference> Components { get; }
    public ImmutableArray<MmcPackageFileDifference> Files { get; }
    public ImmutableArray<MmcInstanceDefinitionDifference> RuntimeChanges { get; }
    public ImmutableArray<string> Warnings { get; }
    public ImmutableArray<string> Errors { get; }
    public bool RequiresChoices { get; }
    public bool ChangesMinecraftVersion { get; }
    internal MmcPackageUpdateJob Job { get; }
    internal ImmutableArray<MmcHistoryFile> LocalSnapshot { get; }
    internal ImmutableArray<MmcHistoryFile> CandidateSnapshot { get; }
    internal MmcInstanceDefinition Source { get; }
    internal ImmutableArray<MmcComponentOverride> Overrides { get; }
    internal ImmutableArray<MmcMetadataSnapshot> Snapshots { get; }
    internal ImmutableDictionary<string, ImmutableArray<byte>> SourceObjects { get; }
    internal ImmutableArray<MmcDefinitionChangeSourceCopy> SourceCopies { get; }
    internal MmcPackageUpdatePlan Plan { get; }
    internal ImmutableArray<MmcDefinitionChangeLocalFact> LocalInputs { get; }
    internal MmcInstancePackageUpdatePreview(MmcInstancePackageUpdateRequest selection, MmcInstancePackageUpdateChoices choices,
        string hash, string packageHash, ImmutableArray<MmcPackageComponentDifference> components, ImmutableArray<MmcPackageFileDifference> files, ImmutableArray<MmcInstanceDefinitionDifference> runtimeChanges,
        ImmutableArray<string> warnings, ImmutableArray<string> errors, bool requiresChoices, bool changesMinecraft, MmcPackageUpdateJob job,
        ImmutableArray<MmcHistoryFile> local, ImmutableArray<MmcHistoryFile> candidate, MmcInstanceDefinition source,
        ImmutableArray<MmcComponentOverride> overrides, ImmutableArray<MmcMetadataSnapshot> snapshots,
        ImmutableDictionary<string, ImmutableArray<byte>> objects, ImmutableArray<MmcDefinitionChangeSourceCopy> copies, MmcPackageUpdatePlan plan,
        ImmutableArray<MmcDefinitionChangeLocalFact> localInputs) {
        Selection = selection; Choices = choices; PreviewSha256 = hash; PackageSha256 = packageHash; Components = components; Files = files;
        RuntimeChanges = runtimeChanges;
        Warnings = warnings; Errors = errors; RequiresChoices = requiresChoices; ChangesMinecraftVersion = changesMinecraft; Job = job;
        LocalSnapshot = local; CandidateSnapshot = candidate; Source = source; Overrides = overrides; Snapshots = snapshots;
        SourceObjects = objects; SourceCopies = copies; Plan = plan;
        LocalInputs = localInputs;
    }
}
public sealed record MmcInstancePackageUpdatePreviewResult(MmcInstanceBackendStatus Status, MmcInstancePackageUpdatePreview? Preview = null,
    string? WorkingRoot = null, string? Failure = null);
public sealed record MmcInstancePackageUpdateCommitRequest(MmcInstancePackageUpdatePreview Preview, string ExpectedPreviewSha256) {
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
    public Action? BeforeTransaction { get; init; }
}
public sealed record MmcInstancePackageUpdateResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstanceHistoryPoint? UndoPoint = null, ImmutableArray<string> WorldBackupIds = default, string? Failure = null);
internal sealed class MmcPackageUpdateFile : PackUpdateFileData { }
internal sealed class MmcPackageUpdatePackage : PackUpdatePackageData<MmcPackageUpdateFile> { }
internal sealed class MmcPackageUpdateChange : PackUpdateChangeData<MmcPackageUpdateFile> { }
internal sealed class MmcPackageUpdatePlan : PackUpdatePlanData<MmcPackageUpdatePackage, MmcPackageUpdateChange> { }
