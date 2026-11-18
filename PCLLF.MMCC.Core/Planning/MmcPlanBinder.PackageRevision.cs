// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json.Serialization;
namespace PCLLF.MMCC.Core;

/// <summary>The host fixes the chosen archive digest and has already captured a complete undo point.</summary>
public sealed record MmcPackageRevisionBindOptions(Guid ExpectedInstanceId, long ExpectedGeneration,
    string ExpectedPrimarySha256, string ExpectedPackageSha256, string OutputJsonSha256, MmcRetainedRevisionFact Retained) {
    public MmcDerivationCompletion? NativeDerivation { get; init; }
    public ReadOnlyMemory<byte>? PackageOwnershipContent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<string> RequiredUndoPaths { get; init; }
}

public static partial class MmcPlanBinder {
    /// <summary>Publishes a newly measured author package without treating current player files as its baseline.</summary>
    public static MmcBindingResult BindPackageRevision(MmcInstanceDefinition current, MmcInstanceDefinition source,
        MmcCompiledProfile plan, IReadOnlyList<MmcMaterializedArtifactFact> facts, MmcPackageRevisionBindOptions options,
        MmcPackageBaselineInput packageBaseline, IReadOnlyList<MmcDerivedOutputRecord>? derivedOutputs = null,
        IReadOnlyList<MmcAssetIndexInput>? assetIndexes = null, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(packageBaseline);
        limits ??= new();
        try {
            var errors = MmcDefinitionCodec.Validate(current, MmcDefinitionValidationMode.Published, limits);
            if (!errors.IsEmpty) return new(null, null, errors);
            errors = MmcDefinitionCodec.Validate(source, MmcDefinitionValidationMode.Draft, limits);
            if (!errors.IsEmpty) return new(null, null, errors);
            MmcDefinitionCodec.CheckHash(options.ExpectedPrimarySha256); MmcDefinitionCodec.CheckHash(options.ExpectedPackageSha256);
            MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            var retained = options.Retained ?? throw new ArgumentNullException(nameof(options.Retained));
            MmcDefinitionCodec.CheckHash(retained.ManifestSha256); MmcDefinitionCodec.CheckHash(retained.PrimarySha256);
            if (options.ExpectedInstanceId == Guid.Empty || current.InstanceId != options.ExpectedInstanceId ||
                current.Generation != options.ExpectedGeneration || current.Generation == long.MaxValue ||
                retained.PointId == Guid.Empty || retained.InstanceId != current.InstanceId || retained.Generation != current.Generation ||
                retained.PrimarySha256 != options.ExpectedPrimarySha256 || retained.BoundFingerprint != current.InstalledProjection!.BoundFingerprint)
                Fail("MMC_PACKAGE_REVISION_AUTHORITY", "The selected current publication and its complete undo authority must match.");
            if (retained.CoverageVersion is not (null or 3 or 4 or 5) || (retained.CoverageVersion is null or 3) && !retained.AdditionalEnvironmentPaths.IsDefaultOrEmpty ||
                retained.CoverageVersion == 4 && retained.AdditionalEnvironmentPaths.IsDefaultOrEmpty ||
                retained.CoverageVersion == 5 && retained.FullSafePolicyVersion != 1 || retained.CoverageVersion != 5 && retained.FullSafePolicyVersion is not null)
                Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "Unsupported history semantics cannot authorize package file mutations.");
            if (!options.RequiredUndoPaths.IsDefaultOrEmpty) {
                if (retained.CoverageVersion != 5 || retained.FullSafePolicyVersion != 1 || retained.AdditionalEnvironmentPaths.IsDefaultOrEmpty)
                    Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "Package file mutations require complete safe-inventory undo coverage, not legacy fixed or single-operation exact coverage.");
                if (options.RequiredUndoPaths.Length > limits.MaxArchiveEntries || retained.AdditionalEnvironmentPaths.Length > limits.MaxArchiveEntries)
                    Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "Exact undo path budget exceeded.");
                var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!retained.AdditionalEnvironmentPaths.SequenceEqual(retained.AdditionalEnvironmentPaths.Order(StringComparer.Ordinal)))
                    Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "Retained exact undo paths must use canonical ordering.");
                foreach (string path in retained.AdditionalEnvironmentPaths) {
                    MmcArtifactRules.Relative(path, "undo");
                    if (path.Contains('\\') || !path.StartsWith(current.Layout.GameRootLeaf + "/", StringComparison.Ordinal) || !covered.Add(path))
                        Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "Retained undo paths must identify unique exact active-game files.");
                }
                foreach (string path in options.RequiredUndoPaths) {
                    MmcArtifactRules.Relative(path, "undo");
                    if (path.Contains('\\') || !covered.Contains(path)) Fail("MMC_PACKAGE_REVISION_UNDO_COVERAGE", "A real package write/delete target is absent from the retained exact-path coverage.");
                }
            }
            if (source.InstanceId != current.InstanceId || source.Generation != current.Generation || source.ParentInstanceId != current.ParentInstanceId ||
                source.SchemaVersion != current.SchemaVersion || source.ManagementKind != current.ManagementKind || source.FormatProfile != current.FormatProfile ||
                source.InstalledProjection is not null || !source.ArtifactInventory.IsEmpty || !source.ArtifactLocks.IsEmpty || !source.DerivedOutputs.IsEmpty ||
                !source.PackageFileInventory.IsEmpty || !source.PackageFileLocks.IsEmpty || source.Origin.PackageSha256 != options.ExpectedPackageSha256 ||
                source.Origin.PackageSha256 == current.Origin.PackageSha256 || source.AuthorFileBaseline is null)
                Fail("MMC_PACKAGE_REVISION_SOURCE", "A new measured archive source is required without manufactured execution or prior-lock authority.");
            if (source.Layout != current.Layout)
                Fail("MMC_PACKAGE_REVISION_LAYOUT", "Changing active/inactive player tree roles requires an explicit layout migration.");
            var baseline = MmcPackageFileBaselineCodec.Read(packageBaseline, source.AuthorFileBaseline!, options.ExpectedPackageSha256, source.SourceBundle.RelativeRoot);
            if (baseline.HasErrors || baseline.Baseline is null) return new(null, null, baseline.Diagnostics);
            if (baseline.Baseline.GameRootLeaf != source.Layout.GameRootLeaf)
                Fail("MMC_PACKAGE_REVISION_LAYOUT", "The new author baseline does not justify the selected game-tree role.");
            if (source.PackageOwnershipReference is { } ownership) {
                if (options.PackageOwnershipContent is not { } content) Fail("MMC_PACKAGE_REVISION_OWNERSHIP", "A new ownership reference requires its measured immutable bytes.");
                var ownershipErrors = MmcPackageOwnershipCodec.Validate(options.PackageOwnershipContent!.Value, ownership, source.Origin.PackageSha256);
                if (!ownershipErrors.IsEmpty) return new(null, null, ownershipErrors);
            } else if (options.PackageOwnershipContent is not null) Fail("MMC_PACKAGE_REVISION_OWNERSHIP", "Ownership content is not referenced by the new package definition.");
            var previous = current.ArtifactLocks.Select(value => (value.ArtifactKey, value.SourceRevision, value.AdoptedSha256, value.SizeBytes))
                .Concat(current.PackageFileLocks.Select(value => (value.ArtifactKey, value.SourceRevision, value.AdoptedSha256, value.SizeBytes)))
                .ToDictionary(value => value.ArtifactKey, StringComparer.Ordinal);
            foreach (var fact in facts) if (previous.TryGetValue(fact.ArtifactKey, out var old) &&
                (fact.SourceRevision != old.SourceRevision || fact.ObservedSha256 != old.AdoptedSha256 || fact.SizeBytes != old.SizeBytes))
                Fail("MMC_PACKAGE_REVISION_REBIND", "An unchanged adopted identity cannot acquire different bytes during a package update.");
            var previousObjects = current.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetObject)
                .GroupBy(value => value.ArtifactKey!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var fact in facts) if (previousObjects.TryGetValue(fact.ArtifactKey, out var old) &&
                (fact.SourceRevision != old.ParentSha256 || fact.ObservedSha256 != old.Sha256 || fact.SizeBytes != old.SizeBytes))
                Fail("MMC_PACKAGE_REVISION_REBIND", "An unchanged adopted asset object cannot acquire different bytes during a package update.");
            var measured = Bind(source with { Generation = 1 }, plan, facts,
                new(1, 1, MmcBindingIntent.InitialImport, options.OutputJsonSha256, 1) { NativeDerivation = options.NativeDerivation },
                derivedOutputs: derivedOutputs, assetIndexes: assetIndexes, limits: limits, packageBaseline: packageBaseline);
            if (measured.HasErrors || measured.Definition is null || measured.Plan is null) return measured;
            long generation = checked(current.Generation + 1);
            var result = measured.Definition with { Generation = generation, InstalledProjection = measured.Definition.InstalledProjection! with {
                ManifestGeneration = generation, BoundFingerprint = new string('0', 64)
            } };
            string fingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(result);
            result = result with { InstalledProjection = result.InstalledProjection! with { BoundFingerprint = fingerprint } };
            errors = MmcDefinitionCodec.Validate(result, MmcDefinitionValidationMode.Published, limits);
            return errors.IsEmpty ? new(result, new(plan, generation, fingerprint, measured.Plan.ActiveArtifactLocks), []) : new(null, null, errors);
        } catch (MmcInputException error) {
            return new(null, null, [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]);
        }
    }
}
