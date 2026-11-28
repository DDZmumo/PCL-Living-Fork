using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Actual PageInstanceExport BAML, original checkboxes and production MMC task/backend.
// These two groups retain the existing 60-second Native child boundary.
internal static class MmcExportPageChecks {
    internal static void Run(FixtureEnvironment environment) => RunPart(environment, admission: false);
    internal static void RunAdmission(FixtureEnvironment environment) => RunPart(environment, admission: true);

    private static void RunPart(FixtureEnvironment environment, bool admission) {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) {
            try {
                if (admission) scope.Admission();
                else scope.PageAndTask();
            } catch (Exception error) {
                Console.Error.WriteLine("FAIL MMC export page assertion before fixture teardown: " + error);
                throw;
            }
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine(admission
            ? "PASS mmc-export-page-admission: stale owner, asynchronous file callback, replaced identity and damaged MMC cannot enter ordinary export."
            : "PASS mmc-export-page: original BAML/controls, real file list, private defaults, portable settings validation, picker/preview cancellation and actual no-overwrite MMC publication.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcExportUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceExportResult> publications = new();
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly ModMinecraft.McInstance? oldOwner = LauncherInstanceContext.Instance;
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState> scheduler;
        private readonly Action previousRefresh;
        private readonly List<PageInstanceExport> pages = [];
        private readonly int previousAnimation = ModAnimation.AniControlEnabled;
        private bool accept = true;
        private string output = "";
        private string? defaultOutputName;
        private int pickerCalls;
        private string? replacementRoot, replacementFingerprint;

        internal Scope(FileTaskScope files) {
            this.files = files;
            ModAnimation.AniControlEnabled++;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            previousRefresh = scheduler.RefreshExtraDownload;
            scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); }));
            Set("SelectOptions", new Func<MmcInstanceExportOptions>(() => throw new IOException("The shared export page opened the obsolete options popup.")));
            Set("SelectOutput", new Func<string, string>(name => { pickerCalls++; defaultOutputName = name; return output; }));
            Set("ConfirmPreview", new Func<MmcInstanceExportPreview, bool>(_ => accept));
            Set("Published", new Action<MmcInstanceExportResult>(result => publications.Enqueue(result)));
            Set("ErrorObserved", new Action<Exception, string>((error, _) => errors.Enqueue(error)));
        }

        private void Set(string name, object? value) {
            var property = Native.Property(ui, name);
            previous.TryAdd(property, property.GetValue(null));
            property.SetValue(null, value);
        }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private static T Part<T>(FrameworkElement page, string name) where T : class => (T)(page.FindName(name) ?? throw new IOException("Missing actual BAML control: " + name));
        private static void Drain(DateTime deadline) => Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        private static void Until(Func<bool> ready, string phase) {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!ready()) {
                Drain(deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("MMC export page did not finish " + phase);
                Thread.Sleep(3);
            }
            Drain(deadline);
        }
        private void WaitForTask(int previousCount) => Until(() => tasks.Count > previousCount && !Busy && Current.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting), "its real task");
        private static Task Inventory(PageInstanceExport page) => (Task)Native.Property(typeof(PageInstanceExport), "MmcInventoryTask").GetValue(page)!;
        private static void WaitInventory(PageInstanceExport page) => Until(() => Inventory(page).IsCompleted, "its real file inventory");
        private static void Start(PageInstanceExport page) => Native.Call(typeof(PageInstanceExport), "StartExport", page);
        private static MmcInstanceExportOptions Read(PageInstanceExport page) => (MmcInstanceExportOptions)Native.Call(typeof(PageInstanceExport), "ReadMmcExportOptions", page)!;

        private ModMinecraft.McInstance Import(string leaf) {
            string root = Path.Combine(files.Root, leaf), game = Path.Combine(root, "minecraft");
            Directory.CreateDirectory(game);
            files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            return new ModMinecraft.McInstance(source).Load();
        }
        private PageInstanceExport Open(ModMinecraft.McInstance owner) {
            LauncherInstanceContext.Instance = owner;
            Guid id = owner.Config.ContextStamp.InstanceId;
            var page = (PageInstanceExport)Activator.CreateInstance(typeof(PageInstanceExport), BindingFlags.Instance | BindingFlags.NonPublic, null, [owner, id], null)!;
            pages.Add(page);
            return page;
        }
        private static void Put(string root, string relative, string text) {
            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, new UTF8Encoding(false));
        }
        private static IEnumerable<string> AllText(DependencyObject owner) {
            if (owner is TextBlock text) yield return text.Text;
            for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(owner); index++)
                foreach (string child in AllText(System.Windows.Media.VisualTreeHelper.GetChild(owner, index))) yield return child;
            if (owner is Panel panel)
                foreach (UIElement child in panel.Children)
                    if (child is TextBlock item) yield return item.Text;
        }

        internal void PageAndTask() {
            var owner = Import("export-page");
            Put(owner.PathVersion, "minecraft/saves/world/level.dat", "private-world");
            Put(owner.PathVersion, "minecraft/screenshots/private.png", "private-image");
            Put(owner.PathVersion, "minecraft/servers.dat", "private-server");
            var page = Open(owner);
            WaitInventory(page);
            Native.Check(Part<Button>(page, "BtnExport").IsEnabled && Part<StackPanel>(page, "PanMmcPortability").Visibility == Visibility.Visible &&
                Part<StackPanel>(page, "PanAdvancedStandard").Visibility == Visibility.Collapsed,
                "Managed mode did not use the original page's bottom action and targeted options.");
            Native.Check(Part<StackPanel>(page, "PanOptions").Children.Contains(Part<CheckBox>(page, "CheckOptionsSaves")) &&
                Part<CheckBox>(page, "CheckOptionsBasic").IsChecked == true && !Part<CheckBox>(page, "CheckOptionsBasic").IsEnabled &&
                Part<CheckBox>(page, "CheckOptionsMod").IsChecked == true && !Part<CheckBox>(page, "CheckOptionsMod").IsEnabled,
                "Original selectors were replaced or required environment became optional.");
            Native.Check(Native.Field(typeof(PageInstanceExport), "PageController").GetValue(page) is null &&
                Native.Field(typeof(PageInstanceExport), "Pipeline").GetValue(page) is null,
                "Managed export initialized the ordinary version reconstruction backend.");
            Native.Check(Read(page) == new MmcInstanceExportOptions(), "Original export page opted into private data or a portable conversion without consent.");
            var required = (StackPanel)Native.Field(typeof(PageInstanceExport), "_mmcRequiredFiles").GetValue(page)!;
            Native.Check(AllText(required).Any(text => text.Contains("minecraft/config/owned.cfg", StringComparison.Ordinal)) &&
                AllText(Part<Panel>(page, "PanOptionsSaves")).Any(text => text.Contains("world/level.dat", StringComparison.Ordinal)),
                "Page did not display the real source file paths.");

            Part<CheckBox>(page, "CheckMmcFixedMemory").IsChecked = true;
            Part<TextBox>(page, "TextMmcMinimumMemory").Text = "2048";
            Part<TextBox>(page, "TextMmcMaximumMemory").Text = "1024";
            Start(page);
            Native.Check(errors.Count == 1 && pickerCalls == 0 && tasks.IsEmpty, "Invalid portable memory reached output selection or export.");
            Part<CheckBox>(page, "CheckMmcFixedMemory").IsChecked = false;
            Part<TextBox>(page, "TextExportName").Text = "Shared managed";
            Part<TextBox>(page, "TextExportVersion").Text = "2.4";
            Start(page);
            Native.Check(tasks.IsEmpty && defaultOutputName == "Shared managed-2.4", "Cancelling the picker created a task or version did not identify only the default ZIP filename.");

            output = Path.Combine(files.Root, "managed-defaults.zip");
            accept = false;
            int before = tasks.Count;
            Start(page); WaitForTask(before);
            Native.Check(Current.State == ModBase.LoadState.Canceled && !File.Exists(output), "Original page's preview cancellation wrote a ZIP.");
            accept = true;
            before = tasks.Count;
            Start(page); WaitForTask(before);
            Native.Check(Current.State == ModBase.LoadState.Finished && publications.Count == 1 && File.Exists(output), "Original page failed to publish through the MMC backend: " + Current.Error);
            using (var zip = ZipFile.OpenRead(output)) {
                Native.Check(zip.GetEntry("mmc-pack.json") is not null && zip.GetEntry("minecraft/config/owned.cfg") is not null &&
                    !zip.Entries.Any(entry => entry.FullName.Contains("/saves/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("/screenshots/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("servers.dat", StringComparison.OrdinalIgnoreCase)),
                    "Original page did not preserve MMC structure and private defaults.");
                using var reader = new StreamReader(zip.GetEntry("instance.cfg")!.Open());
                Native.Check(reader.ReadToEnd().Contains("Shared managed", StringComparison.Ordinal), "The original name field did not control exported instance metadata.");
            }
            byte[] kept = File.ReadAllBytes(output);
            before = tasks.Count;
            Start(page); WaitForTask(before);
            Native.Check(Current.State == ModBase.LoadState.Failed && kept.SequenceEqual(File.ReadAllBytes(output)) && publications.Count == 1,
                "Original page overwrote an existing ZIP.");

            Put(owner.PathVersion, "minecraft/personal-extra.txt", "explicit unknown file");
            page.RefreshAll(); WaitInventory(page);
            Part<CheckBox>(page, "CheckOptionsSaves").IsChecked = true;
            Part<CheckBox>(page, "CheckOptionsScreenshots").IsChecked = true;
            Part<CheckBox>(page, "CheckOptionsServers").IsChecked = true;
            Part<CheckBox>(page, "CheckMmcOtherGameFiles").IsChecked = true;
            output = Path.Combine(files.Root, "managed-selected.zip");
            before = tasks.Count;
            Start(page); WaitForTask(before);
            Native.Check(Current.State == ModBase.LoadState.Finished, "Explicit category choices did not reach the genuine export task: " + Current.Error);
            using (var zip = ZipFile.OpenRead(output))
                Native.Check(zip.GetEntry("minecraft/saves/world/level.dat") is not null && zip.GetEntry("minecraft/screenshots/private.png") is not null &&
                    zip.GetEntry("minecraft/servers.dat") is not null && zip.GetEntry("minecraft/personal-extra.txt") is not null,
                    "The original page's selected file categories were ignored.");
            Native.Check(File.ReadAllText(Path.Combine(owner.PathVersion, "minecraft/saves/world/level.dat")) == "private-world", "Export changed the original player data.");
        }

        internal void Admission() {
            var first = Import("export-admission-first");
            var second = Import("export-admission-second");
            var page = Open(second);
            WaitInventory(page);
            Guid originalId = second.Config.ContextStamp.InstanceId;
            Native.Check((bool)Native.Call(typeof(PageInstanceExport), "IsManagedOwner", page, second, originalId)!, "Navigation could not recognize the captured page owner.");

            page.RefreshAll();
            LauncherInstanceContext.Instance = first;
            WaitInventory(page);
            int before = pickerCalls;
            Start(page);
            Native.Check(!Part<Button>(page, "BtnExport").IsEnabled && pickerCalls == before && tasks.IsEmpty,
                "A stale file-list callback or action adopted the next owner.");
            LauncherInstanceContext.Instance = second;
            page.RefreshAll(); WaitInventory(page);

            string secondRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.PathVersion));
            string projection = Path.Combine(secondRoot, "managed.json");
            byte[] projectionBytes = File.ReadAllBytes(projection);
            File.AppendAllText(projection, " ");
            page.RefreshAll(); Start(page);
            Native.Check(!Part<Button>(page, "BtnExport").IsEnabled && tasks.IsEmpty &&
                Native.Field(typeof(PageInstanceExport), "PageController").GetValue(page) is null, "Damaged MMC entered ordinary export.");
            File.WriteAllBytes(projection, projectionBytes);
            page.RefreshAll(); WaitInventory(page);

            string retained = secondRoot + "-retained";
            Directory.Move(secondRoot, retained);
            CopyTree(first.PathVersion, secondRoot);
            Native.Check(MmcInstanceInspector.Read(secondRoot).IsConsistent, "Replacement fixture must be a different valid MMC identity.");
            replacementRoot = secondRoot;
            replacementFingerprint = TreeFingerprint(secondRoot);
            page.RefreshAll(); Start(page);
            Native.Check(!Part<Button>(page, "BtnExport").IsEnabled && pickerCalls == before && tasks.IsEmpty,
                "Refreshing the existing export page adopted the replacement identity.");
            var reopened = Open(second); WaitInventory(reopened); Start(reopened);
            Native.Check(!Part<Button>(reopened, "BtnExport").IsEnabled && tasks.IsEmpty,
                "A newly opened page on an old attached owner adopted its replacement identity.");
            // Keep the two exact fixture trees until FileTaskScope drains every page/IO
            // producer and cleans its own root. Re-renaming a freshly copied tree in a
            // finally block is unrelated to the admission checks and can hide their
            // original exception under a Windows sharing/cleanup failure. The stale
            // pages must keep rejecting the replacement throughout their teardown.

            string ordinaryRoot = Path.Combine(files.Root, "ordinary-game", "versions", "ordinary");
            Directory.CreateDirectory(ordinaryRoot);
            File.WriteAllText(Path.Combine(ordinaryRoot, "ordinary.json"), """{"id":"ordinary","mainClass":"fixture.Main","libraries":[],"releaseTime":"2024-01-01T00:00:00Z"}""");
            var ordinary = new ModMinecraft.McInstance(ordinaryRoot);
            FileTaskScope.CacheInstance(ordinary);
            LauncherInstanceContext.Instance = ordinary;
            var ordinaryPage = new PageInstanceExport();
            pages.Add(ordinaryPage);
            Native.Check(Part<StackPanel>(ordinaryPage, "PanAdvancedStandard").Visibility == Visibility.Visible &&
                Part<StackPanel>(ordinaryPage, "PanMmcPortability").Visibility == Visibility.Collapsed &&
                Native.Field(typeof(PageInstanceExport), "Pipeline").GetValue(ordinaryPage) is not null &&
                Part<CheckBox>(ordinaryPage, "CheckOptionsJava").Tag is ExportOption,
                "Ordinary versions lost their existing BAML controls or export pipeline.");
        }

        private void CopyTree(string source, string target) {
            string bound = Path.GetFullPath(files.Root) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(source).StartsWith(bound, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(target).StartsWith(bound, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replacement tree must remain inside this FileTaskScope.");
            Directory.CreateDirectory(target);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }

        private static string TreeFingerprint(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));

        public void Dispose() {
            foreach (var page in pages) { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); WaitInventory(page); }
            Native.Check(!Busy, "The real MMC export task remained active at fixture teardown.");
            if (replacementRoot is not null) {
                object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, replacementRoot)!;
                lock (Native.Get(state, "Sync")!) {
                    Native.Check(((System.Collections.IDictionary)Native.Get(state, "Patches")!).Count == 0 &&
                        ((System.Collections.IDictionary)Native.Get(state, "IniPatches")!).Count == 0 && (int)Native.Get(state, "Inflight")! == 0,
                        "Stale-owner visual checks queued settings writes into a replacement identity.");
                }
                InstanceConfigProviders.FlushAndDisposeWithin(replacementRoot);
                Native.Check(TreeFingerprint(replacementRoot) == replacementFingerprint,
                    "Provider teardown changed the replacement instance's files.");
            }
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = previousRefresh;
            ModAnimation.AniControlEnabled = previousAnimation;
            LauncherInstanceContext.Instance = oldOwner;
        }
    }
}
