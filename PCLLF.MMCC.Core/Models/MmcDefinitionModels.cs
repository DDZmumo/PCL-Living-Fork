// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PCLLF.MMCC.Core;

public sealed record MmcPlatformProjectIdentity(string Platform, string ProjectId, string? VersionId = null);
public sealed record MmcInstanceOrigin(string PackageSha256, string OriginalName, MmcPlatformProjectIdentity? PlatformProjectIdentity = null) {
    /// <summary>Optional measured service evidence. Historical three-string identities remain unverified.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MmcObjectReference? PlatformEvidenceReference { get; init; }
}
public sealed record MmcInstanceLayout(string GameRootLeaf, int LayoutVersion = 1);
public enum MmcSourceRecordKind { Manifest, Configuration, Component, RecommendationIndex, PackageMetadata }
/// <summary>References preserved metadata objects, never the original ZIP or a player's absolute path.</summary>
public sealed record MmcSourceMetadataRecord(string RelativePath, string ContentSha256, string ObjectRelativePath,
    MmcSourceRecordKind Kind, string? Uid = null, string? Version = null,
    MmcComponentSourceKind? ComponentSourceKind = null, string? SourceIdentity = null);
public sealed record MmcSourceBundle(string RelativeRoot, string BundleSha256, string ResolutionFingerprint,
    ImmutableArray<MmcSourceMetadataRecord> MetadataRecords) {
    /// <summary>Measured author-package namespace for package-local library keys. Null preserves the historical key protocol.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageLocalScopeSha256 { get; init; }
}
public sealed record MmcObjectReference(string Sha256, string ObjectRelativePath);
public sealed record MmcStoredComponentOverride(string Uid, bool? Enabled = null, string? Version = null,
    MmcObjectReference? PatchReference = null);
public enum MmcArtifactAdoptionState { Unadopted, Adopted }
public sealed record MmcArtifactInventoryRecord(string ArtifactKey, string ComponentUid, MmcArtifactRole Role,
    MmcArtifactScope Scope, string VariantTag, string SourceRevision, string RelativePath,
    MmcArtifactAdoptionState State = MmcArtifactAdoptionState.Unadopted, string? DeclaredSha1 = null,
    string? DeclaredSha256 = null, MmcObjectReference? SourceObject = null, string? SourceMetadataObjectRelativePath = null);
public sealed record MmcArtifactLockRecord(string ArtifactKey, string SourceRevision, string AdoptedSha256,
    long SizeBytes, string RecoveryObjectRelativePath, string TargetRelativePath, string? ObservedSha1 = null);
public sealed record MmcInstalledProjection(string ProfileFingerprint, string OutputJsonSha256, long ManifestGeneration,
    string RuntimePlanFingerprint, string BoundFingerprint) {
    /// <summary>Exact compiled environment. Absence retains historical schema-1 binding semantics.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MmcRuntimeEnvironment? RuntimeEnvironment { get; init; }
}
/// <summary>The large author-file list is stored independently; this envelope pins only its object and count.</summary>
public sealed record MmcExternalBundleReference(string ContentSha256, string ObjectRelativePath, int EntryCount, int FormatVersion = 1);
/// <summary>Player preferences and authentication never belong in this structural definition.</summary>
public sealed record MmcInstanceDefinition(int SchemaVersion, string ManagementKind, string FormatProfile,
    Guid InstanceId, long Generation, MmcInstanceOrigin Origin, MmcSourceBundle SourceBundle, MmcInstanceLayout Layout,
    ImmutableArray<MmcStoredComponentOverride> ComponentOverrides,
    ImmutableArray<MmcArtifactInventoryRecord> ArtifactInventory, ImmutableArray<MmcArtifactLockRecord> ArtifactLocks,
    MmcInstalledProjection? InstalledProjection = null, Guid? ParentInstanceId = null,
    MmcExternalBundleReference? AuthorFileBaseline = null) {
    public ImmutableArray<MmcDerivedOutputRecord> DerivedOutputs { get; init; } = [];
    public ImmutableArray<MmcPackageFileInventoryRecord> PackageFileInventory { get; init; } = [];
    public ImmutableArray<MmcPackageFileLockRecord> PackageFileLocks { get; init; } = [];
    /// <summary>Immutable schema-2 update ownership, separate from its mutable host mirror.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MmcObjectReference? PackageOwnershipReference { get; init; }
}
public enum MmcDefinitionValidationMode { Draft, Published }
public enum MmcDefinitionReadStatus { Valid, Invalid, Unsupported }
public sealed record MmcDefinitionReadResult(MmcInstanceDefinition? Definition, MmcDefinitionReadStatus Status,
    ImmutableArray<MmcDiagnostic> Diagnostics);

public enum MmcBindingIntent { InitialImport, AdoptDeclaredVariant, RepairExisting, UpdateDefinition }
/// <summary>Host observations of verified bytes, not declarations copied from metadata.</summary>
public sealed record MmcMaterializedArtifactFact(string ArtifactKey, string SourceRevision, string ObservedSha256,
    long SizeBytes, string RecoveryObjectRelativePath, string TargetRelativePath, string? ObservedSha1 = null);
public sealed record MmcBindOptions(long ExpectedGeneration, long NewGeneration, MmcBindingIntent Intent,
    string OutputJsonSha256, long ManifestGeneration) {
    public MmcDerivationCompletion? NativeDerivation { get; init; }
}
/// <summary>Cloning preserves one selected published source; no arbitrary target authority is accepted.</summary>
public sealed record MmcCloneBindOptions(Guid ExpectedSourceInstanceId, long ExpectedSourceGeneration,
    string ExpectedSourceBoundFingerprint, Guid NewInstanceId, string OutputJsonSha256) {
    public MmcDerivationCompletion? NativeDerivation { get; init; }
}
public sealed record MmcBoundLaunchPlan(MmcCompiledProfile CompiledProfile, long DefinitionGeneration,
    string BoundFingerprint, ImmutableArray<MmcArtifactLockRecord> ActiveArtifactLocks);
public sealed record MmcBindingResult(MmcInstanceDefinition? Definition, MmcBoundLaunchPlan? Plan,
    ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error);
}
