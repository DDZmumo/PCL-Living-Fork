using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    internal static Func<ModMinecraft.McInstance, MmcPackageCommandSummary, bool> ConfirmPackageCommands { get; set; } = (target, summary) =>
        ModBase.RunInUiWait(() => ModMain.MyMsgBox(
            "整合包“" + target.Name + "”配置了以下附加包内启动或包装命令，继续执行前请确认仅在信任该整合包来源时继续：\n\n" +
            string.Join("\n", summary.Entries.Select(entry => entry.Stage + ": " + entry.RedactedCommand)),
            "确认执行整合包命令", "信任并继续", "取消") == 1);
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

    private static IProgress<MmcInstanceBackendProgress> CreateBackendProgress(ProgressProvider prepInitProgress,
        ProgressProvider prepDownloadProgress, ProgressProvider prepWorkProgress) => new BackendProgressAdapter(prepInitProgress, prepDownloadProgress, prepWorkProgress);

    private sealed class BackendProgressAdapter(ProgressProvider prepInitProgress, ProgressProvider prepDownloadProgress, ProgressProvider prepWorkProgress) : IProgress<MmcInstanceBackendProgress> {
        private readonly object sync = new();
        private readonly System.Collections.Generic.HashSet<string> loggedPhases = new(StringComparer.Ordinal);
        private double maxInit;
        private double maxWork;

        public void Report(MmcInstanceBackendProgress value) {
            if (string.IsNullOrEmpty(value.Phase)) return;
            bool shouldLog = false;
            lock (sync) {
                switch (value.Phase) {
                    case "preparing-existing-launch": Advance(prepInitProgress, ref maxInit, 0.15); break;
                    case "launch-sources-start": Advance(prepInitProgress, ref maxInit, 0.25); shouldLog = loggedPhases.Add(value.Phase); break;
                    case "launch-sources-complete": Advance(prepInitProgress, ref maxInit, 0.60); break;
                    case "launch-compile-start": Advance(prepInitProgress, ref maxInit, 0.75); break;
                    case "launch-compile-complete": Advance(prepInitProgress, ref maxInit, 0.95); break;
                    case "launch-prefetch-start":
                        Advance(prepInitProgress, ref maxInit, 1.0);
                        shouldLog = loggedPhases.Add(value.Phase);
                        break;
                    case "launch-prefetch-complete":
                        Advance(prepInitProgress, ref maxInit, 1.0);
                        prepDownloadProgress?.Set(1);
                        break;
                    case "launch-artifact-scope-start":
                        Advance(prepInitProgress, ref maxInit, 1.0);
                        prepDownloadProgress?.Set(1);
                        Advance(prepWorkProgress, ref maxWork, 0.05);
                        shouldLog = loggedPhases.Add(value.Phase);
                        break;
                    case "runtime-view-materialization": Advance(prepWorkProgress, ref maxWork, 0.18); break;
                    case "asset-index-preparation": Advance(prepWorkProgress, ref maxWork, 0.28); break;
                    case "asset-object-preparation": Advance(prepWorkProgress, ref maxWork, 0.45); break;
                    case "asset-view-materialization": Advance(prepWorkProgress, ref maxWork, 0.65); break;
                    case "launch-preserved-check-start": Advance(prepWorkProgress, ref maxWork, 0.72); break;
                    case "launch-preserved-check-complete": Advance(prepWorkProgress, ref maxWork, 0.78); break;
                    case "launch-bind-start": Advance(prepWorkProgress, ref maxWork, 0.82); break;
                    case "launch-bind-complete": Advance(prepWorkProgress, ref maxWork, 0.88); break;
                    case "launch-final-validation-start": Advance(prepWorkProgress, ref maxWork, 0.90); shouldLog = loggedPhases.Add(value.Phase); break;
                    case "launch-final-validation-complete": Advance(prepWorkProgress, ref maxWork, 0.97); break;
                    case "launch-inputs-ready":
                    case "completed":
                        Advance(prepInitProgress, ref maxInit, 1.0);
                        prepDownloadProgress?.Set(1);
                        Advance(prepWorkProgress, ref maxWork, 1.0);
                        break;
                }
            }
            if (shouldLog) {
                string message = value.Phase switch {
                    "launch-sources-start" => "MMC 环境准备：读取实例源码与配置",
                    "launch-prefetch-start" => "MMC 环境准备：校验并补齐运行库与资源索引",
                    "launch-artifact-scope-start" => "MMC 环境准备：构建运行目录与资源视图",
                    "launch-final-validation-start" => "MMC 环境准备：执行启动前完整性终检",
                    _ => null
                };
                if (message is not null) ModLaunch.McLaunchLog(message);
            }
        }

        private static void Advance(ProgressProvider target, ref double maxValue, double next) {
            if (next <= maxValue) return;
            maxValue = next;
            target?.Set(next);
        }
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
        private volatile ModWatcher.Watcher watcher;
        private MmcRunningSession session;
        private MmcJavaPinStore pins;
        private MmcMutableRuntimeAuthority runtimeAuthority;
        private int canceled;
        private int cancelSignaled;
        private readonly TaskCompletionSource monitoringFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MmcRuntimeCommandContext commandContext;
        private Guid instanceId;
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
            var progressSlices = progress?.SplitBy(0.15, 0.70, 0.15);
            var initProgress = progressSlices?[0];
            var prepareProgress = progressSlices?[1];
            var tailProgress = progressSlices?[2];
            var initSlices = initProgress?.SplitBy(0.55, 0.45);
            var initLoadProgress = initSlices?[0];
            var initJavaProgress = initSlices?[1];
            var prepSlices = prepareProgress?.SplitBy(0.15, 0.35, 0.50);
            var prepInitProgress = prepSlices?[0];
            var prepDownloadProgress = prepSlices?[1];
            var prepWorkProgress = prepSlices?[2];
            ProgressProvider activeDownloadProgress = null;
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            instanceId = primary.Definition.InstanceId;
            paths = MmcInstancePaths.Resolve(instance.PathVersion, primary.Definition.Layout.GameRootLeaf);
            ReconcilePrevious(paths, instanceId, pins, token,runtimeAuthority);
            saved = await MmcSavedSourceStore.LoadAsync(paths, primary.Definition, token).ConfigureAwait(false);
            initLoadProgress?.Set(0.75);
            preferences = MmcLaunchPreferences.Capture(instance, saved.PackageConfiguration, sessionId);
            // Build commandContext only after resolving the persisted GameRootLeaf layout so all package stages
            // (PreLoad, PreLaunch, PostExit) receive the active GameRoot instead of pre-definition default paths.
            commandContext = new(paths.InstanceRoot, paths.GameRoot, sessionId,
                preferences.RuntimeSettings.LauncherName, preferences.RuntimeSettings.LauncherVersion,
                preferences.RuntimeSettings.ResolutionWidth, preferences.RuntimeSettings.ResolutionHeight) {
                PackageConfiguration = saved.PackageConfiguration
            };
            initLoadProgress?.Set(1);
            // The existing login step is non-blocking. Bind only its completed
            // result, never a previous account result while refresh is in flight.
            var loginTask = ModLaunch.McLoginLoader;
            while (loginTask.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading)
                await Task.Delay(10, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (loginTask.State == ModBase.LoadState.Canceled) throw new OperationCanceledException(token);
            if (loginTask.State != ModBase.LoadState.Finished || string.IsNullOrEmpty(loginTask.Output.Type))
                throw new IOException("现有账户登录尚未成功完成，无法绑定 MMC 启动身份。");
            authentication = MmcLaunchAuthentication.Capture(instance, loginTask.Output);
            java = await MmcLaunchJava.SelectAsync(instance, primary.Definition, saved.Resolved.Resolution.Profile,
                preferences.JavaPreference, token, authentication.JavaConstraints, minimumVersion: authentication.MinimumJavaVersion).ConfigureAwait(false);
            initJavaProgress?.Set(0.5);
            selectedJava(java.Java);
            ModLaunch.McLaunchLog("MMC 本次使用 Java " + java.Java.Version + "：" + java.Java.JavaExePath);
            string gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(paths.InstanceRoot))!;
            string cache = Path.Combine(gameRoot, "PCL", "MmcCache");
            var backend = new MmcInstanceBackend<ModNet.NetFile>(MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
                new(Path.Combine(gameRoot, "libraries"), Path.Combine(gameRoot, "assets"), Path.Combine(cache, "sources"), Path.Combine(gameRoot, "versions")),
                MmcDownloads.BuildFile, (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, activeDownloadProgress));
            if (java.RequiresVariantAdoption) {
                if (!ConfirmVariantAdoption(instance, java)) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested(); MmcLaunchPreferences.Revalidate(instance, preferences);
                activeDownloadProgress = initJavaProgress?.SplitTo(0.95);
                var adopted = await backend.AdoptVariantAsync(new(paths.InstanceRoot, java.Environment, primary.Definition.InstanceId,
                    primary.Definition.Generation, primary.Sha256), token).ConfigureAwait(false);
                if (adopted.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                if (adopted.Status != MmcInstanceBackendStatus.Succeeded || adopted.Definition is null)
                    throw new IOException("Java 环境未完成配置：" + adopted.Failure);
                primary = MmcSavedSourceStore.ReadPrimary(paths);
                instanceId = primary.Definition.InstanceId;
                paths = MmcInstancePaths.Resolve(instance.PathVersion, primary.Definition.Layout.GameRootLeaf);
                commandContext = commandContext with {
                    InstanceRoot = paths.InstanceRoot,
                    GameRoot = paths.GameRoot
                };
            }
            initJavaProgress?.Set(1);
            initProgress?.Set(1);
            // Package stage contract §1.2 & D13: fail-closed confirmation of active redacted package
            // commands precedes the first package stage execution, and PreLoad runs without package Env.
            EnsurePackageCommandsConfirmed(instance, saved.PackageConfiguration, CommandRedactionSecrets(), token);
            EnsureGameRootDirectory(paths, primary.Definition.InstanceId, token);
            RunPackageCommand(MmcRuntimeCommandStage.PreLoad, null, token);
            activeDownloadProgress = prepDownloadProgress;
            var backendProgress = CreateBackendProgress(prepInitProgress, prepDownloadProgress, prepWorkProgress);
            var result = await backend.PrepareLaunchAsync(new(paths.InstanceRoot, java.Environment, primary.Definition.InstanceId,
                primary.Definition.Generation) {
                    PreloadedSources = java.RequiresVariantAdoption ? null : saved,
                    PreloadedSourcesPrimarySha256 = java.RequiresVariantAdoption ? null : primary.Sha256
                }, token, backendProgress).ConfigureAwait(false);
            if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
            prepared = result.Snapshot ?? throw new IOException("MMC 环境准备未完成：" + result.Failure);
            prepareProgress?.Set(1);
            instance.Load();
            if (preferences.RuntimeSettings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper) {
                byte[] bridge = MmcRuntimeBridgeTransport.CreateJarBytes();
                support = (await MmcInstanceBackend<ModNet.NetFile>.PrepareLaunchSupportAsync(prepared,
                    new[] { new MmcInstanceLaunchSupportInput("JavaWrapper.jar", WrapperBytes(), WrapperSha256),
                        new MmcInstanceLaunchSupportInput("MmcExactBridge.jar", bridge, Convert.ToHexStringLower(SHA256.HashData(bridge))) }, token).ConfigureAwait(false)).ToArray();
            } else support = [];
            var authProgress = tailProgress?.SplitTo(0.65);
            await authentication.PrepareAsync(prepared, java.Java, authProgress, token).ConfigureAwait(false);
            tailProgress?.Set(0.7);
            WorldBackups.RecoverBeforeLaunch(prepared.Paths.GameRoot, token);
            try {
                PersonalOptionsFiles.ApplyDefaultLanguage(prepared.Paths.GameRoot, Settings.Get<bool>("ToolHelpChinese"),
                    instance.Version, ModBase.IniClearCache,
                    () => (int?)instance.JsonVersion?["world_version"]);
            } catch (Exception error) { Logger.Warn(error, "MMC 游戏语言设置未完成"); }
            try { options.PersonalOptions = PersonalGameSettings.Prepare(instance); }
            catch (Exception error) { Logger.Warn(error, "MMC 个人游戏设置同步未完成，保留现有配置"); }
            options.QuickPlay = QuickPlayWorlds.Prepare(instance, options.QuickPlayWorld, options.ExtraGameArgs, false);
            tailProgress?.Set(1);
            progress?.Set(1);
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
            QuiescedAdmission admission = null;
            var login = ModLaunch.McLoginLoader.Output;
            authentication.Revalidate(instance, login, java.Java);
            using var localSkin = authentication.OpenLocalSession(token);
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
                admission = AcquireQuiescedAdmission(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, token);
                MmcInstanceBackend<ModNet.NetFile>.ValidatePreparedLaunch(admission.Lease, prepared);
                MmcLaunchPreferences.Revalidate(instance, preferences);
                authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output, java.Java);
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
                admission = null;
                RunPackageCommand(MmcRuntimeCommandStage.PreLaunch, runtime, token);
                bool wrapperManaged = runtime.Request.Settings.WrapperCommand is not null;
                if (wrapperManaged) EnsurePackageCommandsConfirmed(instance, preferences.PackageConfiguration, CommandRedactionSecrets(), token);
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
                admission = AcquireQuiescedAdmission(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, token);
                MmcLaunchPreferences.Revalidate(instance, preferences);
                authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output, java.Java);
                ProcessStartInfo startInfo = runtime.CreateStartInfo(out int nonceArgumentIndex);
                MmcExecutableIdentity wrapperIdentity = wrapperManaged
                    ? MmcSessions.ObserveExecutable(MmcSessions.ResolveExecutablePath(startInfo.FileName, startInfo.WorkingDirectory))
                    : null;
                session = MmcSessions.Launch(admission.Lease, new(prepared.Definition.InstanceId, prepared.Definition.Generation, runtime.RuntimeInputFingerprint,
                    MmcSessions.ObserveExecutable(java.Java.JavaExePath), MmcSessions.ObserveExecutable(Environment.ProcessPath!), startInfo, prepared.Paths.GameRootLeaf) {
                        SessionId = sessionId, RuntimeBinding = runtime.RuntimeBinding, RuntimeAuthority = runtimeAuthority,
                        IsWrapperManaged = wrapperManaged, WrapperIdentity = wrapperIdentity, NonceArgumentIndex = nonceArgumentIndex
                    }, () => {
                        token.ThrowIfCancellationRequested();
                        MmcLaunchPreferences.Revalidate(instance, preferences);
                        authentication.Revalidate(instance, ModLaunch.McLoginLoader.Output, java.Java);
                        MmcRuntimeLaunchBuilder.ValidateFinalInputs(admission.Lease, prepared, runtime, token);
                        MmcClasspathInspector.RevalidateManifestInputs(runtime, classes, token);
                    }, (id, identity) => pins.Acquire(id, identity, prepared.Definition.InstanceId, prepared.Paths.InstanceRoot, token));
            } catch {
                // A durable Launching intent may already represent a live process. Abandon only
                // when the session service confirms no active or uncertain record exists (or a
                // failed owned wrapper tree already wrote its Exited.record).
                admission?.Dispose();
                admission = null;
                if (runtime is not null && session is null) {
                    try {
                        using var cleanup = AcquireQuiescedAdmission(prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, CancellationToken.None);
                        var cmdStatus = MmcCommandOperations.ReconcileForSession(cleanup.Lease, sessionId, runtimeAuthority);
                        if (!cmdStatus.Clear)
                            throw new IOException("MMC 包内命令进程组尚待恢复确认：" + cmdStatus.Diagnostic);
                        string sessionDir = prepared.Paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions/" + sessionId.ToString("N"));
                        if (Directory.Exists(sessionDir) || File.Exists(sessionDir)) {
                            var reconciled = MmcSessions.Reconcile(cleanup.Lease, sessionId,
                                new MmcSessionProcessInspector(MmcSessionProcessInspector.TryProcessCommandLineNonce),
                                pins.ReleaseVerified, runtimeAuthority);
                            if (reconciled.Observation != MmcSessionObservation.ProvenExited)
                                throw new IOException("MMC 失败会话尚待恢复确认：" + reconciled.Diagnostic);
                        } else {
                            MmcSessionRuntimeDirectories.AbandonUnlaunched(cleanup.Lease, sessionId, runtime.RuntimeBinding, runtimeAuthority);
                        }
                    } catch (Exception error) { Logger.Warn(error, "MMC 启动临时目录保留，等待会话恢复确认"); }
                }
                throw;
            } finally { admission?.Dispose(); }
            Active.TryAdd(sessionId, session);
            _ = MonitorExitAsync(session, instance, prepared.Paths.InstanceRoot, prepared.Definition.InstanceId, monitoringFinished.Task, runtime,
                commandContext with {
                    RedactionSecrets = RedactionSecrets(runtime),
                    TemporaryDirectory = runtime.MutableDirectories.TemporaryDirectory
                }, CommandRedactionSecrets());
            ModLaunch.McLaunchProcess = session.Process;
            try {
                localSkin?.Attach(session.Process, token);
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
                    OnMonitoringFinished: () => monitoringFinished.TrySetResult(), RequireVerifiedExit: true,
                    AttachPipeOutput: session.HasOwnedPipes ? (onOut, onErr) => session.BeginReadPipes(onOut, onErr) : null,
                    ArePipesDrained: session.HasOwnedPipes ? () => session.PipeDrainTask.IsCompleted : null,
                    CustomTerminate: () => session.TerminateTree(),
                    HasGroupExited: () => session.AreAllProcessesExited);
                ModLaunch.McLaunchWatcher = watcher;
                if (task.IsCanceled || Volatile.Read(ref canceled) != 0) TriggerCancellation();
                task.Output = session.Process;
                ModLaunch.McLaunchLog("已启动 MMC 受管理游戏会话：" + sessionId.ToString("N"));
            } catch {
                if (!session.AreAllProcessesExited) session.TerminateTree();
                monitoringFinished.TrySetResult();
                throw;
            }
        }
        private void TriggerCancellation() {
            Interlocked.Exchange(ref canceled, 1);
            ModWatcher.Watcher activeWatcher = watcher;
            if (activeWatcher is not null) {
                if (Interlocked.Exchange(ref cancelSignaled, 1) == 0) activeWatcher.Kill();
                return;
            }
            if (session is not null && !session.AreAllProcessesExited) session.TerminateTree();
        }
        private void CancelGame() {
            Interlocked.Exchange(ref canceled, 1);
            _ = Task.Run(() => {
                try { TriggerCancellation(); }
                catch (Exception error) { Logger.Warn(error, "取消 MMC 游戏启动时保留会话监视器等待退出"); }
            });
        }

        public void Wait(LegacyLaunchTask<Process, int> task) {
            var state = watcher.State;
            while (state == ModWatcher.Watcher.MinecraftState.Loading) {
                if (task.IsCanceled || Volatile.Read(ref canceled) != 0) TriggerCancellation();
                state = watcher.WaitForStartupState(100);
            }
            if (state == ModWatcher.Watcher.MinecraftState.Canceled) throw new OperationCanceledException();
            if (state == ModWatcher.Watcher.MinecraftState.Crashed) {
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
            EnsurePackageCommandsConfirmed(instance, commandContext.PackageConfiguration, CommandRedactionSecrets(), token);
            var context = plan is null ? commandContext : commandContext with {
                RedactionSecrets = RedactionSecrets(plan),
                TemporaryDirectory = plan.MutableDirectories.TemporaryDirectory
            };
            var variables = plan is null ? null : MmcRuntimeLaunchBuilder.CommandVariables(plan);
            Guid? boundSessionId = plan is null ? null : sessionId;
            MmcSessionRuntimeBinding boundRuntime = plan?.RuntimeBinding;
            RunTrackedStageCommand(stage, instanceId, boundSessionId, boundRuntime,
                context, variables, command, ct => {
                    var q = AcquireQuiescedAdmission(commandContext.InstanceRoot, instanceId, ct);
                    return (q, q.Lease);
                }, text => ModLaunch.McLaunchLog(text), token);
            if (stage == MmcRuntimeCommandStage.PreLaunch) preLaunchCommandRan = true;
        }

        private ImmutableArray<string> CommandRedactionSecrets() {
            var loader = ModLaunch.McLoginLoader;
            if (loader is null || loader.State != ModBase.LoadState.Finished) return [];
            var login = loader.Output;
            string sessionSecret = string.IsNullOrEmpty(login.AccessToken) || string.IsNullOrEmpty(login.Uuid)
                ? null : "token:" + login.AccessToken + ":" + login.Uuid;
            return new[] { login.AccessToken, sessionSecret, login.ClientToken }
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
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
    private static void EnsurePackageCommandsConfirmed(ModMinecraft.McInstance target, MmcConfiguration configuration,
        ImmutableArray<string> secrets, CancellationToken token) {
        var summary = MmcLaunchPreferences.CaptureActivePackageCommands(configuration, secrets);
        if (summary is null) return;
        if (MmcLaunchPreferences.IsPackageCommandsConfirmed(target, summary)) return;
        if (!ConfirmPackageCommands(target, summary))
            throw new OperationCanceledException("已取消执行未经确认的整合包命令。", token);
        token.ThrowIfCancellationRequested();
        MmcLaunchPreferences.ConfirmPackageCommands(target, summary);
    }

    private sealed class QuiescedAdmission(MmcConfigurationQuiescence quiet, MmcAdmissionLease lease) : IDisposable {
        public MmcAdmissionLease Lease { get; } = lease;
        public void Dispose() {
            try { Lease.Dispose(); } finally { quiet.Dispose(); }
        }
    }

    private static QuiescedAdmission AcquireQuiescedAdmission(string instanceRoot, Guid instanceId, CancellationToken token) {
        var quiet = MmcConfigurationCoordinator.Quiesce(instanceRoot, token);
        try {
            return new(quiet, MmcAdmissionGate.Acquire(instanceRoot, instanceId, TimeSpan.Zero, token));
        } catch {
            quiet.Dispose();
            throw;
        }
    }

    private static void EnsureGameRootDirectory(MmcInstancePaths paths, Guid instanceId, CancellationToken token) {
        if (Directory.Exists(paths.GameRoot)) return;
        using var admission = AcquireQuiescedAdmission(paths.InstanceRoot, instanceId, token);
        MmcFileTransactions.AssertNoPending(admission.Lease);
        MmcSessions.AssertNoActive(admission.Lease);
        MmcInstancePaths verified = MmcInstancePaths.Resolve(paths.InstanceRoot, paths.GameRootLeaf);
        if (!Directory.Exists(verified.GameRoot)) Directory.CreateDirectory(verified.GameRoot);
    }

    private static void ReconcilePrevious(MmcInstancePaths paths, Guid instanceId, MmcJavaPinStore pins, CancellationToken token, MmcMutableRuntimeAuthority runtimeAuthority) {
        using var admission = AcquireQuiescedAdmission(paths.InstanceRoot, instanceId, token);
        MmcOperationPendingGuard.AssertNoForeignPending(admission.Lease);
        MmcFileTransactions.AssertNoTransactionsOnly(admission.Lease);
        MmcCommandOperations.ReconcileAll(admission.Lease, runtimeAuthority);
        string directory = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions");
        if (Directory.Exists(directory)) {
            var entries = Directory.EnumerateFileSystemEntries(directory).Take(1025).ToArray();
            if (entries.Length > 1024) throw new IOException("MMC 会话记录超过恢复预算。");
            foreach (string entry in entries) {
                if (!Guid.TryParseExact(Path.GetFileName(entry), "N", out Guid id)) throw new IOException("MMC 存在无法识别的会话记录，需要恢复确认。");
                var result = MmcSessions.Reconcile(admission.Lease, id,
                    new MmcSessionProcessInspector(MmcSessionProcessInspector.TryProcessCommandLineNonce),
                    pins.ReleaseVerified, runtimeAuthority);
                if (result.Observation != MmcSessionObservation.ProvenExited)
                    throw new IOException("此 MMC 实例仍在运行或会话尚待确认：" + result.Diagnostic);
            }
        }
        MmcFileTransactions.AssertNoPending(admission.Lease);
        MmcSessions.AssertNoActive(admission.Lease);
    }
    private static void RunTrackedStageCommand(
        MmcRuntimeCommandStage stage,
        Guid instanceId,
        Guid? boundSessionId,
        MmcSessionRuntimeBinding boundRuntime,
        MmcRuntimeCommandContext context,
        IReadOnlyDictionary<string, string> variables,
        string command,
        Func<CancellationToken, (IDisposable Guard, MmcAdmissionLease Lease)> acquireAdmission,
        Action<string> log,
        CancellationToken token) {
        MmcCommandLease cmdLease;
        var begin = acquireAdmission(token);
        using (begin.Guard) {
            cmdLease = MmcCommandOperations.Begin(begin.Lease, stage, boundSessionId, boundRuntime);
        }
        MmcRuntimeCommandExecution.MmcRuntimeCommandOutcome outcome = new(TreeProvenExited: false, "包内命令进程组退出尚未确认。", []);
        try {
            ExceptionDispatchInfo executionFailure = null;
            try {
                MmcRuntimeCommandExecution.ExecuteTracked(stage, context, variables, command, cmdLease, out outcome, log, token);
            } catch (Exception ex) {
                executionFailure = ExceptionDispatchInfo.Capture(ex);
            }
            try {
                var finish = acquireAdmission(CancellationToken.None);
                using (finish.Guard) {
                    if (outcome.TreeProvenExited) cmdLease.CompleteProvenExited(finish.Lease);
                    else cmdLease.RecordUnconfirmed(finish.Lease, outcome.Diagnostic ?? "包内命令进程组退出尚未确认。", outcome.SurvivingProcesses);
                }
            } catch (Exception settleError) {
                Logger.Warn(settleError, $"MMC 包内命令（{MmcRuntimeCommandExecution.StageName(stage)}）收尾确认失败，保留命令待确认记录");
                if (executionFailure is null) {
                    throw new IOException($"MMC 包内命令（{MmcRuntimeCommandExecution.StageName(stage)}）收尾确认失败，已保留待恢复记录：" + settleError.Message, settleError);
                }
            }
            executionFailure?.Throw();
        } finally {
            cmdLease.Dispose();
        }
    }

    private static async Task MonitorExitAsync(MmcRunningSession session, ModMinecraft.McInstance instance, string root, Guid instanceId,
        Task monitoringFinished, MmcRuntimeLaunchPlan runtime, MmcRuntimeCommandContext commandContext, ImmutableArray<string> commandSecrets) {
        if (session.IsWrapperManaged && session.WrapperProcess is Process wrapperProcess) {
            _ = wrapperProcess.WaitForExitAsync().ContinueWith(_ => {
                try {
                    if (!session.Process.HasExited)
                        ModLaunch.McLaunchLog("MMC 包装入口进程已退出，受管 Java 进程仍在运行，会话继续保持活动状态。");
                } catch { }
            }, TaskScheduler.Default);
        }
        bool reported = false, postExitDone = false;
        while (true) {
            try {
                await session.WaitForExitAsync().ConfigureAwait(false);
                using (var admission = MmcAdmissionGate.Acquire(root, instanceId, TimeSpan.FromSeconds(10)))
                    session.RecordVerifiedExit(admission);
                // Drain watcher stop callbacks (backups, settings finalization, pipes) BEFORE PostExit touches GameRoot.
                await monitoringFinished.ConfigureAwait(false);
                // PostExit runs after Exited.record is persisted, outside the admission lock, and strictly
                // BEFORE retiring mutable runtime directories (TEMP/TMP) or releasing the pin (contract §1.2, D9, D15).
                if (!postExitDone) {
                    postExitDone = true;
                    string command = MmcRuntimeCommandExecution.Read(commandContext, MmcRuntimeCommandStage.PostExit);
                    if (command is not null) try {
                        var summary = MmcLaunchPreferences.CaptureActivePackageCommands(commandContext.PackageConfiguration!, commandSecrets);
                        if (summary is null || !MmcLaunchPreferences.IsPackageCommandsConfirmed(instance, summary))
                            throw new OperationCanceledException("PostExitCommand 缺少有效的命令集确认签名。");
                        // A PostExit failure is a diagnostic only. It neither rolls back the finished session
                        // nor rewrites its exit receipt, so it can never turn a clean exit into a failed one.
                        RunTrackedStageCommand(MmcRuntimeCommandStage.PostExit, instanceId,
                            session.Receipt.SessionId, runtime.RuntimeBinding, commandContext,
                            MmcRuntimeLaunchBuilder.CommandVariables(runtime), command, _ => {
                                var a = MmcAdmissionGate.Acquire(root, instanceId, TimeSpan.FromSeconds(10));
                                return (a, a);
                            }, text => ModLaunch.McLaunchLog(text), CancellationToken.None);
                    } catch (Exception error) {
                        Logger.Warn(error, "MMC 包内命令（PostExitCommand）失败，已结束的会话保持不变");
                    }
                }
                using (var admission = MmcAdmissionGate.Acquire(root, instanceId, TimeSpan.FromSeconds(10)))
                    session.FinalizeVerifiedExit(admission);
                Active.TryRemove(session.Receipt.SessionId, out _);
                session.ReleaseCompletedResources();
                if (Active.IsEmpty && Interlocked.Exchange(ref closeAfterSessions, 0) != 0)
                    ModBase.RunInUi(() => ModMain.FrmMain?.EndProgram(false));
                // Watcher retains the shared Process instance for crash UI and legacy launch state.
                return;
            } catch (Exception error) {
                if (!reported) { Logger.Warn(error, "MMC 游戏会话退出尚未完成确认，保留 Java pin 和恢复记录并等待重试"); reported = true; }
                await Task.Delay(3000).ConfigureAwait(false);
            }
        }
    }
}
