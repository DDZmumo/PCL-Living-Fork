using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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
}

public sealed record MmcRuntimeCommandResult(string Stage, string RedactedCommand, int ExitCode, string ExitCodeText);

/// <summary>
/// Executes the package command stages (<c>PreLoadCommand</c>, <c>PreLaunchCommand</c>, <c>PostExitCommand</c>)
/// exactly as frozen by <c>docs/mmc-phase2-command-contract.md</c>.
///
/// This type is the single production boundary for the stage semantics, so every rule below is exercised
/// without constructing a launcher: the execution host is <c>cmd.exe</c> driven by
/// <see cref="ProcessStartInfo.ArgumentList"/> (never one concatenated <c>Arguments</c> string), the working
/// directory is always the instance GameRoot, expansion uses one runtime dictionary applied once, and a log
/// line carries only the redacted form of the command — an expanded value never reaches the log.
///
/// The stage knows nothing about the launch plan. A caller that has one supplies the plan-derived half of the
/// runtime dictionary as <c>planVariables</c>; a stage that runs before the plan exists supplies none, and
/// then a plan-only variable is genuinely unavailable and is refused rather than guessed.
/// </summary>
public static class MmcRuntimeCommandExecution {
    /// <summary>
    /// Host-injected process entry. The managed launch adapter points this at the launcher's own
    /// <c>StartProcess</c> so an operator-visible failure mode is preserved; the fallback is the ordinary
    /// process entry used by controlled verification.
    /// </summary>
    public static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = start => Process.Start(start);

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
        var configuration = context.PackageConfiguration;
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
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        Validate(context.GameRoot, command);
        string redacted = Redact(command, context.RedactionSecrets);
        // The log entry is built and published before anything else, and it is the redacted template only:
        // an expanded command text is never written, not even at failure time.
        log?.Invoke("执行 MMC 包内命令（" + StageName(stage) + "）：" + redacted);
        string expanded = Expand(command, Variables(context, planVariables), context.RedactionSecrets);
        using var process = Start(context.GameRoot, expanded);
        if (process is null) throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND", "包内命令进程无法创建。");
        try {
            while (!process.WaitForExit(CancellationPollMilliseconds)) {
                if (!cancellationToken.IsCancellationRequested) continue;
                // Cancellation teardown: terminate the whole managed tree, then prove it is gone within a
                // bounded window. "Kill was sent" is never accepted as the proof.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                long until = Environment.TickCount64 + CancellationGraceMilliseconds;
                while (!process.HasExited && Environment.TickCount64 < until) process.WaitForExit(CancellationPollMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
                // Not cancelable and no longer running: fall through to the ordinary exit handling.
                break;
            }
            int exitCode = process.ExitCode;
            string text = exitCode.ToString(CultureInfo.InvariantCulture) + "（0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture) + "）";
            var result = new MmcRuntimeCommandResult(StageName(stage), redacted, exitCode, text);
            log?.Invoke("MMC 包内命令（" + result.Stage + "）退出码 " + text);
            // PreLoad and PreLaunch may change inputs and therefore refuse the launch; PostExit only records
            // a diagnostic and never rolls back the session that already ended.
            if (exitCode != 0 && stage is not MmcRuntimeCommandStage.PostExit)
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_EXIT",
                    "包内命令（" + result.Stage + "）以非零退出码结束，拒绝继续启动：" + text);
            return result;
        } finally {
            if (!process.HasExited) {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static Process? Start(string gameRoot, string text) {
        // cmd.exe with an ArgumentList is the frozen execution host. Concatenating one Arguments string
        // (the legacy form) is exactly what the contract forbids, so it is never constructed here.
        var start = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = gameRoot
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(text);
        return StartProcess(start);
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

    internal static string Redact(string text, ImmutableArray<string> secrets) {
        if (secrets.IsDefaultOrEmpty) return text;
        foreach (string secret in secrets) {
            if (secret is null || secret.Length < 4) continue;
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Paths reach a command in the same forward-slash Java form the launch variables already use.</summary>
    public static string CommandPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    private static bool Truthy(string? value) => value?.ToLowerInvariant() is "true" or "1";
}
