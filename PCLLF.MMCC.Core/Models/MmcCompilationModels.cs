// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

/// <summary>Architecture describes selected Java, not the launcher process.</summary>
public sealed record MmcRuntimeEnvironment(string OperatingSystem, string Architecture,
    int JavaMajor = 0, int JavaBitness = 64, string? OsVersion = null);
public sealed record MmcFieldSource(string ComponentUid, string? ComponentVersion, string Source,
    string SourceSha256, string Field, int Position);
public sealed record MmcArgumentToken(string Value, MmcFieldSource Provenance);
public enum MmcArtifactRole { Classpath, MainJar, Native, DownloadOnly, LoggingConfiguration, AssetIndex }
public enum MmcArtifactScope { Shared, InstanceLocal }
public sealed record MmcMavenCoordinate(string Group, string Artifact, string Version, string Classifier, string Extension) {
    public string Identity => Group + ":" + Artifact + ":" + Classifier;
    public string Name => Group + ":" + Artifact + ":" + Version + (Classifier.Length == 0 ? "" : ":" + Classifier) + (Extension == "jar" ? "" : "@" + Extension);
}
public sealed record MmcArtifactReference(string Key, string ComponentUid, string SourceRevision,
    MmcArtifactRole Role, MmcArtifactScope Scope, MmcMavenCoordinate? Coordinate,
    string RelativePath, ImmutableArray<string> DownloadUrls, string? Sha1, string? Sha256, long? Size,
    string? NativeClassifier, ImmutableArray<string> ExtractExcludes, MmcFieldSource Provenance) {
    /// <summary>Preservation-only input without a component UID; never belongs in a compiled execution plan.</summary>
    public MmcPackageFileOrigin? PackageFileOrigin { get; init; }
}
/// <summary>Source inventory includes inactive declarations; it is never an execution list.</summary>
public sealed record MmcArtifactDeclaration(string ComponentUid, bool Enabled, MmcArtifactRole Role,
    JsonElement Declaration, MmcFieldSource Provenance);
public sealed record MmcAssetIndex(string Id, MmcArtifactReference? Artifact, MmcFieldSource Provenance);
public sealed record MmcOverrideEffect(string ComponentUid, ImmutableArray<string> Fields, bool ReplacesBuiltinLaunchDefinition);
public enum MmcJavaPolicyKind { Unspecified, DeclaredCandidates, Unsatisfiable }
public sealed record MmcJavaConstraint(string Source, int? MinimumMajor = null, int? MaximumMajor = null,
    ImmutableArray<int> AllowedMajors = default);
public sealed record MmcJavaPolicy(MmcJavaPolicyKind Kind, ImmutableArray<int> AllowedMajors,
    string? RecommendedRuntimeName, ImmutableArray<MmcJavaConstraint> HardConstraints) {
    public bool Allows(int major) => major > 0 && Kind != MmcJavaPolicyKind.Unsatisfiable &&
        (Kind != MmcJavaPolicyKind.DeclaredCandidates || AllowedMajors.Contains(major)) &&
        HardConstraints.All(c => (c.MinimumMajor is null || major >= c.MinimumMajor) &&
            (c.MaximumMajor is null || major <= c.MaximumMajor) && (c.AllowedMajors.IsDefault || c.AllowedMajors.Contains(major)));
}
public sealed record MmcJavaCandidate(string Identity, int Major, string Architecture);
public enum MmcJavaSelectionState { CompatibleCandidates, NoInstalledCandidate, Unsatisfiable }
public sealed record MmcJavaSelection(MmcJavaSelectionState State, ImmutableArray<MmcJavaCandidate> Candidates);
/// <summary>A token-free logical plan; host must authorize paths, materialize and bind identities transactionally.</summary>
public sealed record MmcCompiledProfile(string FormatProfile, string SourceFingerprint, string ProfileFingerprint,
    string RuntimePlanFingerprint, MmcRuntimeEnvironment Environment, string MinecraftVersion, string? MinecraftVersionType,
    string MainClass, MmcArtifactReference MainJar, MmcAssetIndex Assets,
    ImmutableArray<MmcArgumentToken> JvmArguments, ImmutableArray<MmcArgumentToken> GameArguments,
    ImmutableArray<string> Tweakers, ImmutableArray<string> Traits,
    ImmutableArray<MmcArtifactReference> Classpath, ImmutableArray<MmcArtifactReference> NativeArtifacts,
    ImmutableArray<MmcArtifactReference> DownloadOnlyArtifacts, ImmutableArray<MmcArtifactReference> AuxiliaryArtifacts,
    ImmutableArray<MmcArtifactDeclaration> ArtifactInventory, MmcJavaPolicy JavaPolicy,
    ImmutableArray<MmcFieldSource> FieldProvenance, ImmutableArray<MmcOverrideEffect> OverrideEffects);
public sealed record MmcCompilationResult(MmcCompiledProfile? Profile, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error);
}
