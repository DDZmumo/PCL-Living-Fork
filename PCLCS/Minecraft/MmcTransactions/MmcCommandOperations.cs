using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcCommandObservedProcess(int ProcessId, long CreationTimeUtcTicks, MmcExecutableIdentity? ExecutableIdentity = null);

public sealed record MmcCommandIntentRecord(
    int SchemaVersion,
    Guid CommandId,
    Guid InstanceId,
    string CanonicalRoot,
    MmcRuntimeCommandStage Stage,
    Guid? SessionId,
    MmcSessionRuntimeBinding? RuntimeBinding,
    string CommandToken,
    string JobName,
    long CreatedUtcTicks,
    int? CreatorWindowsSessionId = null);

public sealed record MmcCommandProcessRecord(
    int SchemaVersion,
    Guid CommandId,
    string IntentHash,
    int RootProcessId,
    long RootCreationTimeUtcTicks,
    MmcExecutableIdentity? RootExecutableIdentity,
    bool JobBacked = false);

public sealed record MmcCommandTailRecord(
    int SchemaVersion,
    Guid CommandId,
    string IntentHash,
    string Diagnostic,
    MmcCommandObservedProcess[] SurvivingProcesses);

public sealed record MmcCommandReconcileResult(bool Clear, string? Diagnostic = null);

public sealed class MmcCommandLease : IDisposable {
    internal MmcCommandLease(MmcInstancePaths paths, string directory, MmcCommandIntentRecord intent, string intentHash, FileStream ownerLock) {
        Paths = paths;
        Directory = directory;
        Intent = intent;
        IntentHash = intentHash;
        this.ownerLock = ownerLock;
    }

    internal MmcInstancePaths Paths { get; }
    internal string Directory { get; }
    public MmcCommandIntentRecord Intent { get; }
    internal string IntentHash { get; }
    private FileStream? ownerLock;
    private bool settled;
    private bool disposed;

    /// <summary>Records the spawned root command PID and identity before releasing its suspended entry thread.</summary>
    public void RecordProcessStarted(int rootProcessId, long rootCreationTimeUtcTicks, MmcExecutableIdentity? rootExecutableIdentity, bool jobBacked = false) {
        if (disposed || settled) return;
        MmcInstancePaths.EnsureNoLinks(Directory);
        var record = new MmcCommandProcessRecord(1, Intent.CommandId, IntentHash, rootProcessId, rootCreationTimeUtcTicks, rootExecutableIdentity, jobBacked);
        MmcDurableFiles.WriteRecord(Path.Combine(Directory, "process.record"), record);
    }

    /// <summary>Settles and deletes the pending command intent inside admission only after whole-tree exit is proven.</summary>
    public void CompleteProvenExited(MmcAdmissionLease lease) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcCommandLease));
        if (settled) return;
        lease.AssertHeld();
        MmcCommandOperations.ValidateLeaseOwnership(lease, Paths, Intent);
        ownerLock?.Dispose();
        ownerLock = null;
        MmcCommandOperations.DeleteCommandDirectory(Directory);
        settled = true;
    }

    /// <summary>Persists surviving/unconfirmed tree diagnostics inside admission and releases only the host lock handle.</summary>
    public void RecordUnconfirmed(MmcAdmissionLease lease, string diagnostic, IEnumerable<MmcCommandObservedProcess>? surviving = null) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcCommandLease));
        if (settled) return;
        lease.AssertHeld();
        MmcCommandOperations.ValidateLeaseOwnership(lease, Paths, Intent);
        MmcInstancePaths.EnsureNoLinks(Directory);
        var tail = new MmcCommandTailRecord(1, Intent.CommandId, IntentHash,
            string.IsNullOrWhiteSpace(diagnostic) ? "包内命令进程组退出尚未确认。" : diagnostic,
            surviving?.ToArray() ?? []);
        MmcDurableFiles.WriteRecord(Path.Combine(Directory, "tail.record"), tail);
        ownerLock?.Dispose();
        ownerLock = null;
        settled = true;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        ownerLock?.Dispose();
        ownerLock = null;
    }
}

/// <summary>
/// Cross-process durable ledger and Windows process-group verifier for package command stages
/// (<c>PreLoad</c>, <c>PreLaunch</c>, <c>PostExit</c>). Pending intents and unconfirmed tails live under
/// <c>PCL/MmcCommands/&lt;commandId&gt;</c> and block cleanup/structural writes/subsequent launches until
/// OS evidence proves the entire command process group has exited.
/// </summary>
public static class MmcCommandOperations {
    internal const string StoreRelative = "PCL/MmcCommands";
    private const string OwnerLockFileName = "owner.lock";
    private const int MaximumEntries = 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Creates an active cross-process lease for a package command inside <paramref name="lease"/>.</summary>
    public static MmcCommandLease Begin(
        MmcAdmissionLease lease,
        MmcRuntimeCommandStage stage,
        Guid? sessionId = null,
        MmcSessionRuntimeBinding? runtimeBinding = null,
        Guid? commandId = null) {
        lease.AssertHeld();
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        MmcFileTransactions.AssertNoTransactionsOnly(lease);
        AssertNoPending(lease);

        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        if (stage is MmcRuntimeCommandStage.PreLoad or MmcRuntimeCommandStage.PreLaunch) {
            MmcSessions.AssertNoActive(lease);
        } else if (stage == MmcRuntimeCommandStage.PostExit) {
            if (sessionId is not Guid sid || sid == Guid.Empty)
                throw new IOException("PostExit 命令必须绑定已验证退出的会话身份。");
            string sessionDir = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions/" + sid.ToString("N"));
            if (MmcInstancePaths.Attributes(Path.Combine(sessionDir, "Exited.record")) is null)
                throw new IOException("PostExit 命令要求会话已持久化 Exited.record。");
        }

        Guid id = commandId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw new IOException("包内命令身份不能为空。");
        string storeRoot = paths.ResolveRelative(MmcPathRole.Instance, StoreRelative);
        MmcInstancePaths.EnsureNoLinks(storeRoot);
        Directory.CreateDirectory(storeRoot);
        string directory = CommandDirectory(paths, id);
        MmcInstancePaths.EnsureNoLinks(directory);
        if (MmcInstancePaths.Attributes(directory) is not null)
            throw new IOException("包内命令目录已存在，不能覆盖待确认命令记录。");

        Directory.CreateDirectory(directory);
        FileStream? ownerLock = null;
        bool transferred = false;
        try {
            ownerLock = AcquireOwnerLock(directory);
            int? creatorWindowsSessionId = TryGetCurrentProcessSessionId(out int currentSessionId) ? currentSessionId : null;
            var intent = new MmcCommandIntentRecord(
                1,
                id,
                lease.InstanceId,
                paths.VersionRoot,
                stage,
                sessionId,
                runtimeBinding,
                id.ToString("N"),
                "Local\\PCLLF.Mmc.Cmd." + id.ToString("N"),
                DateTime.UtcNow.Ticks,
                creatorWindowsSessionId);
            string hash = MmcDurableFiles.WriteRecord(Path.Combine(directory, "intent.record"), intent);
            var commandLease = new MmcCommandLease(paths, directory, intent, hash, ownerLock);
            transferred = true;
            return commandLease;
        } finally {
            if (!transferred) ownerLock?.Dispose();
        }
    }

    /// <summary>Throws if any active or unconfirmed package command ledger entry exists on this instance.</summary>
    public static void AssertNoPending(MmcAdmissionLease lease, Guid? ignoreCommandId = null) {
        lease.AssertHeld();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        string root = paths.ResolveRelative(MmcPathRole.Instance, StoreRelative);
        MmcInstancePaths.EnsureNoLinks(root);
        if (MmcInstancePaths.Attributes(root) is null) return;
        if (!Directory.Exists(root))
            throw new IOException("存在异常的 MMC 包内命令控制路径，拒绝操作。");
        foreach (string entry in Directory.EnumerateFileSystemEntries(root).Take(MaximumEntries + 1)) {
            if (ignoreCommandId is Guid ignored &&
                Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid parsed) &&
                parsed == ignored) {
                continue;
            }
            throw new IOException("存在正在执行或待恢复确认的 MMC 包内命令，拒绝清理或结构写入。");
        }
    }

    /// <summary>Throws if any pending/unconfirmed command linked to <paramref name="sessionId"/> (or unrecognized entry) remains.</summary>
    internal static void AssertNoPendingForSession(MmcAdmissionLease lease, Guid sessionId) {
        var result = ReconcileForSession(lease, sessionId, null, settle: false);
        if (!result.Clear)
            throw new IOException(result.Diagnostic ?? "存在尚未确认退出的关联包内命令。");
    }

    /// <summary>Reconciles all pending command records conservatively before starting a new launch.</summary>
    public static void ReconcileAll(MmcAdmissionLease lease, MmcMutableRuntimeAuthority? runtimeAuthority = null) {
        lease.AssertHeld();
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        MmcFileTransactions.AssertNoTransactionsOnly(lease);
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        string root = paths.ResolveRelative(MmcPathRole.Instance, StoreRelative);
        MmcInstancePaths.EnsureNoLinks(root);
        if (MmcInstancePaths.Attributes(root) is null) return;
        if (!Directory.Exists(root))
            throw new IOException("存在无法识别的 MMC 包内命令记录路径，需要恢复确认。");

        string[] entries = Directory.EnumerateFileSystemEntries(root).Take(MaximumEntries + 1).ToArray();
        if (entries.Length > MaximumEntries)
            throw new IOException("MMC 包内命令记录超过恢复预算。");

        List<(Guid SessionId, MmcSessionRuntimeBinding Binding)> unlaunchedToAbandon = [];
        foreach (string entry in entries) {
            if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid id) || !Directory.Exists(entry))
                throw new IOException("存在无法识别的 MMC 包内命令记录，需要恢复确认。");
            var (clear, diagnostic, abandonCandidate) = ReconcileEntry(lease, paths, entry, id, settle: true);
            if (!clear)
                throw new IOException("MMC 包内命令进程组仍在运行或尚未确认退出：" + diagnostic);
            if (abandonCandidate is { } candidate) unlaunchedToAbandon.Add(candidate);
        }

        // Only retire unlaunched PreLaunch mutable TEMP directories once zero pending command records remain.
        foreach (var (sid, binding) in unlaunchedToAbandon) {
            string sessionDir = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions/" + sid.ToString("N"));
            string historyDir = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + sid.ToString("N"));
            if (MmcInstancePaths.Attributes(sessionDir) is null && MmcInstancePaths.Attributes(historyDir) is null)
                MmcSessionRuntimeDirectories.AbandonUnlaunched(lease, sid, binding, runtimeAuthority);
        }
    }

    /// <summary>Reconciles any command records bound to <paramref name="sessionId"/> without swallowing unknown state.</summary>
    public static MmcCommandReconcileResult ReconcileForSession(
        MmcAdmissionLease lease,
        Guid sessionId,
        MmcMutableRuntimeAuthority? runtimeAuthority = null)
        => ReconcileForSession(lease, sessionId, runtimeAuthority, settle: true);

    private static MmcCommandReconcileResult ReconcileForSession(
        MmcAdmissionLease lease,
        Guid sessionId,
        MmcMutableRuntimeAuthority? runtimeAuthority,
        bool settle) {
        lease.AssertHeld();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        string root = paths.ResolveRelative(MmcPathRole.Instance, StoreRelative);
        MmcInstancePaths.EnsureNoLinks(root);
        if (MmcInstancePaths.Attributes(root) is null) return new(true);
        if (!Directory.Exists(root))
            return new(false, "存在异常的 MMC 包内命令路径，需要恢复确认。");

        string[] entries = Directory.EnumerateFileSystemEntries(root).Take(MaximumEntries + 1).ToArray();
        if (entries.Length > MaximumEntries)
            return new(false, "MMC 包内命令记录超过恢复预算。");

        List<(Guid SessionId, MmcSessionRuntimeBinding Binding)> unlaunchedToAbandon = [];
        foreach (string entry in entries) {
            if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid id) || !Directory.Exists(entry))
                return new(false, "存在无法识别的 MMC 包内命令记录，保留会话与临时目录。");

            MmcCommandIntentRecord intent;
            try {
                MmcInstancePaths.EnsureNoLinks(entry);
                string intentPath = Path.Combine(entry, "intent.record");
                if (MmcInstancePaths.Attributes(intentPath) is null)
                    return new(false, "包内命令记录缺少 intent.record，需要恢复确认。");
                (intent, _) = MmcDurableFiles.ReadRecord<MmcCommandIntentRecord>(intentPath);
                ValidateLeaseOwnership(lease, paths, intent, id);
            } catch (Exception ex) when (IsFailure(ex)) {
                return new(false, "包内命令记录无法验证：" + ex.Message);
            }

            if (intent.SessionId != sessionId) continue;
            if (!settle)
                return new(false, $"会话仍存在未决的 {intent.Stage} 包内命令。");

            var (clear, diagnostic, abandonCandidate) = ReconcileEntry(lease, paths, entry, id, settle: true);
            if (!clear) return new(false, diagnostic);
            if (abandonCandidate is { } candidate) unlaunchedToAbandon.Add(candidate);
        }

        if (settle) {
            foreach (var (sid, binding) in unlaunchedToAbandon) {
                string sessionDir = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions/" + sid.ToString("N"));
                string historyDir = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + sid.ToString("N"));
                if (MmcInstancePaths.Attributes(sessionDir) is null && MmcInstancePaths.Attributes(historyDir) is null)
                    MmcSessionRuntimeDirectories.AbandonUnlaunched(lease, sid, binding, runtimeAuthority);
            }
        }
        return new(true);
    }

    private static (bool Clear, string? Diagnostic, (Guid SessionId, MmcSessionRuntimeBinding Binding)? AbandonCandidate) ReconcileEntry(
        MmcAdmissionLease lease,
        MmcInstancePaths paths,
        string directory,
        Guid commandId,
        bool settle) {
        try {
            MmcInstancePaths.EnsureNoLinks(directory);
            using FileStream? ownerLock = TryAcquireOwnerLock(directory);
            if (ownerLock is null)
                return (false, "包内命令仍在活动宿主持有下执行或收尾。", null);

            string intentPath = Path.Combine(directory, "intent.record");
            if (MmcInstancePaths.Attributes(intentPath) is null)
                return (false, "包内命令缺少 intent.record，需要恢复确认。", null);
            (MmcCommandIntentRecord intent, string intentHash) = MmcDurableFiles.ReadRecord<MmcCommandIntentRecord>(intentPath);
            ValidateLeaseOwnership(lease, paths, intent, commandId);

            if (!IsCommandTreeProvenExited(directory, intent, intentHash, out string? diagnostic))
                return (false, diagnostic ?? "无法确认包内命令整个进程组已退出。", null);

            if (!settle) return (true, null, null);

            ownerLock.Dispose();
            DeleteCommandDirectory(directory);
            var abandon = intent.Stage == MmcRuntimeCommandStage.PreLaunch &&
                          intent.SessionId is Guid sid && sid != Guid.Empty &&
                          intent.RuntimeBinding is { } binding
                ? ((Guid, MmcSessionRuntimeBinding)?)(sid, binding)
                : null;
            return (true, null, abandon);
        } catch (Exception ex) when (IsFailure(ex)) {
            return (false, ex.Message, null);
        }
    }

    internal static Func<string, MmcNamedJobObservation>? ObserveNamedJobOverride { get; set; }

    internal static bool IsCommandTreeProvenExited(
        string? directory,
        MmcCommandIntentRecord intent,
        string? expectedIntentHash,
        out string? diagnostic) {
        diagnostic = null;

        if (!OperatingSystem.IsWindows()) {
            diagnostic = "仅支持在 Windows 上确认包内命令进程组退出状态。";
            return false;
        }

        // 1. Verify same Windows login session before trusting Local\ named Job objects or process probes.
        if (intent.CreatorWindowsSessionId is not int creatorSessionId ||
            !TryGetCurrentProcessSessionId(out int currentSessionId) ||
            creatorSessionId != currentSessionId) {
            diagnostic = $"包内命令创建于 Windows 会话 {intent.CreatorWindowsSessionId?.ToString() ?? "未知"}，无法跨会话验证 Local 作业与进程组。";
            return false;
        }

        // 2. Query named kernel Job object; AccessDenied / query failure / other open error stays Unknown (never downgraded to scan).
        MmcNamedJobObservation job = (ObserveNamedJobOverride ?? MmcSessionProcessTree.ObserveNamedJob)(intent.JobName);
        if (job.State == MmcNamedJobState.Active) {
            diagnostic = job.Diagnostic ?? $"包内命令作业 {intent.JobName} 仍有活动进程 ({string.Join(",", job.ProcessIds)})。";
            return false;
        }
        if (job.State == MmcNamedJobState.Unknown) {
            diagnostic = job.Diagnostic ?? $"无法观测命令作业 {intent.JobName} (Win32={job.Win32Error})，保持未知状态。";
            return false;
        }

        int? recordedRootPid = null;
        bool jobBacked = false;
        if (!string.IsNullOrEmpty(directory)) {
            // 3. Recorded root process identity check (prevents PID reuse confusion while insufficient on its own).
            string procPath = Path.Combine(directory, "process.record");
            if (MmcInstancePaths.Attributes(procPath) is not null) {
                (var proc, _) = MmcDurableFiles.ReadRecord<MmcCommandProcessRecord>(procPath);
                if (proc.SchemaVersion != 1 || proc.CommandId != intent.CommandId ||
                    (expectedIntentHash is not null && proc.IntentHash != expectedIntentHash)) {
                    diagnostic = "包内命令进程记录与意图不匹配。";
                    return false;
                }
                recordedRootPid = proc.RootProcessId;
                jobBacked = proc.JobBacked;
                var rootState = CheckRecordedProcessAlive(proc.RootProcessId, proc.RootCreationTimeUtcTicks, proc.RootExecutableIdentity);
                if (rootState != ProcessGoneState.ProvenGone) {
                    diagnostic = $"包内命令根进程 PID={proc.RootProcessId} 仍存在或无法证实已退出。";
                    return false;
                }
            }

            // 4. Recorded unconfirmed tail descendants from prior cancellation/grace-timeout observation.
            string tailPath = Path.Combine(directory, "tail.record");
            if (MmcInstancePaths.Attributes(tailPath) is not null) {
                (var tail, _) = MmcDurableFiles.ReadRecord<MmcCommandTailRecord>(tailPath);
                if (tail.SchemaVersion != 1 || tail.CommandId != intent.CommandId ||
                    (expectedIntentHash is not null && tail.IntentHash != expectedIntentHash)) {
                    diagnostic = "包内命令残留记录与意图不匹配。";
                    return false;
                }
                foreach (var child in tail.SurvivingProcesses) {
                    var childState = CheckRecordedProcessAlive(child.ProcessId, child.CreationTimeUtcTicks, child.ExecutableIdentity);
                    if (childState != ProcessGoneState.ProvenGone) {
                        diagnostic = $"包内命令子进程 PID={child.ProcessId} 仍存在或无法证实已退出。";
                        return false;
                    }
                }
            }
        }

        // 5. If the named Job itself is still open with atomic JOB_LIST assignment and queries empty, kernel proves 0 descendants.
        if (job.State == MmcNamedJobState.Empty && jobBacked) return true;

        // 6. Named Job is Missing (exact ERROR_FILE_NOT_FOUND in creator's session, e.g. host crashed) or custom fixture bypassed Job.
        // Fall back to whole-system recent-process probe without excluding elevated, access-denied, or cross-session candidates.
        var tokenState = ProbeLiveProcessesForCommandToken(intent.CommandToken, intent.CreatedUtcTicks, recordedRootPid, out int? matchedPid);
        if (tokenState == TokenScanResult.MatchAlive) {
            diagnostic = $"检测到携带命令组令牌的存活后代进程 PID={matchedPid}。";
            return false;
        }
        if (tokenState == TokenScanResult.Unobservable) {
            diagnostic = "存在无法验证环境块的近期/高权限或跨会话进程，按保守策略阻止清理。";
            return false;
        }
        return true;
    }

    internal static TokenScanResult ProbeLiveProcessesForCommandToken(
        string commandToken,
        long createdUtcTicks,
        int? recordedRootPid,
        out int? matchedPid) {
        matchedPid = null;
        if (string.IsNullOrEmpty(commandToken)) return TokenScanResult.Unobservable;
        if (!OperatingSystem.IsWindows()) return TokenScanResult.ProvenAbsent;
        if (!TryGetCurrentProcessSessionId(out int currentSessionId)) return TokenScanResult.Unobservable;

        long startFloorTicks = createdUtcTicks > TimeSpan.FromSeconds(5).Ticks
            ? createdUtcTicks - TimeSpan.FromSeconds(5).Ticks
            : 0;
        int currentPid = Environment.ProcessId;
        if (!TrySnapshotLiveProcesses(out List<(int Pid, long StartTicks, uint SessionId)> snapshot))
            return TokenScanResult.Unobservable;

        bool anyUnobservable = false;
        foreach (var entry in snapshot) {
            int pid = entry.Pid;
            if (pid <= 4 || pid == currentPid) continue;
            if (entry.StartTicks < startFloorTicks) continue;

            var state = InspectCandidateEnvironmentToken(pid, currentSessionId, startFloorTicks, MmcRuntimeCommandExecution.CommandTokenEnvironmentVariable, commandToken);
            if (state == CandidateTokenState.Matched) {
                matchedPid = pid;
                return TokenScanResult.MatchAlive;
            }
            if (state == CandidateTokenState.Unobservable) {
                anyUnobservable = true;
            }
        }
        return anyUnobservable ? TokenScanResult.Unobservable : TokenScanResult.ProvenAbsent;
    }

    private enum ProcessGoneState { ProvenGone, Alive, Unobservable }
    internal enum TokenScanResult { ProvenAbsent, MatchAlive, Unobservable }
    private enum CandidateTokenState { IrrelevantOrGone, CleanNoMatch, Matched, Unobservable }

    internal static TokenScanResult ProbeLiveJavaProcessesForToken(string variableName, string token, long startedTicks, int windowsSession) {
        if (!OperatingSystem.IsWindows()) return TokenScanResult.Unobservable;
        long began = Stopwatch.GetTimestamp();
        int probes = 0;
        // If a relay exits while we inspect it, its successor may already be
        // running but missing from this snapshot. Recheck once, then defer.
        for (int pass = 0; pass < 2; pass++) {
            if (!TrySnapshotLiveProcesses(out var snapshot, windowsSession)) return TokenScanResult.Unobservable;
            bool refresh = false, uncertain = false;
            foreach (var entry in snapshot) {
                if (entry.StartTicks < startedTicks) continue;
                if (++probes > 64 || Stopwatch.GetElapsedTime(began) > TimeSpan.FromMilliseconds(25)) return TokenScanResult.Unobservable;
                switch (InspectCandidateEnvironmentToken(entry.Pid, windowsSession, startedTicks, variableName, token, requireExitProof: true)) {
                    case CandidateTokenState.Matched: return TokenScanResult.MatchAlive;
                    case CandidateTokenState.IrrelevantOrGone: refresh = true; break;
                    case CandidateTokenState.Unobservable: uncertain = true; break;
                }
            }
            if (!refresh) return uncertain ? TokenScanResult.Unobservable : TokenScanResult.ProvenAbsent;
        }
        return TokenScanResult.Unobservable;
    }

    private static bool TrySnapshotLiveProcesses(out List<(int Pid, long StartTicks, uint SessionId)> entries, int? javaWindowsSession = null) {
        entries = [];
        int bufSize = 1024 * 1024;
        nint buffer = Marshal.AllocHGlobal(bufSize);
        try {
            int status;
            while ((status = NtQuerySystemInformation(5, buffer, bufSize, out int required)) == unchecked((int)0xC0000004)) {
                Marshal.FreeHGlobal(buffer);
                bufSize = Math.Max(bufSize * 2, required + 65536);
                buffer = Marshal.AllocHGlobal(bufSize);
            }
            if (status != 0) return false;

            int offset = 0;
            while (true) {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(buffer + offset);
                int pid = unchecked((int)info.UniqueProcessId.ToInt64());
                if (javaWindowsSession is not int session || (pid > 4 && info.SessionId == (uint)session && CouldBeJava(info.ImageName))) {
                    long startTicks = info.CreateTime > 0 ? DateTime.FromFileTimeUtc(info.CreateTime).Ticks : long.MaxValue;
                    entries.Add((pid, startTicks, info.SessionId));
                }
                if (info.NextEntryOffset == 0) break;
                offset += checked((int)info.NextEntryOffset);
            }
            return true;
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool CouldBeJava(UNICODE_STRING image) =>
        image.Buffer == 0 || image.Length == 0 ||
        LegacyJavaProcessGroup.IsJava(Marshal.PtrToStringUni(image.Buffer, image.Length / sizeof(char)));

    private static ProcessGoneState CheckRecordedProcessAlive(int pid, long expectedStartTicks, MmcExecutableIdentity? expectedIdentity) {
        if (pid <= 0 || expectedStartTicks <= 0) return ProcessGoneState.Unobservable;
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return ProcessGoneState.ProvenGone; }
        try {
            if (process.HasExited) return ProcessGoneState.ProvenGone;
            long actualStartTicks = process.StartTime.ToUniversalTime().Ticks;
            if (actualStartTicks != expectedStartTicks) return ProcessGoneState.ProvenGone; // PID reused by newer unrelated process.
            if (expectedIdentity is not null) {
                MmcExecutableIdentity observed = MmcSessions.ObserveProcessExecutable(process);
                if (!MmcSessions.SameExecutable(observed, expectedIdentity)) return ProcessGoneState.ProvenGone;
            }
            return ProcessGoneState.Alive;
        } catch (ArgumentException) {
            return ProcessGoneState.ProvenGone;
        } catch (InvalidOperationException) {
            return ProcessGoneState.ProvenGone;
        } catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException) {
            try { if (process.HasExited) return ProcessGoneState.ProvenGone; } catch { }
            return ProcessGoneState.Unobservable;
        } finally {
            process.Dispose();
        }
    }

    private static CandidateTokenState InspectCandidateEnvironmentToken(
        int pid,
        int currentSessionId,
        long startFloorTicks,
        string variableName,
        string expectedToken,
        bool requireExitProof = false) {
        using SafeProcessHandle queryHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (queryHandle.IsInvalid) {
            int err = Marshal.GetLastWin32Error();
            // Only ERROR_INVALID_PARAMETER (87) proves the PID no longer exists; AccessDenied/other failures stay Unobservable.
            return err == 87 ? CandidateTokenState.IrrelevantOrGone : CandidateTokenState.Unobservable;
        }

        if (ObserveCandidateExit(queryHandle, requireExitProof) is { } exit) return exit;
        if (!TryGetProcessStartTicks(queryHandle, out long startTicks)) return CandidateTokenState.Unobservable;
        if (startTicks < startFloorTicks) return CandidateTokenState.IrrelevantOrGone;
        if (!ProcessIdToSessionId(pid, out uint sessionId) || sessionId != (uint)currentSessionId)
            return CandidateTokenState.Unobservable;

        using SafeProcessHandle vmHandle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (vmHandle.IsInvalid) {
            return ObserveCandidateExit(queryHandle, requireExitProof) ?? CandidateTokenState.Unobservable;
        }

        bool? matched = TryReadPebEnvironmentMatch(vmHandle, variableName, expectedToken);
        if (matched == true) return CandidateTokenState.Matched;
        if (matched == false) return CandidateTokenState.CleanNoMatch;
        return ObserveCandidateExit(queryHandle, requireExitProof) ?? CandidateTokenState.Unobservable;
    }

    private static bool? TryReadPebEnvironmentMatch(SafeProcessHandle vmHandle, string variableName, string expectedToken) {
        if (!Environment.Is64BitProcess) return null;
        var pbi = new PROCESS_BASIC_INFORMATION();
        int status = NtQueryInformationProcess(vmHandle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
        if (status != 0 || pbi.PebBaseAddress == 0) return null;

        byte[] ptrBuf = new byte[8];
        if (!ReadProcessMemory(vmHandle, pbi.PebBaseAddress + 0x20, ptrBuf, 8, out _)) return null;
        nint procParams = checked((nint)BitConverter.ToInt64(ptrBuf, 0));
        if (procParams == 0) return null;

        if (!ReadProcessMemory(vmHandle, procParams + 0x80, ptrBuf, 8, out _)) return null;
        nint envPtr = checked((nint)BitConverter.ToInt64(ptrBuf, 0));
        if (!ReadProcessMemory(vmHandle, procParams + 0x3F0, ptrBuf, 8, out _)) return null;
        ulong envSize = BitConverter.ToUInt64(ptrBuf, 0);
        if (envPtr == 0 || envSize == 0 || envSize > 4 * 1024 * 1024) return null;

        byte[] envBytes = new byte[(int)envSize];
        if (!ReadProcessMemory(vmHandle, envPtr, envBytes, envBytes.Length, out nint bytesRead) || bytesRead <= 0) return null;
        string envText = Encoding.Unicode.GetString(envBytes, 0, checked((int)bytesRead));
        string targetEntry = variableName + "=" + expectedToken;
        foreach (string entry in envText.Split('\0')) {
            if (entry.Length == 0) break;
            if (string.Equals(entry, targetEntry, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static CandidateTokenState? ObserveCandidateExit(SafeProcessHandle handle, bool requireExitProof) {
        if (!GetExitCodeProcess(handle, out uint exitCode))
            return requireExitProof ? CandidateTokenState.Unobservable : CandidateTokenState.IrrelevantOrGone;
        return exitCode == StillActive ? null : CandidateTokenState.IrrelevantOrGone;
    }

    private static bool TryGetProcessStartTicks(SafeProcessHandle handle, out long utcTicks) {
        utcTicks = 0;
        if (!GetProcessTimes(handle, out long creationFileTime, out _, out _, out _)) return false;
        utcTicks = DateTime.FromFileTimeUtc(creationFileTime).Ticks;
        return true;
    }

    internal static bool TryGetCurrentProcessSessionId(out int sessionId) {
        if (OperatingSystem.IsWindows() && ProcessIdToSessionId(Environment.ProcessId, out uint sid)) {
            sessionId = unchecked((int)sid);
            return true;
        }
        sessionId = 0;
        return false;
    }

    internal static void ValidateLeaseOwnership(MmcAdmissionLease lease, MmcInstancePaths paths, MmcCommandIntentRecord intent, Guid? expectedCommandId = null) {
        if (intent.SchemaVersion != 1 ||
            intent.CommandId == Guid.Empty ||
            (expectedCommandId is Guid expectedId && intent.CommandId != expectedId) ||
            intent.InstanceId != lease.InstanceId ||
            !string.Equals(intent.CanonicalRoot, paths.VersionRoot, PathComparison) ||
            string.IsNullOrWhiteSpace(intent.CommandToken) ||
            string.IsNullOrWhiteSpace(intent.JobName))
            throw new IOException("MMC 包内命令意图记录无效或归属不匹配。");
    }

    internal static void DeleteCommandDirectory(string directory) {
        MmcInstancePaths.EnsureNoLinks(directory);
        if (MmcInstancePaths.Attributes(directory) is null) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(directory)) {
            MmcInstancePaths.EnsureNoLinks(child);
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
                DeleteCommandDirectory(child);
            else
                File.Delete(child);
        }
        Directory.Delete(directory, false);
    }

    private static string CommandDirectory(MmcInstancePaths paths, Guid commandId) =>
        paths.ResolveRelative(MmcPathRole.Instance, StoreRelative + "/" + commandId.ToString("N"));

    private static FileStream AcquireOwnerLock(string directory) {
        string path = Path.Combine(directory, OwnerLockFileName);
        MmcInstancePaths.EnsureNoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
    }

    private static FileStream? TryAcquireOwnerLock(string directory) {
        string path = Path.Combine(directory, OwnerLockFileName);
        MmcInstancePaths.EnsureNoLinks(path);
        try {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    private static bool IsFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or NotSupportedException or Win32Exception;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle hProcess, nint lpBaseAddress, [Out] byte[] lpBuffer, nint nSize, out nint lpNumberOfBytesRead);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);
}
