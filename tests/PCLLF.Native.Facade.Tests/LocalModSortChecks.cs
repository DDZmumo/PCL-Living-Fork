using System.Collections;
using System.Windows;
using System.Windows.Controls;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main page, filter controls and mod rows with an isolated loader snapshot.
internal static class LocalModSortChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Root, "local-mod-sort");
        Directory.CreateDirectory(root);
        // HasUpdate reads a genuine instance-scoped setting even when no
        // update exists. Supply its owner while keeping loader ports isolated.
        ModMinecraft.McInstance? previous = LauncherInstanceContext.Instance;
        string instanceRoot = Path.Combine(root, "instance"); Directory.CreateDirectory(instanceRoot);
        ModMinecraft.McInstance owner = new(instanceRoot);
        Native.Check(owner.Config.ContextStamp.Kind == MmcConfigurationContextKind.Unmanaged, "The ordinary mod-sort owner did not attach to its existing fixture directory.");
        IDictionary cache = (IDictionary)Native.Get(Native.Entries["VersionAdvanceDisableModUpdate"]!, "ValueCache")!;
        bool existed = cache.Contains(owner.PathVersion);
        object? oldValue = cache[owner.PathVersion];
        try
        {
            cache[owner.PathVersion] = false;
            LauncherInstanceContext.Instance = owner;
            RunPage(environment, root);
        }
        finally
        {
            LauncherInstanceContext.Instance = previous;
            if (existed) cache[owner.PathVersion] = oldValue;
            else cache.Remove(owner.PathVersion);
        }
    }

    private static void RunPage(FixtureEnvironment environment, string root)
    {
        var page = new PageInstanceMod();
        Native.Check(page.FindName("BtnSort") is Button, "The native mod list lost the sort command.");
        var controller = Native.Get(page, "Native")!;
        var host = Native.Get(controller, "host")!;
        var entries = new List<LocalResourceFile>();
        string mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        LocalResourceFile Create(string name) {
            string path = Path.Combine(mods, name);
            File.WriteAllText(path, "fixture mod");
            return new LocalResourceFile(path);
        }
        for (int i = 0; i < 429; i++) entries.Add(Create($"Fixture{i:D3}.jar"));
        var current = Create("CustomSkinLoader_Universal-15.1.jar");
        current.ProjectVersion = new ResourceVersion { Version = "15.1", ReleaseDate = new DateTime(2026, 1, 1) };
        current.UpdateFile = new ResourceVersion { Version = "15.2", ReleaseDate = new DateTime(2026, 2, 1) };
        Native.Set(host, "Output", (Func<List<LocalResourceFile>>)(() => entries));
        Native.Set(host, "Instance", (Func<ModMinecraft.McInstance?>)(() => null));
        Native.Set(host, "LoaderRun", (Func<int, bool>)(_ => false));
        var list = (ItemsControl)page.FindName("PanList");
        var search = (TextBox)page.FindName("SearchBox");
        var all = (RadioButton)page.FindName("BtnFilterAll");
        var enabled = (RadioButton)page.FindName("BtnFilterEnabled");
        var updates = (RadioButton)page.FindName("BtnFilterCanUpdate");
        var layout = (Grid)page.Child;
        page.Child = null;
        layout.Resources.MergedDictionaries.Add(page.Resources);
        var window = new Window {
            Content = layout, Width = 980, Height = 680,
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false
        };
        try {
            Native.Call(controller.GetType(), "LoadUI", controller);
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            page.InitializeNativeLoader();
            window.Content = null;
            entries.Add(current);
            Native.Call(controller.GetType(), "LoadUI", controller);
            window.Content = layout;
            window.UpdateLayout(); VisualAssetChecks.Drain();
            search.Text = "CustomSkinLoader";
            var row = page.ModItems[current.StorageKey];
            row.Checked = true;
            window.Content = null;
            VisualAssetChecks.Drain();
            window.Content = layout;
            window.UpdateLayout(); VisualAssetChecks.Drain();
            page.RefreshUI();
            for (int i = 0; i < 8; i++) {
                updates.IsChecked = true;
                Native.Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], row), "The update filter lost the searched mod.");
                enabled.IsChecked = true;
                all.IsChecked = true;
                window.UpdateLayout(); VisualAssetChecks.Drain();
                Native.Check(page.SelectedMods.SequenceEqual(new[] { current.StorageKey }), "Returning or filtering dropped the visible selection.");
            }
            Console.WriteLine("PASS local mod page search, selection, details round trip and filter switches.");

            var disabled = Create("CustomSkinLoader_Universal-15.1.jar.disabled");
            entries.Add(disabled);
            Native.Call(controller.GetType(), "LoadUI", controller);
            search.Text = "CustomSkinLoader";
            var targetRow = page.ModItems[current.StorageKey];
            Native.Check(list.Items.OfType<GlassLocalModRow>().Count(row => row.Entry.EnabledName == current.EnabledName) == 2,
                "The two separate files must have independent rows before the move.");
            page.ModItems[disabled.StorageKey].Checked = true;
            var enable = (Button)Native.Get(page, "BtnSelectEnable")!;
            Native.Check(enable.IsEnabled, "The disabled fixture row did not enable its selection command.");
            enable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, enable));
            enabled.IsChecked = true;
            Native.Check(list.Items.OfType<GlassLocalModRow>().Count(row => row.Entry.StorageKey == current.StorageKey) == 1,
                "Enabling an identical disabled copy left two rows for one file.");
            Native.Check(entries.Count == 430 && entries.Count(mod => mod.StorageKey == current.StorageKey) == 1 &&
                !File.Exists(disabled.File.FullName) && File.ReadAllText(current.File.FullName) == "fixture mod",
                "The move did not reconcile the loader snapshot with the files.");
            updates.IsChecked = true;
            Native.Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], targetRow),
                "The existing identical file lost its update metadata or row.");
            Native.Check(page.SelectedMods.Count == 0, "The selection command left an orphaned selection.");
            Console.WriteLine("PASS local mod move onto an identical file followed by the real enabled filter.");

            entries.Add(new LocalResourceFile(current.StorageKey));
            search.Clear();
            search.Text = "CustomSkinLoader";
            enabled.IsChecked = true;
            Native.Check(list.Items.OfType<GlassLocalModRow>().Count(row => row.Entry.StorageKey == current.StorageKey) == 1,
                "A repeated file in the loader output was attached to the list twice.");
        } finally {
            window.Content = null;
            window.Close();
            VisualAssetChecks.Drain();
        }
        environment.AssertNoForbiddenIo();
    }
}
