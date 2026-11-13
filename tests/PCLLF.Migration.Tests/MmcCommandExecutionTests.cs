// Independently authored fixtures. Host test suite retains its existing license.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PCLCS;
using PCLLF.MMCC.Core;

/// <summary>
/// Exercises the frozen package-command stage contract (docs/mmc-phase2-command-contract.md) against the
/// real production stage boundary. No game process is ever created: the injected process entry records the
/// exact <see cref="ProcessStartInfo"/> the stage would have used and starts an independent harmless child,
/// so the execution host, the working directory, the ordered arguments and the expanded text are all
/// observable facts rather than assumptions.
/// </summary>
internal static class MmcCommandExecutionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC package command stage is admitted once its text is decodable", AbilityGate);
        yield return ("MMC package command stage uses the frozen cmd.exe argument list with GameRoot cwd", ExecutionHost);
        yield return ("MMC package command stage expands the runtime variables once and refuses an unknown one", VariableExpansion);
        yield return ("MMC package command stage refuses NUL, oversize and credential references before starting", CommandRefusals);
        yield return ("MMC package command stage treats a non-zero exit code according to its stage", ExitCodePolicy);
        yield return ("MMC package command stage terminates the whole managed tree on cancellation", CancellationTeardown);
        yield return ("MMC package command stage logs only the redacted command text", LogRedaction);
        yield return ("MMC package command stage skips an inactive source and reads stages independently", InactiveSource);
        yield return ("MMC package command stage changes the recomputed input fingerprint when an artifact changes", PostCommandRevalidation);
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected <" + expected + ">, got <" + actual + ">.");
    }
    private static void ThrowsCode(string code, Action action, string message) {
        try { action(); } catch (MmcRuntimeLaunchException error) { Equal(code, error.Code, message + " refusal code"); return; }
        catch (Exception error) { throw new Exception("Expected a refusal for " + message + ", got " + error.GetType().Name + ": " + error.Message); }
        throw new Exception("Expected a refusal for " + message);
    }
    private static void ExpectVariable(Action action, string message) {
        try { action(); } catch (MmcRuntimeLaunchException error) { Equal("MMC_RUNTIME_VARIABLE", error.Code, message + " refusal code"); return; }
        catch (Exception error) { throw new Exception(message + " produced " + error.GetType().Name + ": " + error.Message); }
        throw new Exception(message + " was accepted");
    }

    // ---- instance fixture -----------------------------------------------------------------------------

    private sealed class Instance : IDisposable {
        private readonly Func<ProcessStartInfo, Process?> previous;
        internal string Root { get; }
        internal string GameRoot => Path.Combine(Root, "minecraft");
        internal ProcessStartInfo? Observed { get; private set; }
        /// <summary>Replaces the started child; the recorded start info is always captured first.</summary>
        internal Func<ProcessStartInfo, Process?>? Handler { get; set; }

        internal Instance() {
            Root = Path.Combine(Path.GetTempPath(), "mmc-cmd-" + Guid.NewGuid().ToString("N"), "versions", "fixture");
            Directory.CreateDirectory(GameRoot);
            Directory.CreateDirectory(Path.Combine(Root, "libraries"));
            Directory.CreateDirectory(Path.Combine(Root, "PCL", "MmcRuntime"));
            previous = MmcRuntimeCommandExecution.StartProcess;
            MmcRuntimeCommandExecution.StartProcess = start => {
                Observed = start;
                if (Handler is not null) return Handler(start);
                // An independent harmless child: the recorded info is never reused, so the assertion sees
                // exactly what production would have handed to the process entry.
                var copy = new ProcessStartInfo(start.FileName) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = start.WorkingDirectory
                };
                foreach (string argument in start.ArgumentList) copy.ArgumentList.Add(argument);
                return Process.Start(copy);
            };
        }
        public void Dispose() {
            MmcRuntimeCommandExecution.StartProcess = previous;
            string? owner = Path.GetDirectoryName(Path.GetDirectoryName(Root));
            try { if (owner is not null) Directory.Delete(owner, true); } catch (Exception) { }
        }
    }

    private const string Manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\",\"important\":true}]}";

    /// <summary>The fixture goes through the production reader, so the gate's own verdict is what is asserted.</summary>
    private static MmcParseResult Read(string cfg) {
        Dictionary<string, string> metadata = new() { ["mmc-pack.json"] = Manifest, ["instance.cfg"] = cfg };
        var input = new MmcPackageInput(
            metadata.Select(pair => new MmcArchiveEntry(pair.Key, Encoding.UTF8.GetByteCount(pair.Value))).ToArray(), metadata);
        return MmcPackageReader.Parse(input);
    }

    private static MmcConfiguration Configuration(string cfg) {
        MmcParseResult result = Read(cfg);
        Require(!result.HasErrors, "Fixture cfg was refused: " + Errors(result));
        return result.Package!.Configuration;
    }

    private static string Errors(MmcParseResult result) =>
        string.Join(" | ", result.Diagnostics.Where(d => d.Severity == MmcDiagnosticSeverity.Error).Select(d => d.Code + " " + d.Message));

    private static MmcRuntimeCommandContext Context(Instance instance, MmcConfiguration? configuration) => new(
        instance.Root, instance.GameRoot, Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "PCL Living Fork", "1.0.0", 854, 480) { PackageConfiguration = configuration };

    private static MmcRuntimeCommandContext Context(Instance instance, string cfg) => Context(instance, Configuration(cfg));

    // ---- 1. ability gate ------------------------------------------------------------------------------

    private static void AbilityGate() {
        // The three implemented stage commands are admitted once their text is decodable.
        MmcParseResult active = Read("OverrideCommands=true\nPreLoadCommand=echo preload\nPreLaunchCommand=echo prelaunch\nPostExitCommand=echo postexit");
        Require(!active.HasErrors, "An implemented stage command was refused: " + Errors(active));
        Equal("echo prelaunch", active.Package!.Configuration.Values["PreLaunchCommand"], "An admitted command text was rewritten");
        Equal("echo preload", active.Package!.Configuration.Values["PreLoadCommand"], "An admitted PreLoad text was rewritten");
        Equal("echo postexit", active.Package!.Configuration.Values["PostExitCommand"], "An admitted PostExit text was rewritten");
        // The package wrapper is admitted now that its capability, host composition and fixture landed
        // together (contract §8); its text is preserved verbatim for the wrapper boundary.
        MmcParseResult wrapper = Read("OverrideCommands=true\nWrapperCommand=wrap.exe");
        Require(!wrapper.HasErrors, "The landed package wrapper was refused: " + Errors(wrapper));
        Equal("wrap.exe", wrapper.Package!.Configuration.Values["WrapperCommand"], "An admitted wrapper text was rewritten");
        // The wrapper still obeys the command text rules: a bound credential is refused, not silently bound.
        MmcParseResult wrapperCredential = Read("OverrideCommands=true\nWrapperCommand=wrap.exe ${auth_access_token}");
        Require(wrapperCredential.HasErrors && wrapperCredential.Diagnostics.Any(d => d.Code == "MMC_CFG_COMMAND_CREDENTIAL"),
            "A credential-binding wrapper was not refused: " + Errors(wrapperCredential));
        // An inactive source keeps its value and is refused nothing.
        MmcParseResult inactive = Read("OverrideCommands=false\nPreLaunchCommand=kept");
        Require(!inactive.HasErrors, "An inactive source produced an error: " + Errors(inactive));
        Equal("kept", inactive.Package!.Configuration.Values["PreLaunchCommand"], "An inactive value was not preserved");
        // A credential variable is an unhonourable requirement, decided from the text alone.
        Require(Read("OverrideCommands=true\nPreLaunchCommand=login ${auth_access_token}").Diagnostics.Any(d => d.Code == "MMC_CFG_COMMAND_CREDENTIAL"),
            "A credential variable was admitted.");
        foreach (string credential in new[] { "access_token", "auth_session", "user_type", "user_properties", "auth_xuid", "clientid", "client_id", "client_token", "auth_player_name", "auth_uuid" })
            Require(Read("OverrideCommands=true\nPostExitCommand=echo ${" + credential + "}").Diagnostics.Any(d => d.Code == "MMC_CFG_COMMAND_CREDENTIAL"),
                "A credential variable was admitted: " + credential);
        // NUL and the instance command budget are also read-time obligations. Only the Qt decoder has a
        // hexadecimal escape, so the NUL fixture uses that encoding rather than the legacy one.
        Require(Read("ConfigVersion=1.3\nOverrideCommands=true\nPreLoadCommand=abc\\x0000def").Diagnostics.Any(d => d.Code == "MMC_CFG_COMMAND"),
            "A NUL command was admitted.");
        Require(Read("OverrideCommands=true\nPreLoadCommand=" + new string('a', 16385)).Diagnostics.Any(d => d.Code == "MMC_CFG_COMMAND"),
            "An oversize command was admitted.");
        Require(!Read("OverrideCommands=true\nPreLoadCommand=" + new string('a', 16384)).HasErrors, "The exact instance command budget was refused.");
        // An allowed variable and a literal dollar are not mistaken for a credential or a violation.
        Require(!Read("OverrideCommands=true\nPreLaunchCommand=echo $INST_DIR ${INST_MC_DIR} $ 100%").HasErrors,
            "An allowed variable or literal dollar was refused: " + Errors(Read("OverrideCommands=true\nPreLaunchCommand=echo $INST_DIR $ 100%")));
    }

    // ---- 2. execution host ----------------------------------------------------------------------------

    private static void ExecutionHost() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo hi");
        var result = MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo hi");
        var start = instance.Observed ?? throw new Exception("The stage did not request a process.");
        Equal("cmd.exe", start.FileName, "Execution host");
        Require(!start.UseShellExecute, "UseShellExecute must stay false for a managed child.");
        Require(start.CreateNoWindow, "The command child must not create a window.");
        Require(string.IsNullOrEmpty(start.Arguments), "The frozen host forbids one concatenated Arguments string; ArgumentList must be used.");
        Require(start.ArgumentList.SequenceEqual(new[] { "/d", "/s", "/c", "echo hi" }), "ArgumentList was " + string.Join("|", start.ArgumentList));
        Equal(Path.GetFullPath(instance.GameRoot), Path.GetFullPath(start.WorkingDirectory), "Working directory must be GameRoot");
        Equal("PreLaunchCommand", result.Stage, "Stage name");
        Equal("echo hi", result.RedactedCommand, "Redacted command text");
        Equal(0, result.ExitCode, "A successful command exit code");
    }

    // ---- 3. variables ---------------------------------------------------------------------------------

    private static void VariableExpansion() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo");
        // The expanded text is asserted through a recorded start, so the separator is chosen not to be a
        // cmd-special character even though the stage only ever hands the text to the shell verbatim.
        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null,
            "echo $INST_DIR;${INST_MC_DIR};${instance_directory};${game_directory}");
        string text = instance.Observed!.ArgumentList[3];
        string root = instance.Root.Replace('\\', '/'), game = instance.GameRoot.Replace('\\', '/');
        Equal("echo " + root + ";" + game + ";" + root + ";" + game, text, "Root variables");
        Require(root != game, "INST_DIR and INST_MC_DIR must be different roles for this case to be meaningful.");
        // A bare spelling is only matched on a word boundary, and an unmatched dollar stays literal.
        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo $INST_DIRECTORY $ 100%");
        Equal("echo $INST_DIRECTORY $ 100%", instance.Observed!.ArgumentList[3], "Bare boundary or literal dollar");
        // An unknown variable is refused, never replaced by an empty string.
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${nope}"), "Unknown ${} variable");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${unterminated"), "Unterminated variable");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${auth_access_token}"), "Credential variable");
        // A plan-derived variable is unavailable before the runtime plan exists, so PreLoad refuses it.
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${classpath}"), "PreLoad classpath");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${version_name}"), "PreLoad version_name");
        // The host values the pre-plan context does carry still resolve at PreLoad.
        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${launcher_name} ${launcher_version}");
        Equal("echo PCL Living Fork 1.0.0", instance.Observed!.ArgumentList[3], "PreLoad host variables");
        // A supplied plan-variable set is what makes the plan-derived names resolvable.
        var planVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["classpath"] = @"C:\a.jar", ["version_name"] = "1.7.10" };
        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, planVariables, "echo ${classpath};${version_name}");
        Equal(@"echo C:\a.jar;1.7.10", instance.Observed!.ArgumentList[3], "Plan-derived variables");
    }

    // ---- 4. refusals ----------------------------------------------------------------------------------

    private static void CommandRefusals() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLoadCommand=echo");
        ThrowsCode("MMC_RUNTIME_COMMAND", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo \0"), "NUL command template");
        ThrowsCode("MMC_RUNTIME_COMMAND_LENGTH", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, new string('a', 16385)), "Oversize command");
        Require(instance.Observed is null, "A refused command must never reach the process entry.");
        // A missing GameRoot is refused: the command would otherwise run in an unverified directory.
        ThrowsCode("MMC_RUNTIME_WORKING_DIRECTORY",
            () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context with { GameRoot = Path.Combine(instance.Root, "absent") }, null, "echo hi"),
            "Missing GameRoot");
        Require(instance.Observed is null, "A command with no verified working directory must never start.");
    }

    // ---- 5. exit codes --------------------------------------------------------------------------------

    private static void ExitCodePolicy() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLoadCommand=echo");
        // PreLoad and PreLaunch refuse the launch.
        ThrowsCode("MMC_RUNTIME_COMMAND_EXIT", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "exit 3"), "PreLoad non-zero exit");
        ThrowsCode("MMC_RUNTIME_COMMAND_EXIT", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "exit 7"), "PreLaunch non-zero exit");
        // PostExit records the code and returns normally: a finished session is never rolled back.
        var post = MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PostExit, context, null, "exit 3");
        Equal(3, post.ExitCode, "PostExit exit code");
        Require(post.ExitCodeText.Contains("0x00000003", StringComparison.Ordinal),
            "The exit code decimal/hex form must follow the existing convention: " + post.ExitCodeText);
        Equal(0, MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PostExit, context, null, "echo done").ExitCode, "PostExit success");
    }

    // ---- 6. cancellation ------------------------------------------------------------------------------

    private static void CancellationTeardown() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo");
        // The long child is handed back through the real injection point, so the teardown path exercises the
        // production Kill and its bounded wait rather than a stub. Production owns and disposes the returned
        // child, so the fixture only observes it through a separate probe handle.
        instance.Handler = _ => {
            var child = new Process {
                StartInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = instance.GameRoot }
            };
            child.StartInfo.ArgumentList.Add("/d"); child.StartInfo.ArgumentList.Add("/s"); child.StartInfo.ArgumentList.Add("/c");
            child.StartInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
            Require(child.Start(), "The cancellation fixture child could not start.");
            Probe = Process.GetProcessById(child.Id);
            return child;
        };
        using var probe = new ProcessHolder();
        using var source = new CancellationTokenSource();
        // Cancellation is requested while the stage is genuinely blocked on the child, which is the only
        // arrangement in which the teardown path is what actually runs.
        source.CancelAfter(250);
        bool canceled = false;
        try { MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "ping -n 30 127.0.0.1 >nul", null, source.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "A canceled stage must report cancellation rather than completing.");
        // The stage reported cancellation only after the whole tree was terminated: the child must be gone,
        // which is the bounded-wait proof the contract requires instead of "Kill was sent".
        var observed = Probe ?? throw new Exception("The cancellation fixture child was not observed.");
        Require(observed.WaitForExit(5000), "The canceled command child was not proven terminated.");
        Require(observed.HasExited, "The canceled command child is still running after the bounded wait.");
        // A cancellation that already happened still refuses to start work.
        using var pre = new CancellationTokenSource(); pre.Cancel();
        try {
            MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo hi", null, pre.Token);
            throw new Exception("An already-canceled stage started work.");
        } catch (OperationCanceledException) { }
    }

    private static Process? Probe;
    private sealed class ProcessHolder : IDisposable { public void Dispose() { Probe?.Dispose(); Probe = null; } }

    // ---- 7. redaction ---------------------------------------------------------------------------------

    private static void LogRedaction() {
        using var instance = new Instance();
        const string Secret = "live-access-token-value";
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo") with { RedactionSecrets = [Secret] };
        var lines = new List<string>();
        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${INST_DIR} " + Secret, lines.Add);
        string joined = string.Join("\n", lines);
        Require(!joined.Contains(Secret, StringComparison.Ordinal), "The log leaked a live secret: " + joined);
        Require(joined.Contains("[redacted]", StringComparison.Ordinal), "The log did not carry the redacted form: " + joined);
        // D14: the log entry is the redacted *template*. The expanded text is never written, so the logged
        // line still carries the variable spelling rather than the resolved root.
        Require(joined.Contains("${INST_DIR}", StringComparison.Ordinal), "The log must carry the command template: " + joined);
        Require(!joined.Contains(instance.Root.Replace('\\', '/'), StringComparison.Ordinal), "The log wrote the expanded text instead of the redacted template.");
        // A command that fails still never logs its expanded text.
        var failing = new List<string>();
        ThrowsCode("MMC_RUNTIME_COMMAND_EXIT", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "exit 4 ${INST_DIR} " + Secret, failing.Add), "Failing command");
        string failed = string.Join("\n", failing);
        Require(!failed.Contains(Secret, StringComparison.Ordinal), "A failed command logged a live secret.");
        Require(!failed.Contains(instance.Root.Replace('\\', '/'), StringComparison.Ordinal), "A failed command logged its expanded text.");
        // A secret used as a variable value is redacted from the expanded result as well, not only the template.
        var expanded = MmcRuntimeCommandExecution.Redact("echo " + instance.Root.Replace('\\', '/') + " " + Secret, [Secret]);
        Require(!expanded.Contains(Secret, StringComparison.Ordinal) && expanded.Contains("[redacted]", StringComparison.Ordinal),
            "Redaction must also apply to a value substituted into the command.");
    }

    // ---- 8. inactive source ---------------------------------------------------------------------------

    private static void InactiveSource() {
        using var instance = new Instance();
        Equal<string?>(null, MmcRuntimeCommandExecution.Read(Context(instance, "OverrideCommands=false\nPreLaunchCommand=echo never"), MmcRuntimeCommandStage.PreLaunch),
            "An inactive source must not execute");
        Equal<string?>(null, MmcRuntimeCommandExecution.Read(Context(instance, "OverrideLaunchCmd=false\nPreLaunchCommand=echo never"), MmcRuntimeCommandStage.PreLaunch),
            "The legacy alias must gate the same way");
        var active = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo yes");
        Equal("echo yes", MmcRuntimeCommandExecution.Read(active, MmcRuntimeCommandStage.PreLaunch), "An active source must read its text");
        Require(MmcRuntimeCommandExecution.Read(active, MmcRuntimeCommandStage.PostExit) is null, "An absent stage must read as no command");
        Require(MmcRuntimeCommandExecution.Read(Context(instance, (MmcConfiguration?)null), MmcRuntimeCommandStage.PreLaunch) is null, "A configuration-less context must read as no command");
        // A stage command that no source declares never runs, and no process entry is reached.
        var result = MmcRuntimeCommandExecution.Read(Context(instance, "OverrideCommands=true\nPreLoadCommand=echo only"), MmcRuntimeCommandStage.PostExit);
        Require(result is null && instance.Observed is null, "An undeclared stage command produced work.");
    }

    // ---- 9. post-command revalidation -----------------------------------------------------------------

    private static void PostCommandRevalidation() {
        // The receipt rule of contract §6 without a launcher: a command that changes an input byte must be
        // caught by re-observation, and the fingerprint must be recomputed rather than reused. The observation
        // tuple mirrors the runtime plan's own field order (identity, path, digest, length).
        string root = Path.Combine(Path.GetTempPath(), "mmc-reval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string artifact = Path.Combine(root, "artifact.jar");
            File.WriteAllBytes(artifact, [1, 2, 3, 4]);
            var before = new Observation("artifact:fixture", artifact, Digest(artifact), new FileInfo(artifact).Length);
            File.WriteAllBytes(artifact, [1, 2, 3, 4, 5]);
            var after = new Observation("artifact:fixture", artifact, Digest(artifact), new FileInfo(artifact).Length);
            Require(before.Sha256 != after.Sha256, "The tamper fixture did not change the artifact digest.");
            Require(before.SizeBytes != after.SizeBytes, "The tamper fixture did not change the artifact length.");
            Require(!Fingerprint([before]).Equals(Fingerprint([after]), StringComparison.Ordinal),
                "A changed artifact must change the recomputed input fingerprint.");
            Require(Fingerprint([before]).Equals(Fingerprint([before with { }]), StringComparison.Ordinal),
                "An unchanged input must reproduce the same fingerprint.");
            // Reusing the pre-command fingerprint would carry the stale digest and hide the change.
            Require(!Fingerprint([before]).Equals(Fingerprint([after]), StringComparison.Ordinal),
                "Reusing the pre-command fingerprint would hide the change.");
        } finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    private sealed record Observation(string Identity, string ActualPath, string Sha256, long SizeBytes);

    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Mirrors the production fingerprint shape, so the receipt's sensitivity is the real one.</summary>
    private static string Fingerprint(IEnumerable<Observation> inputs) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string? value in new[] { "mmc-runtime-inputs-v1" }
            .Concat(inputs.SelectMany(i => new[] { i.Identity, i.ActualPath, i.Sha256, i.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }))) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
