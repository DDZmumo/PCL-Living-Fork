using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcHistoryUiChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var scope = new Scope(files);
        scope.Run(); InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-history-ui: genuine Main capture/restore/undo tasks, new monotonic generations, preserved current worlds, stale fixed identity and pre/post-decision cancellation with producer drain.");
    }
    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcHistoryUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceHistoryCaptureResult> captures = new();
        private readonly ConcurrentQueue<MmcInstanceHistoryRestoreResult> restores = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action previousRefresh;
        private Action<ModLoader.LoaderCombo<string>>? register;
        private Action<MmcTransactionCheckpoint>? checkpoint;
        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            previousRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); register?.Invoke(task); }));
            Set("Captured", new Action<MmcInstanceHistoryCaptureResult>(result => captures.Enqueue(result)));
            Set("Restored", new Action<MmcInstanceHistoryRestoreResult>(result => restores.Enqueue(result)));
            Set("CheckpointObserved", new Action<MmcTransactionCheckpoint, int>((phase, _) => checkpoint?.Invoke(phase)));
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private void Wait() {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Busy || Current.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual MMC history producer did not drain.");
                Thread.Sleep(5);
            }
        }
        internal void Run() {
            string root = Path.Combine(files.Root, "history-ui"), game = Path.Combine(root, "minecraft"); Directory.CreateDirectory(game); files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            var before = MmcInstanceInspector.Read(source);
            string configuration = Path.Combine(source, "minecraft", "config", "owned.cfg");
            string world = Path.Combine(source, "minecraft", "saves", "current", "level.dat"); Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "player world v1", new UTF8Encoding(false));
            var capture = new MmcInstanceHistoryCaptureRequest(source, before.Definition!.InstanceId, before.Definition.Generation, before.PrimarySha256!, "before change");
            register = task => task.Cancel(); Native.Static("PCL.MmcHistoryUi", "StartCapture", capture); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && captures.IsEmpty && !Busy && Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Unstarted history task wrote or retained its fence.");
            Current.Loaders.Single().Start(source); Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Late Start escaped history cancellation."); register = null;

            Set("CaptureCheckpointObserved", new Action<MmcInstanceHistoryCheckpoint>(phase => { if (phase == MmcInstanceHistoryCheckpoint.PointPublished) Current.Cancel(); }));
            Native.Static("PCL.MmcHistoryUi", "StartCapture", capture); Wait();
            Native.Check(Current.State == ModBase.LoadState.Finished && captures.Count == 1, "A published history record was reported cancelled.");
            Set("CaptureCheckpointObserved", null);
            var point = captures.Single().Point!;
            File.WriteAllText(configuration, "current personal config B"); File.WriteAllText(world, "player world v2");
            var restore = new MmcInstanceHistoryRestoreRequest(source, before.Definition.InstanceId, before.Definition.Generation, before.PrimarySha256!, point.PointId, point.ManifestSha256);
            checkpoint = phase => { if (phase == MmcTransactionCheckpoint.BeforeDecision) Current.Cancel(); };
            Native.Static("PCL.MmcHistoryUi", "StartRestore", restore); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && File.ReadAllText(configuration) == "current personal config B" && restores.IsEmpty,
                "Before-decision history cancellation replaced the current environment.");
            checkpoint = phase => { if (phase == MmcTransactionCheckpoint.CommitDecided) Current.Cancel(); };
            Native.Static("PCL.MmcHistoryUi", "StartRestore", restore); Wait();
            Native.Check(Current.State == ModBase.LoadState.Finished && restores.Count == 1, "Decided restore was interrupted or lost its outcome.");
            var restored = MmcInstanceInspector.Read(source); var result = restores.Single();
            Native.Check(restored.Definition!.InstanceId == before.Definition.InstanceId && restored.Definition.Generation == before.Definition.Generation + 1 &&
                File.ReadAllText(configuration) == "author data" && File.ReadAllText(world) == "player world v2" && result.UndoPoint is not null, "Restore lost identity/generation, current worlds or complete undo.");
            checkpoint = null;
            Native.Static("PCL.MmcHistoryUi", "StartRestore", restore); Wait();
            Native.Check(Current.State == ModBase.LoadState.Failed && restores.Count == 1 && MmcInstanceInspector.Read(source).PrimarySha256 == restored.PrimarySha256,
                "A stale dialog request was rebound to the new generation.");
            var undo = result.UndoPoint!;
            Native.Static("PCL.MmcHistoryUi", "StartRestore", new MmcInstanceHistoryRestoreRequest(source, restored.Definition.InstanceId, restored.Definition.Generation,
                restored.PrimarySha256!, undo.PointId, undo.ManifestSha256)); Wait();
            Native.Check(Current.State == ModBase.LoadState.Finished && restores.Count == 2 && MmcInstanceInspector.Read(source).Definition!.Generation == before.Definition.Generation + 2 &&
                File.ReadAllText(configuration) == "current personal config B" && File.ReadAllText(world) == "player world v2", "Actual undo failed to recover environment B while retaining current world bytes.");
        }
        public void Dispose() {
            if (Busy) throw new IOException("Actual MMC history producer remains active.");
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = previousRefresh;
        }
    }
}
