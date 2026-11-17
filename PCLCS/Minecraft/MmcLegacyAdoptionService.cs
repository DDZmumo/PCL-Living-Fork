using System.Collections.Immutable;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcLegacyAdoptionReady(int SchemaVersion, Guid OperationId, Guid InstanceId, string PrimarySha256,
    string ConfigSha256, string IniSha256, ImmutableDictionary<string, string> DerivedGameFiles);
internal sealed record MmcLegacyAdoptionReadySeal(int SchemaVersion, Guid OperationId, string Sha256, long SizeBytes);

/// <summary>Explicit copy conversion. The old instance is only read/backed up; only the new private/final paths acquire a managed identity.</summary>
public sealed partial class MmcLegacyAdoptionService<TNetFile> where TNetFile : notnull {
    private readonly MmcMetadataStore metadata;
    private readonly MmcInstanceBackend<TNetFile> backend;
    private readonly MmcLegacyAdoptionHost host;
    public MmcLegacyAdoptionService(MmcMetadataStore metadata, MmcArtifactCacheRoots roots, Func<MmcArtifactDownloadSpec, TNetFile> build,
        Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch, MmcLegacyAdoptionHost host) {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)); this.host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(host.ObserveActivity); ArgumentNullException.ThrowIfNull(host.AcquireLegacyPathLock);
        backend = new(metadata, roots, build, runBatch);
    }
    public Task<MmcLegacyAdoptionPreview> PreviewAsync(MmcLegacyAdoptionPreviewRequest request, CancellationToken cancellationToken = default,
        IProgress<MmcLegacyAdoptionProgress>? progress = null) => Task.Run(() => {
            var source = MmcArchiveSourceReader.ReadAsync(request.OriginalArchivePath, cancellationToken: cancellationToken).GetAwaiter().GetResult();
            try { return Preview(source, request, null, cancellationToken, progress); } catch { source.Dispose(); throw; }
        }, CancellationToken.None);
    /// <summary>On success preview owns the caller's one original ZIP handle. On failure the caller still owns it.</summary>
    public Task<MmcLegacyAdoptionPreview> PreviewPreparedAsync(MmcArchiveSource source, MmcLegacyAdoptionPreviewRequest request,
        MmcResolvedSourceBundle? resolved = null, CancellationToken cancellationToken = default, IProgress<MmcLegacyAdoptionProgress>? progress = null) =>
        Task.Run(() => Preview(source, request, resolved, cancellationToken, progress), CancellationToken.None);
    private MmcLegacyAdoptionPreview Preview(MmcArchiveSource source, MmcLegacyAdoptionPreviewRequest request,
        MmcResolvedSourceBundle? prepared, CancellationToken token, IProgress<MmcLegacyAdoptionProgress>? progress) {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(request);
        if (request.Environment.JavaMajor < 1 || !string.Equals(source.OriginalPath, Path.GetFullPath(request.OriginalArchivePath), MmcLegacyAdoptionSnapshot.Comparison))
            throw new IOException("转换需要固定的原包文件和已选定的真实 Java 环境。");
        string root = MmcLegacyAdoptionSnapshot.Canonical(request.SourceRoot), game = MmcLegacyAdoptionSnapshot.GameRoot(root, request.GameRoot);
        using var pathLock = host.AcquireLegacyPathLock(root) ?? throw new IOException("旧实例正在被修改。");
        RequireIdle(root, game);
        var resolved = prepared ?? MmcMetadataResolution.ResolveAsync(source.Package, metadata, cancellationToken: token).GetAwaiter().GetResult();
        if (!resolved.Resolution.IsComplete || resolved.Resolution.Profile is null || !ReferenceEquals(resolved.Resolution.Profile.Package, source.Package)) throw new IOException("所选原包组件尚未完整解析。");
        var compile = MmcProfileCompiler.Compile(resolved.Resolution.Profile, request.Environment);
        if (compile.HasErrors || compile.Profile is null || !compile.Profile.JavaPolicy.Allows(request.Environment.JavaMajor)) throw new IOException("所选原包不能在指定 Java/平台环境中编译。");
        var author = MmcLegacyAdoptionEvidence.ReadAuthorFiles(source, token, progress);
        MmcConfigurationCoordinator.FlushPending(root);
        MmcLegacyAdoptionInventory inventory; (JObject Config, string Ini, string OldComponents) old;
        using (var quiet = MmcConfigurationCoordinator.Quiesce(root, token))
        using (var gate = MmcAdmissionGate.Acquire(root, Guid.NewGuid(), TimeSpan.Zero, token)) {
            inventory = MmcLegacyAdoptionSnapshot.Capture(root, game, token, progress); old = MmcLegacyAdoptionEvidence.ReadMetadata(inventory);
            MmcLegacyAdoptionSnapshot.Verify(inventory, token, progress); RequireIdle(root, game);
        }
        var check = MmcLegacyAdoptionEvidence.Compare(source, resolved, inventory, author, old.OldComponents);
        string fingerprint = MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Domain = "mmc-legacy-preview-v1", inventory.Fingerprint,
            source.PackageSha256, compile.Profile.ProfileFingerprint, compile.Profile.RuntimePlanFingerprint, check.HistoricalSourceVerified }));
        return new(source, resolved, compile.Profile, inventory, author, check, old.Config.ToString(Newtonsoft.Json.Formatting.None), old.Ini, fingerprint);
    }
    public Task<MmcLegacyAdoptionResult> CreateManagedCopyAsync(MmcLegacyAdoptionPreview preview, MmcLegacyAdoptionCopyRequest request,
        CancellationToken cancellationToken = default, IProgress<MmcLegacyAdoptionProgress>? progress = null) =>
        Task.Run(() => Create(preview, request, cancellationToken, progress, false), CancellationToken.None);
    public Task<MmcLegacyAdoptionResult> CreateManagedCopyAutomaticallyAsync(MmcLegacyAdoptionPreview preview, MmcLegacyAdoptionCopyRequest request,
        CancellationToken cancellationToken = default, IProgress<MmcLegacyAdoptionProgress>? progress = null) =>
        Task.Run(() => Create(preview, request, cancellationToken, progress, true), CancellationToken.None);
    private MmcLegacyAdoptionResult Create(MmcLegacyAdoptionPreview preview, MmcLegacyAdoptionCopyRequest request, CancellationToken external, IProgress<MmcLegacyAdoptionProgress>? progress, bool automaticCompletion) {
        MmcLegacyAdoptionJournal? operation = null; bool decided = false;
        using var fence = new MmcLifecycleCancellationFence(external); CancellationToken token = fence.Token;
        try {
            ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(request); preview.AssertOpen();
            if (!Enum.IsDefined(request.Intent) || request.ConfirmedPreviewFingerprint != preview.ConfirmationFingerprint(request.TargetRoot, request.InstanceName, request.Intent))
                throw new IOException("转换意图、目标或预览确认已变化，必须重新明确确认。");
            if (request.Intent == MmcLegacyAdoptionIntent.StrictVerified && !preview.SourceCheck.HistoricalSourceVerified)
                throw new IOException("旧历史来源未验证，不能静默切换为新来源重绑定。");
            using var pathLock = host.AcquireLegacyPathLock(preview.Inventory.SourceRoot) ?? throw new IOException("旧实例正在被修改。");
            RequireIdle(preview.Inventory.SourceRoot, preview.Inventory.GameRoot);
            operation = MmcLegacyAdoptionJournal.Create(preview, request);
            using var driver = operation.AcquireDriver(create: true);
            MmcConfigurationCoordinator.FlushPending(preview.Inventory.SourceRoot);
            using (var quiet = MmcConfigurationCoordinator.Quiesce(preview.Inventory.SourceRoot, token))
            using (var gate = MmcAdmissionGate.Acquire(preview.Inventory.SourceRoot, operation.Intent.OperationId, TimeSpan.Zero, token)) {
                MmcLegacyAdoptionSnapshot.Verify(preview.Inventory, token, progress);
                _ = MmcLegacyAdoptionBackup.Create(operation.DirectoryPath, preview.Inventory, token, progress);
                MmcLegacyAdoptionSnapshot.Verify(preview.Inventory, token, progress); RequireIdle(preview.Inventory.SourceRoot, preview.Inventory.GameRoot);
            }
            request.Checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.BackupDurable, operation.DirectoryPath); token.ThrowIfCancellationRequested();
            // No source quiescence/admission is retained during dependency/network preparation.
            var importRequest = new MmcInstanceImportRequest(preview.Source.OriginalPath, operation.PrivateRoot, request.InstanceName, preview.Environment);
            var importTask = automaticCompletion ? backend.ImportPreparedAutomaticallyAsync(preview.Source, importRequest, token, new ProgressAdapter(progress), preview.Resolved)
                : backend.ImportPreparedAsync(preview.Source, importRequest, token, new ProgressAdapter(progress), preview.Resolved);
            var imported = importTask.GetAwaiter().GetResult();
            if (imported.Status != MmcInstanceBackendStatus.Succeeded || imported.Definition is null || imported.Paths is null) {
                if (automaticCompletion) {
                    if (imported.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(imported.Failure, token);
                    throw new IOException(imported.Failure ?? "The private MMC import failed.");
                }
                return Result(imported.Status, operation, imported.Failure);
            }
            var paths = imported.Paths; var primary = MmcSavedSourceStore.ReadPrimary(paths);
            using (var quiet = MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot, CancellationToken.None))
            using (var gate = MmcAdmissionGate.Acquire(paths.InstanceRoot, primary.Definition.InstanceId, TimeSpan.Zero, CancellationToken.None)) {
                operation.RecordPublication(primary);
                if (automaticCompletion) operation.InitializeProducerCleanup(imported.ProducedFiles);
                operation.MarkPrivate();
                using var permit = operation.Enter(gate);
                MmcLegacyAdoptionConfiguration.AttachOwned(quiet, gate, operation);
                request.Checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.PrivateImportComplete, operation.DirectoryPath); token.ThrowIfCancellationRequested();
                var saved = ValidateRuntime(paths, primary.Definition, token);
                var derived = DerivedGame(primary.Definition, paths);
                var local = saved.Baseline.Baseline.Files.Where(file => file.Role == PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary && file.TargetRelativePath is not null)
                    .ToDictionary(file => file.TargetRelativePath!, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
                RemoveAuthorPersonalPayload(paths, saved.Baseline, derived, token);
                MmcLegacyAdoptionBackup.CopyPersonalFiles(operation.DirectoryPath, operation.Backup(), preview.Inventory, paths, derived, local, token, progress,
                    automaticCompletion ? operation.ProducedFile : null, automaticCompletion ? operation.ProducedDirectory : null);
                var finalPaths = MmcInstancePaths.Resolve(operation.FinalRoot, paths.GameRootLeaf);
                MmcLegacyAdoptionConfiguration.Copy(quiet, gate, operation, preview, finalPaths);
                WriteReady(operation, paths, primary, derived);
                request.Checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.PersonalSnapshotApplied, operation.DirectoryPath);
            }
            MmcConfigurationCoordinator.FlushPending(preview.Inventory.SourceRoot);
            using var access = new AdoptionAccess(operation, includeLegacy: true, token);
            var privateLease = access.Leases[operation.PrivateRoot];
            using var owner = operation.Enter(privateLease);
            MmcFileTransactions.AssertNoPending(privateLease); MmcSessions.AssertNoActive(privateLease);
            RequireTargetFree(operation, access); RequireIdle(preview.Inventory.SourceRoot, preview.Inventory.GameRoot);
            MmcLegacyAdoptionSnapshot.Verify(preview.Inventory, token, progress);
            VerifyReady(operation, paths, token); _ = ValidateRuntime(paths, primary.Definition, token);
            RequireIdle(preview.Inventory.SourceRoot, preview.Inventory.GameRoot); token.ThrowIfCancellationRequested();
            request.Checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.BeforeDecision, operation.DirectoryPath);
            fence.EnterDecision(); operation.Decide(); decided = true;
            request.Checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.CommitDecided, operation.DirectoryPath);
            return Forward(operation, access, request.Checkpoint);
        } catch (OperationCanceledException error) {
            var failed = Result(decided ? MmcInstanceBackendStatus.NeedsRecovery : MmcInstanceBackendStatus.Cancelled, operation, error.Message);
            return automaticCompletion ? CompleteProducerFailure(operation, failed, request?.AutomaticCleanupCheckpoint) : failed;
        }
        catch (Exception error) when (Expected(error)) {
            var failed = Result(operation is null ? MmcInstanceBackendStatus.Rejected : MmcInstanceBackendStatus.NeedsRecovery, operation, error.Message);
            return automaticCompletion ? CompleteProducerFailure(operation, failed, request?.AutomaticCleanupCheckpoint) : failed;
        }
    }
    public Task<MmcLegacyAdoptionResult> RecoverAsync(string operationDirectory, string? expectedIntentSha256 = null) => Task.Run(() => {
        MmcLegacyAdoptionJournal? operation = null;
        try {
            operation = MmcLegacyAdoptionJournal.Read(operationDirectory);
            using var driver = operation.AcquireDriver(create: false);
            if (expectedIntentSha256 is not null && operation.IntentHash != expectedIntentSha256) throw new IOException("恢复确认的转换记录已变化。");
            if (!operation.HasDecision()) return Result(MmcInstanceBackendStatus.NeedsRecovery, operation, "转换尚未决定发布；旧实例及备份保留。请重新预览创建副本，不会自动采用半成品。");
            using var access = new AdoptionAccess(operation, includeLegacy: false, CancellationToken.None);
            return Forward(operation, access, null);
        } catch (Exception error) when (Expected(error)) { return Result(MmcInstanceBackendStatus.NeedsRecovery, operation, error.Message); }
    }, CancellationToken.None);
    private MmcLegacyAdoptionResult CompleteProducerFailure(MmcLegacyAdoptionJournal? operation, MmcLegacyAdoptionResult failed, Action<MmcInstancePurgeCheckpoint, string?>? checkpoint) {
        if (operation is null) return failed;
        try {
            var current = MmcLegacyAdoptionJournal.Read(operation.DirectoryPath);
            if (current.IntentHash != operation.IntentHash) throw new IOException("Current producer's copy operation changed.");
            // Create's path/driver/configuration leases have all left their scopes before its catch.
            if (operation.HasDecision()) return RecoverAsync(operation.DirectoryPath, operation.IntentHash).GetAwaiter().GetResult();
            using var driver = operation.AcquireDriver(create: false);
            RequireIdle(operation.Intent.SourceRoot, operation.Intent.GameRoot);
            if (Directory.Exists(operation.PrivateRoot)) {
                InstanceConfigProviders.FlushAndDisposeWithin(operation.PrivateRoot);
                using var access = new AdoptionAccess(operation, includeLegacy: false, CancellationToken.None);
                var lease = access.Leases[operation.PrivateRoot]; using var permit = operation.Enter(lease);
                MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
                operation.DiscardProducerPrivate(checkpoint);
            } else {
                string versions = Path.GetDirectoryName(operation.PrivateRoot)!;
                MmcInstancePaths.EnsureNoLinks(versions);
                if (Directory.Exists(versions) && Directory.EnumerateFileSystemEntries(versions).Any())
                    throw new IOException("An inner import still has unresolved private data; preserving all evidence.");
            }
            operation.RecordRolledBack();
            return failed with { Status = failed.Status == MmcInstanceBackendStatus.Cancelled ? failed.Status : MmcInstanceBackendStatus.Rejected,
                Failure = failed.Failure + "\n本次未发布副本已自动回退；旧实例和私有备份保留。" };
        } catch (Exception error) when (Expected(error)) {
            return failed with { Status = MmcInstanceBackendStatus.NeedsRecovery, Failure = failed.Failure + "\n无法安全自动收尾，内部证据保留：" + error.Message + MmcProducerCleanup.PreserveFailure(operation.DirectoryPath, error) };
        }
    }
    private static MmcLegacyAdoptionResult Forward(MmcLegacyAdoptionJournal operation, AdoptionAccess access, Action<MmcLegacyAdoptionCheckpoint, string>? checkpoint) {
        if (!operation.HasDecision()) throw new IOException("管理副本缺少持久发布决定。");
        bool privateOwned = operation.Owns(operation.PrivateRoot), finalOwned = operation.Owns(operation.FinalRoot);
        if (privateOwned && finalOwned) throw new IOException("管理副本在两个位置同时出现，不能猜测归属。");
        if (operation.IsCompleted() && !finalOwned) {
            var complete = operation.RequirePrimary(operation.FinalRoot);
            if (MmcInstancePaths.Attributes(Path.Combine(operation.FinalRoot, MmcLegacyAdoptionJournal.MarkerRelative.Replace('/', Path.DirectorySeparatorChar))) is not null)
                throw new IOException("已完成副本含有其它操作标记。");
            return new(MmcInstanceBackendStatus.Succeeded, complete.Definition, MmcInstancePaths.Resolve(operation.FinalRoot, complete.Definition.Layout.GameRootLeaf), operation.DirectoryPath, Path.Combine(operation.DirectoryPath, "backup.zip"));
        }
        if (privateOwned) {
            var gate = access.Leases[operation.PrivateRoot]; using var permit = operation.Enter(gate);
            MmcFileTransactions.AssertNoPending(gate); MmcSessions.AssertNoActive(gate);
            RequireTargetFree(operation, access); var primary = operation.RequirePrimary(operation.PrivateRoot);
            var paths = MmcInstancePaths.Resolve(operation.PrivateRoot, primary.Definition.Layout.GameRootLeaf);
            VerifyReady(operation, paths, CancellationToken.None); _ = ValidateRuntime(paths, primary.Definition, CancellationToken.None);
            MmcArtifactIo.Directory(Path.GetDirectoryName(operation.FinalRoot)!); MmcLifecycleAccess.SameVolume(operation.PrivateRoot, operation.FinalRoot);
            Directory.Move(operation.PrivateRoot, operation.FinalRoot); checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.DirectoryMoved, operation.DirectoryPath);
        }
        if (!operation.Owns(operation.FinalRoot) || MmcInstancePaths.Attributes(operation.PrivateRoot) is not null) throw new IOException("管理副本位置缺失或冲突；所有目录已保留。");
        var finalLease = access.Leases[operation.FinalRoot];
        using (var permit = operation.Enter(finalLease)) {
            MmcFileTransactions.AssertNoPending(finalLease); MmcSessions.AssertNoActive(finalLease);
            var current = operation.RequirePrimary(operation.FinalRoot); var finalPaths = MmcInstancePaths.Resolve(operation.FinalRoot, current.Definition.Layout.GameRootLeaf);
            VerifyReady(operation, finalPaths, CancellationToken.None);
            MmcConfigurationCoordinator.CompletePathRelocation(access.Quiet[operation.PrivateRoot], access.Quiet[operation.FinalRoot], access.Leases[operation.PrivateRoot], finalLease, current.Definition.InstanceId, current.Sha256);
            MmcConfigurationCoordinator.FlushRelocatedPreferences(access.Quiet[operation.FinalRoot], finalLease, current.Definition.InstanceId, current.Sha256);
            operation.Complete(); operation.ClearMarker(operation.FinalRoot);
            checkpoint?.Invoke(MmcLegacyAdoptionCheckpoint.Completed, operation.DirectoryPath);
            return new(MmcInstanceBackendStatus.Succeeded, current.Definition, finalPaths, operation.DirectoryPath, Path.Combine(operation.DirectoryPath, "backup.zip"));
        }
    }
    private void RequireIdle(string source, string game) { if (host.ObserveActivity(source, game) != MmcLegacyAdoptionActivity.Idle) throw new IOException("旧实例存在活动或无法确认的游戏会话，请完全退出游戏后重试。"); }
    private static MmcLegacyAdoptionResult Result(MmcInstanceBackendStatus status, MmcLegacyAdoptionJournal? operation, string? error) =>
        new(status, OperationDirectory: operation?.DirectoryPath, BackupPath: operation is not null && File.Exists(Path.Combine(operation.DirectoryPath, "backup.zip")) ? Path.Combine(operation.DirectoryPath, "backup.zip") : null, Failure: error);
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        System.Net.Http.HttpRequestException or AggregateException or System.Text.Json.JsonException or Newtonsoft.Json.JsonException or FormatException or OverflowException or System.ComponentModel.Win32Exception or PlatformNotSupportedException;
    private sealed class ProgressAdapter(IProgress<MmcLegacyAdoptionProgress>? progress) : IProgress<MmcInstanceBackendProgress> { public void Report(MmcInstanceBackendProgress value) => progress?.Report(new(value.Phase, value.CompletedBytes, value.TotalBytes)); }
    private sealed class AdoptionAccess : IDisposable {
        internal readonly Dictionary<string, MmcConfigurationQuiescence> Quiet = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, MmcAdmissionLease> Leases = new(StringComparer.OrdinalIgnoreCase);
        internal AdoptionAccess(MmcLegacyAdoptionJournal operation, bool includeLegacy, CancellationToken token) {
            var publication = operation.Publication() ?? throw new IOException("管理副本事实缺失。");
            string[] roots = (includeLegacy ? new[] { operation.Intent.SourceRoot, operation.PrivateRoot, operation.FinalRoot } : new[] { operation.PrivateRoot, operation.FinalRoot }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            try {
                foreach (string root in roots) Quiet.Add(root, MmcConfigurationCoordinator.Quiesce(root, token));
                foreach (string root in roots) Leases.Add(root, MmcAdmissionGate.Acquire(root, root == operation.Intent.SourceRoot ? operation.Intent.OperationId : publication.Record.InstanceId, TimeSpan.Zero, token));
            } catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var lease in Leases.Values.Reverse()) lease.Dispose(); foreach (var quiet in Quiet.Values.Reverse()) quiet.Dispose(); Leases.Clear(); Quiet.Clear(); }
    }
    private static void RequireTargetFree(MmcLegacyAdoptionJournal operation, AdoptionAccess access) {
        if (MmcInstancePaths.Attributes(operation.FinalRoot) is not null) throw new IOException("副本名称已被占用，不会覆盖其它实例。");
        var state = access.Quiet[operation.FinalRoot].State; var publication = operation.Publication()!.Value.Record;
        lock (state.Sync) if (state.Patches.Any(pair => pair.Value.Context != new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, publication.InstanceId)) ||
            state.IniPatches.Any(pair => pair.Value.Context != new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, publication.InstanceId))) throw new IOException("目标名称仍有其它身份的延迟偏好写入。");
    }
}
