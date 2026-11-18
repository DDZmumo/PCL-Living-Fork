using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcRelocationAutoRecoveryChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) { scope.Run(); scope.Finish(); }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-relocation-auto-recovery: real rename/move tasks automatically roll back undecided failures and finish committed failures after producers/leases return; changed owner markers stay preserved.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcLifecycleUi");
        private readonly Stack<Action> restore = new();
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceLifecycleResult> results = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly string root, game, otherGame, worldRelative = Path.Combine("minecraft", "saves", "current", "level.dat");
        private string expectedSource = "", expectedTarget = "", operation = "";
        private Guid expectedId;
        private int recoveries;
        private Action<MmcLifecycleCheckpoint>? checkpoint;
        private Action<string>? recovering;
        private bool finished;

        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            Action refresh = scheduler.RefreshExtraDownload; restore.Push(() => scheduler.RefreshExtraDownload = refresh); scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); }));
            Set("Published", new Action<MmcInstanceLifecycleResult>(result => results.Enqueue(result)));
            Set("CheckpointObserved", new Action<MmcLifecycleCheckpoint>(phase => checkpoint?.Invoke(phase)));
            Set("AutomaticRecoveryObserved", new Action<string>(directory => {
                // This callback executes after the actual backend Task has returned. Taking
                // both admissions and observing zero pause counts proves its producer drained.
                using var sourceLease = MmcAdmissionGate.Acquire(expectedSource, expectedId, TimeSpan.Zero);
                using var targetLease = MmcAdmissionGate.Acquire(expectedTarget, expectedId, TimeSpan.Zero);
                foreach (string path in new[] { expectedSource, expectedTarget }) {
                    object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, path)!;
                    Native.Check((int)Native.Get(state, "PauseCount")! == 0, "Automatic replay started before the real relocation pause exited.");
                }
                Interlocked.Increment(ref recoveries); operation = directory;
                recovering?.Invoke(directory);
            }));
            root = Path.Combine(files.Root, "relocation-auto-recovery"); game = Path.Combine(root, "minecraft"); otherGame = Path.Combine(root, "other-minecraft");
            Directory.CreateDirectory(game); Directory.CreateDirectory(otherGame); files.SelectMinecraft(game);
        }
        private void Set(string name, object value) {
            var property = Native.Property(ui, name); object? previous = property.GetValue(null);
            restore.Push(() => property.SetValue(null, previous)); property.SetValue(null, value);
        }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private void Wait(int count, ModBase.LoadState expected) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (tasks.Count < count || Busy || Current.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading ||
                Native.Get(Current.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true }) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Automatic relocation completion did not drain: " + Current.Error);
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            Native.Check(Current.State == expected, "Automatic relocation ended in " + Current.State + ": " + Current.Error);
        }
        private static ModMinecraft.McInstance Owner(string path) { var owner = new ModMinecraft.McInstance(path).Load(); FileTaskScope.CacheInstance(owner); return owner; }
        private void Prepare(string source, string target, Guid id) { expectedSource = source; expectedTarget = target; expectedId = id; }
        private void AssertWorld(string path) => Native.Check(File.ReadAllText(Path.Combine(path, worldRelative)) == "owned world progress", "Automatic relocation altered current player world bytes.");

        internal void Run() {
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            string world = Path.Combine(source, worldRelative); Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "owned world progress", new UTF8Encoding(false));
            var initial = MmcInstanceInspector.Read(source); var owner = Owner(source);
            string rejectedTarget = Path.Combine(game, "versions", "undecided-rename");
            Prepare(source, rejectedTarget, initial.Definition!.InstanceId);
            checkpoint = phase => { if (phase == MmcLifecycleCheckpoint.MarkerWritten) throw new MmcLifecycleInterruptionException("owned undecided rename interruption"); };
            Native.Static("PCL.MmcLifecycleUi", "StartRename", owner, "undecided-rename", null); Wait(1, ModBase.LoadState.Failed);
            var aborted = MmcInstanceInspector.Read(source);
            Native.Check(recoveries == 1 && results.IsEmpty && aborted.IsConsistent && aborted.PrimarySha256 == initial.PrimarySha256 &&
                !Directory.Exists(rejectedTarget) && !File.Exists(Path.Combine(source, "PCL", "MmcLifecyclePending.json")),
                "An undecided real rename did not automatically restore its original consistent instance.");
            AssertWorld(source);

            string renamedRoot = Path.Combine(game, "versions", "auto-renamed"); Prepare(source, renamedRoot, expectedId);
            checkpoint = phase => { if (phase == MmcLifecycleCheckpoint.CommitDecided) throw new MmcLifecycleInterruptionException("owned decided rename interruption"); };
            Native.Static("PCL.MmcLifecycleUi", "StartRename", owner, "auto-renamed", null); Wait(2, ModBase.LoadState.Finished);
            var renamed = MmcInstanceInspector.Read(renamedRoot);
            Native.Check(recoveries == 2 && results.Count == 1 && renamed.IsConsistent && renamed.Definition!.InstanceId == expectedId &&
                renamed.Definition.Generation == initial.Definition.Generation + 1 && !Directory.Exists(source),
                "A committed real rename still required manual recovery or lost identity/generation.");
            AssertWorld(renamedRoot);

            var renamedOwner = Owner(renamedRoot); string movedRoot = Path.Combine(otherGame, "versions", "auto-renamed"); Prepare(renamedRoot, movedRoot, expectedId);
            checkpoint = phase => { if (phase == MmcLifecycleCheckpoint.DirectoryMoved) throw new MmcLifecycleInterruptionException("owned move finalization interruption"); };
            Native.Static("PCL.MmcLifecycleUi", "StartMove", renamedOwner, otherGame, null); Wait(3, ModBase.LoadState.Finished);
            var moved = MmcInstanceInspector.Read(movedRoot);
            Native.Check(recoveries == 3 && results.Count == 2 && moved.IsConsistent && moved.Definition!.InstanceId == expectedId &&
                moved.Definition.Generation == initial.Definition.Generation + 2 && !Directory.Exists(renamedRoot) &&
                !File.Exists(Path.Combine(movedRoot, "PCL", "MmcLifecyclePending.json")),
                "A moved real instance retained manual finalization or changed its monotonic identity.");
            AssertWorld(movedRoot);

            // A conflicting owner marker must never be guessed, removed, or overwritten.
            var movedOwner = Owner(movedRoot); string conflictTarget = Path.Combine(otherGame, "versions", "must-preserve-conflict");
            Prepare(movedRoot, conflictTarget, expectedId); byte[]? markerBytes = null;
            string marker = Path.Combine(movedRoot, "PCL", "MmcLifecyclePending.json");
            checkpoint = phase => { if (phase == MmcLifecycleCheckpoint.MarkerWritten) { markerBytes = File.ReadAllBytes(marker); throw new MmcLifecycleInterruptionException("owned conflicting-marker interruption"); } };
            recovering = _ => File.WriteAllText(marker, "{foreign operation owner}");
            Native.Static("PCL.MmcLifecycleUi", "StartRename", movedOwner, "must-preserve-conflict", null); Wait(4, ModBase.LoadState.Failed);
            Native.Check(recoveries == 4 && results.Count == 2 && markerBytes is not null && File.ReadAllText(marker) == "{foreign operation owner}" &&
                !Directory.Exists(conflictTarget), "Automatic recovery changed a foreign marker or guessed its ownership.");
            AssertWorld(movedRoot);
            // Restore only the exact injected fixture marker so ordinary fixture teardown has
            // no live operation; this calls the preserved raw primitive directly, never a UI.
            File.WriteAllBytes(marker, markerBytes!);
            var cleanup = new MmcInstanceLifecycleBackend().RecoverAsync(operation).GetAwaiter().GetResult();
            Native.Check(cleanup.Status == MmcInstanceBackendStatus.Cancelled && MmcInstanceInspector.Read(movedRoot).IsConsistent,
                "Owned conflict fixture did not cleanly return to its original state.");
        }

        internal void Finish() {
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            Native.Check(!Busy, "Automatic relocation retained its actual operation fence."); finished = true;
        }
        public void Dispose() {
            if (!finished) Finish();
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            while (restore.Count > 0) restore.Pop()();
        }
    }
}
