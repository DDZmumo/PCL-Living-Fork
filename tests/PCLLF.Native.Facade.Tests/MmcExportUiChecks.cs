using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcExportUiChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        using var scope = new Scope(files);
        scope.Run();
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-export-ui: actual Main options/BAML entry and genuine Loader preview/export; private defaults, unstarted/preview/writer cancellation, stale preview refusal and completed publication preserved.");
    }
    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcExportUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly ConcurrentQueue<ModLoader.LoaderCombo<string>> tasks = new();
        private readonly ConcurrentQueue<MmcInstanceExportResult> results = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action previousRefresh;
        private Func<MmcInstanceExportPreview, bool> confirm = _ => true;
        private Action<ModLoader.LoaderCombo<string>>? registered;
        private Action<MmcInstanceExportCheckpoint>? checkpoint;
        private Action? published;
        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            previousRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("RegisterTask", new Action<ModLoader.LoaderCombo<string>>(task => { tasks.Enqueue(task); ModLoader.LoaderTaskbar.Add(task); registered?.Invoke(task); }));
            Set("ConfirmPreview", new Func<MmcInstanceExportPreview, bool>(preview => confirm(preview)));
            Set("Published", new Action<MmcInstanceExportResult>(result => { results.Enqueue(result); published?.Invoke(); }));
            Set("CheckpointObserved", new Action<MmcInstanceExportCheckpoint, int>((phase, _) => checkpoint?.Invoke(phase)));
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private void Wait(int previousCount = -1) {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (tasks.Count <= previousCount || Busy || Current.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual export worker did not drain.");
                Thread.Sleep(5);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        }
        internal void Run() {
            var viewType = Native.Type("PCL.MmcExportOptionsView");
            object view = Activator.CreateInstance(viewType, BindingFlags.Instance | BindingFlags.NonPublic, null, [], null)!;
            var defaults = (MmcInstanceExportOptions)Native.Call(viewType, "ReadOptions", view)!;
            Native.Check(defaults == new MmcInstanceExportOptions(), "Export view silently opted into personal files, machine conversion or inactive-tree omission.");
            ((MyCheckBox)Native.Field(viewType, "FixedMemory").GetValue(view)!).Checked = true;
            ((System.Windows.Controls.TextBox)Native.Field(viewType, "Minimum").GetValue(view)!).Text = "2048";
            ((System.Windows.Controls.TextBox)Native.Field(viewType, "Maximum").GetValue(view)!).Text = "1024";
            try { Native.Call(viewType, "ReadOptions", view); throw new Exception("Invalid portable memory choice was accepted."); }
            catch (Exception error) when (error is IOException || error.InnerException is IOException) { }

            string root = Path.Combine(files.Root, "export-ui"), game = Path.Combine(root, "minecraft"); Directory.CreateDirectory(game); files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            var instance = new ModMinecraft.McInstance(source).Load(); var inspection = MmcInstanceInspector.Read(source);
            string world = Path.Combine(source, "minecraft", "saves", "world", "level.dat"); Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "private-world", new UTF8Encoding(false));
            string screenshot = Path.Combine(source, "minecraft", "screenshots", "private.png"); Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!); File.WriteAllText(screenshot, "private-shot");
            var request = new MmcInstanceExportRequest(source, Path.Combine(root, "default.zip"), "managed", inspection.Definition!.InstanceId,
                inspection.Definition.Generation, inspection.PrimarySha256!, defaults);

            // Overview no longer duplicates MMC export; real sidebar routes Tag 5 to the original PageInstanceExport in MMC mode.
            var oldPage = PageInstanceLeft.Instance;
            try {
                PageInstanceLeft.Instance = instance;
                var overview = new PageInstanceOverall(); Native.Check(overview.FindName("BtnMmcExport") is null, "Duplicate BtnMmcExport remained in Overview.");
                var left = new PageInstanceLeft();
                Native.Check(((FrameworkElement)left.FindName("ItemExport")).Visibility == Visibility.Visible, "Managed sidebar export entry stayed hidden.");
                var mmcTool = (PageInstanceExport)left.PageGet(FormMain.PageSubType.InstanceExport);
                Native.Check((bool)Native.Call(typeof(PageInstanceExport), "IsManagedOwner", mmcTool, instance, inspection.Definition.InstanceId)!,
                    "Original export page did not bind its MMC owner.");
                PageInstanceLeft.Instance = null; Native.Call(left.GetType(), "SyncInstanceContext", left);
                Native.Check(left.PageGet(FormMain.PageSubType.InstanceExport) is PageInstanceExport, "Ordinary instance did not retain PageInstanceExport.");
            } finally { PageInstanceLeft.Instance = oldPage; }

            registered = task => task.Cancel(); Native.Static("PCL.MmcExportUi", "Start", request); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && !File.Exists(request.OutputZipPath) && !Busy &&
                Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Unstarted export retained busy or created a writer.");
            Current.Loaders.Single().Start(source); Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Late Start escaped the cancelled export fence.");
            registered = null;

            confirm = _ => false; Native.Static("PCL.MmcExportUi", "Start", request); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && !File.Exists(request.OutputZipPath), "Preview cancellation did not remain a cancelled task.");
            confirm = _ => { File.WriteAllText(Path.Combine(source, "minecraft", "config", "owned.cfg"), "changed while confirmation was open"); return true; };
            Native.Static("PCL.MmcExportUi", "Start", request); Wait();
            Native.Check(Current.State == ModBase.LoadState.Failed && !File.Exists(request.OutputZipPath) && results.IsEmpty, "A changed source passed the UI's fixed preview fingerprint.");
            confirm = _ => true;
            checkpoint = phase => { if (phase == MmcInstanceExportCheckpoint.EntryWritten) Current.Cancel(); };
            Native.Static("PCL.MmcExportUi", "Start", request); Wait();
            Native.Check(Current.State == ModBase.LoadState.Canceled && !File.Exists(request.OutputZipPath) && results.IsEmpty, "Writer cancellation published a ZIP or returned before the producer drained.");
            foreach (string partial in Directory.EnumerateFiles(root, "*.partial")) using (new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            checkpoint = null; published = () => Current.Cancel();
            Set("SelectOptions", new Func<MmcInstanceExportOptions>(() => defaults));
            Set("SelectOutput", new Func<string, string>(_ => request.OutputZipPath));
            int previousCount = tasks.Count;
            var previousPage = PageInstanceLeft.Instance;
            try {
                PageInstanceLeft.Instance = instance;
                var left = new PageInstanceLeft();
                Native.Static("PCL.MmcExportUi", "Prompt", instance);
            } finally { PageInstanceLeft.Instance = previousPage; }
            Wait(previousCount);
            Native.Check(Current.State == ModBase.LoadState.Finished && results.Single().Status == MmcInstanceExportStatus.Completed && File.Exists(request.OutputZipPath), "Late cancellation erased a completed publication outcome.");
            using (var zip = ZipFile.OpenRead(request.OutputZipPath)) {
                Native.Check(zip.GetEntry("mmc-pack.json") is not null && zip.GetEntry("minecraft/config/owned.cfg") is not null &&
                    !zip.Entries.Any(entry => entry.FullName.Contains("/saves/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("/screenshots/", StringComparison.OrdinalIgnoreCase)), "Actual UI export ignored its private-data defaults.");
            }
            Native.Check(File.ReadAllText(world) == "private-world" && File.ReadAllText(screenshot) == "private-shot", "Export changed the player's original data.");
        }
        public void Dispose() {
            if (Busy) throw new IOException("Actual MMC export worker is still active.");
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = previousRefresh;
        }
    }
}
