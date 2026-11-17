using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Real Main BAML, original PCLCS page controller, and genuine MMC LoaderCombo tasks.
// Each entry runs in its existing 60-second native child, with FileTaskScope-owned data.
internal static class MmcHistoryPageChecks {
    internal static void Run(FixtureEnvironment environment) => RunPart(environment, false);
    internal static void RunAdmission(FixtureEnvironment environment) => RunPart(environment, true);

    private static void RunPart(FixtureEnvironment environment, bool admission) {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) {
            if (admission) scope.Admission(); else scope.Flow();
            scope.Finish();
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine(admission
            ? "PASS mmc-history-page-admission: real page enforces fixed owner/root/initial identity, rejects stale confirmation and directory replacement, and keeps damaged/pending/unknown states read-only."
            : "PASS mmc-history-page: original BAML/controller list/search/selection, real create/details/restore/undo tasks, current worlds preserved, ordinary page unchanged.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcHistoryUi");
        private readonly Stack<Action> restore = new();
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceHistoryCaptureResult> captures = new();
        private readonly ConcurrentQueue<MmcInstanceHistoryRestoreResult> restores = new();
        private readonly List<PageInstanceRestorePoints> pages = [];
        private readonly List<string> errors = [];
        private readonly List<(string Text, string Title)> details = [];
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly string root, game, source, legacyHistory;
        private readonly ModMinecraft.McInstance owner;
        private readonly MmcInstanceInspection initial;
        private Func<MmcInstanceHistoryPoint, bool, bool> confirm = (_, _) => true;
        private readonly List<bool> confirmations = [];
        private bool finished;

        internal Scope(FileTaskScope files) {
            this.files = files;
            var oldOwner = PageInstanceLeft.Instance;
            restore.Push(() => PageInstanceLeft.Instance = oldOwner);
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            Action previousRefresh = scheduler.RefreshExtraDownload;
            restore.Push(() => scheduler.RefreshExtraDownload = previousRefresh);
            scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); }));
            Set("Captured", new Action<MmcInstanceHistoryCaptureResult>(result => captures.Enqueue(result)));
            Set("Restored", new Action<MmcInstanceHistoryRestoreResult>(result => restores.Enqueue(result)));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Add(error.Message)));
            Set("CaptureLabel", new Func<string>(() => "UI baseline record"));
            Set("ConfirmRestore", new Func<MmcInstanceHistoryPoint, bool, bool>((point, undo) => { confirmations.Add(undo); return confirm(point, undo); }));
            Set("ShowDetails", new Action<string, string>((text, title) => details.Add((text, title))));
            root = Path.Combine(files.Root, "history-page"); game = Path.Combine(root, "minecraft");
            Directory.CreateDirectory(game); files.SelectMinecraft(game);
            source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            owner = new ModMinecraft.McInstance(source).Load(); FileTaskScope.CacheInstance(owner);
            PageInstanceLeft.Instance = owner; initial = MmcInstanceInspector.Read(source);
            legacyHistory = Path.Combine(root, "must-not-use-ordinary-history");
            var legacyRoot = Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot");
            object? previousLegacyRoot = legacyRoot.GetValue(null);
            restore.Push(() => legacyRoot.SetValue(null, previousLegacyRoot));
            legacyRoot.SetValue(null, legacyHistory);
        }

        private void Set(string name, object? value) {
            PropertyInfo property = Native.Property(ui, name); object? previous = property.GetValue(null);
            restore.Push(() => property.SetValue(null, previous)); property.SetValue(null, value);
        }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private static T Part<T>(FrameworkElement page, string name) where T : class => (T)page.FindName(name);
        private static void Click(MyButton button) => Native.Call(typeof(MyButton), "OnClick", button, button, null);
        private static void Click(MyIconTextButton button) => Native.Call(typeof(MyIconTextButton), "OnClick", button);
        private static void Click(MyIconButton button) => Native.Call(typeof(MyIconButton), "OnClick", button, button, EventArgs.Empty);
        private static List<JObject> Entries(PageInstanceRestorePoints page) => (List<JObject>)Native.Property(page.GetType(), "Entries").GetValue(page)!;
        private static object Loader(PageInstanceRestorePoints page) => Native.Get(page, "PointsLoader")!;
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
        private static ModBase.LoadState ReadState(object loader) => (ModBase.LoadState)Native.Property(loader.GetType(), "State").GetValue(loader)!;
        private static bool Ready(PageInstanceRestorePoints page) => (bool)Native.Get(page, "MmcReadReady")!;
        private void Until(Func<bool> done, string phase) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done()) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("MMC history page phase: " + phase + "; errors=" + string.Join("; ", errors));
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        }
        private PageInstanceRestorePoints Open(Guid? expected = null) {
            var page = (PageInstanceRestorePoints)Activator.CreateInstance(typeof(PageInstanceRestorePoints), Native.Flags,
                null, new object?[] { owner, expected }, null)!;
            pages.Add(page);
            Native.Call(typeof(PageInstanceRestorePoints), "PageLoaded", page);
            Read(page, expected != Guid.Empty);
            return page;
        }
        private void Read(PageInstanceRestorePoints page, bool ready = true) {
            Until(() => ReadState(Loader(page)) is ModBase.LoadState.Finished or ModBase.LoadState.Failed, "read real page loader");
            Native.Check(ReadState(Loader(page)) == ModBase.LoadState.Finished,
                "The genuine page loader failed: " + Native.Property(Loader(page).GetType(), "Error").GetValue(Loader(page)));
            if (ready) {
                Native.Check(Ready(page) && Entries(page).All(entry => entry["mmcPoint"]?.ToObject<MmcInstanceHistoryPoint>()?.InstanceId == initial.Definition!.InstanceId),
                    "The page did not accept its own readable snapshot or rendered another instance's history.");
            } else {
                // No read authority is a successful empty, read-only page model. A finished
                // loader alone never grants read or mutation capability.
                Native.Check(!Ready(page) && Entries(page).Count == 0 && Part<StackPanel>(page, "PanEntries").Children.Count == 0 &&
                    !Part<MyButton>(page, "BtnCreate").IsEnabled && !Part<MyIconTextButton>(page, "BtnRestore").IsEnabled &&
                    !Part<MyButton>(page, "BtnMmcUndo").IsEnabled && !Part<MyButton>(page, "BtnFolder").IsEnabled,
                    "An unverified/old owner read external history or retained a mutating/folder action.");
            }
        }
        private void WaitOperation(PageInstanceRestorePoints page, int count, bool success = true) {
            Until(() => tasks.Count >= count && !Busy && tasks.Last().State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting), "actual MMC task completion");
            Native.Check(tasks.Last().State == (success ? ModBase.LoadState.Finished : ModBase.LoadState.Failed), "The actual MMC history task returned an unexpected outcome: " + tasks.Last().Error);
            Read(page, success);
        }

        internal void Flow() {
            var page = Open();
            object controller = Native.Get(page, "Controller")!;
            var rows = Part<StackPanel>(page, "PanEntries");
            Native.Check(controller.GetType().Assembly.GetName().Name == "PCLCS" && ReferenceEquals(Native.Get(controller, "Entries"), Entries(page)) &&
                rows.Children.Count == 0 && Part<MyButton>(page, "BtnCreate").Text == "创建环境记录" &&
                Part<FrameworkElement>(page, "CardRetention").Visibility == Visibility.Collapsed &&
                Part<FrameworkElement>(page, "FilterHealthy").Visibility == Visibility.Collapsed &&
                Part<FrameworkElement>(page, "BtnDelete").Visibility == Visibility.Collapsed &&
                Part<MyHint>(page, "HintMmcHistory").Text.Contains("当前存档", StringComparison.Ordinal),
                "Managed history did not reuse actual original page/controller controls with accurate environment semantics.");
            string world = Path.Combine(source, "minecraft", "saves", "current", "level.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "current world v1", new UTF8Encoding(false));
            Click(Part<MyButton>(page, "BtnCreate")); WaitOperation(page, 1);
            var point = captures.Single().Point!;
            Native.Check(point.FullSafeEnvironment && rows.Children.Count == 1 && Entries(page).Count == 1 &&
                ((MyLocalModItem)rows.Children[0]).Buttons.Count() == 3 &&
                ReferenceEquals(Native.Get(rows.Children[0], "RestorePoint"), Entries(page)[0]),
                "Creating an MMC record did not publish and render its genuine complete record in the original list.");
            var row = (MyLocalModItem)rows.Children[0];
            Click(row.Buttons.ElementAt(1));
            Native.Check(details.Count == 1 && details[0].Title == "MMC 环境记录详情" && details[0].Text.Contains("当前存档", StringComparison.Ordinal) &&
                details[0].Text.Contains("会保留", StringComparison.Ordinal) && !details[0].Text.Contains("正常运行记录", StringComparison.Ordinal),
                "MMC details used ordinary-world/launch-health semantics.");
            row.Checked = true;
            Native.Check(Part<MyIconTextButton>(page, "BtnRestore").IsEnabled && Part<TextBlock>(page, "LabSelect").Text.Contains("环境记录", StringComparison.Ordinal),
                "Original real selection footer did not expose the managed restore command.");
            Part<MySearchBox>(page, "SearchBox").Text = "missing-point-query";
            Native.Check(rows.Children.Count == 0 && Part<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Collapsed,
                "Search retained invisible MMC restore targets.");
            Part<MySearchBox>(page, "SearchBox").Text = "";
            ((MyLocalModItem)rows.Children[0]).Checked = true;
            string config = Path.Combine(source, "minecraft", "config", "owned.cfg");
            File.WriteAllText(config, "current environment B"); File.WriteAllText(world, "current world v2");
            Click(Part<MyIconTextButton>(page, "BtnRestore")); WaitOperation(page, 2);
            var restored = MmcInstanceInspector.Read(source);
            Native.Check(restores.Count == 1 && restored.Definition!.InstanceId == initial.Definition!.InstanceId &&
                restored.Definition.Generation == initial.Definition.Generation + 1 && File.ReadAllText(config) == "author data" &&
                File.ReadAllText(world) == "current world v2" && Part<MyButton>(page, "BtnMmcUndo").IsEnabled,
                "Actual page restore lost monotonic identity, environment bytes, preserved current worlds, or its undo command.");
            Click(Part<MyButton>(page, "BtnMmcUndo")); WaitOperation(page, 3);
            Native.Check(restores.Count == 2 && MmcInstanceInspector.Read(source).Definition!.Generation == initial.Definition!.Generation + 2 &&
                File.ReadAllText(config) == "current environment B" && File.ReadAllText(world) == "current world v2" && confirmations.SequenceEqual(new[] { false, true }),
                "Actual undo did not restore environment B while retaining the current world and explicit confirmation.");
            Native.Check(errors.Count == 0, "The successful page workflow reported an error.");
            CheckOrdinary();
        }

        private void CheckOrdinary() {
            var ordinary = new PageInstanceRestorePoints(); pages.Add(ordinary);
            var points = new List<JObject> { new() { ["id"] = "ordinary", ["label"] = "ordinary point", ["created"] = DateTime.UtcNow,
                ["description"] = "ordinary environment", ["healthy"] = true, ["files"] = new JObject() } };
            Native.Property(typeof(PageInstanceRestorePoints), "Entries").SetValue(ordinary, points);
            Native.Call(typeof(PageInstanceRestorePoints), "RenderList", ordinary);
            Native.Check(Part<MyButton>(ordinary, "BtnCreate").Text == "创建还原点" && Part<FrameworkElement>(ordinary, "CardRetention").Visibility == Visibility.Visible &&
                Part<FrameworkElement>(ordinary, "FilterHealthy").Visibility == Visibility.Visible && Part<FrameworkElement>(ordinary, "BtnDelete").Visibility == Visibility.Visible &&
                Part<FrameworkElement>(ordinary, "BtnMmcUndo").Visibility == Visibility.Collapsed && Part<FrameworkElement>(ordinary, "HintMmcHistory").Visibility == Visibility.Collapsed &&
                ((MyLocalModItem)Part<StackPanel>(ordinary, "PanEntries").Children[0]).Buttons.Count() == 4 &&
                ReferenceEquals(Native.Get(Native.Get(ordinary, "Controller")!, "Entries"), points),
                "Managed adapters changed ordinary restore-point controls, actions, or shared-controller state.");
        }

        internal void Admission() {
            var page = Open(); Click(Part<MyButton>(page, "BtnCreate")); WaitOperation(page, 1);
            string projection = Path.Combine(source, "managed.json"); byte[] projectionBytes = File.ReadAllBytes(projection);
            File.AppendAllText(projection, " "); page.Refresh(); Read(page);
            Native.Check(Entries(page).Count == 1 && !Part<MyButton>(page, "BtnCreate").IsEnabled &&
                !((MyLocalModItem)Part<StackPanel>(page, "PanEntries").Children[0]).Buttons.First().IsEnabled &&
                Part<MyHint>(page, "HintMmcHistory").Text.Contains("修复", StringComparison.Ordinal),
                "Damaged management lost readable history or retained a mutation capability.");
            Click(Part<MyButton>(page, "BtnCreate")); Native.Check(tasks.Count == 1, "Damaged page queued a task.");
            File.WriteAllBytes(projection, projectionBytes); page.Refresh(); Read(page);
            string journal = Path.Combine(source, "PCL", "MmcTransactions", "unrecognized-history-page-journal"); Directory.CreateDirectory(journal);
            // This unrecognized journal has no verified instance envelope. The page must not
            // call the history reader at all; a corrupt index makes an accidental read fail.
            string unreadableIndex = Path.Combine(source, "PCL", "MmcHistory", "index", Guid.NewGuid().ToString("N") + ".record");
            File.WriteAllText(unreadableIndex, "{must-not-read-without-owner-authority}");
            page.Refresh(); Read(page, false);
            Native.Check(!Part<MyButton>(page, "BtnCreate").IsEnabled && Part<MyHint>(page, "HintMmcHistory").Text.Contains("事务", StringComparison.Ordinal),
                "Pending transaction did not lock the original history page.");
            File.Move(unreadableIndex, Path.Combine(root, "retained-unreadable-history-index.record"));
            Directory.Move(journal, Path.Combine(root, "retained-unrecognized-history-page-journal")); page.Refresh(); Read(page);
            string primary = Path.Combine(source, "PCL", "InstanceDefinition.json"); byte[] primaryBytes = File.ReadAllBytes(primary);
            File.WriteAllText(primary, """{"schemaVersion":99,"managementKind":"Mmc","formatProfile":"prism-v1"}""", new UTF8Encoding(false));
            page.Refresh(); Read(page, false);
            Native.Check(!Part<MyButton>(page, "BtnCreate").IsEnabled && Part<MyHint>(page, "HintMmcHistory").Text.Contains("格式", StringComparison.Ordinal),
                "An unknown management format retained a mutation or silently fell back to ordinary history.");
            File.WriteAllBytes(primary, primaryBytes); page.Refresh(); Read(page);
            var unknown = Open(Guid.Empty);
            Native.Check(!Part<MyButton>(unknown, "BtnCreate").IsEnabled && Entries(unknown).Count == 0,
                "An initially unknown identity adopted the directory's current valid instance.");
            unknown.Refresh(); Read(unknown, false);
            Native.Call(typeof(PageInstanceRestorePoints), "PageUnloaded", unknown);

            string replacementRoot = Path.Combine(root, "replacement-source"), replacementGame = Path.Combine(replacementRoot, "minecraft");
            Directory.CreateDirectory(replacementGame);
            string replacement = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, replacementRoot, replacementGame)!;
            var replacementInspection = MmcInstanceInspector.Read(replacement);
            var foreignPoint = new MmcInstanceHistoryBackend().CaptureAsync(new(replacement, replacementInspection.Definition!.InstanceId,
                replacementInspection.Definition.Generation, replacementInspection.PrimarySha256!, "FOREIGN HISTORY MUST NOT BE READ") { FullSafeEnvironment = true }).GetAwaiter().GetResult();
            Native.Check(foreignPoint.Status == MmcInstanceBackendStatus.Succeeded && foreignPoint.Point!.InstanceId != initial.Definition!.InstanceId,
                "Replacement fixture did not contain distinguishable real foreign history.");
            string held = source + "-retained", replaced = source + "-replacement-retained";
            bool swapped = false, sourceMoved = false;
            confirm = (_, _) => {
                Directory.Move(source, held);
                sourceMoved = true;
                CopyFixtureTree(replacement, source);
                swapped = true; return true;
            };
            try {
                ((MyLocalModItem)Part<StackPanel>(page, "PanEntries").Children[0]).Checked = true;
                Click(Part<MyIconTextButton>(page, "BtnRestore")); WaitOperation(page, 2, false);
                Native.Check(swapped && restores.IsEmpty && MmcInstanceInspector.Read(source).Definition!.InstanceId != initial.Definition!.InstanceId,
                    "A stale confirmation changed or adopted the replacement instance.");
                int taskCount = tasks.Count; page.Refresh(); Read(page, false);
                Click(Part<MyButton>(page, "BtnCreate"));
                Native.Check(tasks.Count == taskCount && !Part<MyButton>(page, "BtnCreate").IsEnabled && Entries(page).Count == 0 &&
                    Part<MyHint>(page, "HintMmcHistory").Text.Contains("替换", StringComparison.Ordinal),
                    "Refresh or an old control click adopted a different identity at the same directory.");
                PageInstanceLeft.Instance = new ModMinecraft.McInstance(replacement).Load(); page.Refresh();
                Click(Part<MyButton>(page, "BtnCreate"));
                Native.Check(tasks.Count == taskCount, "An old page followed the newly selected owner.");
            } finally {
                confirm = (_, _) => true;
                if (sourceMoved) {
                    if (Directory.Exists(source)) Directory.Move(source, replaced);
                    Directory.Move(held, source);
                }
                PageInstanceLeft.Instance = owner;
            }
        }

        internal void Finish() {
            foreach (var page in pages) Native.Call(typeof(PageInstanceRestorePoints), "PageUnloaded", page);
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            Native.Check(!Busy && !Directory.Exists(legacyHistory), "Managed page retained a producer or invoked the ordinary history writer.");
            finished = true;
        }
        public void Dispose() {
            if (!finished) Finish();
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            while (restore.Count > 0) restore.Pop()();
        }
    }
}
