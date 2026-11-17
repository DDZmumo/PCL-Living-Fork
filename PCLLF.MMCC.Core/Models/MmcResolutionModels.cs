// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>The host fixes source identity and content; Core performs no I/O.</summary>
public sealed record MmcMetadataSnapshot(string Uid, string Version, string Source, string ContentUtf8, string? ExpectedSha256 = null,
    MmcMetadataRequestKind Kind = MmcMetadataRequestKind.Component);
public sealed record MmcVersionRecommendation(string Uid, string Version, string Source, string IndexSha256, string? VersionSha256 = null);
public sealed record MmcComponentOverride(string Uid, bool? Enabled = null, string? Version = null,
    string? PatchContentUtf8 = null, string? PatchSource = null);
public enum MmcComponentSourceKind { PackageLocal, RemoteMetadata, UserPatch }
public sealed record MmcResolvedComponent(string Uid, string? Version, bool Enabled, bool IsAutomatic,
    MmcComponentSourceKind SourceKind, MmcPatch Patch, MmcComponent? OriginalDeclaration);
public sealed record MmcResolvedProfile(string FormatProfile, string SourceFingerprint, MmcPackage Package,
    ImmutableArray<MmcResolvedComponent> Components, ImmutableArray<MmcVersionRecommendation> AdoptedRecommendations);
public sealed record MmcResolutionResult(MmcResolvedProfile? Profile, ImmutableArray<MmcMetadataRequest> MissingMetadata,
    ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(x => x.Severity == MmcDiagnosticSeverity.Error);
    public bool IsComplete => Profile is not null && MissingMetadata.IsEmpty && !HasErrors;
}
