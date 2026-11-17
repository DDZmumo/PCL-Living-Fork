// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

public enum MmcPackageFileRole { Game, LocalLibrary, InstanceExtra, ForeignLauncherMetadata, Metadata }
public sealed record MmcPackageArchiveDescriptor(string Path, bool IsDirectory, long SizeBytes, uint Crc32);
/// <summary>RelativePath is normalized archive-relative, including the instance-root prefix.</summary>
public sealed record MmcPackageFileRecord(string RelativePath, MmcPackageFileRole Role, string? TargetRelativePath,
    string Sha256, string Sha1, long SizeBytes, uint Crc32);
public sealed record MmcPackageFileBaseline(int FormatVersion, string PackageSha256, string RelativeRoot,
    ImmutableArray<MmcPackageArchiveDescriptor> ArchiveEntries, ImmutableArray<MmcPackageFileRecord> Files);
public sealed record MmcPackageBaselineInput(ReadOnlyMemory<byte> Content);
public sealed record MmcPackageBaselineLimits {
    public int MaxDocumentBytes { get; init; } = 64 * 1024 * 1024;
    public MmcReadLimits ArchiveLimits { get; init; } = new();
}
/// <summary>The external reference EntryCount is Files.Length; ArchiveEntries additionally contains directory descriptors.</summary>
public sealed record MmcValidatedPackageBaseline(string BaselineSha256, MmcExternalBundleReference Reference,
    MmcPackageFileBaseline Baseline, string GameRootLeaf);
public sealed record MmcPackageBaselineReadResult(MmcValidatedPackageBaseline? Baseline, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error);
}
public sealed record MmcPackageFileOrigin(string PackageSha256, string BaselineSha256, string SourceRelativePath);
public sealed record MmcPackageFileInventoryRecord(string ArtifactKey, string SourceRevision, string PackageSha256,
    MmcExternalBundleReference BaselineReference, string SourceRelativePath, string LibraryRelativePath,
    string Sha256, string Sha1, long SizeBytes, uint Crc32) {
    public string RelativeRoot { get; init; } = "";
}
public sealed record MmcPackageFileLockRecord(string ArtifactKey, string SourceRevision, string AdoptedSha256,
    string ObservedSha1, long SizeBytes, string RecoveryObjectRelativePath, string TargetRelativePath);
