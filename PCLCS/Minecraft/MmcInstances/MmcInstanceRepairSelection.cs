using PCLLF.MMCC.Core;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed record MmcInstanceRepairSelection(string VersionRoot, Guid InstanceId, long Generation, string PrimarySha256, bool MissingProjection = false) {
    public string? DivergedProjectionSha256 { get; init; }
}

/// <summary>Read-only maintenance selection. Never creates settings providers or guesses a runtime from the MC version.</summary>
public static partial class MmcInstanceRepairSelections {
    public static MmcInstanceRepairSelection Capture(string versionRoot, Guid? expectedInstanceId = null, bool requireMissingProjection = false) =>
        CaptureCore(versionRoot, expectedInstanceId, requireMissingProjection, false);

    private static MmcInstanceRepairSelection CaptureCore(string versionRoot, Guid? expectedInstanceId, bool requireMissingProjection, bool allowDiverged) {
        var observed = MmcInstanceInspector.Read(versionRoot);
        if (observed.Definition is null || observed.Paths is null || observed.PrimarySha256 is null ||
            expectedInstanceId is { } expected && observed.Definition.InstanceId != expected)
            throw new IOException(observed.Error ?? "MMC repair requires a valid, unchanged instance definition.");
        bool missing = !observed.IsConsistent;
        if (requireMissingProjection && !missing) throw new IOException("The formerly unresolved instance changed; reload its page before repairing.");
        if (missing && allowDiverged && !requireMissingProjection && observed.Management.ObservationComplete) {
            var raw = MmcManagementProbe.Capture(observed.Paths.InstanceRoot);
            var agreement = MmcManagementReader.Evaluate(raw);
            if (agreement.State == MmcManagementState.ManagedMmc && agreement.Envelope?.InstanceId == observed.Definition.InstanceId &&
                agreement.Envelope.Generation == observed.Definition.Generation) {
                byte[] bytes = ProjectionBytes(observed.Paths);
                string hash = MmcDurableFiles.Hash(bytes);
                if (hash == observed.Definition.InstalledProjection!.OutputJsonSha256)
                    throw new IOException(observed.Error ?? "The projection has not diverged; inspect the remaining metadata error.");
                return new(observed.Paths.InstanceRoot, observed.Definition.InstanceId, observed.Definition.Generation, observed.PrimarySha256) {
                    DivergedProjectionSha256 = hash
                };
            }
        }
        if (missing) {
            // A missing generated JSON can be reconstructed only when the real primary and
            // the remaining config hint agree. An existing/diverged JSON is never replaced
            // implicitly, and malformed/foreign/missing identity evidence stays guarded.
            var raw = MmcManagementProbe.Capture(observed.Paths.InstanceRoot);
            string projection = observed.Paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(observed.Paths.InstanceRoot) + ".json");
            var agreement = MmcManagementReader.Evaluate(raw with { VersionHint = raw.ConfigHint });
            if (observed.Management.State != MmcManagementState.ManagedMmcRepairRequired || !raw.ObservationComplete || raw.HasPendingTransaction ||
                raw.VersionHint.Exists || File.Exists(projection) || Directory.Exists(projection) || agreement.State != MmcManagementState.ManagedMmc ||
                agreement.Envelope?.InstanceId != observed.Definition.InstanceId || agreement.Envelope.Generation != observed.Definition.Generation)
                throw new IOException(observed.Error ?? "Inspect conflicting MMC metadata before repairing this instance.");
        }
        return new(observed.Paths.InstanceRoot, observed.Definition.InstanceId, observed.Definition.Generation, observed.PrimarySha256, missing);
    }

    internal static void Revalidate(MmcAdmissionLease lease, MmcInstanceRepairSelection selection) {
        lease.AssertHeld();
        if (lease.InstanceId != selection.InstanceId || !string.Equals(lease.VersionRoot, MmcCanonicalPaths.ForAdmission(selection.VersionRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Repair selection belongs to another admission lease.");
        var current = CaptureCore(lease.VersionRoot, selection.InstanceId, false, selection.DivergedProjectionSha256 is not null);
        if (current.Generation != selection.Generation || current.PrimarySha256 != selection.PrimarySha256 || current.MissingProjection != selection.MissingProjection ||
            current.DivergedProjectionSha256 != selection.DivergedProjectionSha256)
            throw new IOException("The selected MMC identity or remaining management hints changed before repair.");
    }

    internal static void AssertConfiguration(MmcInstanceDefinition definition, JObject config) {
        JToken? hint = (config[MmcManagementReader.ConfigManifestPropertyName] as JObject)?[MmcManagementReader.HintPropertyName];
        var document = new MmcManagementDocument(hint is not null, hint?.ToString(Newtonsoft.Json.Formatting.None));
        var agreement = MmcManagementReader.Evaluate(new(new(true, MmcDefinitionCodec.Serialize(definition)), document, document));
        if (agreement.State != MmcManagementState.ManagedMmc || agreement.Envelope?.InstanceId != definition.InstanceId || agreement.Envelope.Generation != definition.Generation)
            throw new IOException("The captured configuration no longer belongs to the selected MMC generation.");
    }

    public static Task<MmcRuntimeEnvironment> ResolvePublishedEnvironmentAsync(MmcInstanceRepairSelection selection, CancellationToken cancellationToken = default) =>
        Task.Run(() => {
            ArgumentNullException.ThrowIfNull(selection); cancellationToken.ThrowIfCancellationRequested();
            using var lease = MmcAdmissionGate.Acquire(selection.VersionRoot, selection.InstanceId, TimeSpan.Zero, cancellationToken);
            MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
            Revalidate(lease, selection);
            var paths = MmcInstancePaths.Resolve(selection.VersionRoot);
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            if (primary.Definition.InstanceId != selection.InstanceId || primary.Definition.Generation != selection.Generation || primary.Sha256 != selection.PrimarySha256)
                throw new IOException("The selected MMC instance changed before repair preparation.");
            var saved = MmcSavedSourceStore.LoadAsync(paths, primary.Definition, cancellationToken).GetAwaiter().GetResult();
            return MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, primary.Definition, []).Environment;
        }, CancellationToken.None);
}
