using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcLifecycleCatalogState { Quarantined, Incomplete, Completed, Conflict, Unknown, Purging, Purged }
public sealed record MmcLifecycleCatalogEntry(string MinecraftRoot, string OperationDirectory, string Name,
    MmcLifecycleCatalogState State, string Description, Guid? InstanceId = null, long? Generation = null,
    string? SnapshotFingerprint = null, bool CanRestore = false, bool CanRecover = false) {
    public bool CanPurge { get; init; }
}
public sealed record MmcLifecycleCatalog(ImmutableArray<MmcLifecycleCatalogEntry> Entries, bool Truncated);

/// <summary>Read-only, bounded discovery. Only Execute revalidates an exact preview and invokes the existing guarded backend.</summary>
public static partial class MmcInstanceLifecycleCatalog {
    private const int MaximumEntries = 256;
    private const long MaximumScanBytes = 64 * 1024 * 1024;
    private const long MaximumRecordBytes = 64 * 1024;
    private static readonly string[] Records = ["intent.record", "transaction.record", "CommitDecided.record", "completed.record",
        "restore.record", "RestoreCommitDecided.record", "restored.record", MmcInstancePurgeJournal.PlanName,
        MmcInstancePurgeJournal.DecisionName, MmcInstancePurgeJournal.CompletionName];
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Compatibility preview only. Interactive callers use ReadPage and retain its cursors.
    public static MmcLifecycleCatalog Read(string minecraftRoot) {
        var page = ReadPage(new(minecraftRoot, PageSize: MaximumEntries));
        return new(page.Entries, page.Next is not null || page.NeedsRefresh);
    }

    /// <summary>Background-only. Pins the immutable intent against replacement while the backend takes its own quiescence/admission gates.</summary>
    public static MmcInstanceLifecycleResult Execute(MmcLifecycleCatalogEntry preview, bool restore, CancellationToken cancellationToken = default,
        Action<MmcLifecycleCheckpoint, string>? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(preview);
        string root = Root(preview.MinecraftRoot), directory = ValidateDirectory(root, preview.OperationDirectory);
        // FileShare.Read intentionally denies writes/deletion. The intent never moves with
        // the instance, and remains pinned through the backend's final identity checks.
        using var intentPin = Pin(Path.Combine(directory, "intent.record"), MaximumRecordBytes, out _);
        long remaining = MaximumScanBytes;
        var fresh = Inspect(root, directory, ref remaining, cancellationToken);
        if (preview.SnapshotFingerprint is null || fresh.SnapshotFingerprint != preview.SnapshotFingerprint ||
            preview.InstanceId != fresh.InstanceId || preview.Generation != fresh.Generation ||
            (restore ? !fresh.CanRestore : !fresh.CanRecover))
            throw new IOException("操作记录或实例已变化，请重新打开 MMC 隔离区与恢复列表。");
        cancellationToken.ThrowIfCancellationRequested();
        var backend = new MmcInstanceLifecycleBackend();
        if (restore) return backend.RestoreDeletedAsync(directory, cancellationToken, checkpoint).GetAwaiter().GetResult();
        // Recovery replays an existing decision (or rolls back an undecided intent).
        // Once replay begins it must finish or retain a recoverable conflict.
        checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDecision, directory);
        return backend.RecoverAsync(directory).GetAwaiter().GetResult();
    }

    private static MmcLifecycleCatalogEntry Inspect(string root, string directory, ref long remaining, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        directory = ValidateDirectory(root, directory);
        List<FileStream> pins = []; List<MmcAdmissionLease> leases = []; List<string> recordFacts = [];
        try {
            // Pin only the immutable store record before taking the read gates.
            // A catalog must not briefly deny a live producer's Move/ClearPending
            // by opening instance files while that producer holds admission.
            PinOptional(Path.Combine(directory, "intent.record"), MaximumRecordBytes, pins, ref remaining);
            var journal = MmcInstanceLifecycleJournal.Read(directory, cancellationToken); var intent = journal.Intent;
            foreach (string location in new[] { intent.SourceRoot, intent.TargetRoot }.Order(StringComparer.OrdinalIgnoreCase)) {
                cancellationToken.ThrowIfCancellationRequested();
                leases.Add(MmcAdmissionGate.Acquire(location, intent.InstanceId, TimeSpan.Zero));
            }
            foreach (string record in Records) {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(directory, record);
                if (MmcInstancePaths.Attributes(path) is null) continue;
                var pin = Pin(path, record == MmcInstancePurgeJournal.PlanName ? MmcInstancePurgeSnapshot.MaximumRecordBytes + 128L : MaximumRecordBytes, out long length); pins.Add(pin); Budget(ref remaining, length);
                recordFacts.Add(record); recordFacts.Add(HashRecord(pin, cancellationToken)); pin.Position = 0;
            }
            string name = Path.GetFileName(intent.SourceRoot);
            // A durable purge decision supersedes restoration even when the primary or
            // lifecycle marker was already removed. Never run ordinary primary probing first.
            if (MmcInstancePurgeJournal.HasDecision(directory) || MmcInstancePaths.Attributes(Path.Combine(directory, MmcInstancePurgeJournal.CompletionName)) is not null) {
                var purge = MmcInstancePurgeJournal.Read(journal, cancellationToken);
                if (!purge.IsDecided()) throw new IOException("永久清理决定无法验证，已保留所有剩余内容。");
                bool exists = MmcInstancePaths.Attributes(intent.TargetRoot) is not null, completedPurge = purge.IsCompleted();
                string identity = "";
                if (exists) {
                    using var rootPin = MmcPurgeHandle.Open(intent.TargetRoot, true); identity = rootPin.Identity;
                    if (identity != purge.RootEntry.FileIdentity || completedPurge)
                        return new(root, directory, name, MmcLifecycleCatalogState.Conflict, "永久清理路径被新内容占用；不会删除这些内容。", intent.InstanceId, intent.SourceGeneration);
                }
                string purgeFingerprint = MmcDurableFiles.Hash(Encoding.UTF8.GetBytes("mmc-purge-catalog-v1\n" + purge.PlanSha256 + "\n" + exists + "\n" + identity + "\n" + string.Join("\n", recordFacts)));
                return new(root, directory, name, completedPurge ? MmcLifecycleCatalogState.Purged : MmcLifecycleCatalogState.Purging,
                    completedPurge ? "已明确永久清除该实例的私有内容；无法还原。" : "永久清理已决定，尚未完成；只能继续清理，不能还原完整实例。",
                    intent.InstanceId, intent.SourceGeneration, purgeFingerprint, CanRecover: !completedPurge);
            }
            bool sourceExists = MmcInstancePaths.Attributes(intent.SourceRoot) is not null;
            bool targetExists = MmcInstancePaths.Attributes(intent.TargetRoot) is not null;
            foreach (string location in new[] { intent.SourceRoot, intent.TargetRoot })
                PinOptional(Path.Combine(location, MmcInstanceLifecycleJournal.MarkerRelative.Replace('/', Path.DirectorySeparatorChar)), 8192, pins, ref remaining);
            bool sourceOwned = journal.Owns(intent.SourceRoot), targetOwned = journal.Owns(intent.TargetRoot);
            string? sourceHash = null, targetHash = null;
            if (sourceExists) sourceHash = Primary(intent.SourceRoot, intent.InstanceId, [intent.SourceGeneration, intent.TargetGeneration], pins, ref remaining, cancellationToken);
            if (targetExists) targetHash = Primary(intent.TargetRoot, intent.InstanceId, [intent.SourceGeneration, intent.TargetGeneration], pins, ref remaining, cancellationToken);
            string intentHash = journal.IntentSha256;
            string restorePath = Path.Combine(directory, "restore.record");
            if (MmcInstancePaths.Attributes(restorePath) is not null) {
                var restore = MmcDurableFiles.ReadRecord<MmcLifecycleRestore>(restorePath, cancellationToken).Value;
                if (intent.Kind != "Delete" || restore.SchemaVersion != 1 || restore.RestoreId == Guid.Empty ||
                    restore.DeleteOperationId != intent.OperationId || restore.DeleteIntentSha256 != intentHash) throw new IOException("恢复操作记录不匹配。");
            }
            var completed = journal.Completion(); bool restoreDecided = journal.HasRestoreDecision(), restored = journal.IsRestored();
            bool decided = intent.Kind == "Delete" ? journal.HasDeleteDecision() : StructuralDecision(journal,
                sourceOwned ? intent.SourceRoot : targetExists ? intent.TargetRoot : sourceExists ? intent.SourceRoot : null, pins, ref remaining);
            string fingerprint = MmcDurableFiles.Hash(Encoding.UTF8.GetBytes(string.Join("\n", "mmc-lifecycle-catalog-v1", intentHash,
                sourceExists, targetExists, sourceOwned, targetOwned, sourceHash, targetHash,
                completed?.Disposition, decided, restoreDecided, restored) + "\n" + string.Join("\n", recordFacts)));
            MmcLifecycleCatalogEntry Entry(MmcLifecycleCatalogState state, string description, bool restore = false, bool recover = false) =>
                new(root, directory, name, state, description, intent.InstanceId, intent.SourceGeneration, fingerprint, restore, recover);
            if (sourceHash is not null && sourceHash != intent.SourcePrimarySha256 && sourceHash != intent.TargetPrimarySha256 ||
                targetHash is not null && targetHash != (intent.TargetPrimarySha256 ?? intent.SourcePrimarySha256))
                return Entry(MmcLifecycleCatalogState.Conflict, "实例内容与原操作记录不一致；不会替换这些内容。");
            if (!decided && (targetOwned || sourceHash is not null && sourceHash == intent.TargetPrimarySha256))
                return Entry(MmcLifecycleCatalogState.Conflict, "移动后的内容缺少可验证的提交决定，已保留现场。");
            if (restored) {
                if (sourceHash != intent.SourcePrimarySha256 || targetExists)
                    return Entry(MmcLifecycleCatalogState.Conflict, "恢复后的实例位置或内容已变化；所有目录均已保留。");
                if (sourceOwned) return Entry(MmcLifecycleCatalogState.Incomplete, "实例已移回原位置，尚需完成恢复收尾。", recover: true);
                if (MmcInstancePaths.Attributes(Path.Combine(intent.SourceRoot, MmcInstanceLifecycleJournal.MarkerRelative.Replace('/', Path.DirectorySeparatorChar))) is not null)
                    return Entry(MmcLifecycleCatalogState.Conflict, "原位置存在其它操作标记，不能代为清理；内容已保留。");
                return Entry(MmcLifecycleCatalogState.Completed, "已恢复到原来的版本目录。");
            }
            if (sourceExists && targetExists || sourceOwned && targetOwned)
                return Entry(MmcLifecycleCatalogState.Conflict, "原位置与目标位置同时有内容；不会覆盖任何实例。");
            if (restoreDecided) return sourceOwned || targetOwned
                ? Entry(MmcLifecycleCatalogState.Incomplete, "恢复操作尚未完成，可继续恢复。", recover: true)
                : Entry(MmcLifecycleCatalogState.Conflict, "恢复操作的实例位置无法确认；目录已保留。");
            if (completed?.Disposition == "Quarantined") {
                if (!decided || !targetOwned || targetHash != intent.SourcePrimarySha256 || !MmcInstanceLifecycleVisibility.IsCompletedQuarantine(intent.TargetRoot))
                    return Entry(MmcLifecycleCatalogState.Conflict, "隔离区内容或归属已变化，不能自动恢复。");
                if (MmcInstancePurgeJournal.HasPlan(directory)) {
                    var preparation = MmcInstancePurgeJournal.Read(journal, cancellationToken);
                    if (preparation.IsDecided()) throw new IOException("永久清理状态在列表读取期间发生变化，请重新打开列表。");
                    return Entry(MmcLifecycleCatalogState.Quarantined, "永久清理在决定前中断，实例内容仍完整；可撤销准备并恢复隔离区操作。", restore: true, recover: true);
                }
                return Entry(MmcLifecycleCatalogState.Quarantined, "可无覆盖地恢复到原来的版本名称；隔离区仍占用磁盘空间。", restore: true) with { CanPurge = !MmcInstancePurgeJournal.HasPlan(directory) };
            }
            if (completed is not null) {
                bool moved = completed.Disposition == "Moved";
                bool matches = moved ? targetHash == intent.TargetPrimarySha256 && decided : sourceHash == intent.SourcePrimarySha256 && !decided;
                if (!matches) return Entry(MmcLifecycleCatalogState.Conflict, "已完成操作的位置或内容已变化；不会归属到其它实例。");
                return sourceOwned || targetOwned ? Entry(MmcLifecycleCatalogState.Incomplete, "操作已完成，尚需清理原操作标记。", recover: true)
                    : Entry(MmcLifecycleCatalogState.Completed, moved ? "改名或移动已完成。" : "操作已撤回，原实例已保留。");
            }
            if (sourceOwned || targetOwned || sourceHash == intent.SourcePrimarySha256 && !targetExists)
                return Entry(MmcLifecycleCatalogState.Incomplete, decided ? "操作已决定提交，可继续完成。" : "操作在提交前中断，可恢复原实例。", recover: true);
            return Entry(MmcLifecycleCatalogState.Conflict, "未找到与记录完全一致的实例；请保留现场。");
        } finally {
            foreach (var pin in pins) pin.Dispose();
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
        }
    }

    private static string Primary(string versionRoot, Guid id, long[] generations, List<FileStream> pins, ref long remaining, CancellationToken cancellationToken) {
        var paths = MmcInstancePaths.Resolve(versionRoot);
        var pin = Pin(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json"), 16 * 1024 * 1024, out long length);
        pins.Add(pin); Budget(ref remaining, length);
        var primary = MmcSavedSourceStore.ReadPrimary(paths, cancellationToken);
        if (primary.Definition.InstanceId != id || !generations.Contains(primary.Definition.Generation))
            throw new IOException("该位置属于另一个实例或不同代数，已保留其内容。");
        return primary.Sha256;
    }
    private static bool StructuralDecision(MmcInstanceLifecycleJournal journal, string? location, List<FileStream> pins, ref long remaining) {
        string linkPath = Path.Combine(journal.DirectoryPath, "transaction.record");
        // Before the Prepared callback binds this link there cannot be a valid
        // structural decision. Avoid scanning arbitrary transaction directories.
        if (location is null || MmcInstancePaths.Attributes(linkPath) is null) return false;
        var link = MmcDurableFiles.ReadRecord<MmcLifecycleTxLink>(linkPath).Value;
        if (link.TransactionId == Guid.Empty || !MmcDurableFiles.IsHash(link.PreparedSha256)) throw new IOException("结构事务记录损坏。");
        var paths = MmcInstancePaths.Resolve(location);
        string transaction = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactions/" + link.TransactionId.ToString("N"));
        if (!Directory.Exists(transaction)) transaction = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactionHistory/" + link.TransactionId.ToString("N"));
        PinOptional(Path.Combine(transaction, "prepared.record"), MaximumRecordBytes, pins, ref remaining);
        PinOptional(Path.Combine(transaction, "CommitDecided.record"), MaximumRecordBytes, pins, ref remaining);
        return journal.HasStructuralDecision(location);
    }
    private static void PinOptional(string path, long maximum, List<FileStream> pins, ref long remaining) {
        if (MmcInstancePaths.Attributes(path) is null) return;
        var pin = Pin(path, maximum, out long length); pins.Add(pin); Budget(ref remaining, length);
    }
    private static FileStream Pin(string path, long maximum, out long length) {
        MmcInstancePaths.EnsureNoLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        length = stream.Length;
        if (length > maximum) { stream.Dispose(); throw new IOException("操作记录或实例定义超过读取预算。"); }
        return stream;
    }
    private sealed class CatalogPageBudgetException() : IOException("本页达到读取预算；此记录将从下一页继续读取。") { }
    private static void Budget(ref long remaining, long length) {
        if (length > remaining) throw new CatalogPageBudgetException();
        remaining -= length;
    }
    private static string HashRecord(FileStream stream, CancellationToken cancellationToken) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; int count;
        do {
            cancellationToken.ThrowIfCancellationRequested();
            count = stream.Read(buffer, 0, buffer.Length);
            hash.AppendData(buffer, 0, count);
        } while (count != 0);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string Root(string root) { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); MmcInstancePaths.EnsureNoLinks(root); return root; }
    private static string ValidateDirectory(string root, string directory) {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string store = Path.Combine(root, "PCL", "MmcLifecycle");
        if (!string.Equals(Path.GetDirectoryName(directory), store, PathComparison) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id) || id == Guid.Empty)
            throw new IOException("不是当前游戏文件夹中的有效操作记录目录。");
        MmcInstancePaths.EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) throw new IOException("操作记录目录不存在或被文件占用。");
        return directory;
    }
    private static MmcLifecycleCatalogEntry Unknown(string root, string path, string reason) => new(root, path, "无法识别的恢复记录", MmcLifecycleCatalogState.Unknown,
        "记录无法完整验证，已保留现场：" + reason);
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or DecoderFallbackException or OverflowException;
}
