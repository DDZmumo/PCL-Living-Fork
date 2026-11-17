namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    /// <summary>Explicitly discard only the exclusive nonce-owned candidate, after its actual producers drain.</summary>
    public Task<MmcInstancePackageUpdateResult> DiscardAsync(string operationDirectory, Guid operationId, CancellationToken token = default) => Task.Run(() => {
        try {
            token.ThrowIfCancellationRequested(); var job = MmcPackageUpdateJob.Open(operationDirectory, operationId);
            using var use = job.Use();
            InstanceConfigProviders.FlushAndDisposeWithin(job.Root); // Drain before quiescence/admission, never recursively save under those gates.
            Guid identity = job.Owner.InstanceId;
            var paths = MmcInstancePaths.Resolve(job.CandidateRoot);
            if (File.Exists(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json"))) {
                try { identity = MmcSavedSourceStore.ReadPrimary(paths).Definition.InstanceId; }
                catch (Exception error) when (Expected(error)) { /* Deleting owned unpublished data does not infer live authority from damaged metadata. */ }
            }
            using var quiet = MmcConfigurationCoordinator.Quiesce(job.CandidateRoot, token);
            using var gate = MmcAdmissionGate.Acquire(job.CandidateRoot, identity, TimeSpan.Zero, token);
            MmcSessions.AssertNoActive(gate); token.ThrowIfCancellationRequested();
            job.DeleteOwned(); return new MmcInstancePackageUpdateResult(MmcInstanceBackendStatus.Succeeded);
        } catch (OperationCanceledException error) { return new MmcInstancePackageUpdateResult(MmcInstanceBackendStatus.Cancelled, Failure: error.Message); }
        catch (Exception error) when (Expected(error)) { return new MmcInstancePackageUpdateResult(MmcInstanceBackendStatus.NeedsRecovery, Failure: error.Message); }
    }, CancellationToken.None);
}
