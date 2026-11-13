using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host integration; governed by the repository LICENCE.
public static class MmcLaunch {
    private const string WrapperSha256 = "88eb3ee58854e916437831697ef6a2fd493da9bc4ffb018f620cd88f090952aa";
    private static readonly ConcurrentDictionary<Guid, MmcRunningSession> Active = new();
    private static int closeAfterSessions;
    public static bool HasActiveSessions => !Active.IsEmpty;
    // Observes a genuinely registered child; tests may request cancellation here,
    // but cannot replace process creation, the session receipt, or Java pinning.
    internal static Action<Guid, Process> ProcessCreated { get; set; }
    internal static Func<MmcJavaPinStore> JavaPins { get; set; } = () => ModJava.MmcPins;
    internal static Func<MmcMutableRuntimeAuthority> MutableRuntimeAuthorityFactory { get; set; } = () => MmcMutableRuntimeAuthority.Default;
    internal static Func<ModMinecraft.McInstance, MmcLaunchJavaSelection, bool> ConfirmVariantAdoption { get; set; } = (_, selection) =>
        ModBase.RunInUiWait(() => ModMain.MyMsgBox("此实例尚未使用 Java " + selection.RuntimeJava.Major +
            "。是否按整合包声明配置它并启动？", "配置 Java 环境", "配置并启动", "取消") == 1);
    public static bool KeepAliveWhileSessionsFinish() {
        if (Active.IsEmpty) return false;
        Interlocked.Exchange(ref closeAfterSessions, 1);
        return true;
    }

    public static IList<LegacyLaunchStep> CreateSteps(ModMinecraft.McInstance instance, ModLaunch.McLaunchOptions options,
        Action<Java> selectedJava) {
        var context = new Context(instance, options, selectedJava);
        return new List<LegacyLaunchStep> {
            new LegacyLaunchIntStep { Name = "准备 MMC 实例环境", Work = context.Prepare, ProgressWeight = 15, InterruptOnCancel = false },
            new LegacyLaunchIntStep { Name = "执行自定义命令", Work = context.Custom, InterruptOnCancel = false },
            new LegacyLaunchProcessStep { Name = "启动 MMC 游戏", Work = context.Start, ProgressWeight = 2, InterruptOnCancel = false },
            new LegacyLaunchWaitStep { Name = "等待游戏窗口出现", Work = context.Wait, InterruptOnCancel = false }
        };
    }

    private sealed class Context(ModMinecraft.McInstance instance, ModLaunch.McLaunchOptions options, Action<Java> selectedJava) {
        private readonly Guid sessionId = Guid.NewGuid();
        private MmcSavedInstanceSources saved;
        private MmcPreparedInstanceLaunch prepared;
        private MmcLaunchPreferenceSnapshot preferences;
        private MmcLaunchJavaSelection java;
        private MmcLaunchAuthentication authentication;
        private MmcRuntimeLaunchPlan runtime;
        private MmcInstanceLaunchSupport[] support;
        private ModWatcher.Watcher watcher;
        private MmcRunningSession session;
        private MmcJavaPinStore pins;
        private MmcMutableRuntimeAuthority runtimeAuthority;
        private int canceled;
        private readonly TaskCompletionSource monitoringFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MmcRuntimeCommandContext commandContext;
        private bool preLaunchCommandRan;

        public void Prepare(LegacyLaunchTask<int, int> task) => PrepareAsync(task.CreateCancellationToken(), task.CreateSyncProgressProvider()).GetAwaiter().GetResult();
        private async Task PrepareAsync(CancellationToken token, ProgressProvider progress) {
            var operation = ModLaunch.McLaunchLoaderReal;
            if (operation is not null) lock (operation.LockState) {
                operation.HasOnStateChangedThread = true;
                operation.OnStateChangedThread += (_, next, _) => { if (next == ModBase.LoadState.Canceled) CancelGame(); };
                if (operation.State == ModBase.LoadState.Canceled) CancelGame();
            }
            if (options.SaveBatch is not null) throw new InvalidOperationException("MMC 启动参数包含会话绑定信息，当前不能导出普通启动脚本。");
            var paths = MmcInstancePaths.Resolve(instance.PathVersion);
            pins = JavaPins() ?? throw new IOException("MMC Java 保护服务不可用。");
            runtimeAuthority=MutableRuntimeAuthorityFactory()??throw new IOException("MMC 会话临时目录授权不可用。");
            // The package command stages run through the launcher's own process entry, so an
            // operator-visible failure mode is preserved. The injectable boundary itself is left untouched:
            // it stays the single place controlled verification replaces process creation.
            MmcRuntimeCommandExecution.StartProcess = start => ModBase.StartProcess(start);
            // The PreLoad stage runs before this launch loads any instance input, so it sees only the paths
            // the host can already resolve. Its variable surface is therefore the root-only subset; a variable
            // that only the runtime plan can supply is refused at the stage boundary rather than guessed.
            commandContext = new(instance.PathVersion, paths.GameRoot, sessionId, "PCL Living Fork", ModBase.VersionDisplay,
                0, 0);
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            paths = MmcInstancePaths.Resolve(instance.PathVersion, primary.Definition.Layout.GameRootLeaf);
            ReconcilePrevious(paths, primary.Definition.InstanceId, pins, token,runtimeAuthority);
            saved = await MmcSavedSourceStore.LoadAsync(paths, primary.Definition, token).ConfigureAwait(false);
            preferences = MmcLaunchPreferences.Capture(instance, saved.PackageConfiguration, sessionId);
            // The active package configuration is the stage source; the runtime plan does not exist yet, so
            // the stage still reads its command text from here and expands only the frozen root variables.
            commandContext = commandContext with {
                PackageConfiguration = saved.PackageConfiguration,
                LauncherName = preferences.RuntimeSettings.LauncherName,
                LauncherVersion = preferences.RuntimeSettings.LauncherVersion,
                ResolutionWidth = preferences.RuntimeSettings.ResolutionWidth,
                ResolutionHeight = preferences.RuntimeSettings.ResolutionHeight
            };
            // The existing login step is non-blocking. Bind only its completed
            // result, never a previous account result while refresh is in flight.
            var loginTask = ModLaunch.McLoginLoader;
            while (loginTask.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading)
                await Task.Delay(10, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (loginTask.State == ModBase.LoadState.Canceled) throw new OperationCanceledException(token);
            if (loginTask.State != ModBase.LoadState.Finished || string.IsNullOrEmpty(loginTask.Output.Type))
                throw new IOException("现有账户登录尚未成功完成，无法绑定 MMC 启动身份。");
            authentication = MmcLaunchAuthentication.Capture(instance, ModLaunch.McLoginLoader.Output.Type);
            java = await MmcLaunchJava.SelectAsync(instance, primary.Definition, saved.Resolved.Resolution.Profile,
                preferences.JavaPreference, token, authentication.JavaConstraints, minimumVersion: authentication.MinimumJavaVersion).ConfigureAwait(false);
            selectedJava(java.Java);
            ModLaunch.McLaunchLog("MMC 本次使用 Java " + java.Java.Version + "：" + java.Java.JavaExePath);
            string gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(paths.InstanceRoot))!;
            string cache = Path.Combine(gameRoot, "PCL", "MmcCache");
            var backend = new MmcInstanceBackend<ModNet.NetFile>(MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
                new(Path.Combine(gameRoot, "libraries"), Path.Combine(gameRoot, "assets"), Path.Combine(cache, "sources"), Path.Combine(gameRoot, "versions")),
                MmcDownloads.BuildFile, (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, progress));
            if (java.RequiresVariantAdoption) {
                if (!ConfirmVariantAdoption(instance, java)) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested(); MmcLaunchPreferences.Revalidate(instance, preferences);
                var adopted = await backend.AdoptVariantAsync(new(paths.InstanceRoot, java.Environment, primary.Definition.InstanceId,
                    primary.Definition.Generation, primary.Sha256), token).ConfigureAwait(false);
                if (adopted.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                if (adopted.Status != MmcInstanceBackendStatus.Succeeded || adopted.Definition is null)
                    throw new IOException("Java 环境未完成配置：" + adopted.Failure);
                primary = MmcSavedSourceStore.ReadPrimary(paths);
            }
            // Package stage contract §1.2: PreLoadCommand must run after host preparation (paths, sources,
            // preference capture, login wait, Java selection) and after any optional variant adoption, but
            // strictly before the first launch-source progress point that loads this launch's runtime inputs.
            RunPackageCommand(MmcRuntimeCommandStage.PreLoad, null, token);
            var result = await backend.PrepareLaunchAsync(new(paths.InstanceRoot, java.Environment, primary.Definition.InstanceId,
                primary.Definition.Generation), token).ConfigureAwait(false);
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
            prepared = result.Snapshot ?? throw new IOException("MMC 环境准备未完成：" + result.Failure);
            instance.Load();
            support = preferences.RuntimeSettings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper
                ? (await MmcInstanceBackend<ModNet.NetFile>.PrepareLaunchSupportAsync(prepared,
                    new[] { new MmcInstanceLaunchSupportInput("JavaWrapper.jar", WrapperBytes(), WrapperSha256),
                        new MmcInstanceLaunchSupportInput("MmcExactBridge.jar", MmcRuntimeBridgeTransport.CreateJarBytes(), MmcRuntimeBridgeTransport.JarSha256()) }, token).ConfigureAwait(false)).ToArray()
                : [];
            await authentication.PrepareAsync(prepared, java.Java, progress, token).ConfigureAwait(false);
            WorldBackups.RecoverBeforeLaunch(prepared.Paths.GameRoot, token);
            try { options.PersonalOptions = PersonalGameSettings.Prepare(instance); }
            catch (Exception error) { Logger.Warn(error, "MMC 个人游戏设置同步未完成，保留现有配置"); }
            options.QuickPlay = QuickPlayWorlds.Prepare(instance, options.QuickPlayWorld, options.ExtraGameArgs, false);
            token.ThrowIfCancellationRequested();
        }

        public void Custom(LegacyLaunchTask<int, int> task) {
            foreach (var command in preferences.CustomCommands) {
                CancellationToken token = task.CreateCancellationToken(); token.ThrowIfCancellationRequested();
                // Commands remain explicit player preferences. Package commands unsupported by the
                // format profile were rejected at import; never infer or execute an unknown command.
                string text = ModMain.ArgumentReplace(command.Command);
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = prepared.Paths.GameRoot };
                start.ArgumentList.Add("/d"); start.ArgumentList.Add("/s"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(text);
                using var process = Process.Start(start) ?? throw new IOException("无法启动实例自定义命令。");
                try {
                    if (command.WaitForExit) while (!process.HasExited) { token.ThrowIfCancellationRequested(); Thread.Sleep(20); }
                } finally { if (token.IsCancellationRequested && !process.HasExited) process.Kill(entireProcessTree: true); }
            }
        }

        public void Start(LegacyLaunchTask<int, Process> task) {
            CancellationToken token = task.CreateCancellationToken(); token.ThrowIfCancellationRequested();
            MmcAdmissionLease admission = null;
            var login = ModLaunch.McLoginLoader.Output;
            authentication.Revalidate(instance, login.Type, java.Java);
            var account = new MmcRuntimeAccountSnapshot(login.Name, login.Uuid, login.AccessToken,
                "token:" + login.AccessToken + ":" + login.Uuid, login.Type == "Microsoft" ? "msa" : "legacy", "{}", login.ClientToken,
                javaConstraints: authentication.JavaConstraints);
            var settings = preferences.RuntimeSettings with {
                ExtraGameTokens = preferences.RuntimeSettings.ExtraGameTokens.Concat(options.ExtraGameArgs).ToImmutableArray()
            };
            var request = new MmcRuntimeLaunchRequest(prepared.Definition, prepared.BoundPlan, prepared.Paths,
                prepared.Targets.Select(value => new MmcRuntimeArtifactTarget(value.ArtifactKey, value.TargetRelativePath, value.ActualPath)).ToImmutableArray(),
                java.RuntimeJava, settings, account,
                new(prepared.Directories.ViewKey, prepared.Directories.NativeDirectory, prepared.Directories.AssetsRoot, prepared.Directories.GameAssetsRoot),
                Support("JavaWrapper.jar"), Support("MmcExactBridge.jar")){RuntimeAuthority=runtimeAuthority, Authentication=authentication.Binding};
            try {
                // The named admission mutex is thread-affine. Block only this worker while the
                // pure runtime reader finishes, retaining the same thread through Process.Start.
                admission = MmcAdmissionGate.Acquire(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, TimeSpan.Zero, token);
                MmcInstanceBackend<ModNet.NetFile>.ValidatePreparedLaunch(admission, prepared);
                MmcLaunchPreferences.Revalidate(instance, preferences);
                authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output.Type, java.Java);
                runtime = MmcRuntimeLaunchBuilder.BuildAsync(request, token).GetAwaiter().GetResult();
                var classes = MmcClasspathInspector.ObserveAsync(runtime, token).GetAwaiter().GetResult();
                string wrapperHash = Support("JavaWrapper.jar")?.Sha256 ?? Convert.ToHexStringLower(SHA256.HashData("PCLLF:direct-main:v1"u8));
                var authority = MmcLaunchAuthorityEvaluator.Evaluate(prepared.Definition, prepared.BoundPlan, saved.Resolved.Resolution.Profile,
                    new(java.RuntimeJava.IdentitySha256, wrapperHash, runtime.NonSensitiveArgumentFingerprint) {
                        AdditionalJvmArguments = runtime.NonSensitiveAdditionalJvmArguments,
                        AdditionalRequiredClasses = runtime.RequiredClassNames
                    }, classes.CoreObservations);
                if (authority.Diagnostics.Any(value => value.Severity == MmcDiagnosticSeverity.Error))
                    throw new IOException("MMC 启动链校验失败：" + string.Join("；", authority.Diagnostics.Select(value => value.Message)));
                var retainedJava = ModJava.GetManagedJavaRequirement(instance, authority, login.Type == "Nide");
                if (!retainedJava.Range.Contains(java.Java.Version))
                    throw new IOException("选定的 Java 不满足尚未被完整启动链证明替代的约束：" + retainedJava.Range +
                        string.Concat(authority.Diagnostics.Where(value => value.Severity == MmcDiagnosticSeverity.Warning).Select(value => "；" + value.Message)));
                if (authority.HasAuthority) ModLaunch.McLaunchLog("MMC 自定义启动链已核对：" + string.Join("、", authority.Proof.Targets.Select(value => value.ComponentUid).Distinct()));
                token.ThrowIfCancellationRequested();
                // The PreLaunch stage runs outside the admission gate (contract D2): an external command must
                // never be executed while holding it. On cancellation the lease is released by Dispose, and
                // the catch below retires this session's freshly owned mutable directory.
                admission.Dispose();
                RunPackageCommand(MmcRuntimeCommandStage.PreLaunch, runtime, token);
                // A package command may legitimately change an input byte, so the fingerprint must be
                // recomputed from the re-observed inputs. Reusing the pre-command value would let a changed
                // artifact reach the process under a stale receipt. The hot-plan path takes this same route.
                if (preLaunchCommandRan) {
                    MmcRuntimeLaunchBuilder.RevalidateFilesAsync(runtime, token).GetAwaiter().GetResult();
                    MmcClasspathInspector.RevalidateManifestInputs(runtime, classes, token);
                    string confirmed = MmcRuntimeLaunchBuilder.ConfirmInputFingerprint(runtime);
                    if (confirmed != runtime.RuntimeInputFingerprint)
                        throw new MmcRuntimeLaunchException("MMC_RUNTIME_INPUT_CHANGED",
                            "包内命令改变了本次启动的输入，必须显式修复或采用新变体后重新准备启动。");
                }
                // Re-acquire the gate for the continuous final validation → CreateProcess → Running
                // registration window, which the contract requires to hold one uninterrupted lease.
                admission = MmcAdmissionGate.Acquire(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, TimeSpan.Zero, token);
                MmcInstanceBackend<ModNet.NetFile>.ValidatePreparedLaunch(admission, prepared);
                MmcLaunchPreferences.Revalidate(instance, preferences);
                authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output.Type, java.Java);
                session = MmcSessions.Launch(admission, new(prepared.Definition.InstanceId, prepared.Definition.Generation, runtime.RuntimeInputFingerprint,
                    MmcSessions.ObserveExecutable(java.Java.JavaExePath), MmcSessions.ObserveExecutable(Environment.ProcessPath!), runtime.CreateStartInfo(), prepared.Paths.GameRootLeaf) {
                        SessionId = sessionId, RuntimeBinding = runtime.RuntimeBinding,RuntimeAuthority=runtimeAuthority
                    }, () => {
                        token.ThrowIfCancellationRequested();
                        MmcInstanceBackend<ModNet.NetFile>.ValidatePreparedLaunch(admission, prepared);
                        MmcLaunchPreferences.Revalidate(instance, preferences);
                        authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output.Type, java.Java);
                        MmcRuntimeLaunchBuilder.RevalidateFilesAsync(runtime, token).GetAwaiter().GetResult();
                        MmcClasspathInspector.RevalidateManifestInputs(runtime, classes, token);
                    }, (id, identity) => pins.Acquire(id, identity, prepared.Definition.InstanceId, prepared.Paths.InstanceRoot, token));
            } catch {
                // A durable Launching intent may already represent a live process. Abandon only
                // when the session service confirms no active or uncertain record exists.
                if (runtime is not null && session is null) {
                    try {
                        using var cleanup = MmcAdmissionGate.Acquire(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, TimeSpan.Zero);
                        MmcSessionRuntimeDirectories.AbandonUnlaunched(cleanup, sessionId, runtime.RuntimeBinding,runtimeAuthority);
                    } catch (Exception error) { Logger.Warn(error, "MMC 启动临时目录保留，等待会话恢复确认"); }
                }
                throw;
            } finally { admission?.Dispose(); }
            Active.TryAdd(sessionId, session);
            _ = MonitorExitAsync(session, prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, monitoringFinished.Task, runtime, commandContext with { RedactionSecrets = RedactionSecrets(runtime) });
            ModLaunch.McLaunchProcess = session.Process;
            try {
                ProcessCreated?.Invoke(sessionId, session.Process);
                var watchTask = new ModLoader.LoaderTask<Process, int>("MMC 窗口监控", _ => { }) { Input = session.Process };
                watcher = new ModWatcher.Watcher(watchTask, instance, "", game => {
                    TryOptional(() => WorldBackups.Track(instance, game), "记录 MMC 存档备份会话失败");
                    TryOptional(() => PersonalGameSettings.Track((PersonalOptionsSession)options.PersonalOptions, game), "记录 MMC 个人设置会话失败");
                    TryOptional(() => QuickPlayWorlds.Track((QuickPlaySession)options.QuickPlay, game), "记录 MMC Quick Play 会话失败");
                }, () => {
                    TryOptional(() => PersonalGameSettings.Finish((PersonalOptionsSession)options.PersonalOptions), "保存 MMC 个人游戏设置失败");
                    TryOptional(() => QuickPlayWorlds.Finish((QuickPlaySession)options.QuickPlay), "保存 MMC 最近存档记录失败");
                }, LogRedactor: runtime.Redact, WindowTypeOverride: preferences.MaximizeWindow ? 4 : 0,
                    OnMonitoringFinished: () => monitoringFinished.TrySetResult(), RequireVerifiedExit: true);
                ModLaunch.McLaunchWatcher = watcher;
                if ((task.IsCanceled || Volatile.Read(ref canceled) != 0) && !session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
                task.Output = session.Process;
                ModLaunch.McLaunchLog("已启动 MMC 受管理游戏会话：" + sessionId.ToString("N"));
            } catch {
                if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
                monitoringFinished.TrySetResult();
                throw;
            }
        }
        private void CancelGame() {
            Interlocked.Exchange(ref canceled, 1);
            _ = Task.Run(() => {
                try { if (session is not null && !session.Process.HasExited) session.Process.Kill(entireProcessTree: true); }
                catch (Exception error) { Logger.Warn(error, "取消 MMC 游戏启动时保留会话监视器等待退出"); }
            });
        }

        public void Wait(LegacyLaunchTask<Process, int> task) {
            while (watcher.State == ModWatcher.Watcher.MinecraftState.Loading) {
                if (task.IsCanceled && !session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
                Thread.Sleep(100);
            }
            if (watcher.State == ModWatcher.Watcher.MinecraftState.Canceled) throw new OperationCanceledException();
            if (watcher.State == ModWatcher.Watcher.MinecraftState.Crashed) {
                string detail = "Java " + java.Java.Version;
                bool exited = session.Process.HasExited;
                if (exited) {
                    int code = session.Process.ExitCode;
                    detail += "，退出码 " + code + "（0x" + unchecked((uint)code).ToString("X8") + "）";
                }
                throw new IOException((exited ? "MMC 游戏在启动完成前退出" : "MMC 游戏报告启动失败") + "（" + detail + "）。\n游戏日志：" +
                    Path.Combine(prepared.Paths.GameRoot, "logs"));
            }
        }
        /// <summary>
        /// Runs one package command stage from the active configuration. Sources that are inactive
        /// (<c>OverrideCommands=false</c>) keep their value and execute nothing, and a stage with no command
        /// is skipped entirely; the log line carries only the redacted command text.
        /// </summary>
        private void RunPackageCommand(MmcRuntimeCommandStage stage, MmcRuntimeLaunchPlan plan, CancellationToken token) {
            string command = MmcRuntimeCommandExecution.Read(commandContext, stage);
            if (command is null) return;
            var context = plan is null ? commandContext : commandContext with { RedactionSecrets = RedactionSecrets(plan) };
            var variables = plan is null ? null : MmcRuntimeLaunchBuilder.CommandVariables(plan);
            MmcRuntimeCommandExecution.Execute(stage, context, variables, command, text => ModLaunch.McLaunchLog(text), token);
            if (stage == MmcRuntimeCommandStage.PreLaunch) preLaunchCommandRan = true;
        }

        /// <summary>
        /// The live secret surface used to redact a command log line. It is projected read-only from the
        /// runtime plan; a command never expands these values, only removes them from what it logs.
        /// </summary>
        private static System.Collections.Immutable.ImmutableArray<string> RedactionSecrets(MmcRuntimeLaunchPlan plan) {
            var account = plan.Request.Account;
            return new[] { account.AccessToken, account.Session, account.UserPropertiesJson, account.ClientToken, account.Xuid }
                .OfType<string>().Where(value => value.Length != 0).ToImmutableArray();
        }
        private MmcRuntimeSupportArtifact Support(string name) {
            var value = support.SingleOrDefault(value => value.FileName == name);
            return value is null ? null : new(value.ActualPath, value.Sha256, value.SizeBytes);
        }
    }
    private static void TryOptional(Action action, string message) { try { action(); } catch (Exception error) { Logger.Warn(error, message); } }

    private static byte[] WrapperBytes() {
        using var input = typeof(MmcRuntimeLaunchBuilder).Assembly.GetManifestResourceStream("Launch\\JavaWrapper.jar")
            ?? throw new IOException("MMC JavaWrapper 资源缺失。");
        if (input.Length > 4 * 1024 * 1024) throw new IOException("MMC JavaWrapper 资源超过预算。");
        using MemoryStream output = new(); input.CopyTo(output); byte[] bytes = output.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != WrapperSha256) throw new IOException("MMC JavaWrapper 资源摘要不符。");
        return bytes;
    }
    private static void ReconcilePrevious(MmcInstancePaths paths, Guid instanceId, MmcJavaPinStore pins, CancellationToken token,MmcMutableRuntimeAuthority runtimeAuthority) {
        using var admission = MmcAdmissionGate.Acquire(paths.InstanceRoot, instanceId, TimeSpan.Zero, token);
        MmcFileTransactions.AssertNoPending(admission);
        string directory = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions");
        if (!Directory.Exists(directory)) return;
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(1025).ToArray();
        if (entries.Length > 1024) throw new IOException("MMC 会话记录超过恢复预算。");
        foreach (string entry in entries) {
            if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid id)) throw new IOException("MMC 存在无法识别的会话记录，需要恢复确认。");
            var result = MmcSessions.Reconcile(admission, id, new MmcSessionProcessInspector(), pins.ReleaseVerified,runtimeAuthority);
            if (result.Observation != MmcSessionObservation.ProvenExited)
                throw new IOException("此 MMC 实例仍在运行或会话尚待确认：" + result.Diagnostic);
        }
    }
    private static async Task MonitorExitAsync(MmcRunningSession session, string root, Guid instanceId, Task monitoringFinished,
        MmcRuntimeLaunchPlan runtime, MmcRuntimeCommandContext commandContext) {
        bool reported = false, postExitDone = false;
        while (true) {
            try {
                await session.Process.WaitForExitAsync().ConfigureAwait(false);
                using (var admission = MmcAdmissionGate.Acquire(root, instanceId, TimeSpan.FromSeconds(10))) session.CompleteVerifiedExit(admission);
                // PostExit runs after the durable exit receipt and before session cleanup. It is executed
                // once: the surrounding loop re-runs only the receipt/settlement path after a failure.
                if (!postExitDone) {
                    postExitDone = true;
                    string? command = MmcRuntimeCommandExecution.Read(commandContext, MmcRuntimeCommandStage.PostExit);
                    if (command is not null) try {
                        // A PostExit failure is a diagnostic only. It neither rolls back the finished session
                        // nor rewrites its exit receipt, so it can never turn a clean exit into a failed one.
                        MmcRuntimeCommandExecution.Execute(MmcRuntimeCommandStage.PostExit, commandContext, MmcRuntimeLaunchBuilder.CommandVariables(runtime), command, text => ModLaunch.McLaunchLog(text));
                    } catch (Exception error) {
                        Logger.Warn(error, "MMC 包内命令（PostExitCommand）失败，已结束的会话保持不变");
                    }
                }
                await monitoringFinished.ConfigureAwait(false);
                Active.TryRemove(session.Receipt.SessionId, out _);
                if (Active.IsEmpty && Interlocked.Exchange(ref closeAfterSessions, 0) != 0)
                    ModBase.RunInUi(() => ModMain.FrmMain?.EndProgram(false));
                // Watcher retains the shared Process/log readers for crash UI and stop callbacks.
                return;
            } catch (Exception error) {
                if (!reported) { Logger.Warn(error, "MMC 游戏会话退出尚未完成确认，保留 Java pin 和恢复记录并等待重试"); reported = true; }
                await Task.Delay(3000).ConfigureAwait(false);
            }
        }
    }
}
