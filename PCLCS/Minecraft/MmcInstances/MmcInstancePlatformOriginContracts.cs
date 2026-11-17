using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcPlatformInstanceSelection(string VersionRoot, Guid ExpectedInstanceId, long ExpectedGeneration, string ExpectedPrimarySha256);
public sealed record MmcPlatformOriginAttachRequest(MmcPlatformInstanceSelection Instance, string OriginalZip, MmcPlatformReleaseDescriptor Release);
/// <summary>Created only by the service-check backend. Public display descriptors are never publication authority.</summary>
public sealed class MmcPlatformOriginPreview {
    public MmcPlatformOriginAttachRequest Selection { get; }
    public string CandidateSha256 { get; }
    public string PackageSha256 { get; }
    public MmcObjectReference EvidenceReference { get; }
    internal ImmutableArray<byte> Evidence { get; }
    internal MmcPlatformOriginPreview(MmcPlatformOriginAttachRequest request, ImmutableArray<byte> bytes, string candidate, string packageSha256) {
        Selection = request; Evidence = bytes; CandidateSha256 = candidate; EvidenceReference = MmcPlatformOriginCodec.Reference(bytes.AsSpan());
        PackageSha256 = packageSha256;
    }
}
public sealed record MmcPlatformOriginPreviewResult(MmcInstanceBackendStatus Status, MmcPlatformOriginPreview? Preview = null, string? Failure = null);
public sealed record MmcPlatformOriginCommitRequest(MmcPlatformOriginPreview Preview, string ExpectedCandidateSha256) {
    public Action? BeforeTransaction { get; init; }
    public Action<MmcTransactionCheckpoint, int>? TransactionCheckpoint { get; init; }
}
public sealed record MmcPlatformOriginResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcBoundLaunchPlan? BoundPlan = null, MmcInstanceHistoryPoint? UndoPoint = null, string? Failure = null);
public sealed record MmcPlatformOnlineUpdateRequest(MmcPlatformInstanceSelection Instance, MmcPlatformReleaseDescriptor Release, MmcRuntimeEnvironment Environment);
internal sealed record MmcVerifiedPackageOrigin(MmcPlatformInstanceSelection Instance, MmcPlatformReleaseDescriptor Release, ImmutableArray<byte> Evidence, MmcPlatformOriginCatalog Catalog) {
    internal MmcObjectReference Reference => MmcPlatformOriginCodec.Reference(Evidence.AsSpan());
}
