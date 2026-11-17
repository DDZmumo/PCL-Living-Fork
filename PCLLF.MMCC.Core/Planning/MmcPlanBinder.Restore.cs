// SPDX-License-Identifier: MPL-2.0
namespace PCLLF.MMCC.Core;

/// <summary>The host separately verifies the selected history point and the current publication.</summary>
public sealed record MmcHistoryRestoreBindOptions(Guid ExpectedInstanceId, long ExpectedCurrentGeneration,
    string ExpectedCurrentBoundFingerprint, string ExpectedPointBoundFingerprint, string OutputJsonSha256) {
    public MmcDerivationCompletion? NativeDerivation { get; init; }
}

public static partial class MmcPlanBinder {
    /// <summary>
    /// Restores one immutable published environment into a new monotonic generation of the same
    /// instance. The point is validated as its own authority; a host-created draft cannot assert
    /// replacement locks. The host retains the displaced environment in a complete undo point.
    /// </summary>
    public static MmcBindingResult BindRestore(MmcInstanceDefinition current, MmcInstanceDefinition selected,
        MmcCompiledProfile plan, IReadOnlyList<MmcMaterializedArtifactFact> facts, MmcHistoryRestoreBindOptions options,
        IReadOnlyList<MmcDerivedOutputRecord>? derivedOutputs = null, IReadOnlyList<MmcAssetIndexInput>? assetIndexes = null,
        MmcReadLimits? limits = null, MmcPackageBaselineInput? packageBaseline = null) {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(options);
        limits ??= new();
        try {
            foreach (var value in new[] { current, selected }) {
                var errors = MmcDefinitionCodec.Validate(value, MmcDefinitionValidationMode.Published, limits);
                if (!errors.IsEmpty) return new(null, null, errors);
            }
            MmcDefinitionCodec.CheckHash(options.ExpectedCurrentBoundFingerprint);
            MmcDefinitionCodec.CheckHash(options.ExpectedPointBoundFingerprint); MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            if (options.ExpectedInstanceId == Guid.Empty || current.InstanceId != options.ExpectedInstanceId || selected.InstanceId != current.InstanceId ||
                current.Generation != options.ExpectedCurrentGeneration || current.InstalledProjection!.BoundFingerprint != options.ExpectedCurrentBoundFingerprint ||
                selected.InstalledProjection!.BoundFingerprint != options.ExpectedPointBoundFingerprint || selected.Generation > current.Generation || current.Generation == long.MaxValue)
                Fail("MMC_HISTORY_AUTHORITY", "Current publication or selected history identity/binding differs from the fixed selection.");
            if (current.Layout != selected.Layout)
                Fail("MMC_HISTORY_LAYOUT", "History restoration cannot change the roles of current player game trees.");
            if (selected.InstalledProjection.RuntimeEnvironment is { } environment && environment != plan.Environment || plan.Environment.JavaMajor <= 0)
                Fail("MMC_HISTORY_ENVIRONMENT", "History needs its exact published runtime environment.");
            var verified = Bind(selected, plan, facts, new(selected.Generation, selected.Generation, MmcBindingIntent.RepairExisting,
                selected.InstalledProjection.OutputJsonSha256, selected.Generation) { NativeDerivation = options.NativeDerivation },
                derivedOutputs: derivedOutputs, assetIndexes: assetIndexes, limits: limits, publishedBaseline: selected, packageBaseline: packageBaseline);
            if (verified.HasErrors) return verified;
            if (verified.Plan is null || verified.Plan.BoundFingerprint != selected.InstalledProjection.BoundFingerprint)
                Fail("MMC_HISTORY_POINT", "Measured inputs do not preserve the selected historical authority.");
            long generation = checked(current.Generation + 1);
            var restored = selected with {
                Generation = generation,
                InstalledProjection = selected.InstalledProjection with {
                    OutputJsonSha256 = options.OutputJsonSha256, ManifestGeneration = generation,
                    BoundFingerprint = new string('0', 64), RuntimeEnvironment = plan.Environment
                }
            };
            string fingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(restored);
            restored = restored with { InstalledProjection = restored.InstalledProjection with { BoundFingerprint = fingerprint } };
            var diagnostics = MmcDefinitionCodec.Validate(restored, MmcDefinitionValidationMode.Published, limits);
            return diagnostics.IsEmpty ? new(restored, new(plan, generation, fingerprint, verified.Plan.ActiveArtifactLocks), []) : new(null, null, diagnostics);
        } catch (MmcInputException error) {
            return new(null, null, [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]);
        }
    }
}
