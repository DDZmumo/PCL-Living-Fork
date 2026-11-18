// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

public sealed record MmcArtifactDigestFact(string ArtifactKey, string SourceRevision, string Sha256, long SizeBytes);
public sealed record MmcDerivationCompletion(string ProducerVersion, string SourceSetFingerprint,
    string RuntimePlanFingerprint, string ViewKey, int OutputCount, bool Complete);
public enum MmcDerivedOutputKind { NativeFile, AssetObject, AssetVirtualFile, AssetMappedResource }
public sealed record MmcDerivedOutputRecord(MmcDerivedOutputKind Kind, string ParentArtifactKey, string ParentSha256,
    string ProducerVersion, string SourceSetFingerprint, string RuntimePlanFingerprint, string ContextFingerprint, string ViewKey,
    string RelativePath, string TargetRelativePath, string Sha256, long SizeBytes,
    string? ArtifactKey = null, string? ObservedSha1 = null, string? RecoveryObjectRelativePath = null);
/// <summary>Bounded raw bytes from the host's verified immutable asset-index input.</summary>
public sealed record MmcAssetIndexInput(string ArtifactKey, ReadOnlyMemory<byte> Content);
public sealed record MmcAssetIndexObject(string Name, string Sha1, long SizeBytes);
public sealed record MmcAssetIndexDocument(string ArtifactKey, string ContentSha256, bool Virtual, bool MapToResources,
    ImmutableArray<MmcAssetIndexObject> Objects) {
    /// <summary>prism-v1 preserves both raw flags, with the native virtual-first layout precedence.</summary>
    public bool EffectiveMapToResources => MapToResources && !Virtual;
}
public sealed record MmcAssetIndexReadResult(MmcAssetIndexDocument? Index, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error);
}
