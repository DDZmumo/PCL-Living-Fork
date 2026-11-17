// SPDX-License-Identifier: MPL-2.0
namespace PCLLF.MMCC.Core;

public static partial class MmcPlanBinder {
    /// <summary>
    /// Derives a new identity from an unchanged selected published source. Active target-relative
    /// paths, metadata, overrides, all historical locks and original references remain unchanged.
    /// Facts describe the independently measured destination, not assertions copied from the source.
    /// Host copy completion, source/destination admission and publication are separate responsibilities.
    /// </summary>
    public static MmcBindingResult BindClone(MmcInstanceDefinition source, MmcCompiledProfile plan,
        IReadOnlyList<MmcMaterializedArtifactFact> facts, MmcCloneBindOptions options,
        IReadOnlyList<MmcDerivedOutputRecord>? derivedOutputs = null, IReadOnlyList<MmcAssetIndexInput>? assetIndexes = null,
        MmcReadLimits? limits = null, MmcPackageBaselineInput? packageBaseline = null) {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(options);
        limits ??= new();
        try {
            var errors = MmcDefinitionCodec.Validate(source,MmcDefinitionValidationMode.Published,limits);
            if (!errors.IsEmpty) return new(null,null,errors);
            var published=source.InstalledProjection!;
            MmcDefinitionCodec.CheckHash(options.ExpectedSourceBoundFingerprint); MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            if (options.ExpectedSourceInstanceId!=source.InstanceId || options.ExpectedSourceGeneration!=source.Generation ||
                !MmcDefinitionCodec.SameHash(options.ExpectedSourceBoundFingerprint,published.BoundFingerprint))
                Fail("MMC_CLONE_SOURCE","The selected published source identity, generation or binding changed.");
            if (options.NewInstanceId==Guid.Empty || options.NewInstanceId==source.InstanceId)
                Fail("MMC_CLONE_IDENTITY","Clone identity must be nonempty and different from its parent source.");
            if (!MmcProfileFingerprints.Matches(plan) || plan.FormatProfile!=source.FormatProfile ||
                plan.SourceFingerprint!=source.SourceBundle.ResolutionFingerprint || plan.ProfileFingerprint!=published.ProfileFingerprint ||
                plan.RuntimePlanFingerprint!=published.RuntimePlanFingerprint)
                Fail("MMC_CLONE_PLAN","Cloning requires the exact published source and runtime plan, without adopting another variant.");
            if (plan.Environment.JavaMajor<=0 || published.RuntimeEnvironment is { } recorded && recorded!=plan.Environment)
                Fail("MMC_CLONE_ENVIRONMENT","The clone needs an exact positive-Java environment matching its published source.");

            // Reuse the strict existing repair validator WITHOUT minting a fake cross-ID baseline
            // or weakening InitialImport. A source same-generation rebind cannot change any
            // adopted digest, owned path, derived authority or original-reference fingerprint.
            var verified=Bind(source,plan,facts,new(source.Generation,source.Generation,MmcBindingIntent.RepairExisting,
                published.OutputJsonSha256,source.Generation){NativeDerivation=options.NativeDerivation},
                derivedOutputs:derivedOutputs,assetIndexes:assetIndexes,limits:limits,publishedBaseline:source,packageBaseline:packageBaseline);
            if (verified.HasErrors) return verified;
            if (verified.Plan is null || verified.Definition is null || verified.Plan.BoundFingerprint!=published.BoundFingerprint)
                Fail("MMC_CLONE_SOURCE","Measured clone inputs did not preserve the selected published binding.");
            var measuredDerived=(derivedOutputs ?? []).ToDictionary(d=>d.TargetRelativePath,StringComparer.OrdinalIgnoreCase);
            foreach (var required in source.DerivedOutputs.Where(d=>d.Kind!=MmcDerivedOutputKind.AssetObject && d.RuntimePlanFingerprint==plan.RuntimePlanFingerprint))
                if (!measuredDerived.TryGetValue(required.TargetRelativePath,out var observed) || observed!=required)
                    Fail("MMC_CLONE_DERIVED_MISSING","A current published native/asset output lacks its exact destination observation.");

            // Construct from the SOURCE, not a host-provided target draft. Historical inactive
            // locks and derived descriptions survive even when their views will be rebuilt later.
            var clone=source with {
                InstanceId=options.NewInstanceId,ParentInstanceId=source.InstanceId,Generation=1,
                InstalledProjection=new(plan.ProfileFingerprint,options.OutputJsonSha256,1,plan.RuntimePlanFingerprint,new string('0',64)) {
                    RuntimeEnvironment=plan.Environment
                }
            };
            string fingerprint=MmcDefinitionCodec.ComputeBoundFingerprint(clone);
            clone=clone with { InstalledProjection=clone.InstalledProjection! with { BoundFingerprint=fingerprint } };
            errors=MmcDefinitionCodec.Validate(clone,MmcDefinitionValidationMode.Published,limits);
            return errors.IsEmpty ? new(clone,new(plan,1,fingerprint,verified.Plan.ActiveArtifactLocks),[]) : new(null,null,errors);
        } catch (MmcInputException error) {
            return new(null,null,[new(error.Code,MmcDiagnosticSeverity.Error,error.SourcePath,error.Message)]);
        }
    }
}
