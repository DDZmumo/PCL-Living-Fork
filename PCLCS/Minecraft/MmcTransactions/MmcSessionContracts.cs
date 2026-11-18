using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed record MmcExecutableIdentity(string ExecutablePath, string Sha256);
public enum MmcMutableRuntimeLayout { LocalAppDataV2 = 1 }
public sealed record MmcSessionRuntimeBinding(string BoundPlanFingerprint) {
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MmcMutableRuntimeLayout? MutableLayout { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorityFingerprint { get; init; }
}
public sealed record MmcMutableRuntimePaths(string SessionRoot,string TemporaryDirectory,string ExtractDirectory,MmcSessionRuntimeBinding RuntimeBinding);
public sealed record MmcSessionLaunchRequest(Guid InstanceId, long DefinitionGeneration, string RuntimeFingerprint,
    MmcExecutableIdentity JavaIdentity, MmcExecutableIdentity LauncherIdentity, ProcessStartInfo StartInfo, string GameRootLeaf = "minecraft") {
    /// <summary>Allocate once before building mutable runtime paths; null retains the legacy fixture allocation.</summary>
    public Guid? SessionId { get; init; }
    public MmcSessionRuntimeBinding? RuntimeBinding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public MmcMutableRuntimeAuthority? RuntimeAuthority { get; init; }
    /// <summary>True when a package <c>WrapperCommand</c> owns the entry process and spawns Java as a child.</summary>
    public bool IsWrapperManaged { get; init; }
    /// <summary>Expected executable identity of the wrapper entry process when <see cref="IsWrapperManaged"/> is true.</summary>
    public MmcExecutableIdentity? WrapperIdentity { get; init; }
    /// <summary>Zero-based slot in <see cref="ProcessStartInfo.ArgumentList"/> where the session nonce is inserted.</summary>
    public int NonceArgumentIndex { get; init; }
    /// <summary>Maximum time to wait for the wrapper process to spawn the managed Java child carrying the session nonce.</summary>
    public TimeSpan WrapperJavaDiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional in-memory nonce probe override; defaults to Windows PEB command-line inspection during wrapper launch.</summary>
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Func<int, string, bool?>? NonceProbe { get; init; }
}
public sealed record MmcSessionReceipt(int SchemaVersion, Guid SessionId, Guid InstanceId, string CanonicalRoot,
    long DefinitionGeneration, string RuntimeFingerprint, string Nonce, MmcExecutableIdentity JavaIdentity,
    MmcExecutableIdentity LauncherIdentity) {
    public MmcSessionRuntimeBinding? RuntimeBinding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MmcExecutableIdentity? WrapperIdentity { get; init; }
}
public sealed record MmcSessionProcessRegistration(int SchemaVersion, Guid SessionId, string LaunchingSha256,
    int ProcessId, long CreationTimeUtcTicks, MmcExecutableIdentity ExecutableIdentity) {
    /// <summary>
    /// The package wrapper entry process, registered alongside the managed Java process when a
    /// <c>WrapperCommand</c> is active. It is a second identity only: exit is never concluded from it
    /// (contract §4.1/§4.3).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MmcSessionProcessIdentity? WrapperProcess { get; init; }
}
/// <summary>One registered managed process identity. Creation time defeats PID reuse; the image defeats another java.exe.</summary>
public sealed record MmcSessionProcessIdentity(int ProcessId, long CreationTimeUtcTicks, MmcExecutableIdentity ExecutableIdentity);
internal sealed record MmcSessionExitRecord(int SchemaVersion, Guid SessionId, string LaunchingSha256, string Proof);
public enum MmcSessionObservation { MatchingRunning, ProvenExited, Unknown }
public sealed record MmcSessionResult(Guid SessionId, MmcSessionObservation Observation, string? Diagnostic = null);

/// <summary>
/// Host Java-service contract. Acquisition is inside instance admission and before final Java validation.
/// Implementations coordinate with installers/cleaners and retain durable pins after host death/uncertainty.
/// Dispose alone MUST NOT unpin an unconfirmed process. No implementation is silently assumed here.
/// </summary>
public interface IMmcJavaSessionPin : IDisposable {
    MmcExecutableIdentity Identity { get; }
    void KeepForRecovery();
    void CompleteVerifiedExit();
}

/// <summary>Trusted host process observation. Unknown access, PID reuse or missing nonce evidence is Unknown.</summary>
public interface IMmcSessionProcessInspector {
    MmcSessionObservation Inspect(MmcSessionReceipt receipt, MmcSessionProcessRegistration? registration);
}

/// <summary>
/// The closed set of exit proofs (contract D7). <c>OwnedProcessHandleExited</c> is the handle-level proof for a
/// directly owned process, <c>TrustedHostProcessObservation</c> is the conservative host observation, and
/// <c>WrapperManagedExit</c> is the wrapper-mode proof where the managed Java process — not the wrapper entry —
/// was shown to be gone. The wrapper value exists because reusing the handle proof would make "the wrapper
/// exited first" mean "the game exited", which the contract forbids.
/// </summary>
public static class MmcSessionExitProof {
    public const string OwnedProcessHandleExited = "OwnedProcessHandleExited";
    public const string TrustedHostProcessObservation = "TrustedHostProcessObservation";
    public const string WrapperManagedExit = "WrapperManagedExit";
    internal static bool IsKnown(string proof) => proof is OwnedProcessHandleExited or TrustedHostProcessObservation or WrapperManagedExit;
}

/// <summary>Conservative OS identity checks. Command contents are consumed only by the supplied in-memory nonce probe.</summary>
public sealed class MmcSessionProcessInspector(Func<int, string, bool?>? nonceProbe = null) : IMmcSessionProcessInspector {
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    public MmcSessionObservation Inspect(MmcSessionReceipt receipt, MmcSessionProcessRegistration? registration) {
        if (registration is null || registration.ProcessId <= 0 || registration.CreationTimeUtcTicks <= 0 || registration.SessionId != receipt.SessionId)
            return MmcSessionObservation.Unknown;
        if (registration.WrapperProcess is not null)
            return InspectWrapperSession(receipt, registration, registration.WrapperProcess);
        if (receipt.WrapperIdentity is not null)
            return MmcSessionObservation.Unknown;
        Process process;
        try { process = Process.GetProcessById(registration.ProcessId); }
        catch (ArgumentException) { return MmcSessionObservation.ProvenExited; }
        try {
            if (process.HasExited) return MmcSessionObservation.ProvenExited;
            if (process.StartTime.ToUniversalTime().Ticks != registration.CreationTimeUtcTicks) return MmcSessionObservation.Unknown;
            MmcExecutableIdentity observed = MmcSessions.ObserveProcessExecutable(process);
            if (!MmcSessions.SameExecutable(observed, receipt.JavaIdentity) || !MmcSessions.SameExecutable(observed, registration.ExecutableIdentity))
                return MmcSessionObservation.Unknown;
            return nonceProbe?.Invoke(registration.ProcessId, receipt.Nonce) == true ? MmcSessionObservation.MatchingRunning : MmcSessionObservation.Unknown;
        } catch (Exception ex) when (IsInspectionFailure(ex)) {
            return MmcSessionObservation.Unknown;
        } finally { process.Dispose(); }
    }

    /// <summary>
    /// Reads the target process's in-memory command line on Windows via
    /// <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> and checks for the exact session nonce
    /// token without persisting or logging the command line. Returns null when unobservable (e.g. access denied
    /// or process gone).
    /// </summary>
    public static bool? TryProcessCommandLineNonce(int pid, string nonce) {
        if (!OperatingSystem.IsWindows() || pid <= 0 || string.IsNullOrEmpty(nonce)) return null;
        string expectedToken = MmcSessions.NonceArgumentPrefix + nonce;
        using SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;

        int bufferSize = 512;
        for (int attempt = 0; attempt < 4; attempt++) {
            nint buffer = Marshal.AllocHGlobal(bufferSize);
            try {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, (uint)bufferSize, out uint requiredLength);
                if (status == 0) {
                    var text = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                    if (text.Length == 0) return false;
                    if ((text.Length & 1) != 0) return null;
                    long baseAddr = buffer.ToInt64();
                    long strAddr = text.Buffer.ToInt64();
                    if (strAddr < baseAddr || strAddr + text.Length > baseAddr + bufferSize) return null;
                    string? commandLine = Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                    return commandLine is not null && HasExactCommandLineToken(commandLine, expectedToken);
                }
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow && requiredLength > bufferSize && requiredLength <= 1024 * 1024) {
                    bufferSize = checked((int)requiredLength);
                    continue;
                }
                return null;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private MmcSessionObservation InspectWrapperSession(MmcSessionReceipt receipt, MmcSessionProcessRegistration registration,
        MmcSessionProcessIdentity wrapperRegistration) {
        if (wrapperRegistration.ProcessId <= 0 || wrapperRegistration.CreationTimeUtcTicks <= 0 || wrapperRegistration.ExecutableIdentity is null)
            return MmcSessionObservation.Unknown;
        if (receipt.WrapperIdentity is not null && !MmcSessions.SameExecutable(wrapperRegistration.ExecutableIdentity, receipt.WrapperIdentity))
            return MmcSessionObservation.Unknown;

        var (wrapperState, wrapperIdentityMatches) = InspectWrapperEntry(wrapperRegistration, receipt.WrapperIdentity);
        var (javaState, javaIdentityMatches, registeredJavaAliveWithNonce) = InspectManagedJava(receipt, registration);

        if (!wrapperIdentityMatches || !javaIdentityMatches)
            return MmcSessionObservation.Unknown;

        if (javaState == MmcManagedJavaState.Alive) {
            // Confirming MatchingRunning requires verified nonce ownership (parity with unwrapped Inspector).
            if (registeredJavaAliveWithNonce != true) return MmcSessionObservation.Unknown;
            return MmcWrapperExit.Decide(new(wrapperState, MmcManagedJavaState.Alive, WrapperIdentityMatches: true, NonceMatchesJvm: true, ManagedJavaIdentityMatches: true)).Observation;
        }

        if (javaState == MmcManagedJavaState.Unobservable)
            return MmcWrapperExit.Decide(new(wrapperState, MmcManagedJavaState.Unobservable, WrapperIdentityMatches: true, NonceMatchesJvm: null, ManagedJavaIdentityMatches: true)).Observation;

        // ManagedJavaState.Absent: inspect whether any live matching Java process still carries this session's nonce.
        bool? nonceMatchesJvm = nonceProbe is null ? null : ScanLiveMatchingJavaNonce(receipt.JavaIdentity, receipt.Nonce, nonceProbe);
        return MmcWrapperExit.Decide(new(wrapperState, MmcManagedJavaState.Absent, WrapperIdentityMatches: true, nonceMatchesJvm, ManagedJavaIdentityMatches: true)).Observation;
    }

    private static (MmcWrapperEntryState State, bool IdentityMatches) InspectWrapperEntry(
        MmcSessionProcessIdentity wrapperRegistration, MmcExecutableIdentity? receiptWrapperIdentity) {
        Process process;
        try { process = Process.GetProcessById(wrapperRegistration.ProcessId); }
        catch (ArgumentException) { return (MmcWrapperEntryState.Exited, true); }
        try {
            if (process.HasExited) return (MmcWrapperEntryState.Exited, true);
            if (process.StartTime.ToUniversalTime().Ticks != wrapperRegistration.CreationTimeUtcTicks)
                return (MmcWrapperEntryState.Alive, false);
            MmcExecutableIdentity observed = MmcSessions.ObserveProcessExecutable(process);
            bool matches = MmcSessions.SameExecutable(observed, wrapperRegistration.ExecutableIdentity) &&
                           (receiptWrapperIdentity is null || MmcSessions.SameExecutable(observed, receiptWrapperIdentity));
            return (MmcWrapperEntryState.Alive, matches);
        } catch (Exception ex) when (IsInspectionFailure(ex)) {
            return (MmcWrapperEntryState.Unobservable, true);
        } finally { process.Dispose(); }
    }

    private (MmcManagedJavaState State, bool IdentityMatches, bool? NonceMatches) InspectManagedJava(
        MmcSessionReceipt receipt, MmcSessionProcessRegistration registration) {
        Process process;
        try { process = Process.GetProcessById(registration.ProcessId); }
        catch (ArgumentException) { return (MmcManagedJavaState.Absent, true, null); }
        try {
            if (process.HasExited) return (MmcManagedJavaState.Absent, true, null);
            if (process.StartTime.ToUniversalTime().Ticks != registration.CreationTimeUtcTicks)
                return (MmcManagedJavaState.Alive, false, null);
            MmcExecutableIdentity observed = MmcSessions.ObserveProcessExecutable(process);
            if (!MmcSessions.SameExecutable(observed, receipt.JavaIdentity) || !MmcSessions.SameExecutable(observed, registration.ExecutableIdentity))
                return (MmcManagedJavaState.Alive, false, null);
            bool? nonceMatches = nonceProbe?.Invoke(registration.ProcessId, receipt.Nonce);
            return (MmcManagedJavaState.Alive, true, nonceMatches);
        } catch (Exception ex) when (IsInspectionFailure(ex)) {
            return (MmcManagedJavaState.Unobservable, true, null);
        } finally { process.Dispose(); }
    }

    private static bool? ScanLiveMatchingJavaNonce(MmcExecutableIdentity javaIdentity, string nonce, Func<int, string, bool?> probe) {
        string? targetProcessName = Path.GetFileNameWithoutExtension(javaIdentity.ExecutablePath);
        if (string.IsNullOrEmpty(targetProcessName)) return null;
        bool anyUnobservable = false;
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(targetProcessName); }
        catch (Exception ex) when (IsInspectionFailure(ex)) { return null; }

        foreach (Process candidate in candidates) {
            using (candidate) {
                try {
                    if (candidate.Id <= 4 || candidate.HasExited) continue;
                    MmcExecutableIdentity observed = MmcSessions.ObserveProcessExecutable(candidate);
                    if (!MmcSessions.SameExecutable(observed, javaIdentity)) continue;
                    bool? match = probe(candidate.Id, nonce);
                    if (match == true) return true;
                    if (match is null) {
                        if (!candidate.HasExited) anyUnobservable = true;
                    }
                } catch (ArgumentException) {
                    // Process exited during inspection.
                } catch (InvalidOperationException) {
                    // Process exited before property read completed.
                } catch (Exception ex) when (IsInspectionFailure(ex)) {
                    anyUnobservable = true;
                }
            }
        }
        return anyUnobservable ? null : false;
    }

    private static bool HasExactCommandLineToken(string commandLine, string expectedToken) {
        int i = 0, len = commandLine.Length;
        StringBuilder token = new();
        while (i < len) {
            while (i < len && char.IsWhiteSpace(commandLine[i])) i++;
            if (i >= len) break;
            token.Clear();
            bool inQuotes = false;
            while (i < len) {
                char c = commandLine[i];
                if (!inQuotes && char.IsWhiteSpace(c)) break;
                if (c == '\\') {
                    int slashes = 0;
                    while (i < len && commandLine[i] == '\\') { slashes++; i++; }
                    if (i < len && commandLine[i] == '"') {
                        token.Append('\\', slashes / 2);
                        if ((slashes & 1) == 1) { token.Append('"'); i++; continue; }
                        inQuotes = !inQuotes; i++;
                        continue;
                    }
                    token.Append('\\', slashes);
                    continue;
                }
                if (c == '"') {
                    if (inQuotes && i + 1 < len && commandLine[i + 1] == '"') {
                        token.Append('"'); i += 2; continue;
                    }
                    inQuotes = !inQuotes; i++;
                    continue;
                }
                token.Append(c);
                i++;
            }
            if (string.Equals(token.ToString(), expectedToken, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsInspectionFailure(Exception ex) =>
        ex is ArgumentException or Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass,
        nint processInformation, uint processInformationLength, out uint returnLength);
}
