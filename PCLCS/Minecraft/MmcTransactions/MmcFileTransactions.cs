using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>
/// Recoverable cooperative file publication. This is not a CAS against arbitrary external programs,
/// nor a claim about power-loss atomicity. Every actual replaced byte sequence is retained and checked.
/// </summary>
public static class MmcFileTransactions {
    private const string DefinitionPath = "PCL/InstanceDefinition.json";
    private const int MaximumMutations = 100000;
    private const long MaximumStagedBytes = 64L * 1024 * 1024 * 1024;

    public static MmcTransactionResult Execute(MmcAdmissionLease lease, MmcFileTransactionRequest request,
        CancellationToken cancellationToken = default, Action<MmcTransactionCheckpoint, int>? checkpoint = null) {
        lease.AssertHeld(); ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId != lease.InstanceId) throw new IOException("事务身份与准入租约不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        AssertNoPending(lease); AssertNoSessions(paths);
        MmcFileMutation[] mutations = ValidateRequest(lease, paths, request);
        Guid id = Guid.NewGuid(); string directory = TransactionDirectory(paths, id);
        Directory.CreateDirectory(directory);
        MmcPreparedJournal journal = new(1, id, request.InstanceId, paths.VersionRoot, request.ExpectedGeneration, request.NewGeneration,
            mutations.Select(m => new MmcJournalMutation(m.RelativePath, m.ExpectedOldSha256, m.ExpectedNewSha256)).ToArray());
        bool prepared = false, decided = false;
        try {
            long staged = 0;
            for (int i = 0; i < mutations.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                MmcFileMutation mutation = mutations[i]; string target = Target(paths, mutation.RelativePath);
                if (MmcDurableFiles.Observe(target) != mutation.ExpectedOldSha256) throw new IOException("事务前置旧摘要已变化。");
                if (mutation.ExpectedOldSha256 is not null) {
                    CheckStagedBudget(staged, new FileInfo(target).Length);
                    staged = checked(staged + MmcDurableFiles.CopyNew(target, ObjectFile(directory, i, false), mutation.ExpectedOldSha256, cancellationToken));
                    CheckStagedBudget(staged, 0);
                }
                if (mutation.NewContent is not null) {
                    staged = checked(staged + mutation.NewContent.LongLength);
                    if (staged > MaximumStagedBytes) throw new IOException("事务暂存总量超过预算。");
                    MmcDurableFiles.WriteNew(ObjectFile(directory, i, true), mutation.NewContent);
                    if (MmcDurableFiles.Observe(ObjectFile(directory, i, true)) != mutation.ExpectedNewSha256)
                        throw new IOException("新制品摘要不符合锁定记录，禁止重采用。");
                } else if (mutation.NewSourceRelativePath is not null) {
                    string source = StagedSource(paths, mutation.NewSourceRelativePath);
                    if (MmcDurableFiles.Observe(source) != mutation.ExpectedNewSha256) throw new IOException("文件来源摘要不符合锁定内容。");
                    CheckStagedBudget(staged, new FileInfo(source).Length);
                    staged = checked(staged + MmcDurableFiles.CopyNew(source, ObjectFile(directory, i, true), mutation.ExpectedNewSha256!, cancellationToken));
                    CheckStagedBudget(staged, 0);
                }
            }
            ValidateStaged(journal, directory);
            string preparedHash = MmcDurableFiles.WriteRecord(Path.Combine(directory, "prepared.record"), journal);
            WriteMarker(directory, journal, preparedHash, "Pending");
            prepared = true; checkpoint?.Invoke(MmcTransactionCheckpoint.Prepared, -1);
            // Caller changes to mutable request buffers after this point cannot change staged bytes.
            for (int i = 0; i < journal.Mutations.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                Apply(paths, directory, journal.Mutations[i], i, forward: true, checkpoint);
                checkpoint?.Invoke(MmcTransactionCheckpoint.AfterMutation, i);
            }
            ValidateTargets(paths, journal, forward: true);
            checkpoint?.Invoke(MmcTransactionCheckpoint.BeforeDecision, -1);
            cancellationToken.ThrowIfCancellationRequested();
            WriteMarker(directory, journal, preparedHash, "CommitDecided"); decided = true;
            checkpoint?.Invoke(MmcTransactionCheckpoint.CommitDecided, -1);
            // Cancellation after the durable decision completes the commit rather than reporting cancel.
            return Finish(paths, directory, journal, preparedHash, committed: true, checkpoint);
        } catch (MmcTransactionInterruptionException ex) {
            return new(MmcTransactionOutcome.Interrupted, id, ex.Message);
        } catch (Exception ex) when (IsFailure(ex)) {
            if (!prepared) {
                // No live I/O has been attempted. Retain even partially staged bytes in owned history.
                try { Archive(paths, directory, id); return new(MmcTransactionOutcome.Aborted, id, ex.Message); }
                catch (Exception archiveFailure) when (IsFailure(archiveFailure)) { return new(MmcTransactionOutcome.RecoveryRequired, id, ex.Message + " " + archiveFailure.Message); }
            }
            MmcTransactionResult recovered = RecoverCore(lease, id);
            return recovered.Outcome == MmcTransactionOutcome.RecoveryRequired ? recovered :
                new(decided || recovered.Outcome == MmcTransactionOutcome.Committed ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, id, ex.Message);
        }
    }

    public static MmcTransactionResult Recover(MmcAdmissionLease lease, Guid transactionId) {
        lease.AssertHeld(); if (transactionId == Guid.Empty) throw new ArgumentException("事务 ID 不能为空。");
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        AssertNoSessions(MmcInstancePaths.Resolve(lease.VersionRoot));
        return RecoverCore(lease, transactionId);
    }

    public static IReadOnlyList<MmcTransactionResult> RecoverPending(MmcAdmissionLease lease) {
        lease.AssertHeld(); MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        AssertNoSessions(paths);
        string root = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactions");
        if (!Directory.Exists(root)) return Array.Empty<MmcTransactionResult>();
        List<MmcTransactionResult> results = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(root).Take(MaximumMutations + 1)) {
            if (results.Count == MaximumMutations) throw new IOException("待恢复事务超过预算。");
            if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid id))
                results.Add(new(MmcTransactionOutcome.RecoveryRequired, Guid.Empty, "存在身份不明的事务条目，保留原样。"));
            else results.Add(RecoverCore(lease, id));
        }
        return results.AsReadOnly();
    }

    public static void AssertNoPending(MmcAdmissionLease lease) {
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        AssertNoTransactionsOnly(lease);
        MmcCommandOperations.AssertNoPending(lease);
    }

    public static void AssertNoTransactionsOnly(MmcAdmissionLease lease) {
        lease.AssertHeld(); string root = MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactions");
        if (MmcInstancePaths.Attributes(root) is not null && (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any()))
            throw new IOException("存在待恢复 MMC 事务。");
    }

    private static MmcTransactionResult RecoverCore(MmcAdmissionLease lease, Guid id) {
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        string directory = TransactionDirectory(paths, id);
        try {
            if (!Directory.Exists(directory)) {
                string archived = HistoryDirectory(paths, id);
                if (!Directory.Exists(archived)) return new(MmcTransactionOutcome.RecoveryRequired, id, "找不到事务记录，不能猜测结果。");
                (var previous, string previousHash) = MmcDurableFiles.ReadRecord<MmcPreparedJournal>(Path.Combine(archived, "prepared.record"));
                ValidateJournal(lease, paths, previous, id);
                MmcTransactionMarker marker = ReadMarker(archived, previous, previousHash, "Finalized");
                return new(marker.Phase == "FinalizedCommitted" ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, id);
            }
            (var journal, string preparedHash) = MmcDurableFiles.ReadRecord<MmcPreparedJournal>(Path.Combine(directory, "prepared.record"));
            ValidateJournal(lease, paths, journal, id); ValidateStaged(journal, directory); ValidateBackups(journal, directory);
            ReadMarker(directory, journal, preparedHash, "Pending");
            bool decided = MmcInstancePaths.Attributes(Path.Combine(directory, "CommitDecided.record")) is not null;
            if (decided) ReadMarker(directory, journal, preparedHash, "CommitDecided");
            if (MmcInstancePaths.Attributes(Path.Combine(directory, "Finalized.record")) is not null) {
                MmcTransactionMarker final = ReadMarker(directory, journal, preparedHash, "Finalized");
                if ((final.Phase == "FinalizedCommitted") != decided) throw new IOException("事务完成状态与提交决定冲突。");
            }
            if (decided) {
                for (int i = 0; i < journal.Mutations.Length; i++) Apply(paths, directory, journal.Mutations[i], i, true, null);
            } else {
                for (int i = journal.Mutations.Length - 1; i >= 0; i--) Apply(paths, directory, journal.Mutations[i], i, false, null);
            }
            ValidateTargets(paths, journal, decided);
            return Finish(paths, directory, journal, preparedHash, decided, null);
        } catch (Exception ex) when (IsFailure(ex)) { return new(MmcTransactionOutcome.RecoveryRequired, id, ex.Message); }
    }

    private static MmcFileMutation[] ValidateRequest(MmcAdmissionLease lease, MmcInstancePaths paths, MmcFileTransactionRequest request) {
        if (request.ExpectedGeneration < 0 || request.NewGeneration < 1 || request.ExpectedGeneration == long.MaxValue || request.NewGeneration != request.ExpectedGeneration + 1)
            throw new IOException("事务必须发布下一代定义。");
        if (request.Mutations is null || request.Mutations.Count == 0 || request.Mutations.Count > MaximumMutations) throw new IOException("事务文件数量无效。");
        MmcFileMutation[] mutations = request.Mutations.ToArray(); HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (MmcFileMutation mutation in mutations) {
            if (mutation is null) throw new IOException("事务包含空修改。");
            string normalized = NormalizeRelative(mutation.RelativePath);
            if (normalized != mutation.RelativePath || !targets.Add(Target(paths, normalized))) throw new IOException("事务目标路径不规范或重复。");
            if (mutation.ExpectedOldSha256 is not null && !MmcDurableFiles.IsHash(mutation.ExpectedOldSha256)) throw new IOException("旧摘要无效。");
            bool hasNew = mutation.NewContent is not null || mutation.NewSourceRelativePath is not null;
            if (mutation.NewContent is not null && mutation.NewSourceRelativePath is not null || hasNew != (mutation.ExpectedNewSha256 is not null) || !hasNew && mutation.ExpectedOldSha256 is null)
                throw new IOException("事务修改缺少明确的新旧内容。");
            if (mutation.NewContent is not null && (!MmcDurableFiles.IsHash(mutation.ExpectedNewSha256) || MmcDurableFiles.Hash(mutation.NewContent) != mutation.ExpectedNewSha256))
                throw new IOException("新内容不符合已声明摘要。");
            if (mutation.NewSourceRelativePath is not null && (!MmcDurableFiles.IsHash(mutation.ExpectedNewSha256) ||
                MmcDurableFiles.Observe(StagedSource(paths, mutation.NewSourceRelativePath)) != mutation.ExpectedNewSha256))
                throw new IOException("文件来源不符合已声明摘要。");
        }
        MmcFileMutation definition = mutations.SingleOrDefault(m => string.Equals(m.RelativePath, DefinitionPath, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("事务必须包含新代数的实例定义。");
        if (definition.RelativePath != DefinitionPath || definition.NewContent is null) throw new IOException("定义目标必须使用固定路径且不能删除。");
        ValidateDefinition(definition.NewContent, lease.InstanceId, request.NewGeneration);
        string definitionFile = Target(paths, DefinitionPath);
        if (request.ExpectedGeneration == 0) {
            if (MmcDurableFiles.Observe(definitionFile) is not null || definition.ExpectedOldSha256 is not null) throw new IOException("初始定义已存在。");
        } else {
            if (MmcDurableFiles.Observe(definitionFile) != definition.ExpectedOldSha256 || definition.ExpectedOldSha256 is null) throw new IOException("旧定义摘要不一致。");
            ValidateDefinition(ReadSmall(definitionFile), lease.InstanceId, request.ExpectedGeneration);
        }
        return mutations;
    }

    private static void ValidateJournal(MmcAdmissionLease lease, MmcInstancePaths paths, MmcPreparedJournal journal, Guid id) {
        if (journal.SchemaVersion != 1 || journal.TransactionId != id || journal.InstanceId != lease.InstanceId ||
            !string.Equals(journal.CanonicalRoot, paths.VersionRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            journal.ExpectedGeneration < 0 || journal.ExpectedGeneration == long.MaxValue || journal.NewGeneration != journal.ExpectedGeneration + 1 ||
            journal.Mutations is null || journal.Mutations.Length == 0 || journal.Mutations.Length > MaximumMutations)
            throw new IOException("事务日志版本、归属或代数不受支持。");
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in journal.Mutations) {
            if (mutation is null || mutation.RelativePath != NormalizeRelative(mutation.RelativePath) || !targets.Add(Target(paths, mutation.RelativePath)) ||
                mutation.OldSha256 is not null && !MmcDurableFiles.IsHash(mutation.OldSha256) ||
                mutation.NewSha256 is not null && !MmcDurableFiles.IsHash(mutation.NewSha256) || mutation.OldSha256 is null && mutation.NewSha256 is null)
                throw new IOException("事务目标或摘要无效。");
        }
        int definitionIndex = Array.FindIndex(journal.Mutations, m => m.RelativePath == DefinitionPath);
        if (definitionIndex < 0 || journal.Mutations[definitionIndex].NewSha256 is null) throw new IOException("事务日志缺少新定义。");
    }

    private static void ValidateStaged(MmcPreparedJournal journal, string directory) {
        for (int i = 0; i < journal.Mutations.Length; i++) {
            var mutation = journal.Mutations[i];
            if (MmcDurableFiles.Observe(ObjectFile(directory, i, false)) != mutation.OldSha256 ||
                MmcDurableFiles.Observe(ObjectFile(directory, i, true)) != mutation.NewSha256) throw new IOException("事务旧/新对象损坏，不接受替代字节。");
        }
        int index = Array.FindIndex(journal.Mutations, m => m.RelativePath == DefinitionPath);
        ValidateDefinition(ReadSmall(ObjectFile(directory, index, true)), journal.InstanceId, journal.NewGeneration);
        if (journal.ExpectedGeneration != 0) ValidateDefinition(ReadSmall(ObjectFile(directory, index, false)), journal.InstanceId, journal.ExpectedGeneration);
    }

    private static void ValidateBackups(MmcPreparedJournal journal, string directory) {
        foreach (string path in Directory.EnumerateFiles(directory, "backup-*.bin")) {
            string[] pieces = Path.GetFileNameWithoutExtension(path).Split('-');
            if (pieces.Length != 3 || !int.TryParse(pieces[1], out int index) || index < 0 || index >= journal.Mutations.Length || !Guid.TryParseExact(pieces[2], "N", out _))
                throw new IOException("事务备份身份不明。");
            string? hash = MmcDurableFiles.Observe(path); var mutation = journal.Mutations[index];
            if (hash is null || hash != mutation.OldSha256 && hash != mutation.NewSha256) throw new IOException("发现第三方换出内容，已保留所有副本并停止恢复。");
        }
    }

    private static void Apply(MmcInstancePaths paths, string directory, MmcJournalMutation mutation, int index, bool forward,
        Action<MmcTransactionCheckpoint, int>? checkpoint) {
        string target = Target(paths, mutation.RelativePath);
        string? wanted = forward ? mutation.NewSha256 : mutation.OldSha256;
        string? other = forward ? mutation.OldSha256 : mutation.NewSha256;
        string? actual = MmcDurableFiles.Observe(target);
        if (actual == wanted) return;
        if (actual != other) throw new IOException("目标含第三方内容或无法证明的缺失，停止恢复。");
        if (wanted is not null && actual is null && !forward && mutation.OldSha256 is not null && mutation.NewSha256 is null && !HasBackup(directory, index, mutation.OldSha256))
            throw new IOException("没有实际删除备份，不能把未知缺失当作本事务删除。");
        // An existing file replacement cannot cooperatively produce an absent target. Only declared
        // absent-old creations and recoverably moved deletions authorize absent-target publication.
        if (actual is null && mutation.OldSha256 is not null && mutation.NewSha256 is not null)
            throw new IOException("既存目标意外丢失，保留现场。");
        string backup = Path.Combine(directory, "backup-" + index + "-" + Guid.NewGuid().ToString("N") + ".bin");
        string? install = null;
        if (wanted is not null) {
            install = Path.Combine(directory, "install-" + Guid.NewGuid().ToString("N") + ".bin");
            MmcDurableFiles.CopyNew(ObjectFile(directory, index, forward), install, wanted);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); MmcInstancePaths.EnsureNoLinks(target);
        }
        // The callback intentionally sits after the final hash observation to exercise external races.
        checkpoint?.Invoke(MmcTransactionCheckpoint.BeforeMutation, index);
        if (actual is not null) {
            if (install is null) File.Move(target, backup, false);
            else File.Replace(install, target, backup, false);
            if (MmcDurableFiles.Observe(backup) != actual) throw new IOException("替换时换出了第三方内容，已保留备份并停止。");
        } else {
            if (install is null) return;
            File.Move(install, target, false);
        }
        if (MmcDurableFiles.Observe(target) != wanted) throw new IOException("落地后的目标摘要不匹配，保留现场。");
    }

    private static bool HasBackup(string directory, int index, string hash) =>
        Directory.EnumerateFiles(directory, "backup-" + index + "-*.bin").Any(p => MmcDurableFiles.Observe(p) == hash);
    private static void ValidateTargets(MmcInstancePaths paths, MmcPreparedJournal journal, bool forward) {
        foreach (var mutation in journal.Mutations)
            if (MmcDurableFiles.Observe(Target(paths, mutation.RelativePath)) != (forward ? mutation.NewSha256 : mutation.OldSha256))
                throw new IOException("事务最终目标摘要不一致。");
    }
    private static MmcTransactionResult Finish(MmcInstancePaths paths, string directory, MmcPreparedJournal journal, string hash, bool committed,
        Action<MmcTransactionCheckpoint, int>? checkpoint) {
        ValidateBackups(journal, directory); ValidateTargets(paths, journal, committed);
        checkpoint?.Invoke(MmcTransactionCheckpoint.BeforeFinalize, -1);
        string finalized = Path.Combine(directory, "Finalized.record");
        if (MmcInstancePaths.Attributes(finalized) is null) WriteMarker(directory, journal, hash, committed ? "FinalizedCommitted" : "FinalizedAborted");
        else ReadMarker(directory, journal, hash, "Finalized");
        checkpoint?.Invoke(MmcTransactionCheckpoint.Finalized, -1);
        // Moving the complete owned directory removes the live pending marker atomically while retaining
        // all old/new/actual backups for diagnosis. No recursive deletion is used.
        Archive(paths, directory, journal.TransactionId);
        return new(committed ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, journal.TransactionId);
    }
    private static void Archive(MmcInstancePaths paths, string directory, Guid id) {
        MmcInstancePaths.EnsureNoLinks(directory);
        string destination = HistoryDirectory(paths, id);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        MmcInstancePaths.EnsureNoLinks(destination); Directory.Move(directory, destination);
    }
    private static void WriteMarker(string directory, MmcPreparedJournal journal, string hash, string phase) =>
        MmcDurableFiles.WriteRecord(Path.Combine(directory, (phase.StartsWith("Finalized", StringComparison.Ordinal) ? "Finalized" : phase) + ".record"),
            new MmcTransactionMarker(1, journal.TransactionId, journal.InstanceId, hash, phase));
    private static MmcTransactionMarker ReadMarker(string directory, MmcPreparedJournal journal, string hash, string name) {
        var marker = MmcDurableFiles.ReadRecord<MmcTransactionMarker>(Path.Combine(directory, name + ".record")).Value;
        if (marker.SchemaVersion != 1 || marker.TransactionId != journal.TransactionId || marker.InstanceId != journal.InstanceId || marker.PreparedSha256 != hash ||
            (name == "Finalized" ? marker.Phase is not ("FinalizedCommitted" or "FinalizedAborted") : marker.Phase != name)) throw new IOException("事务标记损坏或归属不一致。");
        return marker;
    }
    private static string Target(MmcInstancePaths paths, string relative) => paths.ResolveRelative(MmcPathRole.Instance, NormalizeRelative(relative));
    private static string StagedSource(MmcInstancePaths paths, string relative) {
        string[] pieces = relative.Split('/');
        if (pieces.Length < 5 || pieces[0] != "PCL" || pieces[1] != "MmcRuntime" || pieces[2] != ".staging" ||
            !Guid.TryParseExact(pieces[3], "N", out Guid stagingId) || stagingId == Guid.Empty || relative.Contains('\\'))
            throw new IOException("文件来源必须位于本实例 owned .staging/GUID 目录。");
        return paths.ResolveRelative(MmcPathRole.Instance, relative);
    }
    private static void CheckStagedBudget(long existing, long additional) {
        if (additional < 0 || additional > MmcDurableFiles.MaximumFileBytes || checked(existing + additional) > MaximumStagedBytes)
            throw new IOException("事务暂存单项或总量超过预算。");
    }
    private static string NormalizeRelative(string relative) {
        ArgumentNullException.ThrowIfNull(relative);
        string normalized = relative.Replace('\\', '/');
        string first = normalized.Split('/').Take(2).Aggregate((a, b) => a + "/" + b);
        if (normalized.Equals("PCL/MmcRuntime/.staging", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("PCL/MmcRuntime/.staging/", StringComparison.OrdinalIgnoreCase))
            throw new IOException("暂存来源不能成为持久 live 目标。");
        if (new[] { "PCL/MmcTransactions", "PCL/MmcTransactionHistory", "PCL/MmcSessions", "PCL/MmcSessionHistory", "PCL/MmcCommands" }.Any(p => string.Equals(first, p, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("事务不能修改自己的日志、命令或会话控制目录。");
        return normalized;
    }
    private static string TransactionDirectory(MmcInstancePaths paths, Guid id) => paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactions/" + id.ToString("N"));
    private static string HistoryDirectory(MmcInstancePaths paths, Guid id) => paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcTransactionHistory/" + id.ToString("N"));
    private static string ObjectFile(string directory, int index, bool newer) => Path.Combine(directory, (newer ? "new-" : "old-") + index + ".bin");
    private static byte[] ReadSmall(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("定义超过预算。");
        using MemoryStream bytes = new(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (bytes.Length + count > MmcManagementReader.MaximumDocumentBytes) throw new IOException("定义读取期间超过预算。");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    private static void ValidateDefinition(byte[] bytes, Guid id, long generation) {
        using JsonDocument json = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(bytes));
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("instanceId", out var rawId) || rawId.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(rawId.GetString(), "D", out Guid actualId) || actualId != id || !root.TryGetProperty("generation", out var rawGeneration) ||
            !rawGeneration.TryGetInt64(out long actualGeneration) || actualGeneration != generation) throw new IOException("定义的身份或代数不符合事务请求。");
    }
    private static void AssertNoSessions(MmcInstancePaths paths) {
        string sessions = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions");
        if (MmcInstancePaths.Attributes(sessions) is not null && (!Directory.Exists(sessions) || Directory.EnumerateFileSystemEntries(sessions).Any()))
            throw new IOException("存在活动或待确认 MMC 会话，拒绝结构修改。");
    }
    private static bool IsFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or
        ArgumentException or InvalidOperationException or OverflowException or JsonException or DecoderFallbackException;
}
