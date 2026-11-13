using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcSessionCheckpoint { LaunchingPersisted, ProcessCreated, RunningPersisted, ExitedPersisted }

/// <summary>Direct-process session primitives; wrapper-spawned descendants require separately verified host support.</summary>
public static class MmcSessions {
    public const string NonceArgumentPrefix = "-Dpcllf.mmc.session=";

    public static MmcRunningSession Launch(MmcAdmissionLease lease, MmcSessionLaunchRequest request, Action finalValidation,
        Func<Guid, MmcExecutableIdentity, IMmcJavaSessionPin> acquireJavaPin, Action<MmcSessionCheckpoint>? checkpoint = null) {
        lease.AssertHeld(); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(finalValidation); ArgumentNullException.ThrowIfNull(acquireJavaPin);
        if (request.InstanceId != lease.InstanceId || request.DefinitionGeneration < 1 || !MmcDurableFiles.IsHash(request.RuntimeFingerprint)) throw new IOException("会话身份、代数或计划摘要无效。");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot, request.GameRootLeaf);
        MmcFileTransactions.AssertNoPending(lease); AssertNoActive(lease);
        ProcessStartInfo start = CopyStartInfo(request.StartInfo, paths);
        if (!SameExecutable(ObserveExecutable(start.FileName), request.JavaIdentity)) throw new IOException("进程入口不符合已选 Java 身份。");
        if (request.RuntimeBinding is not null && request.SessionId is null) throw new IOException("Mutable runtime requires one preallocated session ID.");
        Guid sessionId = request.SessionId ?? Guid.NewGuid();
        string sessionDirectory = SessionDirectory(paths, sessionId);
        string sessionHistory = paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + sessionId.ToString("N"));
        if (MmcInstancePaths.Attributes(sessionDirectory) is not null || MmcInstancePaths.Attributes(sessionHistory) is not null)
            throw new IOException("会话 ID 已被使用，不能覆盖原有会话记录。");
        if (request.RuntimeBinding is { } runtimeBinding) MmcSessionRuntimeDirectories.ValidateLaunch(paths,request.InstanceId,sessionId,runtimeBinding,request.RuntimeAuthority);
        string nonce = Guid.NewGuid().ToString("N");
        IMmcJavaSessionPin pin = acquireJavaPin(sessionId, request.JavaIdentity) ?? throw new IOException("Java 服务未返回保护租约。");
        bool durableIntent = false; Process? process = null;
        try {
            if (!SameExecutable(pin.Identity, request.JavaIdentity) || !SameExecutable(ObserveExecutable(start.FileName), request.JavaIdentity)) throw new IOException("Java pin 身份不一致。");
            ValidateCurrentDefinition(paths, request.InstanceId, request.DefinitionGeneration, request.GameRootLeaf);
            if (!SameExecutable(ObserveExecutable(request.LauncherIdentity.ExecutablePath), request.LauncherIdentity)) throw new IOException("宿主镜像身份已变化。");
            finalValidation();
            if (request.RuntimeBinding is { } finalBinding) MmcSessionRuntimeDirectories.ValidateLaunch(paths,request.InstanceId,sessionId,finalBinding,request.RuntimeAuthority);
            string directory = sessionDirectory; Directory.CreateDirectory(directory);
            MmcSessionReceipt receipt = new(1, sessionId, request.InstanceId, paths.VersionRoot, request.DefinitionGeneration,
                request.RuntimeFingerprint, nonce, request.JavaIdentity, request.LauncherIdentity) { RuntimeBinding = request.RuntimeBinding };
            string hash = MmcDurableFiles.WriteRecord(Path.Combine(directory, "Launching.record"), receipt);
            durableIntent = true; checkpoint?.Invoke(MmcSessionCheckpoint.LaunchingPersisted);
            // JVM system properties must precede the main class/-jar boundary, never become game args.
            start.ArgumentList.Insert(0, NonceArgumentPrefix + nonce);
            process = Process.Start(start) ?? throw new IOException("无法创建受管理游戏进程。");
            checkpoint?.Invoke(MmcSessionCheckpoint.ProcessCreated);
            MmcExecutableIdentity image = ObserveProcessExecutable(process);
            if (!SameExecutable(image, request.JavaIdentity)) throw new IOException("新进程镜像与已选 Java 身份不一致。");
            MmcSessionProcessRegistration running = new(1, sessionId, hash, process.Id, process.StartTime.ToUniversalTime().Ticks, image);
            // Wrapper mode registers a second identity: the package wrapper entry is the process the handle
            // belongs to, and the managed Java process is its child. Exit is never concluded from the wrapper,
            // so the two must stay distinguishable in the durable record (contract §4.1, D7).
            if (request.WrapperEntry is not null) {
                Process wrapper = request.WrapperEntry;
                running = running with { WrapperProcess = new(wrapper.Id, wrapper.StartTime.ToUniversalTime().Ticks, ObserveProcessExecutable(wrapper)) };
            }
            MmcDurableFiles.WriteRecord(Path.Combine(directory, "Running.record"), running);
            checkpoint?.Invoke(MmcSessionCheckpoint.RunningPersisted);
            return new(receipt, running, hash, process, pin,request.RuntimeAuthority);
        } catch {
            if (durableIntent) pin.KeepForRecovery();
            else pin.CompleteVerifiedExit();
            process?.Dispose(); pin.Dispose(); throw;
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
        string hash, Process process, IMmcJavaSessionPin pin, Action<MmcSessionCheckpoint>? checkpoint,MmcMutableRuntimeAuthority? runtimeAuthority=null) {
        lease.AssertHeld();
        if (lease.InstanceId != receipt.InstanceId || !string.Equals(lease.VersionRoot, receipt.CanonicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("会话退出与准入身份不一致。");
        if (!process.HasExited) throw new IOException("受管理进程尚未退出，不能释放运行租约。");
        if (process.Id != registration.ProcessId || process.StartTime.ToUniversalTime().Ticks != registration.CreationTimeUtcTicks) throw new IOException("进程句柄身份不一致。");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(lease.VersionRoot); string directory = SessionDirectory(paths, receipt.SessionId);
        var persisted = ReadLaunching(lease, paths, receipt.SessionId, directory);
        if (persisted.Hash != hash || ReadRunning(receipt, hash, directory) != registration) throw new IOException("会话收据与运行租约不一致。");
        WriteExit(directory, receipt, hash, registration.WrapperProcess is null ? MmcSessionExitProof.OwnedProcessHandleExited : MmcSessionExitProof.WrapperManagedExit); checkpoint?.Invoke(MmcSessionCheckpoint.ExitedPersisted);
        MmcSessionRuntimeDirectories.CompleteVerifiedExit(paths, receipt,runtimeAuthority);
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
            !Path.IsPathFullyQualified(receipt.JavaIdentity.ExecutablePath) || !Path.IsPathFullyQualified(receipt.LauncherIdentity.ExecutablePath)) throw new IOException("会话收据版本、身份或内容无效。");
        return (receipt, hash);
    }
    private static MmcSessionProcessRegistration? ReadRunning(MmcSessionReceipt receipt, string hash, string directory) {
        string path = Path.Combine(directory, "Running.record"); if (MmcInstancePaths.Attributes(path) is null) return null;
        var registration = MmcDurableFiles.ReadRecord<MmcSessionProcessRegistration>(path).Value;
        if (registration.SchemaVersion != 1 || registration.SessionId != receipt.SessionId || registration.LaunchingSha256 != hash ||
            registration.ProcessId <= 0 || registration.CreationTimeUtcTicks <= 0 || registration.ExecutableIdentity is null || !SameExecutable(registration.ExecutableIdentity, receipt.JavaIdentity))
            throw new IOException("运行进程登记与收据不一致。");
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
    private readonly string hash;
    private readonly IMmcJavaSessionPin pin;
    private readonly MmcMutableRuntimeAuthority? runtimeAuthority;
    private bool complete, disposed;
    internal MmcRunningSession(MmcSessionReceipt receipt, MmcSessionProcessRegistration registration, string hash, Process process, IMmcJavaSessionPin pin,MmcMutableRuntimeAuthority? runtimeAuthority=null) {
        Receipt = receipt; Registration = registration; this.hash = hash; Process = process; this.pin = pin;this.runtimeAuthority=runtimeAuthority;
    }
    public void CompleteVerifiedExit(MmcAdmissionLease lease, Action<MmcSessionCheckpoint>? checkpoint = null) {
        if (disposed) throw new ObjectDisposedException(nameof(MmcRunningSession));
        if (complete) return;
        MmcSessions.CompleteOwnedExit(lease, Receipt, Registration, hash, Process, pin, checkpoint,runtimeAuthority); complete = true;
    }
    public void Dispose() {
        if (disposed) return;
        if (!complete) pin.KeepForRecovery();
        pin.Dispose(); Process.Dispose(); disposed = true;
    }
}
