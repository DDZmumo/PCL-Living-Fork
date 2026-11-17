using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed record MmcResolvedSourceBundle(MmcResolutionResult Resolution,
    ImmutableArray<MmcMetadataSnapshot> Snapshots);

/// <summary>Host batch I/O around Core's pure resolver. No version-selection policy lives here.</summary>
public static class MmcMetadataResolution {
    public static async Task<MmcResolvedSourceBundle> ResolveAsync(MmcPackage package, MmcMetadataStore store,
        IEnumerable<MmcMetadataSnapshot>? adoptedSnapshots = null,
        IReadOnlyList<MmcComponentOverride>? overrides = null, MmcReadLimits? limits = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(store);
        limits ??= new();
        List<MmcMetadataSnapshot> snapshots = [];
        foreach (MmcMetadataSnapshot snapshot in adoptedSnapshots ?? []) {
            if (snapshots.Count >= limits.MaxComponents * 4L) throw new InvalidDataException("Adopted metadata snapshot budget exceeded.");
            snapshots.Add(snapshot);
        }
        HashSet<MmcMetadataKey> fetched = [];
        // Graph depth is not transport depth: a recommendation and its exact
        // metadata need separate batches, even for one dependency-free node.
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            MmcResolutionResult result = MmcComponentResolver.Resolve(package, snapshots, overrides, limits);
            if (result.HasErrors || result.IsComplete) return new(result, snapshots.ToImmutableArray());
            if (result.MissingMetadata.IsEmpty) throw new InvalidDataException("Incomplete metadata resolution made no progress.");
            MmcMetadataKey[] requests = result.MissingMetadata.Select(request => new MmcMetadataKey(request.Uid,
                request.Version, request.Kind == MmcMetadataRequestKind.Recommendation, request.SourceIdentity)).Distinct().ToArray();
            if (snapshots.Count + (long)requests.Length > limits.MaxComponents * 4L)
                throw new InvalidDataException("Metadata request progress exceeded its snapshot budget.");
            if (requests.Any(key => !fetched.Add(key)))
                throw new InvalidDataException("Fixed metadata did not satisfy its request; refusing an endless refetch.");
            IReadOnlyList<MmcMetadataContent> batch = await store.FetchBatchAsync(requests, cancellationToken).ConfigureAwait(false);
            foreach (MmcMetadataContent item in batch) snapshots.Add(new(item.Key.Uid, item.Key.Version, item.Source,
                item.ContentUtf8, item.Sha256, item.Key.Recommendation ? MmcMetadataRequestKind.Recommendation : MmcMetadataRequestKind.Component));
        }
    }
}
