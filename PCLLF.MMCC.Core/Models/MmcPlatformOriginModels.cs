// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>Modrinth v2 has no file ID: its exact file key is its strongest advertised content digest.</summary>
public sealed record MmcPlatformReleaseFile(string FileId, string FileName, string DownloadUrl, long SizeBytes,
    string? Sha1, string? Sha512);
/// <summary>Pure metadata only. The host is responsible for obtaining this from an authorized service endpoint.</summary>
public sealed record MmcPlatformRelease(MmcPlatformProjectIdentity Identity, string ProjectSource, string VersionSource,
    string ProjectJson, string VersionJson, ImmutableArray<MmcPlatformReleaseFile> Files);
/// <summary>Measurements of the original ZIP, never a reconstruction from extracted members.</summary>
public sealed record MmcPlatformPackageMeasurement(string Sha256, string Sha1, string Sha512, long SizeBytes);
/// <summary>Offline evidence of a host service check; this data cannot independently prove TLS provenance.</summary>
public sealed record MmcPlatformOriginEvidence(int FormatVersion, MmcPlatformProjectIdentity Identity,
    MmcPlatformReleaseFile SelectedFile, MmcPlatformPackageMeasurement Package,
    string ProjectSource, string VersionSource, string ProjectJson, string VersionJson);
