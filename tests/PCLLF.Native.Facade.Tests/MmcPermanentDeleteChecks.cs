using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Owned inert fixture directories only. Real Main task, native handles and local TxF;
// no game, account, network or recursive-delete substitute.
internal static class MmcPermanentDeleteChecks {
    internal static void Run(FixtureEnvironment environment) => RunPart(environment, "ui");
    internal static void RunSafety(FixtureEnvironment environment) => RunPart(environment, "safety");
    internal static void RunRollback(FixtureEnvironment environment) => RunPart(environment, "rollback");

    private static void RunPart(FixtureEnvironment environment, string part) {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) {
            if (part == "ui") scope.Ui(); else if (part == "safety") scope.Safety(); else scope.Rollback();
            scope.Finish();
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-permanent-delete-" + part + ": actual managed deletion task, fixed approved identity/tree and verified NTFS transaction; no quarantine or ordinary delete fallback.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcDeleteUi");
        private readonly Stack<Action> restore = new();
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceLifecycleResult> results = new();
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly string root, game, source, sibling, shared;
        private readonly ModMinecraft.McInstance owner;
        private readonly MmcInstanceInspection initial;
        private Func<string, int> confirmation = _ => 2;
        private Action<MmcInstancePurgeCheckpoint, string?>? checkpoint;
        private Action<ModLoader.LoaderCombo<string>>? registered;
        private int confirmationCount;
        private bool finished;

        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            Action refresh = scheduler.RefreshExtraDownload; restore.Push(() => scheduler.RefreshExtraDownload = refresh); scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); registered?.Invoke(task); }));
            Set("Published", new Action<MmcInstanceLifecycleResult>(result => results.Enqueue(result)));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Enqueue(error)));
            Set("PermanentDeleteCheckpointObserved", new Action<MmcInstancePurgeCheckpoint, string>((phase, path) => checkpoint?.Invoke(phase, path)));
            Set("Confirm", new Func<string, string, bool, int>((text, title, warning) => {
                Native.Check(warning && title.Contains("永久删除", StringComparison.Ordinal) && text.Contains("不可恢复", StringComparison.Ordinal) &&
                    text.Contains("存档", StringComparison.Ordinal) && text.Contains("模组", StringComparison.Ordinal) && text.Contains("配置", StringComparison.Ordinal) &&
                    text.Contains("不会进入回收站", StringComparison.Ordinal) && text.Contains(source!, StringComparison.Ordinal),
                    "Permanent deletion omitted its concrete Chinese irreversible data-loss warning.");
                using (var lease = MmcAdmissionGate.Acquire(source!, initial!.Definition!.InstanceId, TimeSpan.Zero)) {
                    object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, source)!;
                    Native.Check((int)Native.Get(state, "PauseCount")! == 0, "Confirmation retained configuration quiescence.");
                }
                Interlocked.Increment(ref confirmationCount); return confirmation(text);
            }));
            root = Path.Combine(files.Root, "permanent-delete"); game = Path.Combine(root, "minecraft"); Directory.CreateDirectory(game); files.SelectMinecraft(game);
            source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            owner = new ModMinecraft.McInstance(source).Load(); FileTaskScope.CacheInstance(owner); initial = MmcInstanceInspector.Read(source);
            Put(Path.Combine(source, "minecraft", "saves", "current-world", "level.dat"), "owned current player world");
            Put(Path.Combine(source, "minecraft", "mods", "player.jar"), "owned inert player mod");
            Directory.CreateDirectory(Path.Combine(source, "minecraft", "empty-player-directory"));
            sibling = Path.Combine(game, "versions", "ordinary-keep", "keep.txt"); Put(sibling, "other instance must survive");
            shared = Path.Combine(game, "libraries", "shared-keep.jar"); Put(shared, "shared library must survive");
        }

        private void Set(string name, object value) {
            var property = Native.Property(ui, name); object? previous = property.GetValue(null);
            restore.Push(() => property.SetValue(null, previous)); property.SetValue(null, value);
        }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private void Prompt() => Native.Static("PCL.MmcDeleteUi", "PromptDelete", owner, false, null);
        private void Until(Func<bool> done, string phase) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done()) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Permanent deletion phase " + phase + "; " + string.Join("; ", errors.Select(error => error.Message)));
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        }
        private void Wait(int expectedCount, ModBase.LoadState state) {
            Until(() => tasks.Count == expectedCount && !Busy && Current.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                Native.Get(Current.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }, "actual producer completion");
            Native.Check(Current.State == state, "Actual permanent-deletion task returned " + Current.State + ": " + Current.Error);
        }
        private void RejectedBeforeTask(Action trigger, string phase) {
            int count = tasks.Count, previousErrors = errors.Count; string before = Tree(source); trigger();
            Until(() => errors.Count > previousErrors, phase);
            Native.Check(tasks.Count == count && Tree(source) == before && !Busy, "A refused deletion registered a task or changed its source.");
            Preserved();
        }
        private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        private static void CopyFixtureTree(string from, string to) {
            Native.Check(!Directory.Exists(to) && !File.Exists(to), "Fixture copy destination already exists.");
            Directory.CreateDirectory(to);
            foreach (string entry in Directory.EnumerateFileSystemEntries(from)) {
                var attributes = File.GetAttributes(entry);
                Native.Check((attributes & FileAttributes.ReparsePoint) == 0, "An owned copy fixture unexpectedly contains a link.");
                string target = Path.Combine(to, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) CopyFixtureTree(entry, target);
                else File.Copy(entry, target, false);
            }
        }
        private static string Tree(string path) => string.Join("|", Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(entry => Path.GetRelativePath(path, entry).Replace('\\', '/') + (Directory.Exists(entry) ? "/" : ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))))));
        private void Preserved() {
            Native.Check(File.ReadAllText(sibling) == "other instance must survive" && File.ReadAllText(shared) == "shared library must survive" &&
                !Directory.EnumerateDirectories(game, ".pcllf-mmc-quarantine-*", SearchOption.AllDirectories).Any(),
                "Direct deletion changed another instance/shared bytes or created a quarantine directory.");
        }
        private void Detached() {
            Exception? refused = null; try { owner.Config.AssertAttached(); } catch (IOException error) { refused = error; }
            object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, source)!;
            Native.Check(refused is not null && (bool)Native.Get(state, "Detached")! && !Directory.Exists(source),
                "Committed deletion left an old configuration provider able to recreate its source.");
        }

        internal void Ui() {
            string before = Tree(source); confirmation = _ => 1; Prompt(); Until(() => confirmationCount == 1, "default cancellation");
            Native.Check(tasks.IsEmpty && results.IsEmpty && errors.IsEmpty && Tree(source) == before, "Default cancellation wrote or queued a deletion.");
            confirmation = _ => 2; Prompt(); Wait(1, ModBase.LoadState.Finished);
            var result = results.Single();
            Native.Check(result.Status == MmcInstanceBackendStatus.Succeeded && result.Purged && result.PurgeDecided && !result.Quarantined &&
                result.OperationDirectory is null && result.CurrentRoot is null && result.Definition!.InstanceId == initial.Definition!.InstanceId,
                "Real managed deletion was not terminal/direct or published a quarantine operation.");
            Detached(); Preserved();
        }

        internal void Safety() {
            // The same approved primary with changed player bytes must require a new preview.
            string config = Path.Combine(source, "minecraft", "config", "owned.cfg");
            confirmation = _ => { File.WriteAllText(config, "changed after confirmation preview"); return 2; };
            Prompt(); Wait(1, ModBase.LoadState.Failed);
            Native.Check(results.IsEmpty && File.ReadAllText(config) == "changed after confirmation preview" && Directory.Exists(source),
                "An approved stale tree deleted changed player configuration.");
            confirmation = _ => 2;
            using (var held = MmcAdmissionGate.Acquire(source!, initial!.Definition!.InstanceId, TimeSpan.Zero))
                RejectedBeforeTask(Prompt, "busy admission");
            string pending = Path.Combine(source, "PCL", "MmcImportPending.json"); Put(pending, "{unknown pending import}");
            RejectedBeforeTask(Prompt, "pending operation"); File.Move(pending, Path.Combine(root, "retained-import-pending"));
            string sessions = Path.Combine(source, "PCL", "MmcSessions");
            Put(Path.Combine(sessions, Guid.NewGuid().ToString("N"), "Launching.record"), "{unknown session}");
            RejectedBeforeTask(Prompt, "unknown session"); Directory.Move(sessions, Path.Combine(root, "retained-unknown-session"));
            using (var idle = new BackupResourceChecks.IdleProcess()) {
                Guid id = Guid.NewGuid(); string directory = Path.Combine(sessions, id.ToString("N")); Directory.CreateDirectory(directory);
                var identity = MmcSessions.ObserveExecutable(idle.Value.MainModule!.FileName!);
                var receipt = new MmcSessionReceipt(1, id, initial.Definition.InstanceId, source, initial.Definition.Generation,
                    new string('a', 64), new string('b', 32), identity, MmcSessions.ObserveExecutable(Environment.ProcessPath!));
                string launching = Record(Path.Combine(directory, "Launching.record"), receipt);
                _ = Record(Path.Combine(directory, "Running.record"), new MmcSessionProcessRegistration(1, id, launching,
                    idle.Value.Id, idle.Value.StartTime.ToUniversalTime().Ticks, identity));
                RejectedBeforeTask(Prompt, "active session"); idle.Finish();
            }
            Directory.Move(sessions, Path.Combine(root, "retained-active-session"));
            string target = Path.Combine(root, "outside-keep"); Directory.CreateDirectory(target); Put(Path.Combine(target, "keep.txt"), "outside owned sentinel");
            string junction = Path.Combine(source, "minecraft", "mods", "linked-directory");
            EnvironmentHistoryChecks.WithJunction(junction, target, () => RejectedBeforeTask(Prompt, "linked descendant"));
            Native.Check(File.ReadAllText(Path.Combine(target, "keep.txt")) == "outside owned sentinel", "A link refusal touched its outside target.");

            string secondRoot = Path.Combine(root, "replacement-input"), secondGame = Path.Combine(secondRoot, "minecraft"); Directory.CreateDirectory(secondGame);
            string replacement = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, secondRoot, secondGame)!;
            string heldRoot = source + "-original-retained", replacedRoot = source + "-replacement-retained"; bool swapped = false, sourceMoved = false;
            int previousErrors = errors.Count, taskCount = tasks.Count; string replacementBytes = "";
            confirmation = _ => {
                Directory.Move(source, heldRoot); sourceMoved = true; CopyFixtureTree(replacement, source);
                replacementBytes = Tree(source); swapped = true; return 2;
            };
            try {
                Prompt(); Until(() => errors.Count > previousErrors, "replaced identity confirmation");
                string refusal = string.Join("; ", errors.Skip(previousErrors).Select(error => error.ToString()));
                Native.Check(swapped, "The replacement confirmation never completed its fixture swap; no deletion authority was granted. Refusal: " + refusal);
                Native.Check(tasks.Count == taskCount, "A stale replacement confirmation registered a deletion task. Refusal: " + refusal);
                var current = MmcInstanceInspector.Read(source);
                Native.Check(current.IsConsistent && current.Definition!.InstanceId != initial.Definition.InstanceId,
                    "The replacement fixture lost its distinct consistent identity. Refusal: " + refusal);
                string currentBytes = Tree(source);
                Native.Check(currentBytes == replacementBytes, "A refused replacement confirmation changed these fixture paths: " +
                    string.Join(", ", currentBytes.Split('|').Except(replacementBytes.Split('|')).Concat(replacementBytes.Split('|').Except(currentBytes.Split('|'))).Select(entry => entry.Split(':')[0]).Take(8)) +
                    ". Refusal: " + refusal);
            } finally {
                if (sourceMoved) {
                    if (Directory.Exists(source)) Directory.Move(source, replacedRoot);
                    Directory.Move(heldRoot, source);
                }
            }
            Preserved();
        }

        internal void Rollback() {
            string before = Tree(source);
            registered = task => task.Cancel(); Prompt(); Wait(1, ModBase.LoadState.Canceled); registered = null;
            Native.Check(Tree(source) == before && results.IsEmpty, "Unstarted permanent deletion changed its source.");
            bool staged = false;
            checkpoint = (phase, _) => { if (!staged && phase == MmcInstancePurgeCheckpoint.EntryStaged) { staged = true; throw new MmcLifecycleInterruptionException("owned staged-delete rollback injection"); } };
            Prompt(); Wait(2, ModBase.LoadState.Failed);
            Native.Check(staged && Tree(source) == before && results.IsEmpty, "A failure after staging a real delete did not roll back the entire tree.");
            checkpoint = (phase, _) => { if (phase == MmcInstancePurgeCheckpoint.BeforeDecision) Current.Cancel(); };
            Prompt(); Wait(3, ModBase.LoadState.Canceled);
            Native.Check(Tree(source) == before && results.IsEmpty, "Cancellation at the final decision did not roll back staged player bytes.");
            checkpoint = (phase, _) => {
                if (phase == MmcInstancePurgeCheckpoint.CommitDecided) {
                    Current.Cancel(); throw new TimeoutException("owned post-commit timeout notification");
                }
                if (phase == MmcInstancePurgeCheckpoint.Completed) throw new ApplicationException("owned completion notification failure");
            };
            Prompt(); Wait(4, ModBase.LoadState.Finished);
            Native.Check(results.Single() is { Purged: true, PurgeDecided: true, Quarantined: false, Failure: "owned post-commit timeout notification" } && !Directory.Exists(source),
                "Post-commit cancellation/callback failure lost the terminal deletion outcome.");
            Detached(); Preserved();
        }

        private static string Record<T>(string path, T value) => (string)typeof(MmcSessions).Assembly.GetType("PCLCS.MmcDurableFiles")!
            .GetMethod("WriteRecord", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T)).Invoke(null, [path, value])!;
        internal void Finish() {
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            Native.Check(!Busy && tasks.All(task => task.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting)), "Permanent deletion retained actual work.");
            finished = true;
        }
        public void Dispose() {
            if (!finished) Finish();
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            while (restore.Count > 0) restore.Pop()();
        }
    }
}
