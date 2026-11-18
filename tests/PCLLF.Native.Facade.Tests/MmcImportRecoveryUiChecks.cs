using System.Windows;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// The exact automatic backend entry used by Main. Manual catalog/picker/retry UI no longer exists.
internal static partial class MmcImportRecoveryUiChecks {
    internal static void RunRecovery(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var data = new MmcPlatformOriginUiTaskChecks.Data(files, "automatic-import-completion", null);
        var select = new PageSelectRight(); ModMinecraft.McInstanceList = [];
        var controller = Native.Property(select.GetType(), "Service").GetValue(select)!; Native.Call(controller.GetType(), "Render", controller);
        Native.Check(select.FindName("BtnMmcImportRecovery") is null && select.FindName("BtnMmcLegacyRecovery") is null && select.FindName("BtnMmcRecovery") is null &&
            ((FrameworkElement)select.FindName("PanEmpty")).Visibility == Visibility.Visible, "The actual empty-list BAML retained manual MMC recovery controls.");
        using var fixture = new Fixture(data);
        string original = data.Primary.Sha256;
        var committed = fixture.Import("decided", new() { Transaction = (phase, _) => { if (phase == MmcTransactionCheckpoint.CommitDecided) throw new MmcTransactionInterruptionException("owned single decided failure"); } });
        Native.Check(committed.Status == MmcInstanceBackendStatus.Succeeded && committed.Definition?.Generation == 1 && MmcInstanceInspector.Read(fixture.Target("decided")).IsConsistent &&
            !File.Exists(Path.Combine(fixture.Target("decided"), "PCL", "MmcImportPending.json")), "A decided import required a manual recovery action: " + committed.Failure);
        using var cancel = new CancellationTokenSource();
        var rolled = fixture.Import("cancelled", new() { Token = cancel.Token, Transaction = (phase, _) => { if (phase == MmcTransactionCheckpoint.Prepared) cancel.Cancel(); } });
        Native.Check(rolled.Status == MmcInstanceBackendStatus.Cancelled && rolled.WorkingRoot is null && !Directory.Exists(fixture.Target("cancelled")) &&
            !Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").Any(), "A cancelled import left an erroneous version or required a manual recovery action: " + rolled.Failure);
        using var afterMutationCancel = new CancellationTokenSource(); bool applied = false;
        var afterMutation = fixture.Import("cancelled-after-mutation", new() { Token = afterMutationCancel.Token, Transaction = (phase, _) => {
            if (phase == MmcTransactionCheckpoint.AfterMutation && !applied) { applied = true; afterMutationCancel.Cancel(); }
        } });
        Native.Check(applied && afterMutation.Status == MmcInstanceBackendStatus.Cancelled && afterMutation.WorkingRoot is null && !Directory.Exists(fixture.Target("cancelled-after-mutation")) &&
            !Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").Any(), "AfterMutation cancellation left its owned backups or an erroneous version: " + afterMutation.Failure + " / WorkingRoot=" + afterMutation.WorkingRoot);
        Native.Check(MmcImportRecovery.Read(data.Game).Entries.Where(entry => entry.Name is "decided" or "cancelled" or "cancelled-after-mutation").All(entry => !entry.CanRecover && !entry.CanRetry), "Automatically finished operations retained manual catalog actions.");
        Native.Check(data.Primary.Sha256 == original && data.Read("minecraft/saves/world/level.dat") == "personal world", "Automatic completion changed an existing player instance.");
        Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS automatic MMC import: actual Main BAML has no recovery buttons; genuine decided transaction finishes, undecided cancellation rolls back/discards its owned temporary tree; journals and player instance remain; no JVM/game/network.");
    }

    internal static void RunRetry(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var data = new MmcPlatformOriginUiTaskChecks.Data(files, "automatic-import-discard", null);
        using var fixture = new Fixture(data);
        using var cancel = new CancellationTokenSource();
        var partial = fixture.Import("payload-cancel", new() { Token = cancel.Token, Payload = (phase, _) => { if (phase == MmcInitialPayloadCheckpoint.Moved) cancel.Cancel(); } });
        Native.Check(partial.Status == MmcInstanceBackendStatus.Cancelled && partial.WorkingRoot is null && !Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").Any(), "Partially placed initial payload was left as a broken version: " + partial.Failure);
        string? changed = null;
        using var foreignCancellation = new CancellationTokenSource();
        var foreign = fixture.Import("foreign-payload", new() { Token = foreignCancellation.Token, Payload = (phase, _) => {
            if (phase != MmcInitialPayloadCheckpoint.Moved) return;
            string temporary = Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").Single();
            changed = Directory.EnumerateFiles(Path.Combine(temporary, "minecraft"), "*", SearchOption.AllDirectories).Single();
            File.WriteAllText(changed, "changed foreign data must survive"); foreignCancellation.Cancel();
        } });
        Native.Check(foreign.Status == MmcInstanceBackendStatus.NeedsRecovery && changed is not null && File.ReadAllText(changed) == "changed foreign data must survive" && !Directory.Exists(fixture.Target("foreign-payload")),
            "Automatic discard guessed ownership of changed payload bytes or published them.");
        string unknown = Path.Combine(data.Game, "PCL", "MmcImportOperations", "existing-unknown-record"); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "historical recovery evidence");
        var normal = fixture.Import("fresh-after-cancel", new());
        Native.Check(normal.Status == MmcInstanceBackendStatus.Succeeded && File.ReadAllText(Path.Combine(unknown, "keep.txt")) == "historical recovery evidence" && File.ReadAllText(changed!) == "changed foreign data must survive" &&
            data.Read("minecraft/saves/world/level.dat") == "personal world", "Fresh import cleaned historical/foreign data or changed player worlds.");
        Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS automatic MMC rollback: partial authored payload cancels without a leftover version; changed/unknown evidence is preserved and refused; a fresh import succeeds without manual retry UI; no JVM/game/network.");
    }
    internal static void RunCleanupGuards(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var data = new MmcPlatformOriginUiTaskChecks.Data(files, "automatic-import-cleanup-guards", null);
        using var fixture = new Fixture(data);
        foreach (string mode in new[] { "ads", "replacement", "interruption" }) {
            using var cancel = new CancellationTokenSource();
            var existing = Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? temporary = null, payload = null, stream = null; bool boundary = false;
            var result = fixture.Import("guard-" + mode, new() {
                Token = cancel.Token,
                Payload = (phase, _) => {
                    if (phase != MmcInitialPayloadCheckpoint.Moved) return;
                    temporary = Directory.EnumerateDirectories(Path.Combine(data.Game, "versions"), ".pcllf-mmc-import-*").Single(path => !existing.Contains(path));
                    payload = Path.Combine(temporary, "minecraft", "config", "owned.cfg");
                    if (mode == "ads") { stream = payload + ":foreign-evidence"; File.WriteAllText(stream, "foreign ADS must remain"); }
                    cancel.Cancel();
                },
                Cleanup = (phase, _) => {
                    if (mode == "replacement" && phase == MmcInstancePurgeCheckpoint.PlanPrepared) {
                        boundary = true; File.Move(payload!, payload + ".original"); File.WriteAllText(payload!, "foreign replacement must remain");
                    }
                    if (mode == "interruption" && phase == MmcInstancePurgeCheckpoint.BeforeDecision) { boundary = true; throw new IOException("owned interruption before atomic import cleanup commit"); }
                }
            });
            Native.Check(result.Status == MmcInstanceBackendStatus.NeedsRecovery && temporary is not null && Directory.Exists(temporary) &&
                File.Exists(Path.Combine(temporary, "PCL", "MmcImportPending.json")) && File.Exists(Path.Combine(temporary, ".pclignore")) && !Directory.Exists(fixture.Target("guard-" + mode)),
                "Automatic import cleanup lost evidence/markers or deleted an unconfirmed object: " + mode + " / " + result.Failure);
            if (mode == "ads") Native.Check(File.ReadAllText(stream!) == "foreign ADS must remain", "Import cleanup deleted an unlisted NTFS stream.");
            if (mode == "replacement") {
                Native.Check(boundary, "Import cleanup never reached PlanPrepared; no replacement was attempted: " + result.Failure + " / WorkingRoot=" + result.WorkingRoot);
                Native.Check(File.ReadAllText(payload!) == "foreign replacement must remain" && File.Exists(payload + ".original"), "Import cleanup deleted a replacement after its fixed inventory.");
            }
            if (mode == "interruption") {
                Native.Check(boundary, "Import cleanup never reached BeforeDecision; interruption was not injected: " + result.Failure + " / WorkingRoot=" + result.WorkingRoot);
                Native.Check(File.ReadAllText(payload!) == "author data", "An interrupted cleanup partially removed its original root.");
            }
            var entry = MmcImportRecovery.Read(data.Game).Entries.Single(entry => entry.Name == "guard-" + mode);
            if (mode is "replacement" or "interruption") Native.Check(File.Exists(Path.Combine(entry.OperationDirectory, "automatic-cleanup.intent.record")) &&
                !File.Exists(Path.Combine(entry.OperationDirectory, "automatic-cleanup.completed.record")), "Interrupted import cleanup lost its external durable intent.");
        }
        Native.Check(data.Read("minecraft/saves/world/level.dat") == "personal world", "Cleanup guards changed the existing player instance.");
        Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS automatic MMC cleanup guards: actual owned imports preserve ADS, same-name replacements and complete root/markers after an interrupted TxF cleanup; no JVM/game/network.");
    }

    private sealed class Options {
        internal CancellationToken Token;
        internal Action<MmcTransactionCheckpoint, int>? Transaction;
        internal Action<MmcInitialPayloadCheckpoint, int>? Payload;
        internal Action<MmcInstancePurgeCheckpoint, string?>? Cleanup;
    }
    private sealed class Fixture : IDisposable {
        private readonly MmcPlatformOriginUiTaskChecks.Data data;
        internal Fixture(MmcPlatformOriginUiTaskChecks.Data data) => this.data = data;
        internal string Target(string name) => Path.Combine(data.Game, "versions", name);
        internal MmcInstanceBackendResult Import(string name, Options options) {
            var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(data.Metadata, data.Caches, value => value,
                (_, _) => throw new IOException("Automatic import fixture cannot start network transfers."));
            using var source = MmcArchiveSourceReader.ReadAsync(data.OriginalZip).GetAwaiter().GetResult();
            return backend.ImportPreparedAutomaticallyAsync(source, new(data.OriginalZip, Target(name), name, new("windows", "x86_64", 8)) {
                TransactionCheckpoint = options.Transaction, PrivatePayloadCheckpoint = options.Payload, AutomaticCleanupCheckpoint = options.Cleanup
            }, options.Token).GetAwaiter().GetResult();
        }
        public void Dispose() { InstanceConfigProviders.FlushAndDisposeWithin(data.Root); }
    }
}
