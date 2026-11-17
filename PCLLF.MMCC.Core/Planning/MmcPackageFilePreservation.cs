// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>Package-private original preservation, deliberately separate from component execution.</summary>
public static class MmcPackageFilePreservation {
    public static string ComputeSourceRevision(string baselineSha256, string packageSha256, string sourceRelativePath) {
        ValidateIdentity(baselineSha256, packageSha256, sourceRelativePath);
        return MmcFingerprint.Of(["mmc-package-file-source-v1", baselineSha256.ToLowerInvariant(), packageSha256.ToLowerInvariant(), sourceRelativePath]);
    }
    public static string ComputeArtifactKey(string baselineSha256, string packageSha256, string sourceRelativePath) {
        ValidateIdentity(baselineSha256, packageSha256, sourceRelativePath);
        return MmcFingerprint.Of(["mmc-preserved-local-library-v1", baselineSha256.ToLowerInvariant(), packageSha256.ToLowerInvariant(), sourceRelativePath]);
    }
    public static ImmutableArray<MmcPackageFileInventoryRecord> LibraryMembers(MmcValidatedPackageBaseline baseline) {
        ArgumentNullException.ThrowIfNull(baseline);
        return baseline.Baseline.Files.Where(f => f.Role == MmcPackageFileRole.LocalLibrary).Select(file => {
            if (file.TargetRelativePath is null || !file.TargetRelativePath.StartsWith("libraries/", StringComparison.Ordinal))
                throw new ArgumentException("Local-library member has no justified instance-library target.", nameof(baseline));
            string key = ComputeArtifactKey(baseline.BaselineSha256, baseline.Baseline.PackageSha256, file.RelativePath);
            string revision = ComputeSourceRevision(baseline.BaselineSha256, baseline.Baseline.PackageSha256, file.RelativePath);
            return new MmcPackageFileInventoryRecord(key, revision, baseline.Baseline.PackageSha256, baseline.Reference,
                file.RelativePath, file.TargetRelativePath["libraries/".Length..], file.Sha256, file.Sha1, file.SizeBytes, file.Crc32) { RelativeRoot = baseline.Baseline.RelativeRoot };
        }).ToImmutableArray();
    }
    public static ImmutableArray<MmcArtifactReference> CreateLibraryArtifacts(MmcValidatedPackageBaseline baseline) =>
        LibraryMembers(baseline).Select((member, index) => new MmcArtifactReference(member.ArtifactKey, "", member.SourceRevision, MmcArtifactRole.DownloadOnly,
            MmcArtifactScope.InstanceLocal, null, member.LibraryRelativePath, [], member.Sha1, member.Sha256, member.SizeBytes, null, [],
            new("", null, "package-baseline:" + baseline.BaselineSha256, baseline.BaselineSha256, "preservedPackageFile", index)) {
                PackageFileOrigin = new(member.PackageSha256, baseline.BaselineSha256, member.SourceRelativePath)
            }).ToImmutableArray();
    private static void ValidateIdentity(string baselineSha256, string packageSha256, string sourceRelativePath) {
        MmcDefinitionCodec.CheckHash(baselineSha256); MmcDefinitionCodec.CheckHash(packageSha256); MmcArtifactRules.Relative(sourceRelativePath, "package-file");
    }
}
