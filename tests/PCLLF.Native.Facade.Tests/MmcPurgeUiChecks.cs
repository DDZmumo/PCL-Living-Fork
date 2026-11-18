using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Original LF host regressions; every writable path belongs to the isolated GUID fixture.
internal static class MmcPurgeUiChecks {
    internal static void Run(FixtureEnvironment environment) => RunCheck(environment, scope => scope.Complete(false),
        "mmc-purge-ui: actual Main confirmation, exact durable preview, released admission, shared busy lifetime and Purged catalog; adjacent/shared/recreated-source world bytes preserved.");
    internal static void RunDeclinedConfirmation(FixtureEnvironment environment) => RunCheck(environment, scope => scope.Decline(),
        "mmc-purge-declined: declined actual Main confirmation keeps the complete quarantine and records no purge preparation or decision.");
    internal static void RunCancellationBeforeDecision(FixtureEnvironment environment) => RunCheck(environment, scope => scope.CancelBeforeDecision(),
        "mmc-purge-cancel-before-decision: cancellation at the final actual Main decision callback preserves every quarantined byte and removes undecided preparation.");
    internal static void RunCancellationAfterDecision(FixtureEnvironment environment) => RunCheck(environment, scope => scope.Complete(true),
        "mmc-purge-cancel-after-decision: late actual Main cancellation finishes the decided cleanup and preserves the recreated source and its new world.");

    private static void RunCheck(FixtureEnvironment environment, Action<Scope> check, string message) {
        using FileTaskScope files = new(environment);
        using (Scope scope = new(files)) check(scope);
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS " + message);
    }

    private sealed class Scope : IDisposable {
        private const string World = "minecraft/saves/player-world/level.dat";
        private const string ReplacementWorld = "minecraft/saves/replacement-world/level.dat";
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcPurgeUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstancePurgeResult> published = new();
        private readonly ConcurrentQueue<MmcInstancePurgeCheckpoint> checkpoints = new();
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly ConcurrentDictionary<Thread, byte> observedThreads = new();
        private readonly Dictionary<string, string> preserved = new(StringComparer.OrdinalIgnoreCase);
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action previousRefresh;
        private readonly string root, game, source, quarantine, operation, quarantineBefore, operationBefore;
        private readonly byte[] worldBytes = Encoding.UTF8.GetBytes("current player world " + Guid.NewGuid().ToString("N"));
        private readonly MmcInstanceInspection original;
        private readonly MmcLifecycleCatalogEntry selection;
        private ModLoader.LoaderCombo<string>? current;
        private MmcInstancePurgePreview? confirmed;
        private Func<MmcInstancePurgePreview, int> confirmation = _ => 1;
        private Action<MmcInstancePurgeCheckpoint>? checkpoint;
        private Action? decisionReady;
        private int confirmationCalls, decisionCalls, planChecks;
        private string? replacementBefore;

        internal Scope(FileTaskScope files) {
            this.files = files;
            root = Path.Combine(files.Root, "mmc-purge-ui-" + Guid.NewGuid().ToString("N"));
            game = Path.Combine(root, "minecraft"); Directory.CreateDirectory(game); files.SelectMinecraft(game);
            source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            PutBytes(source, World, worldBytes); Directory.CreateDirectory(Path.Combine(source, "minecraft", "empty-player-directory"));
            original = MmcInstanceInspector.Read(source);
            var deleted = new MmcInstanceLifecycleBackend().DeleteAsync(new(source, original.Definition!.InstanceId,
                original.Definition.Generation, original.PrimarySha256!)).GetAwaiter().GetResult();
            Native.Check(deleted.Status == MmcInstanceBackendStatus.Succeeded && deleted.Quarantined &&
                !Directory.Exists(source), "Actual purge UI fixture did not enter completed quarantine: " + deleted.Failure);
            quarantine = deleted.CurrentRoot!; operation = deleted.OperationDirectory!;
            selection = Catalog();
            Native.Check(selection.State == MmcLifecycleCatalogState.Quarantined && selection.CanPurge && selection.CanRestore,
                "Actual catalog did not authorize the completed quarantine preview.");
            quarantineBefore = Tree(quarantine); operationBefore = Tree(operation);
            Preserve(Path.Combine(game, "versions", "adjacent"), "minecraft/saves/neighbor/level.dat", "adjacent world");
            Preserve(Path.Combine(game, "libraries"), "shared-fixture.jar", "shared library");
            Preserve(Path.Combine(game, "assets"), "objects/shared-fixture", "shared asset");
            Preserve(Path.Combine(root, "java"), "bin/java.exe", "inert external Java; never executed");
            preserved.Add(Path.Combine(root, "cache"), Tree(Path.Combine(root, "cache")));
            Preserve(Path.Combine(root, "metadata"), "keep.txt", "external metadata cache");
            preserved.Add(Path.Combine(root, "owned.zip"), Hash(File.ReadAllBytes(Path.Combine(root, "owned.zip"))));

            Native.Check(!Busy, "A previous MMC deletion, cleanup or recovery still owns the shared UI fence.");
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)
                Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            previousRefresh = scheduler.RefreshExtraDownload;
            scheduler.RefreshExtraDownload = () => { }; // Only absent offscreen download-button presentation.
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => {
                Volatile.Write(ref current, task); tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task);
            }));
            Set("Confirm", new Func<MmcInstancePurgePreview, int>(preview => {
                AssertWorkerActive("confirmation"); AssertSharedFenceHeld(); AssertDialogUnlocked(preview);
                Interlocked.Increment(ref confirmationCalls); confirmed = preview;
                Native.Check(preview.InstanceId == original.Definition!.InstanceId && preview.Generation == original.Definition.Generation &&
                    preview.PrimarySha256 == original.PrimarySha256 && Native.Full(preview.SourceRoot) == Native.Full(source) &&
                    Native.Full(preview.QuarantineRoot) == Native.Full(quarantine) && Native.Full(preview.OperationDirectory) == Native.Full(operation),
                    "Actual Main confirmation changed the selected quarantine identity, generation or roots.");
                Native.Check(preview.Entries.Any(entry => entry.RelativePath == World && !entry.Directory && entry.Role == MmcInstancePurgeRole.Game && entry.Sha256 == Hash(worldBytes)) &&
                    preview.Entries.Any(entry => entry.Role == MmcInstancePurgeRole.SavedSource) &&
                    preview.Entries.Any(entry => entry.RelativePath == "minecraft/empty-player-directory" && entry.Directory) &&
                    preview.TotalBytes == preview.Entries.Sum(entry => entry.SizeBytes), "Actual Main confirmation omitted player/private bytes or its complete size.");
                return confirmation(preview);
            }));
            Set("CheckpointObserved", new Action<MmcInstancePurgeCheckpoint, string>((phase, _) => {
                AssertWorkerActive(phase.ToString()); checkpoints.Enqueue(phase);
                if (phase == MmcInstancePurgeCheckpoint.PlanPrepared) AssertConfirmedPlan();
                checkpoint?.Invoke(phase);
            }));
            Set("DecisionReady", new Action(() => {
                AssertWorkerActive("final decision callback"); Interlocked.Increment(ref decisionCalls); decisionReady?.Invoke();
            }));
            Set("Published", new Action<MmcInstancePurgeResult>(result => {
                AssertWorkerActive("publication"); AssertSharedFenceHeld(); published.Enqueue(result);
            }));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Enqueue(error)));
        }

        private ModLoader.LoaderCombo<string> Current => Volatile.Read(ref current) ?? throw new InvalidOperationException("Actual Main purge did not register its Loader.");
        private ModLoader.LoaderTask<string, MmcInstancePurgeResult> Worker => (ModLoader.LoaderTask<string, MmcInstancePurgeResult>)Current.Loaders.Single();
        private static bool Busy => (bool)Native.Property(Native.Type("PCL.MmcDeleteUi"), "HasActiveOperation").GetValue(null)!;
        private static bool OperationCompleted(ModLoader.LoaderCombo<string> task) =>
            (bool)Native.Property(task.Loaders.Single().GetType(), "OperationCompleted").GetValue(task.Loaders.Single())!;
        private void Set(string name, object value) {
            var property = Native.Property(ui, name); previous.Add(property, property.GetValue(null)); property.SetValue(null, value);
        }
        private MmcLifecycleCatalogEntry Catalog() => MmcInstanceLifecycleCatalog.Read(game).Entries.Single(entry => Native.Full(entry.OperationDirectory) == Native.Full(operation));
        private void AssertWorkerActive(string stage) {
            Native.Check(Busy && !OperationCompleted(Current), "Shared MMC UI fence was released before the real purge worker returned at " + stage + ".");
            var thread = Native.Get(Worker, "LastRunningThread") as Thread;
            Native.Check(thread is { IsAlive: true }, "Actual Main purge worker was absent at " + stage + ".");
            observedThreads.TryAdd(thread!, 0);
        }
        private static void AssertSharedFenceHeld() {
            object? unexpected = null;
            try { unexpected = Native.Static("PCL.MmcDeleteUi", "AcquireOperationFence"); }
            catch (IOException) { return; }
            finally { if (unexpected is not null) Native.Call(unexpected.GetType(), "AbandonUnstarted", unexpected); }
            throw new InvalidOperationException("Purge did not exclude a simultaneous deletion/recovery through their actual shared fence.");
        }
        private static void AssertDialogUnlocked(MmcInstancePurgePreview preview) {
            foreach (string path in new[] { preview.SourceRoot, preview.QuarantineRoot }) {
                using var lease = MmcAdmissionGate.Acquire(path, preview.InstanceId, TimeSpan.Zero); lease.AssertHeld();
                object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, path)!;
                Native.Check((int)Native.Get(state, "PauseCount")! == 0, "Permanent-cleanup confirmation retained configuration quiescence.");
            }
        }
        private void AssertConfirmedPlan() {
            // Read the real durable journal rather than reconstructing its record format.
            var assembly = typeof(MmcInstanceLifecycleBackend).Assembly;
            Type lifecycleType = assembly.GetType("PCLCS.MmcInstanceLifecycleJournal", true)!;
            Type purgeType = assembly.GetType("PCLCS.MmcInstancePurgeJournal", true)!;
            object lifecycle = Native.Call(lifecycleType, "Read", null, operation)!;
            object journal = Native.Call(purgeType, "Read", null, lifecycle)!;
            object plan = Native.Property(purgeType, "Plan").GetValue(journal)!;
            Native.Check(confirmed is not null && (string)Native.Property(plan.GetType(), "SnapshotFingerprint").GetValue(plan)! == confirmed.SnapshotFingerprint,
                "Actual purge prepared a different snapshot from the one explicitly confirmed by Main.");
            Interlocked.Increment(ref planChecks);
        }
        private void Start() {
            object? returned = Native.Static("PCL.MmcPurgeUi", "Start", selection);
            Native.Check(ReferenceEquals(returned, Current) && tasks.Count == 1, "Actual purge did not return its registered single Loader root.");
        }
        private void Wait(ModBase.LoadState expected) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (Busy || Current.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading ||
                Native.Get(Worker, "LastRunningThread") is Thread { IsAlive: true } || observedThreads.Keys.Any(thread => thread.IsAlive)) {
                if (Native.Get(Worker, "LastRunningThread") is Thread thread) observedThreads.TryAdd(thread, 0);
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual Main purge Loader/worker did not return: " + Current.Error);
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            Native.Check(Current.State == expected && OperationCompleted(Current) && errors.IsEmpty,
                "Actual Main purge ended unexpectedly: state=" + Current.State + "; expected=" + expected + "; " + Current.Error + "; " + string.Join("; ", errors));
        }

        internal void Complete(bool cancelAfterDecision) {
            checkpoint = phase => {
                if (phase != MmcInstancePurgeCheckpoint.CommitDecided) return;
                Native.Check(File.ReadAllBytes(Path.Combine(quarantine, World.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(worldBytes),
                    "Purge changed player-world bytes before its durable decision.");
                PutBytes(source, ReplacementWorld, Encoding.UTF8.GetBytes("new source owner world"));
                PutBytes(source, "managed.json", Encoding.UTF8.GetBytes("{\"id\":\"replacement\"}")); replacementBefore = Tree(source);
                if (cancelAfterDecision) { Current.Cancel(); AssertWorkerActive("ignored post-decision cancellation"); }
            };
            Start(); Wait(ModBase.LoadState.Finished);
            Native.Check(confirmationCalls == 1 && decisionCalls == 1 && planChecks == 1 && checkpoints.Contains(MmcInstancePurgeCheckpoint.Completed),
                "Actual purge skipped or repeated confirmation, final decision, fixed plan or completion.");
            var result = published.Single();
            Native.Check(ReferenceEquals(result, Worker.Output) && result.Status == MmcInstanceBackendStatus.Succeeded && result.DecisionRecorded &&
                result.Completed && result.RemainingPaths.IsEmpty && result.QuarantineRoot is null && !Directory.Exists(quarantine),
                "Actual Main purge did not publish its completed backend result.");
            var catalog = Catalog();
            Native.Check(catalog.State == MmcLifecycleCatalogState.Purged && !catalog.CanRestore && !catalog.CanRecover && !catalog.CanPurge,
                "Completed actual Main purge remained restorable, recoverable or purgeable in the real catalog.");
            Native.Check(replacementBefore is not null && Tree(source) == replacementBefore &&
                File.ReadAllText(Path.Combine(source, ReplacementWorld.Replace('/', Path.DirectorySeparatorChar))) == "new source owner world",
                "Permanent cleanup crossed into the recreated source or its new world.");
            AssertPreserved();
        }
        internal void Decline() {
            confirmation = _ => 2;
            Start(); Wait(ModBase.LoadState.Canceled);
            Native.Check(confirmationCalls == 1 && decisionCalls == 0 && planChecks == 0 && checkpoints.IsEmpty && published.IsEmpty,
                "Declined confirmation reached purge preparation, a decision or publication.");
            AssertRestorable();
        }
        internal void CancelBeforeDecision() {
            decisionReady = () => { Current.Cancel(); AssertWorkerActive("canceled final decision callback"); };
            Start(); Wait(ModBase.LoadState.Canceled);
            Native.Check(confirmationCalls == 1 && decisionCalls == 1 && planChecks == 1 && checkpoints.Contains(MmcInstancePurgeCheckpoint.BeforeDecision) &&
                !checkpoints.Contains(MmcInstancePurgeCheckpoint.CommitDecided) && !checkpoints.Contains(MmcInstancePurgeCheckpoint.BeforeEntryDelete) && published.IsEmpty,
                "Final callback cancellation crossed the irreversible decision or published cleanup.");
            Native.Check(Worker.Output is { Status: MmcInstanceBackendStatus.Cancelled, DecisionRecorded: false, Completed: false },
                "Final actual Main callback cancellation lost the backend's undecided canceled result.");
            AssertRestorable();
        }
        private void AssertRestorable() {
            Native.Check(Tree(quarantine) == quarantineBefore && Tree(operation) == operationBefore && !Directory.Exists(source) &&
                File.ReadAllBytes(Path.Combine(quarantine, World.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(worldBytes),
                "Undecided actual Main cleanup changed quarantine/world bytes or retained a new purge record.");
            var catalog = Catalog();
            Native.Check(catalog.State == MmcLifecycleCatalogState.Quarantined && catalog.CanRestore && catalog.CanPurge && !catalog.CanRecover,
                "Undecided actual Main cleanup did not retain the complete restorable quarantine.");
            AssertPreserved();
        }
        private void Preserve(string path, string relative, string text) {
            PutBytes(path, relative, Encoding.UTF8.GetBytes(text)); preserved.Add(path, Tree(path));
        }
        private void AssertPreserved() {
            foreach (var pair in preserved)
                Native.Check((File.Exists(pair.Key) ? Hash(File.ReadAllBytes(pair.Key)) : Tree(pair.Key)) == pair.Value,
                    "Actual Main permanent cleanup changed adjacent/shared/source/Java fixture bytes: " + pair.Key);
        }
        private static void PutBytes(string path, string relative, byte[] bytes) {
            string target = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static string Tree(string path) => string.Join('\n', Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(entry => Path.GetRelativePath(path, entry) + ":" + (Directory.Exists(entry) ? "directory" : Hash(File.ReadAllBytes(entry)))));
        public void Dispose() {
            // Keep every hook/root in place until the genuine threads, Loader producers,
            // cancellation callbacks and posted Dispatcher work have actually drained.
            files.FinishActualTasks();
            Native.Check(!Busy && tasks.All(task => task.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) && OperationCompleted(task)) &&
                observedThreads.Keys.All(thread => !thread.IsAlive), "Refusing to restore purge UI hooks while its real operation remains active.");
            foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
            scheduler.RefreshExtraDownload = previousRefresh;
        }
    }
}
