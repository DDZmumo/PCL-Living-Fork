using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>
/// The package <c>WrapperCommand</c> layer: an outer program that becomes the process entry while the managed
/// Java runtime becomes its child (contract §1.2 "Java 包装").
///
/// This is deliberately a different capability from the launcher's own
/// <see cref="MmcRuntimeWrapperMode.JavaWrapper"/> and from ForgeWrapper (design L337). The launcher wrapper
/// decides <em>how the JVM is started</em> and is already reflected in the Java argv by
/// <c>MmcRuntimeLaunchBuilder</c>. The package wrapper decides <em>who wraps the final command line</em> and is
/// applied here, on top of whatever the inner Java invocation already is. The two therefore compose instead of
/// excluding each other: the wrapper is the outer entry in both modes, and the inner argv keeps the
/// JavaWrapper/DirectMain shape it would have had without a package wrapper.
///
/// Nothing here starts a process. The host creates every managed process
/// (<see cref="MmcSessions"/>); this type only computes the entry and its arguments.
/// </summary>
public static class MmcRuntimeWrapperCommand {
    /// <summary>Same instance budget as every other package command (contract §3).</summary>
    public const int MaximumCommandLength = 16384;

    private static readonly string[] BareVariables = ["INST_MC_DIR", "INST_DIR"];

    /// <summary>
    /// Reads the active package wrapper text. An inactive source (<c>OverrideCommands=false</c>) keeps its
    /// value and never wraps, and a blank value declares no wrapper.
    /// </summary>
    public static string? Read(PCLLF.MMCC.Core.MmcConfiguration? configuration) {
        if (configuration is null) return null;
        if (!Truthy(configuration.Values.GetValueOrDefault("OverrideCommands") ?? configuration.Values.GetValueOrDefault("OverrideLaunchCmd"))) return null;
        string? text = configuration.Values.GetValueOrDefault("WrapperCommand");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Wraps one already-built Java invocation. <paramref name="start"/> is the inner invocation exactly as the
    /// launch plan produced it — including the mode-specific argv and the host environment overlay — and the
    /// returned value has the wrapper as its entry, the inner executable and its arguments appended after the
    /// wrapper's own tokens, and the same environment, working directory and stream settings.
    ///
    /// Every inner argument is re-emitted through <see cref="ProcessStartInfo.ArgumentList"/>, so the wrapper
    /// receives each one as one argument: the inner argv is never joined into a string and never re-split.
    /// </summary>
    public static ProcessStartInfo Apply(ProcessStartInfo start, string command,
        IReadOnlyDictionary<string, string> variables) {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(variables);
        ImmutableArray<string> tokens = Tokenize(command, variables);
        string program = tokens[0];
        // The wrapper entry is resolved relative to the instance GameRoot, matching the working-directory
        // contract. A bare name is left to the OS search path, which is the ordinary meaning of a command.
        string entry = program;
        if (!Path.IsPathFullyQualified(program) && program.IndexOfAny(['\\', '/']) >= 0) {
            MmcInstancePaths.CheckDirectoryRole(start.WorkingDirectory);
            entry = Path.GetFullPath(Path.Combine(start.WorkingDirectory, program));
        }
        ProcessStartInfo wrapped = new(entry) {
            UseShellExecute = false, WorkingDirectory = start.WorkingDirectory, CreateNoWindow = start.CreateNoWindow,
            RedirectStandardInput = start.RedirectStandardInput, RedirectStandardOutput = start.RedirectStandardOutput,
            RedirectStandardError = start.RedirectStandardError, WindowStyle = start.WindowStyle,
            StandardInputEncoding = start.StandardInputEncoding, StandardOutputEncoding = start.StandardOutputEncoding,
            StandardErrorEncoding = start.StandardErrorEncoding
        };
        for (int index = 1; index < tokens.Length; index++) wrapped.ArgumentList.Add(tokens[index]);
        // The inner invocation becomes the wrapper's child invocation: executable first, then its exact argv.
        wrapped.ArgumentList.Add(start.FileName);
        foreach (string argument in start.ArgumentList) wrapped.ArgumentList.Add(argument);
        wrapped.Environment.Clear();
        foreach (var pair in start.Environment) wrapped.Environment[pair.Key] = pair.Value;
        return wrapped;
    }

    /// <summary>
    /// Splits the wrapper template with the repository's frozen command tokenizer and then expands each token.
    ///
    /// The order matters and is deliberate. Expanding first and splitting afterwards would re-split a variable's
    /// value: an instance root containing a space (very common on Windows) would silently become two arguments,
    /// so a correct-looking package wrapper would start the wrong program with the wrong arguments. Splitting
    /// first keeps one template token equal to one argument, which is the argv reading of a wrapper command and
    /// the same treatment <c>+jvmArgs</c> already gives its members. Expansion still happens exactly once, the
    /// platform escape still happens only at the final process boundary, and an unknown variable is still
    /// refused rather than replaced by an empty string.
    /// </summary>
    public static ImmutableArray<string> Tokenize(string command, IReadOnlyDictionary<string, string> variables) {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(variables);
        if (command.Contains('\0')) throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER", "包内包装命令包含 NUL。");
        if (command.Length > MaximumCommandLength) throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER_LENGTH", "包内包装命令超过 16384 字符上限。");
        ImmutableArray<string> templates;
        try { templates = PCLLF.MMCC.Core.MmcArgumentTokenizer.Tokenize(command); }
        catch (FormatException) { throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER", "包内包装命令的引号或转义无效。"); }
        if (templates.IsEmpty) throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER", "包内包装命令没有可执行入口。");
        var tokens = ImmutableArray.CreateBuilder<string>(templates.Length);
        foreach (string template in templates) tokens.Add(Expand(template, variables));
        string program = tokens[0];
        if (program.Length > MaximumCommandLength) throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER_LENGTH", "包内包装命令入口超过 16384 字符上限。");
        return tokens.ToImmutable();
    }

    /// <summary>
    /// The wrapper template's variable surface. It is the same plan-derived dictionary the stage commands use
    /// (<see cref="MmcRuntimeLaunchBuilder.CommandVariables"/>), so a wrapper never binds account, session or
    /// authentication values: an unknown key is refused rather than silently replaced.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variables(MmcRuntimeLaunchPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        var result = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["game_directory"] = MmcRuntimeCommandExecution.CommandPath(plan.Request.Paths.GameRoot),
            ["instance_directory"] = MmcRuntimeCommandExecution.CommandPath(plan.Request.Paths.VersionRoot),
            ["INST_DIR"] = MmcRuntimeCommandExecution.CommandPath(plan.Request.Paths.VersionRoot),
            ["INST_MC_DIR"] = MmcRuntimeCommandExecution.CommandPath(plan.Request.Paths.GameRoot),
            ["launcher_name"] = plan.Request.Settings.LauncherName,
            ["launcher_version"] = plan.Request.Settings.LauncherVersion,
            ["resolution_width"] = plan.Request.Settings.ResolutionWidth.ToString(CultureInfo.InvariantCulture),
            ["resolution_height"] = plan.Request.Settings.ResolutionHeight.ToString(CultureInfo.InvariantCulture),
            ["classpath_separator"] = Path.PathSeparator.ToString()
        };
        return result;
    }

    /// <summary>
    /// The single expansion pass. A plan-only variable is genuinely unavailable here and is refused rather than
    /// guessed; the caller that has a plan supplies its dictionary through <see cref="Variables"/>.
    /// </summary>
    private static string Expand(string template, IReadOnlyDictionary<string, string> variables) {
        StringBuilder builder = new(); int position = 0;
        while (position < template.Length) {
            int start = template.IndexOf('$', position);
            if (start < 0) { builder.Append(template, position, template.Length - position); break; }
            builder.Append(template, position, start - position);
            string? key = null; int end = start;
            if (start + 1 < template.Length && template[start + 1] == '{') {
                end = template.IndexOf('}', start + 2);
                if (end < 0) throw new MmcRuntimeLaunchException("MMC_RUNTIME_VARIABLE", "包内包装命令引用了未闭合的启动变量。");
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
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_VARIABLE", "包内包装命令引用了未知的启动变量：" + key);
            builder.Append(value); position = end;
        }
        string expanded = builder.ToString();
        if (expanded.Contains('\0')) throw new MmcRuntimeLaunchException("MMC_RUNTIME_WRAPPER", "包内包装命令展开结果包含 NUL。");
        return expanded;
    }

    private static bool Truthy(string? value) => value?.ToLowerInvariant() is "true" or "1";
}
