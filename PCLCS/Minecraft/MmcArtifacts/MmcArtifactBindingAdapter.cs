using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcPreparedBindingInputs(ImmutableArray<MmcMaterializedArtifactFact> Facts,
    ImmutableArray<MmcDerivedOutputRecord> DerivedOutputs, ImmutableArray<MmcAssetIndexInput> AssetIndexes,
    MmcDerivationCompletion? NativeCompletion) {
    public MmcBindOptions ApplyTo(MmcBindOptions options) => options with { NativeDerivation = NativeCompletion };
}

/// <summary>Converts actual host observations without publishing, adopting old unknown bytes, or replacing prior authority.</summary>
public static class MmcArtifactBindingAdapter {
    private const string NativeProducer = "bounded-native-v1", AssetProducer = "bounded-assets-v1";

    public static MmcPreparedBindingInputs Create(MmcArtifactRequest request, MmcCompiledProfile compiled, MmcPreparedArtifacts prepared) {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(compiled); ArgumentNullException.ThrowIfNull(prepared);
        if (!MmcProfileFingerprints.Matches(compiled)) throw new IOException("Compiled profile fingerprints are not canonical.");
        var paths = request.Paths;
        if (!prepared.StageRoot.StartsWith(paths.RuntimeRoot + Path.DirectorySeparatorChar + ".staging" + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Prepared bytes belong to a different instance workspace.");
        var observations = prepared.Observations.ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
        var locks = prepared.AdoptedLocks.ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
        if (observations.Count != locks.Count) throw new IOException("Prepared locks and actual observations have different identities.");
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in prepared.OrderedBindings) targets[binding.Artifact.Key] = binding.TargetRelativePath;
        var declared = new Dictionary<string, MmcArtifactReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in (request.Artifacts.IsDefault ? [] : request.Artifacts)
            .Concat(request.AdditionalPreservedArtifacts.IsDefault ? [] : request.AdditionalPreservedArtifacts)
            .Concat(compiled.Classpath).Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts)) {
            if (declared.TryGetValue(artifact.Key, out var previous) && (previous.SourceRevision != artifact.SourceRevision || previous.RelativePath != artifact.RelativePath || previous.Role != artifact.Role || previous.Scope != artifact.Scope))
                throw new IOException("Prepared artifact identity has inconsistent declarations.");
            declared[artifact.Key] = artifact;
        }
        if (!prepared.AssetFiles.IsEmpty) {
            var assetObjectRelatives = MapParallel(prepared.AssetFiles, asset => Relative(paths, asset.ObjectPath));
            for (int i = 0; i < prepared.AssetFiles.Length; i++) targets[prepared.AssetFiles[i].ArtifactKey] = assetObjectRelatives[i];
        }
        var facts = ImmutableArray.CreateBuilder<MmcMaterializedArtifactFact>(prepared.AdoptedLocks.Length);
        foreach (var value in prepared.AdoptedLocks) {
            if (!observations.TryGetValue(value.ArtifactKey, out var observed) || observed.ObservedSha256 != value.AdoptedSha256 || observed.SizeBytes != value.SizeBytes ||
                !MmcArtifactIo.Digest(observed.ObservedSha1, 40)) throw new IOException("An adopted record lacks matching measured contents.");
            if (!targets.TryGetValue(value.ArtifactKey, out string? target)) {
                if (!declared.TryGetValue(value.ArtifactKey, out var artifact)) throw new IOException("Preserved observation has no explicit artifact declaration.");
                // A preserved inactive input is not promoted into OrderedBindings/classpath.
                target = MmcArtifactMaterializer.BindingTargetRelative(artifact, prepared.ViewKey);
            }
            facts.Add(new(value.ArtifactKey, value.SourceRevision, observed.ObservedSha256, observed.SizeBytes,
                value.RecoveryObjectRelativePath, target, observed.ObservedSha1));
        }
        var builtFacts = facts.MoveToImmutable();
        if (!builtFacts.IsEmpty) {
            MmcInstancePaths.ForEachBounded(builtFacts, (_, fact, _) => paths.ResolveRelative(MmcPathRole.Instance, fact.TargetRelativePath), CancellationToken.None);
        }
        var factMap = builtFacts.ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
        var derived = ImmutableArray.CreateBuilder<MmcDerivedOutputRecord>();
        MmcDerivationCompletion? nativeCompletion = null;
        if (!compiled.NativeArtifacts.IsEmpty) {
            if (!prepared.NativeExtractionComplete || prepared.NativeCache is null) throw new IOException("Native extraction has no complete host observation.");
            string sources = MmcDerivedProofs.ComputeNativeSourceSetFingerprint(compiled.NativeArtifacts,
                compiled.NativeArtifacts.Select(x => DigestFact(factMap[x.Key])).ToArray(), NativeProducer);
            if (prepared.NativeCache.ViewKey != prepared.ViewKey || prepared.NativeCache.SourceSetFingerprint != sources ||
                prepared.NativeCache.CompleteFiles.Length != prepared.NativeFiles.Length) throw new IOException("Native cache batch differs from its locked inputs.");
            string context = MmcDerivedProofs.ComputeDerivedContextFingerprint(compiled.RuntimePlanFingerprint, sources, NativeProducer);
            var nativeKeys = compiled.NativeArtifacts.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nativeParents = new MmcMaterializedArtifactFact[prepared.NativeFiles.Length];
            for (int i = 0; i < prepared.NativeFiles.Length; i++) {
                var file = prepared.NativeFiles[i];
                if (!nativeKeys.Contains(file.SourceArtifactKey) || !factMap.TryGetValue(file.SourceArtifactKey, out var parent)) throw new IOException("Native output has no active parent.");
                nativeParents[i] = parent;
            }
            string[] nativeTargets = MapParallel(prepared.NativeFiles, file => Relative(paths, file.TargetPath));
            for (int i = 0; i < prepared.NativeFiles.Length; i++) {
                var file = prepared.NativeFiles[i];
                var parent = nativeParents[i];
                derived.Add(new(MmcDerivedOutputKind.NativeFile, parent.ArtifactKey, parent.ObservedSha256, NativeProducer,
                    sources, compiled.RuntimePlanFingerprint, context, prepared.ViewKey, file.RelativePath,
                    nativeTargets[i], file.Sha256, file.SizeBytes));
            }
            nativeCompletion = new(NativeProducer, sources, compiled.RuntimePlanFingerprint, prepared.ViewKey, prepared.NativeFiles.Length, true);
        } else if (!prepared.NativeFiles.IsEmpty) throw new IOException("Unexpected native outputs are not part of the compiled profile.");
        var indexes = prepared.AssetIndexContents.Select(x => new MmcAssetIndexInput(x.ArtifactKey, x.Utf8Content.ToArray())).ToImmutableArray();
        if (compiled.Assets.Artifact is { } index) {
            if (!prepared.AssetsMaterialized || indexes.Length != 1 || indexes[0].ArtifactKey != index.Key || !factMap.TryGetValue(index.Key, out var parent))
                throw new IOException("Asset outputs have no verified index parent.");
            string sources = MmcDerivedProofs.ComputeAssetSourceSetFingerprint(parent.ArtifactKey, parent.ObservedSha256, parent.SizeBytes, AssetProducer);
            string context = MmcDerivedProofs.ComputeDerivedContextFingerprint(compiled.RuntimePlanFingerprint, sources, AssetProducer);
            string ownedAssetsRoot = prepared.AssetsRoot ?? throw new IOException("Owned assets root is missing.");
            foreach (var asset in prepared.AssetFiles.DistinctBy(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase)) {
                var fact = factMap[asset.ArtifactKey];
                if (fact.ObservedSha256 != asset.Sha256 || fact.ObservedSha1 != asset.Sha1 || fact.SizeBytes != asset.SizeBytes || fact.SourceRevision != parent.ObservedSha256)
                    throw new IOException("Asset output differs from its measured original object.");
                string relative = RelativeUnder(ownedAssetsRoot, asset.ObjectPath);
                derived.Add(new(MmcDerivedOutputKind.AssetObject, index.Key, parent.ObservedSha256, AssetProducer, sources,
                    compiled.RuntimePlanFingerprint, context, prepared.ViewKey, relative, fact.TargetRelativePath,
                    fact.ObservedSha256, fact.SizeBytes, fact.ArtifactKey, fact.ObservedSha1, fact.RecoveryObjectRelativePath));
            }
            bool hasAliases = prepared.AssetFiles.Any(a => a.VirtualPath is not null || a.MappedResourcePath is not null);
            if (hasAliases) {
                var aliasTargets = MapParallel(prepared.AssetFiles, asset => (
                    Virtual: asset.VirtualPath is { } v ? Relative(paths, v) : null,
                    Mapped: asset.MappedResourcePath is { } m ? Relative(paths, m) : null));
                for (int i = 0; i < prepared.AssetFiles.Length; i++) {
                    var asset = prepared.AssetFiles[i];
                    var fact = factMap[asset.ArtifactKey];
                    if (asset.VirtualPath is { } virtualPath) AddAlias(MmcDerivedOutputKind.AssetVirtualFile,
                        RelativeUnder(ownedAssetsRoot, virtualPath), aliasTargets[i].Virtual!, fact);
                    if (asset.MappedResourcePath is { } mapped) AddAlias(MmcDerivedOutputKind.AssetMappedResource, asset.Name, aliasTargets[i].Mapped!, fact);
                }
                void AddAlias(MmcDerivedOutputKind kind, string relative, string targetRelative, MmcMaterializedArtifactFact fact) =>
                    derived.Add(new(kind, fact.ArtifactKey, fact.ObservedSha256, AssetProducer, sources, compiled.RuntimePlanFingerprint,
                        context, prepared.ViewKey, relative, targetRelative, fact.ObservedSha256, fact.SizeBytes, ObservedSha1: fact.ObservedSha1));
            }
        } else if (!prepared.AssetFiles.IsEmpty || !indexes.IsEmpty) throw new IOException("Unexpected asset outputs have no compiled index.");
        return new(builtFacts, derived.ToImmutable(), indexes, nativeCompletion);
    }

    private static TResult[] MapParallel<TSource, TResult>(IReadOnlyList<TSource> items, Func<TSource, TResult> map) {
        var results = new TResult[items.Count];
        if (items.Count == 0) return results;
        MmcInstancePaths.ForEachBounded(items, (index, item, _) => results[index] = map(item), CancellationToken.None);
        return results;
    }

    private static MmcArtifactDigestFact DigestFact(MmcMaterializedArtifactFact fact) => new(fact.ArtifactKey, fact.SourceRevision, fact.ObservedSha256, fact.SizeBytes);
    private static string Relative(MmcInstancePaths paths, string target) {
        string relative = RelativeUnder(paths.InstanceRoot, target);
        string authorized = paths.ResolveRelative(MmcPathRole.Instance, relative);
        if (!string.Equals(authorized, Path.GetFullPath(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Prepared target does not match the instance boundary.");
        return relative;
    }
    private static string RelativeUnder(string root, string target) {
        string relative = Path.GetRelativePath(root, target).Replace('\\', '/'); MmcArtifactIo.Relative(relative);
        return relative;
    }
}

public sealed partial class MmcArtifactMaterializer {
    internal static string BindingTargetRelative(MmcArtifactReference artifact, string viewKey) => TargetRelative(artifact, viewKey);
}
