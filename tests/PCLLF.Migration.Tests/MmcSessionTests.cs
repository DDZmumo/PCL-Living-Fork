using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PCLCS;

internal static partial class MmcSessionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC session final validation and creation stay inside admission with one protected argv nonce", LaunchAndExit);
        yield return ("MMC session active receipt blocks structural transactions and another launch", StructuralGate);
        yield return ("MMC session active Dispose keeps durable uncertainty and Java pin", DisposeKeepsProtection);
        yield return ("MMC session PID reuse and absent nonce evidence remain unknown", IdentityAndNonce);
        yield return ("MMC wrapper exit never concludes the game exited while managed Java survives", WrapperExitTable);
        yield return ("MMC wrapper exit registers both identities and proves the whole group gone", WrapperGroup);
        yield return ("MMC wrapper early exit retains live pipes, refuses premature settlement and reconciles managed Java", WrapperEarlyExitAndReconcile);
        yield return ("MMC wrapper identity mismatch and early exit without Java clean up without leaking pins", WrapperFailureCleanup);
        yield return ("MMC session interrupted Launching without PID cannot be guessed exited", LaunchingInterruption);
        yield return ("MMC session rejects stale generation layout and caller nonce overrides", InvalidLaunch);
        yield return ("MMC session serialized receipts contain identities but no process arguments", ReceiptPrivacy);
        yield return ("MMC session host termination retains running receipt and blocks structural writes", KilledHost);
        yield return ("MMC unlaunched mutable runtime cleanup validates ownership and preserves receipts", AbandonMutable);
        yield return ("MMC mutable cleanup failure retains pin and exit evidence for reconciliation retry", RetryMutableCleanup);
        yield return ("MMC deep mutable scopes use a trusted short authority and preserve unknown owners", ShortMutableIsolation);
        yield return ("MMC short mutable cleanup releases pins only after confirmed exit and successful owned cleanup", ShortMutableExit);
        yield return ("MMC short mutable host death retains runtime binding and blocks writes until reconciliation", ShortMutableHostDeath);
        if (OperatingSystem.IsWindows()) yield return ("MMC observes a suspended child image before its module list initializes", SuspendedImage);
    }

    internal static bool TryRunChild(string[] args, out int exitCode) {
        exitCode = 0;
        if(TryRunShortHostChild(args,out exitCode))return true;
        string? nonce = args.FirstOrDefault(a => a.StartsWith(MmcSessions.NonceArgumentPrefix, StringComparison.Ordinal))?[MmcSessions.NonceArgumentPrefix.Length..];
        string[] data = args.Where(a => !a.StartsWith(MmcSessions.NonceArgumentPrefix, StringComparison.Ordinal)).ToArray();
        if (data.Length == 0 || data[0] is not ("--mmc-session-child" or "--mmc-session-host-child")) return false;
        try {
            if (data[0] == "--mmc-session-child") {
                if (data.Length < 4 || nonce is null) throw new Exception("Controlled child has no protected nonce.");
                File.WriteAllText(data[1], Environment.ProcessId + "\n" + nonce);
                var timer = Stopwatch.StartNew();
                while (!File.Exists(data[2]) && timer.ElapsedMilliseconds < 30000) Thread.Sleep(20);
                if (data.Length >= 5 && data[4] == "--emit-stdout") Console.WriteLine("child-stdout-after-stop");
                exitCode = 0; return true;
            }
            if (data.Length != 3 || !Guid.TryParse(data[2], out Guid id)) throw new Exception("Invalid controlled host child.");
            using Fixture fixture = new(data[1], id, initialize: false);
            using var lease = fixture.Lease();
            using var session = MmcSessions.Launch(lease, fixture.Request(), () => { }, (_, image) => new FixturePin(image, fixture.PinFile));
            File.WriteAllText(fixture.HostReady, "ready");
            // Leave admission after registration exactly as a real monitor does.
            lease.Dispose(); Thread.Sleep(Timeout.Infinite);
        } catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 20; }
        return true;
    }

    private static void LaunchAndExit() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); FixturePin? pin = null;
        Guid plannedSession = Guid.NewGuid();
        var binding = new MmcSessionRuntimeBinding(new string('b', 64));
        string mutable = CreateMutable(fixture.Root, plannedSession, binding);
        var request = fixture.Request() with { SessionId = plannedSession, RuntimeBinding = binding };
        using var session = MmcSessions.Launch(lease, request, () => Equal(true, pin is not null), (_, image) => pin = new(image, fixture.PinFile));
        Equal(plannedSession, session.Receipt.SessionId);
        fixture.WaitChild();
        Equal(session.Receipt.Nonce, fixture.ChildNonce());
        Throws<IOException>(() => session.CompleteVerifiedExit(lease));
        fixture.Stop(); session.Process.WaitForExit(10000); session.CompleteVerifiedExit(lease);
        Equal(true, pin!.Completed); Equal(false, File.Exists(fixture.PinFile)); MmcSessions.AssertNoActive(lease);
        Equal(false, Directory.Exists(mutable));
        string retired = Path.Combine(fixture.Root, "PCL", "MmcRuntime", ".session-history", plannedSession.ToString("N"));
        Equal(1, Directory.GetFileSystemEntries(retired).Length);
        Equal("owner.txt", Path.GetFileName(Directory.GetFileSystemEntries(retired).Single()));
        Throws<IOException>(() => MmcSessions.Launch(lease, request, () => { }, (_, _) => throw new Exception("Reused session reached pin acquisition.")));
    }
    private static void StructuralGate() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        using var session = MmcSessions.Launch(lease, fixture.Request(), () => { }, (_, image) => new FixturePin(image, fixture.PinFile));
        fixture.WaitChild();
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, fixture.TransactionRequest()));
        Throws<IOException>(() => MmcSessions.Launch(lease, fixture.Request(), () => { }, (_, image) => new FixturePin(image, fixture.PinFile)));
        fixture.Stop(); session.Process.WaitForExit(10000); session.CompleteVerifiedExit(lease);
    }
    private static void DisposeKeepsProtection() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); FixturePin? pin = null;
        Guid planned = Guid.NewGuid(); var binding = new MmcSessionRuntimeBinding(new string('e', 64));
        string mutable = CreateMutable(fixture.Root, planned, binding);
        var session = MmcSessions.Launch(lease, fixture.Request() with { SessionId = planned, RuntimeBinding = binding }, () => { }, (_, image) => pin = new(image, fixture.PinFile));
        fixture.WaitChild(); var id = session.Receipt.SessionId; session.Dispose();
        Equal(true, pin!.Retained); Equal(true, File.Exists(fixture.PinFile));
        Throws<IOException>(() => MmcSessions.AssertNoActive(lease));
        Equal(MmcSessionObservation.Unknown, MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => pin.CompleteVerifiedExit()).Observation);
        Equal(true, File.Exists(Path.Combine(mutable, "temp", "native.dll")));
        fixture.StopAndWait();
        Equal(MmcSessionObservation.ProvenExited, MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => pin.CompleteVerifiedExit()).Observation);
        Equal(false, Directory.Exists(mutable));
        MmcSessions.AssertNoActive(lease);
    }
    private static void IdentityAndNonce() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        using var session = MmcSessions.Launch(lease, fixture.Request(), () => { }, (_, image) => new FixturePin(image, fixture.PinFile)); fixture.WaitChild();
        try {
        IMmcSessionProcessInspector inspector = new MmcSessionProcessInspector((pid, nonce) => pid == fixture.ChildPid() && nonce == fixture.ChildNonce());
        Equal(MmcSessionObservation.MatchingRunning, inspector.Inspect(session.Receipt, session.Registration));
        Equal(MmcSessionObservation.Unknown, inspector.Inspect(session.Receipt, session.Registration with { CreationTimeUtcTicks = session.Registration.CreationTimeUtcTicks + 1 }));
        Equal(MmcSessionObservation.Unknown, inspector.Inspect(session.Receipt, null));
        Equal(MmcSessionObservation.Unknown, new MmcSessionProcessInspector().Inspect(session.Receipt, session.Registration));
        fixture.Stop(); session.Process.WaitForExit(10000); session.CompleteVerifiedExit(lease);
        } catch (Exception error) { Console.Error.WriteLine("DIAG session inspector primary failure before fixture disposal: " + error); throw; }
    }
    // Contract §4.3. The wrapper entry process is never exit evidence; only the managed Java process is.
    private static void WrapperExitTable() {
        // The decisive row: the wrapper exited first but managed Java is proven alive. No exit, no teardown.
        var alive = MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Alive, true, true, true));
        Equal(MmcSessionObservation.MatchingRunning, alive.Observation);
        Equal(false, alive.MayConcludeExit); Equal(false, alive.MayTerminate);
        Equal(true, alive.Diagnostic is not null);
        // Wrapper alive and Java alive is an ordinary running session.
        Equal(MmcSessionObservation.MatchingRunning, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Alive, MmcManagedJavaState.Alive, true, true, true)).Observation);
        // Wrapper exited and Java proven absent (nonce observed not to survive) is the only proven exit.
        var gone = MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Absent, true, false, true));
        Equal(MmcSessionObservation.ProvenExited, gone.Observation); Equal(true, gone.MayConcludeExit);
        // Absent Java with an unobservable nonce cannot be concluded; the receipt and pin are retained.
        Equal(MmcSessionObservation.Unknown, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Absent, true, null, true)).Observation);
        // A live process still carrying this session's nonce means the managed Java is not gone.
        var nonceAlive = MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Absent, true, true, true));
        Equal(MmcSessionObservation.Unknown, nonceAlive.Observation); Equal(false, nonceAlive.MayConcludeExit);
        // PID reuse (creation time/image mismatch) is never a verdict, whichever process it is found on.
        Equal(MmcSessionObservation.Unknown, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Alive, MmcManagedJavaState.Alive, false, true, true)).Observation);
        Equal(MmcSessionObservation.Unknown, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Alive, true, true, false)).Observation);
        // Unreadable process information stays Unknown rather than being read as "exited".
        Equal(MmcSessionObservation.Unknown, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Unobservable, MmcManagedJavaState.Unobservable, true, null, true)).Observation);
        // Every Unknown verdict withholds both conclusion and termination (F-10).
        foreach (var facts in new[] {
            new MmcWrapperExitFacts(MmcWrapperEntryState.Alive, MmcManagedJavaState.Unobservable, true, null, true),
            new MmcWrapperExitFacts(MmcWrapperEntryState.Unobservable, MmcManagedJavaState.Absent, true, null, true),
            new MmcWrapperExitFacts(MmcWrapperEntryState.Exited, MmcManagedJavaState.Absent, true, null, true)
        }) { var verdict = MmcWrapperExit.Decide(facts); Equal(false, verdict.MayConcludeExit); Equal(false, verdict.MayTerminate); }
        // The handle-level proof belongs to the wrapper entry in wrapper mode, so it can never be reused there.
        Equal(false, MmcWrapperExit.HandleExitIsSufficient(wrapperManaged: true));
        Equal(true, MmcWrapperExit.HandleExitIsSufficient(wrapperManaged: false));
        Equal("WrapperManagedExit", MmcWrapperExit.ProofFor(wrapperManaged: true));
        Equal("OwnedProcessHandleExited", MmcWrapperExit.ProofFor(wrapperManaged: false));
    }
    // The real group proof: MmcSessions launches the outer wrapper in a suspended owned Win32 Job, validates
    // its image before resume, discovers the managed child carrying the protected nonce inside the Job,
    // registers both identities, and terminates and proves the whole group gone.
    private static void WrapperGroup() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        string workingDirectory = Path.Combine(fixture.Root, "minecraft");
        string wrapperPath = MmcSessions.ResolveExecutablePath("cmd.exe", workingDirectory);
        MmcExecutableIdentity wrapperIdentity = MmcSessions.ObserveExecutable(wrapperPath);
        ProcessStartInfo outer = new(wrapperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        foreach (string arg in new[] { "/d", "/c", "call", AppHost(), "--mmc-session-child", fixture.Ready, fixture.StopFile, "bounded-fixture" })
            outer.ArgumentList.Add(arg);
        var request = fixture.Request() with {
            StartInfo = outer,
            IsWrapperManaged = true,
            WrapperIdentity = wrapperIdentity,
            NonceArgumentIndex = 4
        };
        using var session = MmcSessions.Launch(lease, request, () => { }, (_, image) => new FixturePin(image, fixture.PinFile));
        fixture.WaitChild();
        var registration = session.Registration;
        Equal(true, session.IsWrapperManaged && session.HasOwnedPipes);
        Equal(true, session.WrapperProcess is { HasExited: false });
        Equal(true, registration.WrapperProcess is not null);
        Equal(true, registration.WrapperProcess!.ProcessId > 0 && registration.WrapperProcess.CreationTimeUtcTicks > 0);
        Equal(true, registration.WrapperProcess.ProcessId != registration.ProcessId);
        Equal(false, MmcSessions.SameExecutable(registration.WrapperProcess.ExecutableIdentity, registration.ExecutableIdentity));
        Equal(fixture.ChildPid(), registration.ProcessId);
        Equal(session.Receipt.Nonce, fixture.ChildNonce());
        // The durable record round-trips both identities and still carries no arguments or nonce.
        string record = Path.Combine(fixture.Sessions, session.Receipt.SessionId.ToString("N"), "Running.record");
        string text = File.ReadAllText(record);
        Equal(true, text.Contains("wrapperProcess", StringComparison.OrdinalIgnoreCase));
        Equal(false, text.Contains("bounded-fixture", StringComparison.Ordinal));
        Equal(false, text.Contains(fixture.StopFile, StringComparison.Ordinal));
        Equal(false, text.Contains(session.Receipt.Nonce, StringComparison.Ordinal));
        // Teardown terminates the whole group and proves it gone within the bounded grace.
        Equal(true, session.TerminateTree());
        Equal(true, session.WrapperProcess!.HasExited); Equal(true, session.Process.HasExited);
        Equal(true, session.AreAllProcessesExited);
        // A proven wrapper-mode exit is recorded under the wrapper proof, never the handle proof.
        session.CompleteVerifiedExit(lease);
        string history = Path.Combine(fixture.Scope, "versions", "sample", "PCL", "MmcSessionHistory", session.Receipt.SessionId.ToString("N"), "Exited.record");
        Equal(true, File.Exists(history));
        Equal(true, File.ReadAllText(history).Contains("WrapperManagedExit", StringComparison.Ordinal));
        Equal(false, File.ReadAllText(history).Contains("OwnedProcessHandleExited", StringComparison.Ordinal));
    }

    private static void WrapperEarlyExitAndReconcile() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        FixturePin? pin = null;
        string workingDirectory = Path.Combine(fixture.Root, "minecraft");
        string wrapperPath = MmcSessions.ResolveExecutablePath("cmd.exe", workingDirectory);
        MmcExecutableIdentity wrapperIdentity = MmcSessions.ObserveExecutable(wrapperPath);
        ProcessStartInfo outer = new(wrapperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        foreach (string arg in new[] { "/d", "/c", "start", "", "/b", AppHost(), "--mmc-session-child", fixture.Ready, fixture.StopFile, "bounded-fixture", "--emit-stdout" })
            outer.ArgumentList.Add(arg);
        var request = fixture.Request() with {
            StartInfo = outer,
            IsWrapperManaged = true,
            WrapperIdentity = wrapperIdentity,
            NonceArgumentIndex = 6
        };
        var stdout = new ConcurrentQueue<string>();
        Guid sessionId;
        using (var session = MmcSessions.Launch(lease, request, () => { }, (_, image) => pin = new(image, fixture.PinFile))) {
            sessionId = session.Receipt.SessionId;
            session.BeginReadPipes(line => { if (line is not null) stdout.Enqueue(line); }, _ => { });
            fixture.WaitChild();
            Equal(true, session.WrapperProcess!.WaitForExit(10000));
            Equal(false, session.Process.HasExited);
            // Wrapper exit alone must neither conclude game exit nor allow premature settlement.
            var inspector = new MmcSessionProcessInspector(MmcSessionProcessInspector.TryProcessCommandLineNonce);
            Equal(MmcSessionObservation.MatchingRunning, MmcSessions.Reconcile(lease, sessionId, inspector, _ => throw new Exception("running pin must stay")).Observation);
            Throws<IOException>(() => session.RecordVerifiedExit(lease));
            Equal(false, MmcWrapperExit.Decide(new(MmcWrapperEntryState.Exited, MmcManagedJavaState.Alive, true, true, true)).MayConcludeExit);

            fixture.Stop();
            session.WaitForExitAsync().GetAwaiter().GetResult();
            session.PipeDrainTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Equal(true, stdout.Contains("child-stdout-after-stop"));
        }
        // Disposing without CompleteVerifiedExit keeps pin & session so post-exit Reconcile must conclude WrapperManagedExit.
        Equal(true, pin!.Retained); Equal(true, File.Exists(fixture.PinFile));
        var postExit = MmcSessions.Reconcile(lease, sessionId, new MmcSessionProcessInspector(MmcSessionProcessInspector.TryProcessCommandLineNonce), _ => pin.CompleteVerifiedExit());
        Equal(MmcSessionObservation.ProvenExited, postExit.Observation);
        Equal(true, pin.Completed); Equal(false, File.Exists(fixture.PinFile));
        MmcSessions.AssertNoActive(lease);
        string history = Path.Combine(fixture.Scope, "versions", "sample", "PCL", "MmcSessionHistory", sessionId.ToString("N"), "Exited.record");
        Equal(true, File.ReadAllText(history).Contains("WrapperManagedExit", StringComparison.Ordinal));
    }

    private static void WrapperFailureCleanup() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        string workingDirectory = Path.Combine(fixture.Root, "minecraft");
        string wrapperPath = MmcSessions.ResolveExecutablePath("cmd.exe", workingDirectory);
        MmcExecutableIdentity wrapperIdentity = MmcSessions.ObserveExecutable(wrapperPath);

        // 1. Pre-launch identity mismatch fails before persisting Launching.record or keeping pins.
        FixturePin? mismatchedPin = null;
        ProcessStartInfo badOuter = new(wrapperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        badOuter.ArgumentList.Add("/d"); badOuter.ArgumentList.Add("/c"); badOuter.ArgumentList.Add("exit"); badOuter.ArgumentList.Add("0");
        var mismatched = fixture.Request() with {
            StartInfo = badOuter,
            IsWrapperManaged = true,
            WrapperIdentity = MmcSessions.ObserveExecutable(AppHost()),
            NonceArgumentIndex = 2
        };
        Throws<IOException>(() => MmcSessions.Launch(lease, mismatched, () => { }, (_, image) => mismatchedPin = new(image, fixture.PinFile)));
        Equal(true, mismatchedPin is null || (mismatchedPin.Completed && !mismatchedPin.Retained));
        MmcSessions.AssertNoActive(lease);

        // 2. Wrapper exits cleanly without starting Java: discovery fails promptly and recovery clears durable state.
        FixturePin? emptyPin = null;
        Guid id = Guid.NewGuid();
        ProcessStartInfo noJavaOuter = new(wrapperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        noJavaOuter.ArgumentList.Add("/d"); noJavaOuter.ArgumentList.Add("/c"); noJavaOuter.ArgumentList.Add("exit"); noJavaOuter.ArgumentList.Add("0");
        var noJava = fixture.Request() with {
            SessionId = id,
            StartInfo = noJavaOuter,
            IsWrapperManaged = true,
            WrapperIdentity = wrapperIdentity,
            NonceArgumentIndex = 2,
            WrapperJavaDiscoveryTimeout = TimeSpan.FromSeconds(15)
        };
        var elapsed = Stopwatch.StartNew();
        Throws<IOException>(() => MmcSessions.Launch(lease, noJava, () => { }, (_, image) => emptyPin = new(image, fixture.PinFile)));
        Equal(true, elapsed.ElapsedMilliseconds < 5000);
        Equal(true, emptyPin!.Retained && File.Exists(fixture.PinFile));
        var reconciled = MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(MmcSessionProcessInspector.TryProcessCommandLineNonce), _ => emptyPin.CompleteVerifiedExit());
        Equal(MmcSessionObservation.ProvenExited, reconciled.Observation);
        Equal(true, emptyPin.Completed && !File.Exists(fixture.PinFile));
        MmcSessions.AssertNoActive(lease);
    }
    private static void LaunchingInterruption() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        Throws<MmcTransactionInterruptionException>(() => MmcSessions.Launch(lease, fixture.Request(), () => { }, (_, image) => new FixturePin(image, fixture.PinFile), stage => {
            if (stage == MmcSessionCheckpoint.LaunchingPersisted) throw new MmcTransactionInterruptionException("before actual create");
        }));
        Guid id = Guid.ParseExact(Path.GetFileName(Directory.GetDirectories(fixture.Sessions).Single()), "N");
        Equal(MmcSessionObservation.Unknown, MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => throw new Exception("must not release")).Observation);
        Equal(true, File.Exists(fixture.PinFile)); Throws<IOException>(() => MmcSessions.AssertNoActive(lease));
        // This specific fixture knows Process.Start was never called. The generic OS inspector does not
        // make this claim; a real host must supply equally strong evidence before resolving Launching.
        Equal(MmcSessionObservation.ProvenExited, MmcSessions.Reconcile(lease, id, new KnownNotStarted(), _ => File.Delete(fixture.PinFile)).Observation);
    }
    private static void InvalidLaunch() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        Throws<IOException>(() => MmcSessions.Launch(lease, fixture.Request() with { SessionId = Guid.Empty }, () => { }, (_, _) => throw new Exception("Empty session reached pin acquisition.")));
        Throws<IOException>(() => MmcSessions.Launch(lease, fixture.Request() with { DefinitionGeneration = 2 }, () => { }, (_, image) => new FixturePin(image, fixture.PinFile)));
        Throws<IOException>(() => MmcSessions.Launch(lease, fixture.Request() with { GameRootLeaf = ".minecraft" }, () => { }, (_, image) => new FixturePin(image, fixture.PinFile)));
        var request = fixture.Request(); request.StartInfo.ArgumentList.Add(MmcSessions.NonceArgumentPrefix + "forged");
        Throws<IOException>(() => MmcSessions.Launch(lease, request, () => { }, (_, image) => new FixturePin(image, fixture.PinFile)));
        MmcSessions.AssertNoActive(lease);
    }
    private static void ReceiptPrivacy() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); var request = fixture.Request();
        request.StartInfo.RedirectStandardOutput = true; request.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        request.StartInfo.RedirectStandardError = true; request.StartInfo.StandardErrorEncoding = Encoding.Unicode;
        request.StartInfo.ArgumentList.Add("synthetic-access-token-should-not-persist");
        using var session = MmcSessions.Launch(lease, request, () => { }, (_, image) => new FixturePin(image, fixture.PinFile));
        Equal(Encoding.UTF8.CodePage, session.Process.StartInfo.StandardOutputEncoding?.CodePage ?? -1);
        Equal(Encoding.Unicode.CodePage, session.Process.StartInfo.StandardErrorEncoding?.CodePage ?? -1);
        fixture.WaitChild();
        foreach (string record in Directory.GetFiles(fixture.Sessions, "*.record", SearchOption.AllDirectories))
            Equal(false, Encoding.UTF8.GetString(File.ReadAllBytes(record)).Contains("synthetic-access-token-should-not-persist", StringComparison.Ordinal));
        fixture.Stop(); session.Process.WaitForExit(10000); session.CompleteVerifiedExit(lease);
    }
    private static void AbandonMutable() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        Guid id = Guid.NewGuid(); var binding = new MmcSessionRuntimeBinding(new string('d', 64));
        string mutable = CreateMutable(fixture.Root, id, binding), owner = Path.Combine(mutable, "owner.txt");
        string original = File.ReadAllText(owner); File.WriteAllText(owner, "foreign marker");
        Throws<IOException>(() => MmcSessionRuntimeDirectories.AbandonUnlaunched(lease, id, binding));
        Equal(true, File.Exists(Path.Combine(mutable, "temp", "native.dll")));
        File.WriteAllText(owner, original);
        MmcSessionRuntimeDirectories.AbandonUnlaunched(lease, id, binding);
        MmcSessionRuntimeDirectories.AbandonUnlaunched(lease, id, binding);
        Equal(false, Directory.Exists(mutable));
    }
    private static string CreateMutable(string root, Guid id, MmcSessionRuntimeBinding binding) {
        string path = Path.Combine(root, "PCL", "MmcRuntime", "sessions", id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "temp")); Directory.CreateDirectory(Path.Combine(path, "extract", "nested"));
        File.WriteAllText(Path.Combine(path, "owner.txt"), "PCLLF.MmcSessionMutable/v1\n" + id.ToString("N") + "\n" + binding.BoundPlanFingerprint);
        File.WriteAllText(Path.Combine(path, "temp", "native.dll"), "inert native fixture");
        File.WriteAllText(Path.Combine(path, "extract", "nested", "native.dll"), "inert native fixture");
        return path;
    }
    private static void RetryMutableCleanup() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); FixturePin? pin = null;
        Guid id = Guid.NewGuid(); var binding = new MmcSessionRuntimeBinding(new string('f', 64));
        string mutable = CreateMutable(fixture.Root, id, binding), marker = Path.Combine(mutable, "owner.txt");
        string expected = File.ReadAllText(marker);
        using var session = MmcSessions.Launch(lease, fixture.Request() with { SessionId = id, RuntimeBinding = binding }, () => { }, (_, image) => pin = new(image, fixture.PinFile));
        fixture.WaitChild(); fixture.Stop();
        if (!session.Process.WaitForExit(10000)) throw new Exception("Mutable cleanup fixture did not exit.");
        File.WriteAllText(marker, "foreign marker");
        Throws<IOException>(() => session.CompleteVerifiedExit(lease));
        Equal(true, File.Exists(fixture.PinFile));
        Equal(true, File.Exists(Path.Combine(fixture.Sessions, id.ToString("N"), "Exited.record")));
        Throws<IOException>(() => MmcSessions.AssertNoActive(lease));
        File.WriteAllText(marker, expected);
        Equal(MmcSessionObservation.ProvenExited, MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => pin!.CompleteVerifiedExit()).Observation);
        Equal(false, File.Exists(fixture.PinFile)); Equal(false, Directory.Exists(mutable));
        MmcSessions.AssertNoActive(lease);
    }
    private static void SuspendedImage() {
        using Fixture fixture = new();
        string image = AppHost();
        StartupInfo startup = new() { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(image, new StringBuilder('"' + image + "\" --never-resumed"), IntPtr.Zero, IntPtr.Zero, false,
            0x08000004, IntPtr.Zero, Path.Combine(fixture.Root, "minecraft"), ref startup, out var created))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            using Process suspended = Process.GetProcessById((int)created.ProcessId);
            Equal(MmcSessions.ObserveExecutable(image), MmcSessions.ObserveProcessExecutable(suspended));
        } finally {
            try {
                if (!TerminateProcess(created.Process, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (WaitForSingleObject(created.Process, 5000) != 0) throw new Exception("Owned suspended process did not terminate.");
            } finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreatedProcess { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out CreatedProcess process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    private static void KilledHost() {
        using Fixture fixture = new();
        ProcessStartInfo start = new(AppHost()) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--mmc-session-host-child", fixture.Scope, fixture.Id.ToString("D") }) start.ArgumentList.Add(arg);
        using (Process host = Process.Start(start) ?? throw new Exception("Could not start controlled monitor.")) {
            try {
                WaitFile(fixture.HostReady, 12000); fixture.WaitChild();
            } finally { if (!host.HasExited) host.Kill(); if (!host.WaitForExit(5000)) throw new Exception("Controlled monitor did not exit."); }
        }
        // Kill only the explicitly-created monitor, not its process tree: the owned game remains alive.
        using var lease = fixture.Lease();
        Guid id = Guid.ParseExact(Path.GetFileName(Directory.GetDirectories(fixture.Sessions).Single()), "N");
        Equal(true, File.Exists(fixture.PinFile)); Throws<IOException>(() => MmcSessions.AssertNoActive(lease));
        var inspector = new MmcSessionProcessInspector((pid, nonce) => pid == fixture.ChildPid() && nonce == fixture.ChildNonce());
        MmcSessionResult result = MmcSessions.Reconcile(lease, id, inspector, _ => throw new Exception("running pin must remain"));
        Equal(MmcSessionObservation.MatchingRunning, result.Observation);
        fixture.StopAndWait();
        Equal(MmcSessionObservation.ProvenExited, MmcSessions.Reconcile(lease, id, new MmcSessionProcessInspector(), _ => File.Delete(fixture.PinFile)).Observation);
    }

    private static string AppHost() {
        string candidate = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, OperatingSystem.IsWindows() ? ".exe" : null);
        return File.Exists(candidate) ? candidate : throw new Exception("Session fixture requires the actual generated apphost.");
    }
    private static byte[] Definition(Guid id, long generation) => Encoding.UTF8.GetBytes($$$"""{"schemaVersion":1,"managementKind":"Mmc","formatProfile":"prism-v1","instanceId":"{{{id:D}}}","generation":{{{generation}}},"layout":{"gameRootLeaf":"minecraft"}}""");
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    private static void WaitFile(string path, int timeout) { var timer = Stopwatch.StartNew(); while (!File.Exists(path) && timer.ElapsedMilliseconds < timeout) Thread.Sleep(20); if (!File.Exists(path)) throw new Exception("Controlled child did not report ready."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class KnownNotStarted : IMmcSessionProcessInspector { public MmcSessionObservation Inspect(MmcSessionReceipt receipt, MmcSessionProcessRegistration? registration) => MmcSessionObservation.ProvenExited; }
    private sealed class FixturePin(MmcExecutableIdentity identity, string marker) : IMmcJavaSessionPin {
        public MmcExecutableIdentity Identity { get; } = Save(identity, marker);
        public bool Retained { get; private set; }
        public bool Completed { get; private set; }
        private static MmcExecutableIdentity Save(MmcExecutableIdentity identity, string marker) { File.WriteAllText(marker, identity.Sha256); return identity; }
        public void KeepForRecovery() => Retained = true;
        public void CompleteVerifiedExit() { Completed = true; File.Delete(marker); }
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable {
        public string Scope { get; }
        public string Root => Path.Combine(Scope, "versions", "sample");
        public Guid Id { get; }
        public string Ready => Path.Combine(Scope, "game-ready");
        public string HostReady => Path.Combine(Scope, "host-ready");
        public string StopFile => Path.Combine(Scope, "game-stop");
        public string PinFile => Path.Combine(Scope, "java-pin");
        public string Sessions => Path.Combine(Root, "PCL", "MmcSessions");
        private readonly bool cleanup;
        public Fixture() : this(Path.Combine(Path.GetTempPath(), "pcllf-mmc-session-" + Guid.NewGuid().ToString("N")), Guid.NewGuid(), true) { }
        public Fixture(string scope, Guid id, bool initialize) {
            Scope = scope; Id = id; cleanup = initialize;
            if (initialize) {
                Directory.CreateDirectory(Path.Combine(Root, "PCL")); Directory.CreateDirectory(Path.Combine(Root, "minecraft"));
                File.WriteAllBytes(Path.Combine(Root, "PCL", "InstanceDefinition.json"), Definition(id, 1));
            }
        }
        public MmcAdmissionLease Lease() => MmcAdmissionGate.Acquire(Root, Id, TimeSpan.Zero);
        public MmcSessionLaunchRequest Request() {
            var image = MmcSessions.ObserveExecutable(AppHost()); var launcher = MmcSessions.ObserveExecutable(Environment.ProcessPath!);
            ProcessStartInfo start = new(AppHost()) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.Combine(Root, "minecraft") };
            foreach (string arg in new[] { "--mmc-session-child", Ready, StopFile, "bounded-fixture" }) start.ArgumentList.Add(arg);
            return new(Id, 1, new string('a', 64), image, launcher, start);
        }
        public MmcFileTransactionRequest TransactionRequest() {
            byte[] newer = Definition(Id, 2);
            return new(Id, 1, 2, [new("PCL/InstanceDefinition.json", Hash(Definition(Id, 1)), newer, Hash(newer))]);
        }
        public void WaitChild() => WaitFile(Ready, 10000);
        public int ChildPid() => int.Parse(File.ReadAllLines(Ready)[0], System.Globalization.CultureInfo.InvariantCulture);
        public string ChildNonce() => File.ReadAllLines(Ready)[1];
        public void Stop() => File.WriteAllText(StopFile, "stop");
        public void StopAndWait() {
            Stop(); if (!File.Exists(Ready)) return;
            try { using Process process = Process.GetProcessById(ChildPid()); if (!process.WaitForExit(10000)) throw new Exception("Controlled game did not exit."); }
            catch (ArgumentException) { }
        }
        public void Dispose() {
            if (!cleanup) return;
            StopAndWait();
            string verified = Path.GetFullPath(Scope);
            if (!verified.StartsWith(Path.Combine(Path.GetTempPath(), "pcllf-mmc-session-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing unexpected session fixture cleanup root.");
            var elapsed = Stopwatch.StartNew();
            while (Directory.Exists(verified)) {
                try { Directory.Delete(verified, true); break; }
                // A confirmed-exited Windows child can briefly retain its cwd handle. Retry only
                // sharing violations; permission errors and all other cleanup failures remain failures.
                catch (IOException error) when ((error.HResult & 0xffff) == 32 && elapsed.ElapsedMilliseconds < 1000) { Thread.Sleep(20); }
            }
        }
    }
}
