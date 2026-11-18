using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcImportRecoveryState { ProducerActive, Recoverable, OriginalRequired, Completed, Conflict, LegacyUnproven, CloneOnly, Unknown }
public sealed record MmcImportRecoveryEntry(string MinecraftRoot, string OperationDirectory, string Name, MmcImportRecoveryState State,
    string Description, bool CanRecover = false, bool CanRetry = false, string? PreviewFingerprint = null,
    string? PackageSha256 = null, string? TemporaryRoot = null, string? FinalRoot = null);
public sealed record MmcImportRecoveryCatalog(ImmutableArray<MmcImportRecoveryEntry> Entries, bool Truncated);
public sealed record MmcImportRecoveryResult(MmcInstanceBackendStatus Status, MmcInstancePaths? Paths = null,
    string? OperationDirectory = null, bool FreshImportRequired = false, string? Diagnostic = null);

public static class MmcImportRecovery {
    public static MmcImportRecoveryCatalog Read(string minecraftRoot) {
        string root = Root(minecraftRoot), store = Path.Combine(root, "PCL", MmcImportOperation.StoreName);
        List<MmcImportRecoveryEntry> entries = []; HashSet<string> known = new(StringComparer.OrdinalIgnoreCase); bool truncated = false;
        if (MmcInstancePaths.Attributes(store) is not null) {
            try {
                MmcInstancePaths.EnsureNoLinks(store); string[] directories = Directory.EnumerateFileSystemEntries(store).Take(257).ToArray(); truncated |= directories.Length > 256;
                foreach (string directory in directories.Take(256)) {
                    try {
                        var operation = MmcImportOperation.Read(directory); known.Add(operation.Owner.TemporaryRoot); known.Add(operation.Owner.FinalRoot);
                        entries.Add(Inspect(root, operation));
                    } catch (Exception error) when (Expected(error)) { entries.Add(new(root, directory, "无法确认的导入记录", MmcImportRecoveryState.Unknown, "记录无法验证，现场已保留：" + error.Message)); }
                }
            } catch (Exception error) when (Expected(error)) { entries.Add(new(root, store, "无法读取的导入恢复目录", MmcImportRecoveryState.Unknown, error.Message)); }
        }
        string versions = Path.Combine(root, "versions");
        if (Directory.Exists(versions)) {
            MmcInstancePaths.EnsureNoLinks(versions); string[] directories = Directory.EnumerateDirectories(versions).Take(513).ToArray(); truncated |= directories.Length > 512;
            foreach (string directory in directories.Take(512).Where(directory => !known.Contains(directory))) {
                string marker = Path.Combine(directory, "PCL", "MmcImportPending.json"); if (MmcInstancePaths.Attributes(marker) is null) continue;
                try {
                    MmcInstancePaths.EnsureNoLinks(marker); using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > 8192) throw new IOException("旧导入标记超过读取预算。");
                    byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                    using var json = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(bytes));
                    string? hash = json.RootElement.TryGetProperty("PackageSha256", out var value) ? value.GetString() : null;
                    string? final = json.RootElement.TryGetProperty("FinalRoot", out value) ? value.GetString() : null;
                    bool retry = MmcDurableFiles.IsHash(hash); string fingerprint = MmcDurableFiles.Hash(bytes);
                    entries.Add(new(root, "", Path.GetFileName(directory), MmcImportRecoveryState.LegacyUnproven,
                        "旧开发版残留没有生产者退出证明，只能保留现场并选择同 SHA 原包、使用不同名称重新导入。", CanRetry: retry,
                        PreviewFingerprint: fingerprint, PackageSha256: hash, TemporaryRoot: directory, FinalRoot: final));
                } catch (Exception error) when (Expected(error)) { entries.Add(new(root, "", Path.GetFileName(directory), MmcImportRecoveryState.Unknown, "旧残留标记损坏，不能自动处理：" + error.Message)); }
            }
        }
        return new(entries.ToImmutableArray(), truncated);
    }
    private static MmcImportRecoveryEntry Inspect(string root, MmcImportOperation operation) {
        var owner = operation.Owner;
        if (Path.GetDirectoryName(operation.DirectoryPath) != Path.Combine(root, "PCL", MmcImportOperation.StoreName)) throw new IOException("导入记录不属于当前游戏文件夹。");
        if (owner.Purpose == MmcImportReservationPurpose.Clone) return Entry(MmcImportRecoveryState.CloneOnly, "这是克隆预留记录，个人快照不能按作者初始载荷发布。");
        if (operation.RolledBack()) return Entry(MmcImportRecoveryState.Completed, "本次未提交导入已自动回退，临时实例已清理；事务证据保留。此记录不授权恢复或重试当前路径。");
        if (operation.Completed() && !HasOwnedMarker(owner.TemporaryRoot, owner.MarkerSha256) && !HasOwnedMarker(owner.FinalRoot, owner.MarkerSha256))
            return Entry(MmcImportRecoveryState.Completed, "原导入已完成；此记录仅作历史查看，不授权恢复或重试当前路径。实例后续修复、更新或移动不改变该历史结果。");
        bool acquiredUse = false;
        try {
            using var reservation = MmcOwnedImportReservation.Reopen(operation);
            acquiredUse = true;
            bool temporary = Directory.Exists(owner.TemporaryRoot), final = Directory.Exists(owner.FinalRoot);
            if (temporary && final) return Entry(MmcImportRecoveryState.Conflict, "原位置与目标位置同时有内容，不会覆盖任何目录。", retry: true);
            string? current = temporary ? owner.TemporaryRoot : final ? owner.FinalRoot : null;
            if (current is null) return Entry(MmcImportRecoveryState.OriginalRequired, "预留目录不存在，请保留记录并用同 SHA 原包选择不同名称重新导入。", retry: true);
            using var lease = MmcAdmissionGate.Acquire(current, owner.InstanceId, TimeSpan.Zero);
            if (!temporary && operation.Completed() && !File.Exists(Path.Combine(current, "PCL", "MmcImportPending.json"))) {
                var complete = RequirePublicationPrimary(operation, current); RequireHints(operation, current, complete.Definition); return Entry(MmcImportRecoveryState.Completed, "导入已发布；此记录仅供查看。");
            }
            reservation.VerifyRecoveryMarker(current, !temporary);
            using var permit = reservation.EnterPending(lease);
            MmcSessions.AssertNoActive(lease);
            if (temporary) {
                var transactions = InitialTransactions(operation, current);
                if (transactions.Count == 0) return Entry(MmcImportRecoveryState.OriginalRequired, "没有完整初始事务依据，请使用同 SHA 原包、不同名称重新导入。", retry: true);
            } else { var moved = RequirePublicationPrimary(operation, current); RequireHints(operation, current, moved.Definition); }
            return Entry(MmcImportRecoveryState.Recoverable, "可在确认生产者退出后重放原事务并验证载荷，再无覆盖地完成发布。", recover: true, retry: true);
        } catch (IOException error) when (!acquiredUse && (error.HResult & 0xffff) is 32 or 33) { return Entry(MmcImportRecoveryState.ProducerActive, "生产者仍持有使用租约，请等待实际工作结束：" + error.Message); }
        catch (IOException error) { return Entry(MmcImportRecoveryState.Conflict, "恢复依据发生冲突或无法验证，现场已保留：" + error.Message); }
        MmcImportRecoveryEntry Entry(MmcImportRecoveryState state, string description, bool recover = false, bool retry = false) =>
            new(root, operation.DirectoryPath, Path.GetFileName(owner.FinalRoot), state, description, recover, retry, state == MmcImportRecoveryState.Completed ? null : Fingerprint(operation), owner.PackageSha256, owner.TemporaryRoot, owner.FinalRoot);
    }
    private static bool HasOwnedMarker(string root, string expected) {
        try { string path = Path.Combine(root, "PCL", "MmcImportPending.json"); MmcInstancePaths.EnsureNoLinks(path); return File.Exists(path) && new FileInfo(path).Length <= 8192 && MmcDurableFiles.Observe(path) == expected; }
        catch (Exception error) when (Expected(error)) { return false; }
    }
    public static Task<MmcImportRecoveryResult> RecoverAsync(MmcImportRecoveryEntry preview) => Task.Run(() => Recover(preview), CancellationToken.None);
    internal static MmcImportRecoveryResult CompleteProducerFailure(MmcOwnedImportReservation producer, Action<MmcInstancePurgeCheckpoint, string?>? checkpoint = null) {
        var operation = producer.CurrentOperation;
        try {
            operation.VerifySameOwner();
            bool forward = false;
            using (var reserved = MmcOwnedImportReservation.Reopen(operation))
            using (var access = new Access(operation)) {
                if (!Directory.Exists(operation.Owner.TemporaryRoot)) forward = true;
                else {
                    var lease = access.Leases[operation.Owner.TemporaryRoot]; reserved.VerifyRecoveryMarker(lease.VersionRoot, false);
                    using var permit = reserved.EnterPending(lease); MmcSessions.AssertNoActive(lease);
                    var transactions = InitialTransactions(operation, lease.VersionRoot);
                    if (transactions.Count > 1) throw new IOException("Multiple initial transactions cannot be automatically settled.");
                    forward = transactions.Any(transaction => transaction.Decided);
                    if (!forward) {
                        foreach (var transaction in transactions.Where(transaction => transaction.Pending)) {
                            var rolled = MmcFileTransactions.RecoverProducer(lease, transaction.Id, producer.RecordRecoveryProduct);
                            if (rolled.Outcome != MmcTransactionOutcome.Aborted) throw new IOException(rolled.Diagnostic ?? "Uncommitted import did not roll back.");
                        }
                        MmcFileTransactions.AssertNoPending(lease);
                        foreach (var transaction in transactions) producer.RecordRolledBackProducts(MmcInstancePaths.Resolve(lease.VersionRoot), transaction.Id);
                    }
                }
            }
            if (forward) {
                var entry = Inspect(Root(Path.GetDirectoryName(Path.GetDirectoryName(operation.Owner.FinalRoot)!)!), operation);
                if (!entry.CanRecover) throw new IOException(entry.Description);
                return Recover(entry);
            }
            producer.DiscardProducerTemporary(checkpoint);
            return new(MmcInstanceBackendStatus.Succeeded, OperationDirectory: operation.DirectoryPath, FreshImportRequired: true, Diagnostic: "Uncommitted import automatically rolled back; owned temporary instance discarded.");
        } catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, OperationDirectory: operation.DirectoryPath,
            Diagnostic: error.Message + MmcProducerCleanup.PreserveFailure(operation.DirectoryPath, error)); }
    }
    private static MmcImportRecoveryResult Recover(MmcImportRecoveryEntry preview) {
        try {
            if (!preview.CanRecover || string.IsNullOrEmpty(preview.OperationDirectory) || preview.PreviewFingerprint is null) throw new IOException("此残留没有可重放的生产者证明。");
            var operation = MmcImportOperation.Read(preview.OperationDirectory);
            using var reservation = MmcOwnedImportReservation.Reopen(operation);
            if (Fingerprint(operation) != preview.PreviewFingerprint) throw new IOException("导入恢复预览已变化，请重新选择。");
            if (operation.Owner.Purpose != MmcImportReservationPurpose.InitialImport) throw new IOException("克隆不能按初始导入恢复。");
            if (!Directory.Exists(operation.Owner.TemporaryRoot)) return FinishMoved(operation, reservation);
            using (var access = new Access(operation)) {
                if (MmcInstancePaths.Attributes(operation.Owner.FinalRoot) is not null) throw new IOException("目标名称已被占用，保留两个目录。");
                var lease = access.Leases[operation.Owner.TemporaryRoot]; reservation.VerifyRecoveryMarker(lease.VersionRoot, false);
                using var permit = reservation.EnterPending(lease); MmcSessions.AssertNoActive(lease);
                var transactions = InitialTransactions(operation, lease.VersionRoot);
                if (transactions.Count != 1) throw new IOException("无法唯一确认初始导入事务。");
                var transaction = transactions[0];
                if (transaction.Pending) {
                    var result = MmcFileTransactions.Recover(lease, transaction.Id);
                    if (result.Outcome == MmcTransactionOutcome.RecoveryRequired || result.Outcome == MmcTransactionOutcome.Interrupted) throw new IOException(result.Diagnostic);
                    if (result.Outcome == MmcTransactionOutcome.Aborted) { reservation.RecoverRecordedStages(lease); return new(MmcInstanceBackendStatus.NeedsRecovery, OperationDirectory: operation.DirectoryPath, FreshImportRequired: true, Diagnostic: "未提交事务已回滚，旧现场保留；请选择同 SHA 原包并使用不同名称重新导入。"); }
                }
                var current = InitialTransactions(operation, lease.VersionRoot).Single();
                if (!current.Decided) throw new IOException("初始事务没有持久提交决定。");
                var paths = MmcInstancePaths.Resolve(lease.VersionRoot); var primary = MmcSavedSourceStore.ReadPrimary(paths);
                if (primary.Sha256 != current.DefinitionSha256) throw new IOException("初始定义与提交决定中的字节不同。");
                reservation.RestorePayloadFacts(MmcInstancePaths.Resolve(lease.VersionRoot, primary.Definition.Layout.GameRootLeaf), primary.Definition);
                ValidateRuntime(paths, primary.Definition);
                reservation.RecoverRecordedStages(lease);
            }
            var publication = reservation.Publish(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(reservation.TemporaryRoot)).Definition.Layout.GameRootLeaf);
            return new(MmcInstanceBackendStatus.Succeeded, publication, operation.DirectoryPath);
        } catch (Exception error) when (Expected(error)) { return new(MmcInstanceBackendStatus.NeedsRecovery, OperationDirectory: preview.OperationDirectory, Diagnostic: error.Message); }
    }
    private static MmcImportRecoveryResult FinishMoved(MmcImportOperation operation, MmcOwnedImportReservation reservation) {
        using var access = new Access(operation); string root = operation.Owner.FinalRoot;
        if (MmcInstancePaths.Attributes(operation.Owner.TemporaryRoot) is not null) throw new IOException("私有旧位置被其它内容占用。");
        var primary = RequirePublicationPrimary(operation, root); var paths = MmcInstancePaths.Resolve(root, primary.Definition.Layout.GameRootLeaf); var lease = access.Leases[root];
        RequireHints(operation, root, primary.Definition);
        string marker = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json");
        if (MmcInstancePaths.Attributes(marker) is null) {
            if (!operation.Completed()) throw new IOException("已移动目录没有精确标记或完成记录。");
            MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease); return new(MmcInstanceBackendStatus.Succeeded, paths, operation.DirectoryPath);
        }
        reservation.VerifyRecoveryMarker(root, true); using var permit = reservation.EnterPending(lease);
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        reservation.RestorePayloadFacts(paths, primary.Definition); ValidateRuntime(paths, primary.Definition);
        MmcConfigurationCoordinator.CompletePathRelocation(access.Quiet[operation.Owner.TemporaryRoot], access.Quiet[root], access.Leases[operation.Owner.TemporaryRoot], lease, operation.Owner.InstanceId, primary.Sha256);
        MmcConfigurationCoordinator.FlushRelocatedPreferences(access.Quiet[root], lease, operation.Owner.InstanceId, primary.Sha256);
        operation.Complete();
        string ignore = paths.ResolveRelative(MmcPathRole.Instance, ".pclignore"); if (File.Exists(ignore)) File.Delete(ignore); File.Delete(marker);
        return new(MmcInstanceBackendStatus.Succeeded, paths, operation.DirectoryPath);
    }
    private static MmcStoredPrimaryDefinition RequirePublicationPrimary(MmcImportOperation operation, string root) {
        var publication = operation.Publication() ?? throw new IOException("移动后的目录缺少发布封存依据。");
        var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(root, publication.Value.GameRootLeaf));
        if (primary.Definition.InstanceId != operation.Owner.InstanceId || primary.Definition.Generation != 1 || primary.Definition.ParentInstanceId is not null || primary.Definition.Origin.PackageSha256 != operation.Owner.PackageSha256 || primary.Sha256 != publication.Value.PrimarySha256)
            throw new IOException("已发布目标被替换或主定义字节变化，不能归属为原导入。");
        return primary;
    }
    private static void RequireHints(MmcImportOperation operation, string root, MmcInstanceDefinition definition) {
        var observed = MmcManagementReader.Evaluate(MmcManagementProbe.CaptureAtPhysicalRoot(root, Path.GetFileName(operation.Owner.FinalRoot)) with { HasPendingTransaction = false });
        if (observed.State != MmcManagementState.ManagedMmc || observed.Envelope?.InstanceId != definition.InstanceId || observed.Envelope.Generation != definition.Generation)
            throw new IOException("导入定义与剩余配置/版本投影归属不一致，不能清除标记。");
    }
    private sealed record InitialTransaction(Guid Id, bool Pending, bool Decided, string DefinitionSha256);
    private static List<InitialTransaction> InitialTransactions(MmcImportOperation operation, string root) {
        List<InitialTransaction> results = [];
        foreach (bool pending in new[] { true, false }) {
            string store = Path.Combine(root, "PCL", pending ? "MmcTransactions" : "MmcTransactionHistory"); MmcInstancePaths.EnsureNoLinks(store); if (!Directory.Exists(store)) continue;
            string[] directories = Directory.EnumerateDirectories(store).Take(65).ToArray(); if (directories.Length > 64) throw new IOException("初始事务数量超过恢复预算。");
            foreach (string directory in directories) {
                var prepared = MmcDurableFiles.ReadRecord<MmcPreparedJournal>(Path.Combine(directory, "prepared.record")); var value = prepared.Value;
                if (value.SchemaVersion != 1 || value.InstanceId != operation.Owner.InstanceId || value.CanonicalRoot != operation.Owner.TemporaryRoot || value.ExpectedGeneration != 0 || value.NewGeneration != 1 ||
                    !Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id) || id != value.TransactionId || value.Mutations is null || value.Mutations.Length > 100000) throw new IOException("存在非初始导入或未知事务，不能自动恢复。");
                var definition = value.Mutations.SingleOrDefault(mutation => mutation.RelativePath == "PCL/InstanceDefinition.json");
                if (definition is null || definition.OldSha256 is not null || !MmcDurableFiles.IsHash(definition.NewSha256)) throw new IOException("初始事务缺少新定义字节。");
                string decision = Path.Combine(directory, "CommitDecided.record"); bool decided = File.Exists(decision);
                if (decided && MmcImportOperation.ReadSmall<MmcTransactionMarker>(decision).Value != new MmcTransactionMarker(1, id, operation.Owner.InstanceId, prepared.Hash, "CommitDecided")) throw new IOException("事务决定不符合原准备记录。");
                results.Add(new(id, pending, decided, definition.NewSha256!));
            }
        }
        return results;
    }
    private static void ValidateRuntime(MmcInstancePaths paths, MmcInstanceDefinition definition) {
        var saved = MmcSavedSourceStore.LoadAsync(paths, definition).GetAwaiter().GetResult(); var env = definition.InstalledProjection!.RuntimeEnvironment ?? throw new IOException("新导入缺少已记录环境。");
        var result = MmcProfileCompiler.Compile(saved.Resolved.Resolution.Profile!, env); var plan = result.Profile;
        if (result.HasErrors || plan is null || plan.ProfileFingerprint != definition.InstalledProjection.ProfileFingerprint || plan.RuntimePlanFingerprint != definition.InstalledProjection.RuntimePlanFingerprint) throw new IOException("原来源不能重建已提交运行计划。");
        var active = plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).GroupBy(value => value.Key).Select(group => group.First()).ToImmutableArray();
        var declared = active.Where(value => value.Scope == MmcArtifactScope.InstanceLocal).Select(value => "libraries/" + value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preserved = MmcPackageFilePreservation.CreateLibraryArtifacts(saved.Baseline).Where(value => !declared.Contains("libraries/" + value.RelativePath)).ToImmutableArray();
        var request = new MmcArtifactRequest(paths, plan.RuntimePlanFingerprint, active, preserved, MmcPublishedArtifactLocks.Create(definition), MmcArtifactIntent.RepairExisting, plan.Assets);
        using var prepared = new MmcArtifactMaterializer().PrepareAsync(request).GetAwaiter().GetResult();
        if (!prepared.StagedMutations.IsEmpty) throw new IOException("恢复运行库缺失或变化，不能重新采用或覆盖这些字节。");
        var observations = MmcArtifactBindingAdapter.Create(request, plan, prepared);
        var bound = MmcPlanBinder.Bind(definition, plan, observations.Facts, observations.ApplyTo(new(1, 1, MmcBindingIntent.RepairExisting, definition.InstalledProjection.OutputJsonSha256, 1)),
            definition.ArtifactLocks, observations.DerivedOutputs, observations.AssetIndexes, publishedBaseline: definition, packageBaseline: saved.BaselineInput);
        if (bound.HasErrors || bound.Plan?.BoundFingerprint != definition.InstalledProjection.BoundFingerprint) throw new IOException("恢复字节不符合原输入锁和绑定计划。");
    }
    internal static string Fingerprint(MmcImportOperation operation) => MmcDurableFiles.Hash(Encoding.UTF8.GetBytes(string.Join("\n", "mmc-import-recovery-v1", operation.OwnerHash,
        operation.Publication()?.Hash, operation.Completed(), operation.RolledBack(), Observed(operation.Owner.TemporaryRoot), Observed(operation.Owner.FinalRoot))));
    private static string Observed(string root) {
        if (MmcInstancePaths.Attributes(root) is null) return "missing";
        var paths = MmcInstancePaths.Resolve(root); List<string?> facts = [root];
        foreach (string relative in new[] { "PCL/MmcImportPending.json", ".pclignore", "PCL/InstanceDefinition.json", "PCL/config.json", "PCL/MmcImportPayload.json" }) {
            string path = paths.ResolveRelative(MmcPathRole.Instance, relative); if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException("导入预览文件超过预算。"); facts.Add(MmcDurableFiles.Observe(path));
        }
        return string.Join("\n", facts);
    }
    private sealed class Access : IDisposable {
        internal readonly Dictionary<string, MmcConfigurationQuiescence> Quiet = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, MmcAdmissionLease> Leases = new(StringComparer.OrdinalIgnoreCase);
        internal Access(MmcImportOperation operation) {
            try { foreach (string root in new[] { operation.Owner.TemporaryRoot, operation.Owner.FinalRoot }.Order(StringComparer.OrdinalIgnoreCase)) Quiet.Add(root, MmcConfigurationCoordinator.Quiesce(root));
                foreach (string root in Quiet.Keys) Leases.Add(root, MmcAdmissionGate.Acquire(root, operation.Owner.InstanceId, TimeSpan.Zero)); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var lease in Leases.Values.Reverse()) lease.Dispose(); foreach (var quiet in Quiet.Values.Reverse()) quiet.Dispose(); }
    }
    public static void RequireLegacyGameFolderSafe(string minecraftRoot, string operation) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftRoot));
        string[] stores = Path.GetFileName(root).Equals("PCL", StringComparison.OrdinalIgnoreCase)
            ? [Path.Combine(root, MmcImportOperation.StoreName), Path.Combine(root, "PCL", MmcImportOperation.StoreName)]
            : [Path.Combine(root, "PCL", MmcImportOperation.StoreName)];
        foreach (string store in stores) {
            if (MmcInstancePaths.Attributes(store) is null) continue;
            MmcInstancePaths.EnsureNoLinks(store);
            if (!Directory.Exists(store) || Directory.EnumerateFileSystemEntries(store).Any())
                throw new InvalidOperationException(operation + "不能通过旧目录清理删除：该目录含有 MMC 导入恢复记录，请先处理这些私有数据。");
        }
    }
    private static string Root(string value) { string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); MmcInstancePaths.EnsureNoLinks(root); return root; }
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or OverflowException or PlatformNotSupportedException;
}
