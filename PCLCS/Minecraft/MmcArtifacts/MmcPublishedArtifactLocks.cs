using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>Published source inputs include asset objects derived from a hash-bound raw asset index.</summary>
public static class MmcPublishedArtifactLocks {
    public static ImmutableArray<MmcArtifactLock> Create(MmcInstanceDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        if (!MmcDefinitionCodec.Validate(definition).IsEmpty) throw new IOException("Published artifact authority is invalid.");
        Dictionary<string, MmcArtifactLock> inputs = new(StringComparer.Ordinal);
        void Add(MmcArtifactLock value) {
            if (inputs.TryGetValue(value.ArtifactKey, out var old) && old != value)
                throw new IOException("Published source input has conflicting adopted locks.");
            inputs[value.ArtifactKey] = value;
        }
        foreach (var value in definition.ArtifactLocks)
            Add(new(value.ArtifactKey, value.SourceRevision, value.AdoptedSha256, value.SizeBytes, value.RecoveryObjectRelativePath));
        foreach (var value in definition.PackageFileLocks)
            Add(new(value.ArtifactKey, value.SourceRevision, value.AdoptedSha256, value.SizeBytes, value.RecoveryObjectRelativePath));
        // Extracted native/virtual/mapped files are outputs, not new download authorities.
        foreach (var value in definition.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetObject)) {
            if (value.ArtifactKey is null || value.RecoveryObjectRelativePath is null)
                throw new IOException("Published asset-object source input is missing its adopted identity.");
            Add(new(value.ArtifactKey, value.ParentSha256, value.Sha256, value.SizeBytes, value.RecoveryObjectRelativePath));
        }
        return inputs.Values.ToImmutableArray();
    }
}
