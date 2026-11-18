// SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace PCLLF.MMCC.Core;

/// <summary>Shared pure protocols for host extraction/cache proofs; no mutable cache file is an authority.</summary>
public static class MmcDerivedProofs {
    private const string AssetKeyPrefix = "PCLLF:asset-object:v1:";
    public static string ComputeNativeSourceSetFingerprint(IReadOnlyList<MmcArtifactReference> nativeArtifacts,
        IReadOnlyList<MmcArtifactDigestFact> observedInputs, string producerVersion = "bounded-native-v1") {
        ArgumentNullException.ThrowIfNull(nativeArtifacts);
        ArgumentNullException.ThrowIfNull(observedInputs);
        Dictionary<string, MmcArtifactDigestFact> facts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in observedInputs) {
            if (fact is null || !facts.TryAdd(fact.ArtifactKey, fact)) throw new ArgumentException("Duplicate/null observed artifact identity.", nameof(observedInputs));
            MmcDefinitionCodec.CheckHash(fact.ArtifactKey); MmcDefinitionCodec.CheckHash(fact.SourceRevision); MmcDefinitionCodec.CheckHash(fact.Sha256);
            if (fact.SizeBytes < 0) throw new ArgumentException("Negative observed input size.", nameof(observedInputs));
        }
        List<string?> values = ["mmc-native-input-set-v1", producerVersion, nativeArtifacts.Count.ToString(CultureInfo.InvariantCulture)];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in nativeArtifacts) {
            if (artifact is null || artifact.Role != MmcArtifactRole.Native || !unique.Add(artifact.Key) ||
                !facts.TryGetValue(artifact.Key, out var fact) || !MmcDefinitionCodec.SameHash(fact.SourceRevision, artifact.SourceRevision))
                throw new ArgumentException("Native input has no unique matching source observation.", nameof(nativeArtifacts));
            values.AddRange([artifact.Key, artifact.SourceRevision, artifact.NativeClassifier, fact.Sha256, fact.SizeBytes.ToString(CultureInfo.InvariantCulture), artifact.ExtractExcludes.Length.ToString(CultureInfo.InvariantCulture)]);
            values.AddRange(artifact.ExtractExcludes);
        }
        return MmcFingerprint.Of(values);
    }
    public static string ComputeAssetSourceSetFingerprint(string indexArtifactKey, string indexSha256, long sizeBytes,
        string producerVersion = "bounded-assets-v1") {
        MmcDefinitionCodec.CheckHash(indexArtifactKey); MmcDefinitionCodec.CheckHash(indexSha256);
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        return MmcFingerprint.Of(["mmc-asset-input-set-v1", producerVersion, indexArtifactKey, indexSha256, sizeBytes.ToString(CultureInfo.InvariantCulture)]);
    }
    public static string ComputeDerivedContextFingerprint(string runtimePlanFingerprint, string sourceSetFingerprint, string producerVersion) {
        MmcDefinitionCodec.CheckHash(runtimePlanFingerprint); MmcDefinitionCodec.CheckHash(sourceSetFingerprint);
        if (string.IsNullOrEmpty(producerVersion) || producerVersion.Length > 128 || producerVersion.Any(char.IsControl)) throw new ArgumentException("Invalid producer identity.", nameof(producerVersion));
        return MmcFingerprint.Of(["mmc-derived-context-v1", producerVersion, runtimePlanFingerprint, sourceSetFingerprint]);
    }
    public static string ComputeAssetObjectKey(string indexArtifactKey, string indexSha256, string objectSha1, long sizeBytes) {
        MmcDefinitionCodec.CheckHash(indexArtifactKey); MmcDefinitionCodec.CheckHash(indexSha256); MmcDefinitionCodec.CheckHash(objectSha1, 40);
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        return AssetKeyPrefix + MmcFingerprint.Of(["mmc-asset-object-v1", indexArtifactKey.ToLowerInvariant(), indexSha256.ToLowerInvariant(), objectSha1.ToLowerInvariant(), sizeBytes.ToString(CultureInfo.InvariantCulture)]);
    }
    internal static bool IsAssetObjectKey(string key) => key is not null && key.StartsWith(AssetKeyPrefix, StringComparison.Ordinal) &&
        key.Length == AssetKeyPrefix.Length + 64 && key[AssetKeyPrefix.Length..].All(Uri.IsHexDigit);
}
