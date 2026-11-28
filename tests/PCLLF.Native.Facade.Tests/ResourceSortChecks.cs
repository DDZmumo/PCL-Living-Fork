// SPDX-License-Identifier: MPL-2.0
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;
using Controller = PCLCS.ResourcePageController<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using View = PCLCS.ResourcePageView<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;

namespace PCLLF.Native.Facade.Tests;

internal static class ResourceSortChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Data, "resource-sort");
        var owner = FileTaskShell.Install();
        try {
            foreach (var kind in new[] { InstanceResourceKind.ResourcePacks, InstanceResourceKind.ShaderPacks })
                CheckPackageOrdering(owner, root, kind);
            CheckWorldOrdering(owner, root);
            CheckScreenshotOrdering(owner, root);
        } finally { FileTaskShell.CloseUnshownHosts(); }
        Console.WriteLine("PASS instance resource sorting: native buttons, order, selection, reload, filters and search on the Glass resource pages.");
    }

    private static void CheckPackageOrdering(FormMain owner, string root, InstanceResourceKind kind)
    {
        string folder = Path.Combine(root, kind.ToString());
        Directory.CreateDirectory(folder);
        const string newest = "match-newer.zip", middle = "folder-pack", oldest = "match-older.zip";
        bool resourcePack = kind == InstanceResourceKind.ResourcePacks;
        const string metadata = """{"pack":{"pack_format":46,"description":"sorting fixture"}}""";
        foreach (string name in new[] { newest, oldest }) {
            using var archive = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry(resourcePack ? "pack.mcmeta" : "shaders/fixture.vsh").Open());
            writer.Write(resourcePack ? metadata : "// fixture");
        }
        Directory.CreateDirectory(Path.Combine(folder, middle));
        if (resourcePack) File.WriteAllText(Path.Combine(folder, middle, "pack.mcmeta"), metadata);
        else Directory.CreateDirectory(Path.Combine(folder, middle, "shaders"));
        File.SetCreationTimeUtc(Path.Combine(folder, newest), Time(2020));
        Directory.SetCreationTimeUtc(Path.Combine(folder, middle), Time(2019));
        File.SetCreationTimeUtc(Path.Combine(folder, oldest), Time(2018));

        using var page = new GlassLocalResourcesPage(owner, kind);
        var (controller, view, sort) = Parts(page);
        var scanned = InstanceResourceStore.Scan(folder, folder, kind, default);
        string?[] scanOrder = scanned.Select(entry => entry.Name).ToArray();
        controller.ShowResources(folder, folder, scanned);
        AssertOrder(view.PanList, newest, middle, oldest);
        AssertSort(sort, "添加时间 · 倒序", 1, controller);
        string descendingGlyph = SortGlyph(sort);
        Select(view.PanList.Children.OfType<GlassLocalResourceRow>().First());

        SelectSort(page, sort, "添加时间 · 最早在前");
        AssertSort(sort, "添加时间 · 正序", 2, controller);
        Native.Check(SortGlyph(sort) != descendingGlyph, "The resource sort glyph did not change direction.");
        AssertOrder(view.PanList, oldest, middle, newest);
        AssertSelection(view.PanList, controller, Path.Combine(folder, newest));

        controller.ShowResources(folder, folder, InstanceResourceStore.Scan(folder, folder, kind, default));
        AssertSort(sort, "添加时间 · 正序", 2, controller);
        AssertOrder(view.PanList, oldest, middle, newest);
        AssertSelection(view.PanList, controller, Path.Combine(folder, newest));

        Click((ButtonBase)view.FilterFolders.Element);
        AssertOrder(view.PanList, middle);
        Click((ButtonBase)view.FilterAll.Element);
        view.SetSearch("match");
        AssertOrder(view.PanList, oldest, newest);
        SelectSort(page, sort, "添加时间 · 最近在前");
        AssertSort(sort, "添加时间 · 倒序", 1, controller);
        AssertOrder(view.PanList, newest, oldest);
        view.SetSearch("");
        AssertOrder(view.PanList, newest, middle, oldest);
        Native.Check(scanned.Select(entry => entry.Name).SequenceEqual(scanOrder), "Sorting reordered the scanned source catalog.");
    }

    private static void CheckWorldOrdering(FormMain owner, string root)
    {
        string folder = Path.Combine(root, "saves");
        const string newest = "newer-play", oldest = "older-play", unknown = "unknown-play";
        WriteWorld(folder, newest, Time(2020), Time(2018));
        WriteWorld(folder, oldest, Time(2019), Time(2021));
        WriteWorld(folder, unknown, null, Time(2022));
        using var page = new GlassLocalResourcesPage(owner, InstanceResourceKind.Saves);
        var (controller, view, sort) = Parts(page);
        void Reload() => controller.ShowResources(folder, folder, InstanceResourceStore.Scan(folder, folder, InstanceResourceKind.Saves, default));
        Reload();
        Native.Check(controller.Entries.Single(entry => entry.Name == unknown).LastPlayedUtc is null,
            "A world without LastPlayed gained a fabricated play time.");
        AssertSort(sort, "最后游玩 · 倒序", 3, controller);
        AssertOrder(view.PanWorlds, newest, oldest, unknown);
        string descendingGlyph = SortGlyph(sort);
        Select(view.PanWorlds.Children.OfType<StackPanel>()
            .SelectMany(host => host.Children.OfType<GlassLocalResourceRow>())
            .Single(row => row.Entry.Name == newest));
        SelectSort(page, sort, "最后游玩 · 最早在前");
        AssertSort(sort, "最后游玩 · 正序", 4, controller);
        AssertOrder(view.PanWorlds, oldest, newest, unknown);
        Native.Check(SortGlyph(sort) != descendingGlyph, "The world sort glyph did not change direction.");
        Reload();
        AssertSort(sort, "最后游玩 · 正序", 4, controller);
        AssertOrder(view.PanWorlds, oldest, newest, unknown);
        AssertSelection(view.PanWorlds, controller, Path.Combine(folder, newest));
        SelectSort(page, sort, "最后游玩 · 最近在前");
        AssertOrder(view.PanWorlds, newest, oldest, unknown);
        Native.Check(Directory.GetLastWriteTimeUtc(Path.Combine(folder, newest)) == Time(2018),
            "World sorting rewrote a directory timestamp.");
    }

    private static void CheckScreenshotOrdering(FormMain owner, string root)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "screenshots")).FullName;
        const string newest = "new.png", oldest = "old.png";
        foreach (var (name, year) in new[] { (newest, 2020), (oldest, 2018) }) {
            string file = Path.Combine(folder, name);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            File.SetCreationTimeUtc(file, Time(year));
        }
        using var page = new GlassLocalResourcesPage(owner, InstanceResourceKind.Screenshots);
        var (controller, view, sort) = Parts(page);
        void Reload() => controller.ShowResources(folder, folder, InstanceResourceStore.Scan(folder, folder, InstanceResourceKind.Screenshots, default));
        Reload();
        AssertOrder(view.PanScreenshots, newest, oldest);
        Select(view.PanScreenshots.Children.OfType<GlassResourceScreenshot>().First());
        AssertSelection(view.PanScreenshots, controller, Path.Combine(folder, newest));
        SelectSort(page, sort, "添加时间 · 最早在前");
        Reload();
        AssertSort(sort, "添加时间 · 正序", 2, controller);
        AssertOrder(view.PanScreenshots, oldest, newest);
        AssertSelection(view.PanScreenshots, controller, Path.Combine(folder, newest));
    }

    private static (Controller, View, Button) Parts(GlassLocalResourcesPage page)
    {
        // Open the real page, then supply local scan results at the normal render boundary.
        page.Open(loadData: false);
        var controller = (Controller)Native.Get(page, "_controller")!;
        var view = (View)Native.Get(controller, "View")!;
        var sort = view.SortControl as Button ?? throw new InvalidOperationException("The Glass page has no sort button.");
        return (controller, view, sort);
    }

    private static void Click(ButtonBase control)
    {
        Native.Check(control.IsEnabled, "The resource test action is disabled.");
        if (control is Button button) {
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            VisualAssetChecks.Drain();
        } else control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, control));
    }

    private static void SelectSort(GlassLocalResourcesPage page, Button anchor, string label)
    {
        var window = new Window { Content = page, Width = 800, Height = 540, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show();
            VisualAssetChecks.Pump(() => anchor.IsLoaded && anchor.IsVisible, "load the resource sort button");
            Click(anchor);
            Popup popup = GlassChoicePopup.Current(anchor) ?? throw new InvalidOperationException("The sort menu did not open.");
            var option = GlassFeedbackChecks.Visuals(popup.Child).OfType<UIElement>()
                .Single(element => AutomationProperties.GetName(element) == label);
            var keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(option), Environment.TickCount, Key.Enter) {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            option.RaiseEvent(keyboard);
            Native.Check(!popup.IsOpen, "Selecting a sort mode did not close the menu.");
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void Select(Panel row)
    {
        var selector = row.Children.OfType<CheckBox>().Single();
        // The click toggles the actual checkbox and runs its selection callback.
        Native.Call(selector.GetType(), "OnClick", selector);
    }

    private static void AssertSort(Button sort, string text, int mode, Controller controller) =>
        Native.Check(controller.SortMode == mode && AutomationProperties.GetName(sort) == text &&
            ((StackPanel)sort.Content).Children.OfType<TextBlock>().Single().Text == text,
            "The native resource sort action and controller disagree about the current sort.");

    private static string SortGlyph(Button sort) =>
        ((StackPanel)sort.Content).Children.OfType<System.Windows.Shapes.Path>().Single().Data.ToString();

    private static IEnumerable<(InstanceResourceEntry Entry, bool Selected)> Rows(Panel panel)
    {
        foreach (var child in panel.Children) {
            if (child is GlassLocalResourceRow row) yield return (row.Entry, row.Checked);
            else if (child is GlassResourceScreenshot screenshot) yield return (screenshot.Entry, screenshot.Checked);
            else if (child is Panel host)
                foreach (var nested in Rows(host)) yield return nested;
        }
    }

    private static void AssertOrder(Panel panel, params string[] expected)
    {
        var actual = Rows(panel).Select(row => row.Entry.Name).ToArray();
        Native.Check(actual.SequenceEqual(expected),
            $"Expected resource order [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void AssertSelection(Panel panel, Controller controller, string path) =>
        Native.Check(controller.SelectedPaths.SetEquals([path]) &&
            Rows(panel).Where(row => row.Selected).Select(row => row.Entry.FullPath).SequenceEqual([path], StringComparer.OrdinalIgnoreCase),
            "Sorting or reloading lost the resource selected by path.");

    private static DateTime Time(int year) => new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void WriteWorld(string root, string name, DateTime? lastPlayed, DateTime modified)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        using (var output = File.Create(Path.Combine(folder, "level.dat")))
        using (var zip = new GZipStream(output, CompressionMode.Compress)) {
            WorldChecks.Header(zip, 10, "");
            WorldChecks.Header(zip, 10, "Data");
            WorldChecks.Text(zip, "LevelName", name);
            if (lastPlayed is DateTime date) WorldChecks.Number(zip, 4, "LastPlayed", new DateTimeOffset(date).ToUnixTimeMilliseconds());
            zip.WriteByte(0);
            zip.WriteByte(0);
        }
        Directory.SetLastWriteTimeUtc(folder, modified);
    }
}
