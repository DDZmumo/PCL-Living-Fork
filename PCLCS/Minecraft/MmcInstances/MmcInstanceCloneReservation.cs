using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed record MmcClonePreparedManifest(int SchemaVersion, Guid InstanceId, Guid SourceInstanceId, long SourceGeneration,
    string SourcePrimarySha256, string SourcePreferencesSha256, string SourceRoot, string FinalRoot, MmcInstanceCloneOptions Options, MmcCloneInventory Inventory);

internal sealed class MmcInstanceCloneReservation : IDisposable {
    private readonly MmcOwnedImportReservation owned;
    private readonly string markerHash, ignoreHash;
    private string? snapshotHash;
    internal string TemporaryRoot => owned.TemporaryRoot;
    internal string FinalRoot => owned.FinalRoot;
    internal Guid InstanceId => owned.InstanceId;
    internal MmcInstanceCloneReservation(string sourceRoot, string targetRoot, string packageSha256) {
        ValidateRoots(sourceRoot, targetRoot);
        owned = MmcOwnedImportReservation.Create(targetRoot, Guid.NewGuid(), packageSha256, MmcImportReservationPurpose.Clone);
        try {
            ValidateRoots(sourceRoot, targetRoot, owned.TemporaryRoot);
            var paths = MmcInstancePaths.Resolve(TemporaryRoot);
            markerHash = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json"))!;
            ignoreHash = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, ".pclignore"))!;
        } catch { owned.Dispose(); throw; }
    }
    internal static void ValidateRoots(string sourceRoot, string targetRoot, string? privateRoot = null) {
        string source = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(sourceRoot));
        string target = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(targetRoot));
        string[] roots = privateRoot is null ? [source, target] : [source, target, MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(privateRoot))];
        for (int a = 0; a < roots.Length; a++) for (int b = a + 1; b < roots.Length; b++)
            if (MmcConfigurationCoordinator.Within(roots[a], roots[b]) || MmcConfigurationCoordinator.Within(roots[b], roots[a]))
                throw new IOException("Clone source/private/final roots must be distinct and cannot contain each other.");
        if (MmcConfigurationCoordinator.Within(source, Path.GetDirectoryName(target)!)) throw new IOException("A clone cannot be reserved inside its own source tree.");
    }
    internal void Prepare(MmcClonePreparedManifest manifest, MmcAdmissionLease lease) {
        Verify(lease); if (snapshotHash is not null) throw new IOException("Clone preparation already exists.");
        if (manifest.SchemaVersion != 1 || manifest.InstanceId != InstanceId || manifest.FinalRoot != FinalRoot) throw new IOException("Clone preparation belongs to another reservation.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (bytes.Length > 64 * 1024 * 1024) throw new IOException("Clone snapshot manifest exceeds its metadata budget.");
        MmcDurableFiles.WriteNew(MmcInstancePaths.Resolve(TemporaryRoot).ResolveRelative(MmcPathRole.Instance, "PCL/MmcClonePrepared.json"), bytes);
        snapshotHash = MmcDurableFiles.Hash(bytes);
    }
    internal void Verify(MmcAdmissionLease lease) {
        using var pendingPermit = owned.EnterPending(lease);
        lease.AssertHeld();
        if (lease.VersionRoot != TemporaryRoot || lease.InstanceId != InstanceId) throw new IOException("Clone reservation admission mismatch.");
        var paths = MmcInstancePaths.Resolve(TemporaryRoot);
        if (MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json")) != markerHash ||
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, ".pclignore")) != ignoreHash) throw new IOException("Clone ownership markers changed; private data was preserved.");
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
    }
    internal MmcInstancePaths Publish(MmcAdmissionLease privateLease, MmcAdmissionLease finalLease, MmcClonePreparedManifest manifest, MmcInstanceDefinition definition) {
        Verify(privateLease); finalLease.AssertHeld();
        if (finalLease.VersionRoot != FinalRoot || finalLease.InstanceId != InstanceId || MmcInstancePaths.Attributes(FinalRoot) is not null) throw new IOException("Clone target name was claimed; both roots were preserved.");
        var paths = MmcInstancePaths.Resolve(TemporaryRoot, definition.Layout.GameRootLeaf);
        if (snapshotHash is null || MmcDurableFiles.Hash(MmcSavedSourceStore.ReadBounded(paths, "PCL/MmcClonePrepared.json", 64 * 1024 * 1024)) != snapshotHash)
            throw new IOException("Clone snapshot record changed before publication.");
        var primary = MmcSavedSourceStore.ReadPrimary(paths).Definition;
        if (primary.InstanceId != InstanceId || primary.ParentInstanceId != manifest.SourceInstanceId || primary.Generation != 1 ||
            MmcDefinitionCodec.ComputeBoundFingerprint(primary) != definition.InstalledProjection!.BoundFingerprint)
            throw new IOException("Clone primary is not the exact newly bound child instance.");
        if (MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(FinalRoot) + ".json")) != primary.InstalledProjection!.OutputJsonSha256)
            throw new IOException("Clone compatibility projection changed before publication.");
        var observed = MmcManagementReader.Evaluate(MmcManagementProbe.CaptureAtPhysicalRoot(TemporaryRoot, Path.GetFileName(FinalRoot)) with { HasPendingTransaction = false });
        if (observed.State != MmcManagementState.ManagedMmc || observed.Envelope?.InstanceId != InstanceId || observed.Envelope.Generation != 1)
            throw new IOException("Clone ownership projections are incomplete.");
        string staging = paths.ResolveRelative(MmcPathRole.Runtime, ".staging");
        if (Directory.Exists(staging)) { if (Directory.EnumerateFileSystemEntries(staging).Any()) throw new IOException("Clone still owns active preparation workspaces."); Directory.Delete(staging, false); }
        if (Path.GetDirectoryName(TemporaryRoot) != Path.GetDirectoryName(FinalRoot)) throw new IOException("Clone publication must be a no-overwrite sibling rename.");
        Directory.Move(TemporaryRoot, FinalRoot);
        var final = MmcInstancePaths.Resolve(FinalRoot, definition.Layout.GameRootLeaf);
        if (MmcDurableFiles.Observe(final.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json")) != markerHash) throw new IOException("Published clone marker changed.");
        File.Delete(Path.Combine(FinalRoot, ".pclignore")); File.Delete(Path.Combine(FinalRoot, "PCL", "MmcImportPending.json")); return final;
    }
    internal string? RecoveryRoot => owned.RecoveryRoot;
    internal MmcPendingOperationPermit EnterPending(MmcAdmissionLease lease) => owned.EnterPending(lease);
    public void Dispose() => owned.Dispose();
}
