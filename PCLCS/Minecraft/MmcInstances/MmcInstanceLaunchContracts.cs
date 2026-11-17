using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcInstancePrepareLaunchRequest(string VersionRoot, MmcRuntimeEnvironment Environment,
    Guid ExpectedInstanceId, long? ExpectedGeneration = null, bool RepairIncomplete = true) {
    /// <summary>Optional exact selected definition; preparation and any repair must start from these bytes.</summary>
    public string? ExpectedPrimarySha256 { get; init; }
    /// <summary>Optional source bundle already loaded for the exact same primary digest during this launch.</summary>
    public MmcSavedInstanceSources? PreloadedSources { get; init; }
    /// <summary>Primary SHA256 digest matching <see cref="PreloadedSources"/>; mismatched digests trigger a fresh reload.</summary>
    public string? PreloadedSourcesPrimarySha256 { get; init; }
}
public sealed record MmcInstanceLaunchTarget(string ArtifactKey, string TargetRelativePath, string ActualPath);
public sealed record MmcInstanceLaunchDirectories(string ViewKey, string NativeDirectory, string AssetsRoot, string GameAssetsRoot);
public sealed record MmcInstanceLaunchFile(string RelativePath, string Sha256, long SizeBytes);

/// <summary>Measured immutable launch inputs. It owns no workspace or admission/session lease and authorizes no process creation.</summary>
public sealed class MmcPreparedInstanceLaunch {
    public MmcInstanceDefinition Definition { get; }
    public string PrimarySha256 { get; }
    public MmcBoundLaunchPlan BoundPlan { get; }
    public MmcInstancePaths Paths { get; }
    public MmcConfiguration PackageConfiguration { get; }
    public ImmutableArray<MmcInstanceLaunchTarget> Targets { get; }
    public MmcInstanceLaunchDirectories Directories { get; }
    public ImmutableArray<MmcInstanceLaunchFile> Files { get; }
    internal MmcPreparedInstanceLaunch(MmcInstanceDefinition definition, string primarySha256, MmcBoundLaunchPlan plan,
        MmcInstancePaths paths, MmcConfiguration configuration, ImmutableArray<MmcInstanceLaunchTarget> targets,
        MmcInstanceLaunchDirectories directories, ImmutableArray<MmcInstanceLaunchFile> files) {
        Definition = definition; PrimarySha256 = primarySha256; BoundPlan = plan; Paths = paths;
        PackageConfiguration = configuration; Targets = targets; Directories = directories; Files = files;
    }
}
public sealed record MmcInstancePrepareLaunchResult(MmcInstanceBackendStatus Status, MmcPreparedInstanceLaunch? Snapshot = null,
    bool Repaired = false, string? Failure = null, ImmutableArray<MmcDiagnostic> Diagnostics = default) {
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; init; } = Diagnostics.IsDefault ? [] : Diagnostics;
}

/// <summary>Host-owned small support bytes with a caller-reviewed SHA256; never package/authentication data.</summary>
public sealed record MmcInstanceLaunchSupportInput(string FileName, ReadOnlyMemory<byte> Content, string ExpectedSha256);
public sealed record MmcInstanceLaunchSupport(string FileName, string ActualPath, string Sha256, long SizeBytes);
