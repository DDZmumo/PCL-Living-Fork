// Independently authored fixtures. Host test suite retains its existing license.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PCLCS;
using PCLLF.MMCC.Core;

/// <summary>
/// Exercises the frozen package-command stage contract (docs/mmc-phase2-command-contract.md) against the
/// real production stage boundary. No game process is ever created. Preparation checks capture the exact
/// <see cref="ProcessStartInfo"/> and stop at the injected host; Windows execution checks additionally start
/// independent harmless children. Other platforms verify the explicit default-host refusal.
/// </summary>
internal static class MmcCommandExecutionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC package command stage is admitted once its text is decodable", AbilityGate);
        yield return ("MMC package command preparation preserves Windows framing, GameRoot cwd and redacted templates without starting a process", PreparedCommandHost);
        yield return ("MMC package command stage expands the runtime variables once and refuses an unknown one", VariableExpansion);
        yield return ("MMC package command stage refuses NUL, oversize and credential references before starting", CommandRefusals);
        yield return ("MMC package command stage skips an inactive source and reads stages independently", InactiveSource);
        yield return ("MMC package command stage changes the recomputed input fingerprint when an artifact changes", PostCommandRevalidation);
        yield return ("MMC package command preparation applies package Env and session TEMP only for PreLaunch and PostExit", StageEnvironmentOverlay);
        yield return ("MMC package command stage refuses PreLaunch or PostExit Env without a valid session temporary directory", StageEnvironmentRefusals);
        if (OperatingSystem.IsWindows()) {
            yield return ("MMC package command stage uses the frozen cmd.exe argument list with GameRoot cwd", ExecutionHost);
            yield return ("MMC package command stage treats a non-zero exit code according to its stage", ExitCodePolicy);
            yield return ("MMC package command stage terminates the whole managed tree on cancellation", CancellationTeardown);
            yield return ("MMC package command stage logs only the redacted command text", LogRedaction);
            yield return ("MMC package command production start waits for surviving start /b child and cancels the whole job tree", ProductionDefaultStartRootExitsSurvivingChildWaitsAndCancels);
            yield return ("MMC package command cancellation failure or grace timeout retains pending ledger and blocks cleanup until child exits", CancellationFailureAndGraceTimeoutRetainLedgerAndBlockWrites);
            yield return ("MMC package command host kill preserves surviving start /b orphan and blocks cleanup until exit", KilledHostWithSurvivingStartBOrphanRetainsTempAndRecovers);
            yield return ("MMC package command injected Unknown job and cross-session intent never downgrade to process scan", InjectedKernelJobUnknownAndCrossSessionNeverDowngradeToScan);
        } else {
            yield return ("MMC package command default Windows host explicitly refuses every stage on this platform before execution", DefaultWindowsHostUnsupported);
        }
    }

    internal static bool TryRunChild(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length == 0) return false;
        if (args[0] == "--mmc-cmd-child") {
            if (args.Length < 3) throw new Exception("Missing --mmc-cmd-child arguments.");
            string readyPath = args[1], stopPath = args[2];
            Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
            string tmp = readyPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(tmp, readyPath, overwrite: true);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(stopPath) && timer.ElapsedMilliseconds < 30000) Thread.Sleep(20);
            return true;
        }
        if (args[0] == "--mmc-cmd-host-child") {
            if (args.Length < 7 || !Guid.TryParse(args[2], out Guid instanceId) || !Guid.TryParse(args[3], out Guid sessionId))
                throw new Exception("Invalid --mmc-cmd-host-child arguments.");
            string instanceRoot = args[1], bindingHash = args[4], command = args[5], leaseReadyPath = args[6];
            var paths = MmcInstancePaths.Resolve(instanceRoot);
            var authority = MmcMutableRuntimeAuthority.ForOwnedFixture(Path.GetDirectoryName(Path.GetDirectoryName(instanceRoot)!)!);
            var prepared = MmcSessionRuntimeDirectories.Prepare(paths, instanceId, sessionId, bindingHash, authority);
            MmcCommandLease cmdLease;
            using (var admission = MmcAdmissionGate.Acquire(instanceRoot, instanceId, TimeSpan.FromSeconds(5))) {
                cmdLease = MmcCommandOperations.Begin(admission, MmcRuntimeCommandStage.PreLaunch, sessionId, prepared.RuntimeBinding);
            }
            File.WriteAllText(leaseReadyPath, cmdLease.Intent.CommandId.ToString("N"));
            var context = new MmcRuntimeCommandContext(instanceRoot, paths.GameRoot, sessionId, "PCL Living Fork", "1.0.0", 854, 480) {
                PackageConfiguration = Configuration("OverrideCommands=true\nOverrideEnv=false\nPreLaunchCommand=echo host"),
                TemporaryDirectory = prepared.TemporaryDirectory
            };
            using (cmdLease) {
                MmcRuntimeCommandExecution.ExecuteTracked(MmcRuntimeCommandStage.PreLaunch, context, null, command, cmdLease, out _, null, CancellationToken.None);
            }
            return true;
        }
        return false;
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
        private readonly Func<ProcessStartInfo, Process?>? previous;
        internal string Root { get; }
        internal string ScopeRoot => Path.GetDirectoryName(Path.GetDirectoryName(Root)!)!;
        internal string GameRoot => Path.Combine(Root, "minecraft");
        internal Guid InstanceId { get; } = Guid.Parse("11111111-2222-3333-4444-555555555555");
        internal ProcessStartInfo? Observed { get; private set; }
        internal void ResetObserved() => Observed = null;
        /// <summary>Replaces the started child; the recorded start info is always captured first.</summary>
        internal Func<ProcessStartInfo, Process?>? Handler { get; set; }

        internal Instance() : this(useDefaultStart: false, initializeInstanceDefinition: false) { }

        internal Instance(bool useDefaultStart, bool initializeInstanceDefinition) {
            string folder = useDefaultStart ? Guid.NewGuid().ToString("N") : "mmc-cmd-" + Guid.NewGuid().ToString("N");
            Root = Path.Combine(Path.GetTempPath(), folder, "versions", "fixture");
            Directory.CreateDirectory(GameRoot);
            Directory.CreateDirectory(Path.Combine(Root, "libraries"));
            Directory.CreateDirectory(Path.Combine(Root, "PCL", "MmcRuntime"));
            if (initializeInstanceDefinition) {
                File.WriteAllText(Path.Combine(Root, "PCL", "InstanceDefinition.json"),
                    $$$"""{"schemaVersion":1,"managementKind":"Mmc","formatProfile":"prism-v1","instanceId":"{{{InstanceId:D}}}","generation":1,"layout":{"gameRootLeaf":"minecraft"}}""");
            }
            if (!useDefaultStart) {
                previous = MmcRuntimeCommandExecution.StartProcess;
                MmcRuntimeCommandExecution.StartProcess = start => {
                    Observed = start;
                    if (Handler is not null) return Handler(start);
                    // An independent harmless child: the recorded info is never reused, so the assertion sees
                    // exactly what production would have handed to the process entry.
                    var copy = new ProcessStartInfo(start.FileName) {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = start.WorkingDirectory,
                        Arguments = start.Arguments
                    };
                    foreach (string argument in start.ArgumentList) copy.ArgumentList.Add(argument);
                    return Process.Start(copy);
                };
            }
        }
        public void Dispose() {
            if (previous is not null) MmcRuntimeCommandExecution.StartProcess = previous;
            string? owner = Path.GetDirectoryName(Path.GetDirectoryName(Root));
            if (owner is null || !Directory.Exists(owner)) return;
            var timer = Stopwatch.StartNew();
            while (Directory.Exists(owner)) {
                try { Directory.Delete(owner, true); break; }
                catch (IOException error) when ((error.HResult & 0xffff) == 32 && timer.ElapsedMilliseconds < 2000) { Thread.Sleep(20); }
                catch (Exception) { break; }
            }
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

    private static string ExtractShellCommand(ProcessStartInfo start) {
        Require(start.ArgumentList.Count == 0, "cmd.exe host must pass raw Arguments instead of ArgumentList so quotes are preserved.");
        Require(start.Arguments.StartsWith("/d /s /c \"", StringComparison.Ordinal) && start.Arguments.EndsWith("\"", StringComparison.Ordinal),
            "Unexpected cmd.exe Arguments framing: " + start.Arguments);
        return start.Arguments["/d /s /c \"".Length..^1];
    }

    private sealed class PreparedStartObservedException : Exception { }

    private static void ObservePreparation(Instance instance, Action execute, bool preparationOnly = false) {
        if (OperatingSystem.IsWindows() && !preparationOnly) { execute(); return; }
        var previousHandler = instance.Handler;
        instance.Handler = _ => throw new PreparedStartObservedException();
        instance.ResetObserved();
        try {
            execute();
        } catch (PreparedStartObservedException) {
            Require(instance.Observed is not null, "The injected host did not observe a prepared command.");
            return;
        } finally { instance.Handler = previousHandler; }
        throw new Exception("The preparation fixture must stop at the injected host before any process starts.");
    }

    private static void PreparedCommandHost() {
        using var instance = new Instance();
        const string secret = "prepared-command-secret";
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo") with { RedactionSecrets = [secret] };
        List<string> lines = new();
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null,
            "echo ${INST_DIR} " + secret, lines.Add), preparationOnly: true);
        var start = instance.Observed ?? throw new Exception("Prepared command start info is missing.");
        Equal("cmd.exe", start.FileName, "Prepared Windows execution host");
        Require(!start.UseShellExecute && start.CreateNoWindow, "Prepared command changed shell/window behaviour.");
        Equal(Path.GetFullPath(instance.GameRoot), Path.GetFullPath(start.WorkingDirectory), "Prepared GameRoot working directory");
        Equal("echo " + instance.Root.Replace('\\', '/') + " [redacted]", ExtractShellCommand(start), "Prepared expansion/redaction");
        string logged = string.Join("\n", lines);
        Require(logged.Contains("${INST_DIR}", StringComparison.Ordinal) && logged.Contains("[redacted]", StringComparison.Ordinal) &&
            !logged.Contains(secret, StringComparison.Ordinal) && !logged.Contains(instance.Root.Replace('\\', '/'), StringComparison.Ordinal),
            "Preparation logged a credential or the expanded command instead of its redacted template.");
        Equal("echo [redacted]", MmcRuntimeCommandExecution.Redact("echo prefix_secret_longer_suffix", ["prefix_secret", "prefix_secret_longer_suffix"]),
            "Prepared overlapping secret redaction");
    }

    private static void DefaultWindowsHostUnsupported() {
        using var instance = new Instance(useDefaultStart: true, initializeInstanceDefinition: false);
        var context = Context(instance, "OverrideCommands=true\nPreLoadCommand=echo never\nPreLaunchCommand=echo never\nPostExitCommand=echo never");
        foreach (var stage in new[] { MmcRuntimeCommandStage.PreLoad, MmcRuntimeCommandStage.PreLaunch, MmcRuntimeCommandStage.PostExit }) {
            List<string> lines = new(); bool refused = false;
            try { MmcRuntimeCommandExecution.Execute(stage, context, null, "echo never", lines.Add); }
            catch (PlatformNotSupportedException error) {
                refused = error.Message.Contains("Windows", StringComparison.Ordinal) && error.Message.Contains("cmd.exe", StringComparison.Ordinal);
            }
            Require(refused && lines.Count == 0, stage + " did not explicitly refuse the Windows host before announcing execution.");
        }
    }

    private static void ExecutionHost() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo hi");
        var result = MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo hi");
        var start = instance.Observed ?? throw new Exception("The stage did not request a process.");
        Equal("cmd.exe", start.FileName, "Execution host");
        Require(!start.UseShellExecute, "UseShellExecute must stay false for a managed child.");
        Require(start.CreateNoWindow, "The command child must not create a window.");
        Equal("/d /s /c \"echo hi\"", start.Arguments, "cmd.exe must run with /d /s /c framing");
        Equal(0, start.ArgumentList.Count, "cmd.exe ArgumentList must stay empty to prevent CRT \\\" quote corruption");
        Equal(Path.GetFullPath(instance.GameRoot), Path.GetFullPath(start.WorkingDirectory), "Working directory must be GameRoot");
        Equal("PreLaunchCommand", result.Stage, "Stage name");
        Equal("echo hi", result.RedactedCommand, "Redacted command text");
        Equal(0, result.ExitCode, "A successful command exit code");
        // Verify embedded double quotes (real quoted executable paths with spaces) execute cleanly under /d /s /c.
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var quoted = MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "\"" + cmdExe + "\" /d /c \"exit 0\"");
        Equal(0, quoted.ExitCode, "Quoted executable path inside package command failed cmd.exe execution");
    }

    // ---- 3. variables ---------------------------------------------------------------------------------

    private static void VariableExpansion() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLaunchCommand=echo");
        // The expanded text is asserted through a recorded start, so the separator is chosen not to be a
        // cmd-special character even though the stage only ever hands the text to the shell verbatim.
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null,
            "echo $INST_DIR;${INST_MC_DIR};${instance_directory};${game_directory}"));
        string text = ExtractShellCommand(instance.Observed!);
        string root = instance.Root.Replace('\\', '/'), game = instance.GameRoot.Replace('\\', '/');
        Equal("echo " + root + ";" + game + ";" + root + ";" + game, text, "Root variables");
        Require(root != game, "INST_DIR and INST_MC_DIR must be different roles for this case to be meaningful.");
        // A bare spelling is only matched on a word boundary, and an unmatched dollar stays literal.
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo $INST_DIRECTORY $ 100%"));
        Equal("echo $INST_DIRECTORY $ 100%", ExtractShellCommand(instance.Observed!), "Bare boundary or literal dollar");
        // An unknown variable is refused, never replaced by an empty string.
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${nope}"), "Unknown ${} variable");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${unterminated"), "Unterminated variable");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, null, "echo ${auth_access_token}"), "Credential variable");
        // A plan-derived variable is unavailable before the runtime plan exists, so PreLoad refuses it.
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${classpath}"), "PreLoad classpath");
        ExpectVariable(() => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${version_name}"), "PreLoad version_name");
        // The host values the pre-plan context does carry still resolve at PreLoad.
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo ${launcher_name} ${launcher_version}"));
        Equal("echo PCL Living Fork 1.0.0", ExtractShellCommand(instance.Observed!), "PreLoad host variables");
        // A supplied plan-variable set is what makes the plan-derived names resolvable.
        var planVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["classpath"] = @"C:\a.jar", ["version_name"] = "1.7.10" };
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, planVariables, "echo ${classpath};${version_name}"));
        Equal(@"echo C:\a.jar;1.7.10", ExtractShellCommand(instance.Observed!), "Plan-derived variables");
    }

    // ---- 4. refusals ----------------------------------------------------------------------------------

    private static void CommandRefusals() {
        using var instance = new Instance();
        var context = Context(instance, "OverrideCommands=true\nPreLoadCommand=echo");
        ThrowsCode("MMC_RUNTIME_COMMAND", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, "echo \0"), "NUL command template");
        ThrowsCode("MMC_RUNTIME_COMMAND_LENGTH", () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, context, null, new string('a', 16385)), "Oversize command");
        var hugePlan = new Dictionary<string, string>(StringComparer.Ordinal) { ["classpath"] = new string('a', MmcRuntimeWindowsCommandLine.MaximumLengthIncludingTerminator) };
        ThrowsCode("MMC_RUNTIME_COMMAND_LENGTH",
            () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLaunch, context, hugePlan, "echo ${classpath}"),
            "Oversize post-expansion command");
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
        // Overlapping prefix/longer secrets replace the longest secret first so suffixes never leak.
        var overlapped = MmcRuntimeCommandExecution.Redact("echo prefix_secret_longer_suffix", ["prefix_secret", "prefix_secret_longer_suffix"]);
        Equal("echo [redacted]", overlapped, "Overlapping secret redaction");
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

    // ---- 10. stage env overlay (D15) ------------------------------------------------------------------

    private static void StageEnvironmentOverlay() {
        using var instance = new Instance();
        string sessionTemp = Path.Combine(instance.Root, "PCL", "MmcRuntime", "session-temp");
        Directory.CreateDirectory(sessionTemp);
        var cfg = Configuration("OverrideCommands=true\nOverrideEnv=true\nPreLoadCommand=echo preload\nPreLaunchCommand=echo prelaunch\nPostExitCommand=echo postexit\nEnv=[\"PACK_CMD=${INST_DIR}/literal\",\"UNICODE=中文😀\",\"EMPTY=\"]\n");

        // PreLoad must never apply package Env or session TEMP/TMP, even if TemporaryDirectory is supplied.
        var preLoadNoTemp = Context(instance, cfg);
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, preLoadNoTemp, null, "echo preload"));
        Require(instance.Observed is not null && !instance.Observed.Environment.ContainsKey("PACK_CMD"),
            "PreLoad must not receive package Env entries.");
        var preLoadWithTemp = Context(instance, cfg) with { TemporaryDirectory = sessionTemp };
        ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PreLoad, preLoadWithTemp, null, "echo preload"));
        Require(instance.Observed is not null && !instance.Observed.Environment.ContainsKey("PACK_CMD") &&
                !string.Equals(instance.Observed.Environment.TryGetValue("TEMP", out string? preLoadTemp) ? preLoadTemp : null, sessionTemp, StringComparison.OrdinalIgnoreCase),
            "PreLoad must ignore TemporaryDirectory and never inherit package Env.");

        // PreLaunch and PostExit apply MmcRuntimeEnvironmentOverlay literally and force TEMP/TMP to sessionTemp.
        string? prevToolOptions = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
        Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-javaagent:parent.jar");
        try {
            var withTemp = Context(instance, cfg) with { TemporaryDirectory = sessionTemp };
            foreach (var stage in new[] { MmcRuntimeCommandStage.PreLaunch, MmcRuntimeCommandStage.PostExit }) {
                ObservePreparation(instance, () => MmcRuntimeCommandExecution.Execute(stage, withTemp, null, "echo ok"));
                var env = instance.Observed!.Environment;
                Equal("${INST_DIR}/literal", env["PACK_CMD"], stage + " PACK_CMD literal");
                Equal("中文😀", env["UNICODE"], stage + " UNICODE literal");
                Equal("", env["EMPTY"], stage + " empty literal");
                Equal(sessionTemp, env["TEMP"], stage + " TEMP");
                Equal(sessionTemp, env["TMP"], stage + " TMP");
                Require(!env.ContainsKey("JAVA_TOOL_OPTIONS") && !env.ContainsKey("CLASSPATH"),
                    stage + " must strip inherited execution-input channels.");
            }
        } finally {
            Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", prevToolOptions);
        }
    }

    private static void StageEnvironmentRefusals() {
        using var instance = new Instance();
        var cfg = Configuration("OverrideCommands=true\nOverrideEnv=true\nPreLaunchCommand=echo a\nPostExitCommand=echo b\nEnv=[\"PACK_CMD=val\"]\n");

        foreach (var stage in new[] { MmcRuntimeCommandStage.PreLaunch, MmcRuntimeCommandStage.PostExit }) {
            instance.ResetObserved();
            ThrowsCode("MMC_RUNTIME_ENV_SETTINGS",
                () => MmcRuntimeCommandExecution.Execute(stage, Context(instance, cfg), null, "echo ok"),
                stage + " without TemporaryDirectory");
            Require(instance.Observed is null, stage + " refusal must occur before spawning cmd.exe.");

            string missingTemp = Path.Combine(instance.Root, "PCL", "MmcRuntime", "does-not-exist");
            ThrowsCode("MMC_RUNTIME_ENV_SETTINGS",
                () => MmcRuntimeCommandExecution.Execute(stage, Context(instance, cfg) with { TemporaryDirectory = missingTemp }, null, "echo ok"),
                stage + " with nonexistent TemporaryDirectory");
            Require(instance.Observed is null, stage + " missing-temp refusal must occur before spawning cmd.exe.");
        }
    }

    private sealed record Observation(string Identity, string ActualPath, string Sha256, long SizeBytes);

    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Mirrors the production fingerprint shape, so the receipt's sensitivity is the real one.</summary>
    private static string Fingerprint(IEnumerable<Observation> inputs) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (string? value in new[] { "mmc-runtime-inputs-v1" }
            .Concat(inputs.SelectMany(i => new[] { i.Identity, i.ActualPath, i.Sha256, i.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }))) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // ---- 11. whole-tree tracking, Job authority, cancellation failure & host death ----

    private static void ProductionDefaultStartRootExitsSurvivingChildWaitsAndCancels() {
        using var instance = new Instance(useDefaultStart: true, initializeInstanceDefinition: true);
        string childExe = AppHost();
        Guid sessionId = Guid.NewGuid();
        var binding = new MmcSessionRuntimeBinding(new string('c', 64));
        var authority = MmcMutableRuntimeAuthority.ForOwnedFixture(instance.ScopeRoot);
        var mutable = MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(instance.Root), instance.InstanceId, sessionId, binding.BoundPlanFingerprint, authority);

        // Phase A: cmd.exe root exits immediately via `start "" /b`, yet ExecuteTracked stays blocked until descendant exits.
        string readyA = Path.Combine(instance.ScopeRoot, "a-ready.txt"), stopA = Path.Combine(instance.ScopeRoot, "a-stop.txt");
        string cmdA = $"start \"\" /b \"{childExe}\" --mmc-cmd-child \"{readyA}\" \"{stopA}\"";
        MmcCommandLease leaseA;
        using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
            leaseA = MmcCommandOperations.Begin(admission, MmcRuntimeCommandStage.PreLaunch, sessionId, mutable.RuntimeBinding);
        }
        using (leaseA) {
            var ctx = Context(instance, "OverrideCommands=true\nOverrideEnv=false\nPreLaunchCommand=echo ok") with {
                SessionId = sessionId, TemporaryDirectory = mutable.TemporaryDirectory
            };
            MmcRuntimeCommandExecution.MmcRuntimeCommandOutcome? outcomeA = null;
            var taskA = Task.Run(() => MmcRuntimeCommandExecution.ExecuteTracked(
                MmcRuntimeCommandStage.PreLaunch, ctx, null, cmdA, leaseA, out outcomeA));
            int childPidA = WaitChildPid(readyA, 10000);
            Thread.Sleep(250); // Root cmd.exe has already exited after `start /b`; descendant stays alive.
            Require(!taskA.IsCompleted, "ExecuteTracked returned early when cmd.exe exited before start /b child.");
            using (Process procA = Process.GetProcessById(childPidA)) Require(!procA.HasExited, "Descendant child unexpectedly exited before stop.");

            File.WriteAllText(stopA, "stop");
            taskA.GetAwaiter().GetResult();
            Require(outcomeA is { TreeProvenExited: true }, "ExecuteTracked did not confirm tree exit after descendant stopped.");
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                leaseA.CompleteProvenExited(admission);
                MmcSessionRuntimeDirectories.AbandonUnlaunched(admission, sessionId, mutable.RuntimeBinding, authority);
                MmcCommandOperations.AssertNoPending(admission);
                MmcSessions.AssertNoActive(admission);
            }
        }

        // Phase B: Cancel while root cmd.exe exited and only start /b child runs -> Job cancellation kills descendant.
        Guid sessionB = Guid.NewGuid();
        var mutableB = MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(instance.Root), instance.InstanceId, sessionB, new string('d', 64), authority);
        string readyB = Path.Combine(instance.ScopeRoot, "b-ready.txt"), stopB = Path.Combine(instance.ScopeRoot, "b-stop.txt");
        string cmdB = $"start \"\" /b \"{childExe}\" --mmc-cmd-child \"{readyB}\" \"{stopB}\"";
        MmcCommandLease leaseB;
        using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
            leaseB = MmcCommandOperations.Begin(admission, MmcRuntimeCommandStage.PreLaunch, sessionB, mutableB.RuntimeBinding);
        }
        using (leaseB) {
            var ctx = Context(instance, "OverrideCommands=true\nOverrideEnv=false\nPreLaunchCommand=echo ok") with {
                SessionId = sessionB, TemporaryDirectory = mutableB.TemporaryDirectory
            };
            using var cts = new CancellationTokenSource();
            MmcRuntimeCommandExecution.MmcRuntimeCommandOutcome outcomeB = new(false, null, []);
            var taskB = Task.Run(() => {
                try {
                    MmcRuntimeCommandExecution.ExecuteTracked(MmcRuntimeCommandStage.PreLaunch, ctx, null, cmdB, leaseB, out outcomeB, null, cts.Token);
                } catch (OperationCanceledException) {
                    return true;
                }
                return false;
            });
            int childPidB = WaitChildPid(readyB, 10000);
            Thread.Sleep(250);
            cts.Cancel();
            Require(taskB.GetAwaiter().GetResult(), "Expected OperationCanceledException once Job teardown killed start /b descendant.");
            Require(outcomeB.TreeProvenExited, "Expected TreeProvenExited after real Job termination.");
            bool gone = IsProcessGone(childPidB, 3000);
            Require(gone, "Real Job cancellation did not terminate surviving start /b child.");
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                leaseB.CompleteProvenExited(admission);
                MmcSessionRuntimeDirectories.AbandonUnlaunched(admission, sessionB, mutableB.RuntimeBinding, authority);
            }
        }
    }

    private static void CancellationFailureAndGraceTimeoutRetainLedgerAndBlockWrites() {
        using var instance = new Instance(useDefaultStart: true, initializeInstanceDefinition: true);
        string childExe = AppHost();
        var authority = MmcMutableRuntimeAuthority.ForOwnedFixture(instance.ScopeRoot);

        // Subcase 1: TerminateTreeOverride throws Win32Exception(5) -> unconfirmed, blocks cleanup until child stops.
        RunUnconfirmedScenario(instance, authority, failByException: true, childExe, "11");
        // Subcase 2: TerminateTreeOverride returns true without killing, grace timeout expires with surviving start /b child -> still unconfirmed!
        RunUnconfirmedScenario(instance, authority, failByException: false, childExe, "22");
    }

    private static void RunUnconfirmedScenario(
        Instance instance,
        MmcMutableRuntimeAuthority authority,
        bool failByException,
        string childExe,
        string tag) {
        Guid sessionId = Guid.NewGuid();
        var mutable = MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(instance.Root), instance.InstanceId, sessionId, new string(tag[0], 64), authority);
        string ready = Path.Combine(instance.ScopeRoot, tag + "-ready.txt"), stop = Path.Combine(instance.ScopeRoot, tag + "-stop.txt");
        string cmd = $"start \"\" /b \"{childExe}\" --mmc-cmd-child \"{ready}\" \"{stop}\"";
        MmcCommandLease cmdLease;
        using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
            cmdLease = MmcCommandOperations.Begin(admission, MmcRuntimeCommandStage.PreLaunch, sessionId, mutable.RuntimeBinding);
        }

        var prevTerminate = MmcRuntimeCommandExecution.TerminateTreeOverride;
        var prevGrace = MmcRuntimeCommandExecution.CancellationGraceMillisecondsOverride;
        MmcRuntimeCommandExecution.TerminateTreeOverride = failByException
            ? (_, _) => throw new Win32Exception(5, "Simulated TerminateJobObject access denied")
            : (_, _) => true; // Simulates unresponsive tree outliving the grace period.
        MmcRuntimeCommandExecution.CancellationGraceMillisecondsOverride = 80;
        int childPid = 0;
        try {
            var ctx = Context(instance, "OverrideCommands=true\nOverrideEnv=false\nPreLaunchCommand=echo ok") with {
                SessionId = sessionId, TemporaryDirectory = mutable.TemporaryDirectory
            };
            using var cts = new CancellationTokenSource();
            MmcRuntimeCommandExecution.MmcRuntimeCommandOutcome outcome = new(true, null, []);
            var task = Task.Run(() => {
                try {
                    MmcRuntimeCommandExecution.ExecuteTracked(MmcRuntimeCommandStage.PreLaunch, ctx, null, cmd, cmdLease, out outcome, null, cts.Token);
                    return (string?)null;
                } catch (MmcRuntimeLaunchException ex) {
                    return ex.Code;
                }
            });
            childPid = WaitChildPid(ready, 10000);
            using var observedJob = RetainCommandJob(cmdLease.Intent.JobName);
            Thread.Sleep(250);
            cts.Cancel();
            Equal("MMC_RUNTIME_COMMAND_UNCONFIRMED", task.GetAwaiter().GetResult(), tag + " must throw UNCONFIRMED instead of canceling cleanly");
            Require(!outcome.TreeProvenExited && outcome.SurvivingProcesses.Any(p => p.ProcessId == childPid),
                tag + " outcome must record the surviving descendant PID.");

            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                cmdLease.RecordUnconfirmed(admission, outcome.Diagnostic ?? "unconfirmed", outcome.SurvivingProcesses);
            }
            cmdLease.Dispose();

            // While child is still running: pending ledger + TEMP remain, and structural writes/Abandon/Reconcile refuse.
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                Require(Directory.Exists(mutable.TemporaryDirectory), tag + " TEMP was prematurely deleted while descendant survived.");
                Require(File.Exists(Path.Combine(instance.Root, "PCL", "MmcCommands", cmdLease.Intent.CommandId.ToString("N"), "tail.record")),
                    tag + " tail.record was not persisted.");
                bool blocked = false;
                try { MmcFileTransactions.AssertNoPending(admission); } catch (IOException) { blocked = true; }
                Require(blocked, tag + " AssertNoPending failed to block during unconfirmed command tail.");
                blocked = false;
                try { MmcSessions.AssertNoActive(admission); } catch (IOException) { blocked = true; }
                Require(blocked, tag + " AssertNoActive failed to block during unconfirmed command tail.");
                blocked = false;
                try { MmcSessionRuntimeDirectories.AbandonUnlaunched(admission, sessionId, mutable.RuntimeBinding, authority); } catch (IOException) { blocked = true; }
                Require(blocked, tag + " AbandonUnlaunched deleted TEMP despite unconfirmed command tail.");
                blocked = false;
                try { MmcCommandOperations.ReconcileAll(admission, authority); } catch (IOException) { blocked = true; }
                Require(blocked, tag + " ReconcileAll falsely cleared while descendant still ran.");
            }

            File.WriteAllText(stop, "stop");
            Require(IsProcessGone(childPid, 8000), tag + " descendant did not exit after stop.");
            WaitForEmptyCommandJob(cmdLease.Intent.JobName);
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                MmcCommandOperations.ReconcileAll(admission, authority);
                MmcFileTransactions.AssertNoPending(admission);
                MmcSessions.AssertNoActive(admission);
                Require(!Directory.Exists(mutable.SessionRoot), tag + " ReconcileAll did not retire unlaunched TEMP after descendant exited.");
            }
        } finally {
            MmcRuntimeCommandExecution.TerminateTreeOverride = prevTerminate;
            MmcRuntimeCommandExecution.CancellationGraceMillisecondsOverride = prevGrace;
            cmdLease.Dispose();
            File.WriteAllText(stop, "stop");
            if (childPid > 0) IsProcessGone(childPid, 3000);
        }
    }

    private static void KilledHostWithSurvivingStartBOrphanRetainsTempAndRecovers() {
        using var instance = new Instance(useDefaultStart: true, initializeInstanceDefinition: true);
        string appHost = AppHost();
        Guid sessionId = Guid.NewGuid();
        string bindingHash = new('e', 64);
        string leaseReady = Path.Combine(instance.ScopeRoot, "host-lease-ready.txt");
        string childReady = Path.Combine(instance.ScopeRoot, "orphan-ready.txt");
        string childStop = Path.Combine(instance.ScopeRoot, "orphan-stop.txt");
        string cmd = $"start \"\" /b \"{appHost}\" --mmc-cmd-child \"{childReady}\" \"{childStop}\"";

        var psi = new ProcessStartInfo(appHost) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--mmc-cmd-host-child", instance.Root, instance.InstanceId.ToString("D"), sessionId.ToString("D"), bindingHash, cmd, leaseReady })
            psi.ArgumentList.Add(arg);

        int childPid = 0;
        try {
            using Process host = Process.Start(psi) ?? throw new Exception("Failed to spawn host child.");
            WaitFileReady(leaseReady, 12000);
            childPid = WaitChildPid(childReady, 12000);
            string commandId = Guid.ParseExact(File.ReadAllText(leaseReady), "N").ToString("N");
            (var intent, _) = MmcDurableFiles.ReadRecord<MmcCommandIntentRecord>(
                Path.Combine(instance.Root, "PCL", "MmcCommands", commandId, "intent.record"));
            using var observedJob = RetainCommandJob(intent.JobName);
            Thread.Sleep(250); // Ensure cmd.exe root already exited leaving only start /b child alive.
            if (!host.HasExited) host.Kill(entireProcessTree: false); // Kill ONLY the host!
            Require(host.WaitForExit(5000), "Host process did not exit after Kill.");

            var authority = MmcMutableRuntimeAuthority.ForOwnedFixture(instance.ScopeRoot);
            var binding = new MmcSessionRuntimeBinding(bindingHash);
            var mutable = MmcSessionRuntimeDirectories.Paths(MmcInstancePaths.Resolve(instance.Root), instance.InstanceId, sessionId, binding, authority);
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                Require(Directory.Exists(mutable.TemporaryDirectory), "TEMP disappeared before recovery.");
                bool refused = false;
                try { MmcCommandOperations.ReconcileAll(admission, authority); } catch (IOException) { refused = true; }
                Require(refused, "ReconcileAll must refuse when killed host left surviving start /b orphan.");
                refused = false;
                try { MmcFileTransactions.AssertNoPending(admission); } catch (IOException) { refused = true; }
                Require(refused, "AssertNoPending must refuse during orphaned command recovery.");
            }

            File.WriteAllText(childStop, "stop");
            Require(IsProcessGone(childPid, 8000), "Orphaned start /b child did not exit after stop.");
            WaitForEmptyCommandJob(intent.JobName);

            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                MmcCommandOperations.ReconcileAll(admission, authority);
                MmcFileTransactions.AssertNoPending(admission);
                MmcSessions.AssertNoActive(admission);
                Require(!Directory.Exists(mutable.SessionRoot), "ReconcileAll did not retire PreLaunch TEMP after orphan exited.");
            }
        } finally {
            File.WriteAllText(childStop, "stop");
            if (childPid > 0) IsProcessGone(childPid, 3000);
        }
    }

    private static void WaitForEmptyCommandJob(string name) {
        // The named job can retain a console host or report an exiting PID after Process.HasExited.
        // Exercise successful recovery only after the held kernel job itself reports zero processes.
        var clock = Stopwatch.StartNew();
        MmcNamedJobObservation job;
        while ((job = MmcSessionProcessTree.ObserveNamedJob(name)).State == MmcNamedJobState.Active &&
               clock.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(25);
        Require(job.State == MmcNamedJobState.Empty, $"Fixture command job did not drain: {job.State}, {job.Diagnostic}");
    }

    private static SafeWaitHandle RetainCommandJob(string name) {
        // Retain the actual kernel evidence as the fixture's processes exit.
        // Background processes on the runner can change during the fallback scan.
        const uint query = 0x0004;
        var job = OpenJobObjectW(query, false, name);
        if (!job.IsInvalid) return job;
        int error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error, "Could not retain the fixture's named command job.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle OpenJobObjectW(uint desiredAccess, bool inheritHandle, string name);

    private static void InjectedKernelJobUnknownAndCrossSessionNeverDowngradeToScan() {
        // Explicit fault-injection boundary test: separates injected OS observation failures (no live child)
        // from real OS process-tree evidence covered in the tests above.
        using var instance = new Instance(useDefaultStart: true, initializeInstanceDefinition: true);
        var paths = MmcInstancePaths.Resolve(instance.Root);
        Guid commandId = Guid.NewGuid();
        var prevJobObserver = MmcCommandOperations.ObserveNamedJobOverride;
        try {
            MmcCommandLease lease;
            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                lease = MmcCommandOperations.Begin(admission, MmcRuntimeCommandStage.PreLoad, null, null, commandId);
            }
            string cmdDir = Path.Combine(instance.Root, "PCL", "MmcCommands", commandId.ToString("N"));
            string intentPath = Path.Combine(cmdDir, "intent.record");
            (var intent, string hash) = MmcDurableFiles.ReadRecord<MmcCommandIntentRecord>(intentPath);
            Require(intent.CreatorWindowsSessionId is not null, "CreatorWindowsSessionId was not persisted.");
            lease.RecordProcessStarted(999999999, DateTime.UtcNow.Ticks, null, jobBacked: true);
            lease.Dispose();

            // 1. AccessDenied (5) from OpenJobObject stays Unknown even when 0 matching processes exist.
            MmcCommandOperations.ObserveNamedJobOverride = _ => new MmcNamedJobObservation(MmcNamedJobState.Unknown, [], 5, "Injected ERROR_ACCESS_DENIED");
            Require(!MmcCommandOperations.IsCommandTreeProvenExited(cmdDir, intent, hash, out _), "AccessDenied job state must stay Unknown.");

            // 2. QueryInformationJobObject failure stays Unknown.
            MmcCommandOperations.ObserveNamedJobOverride = _ => new MmcNamedJobObservation(MmcNamedJobState.Unknown, [], 31, "Injected query failure");
            Require(!MmcCommandOperations.IsCommandTreeProvenExited(cmdDir, intent, hash, out _), "Query failure job state must stay Unknown.");

            // 3. Cross-session CreatorWindowsSessionId stays Unknown even if same-session Job reports Missing.
            MmcCommandOperations.ObserveNamedJobOverride = _ => new MmcNamedJobObservation(MmcNamedJobState.Missing, [], 2, null);
            var otherSessionIntent = intent with { CreatorWindowsSessionId = intent.CreatorWindowsSessionId + 1 };
            Require(!MmcCommandOperations.IsCommandTreeProvenExited(cmdDir, otherSessionIntent, hash, out _), "Cross-session command must never downgrade to exit.");
            var nullSessionIntent = intent with { CreatorWindowsSessionId = null };
            Require(!MmcCommandOperations.IsCommandTreeProvenExited(cmdDir, nullSessionIntent, hash, out _), "Missing session ID must never be assumed exited.");

            // 4. Empty named Job with JobBacked=true confirms exited; ReconcileAll clears once real JobMissing & no orphan.
            MmcCommandOperations.ObserveNamedJobOverride = _ => new MmcNamedJobObservation(MmcNamedJobState.Empty, [], 0, null);
            Require(MmcCommandOperations.IsCommandTreeProvenExited(cmdDir, intent, hash, out _), "Empty JobBacked=true state must confirm exited.");

            using (var admission = MmcAdmissionGate.Acquire(instance.Root, instance.InstanceId, TimeSpan.Zero)) {
                MmcCommandOperations.ReconcileAll(admission);
                MmcCommandOperations.AssertNoPending(admission);
            }
        } finally {
            MmcCommandOperations.ObserveNamedJobOverride = prevJobObserver;
        }
    }

    private static string AppHost() {
        string candidate = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, OperatingSystem.IsWindows() ? ".exe" : null);
        return File.Exists(candidate) ? candidate : throw new Exception("Command fixture requires generated apphost.");
    }

    private static void WaitFileReady(string path, int timeoutMs) {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
        if (!File.Exists(path)) throw new Exception("Timed out waiting for fixture file: " + path);
    }

    private static int WaitChildPid(string readyPath, int timeoutMs) {
        WaitFileReady(readyPath, timeoutMs);
        return int.Parse(File.ReadAllText(readyPath).Trim(), CultureInfo.InvariantCulture);
    }

    private static bool IsProcessGone(int pid, int timeoutMs) {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs) {
            try {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            } catch (ArgumentException) {
                return true;
            }
            Thread.Sleep(25);
        }
        return false;
    }
}
