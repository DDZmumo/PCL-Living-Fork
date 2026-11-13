using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcSessionCheckpoint { LaunchingPersisted, ProcessCreated, RunningPersisted, ExitedPersisted }

/// <summary>Owned-process session primitives supporting direct Java and wrapped process-tree entries.</summary>
public static class MmcSessions {
    public const string NonceArgumentPrefix = "-Dpcllf.mmc.session=";

    /// <summary>
    /// Resolves a direct executable, relative path (against <paramref name="workingDirectory"/>), or bare command
    /// name (against standard OS search directories) into a concrete file path before identity hashing.
    /// </summary>
    public static string ResolveExecutablePath(string commandOrPath, string workingDirectory) {
        if (string.IsNullOrWhiteSpace(commandOrPath)) throw new IOException("可执行入口不能为空。");
        string trimmed = commandOrPath.Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.IndexOf('\0') >= 0) throw new IOException("可执行入口包含无效字符。");
        static IEnumerable<string> CandidateFiles(string basePath) =>
            string.IsNullOrEmpty(Path.GetExtension(basePath))
                ? [basePath + ".exe", basePath + ".com", basePath]
                : [basePath];

        if (Path.IsPathFullyQualified(trimmed)) {
            foreach (string candidate in CandidateFiles(Path.GetFullPath(trimmed))) {
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("找不到指定的可执行入口程序。", trimmed);
        }
        if (trimmed.IndexOfAny(['\\', '/']) >= 0) {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
                throw new IOException("解析相对可执行入口程序需要合法工作目录。");
            string baseDir = Path.GetFullPath(workingDirectory);
            MmcInstancePaths.CheckDirectoryRole(baseDir);
            foreach (string candidate in CandidateFiles(Path.GetFullPath(Path.Combine(baseDir, trimmed)))) {
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("找不到指定的相对可执行入口程序。", trimmed);
        }

        List<string> searchDirs = [];
        void AddDir(string? dir) {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try {
                string full = Path.GetFullPath(dir.Trim().Trim('"'));
                if (!searchDirs.Contains(full, StringComparer.OrdinalIgnoreCase)) searchDirs.Add(full);
            } catch (ArgumentException) { } catch (PathTooLongException) { }
        }
        AddDir(Path.GetDirectoryName(Environment.ProcessPath));
        AddDir(Environment.SystemDirectory);
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar)) {
            foreach (string item in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) AddDir(item);
        }
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Path.IsPathFullyQualified(workingDirectory)) AddDir(workingDirectory);

        foreach (string dir in searchDirs) {
            foreach (string candidate in CandidateFiles(Path.Combine(dir, trimmed))) {
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("找不到指定的包装入口程序：" + trimmed, trimmed);
    }

    public static MmcRunningSession Launch(MmcAdmissionLease lease, MmcSessionLaunchRequest request, Action finalValidation,
        Func<Guid, MmcExecutableIdentity, IMmcJavaSessionPin> acquireJavaPin, Action<MmcSessionCheckpoint>? checkpoint = null) {
        lease.AssertHeld(); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(finalValidation); ArgumentNullException.ThrowIfNull(acquireJavaPin);
        if (request.InstanceId != lease.InstanceId || request.DefinitionGeneration < 1 || !MmcDurableFiles.IsHash(request.RuntimeFingerprint)) throw new IOException("会话身份、代数或计划摘要无效。");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot, request.GameRootLeaf);
        MmcFileTransactions.AssertNoPending(lease); AssertNoActive(lease);
        ProcessStartInfo start = CopyStartInfo(request.StartInfo, paths);
        string resolvedEntryPath;
        if (request.IsWrapperManaged) {
            if (request.WrapperIdentity is null || !MmcDurableFiles.IsHash(request.WrapperIdentity.Sha256) || !Path.IsPathFullyQualified(request.WrapperIdentity.ExecutablePath))
                throw new IOException("包装模式必须提供已校验的包装入口程序身份。");
            if (request.NonceArgumentIndex < 0 || request.NonceArgumentIndex > start.ArgumentList.Count)
                throw new IOException("包装模式的会话 nonce 注入位置无效。");
            resolvedEntryPath = ResolveExecutablePath(start.FileName, start.WorkingDirectory);
            if (!SameExecutable(ObserveExecutable(resolvedEntryPath), request.WrapperIdentity))
                throw new IOException("包装进程入口不符合已选包装程序身份。");
            if (!SameExecutable(ObserveExecutable(request.JavaIdentity.ExecutablePath), request.JavaIdentity))
                throw new IOException("受管 Java 入口不符合已选 Java 身份。");
        } else {
            if (request.WrapperIdentity is not null || request.NonceArgumentIndex != 0)
                throw new IOException("直接启动模式不接受包装入口配置。");
            resolvedEntryPath = start.FileName;
            if (!SameExecutable(ObserveExecutable(resolvedEntryPath), request.JavaIdentity))
                throw new IOException("进程入口不符合已选 Java 身份。");
        }
        if (request.RuntimeBinding is not null && request.SessionId is null) throw new IOException("Mutable runtime requires one preallocated session ID.");
        Guid sessionId = request.SessionId ?? Guid.NewGuid();
        string sessionDirectory = SessionDirectory(paths, sessionId);
        string sessionHistory = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + sessionId.ToString("N"));
        if (MmcInstancePaths.Attributes(sessionDirectory) is not null || MmcInstancePaths.Attributes(sessionHistory) is not null)
            throw new IOException("会话 ID 已被使用，不能覆盖原有会话记录。");
        if (request.RuntimeBinding is { } runtimeBinding) MmcSessionRuntimeDirectories.ValidateLaunch(paths,request.InstanceId,sessionId,runtimeBinding,request.RuntimeAuthority);
        string nonce = Guid.NewGuid().ToString("N");
        IMmcJavaSessionPin pin = acquireJavaPin(sessionId, request.JavaIdentity) ?? throw new IOException("Java 服务未返回保护租约。");
        bool durableIntent = false; Process? process = null; MmcSessionProcessTree? processTree = null;
        MmcSessionReceipt? launchingReceipt = null; string? launchingHash = null;
        try {
            if (!SameExecutable(pin.Identity, request.JavaIdentity)) throw new IOException("Java pin 身份不一致。");
            if (request.IsWrapperManaged) {
                resolvedEntryPath = ResolveExecutablePath(start.FileName, start.WorkingDirectory);
                if (!SameExecutable(ObserveExecutable(resolvedEntryPath), request.WrapperIdentity!)) throw new IOException("包装进程入口身份已变化。");
                if (!SameExecutable(ObserveExecutable(request.JavaIdentity.ExecutablePath), request.JavaIdentity)) throw new IOException("受管 Java 镜像身份已变化。");
            } else {
                if (!SameExecutable(ObserveExecutable(start.FileName), request.JavaIdentity)) throw new IOException("Java pin 身份不一致。");
            }
            ValidateCurrentDefinition(paths, request.InstanceId, request.DefinitionGeneration, request.GameRootLeaf);
            if (!SameExecutable(ObserveExecutable(request.LauncherIdentity.ExecutablePath), request.LauncherIdentity)) throw new IOException("宿主镜像身份已变化。");
            finalValidation();
            if (request.RuntimeBinding is { } finalBinding) MmcSessionRuntimeDirectories.ValidateLaunch(paths,request.InstanceId,sessionId,finalBinding,request.RuntimeAuthority);
            string directory = sessionDirectory; Directory.CreateDirectory(directory);
            MmcSessionReceipt receipt = new(1, sessionId, request.InstanceId, paths.VersionRoot, request.DefinitionGeneration,
                request.RuntimeFingerprint, nonce, request.JavaIdentity, request.LauncherIdentity) {
                RuntimeBinding = request.RuntimeBinding,
                WrapperIdentity = request.IsWrapperManaged ? request.WrapperIdentity : null
            };
            string hash = MmcDurableFiles.WriteRecord(Path.Combine(directory, "Launching.record"), receipt);
            launchingReceipt = receipt; launchingHash = hash;
            durableIntent = true; checkpoint?.Invoke(MmcSessionCheckpoint.LaunchingPersisted);
            // JVM system properties must precede the main class/-jar boundary, never become game args.
            start.ArgumentList.Insert(request.NonceArgumentIndex, NonceArgumentPrefix + nonce);
            if (OperatingSystem.IsWindows()) MmcRuntimeWindowsCommandLine.Validate(resolvedEntryPath, start.ArgumentList);

            MmcSessionProcessRegistration running;
            if (!request.IsWrapperManaged) {
                process = Process.Start(start) ?? throw new IOException("无法创建受管理游戏进程。");
                checkpoint?.Invoke(MmcSessionCheckpoint.ProcessCreated);
                MmcExecutableIdentity image = ObserveProcessExecutable(process);
                if (!SameExecutable(image, request.JavaIdentity)) throw new IOException("新进程镜像与已选 Java 身份不一致。");
                running = new(1, sessionId, hash, process.Id, process.StartTime.ToUniversalTime().Ticks, image);
            } else {
                MmcExecutableIdentity? wrapperImage = null;
                processTree = MmcSessionProcessTree.Start(resolvedEntryPath, start, redirectPipes: true, validateSuspendedProcess: wrapper => {
                    MmcExecutableIdentity observedWrapper = ObserveProcessExecutable(wrapper);
                    if (!SameExecutable(observedWrapper, request.WrapperIdentity!))
                        throw new IOException("包装入口进程镜像与预期身份不一致。");
                    wrapperImage = observedWrapper;
                });
                checkpoint?.Invoke(MmcSessionCheckpoint.ProcessCreated);
                (process, MmcExecutableIdentity javaImage, long javaStartTicks) = DiscoverManagedJavaChild(
                    processTree, request.JavaIdentity, nonce, request.WrapperJavaDiscoveryTimeout,
                    request.NonceProbe ?? MmcSessionProcessInspector.TryProcessCommandLineNonce);
                running = new(1, sessionId, hash, process.Id, javaStartTicks, javaImage) {
                    WrapperProcess = new(processTree.EntryProcess.Id, processTree.EntryStartTimeUtcTicks, wrapperImage!)
                };
            }
            MmcDurableFiles.WriteRecord(Path.Combine(directory, "Running.record"), running);
            checkpoint?.Invoke(MmcSessionCheckpoint.RunningPersisted);
            return new(receipt, running, hash, process, pin, request.RuntimeAuthority, processTree);
        } catch {
            bool provenTreeExited = processTree is not null &&
                processTree.TerminateAndWait(MmcProcessTreeTermination.GraceMilliseconds) &&
                (process is null || process.HasExited);
            if (durableIntent) {
                // When the owned wrapper Job object proved every child exited (for instance, the
                // wrapper exited cleanly without ever starting Java), persist an explicit exit proof so
                // reconciliation can clean up the failed launch without guessing.
                if (provenTreeExited && launchingReceipt is not null && launchingHash is not null) {
                    try { WriteExit(sessionDirectory, launchingReceipt, launchingHash, MmcSessionExitProof.WrapperManagedExit); }
                    catch { }
                }
                pin.KeepForRecovery();
            } else pin.CompleteVerifiedExit();
            process?.Dispose(); processTree?.Dispose(); pin.Dispose(); throw;
        }
    }

    private static (Process Process, MmcExecutableIdentity Image, long StartTimeUtcTicks) DiscoverManagedJavaChild(
        MmcSessionProcessTree tree, MmcExecutableIdentity expectedJavaIdentity, string nonce, TimeSpan timeout,
        Func<int, string, bool?> nonceProbe) {
        long timeoutMs = Math.Clamp((long)timeout.TotalMilliseconds, 100L, 300_000L);
        long deadline = Environment.TickCount64 + timeoutMs;
        int wrapperPid = tree.EntryProcess.Id;
        int iteration = 0;

        while (true) {
            int[] pids = tree.ObserveProcessIds();
            foreach (int pid in pids) {
                if (pid <= 0 || pid == wrapperPid) continue;
                Process? candidate = null;
                try {
                    candidate = Process.GetProcessById(pid);
                    MmcExecutableIdentity observedImage = ObserveProcessExecutable(candidate);
                    if (!SameExecutable(observedImage, expectedJavaIdentity)) {
                        candidate.Dispose();
                        continue;
                    }
                    if (nonceProbe(pid, nonce) != true) {
                        candidate.Dispose();
                        continue;
                    }
                    long startTicks = candidate.StartTime.ToUniversalTime().Ticks;
                    return (candidate, observedImage, startTicks);
                } catch (Exception ex) when (IsFailure(ex)) {
                    candidate?.Dispose();
                }
            }

            if (pids.Length == 0 && tree.EntryProcess.HasExited) {
                if (tree.ObserveProcessIds().Length == 0)
                    throw new IOException("包装入口进程在启动受管 Java 进程前已退出。");
            }
            if (Environment.TickCount64 >= deadline)
                throw new IOException("等待包装程序启动受管 Java 进程超时。");
            Thread.Sleep(iteration++ < 10 ? 5 : 15);
        }
    }

    public static void AssertNoActive(MmcAdmissionLease lease) {
        MmcOperationPendingGuard.AssertNoForeignPending(lease);
        lease.AssertHeld(); string root = MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions");
        if (MmcInstancePaths.Attributes(root) is not null && (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any()))
            throw new IOException("存在活动或待确认 MMC 会话。");
    }

    public static MmcSessionResult Reconcile(MmcAdmissionLease lease, Guid sessionId, IMmcSessionProcessInspector inspector,
        Action<MmcSessionReceipt> releaseVerifiedJavaPin,MmcMutableRuntimeAuthority? runtimeAuthority=null) {
        lease.AssertHeld(); ArgumentNullException.ThrowIfNull(inspector); ArgumentNullException.ThrowIfNull(releaseVerifiedJavaPin);
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        try {
            string directory = SessionDirectory(paths, sessionId);
            if (!Directory.Exists(directory)) return new(sessionId, MmcSessionObservation.Unknown, "会话收据缺失，不能猜测进程已经退出。");
            (MmcSessionReceipt receipt, string hash) = ReadLaunching(lease, paths, sessionId, directory);
            MmcSessionProcessRegistration? running = ReadRunning(receipt, hash, directory);
            if (MmcInstancePaths.Attributes(Path.Combine(directory, "Exited.record")) is not null) {
                ValidateExit(directory, receipt, hash);
                MmcSessionRuntimeDirectories.CompleteVerifiedExit(paths, receipt,runtimeAuthority);
                releaseVerifiedJavaPin(receipt); Archive(paths, directory, sessionId);
                return new(sessionId, MmcSessionObservation.ProvenExited);
            }
            MmcSessionObservation observation = inspector.Inspect(receipt, running);
            if (observation != MmcSessionObservation.ProvenExited) return new(sessionId, observation, observation == MmcSessionObservation.Unknown ? "会话仍待确认，保持只读。" : null);
            WriteExit(directory, receipt, hash, running?.WrapperProcess is null ? MmcSessionExitProof.TrustedHostProcessObservation : MmcSessionExitProof.WrapperManagedExit);
                MmcSessionRuntimeDirectories.CompleteVerifiedExit(paths, receipt,runtimeAuthority);
            releaseVerifiedJavaPin(receipt); Archive(paths, directory, sessionId);
            return new(sessionId, MmcSessionObservation.ProvenExited);
        } catch (Exception ex) when (IsFailure(ex)) { return new(sessionId, MmcSessionObservation.Unknown, ex.Message); }
    }

    public static MmcExecutableIdentity ObserveExecutable(string path) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new IOException("进程入口必须是绝对路径。");
        string full = MmcCanonicalPaths.ForExistingFile(Path.GetFullPath(path)); MmcInstancePaths.EnsureNoLinks(full);
        return new(full, MmcDurableFiles.Observe(full) ?? throw new IOException("进程镜像不存在。"));
    }
    public static MmcExecutableIdentity ObserveProcessExecutable(Process process) => ObserveExecutable(MmcProcessImages.Read(process));
    internal static bool SameExecutable(MmcExecutableIdentity first, MmcExecutableIdentity second) =>
        first is not null && second is not null && MmcDurableFiles.IsHash(first.Sha256) && first.Sha256 == second.Sha256 &&
        string.Equals(Path.GetFullPath(first.ExecutablePath), Path.GetFullPath(second.ExecutablePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void CompleteOwnedExit(MmcAdmissionLease lease, MmcSessionReceipt receipt, MmcSessionProcessRegistration registration,
        string hash, Process process, IMmcJavaSessionPin pin, Action<MmcSessionCheckpoint>? checkpoint,
        MmcMutableRuntimeAuthority? runtimeAuthority = null, MmcSessionProcessTree? processTree = null) {
        RecordOwnedExit(lease, receipt, registration, hash, process, processTree, checkpoint);
        FinalizeOwnedExit(lease, receipt, registration, hash, pin, runtimeAuthority);
    }

    internal static void RecordOwnedExit(MmcAdmissionLease lease, MmcSessionReceipt receipt, MmcSessionProcessRegistration registration,
        string hash, Process process, MmcSessionProcessTree? processTree, Action<MmcSessionCheckpoint>? checkpoint) {
        lease.AssertHeld();
        if (lease.InstanceId != receipt.InstanceId || !string.Equals(lease.VersionRoot, receipt.CanonicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("会话退出与准入身份不一致。");
        if (!process.HasExited) throw new IOException("受管理进程尚未退出，不能释放运行租约。");
        if (processTree is not null && processTree.ObserveProcessIds().Length > 0)
            throw new IOException("受管理进程组仍有活动成员，不能写入退出收据。");
        if (process.Id != registration.ProcessId || process.StartTime.ToUniversalTime().Ticks != registration.CreationTimeUtcTicks) throw new IOException("进程句柄身份不一致。");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot); string directory = SessionDirectory(paths, receipt.SessionId);
        var persisted = ReadLaunching(lease, paths, receipt.SessionId, directory);
        if (persisted.Hash != hash || ReadRunning(receipt, hash, directory) != registration) throw new IOException("会话收据与运行租约不一致。");
        WriteExit(directory, receipt, hash, registration.WrapperProcess is null ? MmcSessionExitProof.OwnedProcessHandleExited : MmcSessionExitProof.WrapperManagedExit);
        checkpoint?.Invoke(MmcSessionCheckpoint.ExitedPersisted);
    }

    internal static void FinalizeOwnedExit(MmcAdmissionLease lease, MmcSessionReceipt receipt, MmcSessionProcessRegistration registration,
        string hash, IMmcJavaSessionPin pin, MmcMutableRuntimeAuthority? runtimeAuthority = null) {
        lease.AssertHeld();
        if (lease.InstanceId != receipt.InstanceId || !string.Equals(lease.VersionRoot, receipt.CanonicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("会话退出与准入身份不一致。");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot); string directory = SessionDirectory(paths, receipt.SessionId);
        var persisted = ReadLaunching(lease, paths, receipt.SessionId, directory);
        if (persisted.Hash != hash || ReadRunning(receipt, hash, directory) != registration) throw new IOException("会话收据与运行租约不一致。");
        if (MmcInstancePaths.Attributes(Path.Combine(directory, "Exited.record")) is null)
            throw new IOException("尚未写入退出收据，不能执行会话收尾。");
        ValidateExit(directory, receipt, hash);
        MmcSessionRuntimeDirectories.CompleteVerifiedExit(paths, receipt, runtimeAuthority);
        pin.CompleteVerifiedExit(); Archive(paths, directory, receipt.SessionId);
    }

    private static ProcessStartInfo CopyStartInfo(ProcessStartInfo source, MmcInstancePaths paths) {
        ArgumentNullException.ThrowIfNull(source);
        if (source.UseShellExecute || !string.IsNullOrEmpty(source.Arguments)) throw new IOException("MMC 受管理进程必须使用 ArgumentList 和直接进程入口。");
        string workingDirectory = Path.GetFullPath(source.WorkingDirectory);
        if (!string.Equals(workingDirectory, paths.GameRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("进程工作目录不符合实例 GameRoot。");
        MmcInstancePaths.CheckDirectoryRole(paths.GameRoot);
        if (!Directory.Exists(paths.GameRoot)) throw new IOException("游戏目录尚未物化。");
        ProcessStartInfo copy = new(source.FileName) {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = source.CreateNoWindow,
            RedirectStandardInput = source.RedirectStandardInput, RedirectStandardOutput = source.RedirectStandardOutput,
            RedirectStandardError = source.RedirectStandardError, WindowStyle = source.WindowStyle,
            StandardInputEncoding = source.StandardInputEncoding, StandardOutputEncoding = source.StandardOutputEncoding,
            StandardErrorEncoding = source.StandardErrorEncoding
        };
        foreach (string argument in source.ArgumentList) {
            if (argument.StartsWith(NonceArgumentPrefix, StringComparison.Ordinal)) throw new IOException("包或用户参数不能覆盖内部会话 nonce。");
            copy.ArgumentList.Add(argument);
        }
        copy.Environment.Clear(); foreach (var pair in source.Environment) copy.Environment[pair.Key] = pair.Value;
        return copy;
    }
    private static void ValidateCurrentDefinition(MmcInstancePaths paths, Guid instanceId, long generation, string gameRootLeaf) {
        string path = paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json");
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例定义超过读取预算。");
        using MemoryStream content = new(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (content.Length + count > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例定义读取期间超过预算。");
            content.Write(buffer, 0, count);
        }
        using JsonDocument json = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(content.ToArray()));
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("instanceId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "D", out Guid actualId) || actualId != instanceId || !root.TryGetProperty("generation", out var gen) ||
            !gen.TryGetInt64(out long actualGeneration) || actualGeneration != generation || !root.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object ||
            !layout.TryGetProperty("gameRootLeaf", out var leaf) || leaf.ValueKind != JsonValueKind.String || leaf.GetString() != gameRootLeaf)
            throw new IOException("最终实例身份、代数或布局已经改变。");
    }
    private static (MmcSessionReceipt Receipt, string Hash) ReadLaunching(MmcAdmissionLease lease, MmcInstancePaths paths, Guid id, string directory) {
        (var receipt, string hash) = MmcDurableFiles.ReadRecord<MmcSessionReceipt>(Path.Combine(directory, "Launching.record"));
        if (receipt.SchemaVersion != 1 || receipt.SessionId != id || receipt.InstanceId != lease.InstanceId || receipt.DefinitionGeneration < 1 ||
            !string.Equals(receipt.CanonicalRoot, paths.VersionRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !MmcDurableFiles.IsHash(receipt.RuntimeFingerprint) || !Guid.TryParseExact(receipt.Nonce, "N", out _) ||
            receipt.JavaIdentity is null || receipt.LauncherIdentity is null || !MmcDurableFiles.IsHash(receipt.JavaIdentity.Sha256) || !MmcDurableFiles.IsHash(receipt.LauncherIdentity.Sha256) ||
            !Path.IsPathFullyQualified(receipt.JavaIdentity.ExecutablePath) || !Path.IsPathFullyQualified(receipt.LauncherIdentity.ExecutablePath) ||
            (receipt.WrapperIdentity is not null && (!MmcDurableFiles.IsHash(receipt.WrapperIdentity.Sha256) || !Path.IsPathFullyQualified(receipt.WrapperIdentity.ExecutablePath))))
            throw new IOException("会话收据版本、身份或内容无效。");
        return (receipt, hash);
    }
    private static MmcSessionProcessRegistration? ReadRunning(MmcSessionReceipt receipt, string hash, string directory) {
        string path = Path.Combine(directory, "Running.record"); if (MmcInstancePaths.Attributes(path) is null) return null;
        var registration = MmcDurableFiles.ReadRecord<MmcSessionProcessRegistration>(path).Value;
        if (registration.SchemaVersion != 1 || registration.SessionId != receipt.SessionId || registration.LaunchingSha256 != hash ||
            registration.ProcessId <= 0 || registration.CreationTimeUtcTicks <= 0 || registration.ExecutableIdentity is null || !SameExecutable(registration.ExecutableIdentity, receipt.JavaIdentity))
            throw new IOException("运行进程登记与收据不一致。");
        if (registration.WrapperProcess is { } wrapper && (wrapper.ProcessId <= 0 || wrapper.CreationTimeUtcTicks <= 0 || wrapper.ExecutableIdentity is null ||
            !MmcDurableFiles.IsHash(wrapper.ExecutableIdentity.Sha256) || !Path.IsPathFullyQualified(wrapper.ExecutableIdentity.ExecutablePath) ||
            (receipt.WrapperIdentity is not null && !SameExecutable(wrapper.ExecutableIdentity, receipt.WrapperIdentity))))
            throw new IOException("包装入口进程登记与收据不一致。");
        return registration;
    }
    private static void WriteExit(string directory, MmcSessionReceipt receipt, string hash, string proof) {
        if (MmcInstancePaths.Attributes(Path.Combine(directory, "Exited.record")) is null)
            MmcDurableFiles.WriteRecord(Path.Combine(directory, "Exited.record"), new MmcSessionExitRecord(1, receipt.SessionId, hash, proof));
        else ValidateExit(directory, receipt, hash);
    }
    private static void ValidateExit(string directory, MmcSessionReceipt receipt, string hash) {
        var exit = MmcDurableFiles.ReadRecord<MmcSessionExitRecord>(Path.Combine(directory, "Exited.record")).Value;
        if (exit.SchemaVersion != 1 || exit.SessionId != receipt.SessionId || exit.LaunchingSha256 != hash ||
            !MmcSessionExitProof.IsKnown(exit.Proof)) throw new IOException("退出收据无效。");
    }
    private static string SessionDirectory(MmcInstancePaths paths, Guid id) {
        if (id == Guid.Empty) throw new IOException("会话 ID 不能为空。");
        return paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessions/" + id.ToString("N"));
    }
    private static void Archive(MmcInstancePaths paths, string directory, Guid id) {
        string destination = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + id.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); MmcInstancePaths.EnsureNoLinks(directory); MmcInstancePaths.EnsureNoLinks(destination);
        Directory.Move(directory, destination);
    }
    private static bool IsFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        NotSupportedException or Win32Exception or JsonException or DecoderFallbackException;
}

/// <summary>Retains a process handle and Java pin; Dispose during activity preserves recovery evidence.</summary>
public sealed class MmcRunningSession : IDisposable {
    public MmcSessionReceipt Receipt { get; }
    public MmcSessionProcessRegistration Registration { get; }
    public Process Process { get; }
    public Process? WrapperProcess => processTree?.EntryProcess;
    public bool IsWrapperManaged => Registration.WrapperProcess is not null;
    public bool HasOwnedPipes => processTree is not null;
    public bool AreAllProcessesExited => Process.HasExited && (processTree is null || processTree.ObserveProcessIds().Length == 0);
    public Task PipeDrainTask => processTree?.PipeDrainTask ?? Task.CompletedTask;
    private readonly string hash;
    private readonly IMmcJavaSessionPin pin;
    private readonly MmcMutableRuntimeAuthority? runtimeAuthority;
    private readonly MmcSessionProcessTree? processTree;
    private bool exitedRecorded, complete, disposed;
    internal MmcRunningSession(MmcSessionReceipt receipt, MmcSessionProcessRegistration registration, string hash, Process process,
        IMmcJavaSessionPin pin, MmcMutableRuntimeAuthority? runtimeAuthority = null, MmcSessionProcessTree? processTree = null) {
        Receipt = receipt; Registration = registration; this.hash = hash; Process = process; this.pin = pin;
        this.runtimeAuthority = runtimeAuthority; this.processTree = processTree;
    }
    /// <summary>Starts asynchronous pipe readers for the owned process group (wrapper mode).</summary>
    public void BeginReadPipes(Action<string?> onOutputLine, Action<string?> onErrorLine) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        if (processTree is null) throw new InvalidOperationException("当前会话未绑定受管进程组管道。");
        processTree.BeginReadPipes(onOutputLine, onErrorLine);
    }
    /// <summary>Waits until both the managed Java process and any remaining owned group members have exited.</summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (processTree is not null) await processTree.WaitForGroupEmptyAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Terminates the entire owned process group and proves all members gone within the bounded grace window.</summary>
    public bool TerminateTree(int graceMilliseconds = MmcProcessTreeTermination.GraceMilliseconds) {
        if (disposed) return true;
        return processTree is not null
            ? processTree.TerminateAndWait(graceMilliseconds)
            : MmcProcessTreeTermination.TerminateAndProveGone(null, Process, graceMilliseconds);
    }
    /// <summary>Phase 1 exit settlement: persists Exited.record after proving Java and its owned group exited.</summary>
    public void RecordVerifiedExit(MmcAdmissionLease lease, Action<MmcSessionCheckpoint>? checkpoint = null) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        if (exitedRecorded) return;
        MmcSessions.RecordOwnedExit(lease, Receipt, Registration, hash, Process, processTree, checkpoint);
        exitedRecorded = true;
    }
    /// <summary>Phase 2 exit settlement: retires mutable runtime directories, releases Java pin and archives session.</summary>
    public void FinalizeVerifiedExit(MmcAdmissionLease lease) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        if (complete) return;
        MmcSessions.FinalizeOwnedExit(lease, Receipt, Registration, hash, pin, runtimeAuthority);
        exitedRecorded = true;
        complete = true;
    }
    public void CompleteVerifiedExit(MmcAdmissionLease lease, Action<MmcSessionCheckpoint>? checkpoint = null) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        if (complete) return;
        RecordVerifiedExit(lease, checkpoint);
        FinalizeVerifiedExit(lease);
    }
    public void Dispose() {
        if (disposed) return;
        if (!complete) pin.KeepForRecovery();
        pin.Dispose(); Process.Dispose(); processTree?.Dispose(); disposed = true;
    }
}
