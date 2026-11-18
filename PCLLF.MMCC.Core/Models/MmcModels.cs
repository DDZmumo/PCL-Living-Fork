// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

/// <summary>Archive facts supplied by the host, never an authorization to access a path.</summary>
public sealed record MmcArchiveEntry(string Path, long UncompressedSize = 0, bool IsDirectory = false);

/// <summary>Decoded UTF-8 metadata and archive facts. The caller owns I/O and decoding.</summary>
public sealed record MmcPackageInput(
    IReadOnlyList<MmcArchiveEntry> Entries,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record MmcReadLimits {
    public int MaxArchiveEntries { get; init; } = 100_000;
    public int MaxComponents { get; init; } = 4096;
    public int MaxDependencyEdges { get; init; } = 65_536;
    public int MaxDepth { get; init; } = 128;
    public int MaxMetadataBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxTotalMetadataBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxArchiveBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaxTotalPathCharacters { get; init; } = 16L * 1024 * 1024;
    public long MaxLogicalPathCharacters { get; init; } = 32L * 1024 * 1024;
}

public enum MmcDiagnosticSeverity { Information, Warning, Error }
public sealed record MmcDiagnostic(string Code, MmcDiagnosticSeverity Severity, string Source, string Message);
public enum MmcMetadataRequestKind { Component, Recommendation }
public sealed record MmcMetadataRequest(string Uid, string Version, string RequestedBy,
    MmcMetadataRequestKind Kind = MmcMetadataRequestKind.Component, string SourceIdentity = "prism-meta");
public sealed record MmcRequirement(string Uid, string? EqualsVersion, string? SuggestedVersion);

/// <summary>Cloned JSON values remain immutable and usable after the parsing document is disposed.</summary>
public sealed record MmcPatch(
    string Source, string ContentSha256, string Uid, string? Version,
    ImmutableArray<MmcRequirement> Requires, ImmutableArray<MmcRequirement> Conflicts,
    ImmutableDictionary<string, JsonElement> Fields);

public sealed record MmcComponent(
    string Uid, string? RequestedVersion, string? CachedVersion,
    bool Disabled, bool DependencyOnly, bool Important, bool EffectiveEnabled,
    string? EffectiveVersion, MmcPatch? LocalPatch,
    ImmutableDictionary<string, JsonElement> ManifestFields);

public enum MmcConfigurationFormat { Absent, Legacy, QtScalar }
public sealed record MmcOpaquePackageMetadata(string RelativePath, string? ContentSha256);
/// <summary>One ordered active cfg environment entry. The value is literal: cfg decoding applies, macro expansion does not.</summary>
public sealed record MmcEnvironmentVariable(string Name, string Value);
public sealed record MmcConfiguration(
    MmcConfigurationFormat Format, ImmutableDictionary<string, string> Values) {
    public string? Name => Values.GetValueOrDefault("name");
    /// <summary>
    /// Ordered mapping of an active <c>OverrideEnv / Env</c>, in source order. Empty when the source
    /// declares no active mapping; the managed process then inherits the host's base environment.
    /// </summary>
    public ImmutableArray<MmcEnvironmentVariable> EnvironmentOverride { get; init; } = [];
}

/// <summary>A parsed source bundle, deliberately not an executable or install-ready launch plan.</summary>
public sealed record MmcPackage(
    string RelativeRoot, string GameRootLeaf, string ManifestSha256,
    ImmutableArray<MmcComponent> Components, MmcConfiguration Configuration,
    ImmutableArray<MmcMetadataRequest> MissingMetadata,
    ImmutableArray<MmcArchiveEntry> ArchiveEntries, long MetadataUtf8ByteCount = 0) {
    public ImmutableArray<MmcOpaquePackageMetadata> PackageMetadata { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageLocalScopeSha256 { get; init; }
}

/// <summary>Read diagnostics only. HasErrors=false is neither a complete semantic profile nor install/launch authorization.</summary>
public sealed record MmcParseResult(MmcPackage? Package, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(x => x.Severity == MmcDiagnosticSeverity.Error);
}

/// <summary>Validated logical archive index; the host still owns bounded byte reads and real path authorization.</summary>
public sealed record MmcArchiveInspection(string? RelativeRoot, string? GameRootLeaf,
    ImmutableArray<string> MetadataPaths, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasErrors => Diagnostics.Any(x => x.Severity == MmcDiagnosticSeverity.Error);
}
