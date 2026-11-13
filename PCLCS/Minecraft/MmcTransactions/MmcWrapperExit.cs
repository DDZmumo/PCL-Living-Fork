using System;
using System.Diagnostics;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>
/// The observation facts the wrapper exit table (contract §4.3) decides on. They are deliberately coarse
/// values, not a live process handle: the caller that can actually observe the OS produces them, and the
/// decision below is then a pure function of them.
/// </summary>
/// <param name="WrapperEntryState">Whether the package wrapper entry process is still alive.</param>
/// <param name="ManagedJavaState">Whether the managed Java process is alive, gone, or unobservable.</param>
/// <param name="WrapperIdentityMatches">Whether the observed wrapper image and creation time still match the registration.</param>
/// <param name="NonceMatchesJvm">Whether a live process carrying this session's nonce was found. Null means unobservable.</param>
/// <param name="ManagedJavaIdentityMatches">Whether the observed managed Java image/creation time match the registration.</param>
public sealed record MmcWrapperExitFacts(
    MmcWrapperEntryState WrapperEntryState,
    MmcManagedJavaState ManagedJavaState,
    bool WrapperIdentityMatches,
    bool? NonceMatchesJvm,
    bool ManagedJavaIdentityMatches);

public enum MmcWrapperEntryState { Alive, Exited, Unobservable }
public enum MmcManagedJavaState { Alive, Absent, Unobservable }

/// <summary>
/// The wrapper-mode exit decision and its resulting action (contract §4.3).
/// </summary>
/// <param name="Observation">What the session state is.</param>
/// <param name="Diagnostic">A diagnostic to record, or null.</param>
/// <param name="MayConcludeExit">Whether writing the exit receipt and releasing the run lease is permitted.</param>
/// <param name="MayTerminate">Whether terminating a process is permitted.</param>
public sealed record MmcWrapperExitVerdict(MmcSessionObservation Observation, string? Diagnostic, bool MayConcludeExit, bool MayTerminate);

/// <summary>
/// Decides whether a wrapper-managed launch has ended. This is the single place the phase-two rule
/// "the wrapper exiting first does not mean the game exited" is enforced, so it is exercised directly by
/// controlled fixtures instead of only through a live launcher.
///
/// The wrapper entry process is never exit evidence: only the managed Java process is. Every row below
/// corresponds to the contract's §4.3 table, and any fact that cannot be established keeps the session
/// <see cref="MmcSessionObservation.Unknown"/> with its receipt and Java pin retained (F-10).
/// </summary>
public static class MmcWrapperExit {
    public static MmcWrapperExitVerdict Decide(MmcWrapperExitFacts facts) {
        ArgumentNullException.ThrowIfNull(facts);
        // An unrelated process, or a wrapper whose identity no longer matches its registration (PID reuse),
        // can never justify a verdict about this session.
        if (!facts.WrapperIdentityMatches)
            return Unknown("包装程序镜像或创建时间与登记不符（可能发生 PID 重用），保持只读待确认。");
        if (!facts.ManagedJavaIdentityMatches)
            return Unknown("受管 Java 进程镜像或创建时间与登记不符（可能发生 PID 重用），保持只读待确认。");
        switch (facts.ManagedJavaState) {
            case MmcManagedJavaState.Alive:
                // The decisive row: the wrapper may already be gone while the game is still running. Nothing
                // here may write an exit receipt, release the lease or terminate anything.
                return facts.WrapperEntryState == MmcWrapperEntryState.Exited
                    ? new(MmcSessionObservation.MatchingRunning,
                        "包装程序已退出，但受管 Java 进程仍存活，本次会话仍在运行。", false, false)
                    : new(MmcSessionObservation.MatchingRunning, null, false, false);
            case MmcManagedJavaState.Absent:
                // Only a nonce-confirmed observation can turn "not found" into "proven gone": without that
                // evidence the absence may be an access failure or a different process.
                if (facts.NonceMatchesJvm is null)
                    return Unknown("受管 Java 进程是否携带本次会话 nonce 无法观察，不能断言已退出。");
                if (facts.NonceMatchesJvm == true)
                    return Unknown("登记进程已不存在，但仍有进程携带本次会话 nonce，不能断言已退出。");
                return new(MmcSessionObservation.ProvenExited, null, true, false);
            default:
                return Unknown("受管 Java 进程无法观察（权限或身份不符），保持只读待确认。");
        }
    }

    /// <summary>
    /// The proof value a wrapper-mode exit must be recorded under. It is never the handle-level proof, because
    /// the handle in wrapper mode belongs to the wrapper entry: reusing it would record "the wrapper exited" as
    /// "the game exited".
    /// </summary>
    public static string ProofFor(bool wrapperManaged) =>
        wrapperManaged ? MmcSessionExitProof.WrapperManagedExit : MmcSessionExitProof.OwnedProcessHandleExited;

    /// <summary>
    /// Whether a completed wrapper-mode exit may be concluded from the wrapper's own handle. It never may: the
    /// caller must have a managed-Java verdict.
    /// </summary>
    public static bool HandleExitIsSufficient(bool wrapperManaged) => !wrapperManaged;

    private static MmcWrapperExitVerdict Unknown(string diagnostic) =>
        new(MmcSessionObservation.Unknown, diagnostic, false, false);
}

/// <summary>
/// Bounded teardown for a whole managed process group (contract §4.2). Termination is followed by a bounded
/// wait and a re-proof: "Kill was sent" is never accepted as the proof.
/// </summary>
public static class MmcProcessTreeTermination {
    /// <summary>Bounded cancellation grace taken from the existing exit-confirmation retry interval.</summary>
    public const int GraceMilliseconds = 3000;
    private const int PollMilliseconds = 25;

    /// <summary>
    /// Terminates every member of the managed group and returns whether the group is provably gone within the
    /// grace window. The wrapper entry and the managed Java process are both members, so neither may be left out.
    /// </summary>
    public static bool TerminateAndProveGone(Process? wrapperEntry, Process? managedJava, int graceMilliseconds = GraceMilliseconds) {
        Kill(wrapperEntry); Kill(managedJava);
        long until = Environment.TickCount64 + Math.Max(0, graceMilliseconds);
        do {
            bool wrapperGone = wrapperEntry is null || wrapperEntry.HasExited;
            bool javaGone = managedJava is null || managedJava.HasExited;
            if (wrapperGone && javaGone) return true;
            Thread.Sleep(PollMilliseconds);
        } while (Environment.TickCount64 < until);
        return (wrapperEntry is null || wrapperEntry.HasExited) && (managedJava is null || managedJava.HasExited);
    }

    private static void Kill(Process? process) {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The exact bounded-wait span the contract fixes, exposed so a fixture can assert it.</summary>
    public static long Deadline(long startTicks) => startTicks + GraceMilliseconds;
}
