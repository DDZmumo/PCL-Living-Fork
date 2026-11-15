using System.Collections;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main page, sort-button clicks, presentation controller and row controls.
// Only list/selection/loader host ports are isolated; no sorting algorithm is copied.
internal static class LocalModSortChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Root, "local-mod-sort");
        Directory.CreateDirectory(root);
        // HasUpdate reads a genuine instance-scoped setting even when no
        // update exists. Supply its owner while keeping loader ports isolated.
        ModMinecraft.McInstance? previous = PageInstanceLeft.Instance;
        ModMinecraft.McInstance owner = new(Path.Combine(root, "instance"));
        IDictionary cache = (IDictionary)Native.Get(Native.Entries["VersionAdvanceDisableModUpdate"]!, "ValueCache")!;
        bool existed = cache.Contains(owner.PathVersion);
        object? oldValue = cache[owner.PathVersion];
        try
        {
            cache[owner.PathVersion] = false;
            PageInstanceLeft.Instance = owner;
            RunPage(environment, root);
        }
        finally
        {
            PageInstanceLeft.Instance = previous;
            if (existed) cache[owner.PathVersion] = oldValue;
            else cache.Remove(owner.PathVersion);
        }
    }

    private static void RunPage(FixtureEnvironment environment, string root)
    {
        LocalResourceFile older = Mod("older.jar", "needle older", 2001);
        LocalResourceFile middle = Mod("middle.jar.disabled", "unrelated furniture", 2002);
        LocalResourceFile newer = Mod("newer.jar", "needle newer", 2003);
        List<LocalResourceFile> output = [middle, newer, older];
        PageInstanceMod page = new();
        object controller = Native.Get(page, "Native")!;
        object host = Native.Get(controller, "host")!;
        int scans = 0, animations = 1;
        Native.Set(host, "Output", new Func<List<LocalResourceFile>?>(() => output));
        Native.Set(host, "Instance", new Func<ModMinecraft.McInstance?>(() => null));
        Native.Set(host, "AnimationControl", new Func<int>(() => animations));
        Native.Set(host, "ChangeAnimationControl", new Action<int>(value => animations += value));
        Native.Set(host, "LoaderRun", new Func<int, bool>(_ => { scans++; throw new InvalidOperationException("Sorting must not run the loader."); }));
        Native.Set(host, "NewItem", new Func<LocalResourceFile, bool, MyLocalModItem>((entry, selected) => new() { Entry = entry, Title = entry.Display, Description = entry.File.Name, Checked = selected }));
        Native.Set(host, "BindUpdate", new Action<LocalResourceFile, MyLocalModItem>((_, _) => { }));
        void Call(string method, params object?[] args) => Native.Call(controller.GetType(), method, controller, args);
        T Part<T>(string name) where T : class => (page.FindName(name) ?? Native.Get(page, name)) as T ?? throw new InvalidOperationException("Missing real mod page part " + name);
        MyIconTextButton sort = Part<MyIconTextButton>("BtnSort");
        Panel rows = Part<Panel>("PanList");
        string[] Names() => rows.Children.OfType<MyLocalModItem>().Select(item => item.Entry.File.Name).ToArray();
        void Order(params string[] names) => Native.Check(Names().SequenceEqual(names), "Local mod page order differs: " + string.Join(",", Names()));

        Native.Check(page.FindName("ComboSort") is null && ResourceSortChecks.Mode(page) == 1 && sort.Text == "添加时间 · 倒序", "Real mod page must offer the compact descending addition-time action.");
        page.SelectedMods.Add(older.StorageKey);
        Call("LoadUI");
        Order(newer.File.Name, middle.File.Name, older.File.Name);
        ResourceSortChecks.SetSort(page, 1);
        Order(newer.File.Name, middle.File.Name, older.File.Name);
        Native.Check((LocalResourceSortOrder)Native.Property(controller.GetType(), "SortOrder").GetValue(controller)! == LocalResourceSortOrder.AddedNewest,
            "The real sort-button clicks did not reach the presentation controller.");
        Native.Check(page.SelectedMods.SequenceEqual(new[] { older.StorageKey }) && rows.Children.OfType<MyLocalModItem>().Single(item => ReferenceEquals(item.Entry, older)).Checked,
            "Switching sort lost the real row selection.");
        ResourceSortChecks.SetSort(page, 2);
        Order(older.File.Name, middle.File.Name, newer.File.Name);
        Call("RefreshUI");
        Order(older.File.Name, middle.File.Name, newer.File.Name);
        Call("LoadUI");
        Native.Check(ResourceSortChecks.Mode(page) == 2, "Reloading the actual UI reset the sort preference.");
        Order(older.File.Name, middle.File.Name, newer.File.Name);

        // Filter and search reach the same final visible-list ordering path.
        Call("FilterChanged", 1);
        Order(older.File.Name, newer.File.Name);
        Call("FilterChanged", 2);
        Order(middle.File.Name);
        Call("FilterChanged", 0);
        MySearchBox search = Part<MySearchBox>("SearchBox");
        search.Text = "needle";
        page.SearchRun();
        Order(older.File.Name, newer.File.Name);
        ResourceSortChecks.SetSort(page, 1);
        Order(newer.File.Name, older.File.Name);
        ResourceSortChecks.SetSort(page, 2);
        Order(older.File.Name, newer.File.Name);
        ResourceSortChecks.ClickSort(page);
        Native.Check(ResourceSortChecks.Mode(page) == 1 && sort.Text == "添加时间 · 倒序", "Mod action failed to alternate between ascending and descending order.");
        Order(newer.File.Name, older.File.Name);
        search.Text = "";
        page.SearchRun();
        Order(newer.File.Name, middle.File.Name, older.File.Name);
        Native.Check(output.SequenceEqual(new[] { middle, newer, older }), "Sorting mutated the original loader output.");
        Native.Check(scans == 0 && animations == 1, "Changing sort invoked scanning or unbalanced animation suppression.");

        // A new row arriving from the loader must obey the active mode as well.
        ResourceSortChecks.SetSort(page, 1);
        LocalResourceFile latest = Mod("latest.jar", "needle latest", 2004);
        output.Add(latest);
        Call("LoadUI");
        Order(latest.File.Name, newer.File.Name, middle.File.Name, older.File.Name);
        Native.Check(ResourceSortChecks.Mode(page) == 1 && latest.File.Exists && middle.File.Exists, "Refresh reset ordering or changed disabled/new mod files.");
        Part<FrameworkElement>("PanListBack").Opacity = 1;
        Render(page, Path.Combine(root, "mod-sort-compact.png"), 470, 600, () =>
        {
            Native.Check(page.IsAncestorOf(sort) && page.IsAncestorOf(rows), "Compact mod content was not attached to the real page.");
            ResourceSortChecks.CheckButtonLayout(page);
            Rect combo = sort.TransformToAncestor(page).TransformBounds(new Rect(sort.RenderSize));
            Rect list = rows.TransformToAncestor(page).TransformBounds(new Rect(rows.RenderSize));
            Native.Check(combo.Height > 0 && combo.Right <= page.ActualWidth + 1 && combo.Bottom <= list.Top + 1,
                "Compact mod header clipped the sort action or overlapped the list.");
        });
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native local-mod-sort: actual compact sort-button/default/time order, selection, filters, fuzzy search, refreshed/disabled rows and compact header.");

        LocalResourceFile Mod(string name, string title, int year)
        {
            string path = Path.Combine(root, name);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (StreamWriter writer = new(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write("{\"schemaVersion\":1,\"id\":\"sort_fixture\",\"version\":\"1\"}");
            DateTime created = new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, created);
            Native.Check(File.GetCreationTimeUtc(path) == created, "Windows fixture did not establish its intended file creation time.");
            return new LocalResourceFile(path) { Display = title };
        }
    }

    private static void Render(FrameworkElement element, string file, int width, int height, Action verifyLayout)
    {
        ((FrameworkElement)element.FindName("PanAllBack")).Visibility = Visibility.Visible;
        Grid host = new(); host.Children.Add(element);
        try
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            verifyLayout();
            RenderTargetBitmap image = new(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream output = File.Create(file)) encoder.Save(output);
            ResourceSortChecks.SavePreview(file);
        }
        finally { host.Children.Clear(); }
    }
}
