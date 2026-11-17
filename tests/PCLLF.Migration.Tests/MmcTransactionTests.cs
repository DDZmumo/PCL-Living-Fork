using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PCLCS;

internal static class MmcTransactionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC transaction publishes definition before one durable decision and archives evidence", Commit);
        yield return ("MMC transaction interruption before decide rolls all bytes back idempotently", Rollback);
        yield return ("MMC transaction interruption after decide completes forward idempotently", Forward);
        yield return ("MMC transaction cancellation before decision rolls back but after decision commits", CancellationBoundary);
        yield return ("MMC transaction actual replacement backup preserves racing third-party bytes", ReplacementRace);
        yield return ("MMC transaction recovery never overwrites third-party current bytes", CurrentConflict);
        yield return ("MMC transaction recovery rejects corrupt staged originals and damaged journals", CorruptRecovery);
        yield return ("MMC transaction missing old target without a move backup stays recovery required", UnknownMissingTarget);
        yield return ("MMC transaction delete uses recoverable move and rollback restores bytes", DeleteRollback);
        yield return ("MMC transaction input identity generation hashes and reserved paths are checked", InvalidRequests);
        yield return ("MMC transaction file-backed staging streams into locked journal objects and refuses tampering or escape", FileBackedSource);
        yield return ("MMC admission gate excludes another process even when its claimed instance ID differs", CrossProcessGate);
        yield return ("MMC transaction recovers real process termination before and after decision", KilledChildRecovery);
    }

    internal static bool TryRunChild(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--mmc-transaction-child") return false;
        try {
            if (args.Length != 5 || !Guid.TryParse(args[3], out Guid id)) throw new Exception("Invalid controlled child request.");
            if (args[1] == "gate") {
                try { using var lease = MmcAdmissionGate.Acquire(args[2], id, TimeSpan.FromMilliseconds(150)); exitCode = 12; }
                catch (IOException) { exitCode = 0; }
                return true;
            }
            using var owned = MmcAdmissionGate.Acquire(args[2], id, TimeSpan.Zero);
            var request = Request(args[2], id);
            MmcTransactionCheckpoint stop = Enum.Parse<MmcTransactionCheckpoint>(args[1]);
            MmcFileTransactions.Execute(owned, request, checkpoint: (phase, index) => {
                if (phase != stop) return;
                File.WriteAllText(args[4], "ready");
                Thread.Sleep(Timeout.Infinite);
            });
            exitCode = 13;
        } catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 14; }
        return true;
    }

    private static void Commit() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        bool beforeDecision = false;
        var result = MmcFileTransactions.Execute(lease, fixture.Request(), checkpoint: (stage, _) => {
            if (stage != MmcTransactionCheckpoint.BeforeDecision) return;
            beforeDecision = true;
            Equal(Encoding.UTF8.GetString(Definition(fixture.Id, 2)), File.ReadAllText(fixture.Definition));
            Equal(0, Directory.GetFiles(fixture.Transactions, "CommitDecided.record", SearchOption.AllDirectories).Length);
        });
        Equal(MmcTransactionOutcome.Committed, result.Outcome); Equal(true, beforeDecision);
        Equal("new-library", File.ReadAllText(fixture.Library));
        Equal(0, Directory.GetFileSystemEntries(fixture.Transactions).Length);
        Equal(MmcTransactionOutcome.Committed, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
    }
    private static void Rollback() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        var result = MmcFileTransactions.Execute(lease, fixture.Request(), checkpoint: (stage, _) => {
            if (stage == MmcTransactionCheckpoint.BeforeDecision) throw new MmcTransactionInterruptionException("controlled interruption");
        });
        Equal(MmcTransactionOutcome.Interrupted, result.Outcome);
        Equal(MmcTransactionOutcome.Aborted, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal("old-library", File.ReadAllText(fixture.Library)); Equal(false, File.Exists(fixture.Added));
        Equal(Encoding.UTF8.GetString(Definition(fixture.Id, 1)), File.ReadAllText(fixture.Definition));
        Equal(MmcTransactionOutcome.Aborted, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
    }
    private static void Forward() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        var result = MmcFileTransactions.Execute(lease, fixture.Request(), checkpoint: (stage, _) => {
            if (stage == MmcTransactionCheckpoint.CommitDecided) throw new MmcTransactionInterruptionException("controlled interruption");
        });
        Equal(MmcTransactionOutcome.Committed, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal("new-library", File.ReadAllText(fixture.Library)); Equal("new-file", File.ReadAllText(fixture.Added));
    }
    private static void CancellationBoundary() {
        foreach (bool afterDecision in new[] { false, true }) {
            using Fixture fixture = new(); using var lease = fixture.Lease(); using CancellationTokenSource cancellation = new();
            var result = MmcFileTransactions.Execute(lease, fixture.Request(), cancellation.Token, (stage, index) => {
                if (afterDecision ? stage == MmcTransactionCheckpoint.CommitDecided : stage == MmcTransactionCheckpoint.AfterMutation && index == 0)
                    cancellation.Cancel();
            });
            Equal(afterDecision ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, result.Outcome);
            Equal(afterDecision ? "new-library" : "old-library", File.ReadAllText(fixture.Library));
        }
    }
    private static void ReplacementRace() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); bool raced = false;
        var result = MmcFileTransactions.Execute(lease, fixture.Request(), checkpoint: (stage, index) => {
            if (stage == MmcTransactionCheckpoint.BeforeMutation && index == 0 && !raced) {
                raced = true; File.WriteAllText(fixture.Library, "third-party-race");
            }
        });
        Equal(MmcTransactionOutcome.RecoveryRequired, result.Outcome);
        string tx = Path.Combine(fixture.Transactions, result.TransactionId.ToString("N"));
        Equal(true, Directory.GetFiles(tx, "backup-*.bin").Any(p => File.ReadAllText(p) == "third-party-race"));
        Equal("old-library", File.ReadAllText(Path.Combine(tx, "old-0.bin")));
        Equal(MmcTransactionOutcome.RecoveryRequired, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
    }
    private static void CurrentConflict() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        var result = InterruptPrepared(fixture, lease);
        File.WriteAllText(fixture.Library, "third-party-current");
        Equal(MmcTransactionOutcome.RecoveryRequired, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal("third-party-current", File.ReadAllText(fixture.Library));
    }
    private static void CorruptRecovery() {
        foreach (bool journal in new[] { false, true }) {
            using Fixture fixture = new(); using var lease = fixture.Lease();
            var result = InterruptPrepared(fixture, lease);
            string tx = Path.Combine(fixture.Transactions, result.TransactionId.ToString("N"));
            File.WriteAllText(Path.Combine(tx, journal ? "prepared.record" : "old-0.bin"), "damaged");
            Equal(MmcTransactionOutcome.RecoveryRequired, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
            Equal("old-library", File.ReadAllText(fixture.Library));
        }
    }
    private static void UnknownMissingTarget() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); var result = InterruptPrepared(fixture, lease);
        File.Delete(fixture.Library);
        Equal(MmcTransactionOutcome.RecoveryRequired, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal(false, File.Exists(fixture.Library));
    }
    private static void DeleteRollback() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        var request = fixture.Request(); var changes = request.Mutations.ToArray();
        changes[0] = new("libraries/local.jar", Hash(Encoding.UTF8.GetBytes("old-library")), null, null);
        var result = MmcFileTransactions.Execute(lease, request with { Mutations = changes }, checkpoint: (stage, index) => {
            if (stage == MmcTransactionCheckpoint.AfterMutation && index == 0) throw new MmcTransactionInterruptionException("deleted");
        });
        Equal(false, File.Exists(fixture.Library));
        Equal(MmcTransactionOutcome.Aborted, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal("old-library", File.ReadAllText(fixture.Library));
    }
    private static void InvalidRequests() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); var request = fixture.Request();
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, request with { InstanceId = Guid.NewGuid() }));
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, request with { ExpectedGeneration = 8, NewGeneration = 9 }));
        var changes = request.Mutations.ToArray(); changes[0] = changes[0] with { ExpectedNewSha256 = new string('0', 64) };
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, request with { Mutations = changes }));
        changes = request.Mutations.ToArray(); changes[0] = changes[0] with { RelativePath = "PCL/MmcTransactions/fake" };
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, request with { Mutations = changes }));
        Equal("old-library", File.ReadAllText(fixture.Library));
    }
    private static void FileBackedSource() {
        using Fixture fixture = new(); using var lease = fixture.Lease(); var request = fixture.Request();
        string relative = "PCL/MmcRuntime/.staging/" + Guid.NewGuid().ToString("N") + "/local.jar";
        string source = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        // A larger-than-copy-buffer fixture proves the source path traverses streaming I/O, not a DTO byte[].
        using (FileStream output = new(source, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            byte[] chunk = Encoding.UTF8.GetBytes(new string('x', 81920));
            for (int i = 0; i < 4; i++) output.Write(chunk);
        }
        string hash;
        using (FileStream input = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var changes = request.Mutations.ToArray(); changes[0] = new("libraries/local.jar", changes[0].ExpectedOldSha256, null, hash, relative);
        var streamed = request with { Mutations = changes };
        var invalid = changes.ToArray(); invalid[0] = invalid[0] with { NewSourceRelativePath = "../outside.jar" };
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, request with { Mutations = invalid }));
        File.AppendAllText(source, "tampered");
        Throws<IOException>(() => MmcFileTransactions.Execute(lease, streamed));
        using (FileStream trim = new(source, FileMode.Open, FileAccess.Write, FileShare.None)) trim.SetLength(4 * 81920);
        var result = MmcFileTransactions.Execute(lease, streamed, checkpoint: (stage, _) => {
            if (stage == MmcTransactionCheckpoint.Prepared) {
                File.WriteAllText(source, "changed after prepared");
                throw new MmcTransactionInterruptionException("journal owns its verified source copy");
            }
        });
        Equal(MmcTransactionOutcome.Interrupted, result.Outcome);
        // Recovery is independent of the mutable source path after Prepared.
        Equal(MmcTransactionOutcome.Aborted, MmcFileTransactions.Recover(lease, result.TransactionId).Outcome);
        Equal("old-library", File.ReadAllText(fixture.Library));
        File.Copy(Path.Combine(fixture.Root, "PCL", "MmcTransactionHistory", result.TransactionId.ToString("N"), "new-0.bin"), source, true);
        Equal(MmcTransactionOutcome.Committed, MmcFileTransactions.Execute(lease, streamed).Outcome);
        using FileStream published = File.OpenRead(fixture.Library);
        Equal(hash, Convert.ToHexString(SHA256.HashData(published)).ToLowerInvariant());
    }
    private static void CrossProcessGate() {
        using Fixture fixture = new(); using var lease = fixture.Lease();
        using Process child = StartChild("gate", fixture, Guid.NewGuid(), Path.Combine(fixture.Scope, "unused"));
        if (!child.WaitForExit(10000)) { child.Kill(true); throw new Exception("Gate child timed out."); }
        Equal(0, child.ExitCode);
    }
    private static void KilledChildRecovery() {
        foreach (var stop in new[] { MmcTransactionCheckpoint.Prepared, MmcTransactionCheckpoint.AfterMutation, MmcTransactionCheckpoint.CommitDecided }) {
            using Fixture fixture = new(); string signal = Path.Combine(fixture.Scope, "child-ready");
            using (Process child = StartChild(stop.ToString(), fixture, fixture.Id, signal)) {
                try {
                    var watch = Stopwatch.StartNew();
                    while (!File.Exists(signal) && !child.HasExited && watch.ElapsedMilliseconds < 12000) Thread.Sleep(20);
                    if (!File.Exists(signal)) throw new Exception("Controlled transaction child did not reach checkpoint.");
                } finally { if (!child.HasExited) child.Kill(true); if (!child.WaitForExit(5000)) throw new Exception("Controlled child did not exit."); }
            }
            using var lease = fixture.Lease();
            var result = MmcFileTransactions.RecoverPending(lease).Single();
            Equal(stop == MmcTransactionCheckpoint.CommitDecided ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, result.Outcome);
            Equal(stop == MmcTransactionCheckpoint.CommitDecided ? "new-library" : "old-library", File.ReadAllText(fixture.Library));
        }
    }
    private static Process StartChild(string mode, Fixture fixture, Guid id, string signal) {
        string executable = Environment.ProcessPath ?? throw new Exception("No process path.");
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--mmc-transaction-child", mode, fixture.Root, id.ToString("D"), signal }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Could not start controlled child.");
    }
    private static MmcTransactionResult InterruptPrepared(Fixture fixture, MmcAdmissionLease lease) =>
        MmcFileTransactions.Execute(lease, fixture.Request(), checkpoint: (stage, _) => {
            if (stage == MmcTransactionCheckpoint.Prepared) throw new MmcTransactionInterruptionException("prepared");
        });
    private static MmcFileTransactionRequest Request(string root, Guid id) {
        byte[] newer = Encoding.UTF8.GetBytes("new-library"), added = Encoding.UTF8.GetBytes("new-file"), definition = Definition(id, 2);
        return new(id, 1, 2, [new("libraries/local.jar", Hash(File.ReadAllBytes(Path.Combine(root, "libraries", "local.jar"))), newer, Hash(newer)),
            new("minecraft/config/added.txt", null, added, Hash(added)),
            new("PCL/InstanceDefinition.json", Hash(File.ReadAllBytes(Path.Combine(root, "PCL", "InstanceDefinition.json"))), definition, Hash(definition))]);
    }
    private static byte[] Definition(Guid id, long generation) => Encoding.UTF8.GetBytes($$$"""{"schemaVersion":1,"managementKind":"Mmc","formatProfile":"prism-v1","instanceId":"{{{id:D}}}","generation":{{{generation}}},"layout":{"gameRootLeaf":"minecraft"}}""");
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Fixture : IDisposable {
        public string Scope { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-transaction-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public Guid Id { get; } = Guid.NewGuid();
        public string Definition => Path.Combine(Root, "PCL", "InstanceDefinition.json");
        public string Library => Path.Combine(Root, "libraries", "local.jar");
        public string Added => Path.Combine(Root, "minecraft", "config", "added.txt");
        public string Transactions => Path.Combine(Root, "PCL", "MmcTransactions");
        public Fixture() {
            Root = Path.Combine(Scope, "versions", "sample");
            Directory.CreateDirectory(Path.GetDirectoryName(Definition)!); Directory.CreateDirectory(Path.GetDirectoryName(Library)!);
            File.WriteAllBytes(Definition, MmcTransactionTests.Definition(Id, 1)); File.WriteAllText(Library, "old-library");
        }
        public MmcAdmissionLease Lease() => MmcAdmissionGate.Acquire(Root, Id, TimeSpan.Zero);
        public MmcFileTransactionRequest Request() => MmcTransactionTests.Request(Root, Id);
        public void Dispose() { if (Directory.Exists(Scope)) Directory.Delete(Scope, true); }
    }
}
