using System;
using System.Diagnostics;

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
    /// <summary>
    /// Host-created package wrapper entry process, when a <c>WrapperCommand</c> is active. The host creates it
    /// (never Core), and it is registered as a second identity so that its exit is never mistaken for the
    /// game's exit. It is a live handle, so it is never serialized into a receipt.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Process? WrapperEntry { get; init; }
}
public sealed record MmcSessionReceipt(int SchemaVersion, Guid SessionId, Guid InstanceId, string CanonicalRoot,
    long DefinitionGeneration, string RuntimeFingerprint, string Nonce, MmcExecutableIdentity JavaIdentity,
    MmcExecutableIdentity LauncherIdentity) {
    public MmcSessionRuntimeBinding? RuntimeBinding { get; init; }
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
    public MmcSessionObservation Inspect(MmcSessionReceipt receipt, MmcSessionProcessRegistration? registration) {
        if (registration is null || registration.ProcessId <= 0 || registration.CreationTimeUtcTicks <= 0 || registration.SessionId != receipt.SessionId)
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
        } catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException) {
            return MmcSessionObservation.Unknown;
        } finally { process.Dispose(); }
    }
}
