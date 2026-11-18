using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>One frozen package-command stage. Failure handling is stage-specific (contract §1.3 / F-14).</summary>
public enum MmcRuntimeCommandStage { PreLoad, PreLaunch, PostExit }

/// <summary>
/// The frozen inputs one package command stage expands against: the resolved roots, the active cfg, and the
/// host values that already exist before the runtime plan does. It deliberately carries no credential value;
/// the command variable surface never binds account data (contract §3).
/// </summary>
public sealed record MmcRuntimeCommandContext(
    string InstanceRoot, string GameRoot, Guid SessionId, string LauncherName, string LauncherVersion,
    int ResolutionWidth, int ResolutionHeight) {
    /// <summary>Package behaviour source; the command text is read from it at the stage point, never cached.</summary>
    public PCLLF.MMCC.Core.MmcConfiguration? PackageConfiguration { get; init; }
    /// <summary>Live account secrets projected for redaction only; never expanded into a command.</summary>
    public ImmutableArray<string> RedactionSecrets { get; init; } = [];
    /// <summary>
    /// Session temporary directory (<c>PreLaunch</c> and <c>PostExit</c> only, D15); null before mutable
    /// runtime paths exist (<c>PreLoad</c>), which must never inherit package <c>Env</c>.
    /// </summary>
    public string? TemporaryDirectory { get; init; }
}

public sealed record MmcRuntimeCommandResult(string Stage, string RedactedCommand, int ExitCode, string ExitCodeText);

/// <summary>
/// Executes the package command stages (<c>PreLoadCommand</c>, <c>PreLaunchCommand</c>, <c>PostExitCommand</c>)
/// exactly as frozen by <c>docs/mmc-phase2-command-contract.md</c>.
///
/// This type is the single production boundary for the stage semantics, so every rule below is exercised
/// without constructing a launcher: the execution host is <c>cmd.exe</c> invoked via
/// <c>/d /s /c "&lt;command&gt;"</c> (never shell execute, never raw unquoted cmd invocation), the working
/// directory is always the instance GameRoot, expansion uses one runtime dictionary applied once, and a log
/// line carries only the redacted form of the command — an expanded value never reaches the log.
///
/// The stage knows nothing about the launch plan. A caller that has one supplies the plan-derived half of the
/// runtime dictionary as <c>planVariables</c>; a stage that runs before the plan exists supplies none, and
/// then a plan-only variable is genuinely unavailable and is refused rather than guessed.
/// </summary>
public static class MmcRuntimeCommandExecution {
    private static readonly Func<ProcessStartInfo, Process?> DefaultStartProcess = start => Process.Start(start);

    /// <summary>
    /// Host-injected process entry. Controlled verification may replace this hook; otherwise production spawns
    /// <c>cmd.exe</c> suspended directly inside an owned Windows Job Object so no fast child escapes group tracking.
    /// The default host explicitly refuses non-Windows platforms; an injected host remains a separate boundary.
    /// </summary>
    public static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = DefaultStartProcess;

    internal const string CommandTokenEnvironmentVariable = "PCLLF_MMC_CMD_TOKEN";
    internal static Func<Process, SafeWaitHandle?, bool>? TerminateTreeOverride { get; set; }
    internal static int? CancellationGraceMillisecondsOverride { get; set; }

    public sealed record MmcRuntimeCommandOutcome(
        bool TreeProvenExited,
        string? Diagnostic,
        MmcCommandObservedProcess[] SurvivingProcesses);

    /// <summary>Existing instance command budget (contract §3, <c>MmcLaunchPreferences.cs:92</c>).</summary>
    public const int MaximumCommandLength = 16384;
    /// <summary>Bounded cancellation grace taken from the existing exit-confirmation retry interval.</summary>
    public const int CancellationGraceMilliseconds = 3000;

    private const int CancellationPollMilliseconds = 25;

    private static readonly string[] BareVariables = ["INST_MC_DIR", "INST_DIR"];

    /// <summary>
    /// Reads the stage's command text from the active package configuration. A source that is inactive
    /// (<c>OverrideCommands=false</c>) keeps but never executes its value, so this returns null.
    /// </summary>
    public static string? Read(MmcRuntimeCommandContext context, MmcRuntimeCommandStage stage) {
        ArgumentNullException.ThrowIfNull(context);
        return Read(context.PackageConfiguration, stage);
    }

    /// <summary>
    /// Reads the stage's command text directly from a package configuration. A source that is inactive
    /// (<c>OverrideCommands=false</c>) keeps but never executes its value, so this returns null.
    /// </summary>
    public static string? Read(PCLLF.MMCC.Core.MmcConfiguration? configuration, MmcRuntimeCommandStage stage) {
        if (configuration is null) return null;
        if (!Truthy(configuration.Values.GetValueOrDefault("OverrideCommands") ?? configuration.Values.GetValueOrDefault("OverrideLaunchCmd"))) return null;
        string? text = configuration.Values.GetValueOrDefault(Field(stage));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string Field(MmcRuntimeCommandStage stage) => stage switch {
        MmcRuntimeCommandStage.PreLoad => "PreLoadCommand",
        MmcRuntimeCommandStage.PreLaunch => "PreLaunchCommand",
        _ => "PostExitCommand"
    };

    public static string StageName(MmcRuntimeCommandStage stage) => Field(stage);

    /// <summary>
    /// Expands, validates and runs one stage command. <paramref name="planVariables"/> carries the values
    /// that only an existing runtime plan can supply; passing null means this stage precedes that plan.
    /// </summary>
    public static MmcRuntimeCommandResult Execute(MmcRuntimeCommandStage stage, MmcRuntimeCommandContext context,
        IReadOnlyDictionary<string, string>? planVariables, string command, Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        ExecuteCore(stage, context, planVariables, command, commandLease: null, out _, log, cancellationToken);

    public static MmcRuntimeCommandResult ExecuteTracked(MmcRuntimeCommandStage stage, MmcRuntimeCommandContext context,
        IReadOnlyDictionary<string, string>? planVariables, string command, MmcCommandLease commandLease,
        out MmcRuntimeCommandOutcome outcome, Action<string>? log = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(commandLease);
        return ExecuteCore(stage, context, planVariables, command, commandLease, out outcome, log, cancellationToken);
    }

    private static MmcRuntimeCommandResult ExecuteCore(MmcRuntimeCommandStage stage, MmcRuntimeCommandContext context,
        IReadOnlyDictionary<string, string>? planVariables, string command, MmcCommandLease? commandLease,
        out MmcRuntimeCommandOutcome outcome, Action<string>? log, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        outcome = new(TreeProvenExited: true, null, []);
        Validate(context.GameRoot, command);
        string redacted = Redact(command, context.RedactionSecrets);
        string expanded = Expand(command, Variables(context, planVariables), context.RedactionSecrets);
        cancellationToken.ThrowIfCancellationRequested();

        bool usingCustomStart = !ReferenceEquals(StartProcess, DefaultStartProcess);
        if (!usingCustomStart && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MMC 包内命令默认执行器仅支持 Windows cmd.exe 与受管理的 Windows Job 进程组。");

        // The log entry is built and published only after static checks and pre-start cancellation gates pass,
        // and carries the redacted template only: an expanded command text never reaches the log.
        log?.Invoke("执行 MMC 包内命令（" + StageName(stage) + "）：" + redacted);

        string commandToken = commandLease?.Intent.CommandToken ?? Guid.NewGuid().ToString("N");
        string? jobName = commandLease?.Intent.JobName;
        long createdUtcTicks = commandLease?.Intent.CreatedUtcTicks ?? DateTime.UtcNow.Ticks;
        ProcessStartInfo start = BuildStartInfo(stage, context, expanded, commandToken);

        MmcSessionProcessTree? tree = null;
        SafeWaitHandle? manualJob = null;
        Process? process = null;
        bool jobAssigned = false;
        bool outcomeDecided = false;

        try {
            if (!usingCustomStart && OperatingSystem.IsWindows()) {
                string cmdExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                tree = MmcSessionProcessTree.Start(
                    cmdExePath,
                    start,
                    redirectPipes: false,
                    validateSuspendedProcess: proc => RecordStartedProcess(commandLease, proc, jobBacked: true, throwOnFailure: true),
                    jobName: jobName,
                    allowRawArguments: true);
                process = tree.EntryProcess;
                jobAssigned = true;
            } else {
                if (OperatingSystem.IsWindows()) manualJob = MmcSessionProcessTree.CreateConfiguredJobHandle(jobName);
                process = StartProcess(start);
                if (process is null) throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND", "包内命令进程无法创建。");
                if (manualJob is not null) {
                    jobAssigned = MmcSessionProcessTree.TryAssignProcessToJob(manualJob, process, out _);
                }
                RecordStartedProcess(commandLease, process, jobAssigned, throwOnFailure: false);
            }

            while (true) {
                if (cancellationToken.IsCancellationRequested) {
                    var teardown = TeardownAndProveGone(process, tree, manualJob, jobAssigned, commandToken, createdUtcTicks);
                    outcome = teardown;
                    outcomeDecided = true;
                    if (!teardown.TreeProvenExited) {
                        throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_UNCONFIRMED",
                            "包内命令取消后仍无法确认整个命令进程组已退出：" + teardown.Diagnostic);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                bool rootExited = SafeHasExited(process);
                bool jobQueryOk = TryGetActiveJobPids(tree, manualJob, out int[] remainingJobPids, out int jobQueryErr);
                if (rootExited && (!jobAssigned || (jobQueryOk && remainingJobPids.Length == 0))) {
                    if (jobAssigned) break;
                    var fallbackScan = MmcCommandOperations.ProbeLiveProcessesForCommandToken(commandToken, createdUtcTicks, SafeProcessId(process), out int? unmatchedPid);
                    if (fallbackScan == MmcCommandOperations.TokenScanResult.ProvenAbsent) break;
                    if (fallbackScan == MmcCommandOperations.TokenScanResult.Unobservable) {
                        outcome = new(false, "存在无法验证环境块的近期/高权限或跨会话进程，无法确认包内命令后代已清空。", []);
                        outcomeDecided = true;
                        throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_UNCONFIRMED", outcome.Diagnostic!);
                    }
                    Thread.Sleep(CancellationPollMilliseconds);
                    continue;
                }

                if (!rootExited) process.WaitForExit(CancellationPollMilliseconds);
                else Thread.Sleep(CancellationPollMilliseconds);
            }

            int exitCode = process.ExitCode;
            string text = exitCode.ToString(CultureInfo.InvariantCulture) + "（0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture) + "）";
            var result = new MmcRuntimeCommandResult(StageName(stage), redacted, exitCode, text);
            log?.Invoke("MMC 包内命令（" + result.Stage + "）退出码 " + text);
            outcome = new(TreeProvenExited: true, null, []);
            outcomeDecided = true;
            // PreLoad and PreLaunch may change inputs and therefore refuse the launch; PostExit only records
            // a diagnostic and never rolls back the session that already ended.
            if (exitCode != 0 && stage is not MmcRuntimeCommandStage.PostExit)
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_EXIT",
                    "包内命令（" + result.Stage + "）以非零退出码结束，拒绝继续启动：" + text);
            return result;
        } finally {
            if (!outcomeDecided && process is not null) {
                var teardown = TeardownAndProveGone(process, tree, manualJob, jobAssigned, commandToken, createdUtcTicks);
                outcome = teardown;
                outcomeDecided = true;
            }
            manualJob?.Dispose();
            tree?.Dispose();
            process?.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(MmcRuntimeCommandStage stage, MmcRuntimeCommandContext context, string text, string commandToken) {
        // cmd.exe does not parse C-runtime backslash-quote escaping (`\"`), so passing quoted paths
        // through ProcessStartInfo.ArgumentList turns `"path"` into `\"path\"`. Using `/d /s /c "<command>"`
        // in Arguments makes cmd.exe disable AutoRun (/d) and strip only the outermost quotes (/s)
        // while delivering the command text with embedded quotes verbatim.
        string arguments = "/d /s /c \"" + text + "\"";
        MmcRuntimeWindowsCommandLine.ValidateVerbatim("cmd.exe", arguments);
        var start = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = context.GameRoot,
            Arguments = arguments
        };
        if (stage is MmcRuntimeCommandStage.PreLaunch or MmcRuntimeCommandStage.PostExit) {
            ImmutableArray<PCLLF.MMCC.Core.MmcEnvironmentVariable> mapping = context.PackageConfiguration?.EnvironmentOverride ?? [];
            if (mapping.IsDefault)
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV_SETTINGS", "包内环境映射必须为显式列表。");
            if (!string.IsNullOrWhiteSpace(context.TemporaryDirectory)) {
                string temporaryDirectory = Path.GetFullPath(context.TemporaryDirectory);
                if (!Path.IsPathFullyQualified(context.TemporaryDirectory) || !MmcInstancePaths.CheckDirectoryRole(temporaryDirectory))
                    throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV_SETTINGS", "会话临时目录必须是已物化的绝对路径。");
                MmcRuntimeEnvironmentOverlay.Apply(start.Environment, mapping, temporaryDirectory);
            } else if (!mapping.IsEmpty) {
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV_SETTINGS", "包内环境覆盖要求活动会话的临时目录。");
            }
        }
        start.Environment[CommandTokenEnvironmentVariable] = commandToken;
        return start;
    }

    private static void RecordStartedProcess(MmcCommandLease? commandLease, Process process, bool jobBacked, bool throwOnFailure) {
        if (commandLease is null) return;
        try {
            int pid = process.Id;
            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            MmcExecutableIdentity? image = null;
            try { image = MmcSessions.ObserveProcessExecutable(process); } catch { }
            commandLease.RecordProcessStarted(pid, startTicks, image, jobBacked);
        } catch when (!throwOnFailure) {
            // Custom fixture boundary only: real suspended production processes fail closed before ResumeThread.
        }
    }

    private static MmcRuntimeCommandOutcome TeardownAndProveGone(
        Process process,
        MmcSessionProcessTree? tree,
        SafeWaitHandle? manualJob,
        bool jobAssigned,
        string commandToken,
        long createdUtcTicks) {
        SafeWaitHandle? activeJob = tree?.JobHandle ?? manualJob;
        bool hasJobHandle = activeJob is not null && !activeJob.IsInvalid && !activeJob.IsClosed;
        bool terminationSucceeded = true;
        string? terminationError = null;

        var customTerminate = TerminateTreeOverride;
        if (customTerminate is not null) {
            try {
                if (!customTerminate(process, activeJob)) {
                    terminationSucceeded = false;
                    terminationError = "包内命令进程组终止回调返回失败。";
                }
            } catch (Exception ex) {
                terminationSucceeded = false;
                terminationError = ex.Message;
            }
        } else {
            if (tree is not null) {
                if (!tree.Terminate(out int jobErr) && (!TryGetActiveJobPids(tree, manualJob, out int[] afterErrPids, out _) || afterErrPids.Length > 0)) {
                    terminationSucceeded = false;
                    terminationError = $"TerminateJobObject 失败 (Win32={jobErr})。";
                }
            } else if (manualJob is not null && !manualJob.IsInvalid && !manualJob.IsClosed) {
                if (!MmcSessionProcessTree.TryTerminateJobHandle(manualJob, out int jobErr) && (!TryGetActiveJobPids(tree, manualJob, out int[] afterErrPids, out _) || afterErrPids.Length > 0)) {
                    terminationSucceeded = false;
                    terminationError = $"TerminateJobObject 失败 (Win32={jobErr})。";
                }
            }

            if (!SafeHasExited(process)) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) {
                    if (!SafeHasExited(process) || !TryGetActiveJobPids(tree, manualJob, out int[] afterKillPids, out _) || afterKillPids.Length > 0) {
                        terminationSucceeded = false;
                        terminationError = terminationError is null ? ex.Message : terminationError + "；" + ex.Message;
                    }
                }
            }
        }

        int graceMs = CancellationGraceMillisecondsOverride ?? CancellationGraceMilliseconds;
        long until = Environment.TickCount64 + Math.Max(0, graceMs);
        while (Environment.TickCount64 < until) {
            bool exitedNow = SafeHasExited(process);
            bool emptyNow = TryGetActiveJobPids(tree, manualJob, out int[] loopPids, out _) && loopPids.Length == 0;
            if (exitedNow && emptyNow) break;
            Thread.Sleep(CancellationPollMilliseconds);
        }

        bool rootExited = SafeHasExited(process);
        bool jobQueryOk = TryGetActiveJobPids(tree, manualJob, out int[] remainingJobPids, out int jobQueryErr);
        int? rootPid = SafeProcessId(process);
        int? tokenMatchedPid = null;
        var tokenState = MmcCommandOperations.TokenScanResult.ProvenAbsent;
        if (!jobAssigned || !jobQueryOk || !rootExited || remainingJobPids.Length > 0 || !terminationSucceeded) {
            tokenState = MmcCommandOperations.ProbeLiveProcessesForCommandToken(commandToken, createdUtcTicks, rootPid, out tokenMatchedPid);
        }

        var surviving = SnapshotSurvivingProcesses(process, rootExited, remainingJobPids, tokenMatchedPid);
        bool provenGone = terminationSucceeded &&
                          rootExited &&
                          (!hasJobHandle || (jobQueryOk && remainingJobPids.Length == 0)) &&
                          (jobAssigned || tokenState == MmcCommandOperations.TokenScanResult.ProvenAbsent) &&
                          tokenMatchedPid is null &&
                          surviving.Length == 0;
        if (provenGone) return new(true, null, []);

        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(terminationError)) reasons.Add(terminationError);
        if (!rootExited) reasons.Add("根进程仍在运行或无法确认退出");
        if (!jobQueryOk && hasJobHandle) reasons.Add($"无法查询命令作业进程列表 (Win32={jobQueryErr})");
        if (remainingJobPids.Length > 0) reasons.Add($"作业组尚有存活 PID ({string.Join(",", remainingJobPids)})");
        if (tokenMatchedPid is int tpid) reasons.Add($"命令令牌探测到存活后代 PID={tpid}");
        else if ((!jobAssigned || !jobQueryOk) && tokenState != MmcCommandOperations.TokenScanResult.ProvenAbsent) reasons.Add("无法排除包内命令后代进程存活");
        if (reasons.Count == 0) reasons.Add("无法获得整个命令进程组退出证明");
        return new(false, string.Join("；", reasons), surviving);
    }

    private static bool TryGetActiveJobPids(MmcSessionProcessTree? tree, SafeWaitHandle? manualJob, out int[] pids, out int win32Error) {
        if (tree is not null) return tree.TryObserveProcessIds(out pids, out win32Error);
        if (manualJob is not null && !manualJob.IsInvalid && !manualJob.IsClosed)
            return MmcSessionProcessTree.TryObserveJobProcessIds(manualJob, out pids, out win32Error);
        pids = [];
        win32Error = 0;
        return true;
    }

    private static bool SafeHasExited(Process process) {
        try { return process.HasExited; }
        catch { return false; }
    }

    private static int? SafeProcessId(Process process) {
        try { return process.Id; }
        catch { return null; }
    }

    private static MmcCommandObservedProcess[] SnapshotSurvivingProcesses(
        Process rootProcess,
        bool rootExited,
        int[] remainingJobPids,
        int? tokenMatchedPid) {
        var list = new List<MmcCommandObservedProcess>();
        var seen = new HashSet<int>();
        if (!rootExited && SafeProcessId(rootProcess) is int rpid && seen.Add(rpid))
            TryAddObserved(list, rootProcess, rpid);
        foreach (int pid in remainingJobPids)
            if (seen.Add(pid)) TryAddObservedById(list, pid);
        if (tokenMatchedPid is int tpid && seen.Add(tpid))
            TryAddObservedById(list, tpid);
        return list.ToArray();
    }

    private static void TryAddObservedById(List<MmcCommandObservedProcess> target, int pid) {
        try {
            using Process proc = Process.GetProcessById(pid);
            if (!proc.HasExited) TryAddObserved(target, proc, pid);
        } catch { }
    }

    private static void TryAddObserved(List<MmcCommandObservedProcess> target, Process process, int pid) {
        try {
            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            MmcExecutableIdentity? image = null;
            try { image = MmcSessions.ObserveProcessExecutable(process); } catch { }
            target.Add(new(pid, startTicks, image));
        } catch {
            target.Add(new(pid, 0, null));
        }
    }

    private static void Validate(string gameRoot, string command) {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Path.IsPathFullyQualified(gameRoot))
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_WORKING_DIRECTORY", "包内命令要求已解析的 GameRoot 绝对工作目录。");
        MmcInstancePaths.CheckDirectoryRole(gameRoot);
        if (!Directory.Exists(gameRoot))
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_WORKING_DIRECTORY", "GameRoot 尚未物化，包内命令不能执行。");
        MmcInstancePaths.EnsureNoLinks(gameRoot);
        if (command.Contains('\0'))
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND", "包内命令文本包含 NUL。");
        if (command.Length > MaximumCommandLength)
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_LENGTH", "包内命令超过 16384 字符上限。");
    }

    /// <summary>
    /// The command variable surface. Account, session and authentication values are absent by construction,
    /// so a credential variable reference fails as an unknown variable instead of being silently bound.
    /// </summary>
    private static Dictionary<string, string> Variables(MmcRuntimeCommandContext context, IReadOnlyDictionary<string, string>? planVariables) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["game_directory"] = CommandPath(context.GameRoot),
            ["instance_directory"] = CommandPath(context.InstanceRoot),
            ["INST_DIR"] = CommandPath(context.InstanceRoot),
            ["INST_MC_DIR"] = CommandPath(context.GameRoot),
            ["launcher_name"] = context.LauncherName,
            ["launcher_version"] = context.LauncherVersion,
            ["resolution_width"] = context.ResolutionWidth.ToString(CultureInfo.InvariantCulture),
            ["resolution_height"] = context.ResolutionHeight.ToString(CultureInfo.InvariantCulture),
            ["classpath_separator"] = Path.PathSeparator.ToString()
        };
        if (planVariables is not null)
            foreach (var pair in planVariables) result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>
    /// One pass over the template with the runtime dictionary. An unknown key is an explicit refusal: a
    /// requirement this host cannot honour is never silently replaced by an empty string. A dollar with no
    /// matching key stays literal, and the bare spellings keep their word-boundary rule.
    /// </summary>
    private static string Expand(string template, Dictionary<string, string> variables, ImmutableArray<string> secrets) {
        StringBuilder builder = new(); int position = 0;
        while (position < template.Length) {
            int start = template.IndexOf('$', position);
            if (start < 0) { builder.Append(template, position, template.Length - position); break; }
            builder.Append(template, position, start - position);
            string? key = null; int end = start;
            if (start + 1 < template.Length && template[start + 1] == '{') {
                end = template.IndexOf('}', start + 2);
                if (end < 0) throw new MmcRuntimeLaunchException("MMC_RUNTIME_VARIABLE", "包内命令引用了未闭合的启动变量。");
                key = template[(start + 2)..end]; end++;
            } else {
                foreach (string bare in BareVariables) {
                    int stop = start + bare.Length + 1;
                    if (stop <= template.Length && template.AsSpan(start + 1, bare.Length).SequenceEqual(bare) &&
                        (stop == template.Length || !char.IsAsciiLetterOrDigit(template[stop]) && template[stop] != '_')) { key = bare; end = stop; break; }
                }
            }
            if (key is null) { builder.Append('$'); position = start + 1; continue; }
            if (!variables.TryGetValue(key, out string? value))
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_VARIABLE", "包内命令引用了未知或此阶段没有值的启动变量：" + key);
            builder.Append(value); position = end;
        }
        string expanded = builder.ToString();
        if (expanded.Contains('\0')) throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND", "包内命令展开结果包含 NUL。");
        return Redact(expanded, secrets);
    }

    public static string Redact(string text, ImmutableArray<string> secrets) {
        if (secrets.IsDefaultOrEmpty) return text;
        if (secrets.Length == 1) {
            string single = secrets[0];
            return single is { Length: >= 4 } ? text.Replace(single, "[redacted]", StringComparison.Ordinal) : text;
        }
        // Replace longer secrets first so a shorter prefix secret cannot leave the remainder of a longer secret unredacted.
        var ordered = new string[secrets.Length];
        secrets.CopyTo(ordered);
        Array.Sort(ordered, static (left, right) => (right?.Length ?? 0).CompareTo(left?.Length ?? 0));
        foreach (string secret in ordered) {
            if (secret is null || secret.Length < 4) break;
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Paths reach a command in the same forward-slash Java form the launch variables already use.</summary>
    public static string CommandPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    private static bool Truthy(string? value) => value?.ToLowerInvariant() is "true" or "1";
}
