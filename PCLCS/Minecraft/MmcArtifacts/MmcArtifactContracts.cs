using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcArtifactIntent { AdoptDeclaredVariant, RepairExisting }

/// <summary>Host-authorized source cache or unpacked-source boundary; never an execution path.</summary>
public sealed record MmcArtifactSource(string ApprovedRoot, string FilePath);

/// <summary>Adapters call the existing download/cache machinery; this service owns no download queue.</summary>
public sealed record MmcArtifactSourceAccess(
    Func<MmcArtifactReference, CancellationToken, ValueTask<MmcArtifactSource?>>? TryGetCachedAsync = null,
    Func<MmcArtifactReference, IProgress<MmcArtifactProgress>?, CancellationToken, ValueTask<MmcArtifactSource?>>? FetchAsync = null);

public sealed record MmcArtifactLock(string ArtifactKey, string SourceRevision, string AdoptedSha256,
    long SizeBytes, string RecoveryObjectRelativePath);

public sealed record MmcArtifactProgress(string Phase, string ArtifactKey, long CompletedBytes);

public sealed record MmcArtifactRequest(MmcInstancePaths Paths, string LogicalContext,
    ImmutableArray<MmcArtifactReference> Artifacts, ImmutableArray<MmcArtifactReference> AdditionalPreservedArtifacts,
    ImmutableArray<MmcArtifactLock> ExistingLocks, MmcArtifactIntent Intent, MmcAssetIndex? Assets = null,
    MmcNativeViewCache? TrustedNativeCache = null);

public sealed record MmcArtifactBinding(MmcArtifactReference Artifact, string Sha256, long SizeBytes,
    string TargetRelativePath, string TargetPath, string VerifiedInputPath, string? ObservedSha1 = null);
public sealed record MmcArtifactObservation(string ArtifactKey, string ObservedSha256, string ObservedSha1, long SizeBytes);
public sealed record MmcNativeFileBinding(string SourceArtifactKey, string RelativePath, string TargetPath, string Sha256, long SizeBytes);
public sealed record MmcNativeFileFact(string SourceArtifactKey, string RelativePath, string Sha256, long SizeBytes);
/// <summary>Only accept from validated instance binding/definition records, never a self-authenticating runtime manifest.</summary>
public sealed record MmcNativeViewCache(string ViewKey, string SourceSetFingerprint, ImmutableArray<MmcNativeFileFact> CompleteFiles);
public sealed record MmcAssetFileBinding(string Name, string ArtifactKey, string Sha1, string Sha256, long SizeBytes,
    string ObjectPath, string? VirtualPath, string? MappedResourcePath);
public sealed record MmcAssetIndexContent(string ArtifactKey, string ObservedSha256, ImmutableArray<byte> Utf8Content);

/// <summary>Publication is exclusively the instance transaction executor's responsibility.</summary>
public sealed record MmcArtifactStagedMutation(string StagedPath, string NewSourceRelativePath, string TargetRelativePath,
    string NewSha256, long SizeBytes, bool ExpectedMissing, string? ExpectedExistingSha256);

public sealed record MmcArtifactLimits(long MaxArtifactBytes = 4L * 1024 * 1024 * 1024,
    long MaxCopiedBytes = 64L * 1024 * 1024 * 1024, int MaxArtifacts = 100000,
    int MaxNativeEntries = 10000, long MaxNativeBytes = 256L * 1024 * 1024,
    int MaxAssetObjects = 100000, int MaxAssetIndexBytes = 16 * 1024 * 1024);

/// <summary>
/// Immutable binding result plus an owned temporary workspace. Dispose only after the transaction
/// has consumed the staged files. Disposal never removes published runtime views or original objects.
/// This result does not acquire an operation/session lease or authorize a game launch.
/// </summary>
public sealed class MmcPreparedArtifacts : IDisposable {
    private readonly MmcArtifactWorkspace workspace;
    public string ViewKey { get; }
    public string StageRoot => workspace.Root;
    public ImmutableArray<MmcArtifactBinding> OrderedBindings { get; }
    public ImmutableArray<MmcArtifactLock> AdoptedLocks { get; }
    public ImmutableArray<MmcArtifactStagedMutation> StagedMutations { get; }
    public bool NativeExtractionComplete { get; }
    public bool AssetsMaterialized { get; }
    public string? NativeDirectory { get; }
    public string? AssetsRoot { get; }
    public ImmutableArray<MmcNativeFileBinding> NativeFiles { get; }
    public ImmutableArray<MmcAssetFileBinding> AssetFiles { get; }
    public string? GameAssetsRoot { get; }
    public string? MappedResourcesRoot { get; }
    public ImmutableArray<MmcArtifactObservation> Observations { get; }
    public MmcNativeViewCache? NativeCache { get; }
    public bool NativeCacheReused { get; }
    public ImmutableArray<MmcAssetIndexContent> AssetIndexContents { get; }

    internal MmcPreparedArtifacts(MmcArtifactWorkspace workspace, string viewKey,
        ImmutableArray<MmcArtifactBinding> bindings, ImmutableArray<MmcArtifactLock> locks,
        ImmutableArray<MmcArtifactStagedMutation> mutations, bool nativeComplete, bool assetsMaterialized,
        string? nativeDirectory = null, string? assetsRoot = null, ImmutableArray<MmcNativeFileBinding> nativeFiles = default,
        ImmutableArray<MmcAssetFileBinding> assetFiles = default, string? gameAssetsRoot = null, string? mappedResourcesRoot = null,
        ImmutableArray<MmcArtifactObservation> observations = default, MmcNativeViewCache? nativeCache = null, bool nativeCacheReused = false,
        ImmutableArray<MmcAssetIndexContent> assetIndexContents = default) {
        this.workspace = workspace; ViewKey = viewKey; OrderedBindings = bindings; AdoptedLocks = locks;
        StagedMutations = mutations; NativeExtractionComplete = nativeComplete; AssetsMaterialized = assetsMaterialized;
        NativeDirectory = nativeDirectory; AssetsRoot = assetsRoot;
        NativeFiles = nativeFiles.IsDefault ? [] : nativeFiles;
        AssetFiles = assetFiles.IsDefault ? [] : assetFiles;
        GameAssetsRoot = gameAssetsRoot; MappedResourcesRoot = mappedResourcesRoot;
        Observations = observations.IsDefault ? [] : observations;
        NativeCache = nativeCache; NativeCacheReused = nativeCacheReused;
        AssetIndexContents = assetIndexContents.IsDefault ? [] : assetIndexContents;
    }
    public void Dispose() => workspace.Dispose();
}
