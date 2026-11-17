using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PCLCS;
using PCLLF.MMCC.Core;

// Original LF host acceptance fixture; repository LICENCE applies.
internal static class MmcRealCrashChecks {
    internal static int Run(string? selected) {
        using var fixture = new CrashFixture();
        try {
            string[] cases = selected is null ? ["prepared", "committed", "foreign"] : [selected];
            foreach (string name in cases) {
                using var scenario = new CrashScenario(fixture.Owner, name);
                fixture.Log("scenario-started", scenario.Owner);
                using (var child = new OwnedChild(fixture, scenario)) {
                    CheckpointReceipt receipt = child.WaitForCheckpoint();
                    AssertActiveProducer(fixture, scenario, receipt);
                    child.KillAtCheckpoint(receipt);
                    AssertUseReleased(fixture, receipt);
                    if (name == "prepared") RollbackAndRetry(fixture, scenario, receipt);
                    else if (name == "committed") RollForward(fixture, scenario, receipt);
                    else PreserveForeign(fixture, scenario, receipt);
                }
                fixture.Log("scenario-passed", new { name, scenario.Root });
            }
            CrashIo.WriteJson(Path.Combine(fixture.Root, "summary.json"), new { Passed = true, Cases = cases,
                Scope = "Actual parent-owned Windows child termination and genuine LF import recovery; no JVM, Minecraft, UI, or external network.", fixture.Owner });
            fixture.Log("acceptance-passed", new { Count = cases.Length, EvidenceRoot = fixture.Root });
            return 0;
        } catch (Exception error) {
            fixture.Log("acceptance-failed", new { Error = error.ToString(), EvidenceRoot = fixture.Root });
            throw;
        } finally { Console.WriteLine("RETAINED real-crash evidence: " + fixture.Root); }
    }

    internal static int RunChild(string root, Guid rootNonce, string directory, Guid scenarioNonce) {
        var opened = CrashScenario.OpenChild(root, rootNonce, directory, scenarioNonce);
        using var scenario = opened.Scenario;
        using var self = Process.GetCurrentProcess();
        var identity = new ChildIdentity(opened.Fixture.Root, opened.Fixture.Nonce, scenario.Root, scenario.Owner.Nonce,
            opened.Fixture.ParentPid, opened.Fixture.ParentStartedUtcTicks, opened.Fixture.ParentImage,
            self.Id, CrashIo.Started(self), CrashIo.CurrentImage);
        CrashIo.Check(CrashIo.Same(identity.Image, opened.Fixture.ParentImage), "Child is not the exact parent test apphost.");
        CrashIo.WriteJson(scenario.Startup, identity);
        bool reached = false;
        var result = scenario.Backend().ImportAsync(scenario.Request() with {
            TransactionCheckpoint = (phase, _) => {
                if (phase != scenario.Owner.Checkpoint) return;
                CrashIo.Check(!reached, "The selected real checkpoint was reached twice."); reached = true;
                CheckpointReceipt receipt = CaptureCheckpoint(scenario, identity, phase);
                CrashIo.WriteJson(scenario.Checkpoint, receipt);
                Console.WriteLine("WAITING at real durable " + phase + "; PID=" + self.Id + "; rootNonce=" + rootNonce.ToString("N"));
                Console.Out.Flush();
                // No simulated exception/cancellation and no managed cleanup. Only the verified parent terminates this process.
                Thread.Sleep(Timeout.Infinite);
            }
        }).GetAwaiter().GetResult();
        Console.Error.WriteLine("Child import returned instead of being killed: " + result.Status + "; " + result.Failure + "; checkpointReached=" + reached);
        return 1;
    }

    private static CheckpointReceipt CaptureCheckpoint(CrashScenario scenario, ChildIdentity identity, MmcTransactionCheckpoint checkpoint) {
        var entry = scenario.Entry();
        CrashIo.Check(entry.State == MmcImportRecoveryState.ProducerActive && entry.TemporaryRoot is not null && entry.FinalRoot is not null,
            "Real child did not retain the active production Use lease.");
        string temporary = entry.TemporaryRoot!;
        var owner = CrashIo.Record(CrashIo.Under(entry.OperationDirectory, "reservation.record"));
        Guid instanceId = owner.Value.GetProperty("InstanceId").GetGuid();
        CrashIo.Check(owner.Value.GetProperty("PackageSha256").GetString() == CrashIo.HashFile(scenario.Archive) &&
            CrashIo.Same(owner.Value.GetProperty("TemporaryRoot").GetString()!, temporary) && CrashIo.Same(entry.FinalRoot!, scenario.Final), "Real reservation owner differs from this fixture.");
        string transactions = CrashIo.Under(temporary, "PCL/MmcTransactions");
        string transaction = Directory.EnumerateDirectories(transactions).Single();
        var prepared = CrashIo.Record(CrashIo.Under(transaction, "prepared.record"));
        Guid transactionId = prepared.Value.GetProperty("TransactionId").GetGuid();
        CrashIo.Check(Path.GetFileName(transaction) == transactionId.ToString("N") && prepared.Value.GetProperty("InstanceId").GetGuid() == instanceId &&
            prepared.Value.GetProperty("ExpectedGeneration").GetInt64() == 0 && prepared.Value.GetProperty("NewGeneration").GetInt64() == 1 &&
            CrashIo.Same(prepared.Value.GetProperty("CanonicalRoot").GetString()!, temporary), "Real prepared journal is not this initial import.");
        var pending = CrashIo.Record(CrashIo.Under(transaction, "Pending.record"));
        CrashIo.Check(pending.Value.GetProperty("TransactionId").GetGuid() == transactionId && pending.Value.GetProperty("InstanceId").GetGuid() == instanceId &&
            pending.Value.GetProperty("PreparedSha256").GetString() == prepared.Hash && pending.Value.GetProperty("Phase").GetString() == "Pending", "Real pending marker does not bind the prepared import.");
        string decision = CrashIo.Under(transaction, "CommitDecided.record"); string? decisionHash = null;
        if (checkpoint == MmcTransactionCheckpoint.CommitDecided) {
            var decided = CrashIo.Record(decision); decisionHash = decided.Hash;
            CrashIo.Check(decided.Value.GetProperty("TransactionId").GetGuid() == transactionId && decided.Value.GetProperty("InstanceId").GetGuid() == instanceId &&
                decided.Value.GetProperty("PreparedSha256").GetString() == prepared.Hash && decided.Value.GetProperty("Phase").GetString() == "CommitDecided", "Real durable decision does not bind the prepared import.");
        } else CrashIo.Check(!File.Exists(decision), "Prepared child already has a commit decision.");
        List<ExpectedTarget> targets = []; int index = 0;
        foreach (var mutation in prepared.Value.GetProperty("Mutations").EnumerateArray()) {
            string relative = mutation.GetProperty("RelativePath").GetString()!;
            CrashIo.Check(mutation.GetProperty("OldSha256").ValueKind == JsonValueKind.Null, "Initial import unexpectedly overwrites old bytes.");
            string? hash = mutation.GetProperty("NewSha256").GetString(); string? evidence = null; long size = 0;
            if (hash is not null) {
                byte[] bytes = CrashIo.ReadBytes(CrashIo.Under(transaction, "new-" + index + ".bin"));
                CrashIo.Check(CrashIo.Hash(bytes) == hash, "Real staged object differs from prepared expected bytes.");
                evidence = "target-" + index + ".bin"; size = bytes.LongLength;
                CrashIo.WriteBytes(CrashIo.Under(scenario.ExpectedRoot, evidence), bytes);
                if (relative == "PCL/InstanceDefinition.json") {
                    var definition = MmcDefinitionCodec.Read(Encoding.UTF8.GetString(bytes));
                    CrashIo.Check(definition.Definition?.InstanceId == instanceId && definition.Definition.Generation == 1, "Actual staged definition is not original id/G1.");
                }
            }
            targets.Add(new(index++, relative, hash, size, evidence));
        }
        CrashIo.Check(targets.Any(target => target.RelativePath == "PCL/InstanceDefinition.json"), "No real staged definition target.");
        scenario.AssertAuthorPayload(temporary);
        return new(identity, checkpoint.ToString(), entry.OperationDirectory, temporary, scenario.Final, instanceId, transactionId,
            prepared.Hash, decisionHash, entry.PackageSha256!, CrashIo.HashFile(CrashIo.Under(entry.OperationDirectory, "producer.use")), targets.ToArray());
    }

    private static void AssertActiveProducer(CrashFixture fixture, CrashScenario scenario, CheckpointReceipt receipt) {
        var entry = scenario.Entry(receipt.OperationDirectory);
        CrashIo.Check(entry.State == MmcImportRecoveryState.ProducerActive && !entry.CanRecover && !entry.CanRetry,
            "Live actual producer was offered recovery or retry.");
        string lease = CrashIo.Under(receipt.OperationDirectory, "producer.use"); bool denied = false;
        CrashIo.Check(CrashIo.HashFile(lease) == receipt.ProducerUseSha256, "Active Use bytes differ from the child's durable checkpoint receipt.");
        try { using var unexpected = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { denied = true; }
        CrashIo.Check(denied, "Live Windows Use handle did not exclude another writer.");
        string before = CrashIo.Tree(receipt.TemporaryRoot) + CrashIo.Tree(receipt.OperationDirectory);
        var refused = MmcImportRecovery.RecoverAsync(entry with { CanRecover = true }).GetAwaiter().GetResult();
        CrashIo.Check(refused.Status == MmcInstanceBackendStatus.NeedsRecovery && !refused.FreshImportRequired &&
            before == CrashIo.Tree(receipt.TemporaryRoot) + CrashIo.Tree(receipt.OperationDirectory) && !Directory.Exists(scenario.Final),
            "An edited preview recovered the still-live child or changed its bytes.");
        fixture.Log("active-producer-refused", new { scenario.Owner.Name, receipt.Child.Pid, entry.State, refused.Status, refused.Diagnostic, LeaseSha256 = CrashIo.HashFile(lease), TreeSha256 = CrashIo.Hash(Encoding.UTF8.GetBytes(before)) });
    }

    private static void AssertUseReleased(CrashFixture fixture, CheckpointReceipt receipt) {
        string lease = CrashIo.Under(receipt.OperationDirectory, "producer.use");
        using (var released = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            CrashIo.Check(released.Length > 0, "OS released an empty/substituted producer Use file.");
        }
        CrashIo.Check(CrashIo.HashFile(lease) == receipt.ProducerUseSha256, "Producer Use bytes changed across the actual process termination.");
        fixture.Log("os-released-producer-use", new { receipt.Child.Pid, Lease = lease, LeaseSha256 = CrashIo.HashFile(lease) });
    }

    private static void RollbackAndRetry(CrashFixture fixture, CrashScenario scenario, CheckpointReceipt receipt) {
        var entry = scenario.Entry(receipt.OperationDirectory);
        CrashIo.Check(entry.CanRecover && entry.State == MmcImportRecoveryState.Recoverable, "Killed undecided producer was not recoverable: " + entry.Description);
        string payload = CrashIo.Tree(CrashIo.Under(receipt.TemporaryRoot, "minecraft"));
        var recovered = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        CrashIo.Check(recovered.Status == MmcInstanceBackendStatus.NeedsRecovery && recovered.FreshImportRequired &&
            Directory.Exists(receipt.TemporaryRoot) && !Directory.Exists(scenario.Final) &&
            !File.Exists(CrashIo.Under(receipt.TemporaryRoot, "PCL/InstanceDefinition.json")) && payload == CrashIo.Tree(CrashIo.Under(receipt.TemporaryRoot, "minecraft")),
            "Actually killed undecided import did not roll back while retaining its payload: " + recovered.Diagnostic);
        string history = CrashIo.Under(receipt.TemporaryRoot, "PCL/MmcTransactionHistory/" + receipt.TransactionId.ToString("N"));
        var finalization = CrashIo.Record(CrashIo.Under(history, "Finalized.record"));
        CrashIo.Check(finalization.Value.GetProperty("Phase").GetString() == "FinalizedAborted" && !File.Exists(CrashIo.Under(history, "CommitDecided.record")), "Undecided real transaction was not archived as aborted.");
        scenario.AssertAuthorPayload(receipt.TemporaryRoot);
        string parked = CrashIo.Tree(receipt.TemporaryRoot);
        CrashIo.WriteJson(Path.Combine(scenario.Root, "parked-after-rollback.json"), new { Receipt = receipt, Tree = parked });
        entry = scenario.Entry(receipt.OperationDirectory); CrashIo.Check(entry.CanRetry, "Actual rollback lost same-package retry.");
        var backend = scenario.Backend();
        var sameName = backend.RetryInitialImportAsync(entry, scenario.Request()).GetAwaiter().GetResult();
        CrashIo.Check(sameName.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(scenario.Final), "Retry claimed the original final name.");
        string retry = CrashIo.Under(scenario.Game, "versions/retry-" + Guid.NewGuid().ToString("N"));
        string wrongArchive = CrashIo.Under(scenario.Root, "source/different.zip"); scenario.WritePackage(wrongArchive, Encoding.UTF8.GetBytes("different author payload"));
        var wrong = backend.RetryInitialImportAsync(entry, scenario.Request(wrongArchive, retry)).GetAwaiter().GetResult();
        CrashIo.Check(wrong.Status == MmcInstanceBackendStatus.Rejected && !Directory.Exists(retry) && parked == CrashIo.Tree(receipt.TemporaryRoot), "Retry accepted a different SHA or touched old parked bytes.");
        string sameArchive = CrashIo.Under(scenario.Root, "source/renamed-original.zip"); File.Copy(scenario.Archive, sameArchive);
        CrashIo.Check(CrashIo.HashFile(sameArchive) == receipt.PackageSha256, "Renamed retry source is not byte-identical.");
        var retried = backend.RetryInitialImportAsync(entry, scenario.Request(sameArchive, retry)).GetAwaiter().GetResult();
        var retryDefinition = retried.Definition ?? throw new InvalidOperationException("Same-SHA retry returned no definition: " + retried.Failure);
        CrashIo.Check(retried.Status == MmcInstanceBackendStatus.Succeeded && retried.Definition is { Generation: 1 } &&
            retried.Definition.InstanceId != receipt.InstanceId && retried.Definition.Origin.PackageSha256 == receipt.PackageSha256 &&
            parked == CrashIo.Tree(receipt.TemporaryRoot) && !Directory.Exists(scenario.Final) && MmcInstanceInspector.Read(retry).IsConsistent,
            "Same-SHA/different-name actual retry failed or modified old retained data: " + retried.Failure);
        scenario.AssertAuthorPayload(retry); scenario.AssertGuards();
        fixture.Log("prepared-rollback-and-retry-passed", new { receipt.Child.Pid, receipt.InstanceId, receipt.TransactionId, recovered.Status, recovered.FreshImportRequired,
            SameNameStatus = sameName.Status, WrongShaStatus = wrong.Status, RetryStatus = retried.Status, RetryInstanceId = retryDefinition.InstanceId,
            RetryGeneration = retryDefinition.Generation, PackageSha256 = receipt.PackageSha256, OriginalArchive = scenario.Archive, RenamedArchive = sameArchive,
            ParkedRoot = receipt.TemporaryRoot, RetryRoot = retry, ParkedTreeSha256 = CrashIo.Hash(Encoding.UTF8.GetBytes(parked)) });
    }

    private static void RollForward(CrashFixture fixture, CrashScenario scenario, CheckpointReceipt receipt) {
        CrashIo.Check(receipt.DecisionSha256 is not null, "Committed receipt has no actual durable decision.");
        // Preserve the original ZIP bytes at a different fixture path; recovery cannot reopen the original source pathname.
        string offlineArchive = CrashIo.Under(scenario.Root, "source/original.zip.offline-evidence"); File.Move(scenario.Archive, offlineArchive);
        var entry = scenario.Entry(receipt.OperationDirectory);
        CrashIo.Check(entry.CanRecover && !File.Exists(scenario.Archive), "Killed decided import did not expose offline recovery.");
        var recovered = MmcImportRecovery.RecoverAsync(entry).GetAwaiter().GetResult();
        CrashIo.Check(recovered.Status == MmcInstanceBackendStatus.Succeeded && !recovered.FreshImportRequired &&
            recovered.Paths is not null && CrashIo.Same(recovered.Paths.InstanceRoot, scenario.Final) && !Directory.Exists(receipt.TemporaryRoot),
            "Actually killed decided import did not finish publication: " + recovered.Diagnostic);
        var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(scenario.Final));
        CrashIo.Check(primary.Definition.InstanceId == receipt.InstanceId && primary.Definition.Generation == 1 &&
            primary.Definition.Origin.PackageSha256 == receipt.PackageSha256 && MmcInstanceInspector.Read(scenario.Final).IsConsistent,
            "Recovered decided publication changed original id/G1/source identity.");
        long expectedBytes = AssertExpectedBytes(scenario, receipt, scenario.Final);
        scenario.AssertAuthorPayload(scenario.Final);
        CrashIo.Check(!File.Exists(CrashIo.Under(scenario.Final, ".pclignore")) && !File.Exists(CrashIo.Under(scenario.Final, "PCL/MmcImportPending.json")) &&
            scenario.Entry(receipt.OperationDirectory).State == MmcImportRecoveryState.Completed, "Recovered publication left actionable import guards.");
        string history = CrashIo.Under(scenario.Final, "PCL/MmcTransactionHistory/" + receipt.TransactionId.ToString("N"));
        CrashIo.Check(CrashIo.Record(CrashIo.Under(history, "Finalized.record")).Value.GetProperty("Phase").GetString() == "FinalizedCommitted", "Recovered real decision was not archived as committed.");
        scenario.AssertGuards(offlineArchive);
        fixture.Log("commit-roll-forward-passed", new { receipt.Child.Pid, receipt.InstanceId, Generation = primary.Definition.Generation, PrimarySha256 = primary.Sha256,
            receipt.TransactionId, receipt.PreparedSha256, receipt.DecisionSha256, ExpectedTargetCount = receipt.Targets.Length, ExpectedBytes = expectedBytes,
            recovered.Status, FinalRoot = scenario.Final, PreservedOfflineArchive = offlineArchive, PackageSha256 = CrashIo.HashFile(offlineArchive) });
    }

    private static void PreserveForeign(CrashFixture fixture, CrashScenario scenario, CheckpointReceipt receipt) {
        string world = CrashIo.Under(receipt.TemporaryRoot, "minecraft/saves/foreign-world/level.dat");
        string unknown = CrashIo.Under(receipt.TemporaryRoot, "minecraft/unknown-bytes.bin");
        string player = CrashIo.Under(receipt.TemporaryRoot, "minecraft/config/player.cfg");
        byte[] worldBytes = Encoding.UTF8.GetBytes("foreign player world " + scenario.Owner.Nonce);
        byte[] unknownBytes = Encoding.UTF8.GetBytes("unknown bytes " + scenario.Owner.Nonce);
        byte[] changedPlayer = scenario.PlayerBytes.ToArray(); changedPlayer[0] ^= 0x20;
        CrashIo.WriteBytes(world, worldBytes); CrashIo.WriteBytes(unknown, unknownBytes);
        var unknownOnly = MmcImportRecovery.RecoverAsync(scenario.Entry(receipt.OperationDirectory)).GetAwaiter().GetResult();
        CrashIo.Check(unknownOnly.Status == MmcInstanceBackendStatus.NeedsRecovery && !unknownOnly.FreshImportRequired &&
            !Directory.Exists(scenario.Final) && CrashIo.ReadBytes(world).SequenceEqual(worldBytes) && CrashIo.ReadBytes(unknown).SequenceEqual(unknownBytes),
            "Unknown-only world/game bytes did not refuse real publication or were removed.");
        scenario.AssertAuthorPayload(receipt.TemporaryRoot);
        CrashIo.Check(CrashIo.ReadBytes(player).SequenceEqual(scenario.PlayerBytes), "Foreign-byte fixture did not start from exact author settings.");
        using (var changed = new FileStream(player, FileMode.Truncate, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough)) {
            changed.Write(changedPlayer); changed.Flush(true);
        }
        var recovered = MmcImportRecovery.RecoverAsync(scenario.Entry(receipt.OperationDirectory)).GetAwaiter().GetResult();
        RequireForeign(); string afterFirst = CrashIo.Tree(receipt.TemporaryRoot);
        var repeated = MmcImportRecovery.RecoverAsync(scenario.Entry(receipt.OperationDirectory)).GetAwaiter().GetResult();
        RequireForeign();
        CrashIo.Check(recovered.Status == MmcInstanceBackendStatus.NeedsRecovery && repeated.Status == MmcInstanceBackendStatus.NeedsRecovery &&
            !recovered.FreshImportRequired && !repeated.FreshImportRequired && afterFirst == CrashIo.Tree(receipt.TemporaryRoot),
            "Real recovery accepted or changed unknown/player bytes on refusal or a repeat attempt.");
        long expectedBytes = AssertExpectedBytes(scenario, receipt, receipt.TemporaryRoot);
        scenario.AssertGuards();
        CrashIo.WriteJson(Path.Combine(scenario.Root, "foreign-preserved.json"), new { Receipt = receipt, World = world, WorldSha256 = CrashIo.HashFile(world),
            Unknown = unknown, UnknownSha256 = CrashIo.HashFile(unknown), Player = player, PlayerSha256 = CrashIo.HashFile(player), Tree = afterFirst });
        fixture.Log("foreign-bytes-preserved", new { receipt.Child.Pid, UnknownOnlyStatus = unknownOnly.Status, UnknownOnlyDiagnostic = unknownOnly.Diagnostic,
            recovered.Status, recovered.Diagnostic, RepeatStatus = repeated.Status,
            RepeatDiagnostic = repeated.Diagnostic, World = world, Unknown = unknown, ChangedPlayer = player, ExpectedStructuralBytes = expectedBytes, ParkedRoot = receipt.TemporaryRoot });
        void RequireForeign() => CrashIo.Check(!Directory.Exists(scenario.Final) && Directory.Exists(receipt.TemporaryRoot) &&
            CrashIo.ReadBytes(world).SequenceEqual(worldBytes) && CrashIo.ReadBytes(unknown).SequenceEqual(unknownBytes) &&
            CrashIo.ReadBytes(player).SequenceEqual(changedPlayer) && CrashIo.ReadBytes(CrashIo.Under(receipt.TemporaryRoot, "minecraft/saves/author-world/level.dat")).SequenceEqual(scenario.WorldBytes),
            "Recovery published/deleted a foreign world, unknown file, changed player settings, or author world.");
    }

    private static long AssertExpectedBytes(CrashScenario scenario, CheckpointReceipt receipt, string root) {
        long total = 0;
        foreach (var target in receipt.Targets) {
            string actual = CrashIo.Under(root, target.RelativePath);
            if (target.Sha256 is null) { CrashIo.Check(!File.Exists(actual), "Deleted target unexpectedly exists."); continue; }
            byte[] expected = CrashIo.ReadBytes(CrashIo.Under(scenario.ExpectedRoot, target.EvidenceName!));
            byte[] observed = CrashIo.ReadBytes(actual);
            CrashIo.Check(expected.LongLength == target.SizeBytes && CrashIo.Hash(expected) == target.Sha256 && expected.AsSpan().SequenceEqual(observed),
                "Published target differs from actual pre-kill staged expected bytes: " + target.RelativePath);
            total = checked(total + expected.LongLength);
        }
        return total;
    }

    private sealed class OwnedChild : IDisposable {
        private readonly CrashFixture fixture;
        private readonly CrashScenario scenario;
        private readonly Process process;
        private readonly long startedUtcTicks;
        private readonly Task<string> stdout, stderr;
        private ChildIdentity? identity;
        private bool stopped, outputSaved;
        internal OwnedChild(CrashFixture fixture, CrashScenario scenario) {
            this.fixture = fixture; this.scenario = scenario;
            ProcessStartInfo start = new(fixture.Apphost) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scenario.Root,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "--child", fixture.Root, fixture.Owner.Nonce.ToString("N"), scenario.Root, scenario.Owner.Nonce.ToString("N") }) start.ArgumentList.Add(value);
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned real import child.");
            startedUtcTicks = CrashIo.Started(process);
            stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
            fixture.Log("child-created", new { scenario.Owner.Name, Pid = process.Id, StartedUtcTicks = startedUtcTicks, Image = fixture.Apphost,
                RootNonce = fixture.Owner.Nonce, ScenarioNonce = scenario.Owner.Nonce, Arguments = start.ArgumentList.ToArray() });
        }
        internal CheckpointReceipt WaitForCheckpoint() {
            Stopwatch deadline = Stopwatch.StartNew();
            WaitFor(scenario.Startup); identity = CrashIo.ReadJson<ChildIdentity>(scenario.Startup); VerifyIdentity(identity);
            WaitFor(scenario.Checkpoint); var receipt = CrashIo.ReadJson<CheckpointReceipt>(scenario.Checkpoint);
            VerifyIdentity(receipt.Child); CrashIo.Check(receipt.Child == identity && receipt.Checkpoint == scenario.Owner.Checkpoint.ToString(), "Checkpoint identity or phase changed.");
            var owner = CrashIo.Record(CrashIo.Under(receipt.OperationDirectory, "reservation.record"));
            CrashIo.Check(CrashIo.Same(receipt.OperationDirectory, CrashIo.Under(scenario.Game, "PCL/MmcImportOperations/" + owner.Value.GetProperty("OperationId").GetGuid().ToString("N"))) &&
                owner.Value.GetProperty("InstanceId").GetGuid() == receipt.InstanceId && owner.Value.GetProperty("PackageSha256").GetString() == receipt.PackageSha256 &&
                CrashIo.Same(owner.Value.GetProperty("TemporaryRoot").GetString()!, receipt.TemporaryRoot) && CrashIo.Same(owner.Value.GetProperty("FinalRoot").GetString()!, scenario.Final) &&
                CrashIo.Same(receipt.FinalRoot, scenario.Final), "Checkpoint receipt escaped this actual reservation.");
            string transaction = CrashIo.Under(receipt.TemporaryRoot, "PCL/MmcTransactions/" + receipt.TransactionId.ToString("N"));
            var prepared = CrashIo.Record(CrashIo.Under(transaction, "prepared.record"));
            CrashIo.Check(prepared.Hash == receipt.PreparedSha256 && prepared.Value.GetProperty("Mutations").GetArrayLength() == receipt.Targets.Length, "Actual journal changed before termination.");
            foreach (var target in receipt.Targets) {
                var mutation = prepared.Value.GetProperty("Mutations")[target.Index];
                CrashIo.Check(mutation.GetProperty("RelativePath").GetString() == target.RelativePath && mutation.GetProperty("NewSha256").GetString() == target.Sha256,
                    "Expected-byte receipt does not match actual journal target.");
            }
            string decision = CrashIo.Under(transaction, "CommitDecided.record");
            CrashIo.Check(scenario.Owner.Checkpoint == MmcTransactionCheckpoint.Prepared ? !File.Exists(decision) && receipt.DecisionSha256 is null :
                CrashIo.Record(decision).Hash == receipt.DecisionSha256, "Actual decision boundary differs from selected checkpoint.");
            fixture.Log("checkpoint-observed", receipt); return receipt;
            void WaitFor(string path) {
                while (!File.Exists(path)) {
                    if (process.HasExited) { SaveOutput(); throw new InvalidOperationException("Owned child exited naturally before durable checkpoint: " + process.ExitCode); }
                    if (deadline.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Owned child did not reach its actual checkpoint within 45 seconds.");
                    process.WaitForExit(25);
                }
            }
        }
        private void VerifyIdentity(ChildIdentity candidate) {
            process.Refresh();
            CrashIo.Check(!process.HasExited && process.Id != fixture.Owner.ParentPid && process.Id == candidate.Pid &&
                CrashIo.Started(process) == startedUtcTicks && candidate.StartedUtcTicks == startedUtcTicks &&
                CrashIo.Same(process.MainModule!.FileName, fixture.Apphost) && CrashIo.Same(candidate.Image, fixture.Apphost) &&
                candidate.ParentPid == fixture.Owner.ParentPid && candidate.ParentStartedUtcTicks == fixture.Owner.ParentStartedUtcTicks &&
                CrashIo.Same(candidate.ParentImage, fixture.Owner.ParentImage) && CrashIo.Same(candidate.Root, fixture.Root) && candidate.RootNonce == fixture.Owner.Nonce &&
                CrashIo.Same(candidate.ScenarioRoot, scenario.Root) && candidate.ScenarioNonce == scenario.Owner.Nonce,
                "Refusing termination: PID, creation time, exact image, parent, or fixture nonces do not match this Process.Start handle.");
        }
        internal void KillAtCheckpoint(CheckpointReceipt receipt) {
            CrashIo.Check(identity is not null && receipt.Child == identity, "No verified actual child checkpoint.");
            Stop("checkpoint-acceptance", receipt.Checkpoint);
        }
        private void Stop(string reason, string? checkpoint) {
            var verified = identity ?? throw new InvalidOperationException("No nonce-bearing child identity; no termination is authorized.");
            VerifyIdentity(verified);
            fixture.Log("owned-child-kill-requested", new { Reason = reason, Checkpoint = checkpoint, Identity = verified, EntireProcessTree = false });
            // The held Process.Start handle plus PID/creation/image/nonces prove ownership; never enumerate or kill user applications.
            process.Kill(entireProcessTree: false);
            CrashIo.Check(process.WaitForExit(5000), "Owned killed child did not exit within five seconds."); stopped = true;
            SaveOutput(); CrashIo.Check(process.ExitCode == -1, "Windows owned child did not return actual forced-termination exit code -1.");
            fixture.Log("owned-child-killed", new { Reason = reason, Checkpoint = checkpoint, verified.Pid, verified.StartedUtcTicks, verified.Image,
                verified.RootNonce, verified.ScenarioNonce, process.ExitCode, NoManagedReturn = true });
        }
        private void SaveOutput() {
            if (outputSaved) return;
            CrashIo.Check(Task.WaitAll([stdout, stderr], 5000), "Stopped child output did not reach EOF within five seconds.");
            CrashIo.WriteBytes(CrashIo.Under(scenario.Root, "child-stdout.txt"), Encoding.UTF8.GetBytes(stdout.GetAwaiter().GetResult()));
            CrashIo.WriteBytes(CrashIo.Under(scenario.Root, "child-stderr.txt"), Encoding.UTF8.GetBytes(stderr.GetAwaiter().GetResult()));
            outputSaved = true;
        }
        public void Dispose() {
            try {
                if (!stopped && !process.HasExited) {
                    // On a failed checkpoint wait, the independently flushed startup identity still governs exact owned cleanup.
                    if (identity is null && File.Exists(scenario.Startup)) identity = CrashIo.ReadJson<ChildIdentity>(scenario.Startup);
                    Stop("cleanup-after-failed-acceptance", null);
                } else if (!outputSaved) SaveOutput();
            } finally { process.Dispose(); }
        }
    }
}
