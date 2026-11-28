using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Windows;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcMoveUiChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var scope = new Scope(files);
        scope.Run(); InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-move-ui: actual Main BAML prompt and registered-folder selection retain pre-dialog identity; stale preview/early cancel denied, decided move preserves identity/world/config and producer drain.");
    }
    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcLifecycleUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceLifecycleResult> results = new();
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState> scheduler;
        private readonly Action previousRefresh;
        private readonly List<ModMinecraft.McFolder> previousFolders;
        private Action<ModLoader.LoaderCombo<string>>? register;
        private Action<MmcLifecycleCheckpoint>? checkpoint;
        internal Scope(FileTaskScope files) {
            this.files = files; previousFolders = ModMinecraft.McFolderList;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            previousRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); register?.Invoke(task); }));
            Set("Published", new Action<MmcInstanceLifecycleResult>(value => results.Enqueue(value)));
            Set("CheckpointObserved", new Action<MmcLifecycleCheckpoint>(phase => checkpoint?.Invoke(phase)));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Enqueue(error)));
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private void Wait(int previousCount = -1) {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (tasks.Count <= previousCount || Busy || Current.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (!errors.IsEmpty) throw new AggregateException(errors);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual MMC relocation producer did not drain.");
                Thread.Sleep(5);
            }
        }
        internal void Run() {
            string root = Path.Combine(files.Root, "move-ui"), game = Path.Combine(root, "minecraft"), targetGame = Path.Combine(root, "other-minecraft");
            Directory.CreateDirectory(game); Directory.CreateDirectory(targetGame); files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            var instance = new ModMinecraft.McInstance(source).Load(); var before = MmcInstanceInspector.Read(source);
            string target = Path.Combine(targetGame, "versions", "managed");
            string world = Path.Combine(source, "minecraft", "saves", "current", "level.dat"); Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "player world", new UTF8Encoding(false));
            using var provider = new InstanceConfigProvider(source);
            MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "" }); provider.Save();
            register = task => task.Cancel(); Native.Static("PCL.MmcLifecycleUi", "StartMove", instance, targetGame, null); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && !Directory.Exists(target) && Directory.Exists(source) && !Busy &&
                Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Unstarted move created its target or retained busy.");
            Current.Loaders.Single().Start(source); Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Late Start escaped move cancellation."); register = null;
            ModMinecraft.McFolderList = [new() { Name = "source", Location = game }, new() { Name = "destination", Location = targetGame }];
            Set("SelectMoveFolder", new Func<string[], string>(choices => {
                Native.Check(choices.Length == 1 && Path.GetFullPath(choices[0]) == Path.GetFullPath(targetGame), "Move selection included the current folder or lost registered folder identity."); return choices[0];
            }));
            string primary = Path.Combine(source, "PCL", "InstanceDefinition.json"); byte[] original = File.ReadAllBytes(primary);
            Set("ConfirmMove", new Func<string, string, bool>((from, to) => {
                Native.Check(from == before.Paths!.InstanceRoot && Path.GetFullPath(to) == Path.GetFullPath(target), "Move confirmation changed source/target identity.");
                File.AppendAllText(primary, " "); return true;
            }));
            int count = tasks.Count; Native.Static("PCL.MmcLifecycleUi", "PromptMove", instance, null); Wait(count);
            Native.Check(Current.State == ModBase.LoadState.Failed && Directory.Exists(source) && !Directory.Exists(target) && results.IsEmpty, "A changed source passed the fixed move confirmation.");
            File.WriteAllBytes(primary, original);
            Set("ConfirmMove", new Func<string, string, bool>((_, _) => true));
            checkpoint = phase => { if (phase == MmcLifecycleCheckpoint.CommitDecided) Current.Cancel(); };
            var previousPage = LauncherInstanceContext.Instance; count = tasks.Count;
            try {
                LauncherInstanceContext.Instance = instance;
                Native.Check(new PageInstanceOverall().FindName("BtnMmcMove") is null, "Duplicate BtnMmcMove remained in Overview.");
                var left = FileTaskShell.Install().GlassInstanceNavigation;
                var toolPage = (PageInstanceMmcTool)left.GetPage(FormMain.PageSubType.InstanceMmcMove);
                Native.Call(toolPage.GetType(), "BtnPrimaryAction_Click", toolPage, toolPage.FindName("BtnPrimaryAction"), null);
            } finally { LauncherInstanceContext.Instance = previousPage; }
            Wait(count);
            var result = results.Single(); var moved = MmcInstanceInspector.Read(target);
            Native.Check(Current.State == ModBase.LoadState.Finished && result.Status == MmcInstanceBackendStatus.Succeeded && moved.IsConsistent &&
                moved.Definition!.InstanceId == before.Definition!.InstanceId && moved.Definition.Generation == before.Definition.Generation + 1 && !Directory.Exists(source),
                "Actual decided move lost its terminal outcome or monotonic identity.");
            Native.Check(File.ReadAllText(Path.Combine(target, "minecraft", "saves", "current", "level.dat")) == "player world" &&
                File.ReadAllText(Path.Combine(target, "minecraft", "config", "owned.cfg")) == "author data", "Move changed player/author bytes.");
            using var movedProvider = new InstanceConfigProvider(target);
            Native.Check(MmcIniConfiguration.CapturePreferences(movedProvider).Ini.GetValueOrDefault("MmcJvmArgsMode") == "Custom" &&
                MmcIniConfiguration.CapturePreferences(movedProvider).Ini.GetValueOrDefault("VersionAdvanceJvm", "not empty") == "", "Move lost the explicit empty JVM preference.");
        }
        public void Dispose() {
            if (Busy) throw new IOException("Actual MMC relocation producer remains active.");
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            ModMinecraft.McFolderList = previousFolders; scheduler.RefreshExtraDownload = previousRefresh;
        }
    }
}
