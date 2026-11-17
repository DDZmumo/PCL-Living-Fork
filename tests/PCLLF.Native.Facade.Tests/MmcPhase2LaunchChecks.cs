using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Network;
using PCLLF.MMCC.Core;
using WatcherHost = PCLCS.LegacyWatcherHost<PCL.ModWatcher.Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<System.Diagnostics.Process, int>, PCL.ModWatcher.Watcher.MinecraftState>;

namespace PCLLF.Native.Facade.Tests;

// Original LF facade checks; exercises actual Main MmcInstances, MmcLaunch and ModWatcher Phase 2 contracts
// (D13 confirmation, D15 env/TEMP isolation, D9 PostExit ordering, real WrapperCommand Job tree + pipes, and §12 hints)
// using self-authored Java 17 probes in an isolated child process.
internal static class MmcPhase2LaunchChecks {
    internal const string Group = "mmc-phase2-launch";
    internal const string DotMcActiveRootGroup = "mmc-phase2-dotmc-active-root";
    internal const string PostExitLeaseGroup = "mmc-phase2-postexit-lease";
    private const string ChildSwitch = "--mmc-phase2-launch-child";
    private static Scope? retained;

    internal static void Run(FixtureEnvironment environment) => RunGroup(environment, Group, s => s.RunPrimary());
    internal static void RunDotMcActiveRoot(FixtureEnvironment environment) => RunGroup(environment, DotMcActiveRootGroup, s => s.RunDotMcActiveRoot());
    internal static void RunPostExitLease(FixtureEnvironment environment) => RunGroup(environment, PostExitLeaseGroup, s => s.RunPostExitLease());

    private static void RunGroup(FixtureEnvironment environment, string group, Action<Scope> runScenario) {
        string receipt = group + ".done";
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!args.Contains(ChildSwitch, StringComparer.Ordinal)) { RunOwnedChild(group, receipt); return; }
        try {
            Native.Check(args.Length == 7 && args[0] == "--groups" && args[1] == group && args[2] == ChildSwitch &&
                Guid.TryParseExact(args[3], "N", out _) && args[4] == "--isolated" && args[5] == "--fixture" &&
                Native.Full(args[6]) == Native.Full(environment.Root), "Phase 2 launch child is not bound to its owned fixture.");
            ModpackUpdateCleanup.VerifyIsolatedChild();
            retained = new(environment);
            runScenario(retained);
            retained.Finish();
            environment.AssertNoForbiddenIo();
            Native.Check(System.Windows.Application.Current.Windows.Count == 0, "Phase 2 launch checks unexpectedly opened a window.");
            string value = string.Join('\n', args[3], Environment.ProcessId, Native.Full(environment.Root), MainHash(), group + "-verified");
            string pending = Path.Combine(environment.Root, receipt + ".pending");
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                output.Write(Encoding.UTF8.GetBytes(value));
                output.Flush(true);
            }
            Console.WriteLine("PASS " + group + ": verified against real Main MmcLaunch/ModWatcher and controlled Java 17 fixtures.");
            Console.Out.Flush(); Console.Error.Flush();
            File.Move(pending, Path.Combine(environment.Root, receipt));
            using ManualResetEventSlim keepGuards = new(); keepGuards.Wait();
        } catch (Exception error) {
            try { retained?.StopOwnedSessions(); } catch { }
            File.WriteAllText(Path.Combine(environment.Root, receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("Phase 2 launch child failed with guards retained: " + error);
        }
    }

    private sealed class Scope {
        private readonly FixtureEnvironment environment;
        private readonly string root, game, cache, metadataRoot, java17, javac, probeDir, traceDir, pinDir;
        private readonly byte[] fixtureJar;
        private readonly MmcMetadataStore metadata;
        private readonly MmcArtifactCacheRoots cacheRoots;
        private readonly PackRootInstallChecksLoopback server = new();
        private static readonly byte[] assetIndexBytes = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        private static readonly string assetIndexSha1 = Convert.ToHexStringLower(SHA1.HashData(assetIndexBytes));
        private readonly ConcurrentQueue<string> watcherLogs = new();
        private readonly ConcurrentDictionary<Guid, MmcRunningSession> capturedSessions = new();

        internal Scope(FixtureEnvironment environment) {
            this.environment = environment;
            root = Path.Combine(environment.Data, "mmc-phase2");
            game = Path.Combine(root, "game");
            cache = Path.Combine(game, "PCL", "MmcCache");
            metadataRoot = Path.Combine(cache, "metadata");
            probeDir = Path.Combine(root, "cmd-probe");
            traceDir = Path.Combine(root, "trace");
            pinDir = Path.Combine(root, "java-pins");
            Directory.CreateDirectory(Path.Combine(game, "versions"));
            Directory.CreateDirectory(Path.Combine(game, "assets", "indexes"));
            File.WriteAllBytes(Path.Combine(game, "assets", "indexes", "fixture.json"), assetIndexBytes);
            server.Add("/assets.json", assetIndexBytes);
            Directory.CreateDirectory(probeDir);
            Directory.CreateDirectory(traceDir);
            Directory.CreateDirectory(pinDir);

            java17 = RequiredJava17();
            javac = Path.Combine(Path.GetDirectoryName(java17)!, "javac.exe");
            Native.Check(File.Exists(javac), "Required javac.exe alongside PCLLF_MMC_TEST_JAVA17 is missing: " + javac);
            fixtureJar = CompileFixtures();

            ConfigProviders.Default = new JsonConfigProvider(Path.Combine(root, "private-profile.json"));
            ModBase.RunInUi();
            _ = FileTaskShell.Install();
            MmcGtnhPostPublishChecks.InstallEmptyLegacyAccountStore();
            foreach (string owner in new[] { "PCL.LocalGameData", "PCL.WorldBackups", "PCL.EnvironmentHistory" })
                Native.Property(Native.Type(owner), "TestRoot").SetValue(null, Path.Combine(root, owner.Split('.').Last()));

            Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java17)!)]));
            Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);

            Native.Property(typeof(MmcLaunch), "JavaPins").SetValue(null, new Func<MmcJavaPinStore>(() => new(pinDir)));
            var mutableAuthority = (MmcMutableRuntimeAuthority)Native.Call(typeof(MmcMutableRuntimeAuthority), "ForOwnedFixture", null, environment.Root)!;
            Native.Property(typeof(MmcLaunch), "MutableRuntimeAuthorityFactory").SetValue(null, new Func<MmcMutableRuntimeAuthority>(() => mutableAuthority));

            var network = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            network.SignCdn = url => server.Owns(url) ? url : throw new IOException("Phase 2 fixture unexpectedly requested CDN download: " + url);
            network.TempFolder = () => Path.Combine(root, "net-temp") + Path.DirectorySeparatorChar;
            network.RequestTempFolder = () => Path.Combine(root, "net-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
            network.AppData = () => Path.Combine(root, "net-appdata") + Path.DirectorySeparatorChar;
            network.MinecraftFolders = () => [game + Path.DirectorySeparatorChar];

            foreach (var pair in new Dictionary<string, object> {
                ["LaunchAdvanceJvm"] = "", ["LaunchAdvanceGame"] = "", ["LaunchAdvanceRun"] = "",
                ["LaunchAdvanceRunWait"] = true, ["LaunchAdvanceDisableJLW"] = false, ["LaunchArgumentJavaSelect"] = "",
                ["LaunchRamType"] = 1, ["LaunchRamCustom"] = 15, ["LaunchArgumentWindowType"] = 3,
                ["LaunchArgumentWindowWidth"] = "854", ["LaunchArgumentWindowHeight"] = "480",
                ["LaunchPersonalOptionsSync"] = false, ["LaunchPersonalOptionsKeys"] = false,
                ["LaunchRecentWorlds"] = false, ["UiMusicStop"] = false, ["UiMusicStart"] = false,
                ["LaunchArgumentVisible"] = 1
            }) Native.Cache(pair.Key, pair.Value);

            var watcherHost = (WatcherHost)Native.Field(typeof(ModWatcher), "Host").GetValue(null)!;
            watcherHost.LaunchLog = (text, _) => watcherLogs.Enqueue(text);
            watcherHost.RefreshShutdownButton = () => { };
            watcherHost.RestoreLauncher = () => { };
            watcherHost.EndProgram = () => { };
            watcherHost.CrashHint = () => { };
            watcherHost.FeedbackInfo = () => { };

            Native.Field(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, game + Path.DirectorySeparatorChar);
            InstallIsolatedLogin("phase2-default-secret-" + Guid.NewGuid().ToString("N"));
            metadata = new MmcMetadataStore(metadataRoot, new Uri("https://metadata.invalid/"), new HttpClient(new NoNetwork()));
            cacheRoots = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(cache, "sources"), Path.Combine(game, "versions"));
        }

        private static void InstallIsolatedLogin(string accessToken) {
            ModLaunch.McLoginLoader = new("Phase2 isolated account", _ => throw new IOException("No account IO expected.")) {
                Output = new() {
                    Name = "Phase2User", Uuid = Guid.NewGuid().ToString("N"),
                    AccessToken = accessToken, ClientToken = "phase2-client-" + Guid.NewGuid().ToString("N"),
                    Type = "Legacy", ProfileJson = "{}"
                },
                State = ModBase.LoadState.Finished
            };
        }

        internal void RunPrimary() {
            string primaryRoot = ImportPrimaryWithHint();
            ExerciseConfirmationEnvPostExitAndEarlyWrapper(primaryRoot);
            ExercisePersistentWrapperCancel();
        }

        internal void RunDotMcActiveRoot() => ExerciseCoexistingDotMinecraftActiveRoot();
        internal void RunPostExitLease() => ExerciseBlockedPostExitSecondHostRecoveryAndSuccessfulStageClearing();

        internal void Finish() {
            Until(() => !MmcLaunch.HasActiveSessions, "MMC active sessions empty");
            Until(() => ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading &&
                !IsThreadAlive(ModMinecraft.McInstanceListLoader), "instance list loader exit");
            DrainUi(DateTime.UtcNow.AddSeconds(10));
            server.Dispose();
            InstanceConfigProviders.FlushAndDisposeWithin(root);
        }

        internal void StopOwnedSessions() {
            foreach (var pair in capturedSessions) {
                try { if (!pair.Value.AreAllProcessesExited) pair.Value.TerminateTree(1000); } catch { }
            }
            try { server.Dispose(); } catch { }
        }

        private string ImportPrimaryWithHint() {
            string archive = Path.Combine(root, "phase2-pack.zip");
            string javaForward = Forward(java17), probeForward = Forward(probeDir), traceForward = Forward(traceDir), pinForward = Forward(pinDir);
            string cfg = string.Join('\n',
                "ConfigVersion=1.3",
                "name=phase2-pack",
                "OverrideJavaArgs=true",
                "JvmArgs=-Dphase2.a=1\\n-Dphase2.b=2",
                "OverrideCommands=true",
                $"PreLoadCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe preload \\\"{traceForward}/preload.txt\\\"",
                $"PreLaunchCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe prelaunch \\\"{traceForward}/prelaunch.txt\\\"",
                $"PostExitCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe postexit \\\"{traceForward}/postexit.txt\\\" \\\"${{INST_DIR}}\\\" \\\"{pinForward}\\\"",
                "WrapperCommand=cmd.exe /d /c start \\\"\\\" /b",
                "OverrideEnv=true",
                "Env=PKG_ENV_KEY=pkg-env-val-987",
                "");
            WritePackArchive(archive, cfg);

            object queue = Native.Field(typeof(ModMain), "HintWaiting").GetValue(null)!;
            Native.Call(queue.GetType(), "Clear", queue);

            var task = ImportViaMain(archive, "phase2-pack");
            Until(() => task.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                !IsThreadAlive(task.Loaders.Single()), "primary MMC import finish");
            Native.Check(task.State == ModBase.LoadState.Finished, "Primary MMC import failed: " + task.Error);
            ModLoader.LoaderTaskbar.Remove(task);
            Until(() => ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading, "instance refresh finish");
            DrainUi(DateTime.UtcNow.AddSeconds(10));

            Array hints = (Array)Native.Call(queue.GetType(), "ToArray", queue)!;
            object? diffHint = hints.Cast<object>().FirstOrDefault(h =>
                ((string?)Value(h, "Text"))?.Contains("Multiline package JVM arguments remain in source", StringComparison.Ordinal) == true);
            Native.Check(diffHint is not null && Value(diffHint, "Type")?.ToString() == "Blue",
                "MmcInstances.ImportAsync did not surface MMC_PACKAGE_JVM_UI_DIFFERENCE Warning through ModMain.Hint.");
            Native.Call(queue.GetType(), "Clear", queue);

            string instanceRoot = Path.Combine(game, "versions", "phase2-pack");
            Native.Check(Directory.Exists(instanceRoot), "Imported phase2 instance directory missing.");
            return instanceRoot;
        }

        private void ExerciseConfirmationEnvPostExitAndEarlyWrapper(string versionRoot) {
            var instance = new ModMinecraft.McInstance(versionRoot);
            Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            string secret = "phase2-secret-" + Guid.NewGuid().ToString("N");
            InstallIsolatedLogin(secret);

            string preloadTxt = Path.Combine(traceDir, "preload.txt");
            string prelaunchTxt = Path.Combine(traceDir, "prelaunch.txt");
            string postexitTxt = Path.Combine(traceDir, "postexit.txt");

            int prompts = 0;
            string? expectedConfirmDigest = null;
            var confirmProp = Native.Property(typeof(MmcLaunch), "ConfirmPackageCommands");
            var createdProp = Native.Property(typeof(MmcLaunch), "ProcessCreated");
            var oldConfirm = (Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>?)confirmProp.GetValue(null);
            var oldCreated = (Action<Guid, Process>?)createdProp.GetValue(null);
            try {
                // 1. Denied confirmation cancels fail-closed BEFORE PreLoad or any stage executes.
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((target, summary) => {
                    prompts++;
                    Native.Check(ReferenceEquals(target, instance), "ConfirmPackageCommands received unexpected instance.");
                    Native.Check(summary.Entries.Select(e => e.Stage).SequenceEqual(new[] {
                        "PreLoadCommand", "PreLaunchCommand", "PostExitCommand", "WrapperCommand"
                    }), "Package command summary did not include all four active stages.");
                    Native.Check(summary.Fingerprint.Length == 64 && summary.Entries.All(e => !e.RedactedCommand.Contains(secret, StringComparison.Ordinal)),
                        "Command confirmation summary exposed secret or missed digest.");
                    expectedConfirmDigest = summary.Fingerprint;
                    return false;
                }));

                string ready0 = Path.Combine(traceDir, "ready0.txt"), stop0 = Path.Combine(traceDir, "stop0.txt");
                var deniedCombo = StartLaunch("phase2-denied", instance, ["--ready", ready0, "--stop", stop0], out _);
                WaitComboTerminal(deniedCombo);
                Native.Check(deniedCombo.State == ModBase.LoadState.Canceled && prompts == 1, "Denied command confirmation did not cancel cleanly.");
                Native.Check(!File.Exists(preloadTxt) && !File.Exists(prelaunchTxt) && !File.Exists(postexitTxt) && !File.Exists(ready0),
                    "Denied package confirmation executed a stage or process.");
                Native.Check(!MmcIniConfiguration.CapturePreferences(instance.Config).Ini.ContainsKey(MmcLaunchPreferences.PackageCommandConfirmedKey),
                    "Denied confirmation persisted MmcPackageCommandConfirmed.");
                Native.Check(!MmcLaunch.HasActiveSessions, "Denied launch retained active session.");

                // 2. Confirmed launch: early-exiting wrapper (cmd /d /c start "" /b), D15 Env & TEMP/TMP, D9 PostExit ordering, log redaction.
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((_, summary) => {
                    prompts++;
                    Native.Check(summary.Fingerprint == expectedConfirmDigest, "Confirmed digest drifted between prompts.");
                    return true;
                }));
                string ready1 = Path.Combine(traceDir, "ready1.txt"), stop1 = Path.Combine(traceDir, "stop1.txt");
                var (combo1, session1, plan1) = RunActiveLaunch("phase2-launch-1", instance, ["--ready", ready1, "--stop", stop1], ready1);
                Native.Check(prompts == 2, "Confirmed launch did not invoke ConfirmPackageCommands exactly once.");
                Native.Check(combo1.State == ModBase.LoadState.Finished, "Confirmed launch failed before Reaching Running: " + combo1.Error);

                // Wrapper exited early while Java remained running and pipes stayed alive.
                Native.Check(session1.IsWrapperManaged && session1.HasOwnedPipes, "Session did not attach wrapper identity & owned pipes.");
                Native.Check(session1.WrapperProcess is { } wrapper1 && wrapper1.WaitForExit(10000), "Early-exiting cmd start /b wrapper did not exit promptly.");
                Native.Check(!session1.Process.HasExited, "Managed Java exited prematurely.");
                Native.Check(MmcLaunch.HasActiveSessions, "Early wrapper exit dropped active session tracking.");

                string runningRecord = File.ReadAllText(Path.Combine(versionRoot, "PCL", "MmcSessions", session1.Receipt.SessionId.ToString("N"), "Running.record"));
                Native.Check(runningRecord.Contains("wrapperProcess", StringComparison.OrdinalIgnoreCase) &&
                    runningRecord.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                    "Running.record lost wrapper identity.");

                var iniAfterConfirm = MmcIniConfiguration.CapturePreferences(instance.Config).Ini;
                Native.Check(iniAfterConfirm.TryGetValue(MmcLaunchPreferences.PackageCommandConfirmedKey, out string? savedConfirm) &&
                    savedConfirm == expectedConfirmDigest, "Accepted package command confirmation digest was not saved to instance Setup.ini.");

                var readyProps1 = ParseProps(ready1);
                var preloadProps1 = ParseProps(preloadTxt);
                var prelaunchProps1 = ParseProps(prelaunchTxt);
                string sessionTemp = plan1.MutableDirectories.TemporaryDirectory;

                Native.Check(readyProps1["pid"] == session1.Process.Id.ToString() &&
                    readyProps1["nonce"] == session1.Receipt.Nonce &&
                    readyProps1["pkg"] == "pkg-env-val-987" &&
                    SamePath(readyProps1["temp"], sessionTemp),
                    "Managed Java under WrapperCommand lost nonce, PKG_ENV_KEY, or session TEMP.");

                Native.Check(string.IsNullOrEmpty(preloadProps1["pkg"]) && !SamePath(preloadProps1["temp"], sessionTemp),
                    "D15 violation: PreLoadCommand received package Env or active session TEMP.");

                Native.Check(prelaunchProps1["pkg"] == "pkg-env-val-987" &&
                    SamePath(prelaunchProps1["temp"], sessionTemp) &&
                    SamePath(prelaunchProps1["tmp"], sessionTemp) &&
                    prelaunchProps1["tempDirExists"] == "1",
                    "D15 violation: PreLaunchCommand did not receive package Env and materialized session TEMP/TMP.");

                // Signal clean exit of managed Java -> PostExitCommand runs BEFORE mutable retirement & pin release.
                File.WriteAllText(stop1, "stop");
                Until(() => !MmcLaunch.HasActiveSessions && File.Exists(postexitTxt) &&
                    !Directory.Exists(plan1.MutableDirectories.SessionRoot) &&
                    Directory.GetFiles(pinDir, "*.json", SearchOption.AllDirectories).Length == 0,
                    "PostExit + FinalizeVerifiedExit settlement");

                var postexitProps1 = ParseProps(postexitTxt);
                Native.Check(postexitProps1["pkg"] == "pkg-env-val-987" &&
                    SamePath(postexitProps1["temp"], sessionTemp) &&
                    SamePath(postexitProps1["tmp"], sessionTemp) &&
                    postexitProps1["tempDirExists"] == "1",
                    "D15 violation: PostExitCommand lacked package Env or live TemporaryDirectory.");
                Native.Check(postexitProps1["exitedRecordCount"] == "1" && postexitProps1["pinFileCount"] == "1",
                    "D9 ordering violation: PostExit did not run strictly AFTER Exited.record and BEFORE pin release.");

                string historyExited = Path.Combine(versionRoot, "PCL", "MmcSessionHistory", session1.Receipt.SessionId.ToString("N"), "Exited.record");
                Native.Check(File.Exists(historyExited) && File.ReadAllText(historyExited).Contains(MmcSessionExitProof.WrapperManagedExit, StringComparison.Ordinal),
                    "Wrapped session exit proof was not archived as WrapperManagedExit.");

                string[] latest = ModLaunch.McLaunchWatcher.LatestLog.Concat(ModLaunch.McLaunchWatcher.WaitingLog).Where(l => l is not null).ToArray();
                bool hasStarted = latest.Any(l => l.Contains("GAME_STARTED", StringComparison.Ordinal));
                bool hasRedacted = latest.Any(l => l.Contains("ACCESS_ECHO=[redacted]", StringComparison.Ordinal));
                bool noSecret = latest.All(l => !l.Contains(secret, StringComparison.Ordinal));
                Native.Check(hasStarted && hasRedacted && noSecret,
                    $"Owned pipe log streaming lost stdout or leaked raw secret through ModWatcher (count={latest.Length}, started={hasStarted}, redacted={hasRedacted}, noSecret={noSecret}, sample={string.Join(" | ", latest.Take(10))}).");

                // 3. Repeat launch reuses confirmation without reprompting.
                File.Delete(preloadTxt); File.Delete(prelaunchTxt); File.Delete(postexitTxt);
                string ready2 = Path.Combine(traceDir, "ready2.txt"), stop2 = Path.Combine(traceDir, "stop2.txt");
                var (combo2, _, plan2) = RunActiveLaunch("phase2-launch-2", instance, ["--ready", ready2, "--stop", stop2], ready2);
                Native.Check(combo2.State == ModBase.LoadState.Finished && prompts == 2,
                    "Unchanged package commands reprompted even after MmcPackageCommandConfirmed was persisted.");
                File.WriteAllText(stop2, "stop");
                Until(() => !MmcLaunch.HasActiveSessions && !Directory.Exists(plan2.MutableDirectories.SessionRoot), "repeat launch exit");

                // 4. Stale / mismatched confirmation digest fails closed and re-prompts.
                MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string?> {
                    [MmcLaunchPreferences.PackageCommandConfirmedKey] = new string('0', 64)
                });
                instance.Config.Save();
                var cfgLoaded = MmcSavedSourceStore.LoadAsync(MmcInstancePaths.Resolve(versionRoot), MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(versionRoot)).Definition).GetAwaiter().GetResult().PackageConfiguration;
                var currentSummary = MmcLaunchPreferences.CaptureActivePackageCommands(cfgLoaded)!;
                Native.Check(!MmcLaunchPreferences.IsPackageCommandsConfirmed(instance, currentSummary),
                    "Mismatched MmcPackageCommandConfirmed digest was still treated as confirmed.");
                string readyDeny = Path.Combine(traceDir, "readyDeny.txt"), stopDeny = Path.Combine(traceDir, "stopDeny.txt");
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((_, s) => { prompts++; Native.Check(s.Fingerprint == expectedConfirmDigest, "Summary mismatch."); return false; }));
                var staleDenied = StartLaunch("phase2-stale-denied", instance, ["--ready", readyDeny, "--stop", stopDeny], out _);
                WaitComboTerminal(staleDenied);
                Native.Check(staleDenied.State == ModBase.LoadState.Canceled && prompts == 3,
                    "Changed/stale package command confirmation did not reprompt and fail closed on denial.");
            } finally {
                confirmProp.SetValue(null, oldConfirm);
                createdProp.SetValue(null, oldCreated);
            }
        }

        private void ExercisePersistentWrapperCancel() {
            string archive = Path.Combine(root, "phase2-cancel.zip");
            string cfg = string.Join('\n',
                "ConfigVersion=1.3",
                "name=phase2-cancel",
                "OverrideCommands=true",
                "WrapperCommand=cmd.exe /d /c call",
                "");
            WritePackArchive(archive, cfg);
            var importTask = ImportViaMain(archive, "phase2-cancel");
            Until(() => importTask.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                !IsThreadAlive(importTask.Loaders.Single()), "cancel instance import");
            Native.Check(importTask.State == ModBase.LoadState.Finished, "Cancel test instance import failed: " + importTask.Error);
            ModLoader.LoaderTaskbar.Remove(importTask);

            string versionRoot = Path.Combine(game, "versions", "phase2-cancel");
            var instance = new ModMinecraft.McInstance(versionRoot);
            Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            var confirmProp = Native.Property(typeof(MmcLaunch), "ConfirmPackageCommands");
            var createdProp = Native.Property(typeof(MmcLaunch), "ProcessCreated");
            var oldConfirm = (Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>?)confirmProp.GetValue(null);
            try {
                bool prompted = false;
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((_, summary) => {
                    prompted = true;
                    return summary.Entries.Select(e => e.Stage).SequenceEqual(new[] { "WrapperCommand" });
                }));
                string ready3 = Path.Combine(traceDir, "ready3.txt"), stop3 = Path.Combine(traceDir, "stop3.txt");
                var combo = StartLaunch("phase2-cancel-wrap", instance, ["--ready", ready3, "--stop", stop3, "--hold-loading"], out object context);
                MmcRunningSession? running = null;
                Guid sessionId = Guid.Empty;
                var oldCreated = (Action<Guid, Process>?)createdProp.GetValue(null);
                createdProp.SetValue(null, new Action<Guid, Process>((id, proc) => {
                    oldCreated?.Invoke(id, proc);
                    sessionId = id;
                    var active = (ConcurrentDictionary<Guid, MmcRunningSession>)Native.Field(typeof(MmcLaunch), "Active").GetValue(null)!;
                    if (active.TryGetValue(id, out var s)) { running = s; capturedSessions[id] = s; }
                }));
                try {
                    Until(() => File.Exists(ready3) && running is not null, "persistent wrapped process ready");
                    Native.Check(prompted, "Persistent wrapper launch did not prompt for WrapperCommand confirmation.");
                    Process wrapperProc = running!.WrapperProcess ?? throw new IOException("WrapperProcess missing on wrapped session.");
                    Process javaProc = running.Process;
                    Native.Check(!wrapperProc.HasExited && !javaProc.HasExited, "Persistent wrapper or Java exited before cancellation.");

                    combo.Cancel();
                    WaitComboTerminal(combo);
                    Until(() => !MmcLaunch.HasActiveSessions && Directory.GetFiles(pinDir, "*.json", SearchOption.AllDirectories).Length == 0,
                        "wrapped cancellation settlement");
                    DrainUi(DateTime.UtcNow.AddSeconds(10));

                    Native.Check(combo.State == ModBase.LoadState.Canceled, "Cancelled wrapped launch ended as " + combo.State);
                    Native.Check(!watcherLogs.Any(line => line.Contains("崩溃", StringComparison.Ordinal)),
                        "Cancelled wrapped launch unexpectedly triggered crash analysis.");
                    Native.Check(wrapperProc.HasExited && javaProc.HasExited && running.AreAllProcessesExited,
                        "CancelGame did not terminate both persistent wrapper and managed Java.");
                    var runtime = (MmcRuntimeLaunchPlan)Native.Get(context, "runtime")!;
                    Native.Check(!Directory.Exists(runtime.MutableDirectories.SessionRoot),
                        "Cancelled wrapped launch leaked mutable runtime directory.");
                    string historyExited = Path.Combine(versionRoot, "PCL", "MmcSessionHistory", sessionId.ToString("N"), "Exited.record");
                    Native.Check(File.Exists(historyExited) && File.ReadAllText(historyExited).Contains(MmcSessionExitProof.WrapperManagedExit, StringComparison.Ordinal),
                        "Cancelled wrapped launch did not archive WrapperManagedExit.");
                } finally {
                    createdProp.SetValue(null, oldCreated);
                }
            } finally {
                confirmProp.SetValue(null, oldConfirm);
            }
        }

        private void ExerciseCoexistingDotMinecraftActiveRoot() {
            string archive = Path.Combine(root, "phase2-dotmc.zip");
            string javaForward = Forward(java17), probeForward = Forward(probeDir), traceForward = Forward(traceDir);
            string cfg = string.Join('\n',
                "ConfigVersion=1.3",
                "name=phase2-dotmc",
                "OverrideCommands=true",
                $"PreLoadCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe preload \\\"{traceForward}/dotmc-preload.txt\\\" \\\"${{INST_DIR}}\\\" \\\"\\\" \\\"${{INST_MC_DIR}}\\\" \\\"${{game_directory}}\\\" \\\"rel-preload.txt\\\"",
                $"PreLaunchCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe prelaunch \\\"{traceForward}/dotmc-prelaunch.txt\\\" \\\"${{INST_DIR}}\\\" \\\"\\\" \\\"${{INST_MC_DIR}}\\\" \\\"${{game_directory}}\\\" \\\"rel-prelaunch.txt\\\"",
                $"PostExitCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe postexit \\\"{traceForward}/dotmc-postexit.txt\\\" \\\"${{INST_DIR}}\\\" \\\"\\\" \\\"${{INST_MC_DIR}}\\\" \\\"${{game_directory}}\\\" \\\"rel-postexit.txt\\\"",
                "");
            WritePackArchive(archive, cfg, dotMinecraftRoot: true);

            var importTask = ImportViaMain(archive, "phase2-dotmc");
            Until(() => importTask.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                !IsThreadAlive(importTask.Loaders.Single()), "dotmc instance import");
            Native.Check(importTask.State == ModBase.LoadState.Finished, "dotmc instance import failed: " + importTask.Error);
            ModLoader.LoaderTaskbar.Remove(importTask);
            Until(() => ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading, "dotmc instance refresh finish");
            DrainUi(DateTime.UtcNow.AddSeconds(10));

            string versionRoot = Path.Combine(game, "versions", "phase2-dotmc");
            string activeGameRoot = Path.Combine(versionRoot, ".minecraft");
            string inactiveGameRoot = Path.Combine(versionRoot, "minecraft");
            Native.Check(Directory.Exists(activeGameRoot), "Imported active .minecraft directory missing.");
            Native.Check(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(versionRoot, ".minecraft")).Definition.Layout.GameRootLeaf == ".minecraft",
                "Imported pack did not persist .minecraft layout.");

            // Materialize a coexisting inactive minecraft tree; MmcLaunch must keep all package stages on active .minecraft.
            Directory.CreateDirectory(inactiveGameRoot);
            string inactiveSentinel = Path.Combine(inactiveGameRoot, "inactive-keep.txt");
            File.WriteAllText(inactiveSentinel, "inactive-minecraft-preserved\n", new UTF8Encoding(false));

            var instance = new ModMinecraft.McInstance(versionRoot);
            Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            var confirmProp = Native.Property(typeof(MmcLaunch), "ConfirmPackageCommands");
            var oldConfirm = (Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>?)confirmProp.GetValue(null);
            try {
                bool confirmed = false;
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((target, summary) => {
                    confirmed = true;
                    Native.Check(ReferenceEquals(target, instance), "dotmc confirmation target mismatch.");
                    Native.Check(summary.Entries.Select(e => e.Stage).SequenceEqual(new[] {
                        "PreLoadCommand", "PreLaunchCommand", "PostExitCommand"
                    }), "dotmc confirmation stages mismatch.");
                    return true;
                }));
                string ready = Path.Combine(traceDir, "dotmc-ready.txt"), stop = Path.Combine(traceDir, "dotmc-stop.txt");
                var (combo, session, plan) = RunActiveLaunch("phase2-dotmc-launch", instance, ["--ready", ready, "--stop", stop], ready);
                Native.Check(confirmed, "dotmc launch did not prompt package command confirmation.");
                Native.Check(combo.State == ModBase.LoadState.Finished, "dotmc launch failed: " + combo.Error);
                Native.Check(SamePath(plan.Request.Paths.GameRoot, activeGameRoot), "Runtime plan GameRoot did not match active .minecraft.");

                File.WriteAllText(stop, "stop");
                string postexitOut = Path.Combine(traceDir, "dotmc-postexit.txt");
                Until(() => session.Process.HasExited && !MmcLaunch.HasActiveSessions && File.Exists(postexitOut) &&
                    !Directory.Exists(plan.MutableDirectories.SessionRoot), "dotmc launch PostExit & cleanup");

                foreach (string stage in new[] { "preload", "prelaunch", "postexit" }) {
                    var props = ParseProps(Path.Combine(traceDir, $"dotmc-{stage}.txt"));
                    Native.Check(SamePath(props["cwd"], activeGameRoot),
                        $"Stage {stage} cwd resolved to '{props["cwd"]}' instead of active '{activeGameRoot}'.");
                    Native.Check(SamePath(props["instMcDir"], activeGameRoot),
                        $"Stage {stage} INST_MC_DIR resolved to '{props["instMcDir"]}' instead of active '{activeGameRoot}'.");
                    Native.Check(SamePath(props["gameDir"], activeGameRoot),
                        $"Stage {stage} game_directory resolved to '{props["gameDir"]}' instead of active '{activeGameRoot}'.");
                    string activeRel = Path.Combine(activeGameRoot, $"rel-{stage}.txt");
                    Native.Check(File.Exists(activeRel), $"Stage {stage} relative write missing under active .minecraft.");
                    string[] relLines = File.ReadAllLines(activeRel, Encoding.UTF8);
                    Native.Check(relLines.Length >= 2 && relLines[0] == stage && SamePath(relLines[1], activeGameRoot),
                        $"Stage {stage} relative write recorded wrong cwd.");
                    Native.Check(!File.Exists(Path.Combine(inactiveGameRoot, $"rel-{stage}.txt")),
                        $"Stage {stage} leaked relative write into inactive minecraft tree.");
                }

                Native.Check(File.ReadAllText(inactiveSentinel, Encoding.UTF8) == "inactive-minecraft-preserved\n",
                    "Inactive minecraft sentinel was altered.");
                string[] inactiveEntries = Directory.GetFileSystemEntries(inactiveGameRoot);
                Native.Check(inactiveEntries.Length == 1 && SamePath(inactiveEntries[0], inactiveSentinel),
                    "Unexpected entries written into inactive minecraft tree: " + string.Join(", ", inactiveEntries));
                Native.Check(File.ReadAllText(Path.Combine(activeGameRoot, "pack-sentinel.txt"), Encoding.UTF8) == "active-dot-minecraft-layout\n",
                    "Active .minecraft pack sentinel was altered.");

                // Re-verify when active .minecraft is absent while inactive minecraft already exists:
                // MmcLaunch must create active .minecraft and run all three stages into it without altering minecraft.
                Directory.Delete(activeGameRoot, true);
                foreach (string stage in new[] { "preload", "prelaunch", "postexit" }) {
                    string p = Path.Combine(traceDir, $"dotmc-{stage}.txt");
                    if (File.Exists(p)) File.Delete(p);
                }
                string readyRecreate = Path.Combine(traceDir, "dotmc-recreate-ready.txt"), stopRecreate = Path.Combine(traceDir, "dotmc-recreate-stop.txt");
                var (comboRecreate, sessionRecreate, planRecreate) = RunActiveLaunch("phase2-dotmc-recreate", instance, ["--ready", readyRecreate, "--stop", stopRecreate], readyRecreate);
                Native.Check(comboRecreate.State == ModBase.LoadState.Finished, "dotmc recreate launch failed: " + comboRecreate.Error);
                Native.Check(Directory.Exists(activeGameRoot) && SamePath(planRecreate.Request.Paths.GameRoot, activeGameRoot),
                    "Missing active .minecraft was not recreated as active GameRoot.");
                File.WriteAllText(stopRecreate, "stop");
                Until(() => sessionRecreate.Process.HasExited && !MmcLaunch.HasActiveSessions && File.Exists(Path.Combine(traceDir, "dotmc-postexit.txt")) &&
                    !Directory.Exists(planRecreate.MutableDirectories.SessionRoot), "dotmc recreate PostExit & cleanup");
                foreach (string stage in new[] { "preload", "prelaunch", "postexit" }) {
                    var props = ParseProps(Path.Combine(traceDir, $"dotmc-{stage}.txt"));
                    Native.Check(SamePath(props["cwd"], activeGameRoot) && SamePath(props["instMcDir"], activeGameRoot) && SamePath(props["gameDir"], activeGameRoot),
                        $"Recreated dotmc stage {stage} missed active .minecraft.");
                    Native.Check(File.Exists(Path.Combine(activeGameRoot, $"rel-{stage}.txt")) && !File.Exists(Path.Combine(inactiveGameRoot, $"rel-{stage}.txt")),
                        $"Recreated dotmc stage {stage} wrote outside active .minecraft.");
                }
                Native.Check(Directory.GetFileSystemEntries(inactiveGameRoot).Length == 1 && SamePath(Directory.GetFileSystemEntries(inactiveGameRoot)[0], inactiveSentinel),
                    "Inactive minecraft tree changed during missing-.minecraft creation launch.");
            } finally {
                confirmProp.SetValue(null, oldConfirm);
            }
        }

        private void ExerciseBlockedPostExitSecondHostRecoveryAndSuccessfulStageClearing() {
            string archive = Path.Combine(root, "phase2-postexit-lease.zip");
            string javaForward = Forward(java17), probeForward = Forward(probeDir), traceForward = Forward(traceDir), pinForward = Forward(pinDir);
            string enteredPath = Path.Combine(traceDir, "postexit-lease-entered.txt");
            string gatePath = Path.Combine(traceDir, "postexit-lease-gate.txt");
            string cfg = string.Join('\n',
                "ConfigVersion=1.3",
                "name=phase2-postexit-lease",
                "OverrideCommands=true",
                $"PreLoadCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe preload \\\"{traceForward}/lease-preload.txt\\\"",
                $"PreLaunchCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe prelaunch \\\"{traceForward}/lease-prelaunch.txt\\\"",
                $"PostExitCommand=\\\"{javaForward}\\\" -cp \\\"{probeForward}\\\" fixture.CmdProbe postexit \\\"{traceForward}/lease-postexit.txt\\\" \\\"${{INST_DIR}}\\\" \\\"{pinForward}\\\" \\\"\\\" \\\"\\\" \\\"\\\" \\\"{Forward(enteredPath)}\\\" \\\"{Forward(gatePath)}\\\"",
                "OverrideEnv=true",
                "Env=PKG_ENV_KEY=pkg-lease-val",
                "");
            WritePackArchive(archive, cfg);

            var importTask = ImportViaMain(archive, "phase2-postexit-lease");
            Until(() => importTask.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                !IsThreadAlive(importTask.Loaders.Single()), "postexit-lease import");
            Native.Check(importTask.State == ModBase.LoadState.Finished, "postexit-lease import failed: " + importTask.Error);
            ModLoader.LoaderTaskbar.Remove(importTask);
            Until(() => ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading, "postexit-lease instance refresh finish");
            DrainUi(DateTime.UtcNow.AddSeconds(10));

            string versionRoot = Path.Combine(game, "versions", "phase2-postexit-lease");
            string cmdStore = Path.Combine(versionRoot, "PCL", "MmcCommands");
            var instance = new ModMinecraft.McInstance(versionRoot);
            Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            var confirmProp = Native.Property(typeof(MmcLaunch), "ConfirmPackageCommands");
            var oldConfirm = (Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>?)confirmProp.GetValue(null);
            try {
                confirmProp.SetValue(null, new Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool>((_, _) => true));
                string ready1 = Path.Combine(traceDir, "lease-ready1.txt"), stop1 = Path.Combine(traceDir, "lease-stop1.txt");
                var (combo1, session1, plan1) = RunActiveLaunch("phase2-lease-launch-1", instance, ["--ready", ready1, "--stop", stop1], ready1);
                Native.Check(combo1.State == ModBase.LoadState.Finished, "First launch in PostExit lease test failed: " + combo1.Error);

                // Bug #1 verification: successful PreLoad + PreLaunch must settle their MmcCommands lease cleanly with zero leftover files!
                Native.Check(!Directory.Exists(cmdStore) || Directory.GetFileSystemEntries(cmdStore).Length == 0,
                    "Successful PreLoad/PreLaunch left residual PCL/MmcCommands entries.");

                Guid id = session1.Receipt.SessionId;
                string exitedReceipt = Path.Combine(versionRoot, "PCL", "MmcSessions", id.ToString("N"), "Exited.record");
                File.WriteAllText(stop1, "stop");
                Until(() => session1.Process.HasExited && File.Exists(exitedReceipt) && File.Exists(enteredPath),
                    "game exited with Exited.record while PostExit is actively blocked");

                // While PostExit is running (outside admission), owner.lock must keep TEMP, pin, and session receipts intact against second-host Reconcile and second Launch.
                Guid instanceId = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(versionRoot)).Definition.InstanceId;
                using (var lease = MmcAdmissionGate.Acquire(versionRoot, instanceId, TimeSpan.FromSeconds(5))) {
                    var obs = MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => throw new Exception("Second host unpinned Java during active PostExit!")).Observation;
                    Native.Check(obs == MmcSessionObservation.Unknown, "Second host Reconcile returned " + obs + " instead of Unknown during active PostExit.");
                }

                string blockedReady = Path.Combine(traceDir, "lease-blocked-ready.txt"), blockedStop = Path.Combine(traceDir, "lease-blocked-stop.txt");
                var blockedSecond = StartLaunch("phase2-lease-second-blocked", instance, ["--ready", blockedReady, "--stop", blockedStop], out _);
                WaitComboTerminal(blockedSecond);
                Native.Check(blockedSecond.State == ModBase.LoadState.Failed,
                    "Second launch did not fail closed while first launch's PostExit owner lease was active. State=" + blockedSecond.State);
                Native.Check(!File.Exists(blockedReady), "Second launch started game despite active PostExit owner lease.");
                Native.Check(Directory.Exists(plan1.MutableDirectories.TemporaryDirectory), "Second host attempt retired TEMP during active PostExit.");
                Native.Check(Directory.GetFiles(pinDir, "*.json", SearchOption.AllDirectories).Length == 1, "Second host attempt removed Java pin during active PostExit.");
                Native.Check(File.Exists(exitedReceipt), "Second host attempt removed/archived Exited.record during active PostExit.");

                // Release PostExit gate -> single clean archival + cleanup + unpin, followed by clean subsequent launch.
                File.WriteAllText(gatePath, "release");
                string postexitOut = Path.Combine(traceDir, "lease-postexit.txt");
                Until(() => !MmcLaunch.HasActiveSessions && File.Exists(postexitOut) &&
                    !Directory.Exists(plan1.MutableDirectories.SessionRoot) &&
                    Directory.GetFiles(pinDir, "*.json", SearchOption.AllDirectories).Length == 0,
                    "PostExit release & single clean archival");
                Native.Check(!Directory.Exists(cmdStore) || Directory.GetFileSystemEntries(cmdStore).Length == 0,
                    "Successful PostExit left residual PCL/MmcCommands entries.");
                Native.Check(Directory.Exists(Path.Combine(versionRoot, "PCL", "MmcSessionHistory", id.ToString("N"))),
                    "Session was not archived once to MmcSessionHistory after PostExit finished.");
                Native.Check(!Directory.Exists(Path.Combine(versionRoot, "PCL", "MmcSessions", id.ToString("N"))),
                    "Active session directory survived finalization.");

                // Launch #2 must now succeed cleanly with PreLoad/PreLaunch/PostExit all settling cleanly!
                string ready2 = Path.Combine(traceDir, "lease-ready2.txt"), stop2 = Path.Combine(traceDir, "lease-stop2.txt");
                var (combo2, session2, plan2) = RunActiveLaunch("phase2-lease-launch-2", instance, ["--ready", ready2, "--stop", stop2], ready2);
                Native.Check(combo2.State == ModBase.LoadState.Finished, "Second launch after PostExit completion failed: " + combo2.Error);
                File.WriteAllText(stop2, "stop");
                Until(() => session2.Process.HasExited && !MmcLaunch.HasActiveSessions && !Directory.Exists(plan2.MutableDirectories.SessionRoot) &&
                    Directory.GetFiles(pinDir, "*.json", SearchOption.AllDirectories).Length == 0,
                    "Subsequent launch exit & settlement");
                Native.Check(!Directory.Exists(cmdStore) || Directory.GetFileSystemEntries(cmdStore).Length == 0,
                    "Subsequent launch left residual PCL/MmcCommands entries.");
            } finally {
                File.WriteAllText(gatePath, "release");
                confirmProp.SetValue(null, oldConfirm);
            }
        }

        private ModLoader.LoaderCombo<string> ImportViaMain(string archive, string name) {
            var hook = Native.Property(typeof(MmcInstances), "ServicesFactory");
            object? old = hook.GetValue(null);
            hook.SetValue(null, new Func<ModpackMmcInstallRequest, MmcImportServices>(_ => new() {
                Metadata = metadata, CacheRoots = cacheRoots,
                SelectName = _ => name,
                Published = _ => { },
                DownloadCreated = _ => { }
            }));
            try { return MmcInstances.StartImport(new(archive, game)); }
            finally { hook.SetValue(null, old); }
        }

        private (ModLoader.LoaderCombo<object> Combo, MmcRunningSession Session, MmcRuntimeLaunchPlan Plan) RunActiveLaunch(
            string name, ModMinecraft.McInstance instance, string[] extraGameArgs, string readyFile) {
            MmcRunningSession? captured = null;
            var createdProp = Native.Property(typeof(MmcLaunch), "ProcessCreated");
            var oldCreated = (Action<Guid, Process>?)createdProp.GetValue(null);
            createdProp.SetValue(null, new Action<Guid, Process>((id, proc) => {
                oldCreated?.Invoke(id, proc);
                var active = (ConcurrentDictionary<Guid, MmcRunningSession>)Native.Field(typeof(MmcLaunch), "Active").GetValue(null)!;
                if (active.TryGetValue(id, out var s)) { captured = s; capturedSessions[id] = s; }
            }));
            try {
                var combo = StartLaunch(name, instance, extraGameArgs, out object context);
                WaitComboTerminal(combo);
                Native.Check(captured is not null, $"Launch completed without registering an MmcRunningSession. State={combo.State}, Error={combo.Error}");
                Until(() => File.Exists(readyFile), "Java ready file written");
                var runtime = (MmcRuntimeLaunchPlan)Native.Get(context, "runtime")!;
                return (combo, captured!, runtime);
            } finally {
                createdProp.SetValue(null, oldCreated);
            }
        }

        private static ModLoader.LoaderCombo<object> StartLaunch(string name, ModMinecraft.McInstance instance, string[] extraGameArgs, out object context) {
            ModLaunch.McLaunchProcess = null!;
            ModLaunch.McLaunchWatcher = null!;
            var options = new ModLaunch.McLaunchOptions { Instance = instance };
            foreach (string arg in extraGameArgs) options.ExtraGameArgs.Add(arg);
            ModLaunch.CurrentLaunchOptions = options;
            var steps = MmcLaunch.CreateSteps(instance, options, j => ModLaunch.McLaunchJavaSelected = j);
            context = steps.OfType<LegacyLaunchProcessStep>().Single().Work.Target!;
            var operation = (LegacyLaunchOperation)Native.Static("PCL.ModLaunch", "CreateLaunchCombo", name, steps)!;
            var combo = (ModLoader.LoaderCombo<object>)operation.Source;
            ModLaunch.McLaunchLoaderReal = combo;
            ModLoader.LoaderTaskbar.Add(combo);
            operation.Start();
            return combo;
        }

        private static void WaitComboTerminal(ModLoader.LoaderCombo<object> combo) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (combo.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading ||
                combo.Loaders.Any(IsThreadAlive)) {
                DrainUi(deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("MMC launch combo did not finish cleanly: " + combo.State + " / " + combo.Error);
                Thread.Sleep(5);
            }
            DrainUi(deadline);
            ModLoader.LoaderTaskbar.Remove(combo);
        }

        private static bool IsThreadAlive(ModLoader.LoaderBase loader) =>
            Native.Get(loader, "LastRunningThread") is Thread { IsAlive: true };

        private void WritePackArchive(string path, string cfg, bool dotMinecraftRoot = false) {
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            AddEntry(zip, "mmc-pack.json", Encoding.UTF8.GetBytes("""{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.19.2"}]}"""));
            AddEntry(zip, "patches/net.minecraft.json", Encoding.UTF8.GetBytes(
                $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.19.2","mainClass":"fixture.GameMain","minecraftArguments":"--username ${auth_player_name} --accessToken ${auth_access_token}","assetIndex":{"id":"fixture","url":"{{server.Url("/assets.json")}}","sha1":"{{assetIndexSha1}}","size":{{assetIndexBytes.Length}}},"mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"compatibleJavaMajors":[17]}"""));
            AddEntry(zip, "libraries/fixture.jar", fixtureJar);
            AddEntry(zip, "instance.cfg", Encoding.UTF8.GetBytes(cfg));
            if (dotMinecraftRoot) AddEntry(zip, ".minecraft/pack-sentinel.txt", Encoding.UTF8.GetBytes("active-dot-minecraft-layout\n"));
        }

        private static void AddEntry(ZipArchive zip, string name, byte[] bytes) {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private byte[] CompileFixtures() {
            string src = Path.Combine(root, "src", "fixture"), gameClasses = Path.Combine(root, "game-classes");
            Directory.CreateDirectory(src); Directory.CreateDirectory(gameClasses);
            string cmdProbeJava = Path.Combine(src, "CmdProbe.java");
            string gameMainJava = Path.Combine(src, "GameMain.java");
            File.WriteAllText(cmdProbeJava, """
package fixture;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.stream.Stream;

public final class CmdProbe {
    public static void main(String[] args) throws Exception {
        String stage = args[0];
        Path out = Paths.get(args[1]);
        String instDir = args.length >= 3 ? args[2] : "";
        String pinDir = args.length >= 4 ? args[3] : "";
        String instMcDir = args.length >= 5 ? args[4] : "";
        String gameDir = args.length >= 6 ? args[5] : "";
        String relWrite = args.length >= 7 ? args[6] : "";
        String enteredFile = args.length >= 8 ? args[7] : "";
        String waitGateFile = args.length >= 9 ? args[8] : "";
        if (!enteredFile.isEmpty()) {
            Path entered = Paths.get(enteredFile);
            if (entered.getParent() != null) Files.createDirectories(entered.getParent());
            Files.writeString(entered, "entered\n", StandardCharsets.UTF_8);
        }
        if (!waitGateFile.isEmpty()) {
            Path gate = Paths.get(waitGateFile);
            long gateDeadline = System.currentTimeMillis() + 25_000L;
            while (!Files.exists(gate) && System.currentTimeMillis() < gateDeadline) Thread.sleep(20);
        }
        String cwd = Paths.get("").toAbsolutePath().normalize().toString();
        if (!relWrite.isEmpty()) {
            Files.writeString(Paths.get(relWrite), stage + "\n" + cwd + "\n", StandardCharsets.UTF_8);
        }
        String temp = System.getenv("TEMP");
        String tmp = System.getenv("TMP");
        String pkg = System.getenv("PKG_ENV_KEY");
        String tempDirExists = (temp != null && !temp.isEmpty() && Files.isDirectory(Paths.get(temp))) ? "1" : "0";
        long exitedCount = 0;
        if (!instDir.isEmpty()) {
            Path sessions = Paths.get(instDir, "PCL", "MmcSessions");
            if (Files.isDirectory(sessions)) {
                try (Stream<Path> walk = Files.walk(sessions, 2)) {
                    exitedCount = walk.filter(p -> p.getFileName().toString().equals("Exited.record")).count();
                }
            }
        }
        long pinCount = 0;
        if (!pinDir.isEmpty() && Files.isDirectory(Paths.get(pinDir))) {
            try (Stream<Path> walk = Files.walk(Paths.get(pinDir), 3)) {
                pinCount = walk.filter(p -> Files.isRegularFile(p) && p.getFileName().toString().endsWith(".json")).count();
            }
        }
        String content = "stage=" + stage + "\n"
            + "cwd=" + cwd + "\n"
            + "instMcDir=" + instMcDir + "\n"
            + "gameDir=" + gameDir + "\n"
            + "temp=" + (temp == null ? "" : temp) + "\n"
            + "tmp=" + (tmp == null ? "" : tmp) + "\n"
            + "pkg=" + (pkg == null ? "" : pkg) + "\n"
            + "tempDirExists=" + tempDirExists + "\n"
            + "exitedRecordCount=" + exitedCount + "\n"
            + "pinFileCount=" + pinCount + "\n";
        Files.createDirectories(out.getParent());
        Files.writeString(out, content, StandardCharsets.UTF_8);
    }
}
""", new UTF8Encoding(false));

            File.WriteAllText(gameMainJava, """
package fixture;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

public final class GameMain {
    public static void main(String[] args) throws Exception {
        String ready = null, stop = null, token = "";
        boolean holdLoading = false;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--ready": ready = args[++i]; break;
                case "--stop": stop = args[++i]; break;
                case "--accessToken": token = args[++i]; break;
                case "--hold-loading": holdLoading = true; break;
            }
        }
        String nonce = System.getProperty("pcllf.mmc.session", "");
        String pkg = System.getenv("PKG_ENV_KEY");
        String temp = System.getenv("TEMP");
        System.out.println("GAME_STARTED");
        System.out.println("ACCESS_ECHO=" + token);
        if (!holdLoading) {
            System.out.println("Setting user: fixture");
            System.out.println("LWJGL version: 3");
            System.out.println("OpenAL initialized");
            System.out.println("Created: 1x1 textures-atlas");
            System.out.println("Found animation info");
        }
        System.out.flush();
        if (ready != null) {
            String text = "pid=" + ProcessHandle.current().pid() + "\n"
                + "nonce=" + nonce + "\n"
                + "pkg=" + (pkg == null ? "" : pkg) + "\n"
                + "temp=" + (temp == null ? "" : temp) + "\n";
            Path p = Paths.get(ready);
            Files.createDirectories(p.getParent());
            Files.writeString(p, text, StandardCharsets.UTF_8);
        }
        if (stop != null) {
            Path stopPath = Paths.get(stop);
            long deadline = System.currentTimeMillis() + 25_000L;
            while (!Files.exists(stopPath) && System.currentTimeMillis() < deadline) Thread.sleep(20);
        }
        System.out.println("GAME_STOPPING");
        System.out.flush();
    }
}
""", new UTF8Encoding(false));

            RunJavac(probeDir, cmdProbeJava);
            RunJavac(gameClasses, gameMainJava);

            using MemoryStream ms = new();
            using (var jar = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
                AddEntry(jar, "META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\nMain-Class: fixture.GameMain\r\n\r\n"));
                AddEntry(jar, "fixture/GameMain.class", File.ReadAllBytes(Path.Combine(gameClasses, "fixture", "GameMain.class")));
            }
            return ms.ToArray();
        }

        private void RunJavac(string outDir, string sourceFile) {
            var start = new ProcessStartInfo(javac) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--release", "17", "-g:none", "-d", outDir, sourceFile }) start.ArgumentList.Add(arg);
            foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
            using var compiler = Process.Start(start) ?? throw new IOException("Could not start javac.");
            var stdout = compiler.StandardOutput.ReadToEndAsync();
            var stderr = compiler.StandardError.ReadToEndAsync();
            if (!compiler.WaitForExit(20000)) { compiler.Kill(true); compiler.WaitForExit(5000); throw new TimeoutException("javac timed out."); }
            Native.Check(Task.WaitAll([stdout, stderr], 5000) && compiler.ExitCode == 0, "javac failed: " + stderr.Result);
        }

        private static Dictionary<string, string> ParseProps(string path) {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8)) {
                int eq = line.IndexOf('=');
                if (eq > 0) result[line[..eq]] = line[(eq + 1)..];
            }
            return result;
        }

        private static bool SamePath(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        private static string Forward(string path) => path.Replace('\\', '/');
        private static object? Value(object item, string name) => item.GetType().GetField(name, Native.Flags) is { } f ? f.GetValue(item) : Native.Property(item.GetType(), name).GetValue(item);
        private static void DrainUi(DateTime deadline) => Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        private static void Until(Func<bool> condition, string what) {
            var timer = Stopwatch.StartNew();
            while (!condition()) {
                if (timer.ElapsedMilliseconds > 25000) throw new TimeoutException("Timed out waiting for: " + what);
                Thread.Sleep(20);
            }
        }
        private static string RequiredJava17() {
            string? path = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA17");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("Required explicit PCLLF_MMC_TEST_JAVA17 executable is missing.");
            return path;
        }
        private sealed class NoNetwork : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new IOException("Phase 2 test permits no metadata HTTP traffic.");
        }
    }

    private static string MainHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location)));

    private static void RunOwnedChild(string group, string receiptFileName) {
        string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N"));
        string nonce = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        Program.ValidateRoot(root);
        Process? child = null; Task<string>? stdout = null, stderr = null; bool stopped = false;
        try {
            string output = Path.Combine(root, "output");
            Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
            var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) {
                UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "--groups", group, ChildSwitch, nonce, "--isolated", "--fixture", root }) start.ArgumentList.Add(arg);
            child = Process.Start(start) ?? throw new IOException("Owned Phase 2 launch child did not start.");
            stdout = child.StandardOutput.ReadToEndAsync();
            stderr = child.StandardError.ReadToEndAsync();
            string receipt = Path.Combine(root, receiptFileName);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(receipt)) {
                if (File.Exists(receipt + ".failed")) throw new IOException("Owned Phase 2 launch child failed: " + File.ReadAllText(receipt + ".failed"));
                if (timer.Elapsed > TimeSpan.FromSeconds(50)) throw new TimeoutException("Owned Phase 2 launch child did not prove completion.");
                if (child.WaitForExit(10)) throw new IOException("Phase 2 launch child exited before its drain receipt: " + child.ExitCode);
            }
            Native.Check(File.ReadAllText(receipt) == string.Join('\n', nonce, child.Id, Native.Full(root), MainHash(), group + "-verified"),
                "Phase 2 launch receipt does not bind nonce/PID/root/Main.");
            child.Kill(true);
            Native.Check(child.WaitForExit(5000), "Owned Phase 2 launch child did not terminate.");
            stopped = true;
            Native.Check(Task.WaitAll([stdout, stderr], 5000) && stderr.Result.Length == 0,
                "Owned Phase 2 launch output did not drain without error: " + stderr.Result);
            Console.Write(stdout.Result);
        } finally {
            if (child is not null) {
                if (!child.HasExited) { child.Kill(true); stopped = child.WaitForExit(5000); }
                child.Dispose();
            }
            if (stdout is not null && stderr is not null && Task.WaitAll([stdout, stderr], 5000) && stderr.Result.Length != 0) {
                Console.Write(stdout.Result); Console.Error.Write(stderr.Result);
            }
            if (stopped) {
                var elapsed = Stopwatch.StartNew();
                while (Directory.Exists(root)) {
                    try { Program.ValidateRoot(root); Directory.Delete(root, true); break; }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && elapsed.ElapsedMilliseconds < 2000) { Thread.Sleep(20); }
                }
            }
        }
    }
}
