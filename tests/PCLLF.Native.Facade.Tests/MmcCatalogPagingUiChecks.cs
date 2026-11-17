using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Actual Main view/buttons/modal selection port and production lifecycle backend.
internal static class MmcCatalogPagingUiChecks {
    internal static void Run(FixtureEnvironment environment) => Execute(environment, false);
    internal static void RunStaleConfirmation(FixtureEnvironment environment) => Execute(environment, true);
    private static void Execute(FixtureEnvironment environment, bool stale) {
        using FileTaskScope files = new(environment); using var scope = new Scope(files);
        scope.Check(stale); InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine(stale
            ? "PASS mmc-catalog-paging-stale: actual Main explicit selection rejects intent replacement during confirmation, keeps the quarantine/world and publishes no result."
            : "PASS mmc-catalog-paging-ui: actual LF controls/modal port reach the 257th owned record, reset selection on forward/back/refresh, return to the selected page after declining, cancel/drain real reads and restore the exact instance; no JVM.");
    }
    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcDeleteUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceLifecycleResult> results = new();
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action oldRefresh;
        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("SelectEntry", null);
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(value => { tasks.Enqueue(value); ModLoader.LoaderTaskbar.Add(value); }));
            Set("Published", new Action<MmcInstanceLifecycleResult>(value => results.Enqueue(value)));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Enqueue(error)));
        }
        internal void Check(bool stale) {
            string root = Path.Combine(files.Root, stale ? "catalog-stale" : "catalog-paging"), game = Path.Combine(root, "minecraft");
            Directory.CreateDirectory(game); files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            string world = Path.Combine(source, "minecraft", "saves", "owned-world", "level.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "owned lifecycle world");
            var imported = MmcInstanceInspector.Read(source);
            var deleted = new MmcInstanceLifecycleBackend().DeleteAsync(new(source, imported.Definition!.InstanceId,
                imported.Definition.Generation, imported.PrimarySha256!)).GetAwaiter().GetResult();
            Native.Check(deleted.Status == MmcInstanceBackendStatus.Succeeded, "Native paging fixture could not quarantine its instance.");
            string store = Path.Combine(game, "PCL", "MmcLifecycle"), operation = Path.GetFileName(deleted.OperationDirectory!);
            Native.Check(StringComparer.Ordinal.Compare(256.ToString("x32"), operation) < 0, "Real fixture GUID did not follow sequential diagnostic GUIDs.");
            for (int i = 256; i > 0; i--) {
                string bad = Path.Combine(store, i.ToString("x32")); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad, "intent.record"), "broken record");
            }
            int shown = 0, confirmations = 0;
            Set("ShowCatalog", new Func<FrameworkElement, int>(view => {
                shown++; AssertUnlocked(source, deleted.CurrentRoot!, imported.Definition.InstanceId);
                Native.Check(Property(view, "Selected") is null && !(bool)Property(view, "CanConfirm")!, "A new/reopened page inherited a selection.");
                object port = Native.Static("PCL.MessageDialogPlatform", "VersionSelection", view) ?? throw new IOException("Missing actual catalog modal selection port.");
                var hasSelection = (Func<bool>)Native.Get(port, "HasSelection")!;
                Native.Check(!hasSelection(), "The real modal accepted a page before explicit selection.");
                if (shown == 1) {
                    string[] first = Leaves(view); var old = Items(view)[0]; old.Checked = true;
                    Native.Check(hasSelection(), "Explicit selection did not enable the actual modal button.");
                    Click(view, 1); Native.Check(Property(view, "Selected") is null && !hasSelection(), "Next page retained the old row selection while reading."); AwaitRead(view);
                    old.Checked = false; old.Checked = true;
                    Native.Check(Property(view, "Selected") is null && !hasSelection(), "A removed prior-page control rebound an old index to the new page.");
                    Click(view, 0); AwaitRead(view);
                    Native.Check(Leaves(view).SequenceEqual(first) && !hasSelection(), "Previous did not return the same unselected range.");
                    Items(view)[0].Checked = true; Click(view, 2); AwaitRead(view);
                    Native.Check(Leaves(view).SequenceEqual(first) && Property(view, "Selected") is null && !hasSelection(), "Refresh retained stale selection or changed a stable range.");
                    while (Page(view).Next is not null) { Click(view, 1); AwaitRead(view); }
                } else Native.Check(shown == 2 && Page(view).Entries.Single().OperationDirectory == deleted.OperationDirectory, "Returning from confirmation lost the originating last page.");
                var chosen = Items(view).Single(item => ((MmcLifecycleCatalogEntry)item.Tag).CanRestore);
                chosen.Checked = true;
                Native.Check(hasSelection() && ((MmcLifecycleCatalogEntry)Property(view, "Selected")!).InstanceId == imported.Definition.InstanceId,
                    "The 257th real quarantine was not explicitly selected.");
                return 1;
            }));
            Set("Confirm", new Func<string, string, bool, int>((_, _, _) => {
                confirmations++; AssertUnlocked(source, deleted.CurrentRoot!, imported.Definition.InstanceId);
                if (stale) { File.AppendAllText(Path.Combine(deleted.OperationDirectory!, "intent.record"), "changed after selection"); return 1; }
                return confirmations == 1 ? 2 : 1;
            }));
            Native.Static("PCL.MmcDeleteUi", "PromptCatalog", game);
            Until(() => !errors.IsEmpty || !tasks.IsEmpty && !(bool)Native.Property(ui, "HasActiveOperation").GetValue(null)! && tasks.Last().State != ModBase.LoadState.Loading,
                "The actual paged catalog action did not finish.");
            Native.Check(errors.IsEmpty, "The actual catalog UI failed: " + string.Join("; ", errors));
            var task = tasks.Single();
            if (stale) Native.Check(task.State == ModBase.LoadState.Failed && results.IsEmpty && Directory.Exists(deleted.CurrentRoot) && !Directory.Exists(source),
                "A stale confirmation restored or published the changed record.");
            else Native.Check(shown == 2 && confirmations == 2 && task.State == ModBase.LoadState.Finished && Directory.Exists(source) &&
                results.Single().Definition!.InstanceId == imported.Definition.InstanceId, "Returning/reselecting restored another row or failed to restore.");
            string worldRoot = stale ? deleted.CurrentRoot! : source;
            Native.Check(File.ReadAllText(Path.Combine(worldRoot, "minecraft", "saves", "owned-world", "level.dat")) == "owned lifecycle world", "Catalog navigation changed the fixture world.");
            if (!stale) CheckCancelledRead(game);
        }
        private static void CheckCancelledRead(string game) {
            var request = new MmcLifecycleCatalogPageRequest(game);
            var page = Task.Run(() => MmcInstanceLifecycleCatalog.ReadPage(request)).GetAwaiter().GetResult();
            object view = Activator.CreateInstance(Native.Type("PCL.MmcLifecycleCatalogView"), Native.Flags, null, [request, page], null)!;
            Click(view, 1);
            var close = (Task)Native.Call(view.GetType(), "CloseAndDrainAsync", view)!;
            Until(() => close.IsCompleted, "Closing the list did not drain its genuine background enumeration/read."); close.GetAwaiter().GetResult();
            Native.Check(Property(view, "Selected") is null && !(bool)Property(view, "CanConfirm")!, "Cancelled page reading retained a selection.");
        }
        private static void AssertUnlocked(string source, string target, Guid id) {
            using var first = MmcAdmissionGate.Acquire(source, id, TimeSpan.Zero);
            using var second = MmcAdmissionGate.Acquire(target, id, TimeSpan.Zero);
            foreach (string root in new[] { source, target }) {
                object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, root)!;
                Native.Check((int)Native.Get(state, "PauseCount")! == 0, "A catalog selection/confirmation retained configuration quiescence.");
            }
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        public void Dispose() {
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            scheduler.RefreshExtraDownload = oldRefresh;
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
        }
    }
    private static object? Property(object value, string name) => Native.Property(value.GetType(), name).GetValue(value);
    private static MmcLifecycleCatalogPage Page(object view) => (MmcLifecycleCatalogPage)Property(view, "CurrentPage")!;
    private static List<MyListItem> Items(object view) => ((IList)Native.Get(view, "Selections")!).Cast<MyListItem>().ToList();
    private static string[] Leaves(object view) => Page(view).Entries.Select(entry => Path.GetFileName(entry.OperationDirectory)).ToArray();
    private static void Click(object view, int index) {
        var filters = (Panel)Native.Field(view.GetType().BaseType!, "Filters").GetValue(view)!;
        var button = (MyButton)filters.Children[index]; Native.Check(button.IsEnabled, "A required paging button is disabled.");
        Native.Call(typeof(MyButton), "OnClick", button, button, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
    }
    private static void AwaitRead(object view) => Until(() => !(bool)Property(view, "IsReading")!, "The real catalog page read did not finish.");
    private static void Until(Func<bool> complete, string error) {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!complete()) {
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(error);
            Thread.Sleep(2);
        }
        Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
    }
}
