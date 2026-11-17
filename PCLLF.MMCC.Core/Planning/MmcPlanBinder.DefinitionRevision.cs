// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json.Serialization;
namespace PCLLF.MMCC.Core;

/// <summary>Host proof of an actually captured complete environment, not a request to create one.</summary>
public sealed record MmcRetainedRevisionFact(Guid PointId, string ManifestSha256, Guid InstanceId, long Generation,
    string PrimarySha256, string BoundFingerprint) {
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CoverageVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FullSafePolicyVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<string> AdditionalEnvironmentPaths { get; init; }
}
public sealed record MmcDefinitionRevisionBindOptions(string ExpectedPrimarySha256, string OutputJsonSha256,
    MmcRetainedRevisionFact Retained) {
    public MmcDerivationCompletion? NativeDerivation { get; init; }
}

public static partial class MmcPlanBinder {
    /// <summary>Explicit definition revision rebuilds the current adopted set. Replaced authority
    /// remains in the separately verified complete history point; repair cannot invoke this policy.</summary>
    public static MmcBindingResult BindDefinitionRevision(MmcInstanceDefinition current, MmcInstanceDefinition source,
        MmcCompiledProfile plan, IReadOnlyList<MmcMaterializedArtifactFact> facts, MmcDefinitionRevisionBindOptions options,
        IReadOnlyList<MmcDerivedOutputRecord>? derivedOutputs = null, IReadOnlyList<MmcAssetIndexInput>? assetIndexes = null,
        MmcReadLimits? limits = null, MmcPackageBaselineInput? packageBaseline = null) {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(facts);
        limits ??= new();
        try {
            var validation = MmcDefinitionCodec.Validate(current, MmcDefinitionValidationMode.Published, limits);
            if (!validation.IsEmpty) return new(null, null, validation);
            validation = MmcDefinitionCodec.Validate(source, MmcDefinitionValidationMode.Draft, limits);
            if (!validation.IsEmpty) return new(null, null, validation);
            MmcDefinitionCodec.CheckHash(options.ExpectedPrimarySha256); MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            var retained = options.Retained ?? throw new ArgumentNullException(nameof(options.Retained));
            MmcDefinitionCodec.CheckHash(retained.ManifestSha256); MmcDefinitionCodec.CheckHash(retained.PrimarySha256);
            if (retained.PointId == Guid.Empty || retained.InstanceId != current.InstanceId || retained.Generation != current.Generation ||
                retained.PrimarySha256 != options.ExpectedPrimarySha256 || retained.BoundFingerprint != current.InstalledProjection!.BoundFingerprint)
                Fail("MMC_REVISION_HISTORY", "A complete fixed undo environment for the current publication is required.");
            if (source.InstanceId != current.InstanceId || source.Generation != current.Generation || source.Origin != current.Origin ||
                source.Layout != current.Layout || source.ParentInstanceId != current.ParentInstanceId || source.FormatProfile != current.FormatProfile ||
                source.SourceBundle.PackageLocalScopeSha256 != current.SourceBundle.PackageLocalScopeSha256 ||
                source.PackageOwnershipReference != current.PackageOwnershipReference ||
                source.SchemaVersion != current.SchemaVersion || source.ManagementKind != current.ManagementKind || current.Generation == long.MaxValue || source.InstalledProjection is not null ||
                !source.ArtifactInventory.IsEmpty || !source.ArtifactLocks.IsEmpty || !source.DerivedOutputs.IsEmpty ||
                !source.PackageFileInventory.IsEmpty || !source.PackageFileLocks.IsEmpty || SourceEnvelopeFingerprint(source) == SourceEnvelopeFingerprint(current))
                Fail("MMC_REVISION_SOURCE", "Revision accepts changed component sources only, without host-manufactured prior locks or identity/layout changes.");
            if (source.AuthorFileBaseline is not { } baseline || current.AuthorFileBaseline is not { } oldBaseline ||
                baseline.ContentSha256 != oldBaseline.ContentSha256 || baseline.EntryCount != oldBaseline.EntryCount || baseline.FormatVersion != oldBaseline.FormatVersion ||
                !OriginalRecords(current).SequenceEqual(OriginalRecords(source)))
                Fail("MMC_REVISION_SOURCE", "Component revisions must retain the measured author baseline and original author metadata.");
            var prior = current.ArtifactLocks.ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
            foreach (var fact in facts) if (prior.TryGetValue(fact.ArtifactKey, out var old) &&
                (fact.SourceRevision != old.SourceRevision || fact.ObservedSha256 != old.AdoptedSha256 || fact.SizeBytes != old.SizeBytes))
                Fail("MMC_REVISION_REBIND", "An unchanged adopted artifact identity cannot accept different bytes.");
            var priorAssets = current.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetObject)
                .GroupBy(value => value.ArtifactKey!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var fact in facts) {
                if (priorAssets.TryGetValue(fact.ArtifactKey, out var oldAsset) && (oldAsset.ParentSha256 != fact.SourceRevision || oldAsset.Sha256 != fact.ObservedSha256 || oldAsset.SizeBytes != fact.SizeBytes))
                    Fail("MMC_REVISION_REBIND", "An unchanged adopted asset-object identity cannot accept different bytes.");
            }
            // Initial binding is an internal validator of the fresh source, not host authority to
            // reset generations. Only this method can derive the resulting N+1 publication.
            var draft = source with { Generation = 1 };
            var measured = Bind(draft, plan, facts, new(1, 1, MmcBindingIntent.InitialImport, options.OutputJsonSha256, 1) {
                NativeDerivation = options.NativeDerivation
            }, derivedOutputs: derivedOutputs, assetIndexes: assetIndexes, limits: limits, packageBaseline: packageBaseline);
            if (measured.HasErrors || measured.Definition is null || measured.Plan is null) return measured;
            long next = checked(current.Generation + 1);
            var definition = measured.Definition with { Generation = next,
                InstalledProjection = measured.Definition.InstalledProjection! with { ManifestGeneration = next, BoundFingerprint = new string('0', 64) } };
            string fingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(definition);
            definition = definition with { InstalledProjection = definition.InstalledProjection! with { BoundFingerprint = fingerprint } };
            validation = MmcDefinitionCodec.Validate(definition, MmcDefinitionValidationMode.Published, limits);
            return validation.IsEmpty ? new(definition, new(plan, next, fingerprint, measured.Plan.ActiveArtifactLocks), []) : new(null, null, validation);
        } catch (MmcInputException error) { return new(null, null, [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]); }
    }
    private static IEnumerable<(MmcSourceRecordKind, string, string, string?, string?, MmcComponentSourceKind?, string?)> OriginalRecords(MmcInstanceDefinition value) =>
        value.SourceBundle.MetadataRecords.Where(item => item.Kind is MmcSourceRecordKind.Manifest or MmcSourceRecordKind.Configuration or MmcSourceRecordKind.PackageMetadata || item.ComponentSourceKind == MmcComponentSourceKind.PackageLocal)
            .OrderBy(item => item.Kind).ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => (item.Kind, item.RelativePath, item.ContentSha256, item.Uid, item.Version, item.ComponentSourceKind, item.SourceIdentity));
}
