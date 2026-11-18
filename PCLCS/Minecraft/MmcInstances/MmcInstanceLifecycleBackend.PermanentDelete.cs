using System.Collections.Immutable;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed partial class MmcInstanceLifecycleBackend {
    public Task<MmcInstancePermanentDeletePreview> PreviewPermanentDeleteAsync(MmcInstanceDeleteRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => PreviewPermanentDelete(request, cancellationToken), CancellationToken.None);
    public Task<MmcInstanceLifecycleResult> PermanentlyDeleteAsync(MmcInstancePermanentDeleteRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => PermanentlyDelete(request, cancellationToken), CancellationToken.None);

    private static MmcInstancePermanentDeletePreview PreviewPermanentDelete(MmcInstanceDeleteRequest request, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        string root = Canonical(request.SourceRoot);
        using var quiet = MmcConfigurationCoordinator.Quiesce(root, token);
        using var lease = MmcAdmissionGate.Acquire(root, request.ExpectedInstanceId, TimeSpan.Zero, token);
        var snapshot = CaptureLiveDelete(request with { SourceRoot = root }, lease, token);
        return new(snapshot, LiveDeleteFingerprint(snapshot));
    }

    private static MmcLiveDeleteSnapshot CaptureLiveDelete(MmcInstanceDeleteRequest request, MmcAdmissionLease lease, CancellationToken token) {
        var primary = Source(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
        var inspection = MmcInstanceInspector.Read(lease.VersionRoot);
        if (!inspection.IsConsistent || inspection.Definition?.InstanceId != request.ExpectedInstanceId || inspection.PrimarySha256 != request.ExpectedPrimarySha256)
            throw new IOException("MMC 实例未通过完整校验，已停止永久删除。");
        var paths = MmcInstancePaths.Resolve(lease.VersionRoot, primary.Definition.Layout.GameRootLeaf);
        MmcInstancePurgeSnapshot.AssertIdle(lease, paths);
        // Complete, indexed history snapshots are owned records of this same instance.
        // No other nested owner (including an abandoned or foreign snapshot) is authorized.
        HashSet<string> historyPrimaries = new(StringComparer.OrdinalIgnoreCase);
        foreach (var point in MmcInstanceHistoryStore.List(paths, token)) {
            if (point.InstanceId != request.ExpectedInstanceId) throw new IOException("发现其他实例的环境记录，已保留全部内容。");
            var loaded = MmcInstanceHistoryStore.Read(paths, point.PointId, point.ManifestSha256, token);
            historyPrimaries.Add(Path.GetRelativePath(paths.InstanceRoot, loaded.SourcePaths.InstanceRoot).Replace('\\', '/') + "/" + MmcInstancePurgeSnapshot.Primary);
        }
        using var pins = new MmcPurgeDirectoryPins(paths.InstanceRoot);
        var entries = MmcInstancePurgeSnapshot.CaptureTree(paths, pins.Last, token, historyPrimaries);
        if (entries.SingleOrDefault(entry => entry.RelativePath == MmcInstancePurgeSnapshot.Primary) is not { Directory: false } definition || definition.Sha256 != request.ExpectedPrimarySha256)
            throw new IOException("永久删除清单缺少已核实的实例主定义。");
        _ = Source(lease, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256);
        return new(paths.InstanceRoot, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256, paths.GameRootLeaf, entries);
    }

    private static string LiveDeleteFingerprint(MmcLiveDeleteSnapshot snapshot) => MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot));

    private static MmcInstanceLifecycleResult PermanentlyDelete(MmcInstancePermanentDeleteRequest request, CancellationToken token) {
        bool committed = false; string? root = request?.Preview?.SourceRoot; MmcInstanceDefinition? definition = null;
        try {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Preview);
            var preview = request.Preview;
            if (preview.InstanceId == Guid.Empty || preview.SnapshotFingerprint != request.ConfirmedPreviewFingerprint ||
                preview.SnapshotFingerprint != LiveDeleteFingerprint(preview.Snapshot) || Canonical(preview.SourceRoot) != preview.SourceRoot)
                throw new IOException("永久删除需要确认当前实例的完整固定清单。");
            using var quiet = MmcConfigurationCoordinator.Quiesce(preview.SourceRoot, token);
            using var lease = MmcAdmissionGate.Acquire(preview.SourceRoot, preview.InstanceId, TimeSpan.Zero, token);
            var selection = new MmcInstanceDeleteRequest(preview.SourceRoot, preview.InstanceId, preview.Generation, preview.PrimarySha256);
            var fresh = CaptureLiveDelete(selection, lease, token);
            if (LiveDeleteFingerprint(fresh) != preview.SnapshotFingerprint)
                throw new IOException("确认后实例文件、目录或身份已变化，请重新确认；当前内容已保留。");
            definition = Source(lease, preview.InstanceId, preview.Generation, preview.PrimarySha256).Definition;
            var paths = MmcInstancePaths.Resolve(preview.SourceRoot, fresh.GameRootLeaf);
            // Only outer ancestor pins survive staging. Every approved entry is verified
            // again through its transacted writer; root disposition proves no new child remains.
            using var ancestors = new MmcPurgeDirectoryPins(Path.GetDirectoryName(preview.SourceRoot)!);
            using (var transaction = new MmcPurgeNtfsTransaction()) {
                request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.PlanPrepared, null);
                foreach (var entry in fresh.Entries.Where(entry => !entry.Directory)
                    .OrderBy(entry => entry.RelativePath == MmcInstancePurgeSnapshot.Primary ? 1 : 0).ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)) Stage(entry);
                foreach (var entry in fresh.Entries.Where(entry => entry.Directory)
                    .OrderByDescending(entry => entry.RelativePath.Count(character => character == '/')).ThenByDescending(entry => entry.RelativePath.Length).ThenByDescending(entry => entry.RelativePath, StringComparer.Ordinal)) Stage(entry);
                token.ThrowIfCancellationRequested();
                request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeDecision, null);
                token.ThrowIfCancellationRequested();
                transaction.Commit(); committed = true;

                void Stage(MmcInstancePurgeEntry entry) {
                    token.ThrowIfCancellationRequested();
                    string target = MmcInstancePurgeSnapshot.Target(paths, entry.RelativePath);
                    using (var writer = transaction.OpenWriter(target, entry.Directory)) {
                        writer.Verify(entry, token); writer.AssertDefaultDataStreamOnly();
                        request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.BeforeEntryDelete, entry.RelativePath);
                        writer.StageTransactedDelete();
                    }
                    request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.EntryStaged, entry.RelativePath);
                }
            }
            // A callback failure after Commit must never turn a completed deletion into a
            // rollback request. Finish provider detachment even when reporting hooks fail.
            Exception? notificationFailure = null;
            try { request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.CommitDecided, null); }
            catch (Exception error) when (NonFatalDeleteError(error)) { notificationFailure = error; }
            MmcConfigurationCoordinator.DetachDeletedRoot(preview.SourceRoot);
            try { request.Checkpoint?.Invoke(MmcInstancePurgeCheckpoint.Completed, null); }
            catch (Exception error) when (NonFatalDeleteError(error)) { notificationFailure ??= error; }
            return new(MmcInstanceBackendStatus.Succeeded, Definition: definition, Failure: notificationFailure?.Message) { PurgeDecided = true, Purged = true };
        } catch (Exception error) when (committed && NonFatalDeleteError(error) || Failure(error) || error is MmcLifecycleInterruptionException or OperationCanceledException or PlatformNotSupportedException) {
            if (committed) {
                try { MmcConfigurationCoordinator.DetachDeletedRoot(root!); }
                catch (Exception cleanup) when (NonFatalDeleteError(cleanup)) { return new(MmcInstanceBackendStatus.NeedsRecovery, Definition: definition,
                    Failure: "永久删除已提交，旧配置收尾失败；新出现的目录已保留。" + cleanup.Message) { PurgeDecided = true, Purged = true }; }
                return new(MmcInstanceBackendStatus.Succeeded, Definition: definition, Failure: error.Message) { PurgeDecided = true, Purged = true };
            }
            return new(error is OperationCanceledException ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.Rejected,
                CurrentRoot: root, Definition: definition, Failure: "永久删除未提交，实例内容已保留。" + error.Message);
        }
    }
    private static bool NonFatalDeleteError(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
