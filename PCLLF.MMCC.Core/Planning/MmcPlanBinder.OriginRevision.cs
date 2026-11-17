// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
namespace PCLLF.MMCC.Core;

public sealed record MmcOriginRevisionBindOptions(Guid ExpectedInstanceId, long ExpectedGeneration, string ExpectedPrimarySha256,
    string OutputJsonSha256, MmcRetainedRevisionFact Retained);

public static partial class MmcPlanBinder {
    /// <summary>Only attaches measured origin evidence. Existing components, locks, sources, and all player content remain unchanged.</summary>
    public static MmcBindingResult BindOriginRevision(MmcInstanceDefinition current, MmcCompiledProfile plan,
        ReadOnlyMemory<byte> evidenceBytes, MmcPlatformProjectIdentity identity, MmcOriginRevisionBindOptions options,
        MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(options);
        var errors = MmcDefinitionCodec.Validate(current, MmcDefinitionValidationMode.Published, limits);
        if (!errors.IsEmpty) return new(null, null, errors);
        try {
            MmcDefinitionCodec.CheckHash(options.ExpectedPrimarySha256); MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            var retained = options.Retained;
            if (current.InstanceId != options.ExpectedInstanceId || current.Generation != options.ExpectedGeneration || current.Generation == long.MaxValue ||
                retained is null || retained.PointId == Guid.Empty || retained.InstanceId != current.InstanceId || retained.Generation != current.Generation ||
                retained.PrimarySha256 != options.ExpectedPrimarySha256 || retained.BoundFingerprint != current.InstalledProjection!.BoundFingerprint)
                Fail("MMC_ORIGIN_REVISION_AUTHORITY", "Origin revision requires the selected current publication and its actual undo point.");
            MmcDefinitionCodec.CheckHash(retained!.ManifestSha256);
            if (!MmcProfileFingerprints.Matches(plan) || plan.SourceFingerprint != current.SourceBundle.ResolutionFingerprint ||
                plan.ProfileFingerprint != current.InstalledProjection!.ProfileFingerprint || plan.RuntimePlanFingerprint != current.InstalledProjection.RuntimePlanFingerprint ||
                current.InstalledProjection.RuntimeEnvironment is { } environment && environment != plan.Environment)
                Fail("MMC_ORIGIN_REVISION_PLAN", "Origin revision cannot change the published component/runtime plan.");
            var origin = current.Origin with { PlatformProjectIdentity = identity, PlatformEvidenceReference = MmcPlatformOriginCodec.Reference(evidenceBytes.Span) };
            _ = MmcPlatformOriginCodec.Read(evidenceBytes, origin);
            if (origin == current.Origin) Fail("MMC_ORIGIN_REVISION_UNCHANGED", "The selected origin evidence is already adopted.");
            long next = checked(current.Generation + 1);
            var result = current with { Generation = next, Origin = origin, InstalledProjection = current.InstalledProjection with {
                OutputJsonSha256 = options.OutputJsonSha256, ManifestGeneration = next, BoundFingerprint = new string('0', 64)
            } };
            string fingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(result);
            result = result with { InstalledProjection = result.InstalledProjection! with { BoundFingerprint = fingerprint } };
            errors = MmcDefinitionCodec.Validate(result, MmcDefinitionValidationMode.Published, limits);
            var locks = current.ArtifactLocks.ToDictionary(item => item.ArtifactKey, StringComparer.Ordinal);
            var active = ImmutableArray.CreateBuilder<MmcArtifactLockRecord>();
            foreach (string key in ActiveArtifacts(plan).Keys) {
                if (!locks.TryGetValue(key, out var item)) Fail("MMC_ORIGIN_REVISION_PLAN", "A current published plan input has no adopted lock.");
                active.Add(item!);
            }
            return errors.IsEmpty ? new(result, new(plan, next, fingerprint, active.ToImmutable()), []) : new(null, null, errors);
        } catch (MmcInputException error) { return new(null, null, [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]); }
        catch (ArgumentException error) { return new(null, null, [new("MMC_ORIGIN_EVIDENCE", MmcDiagnosticSeverity.Error, "origin", error.Message)]); }
    }
}
