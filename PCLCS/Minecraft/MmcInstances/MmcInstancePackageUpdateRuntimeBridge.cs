using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
// Shares existing typed ownership/publication helpers without invoking an ordinary installer.
public sealed partial class MmcInstanceDefinitionChangeBackend<TNetFile> where TNetFile : notnull {
    internal static ImmutableArray<MmcHistoryFile> PackageMappedFacts(MmcInstancePaths paths, MmcCompiledProfile compiled,
        MmcPrefetchedSources fetched, CancellationToken token) => MappedResourceFacts(paths, compiled, fetched, token);
    internal static void PackageAssertMapped(MmcInstancePaths paths, MmcInstanceDefinition current, ImmutableArray<MmcHistoryFile> desired) => AssertMappedResourceTargets(paths, current, desired);
    internal static void PackageAssertPrivate(MmcInstancePaths paths, MmcInstanceDefinition current, IReadOnlyDictionary<string, string> desired) => AssertPrivateTargets(paths, current, desired);
    internal static IEnumerable<MmcFileMutation> PackagePrivateMutations(MmcInstancePaths paths, MmcInstanceDefinition current,
        ImmutableArray<MmcDefinitionChangeLocalFact> desired, MmcArtifactWorkspace workspace, CancellationToken token) => StagePrivate(paths, current, desired, workspace, token);
    internal static void PackageManifest(MmcConfigurationWriteScope scope, MmcResolvedProfile profile, MmcCompiledProfile compiled, Guid id, long generation) => SetManifest(scope, profile, compiled, id, generation);
    internal static MmcFileMutation[] PackageMerge(IEnumerable<MmcFileMutation> values) => Merge(values);
}
