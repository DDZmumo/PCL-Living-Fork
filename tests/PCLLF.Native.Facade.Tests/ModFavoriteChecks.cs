using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Startup;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Foundation;
using PCLLF.Presentation.Windowing;
using Application = System.Windows.Application;
using MainHost = PCLLF.Presentation.Windowing.LegacyMainWindowHost<PCL.FormMain.PageStackData, PCL.ModMinecraft.McInstance, PCL.ModAnimation.AniData>;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main BAML, typed title events, resource rows, matcher callbacks and routing.
// These are hosted component checks, separate from the genuine product cold entry.
internal static partial class ModFavoriteChecks {
    private static int Pending => (int)Native.Property(Native.Type("PCL.ResourceFavorites"), "PendingCount").GetValue(null)!;
    internal static void Run(FixtureEnvironment environment) {
        using Scope scope = new(environment);
        LocalRows(scope);
        ResourceRows(scope);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native resource favorites: original PageResource search/result/paging BAML, default online-controller host contract, native search/Enter/reset/source/tag/exact-version/loader/category combinations and last-item pagination; zero remote request callbacks and an unstarted favorites search loader; project identity, online/detail/local stars, cache metadata, writer heartbeat/failure/corruption and minimum-window/theme policies. Hosted component, not product cold entry.");
    }
    private static void LocalRows(Scope s) {
        string firstRoot = Path.Combine(s.Root, "instance-a");
        s.Legacy = Path.Combine(firstRoot, "PCL", "Setup.ini"); Directory.CreateDirectory(Path.GetDirectoryName(s.Legacy)!);
        File.WriteAllText(s.Legacy, "UiModFavorite:Alpha.jar|Beta.jar\r\nKeep:保留\r\n"); s.LegacyBytes = File.ReadAllBytes(s.Legacy);
        var owner = new ModMinecraft.McInstance(firstRoot); LauncherInstanceContext.Instance = owner;
        Native.Check(owner.Config.ContextStamp.Kind == MmcConfigurationContextKind.Unmanaged, "The ordinary favorite fixture must exist before its provider attaches.");
        LocalResourceFile a = s.Mod("Alpha-v1.jar"), b = s.Mod("Alpha-v2.jar.disabled"), unknown = s.Mod("Unknown.jar");
        a.Project = Project("AbCd1234"); b.Project = Project("AbCd1234");
        var page = new PageInstanceMod(); ModMain.FrmInstanceMod = page;
        object controller = Native.Get(page, "Native")!, host = Native.Get(controller, "host")!;
        Native.Set(host, "Output", new Func<List<LocalResourceFile>>(() => new() { a, b, unknown }));
        Native.Set(host, "Instance", new Func<ModMinecraft.McInstance?>(() => null)); // Unrelated dependency inspection has its own family.
        Native.Call(controller.GetType(), "LoadUI", controller);
        Native.Check(page.ModItems.Count == 3 && page.FindName("BtnFilterFavorite") is null && Part<Panel>(page, "PanFilter").Children.Count == 5, "Original local filters or the new duplicate filter/rows changed");
        Native.Call(controller.GetType(),"FilterChanged",controller,4);
        Native.Check(Part<ItemsControl>(page,"PanList").Items.Count==2,"Exact same-project duplicates were not grouped in the native page.");
        Native.Call(controller.GetType(),"FilterChanged",controller,0);
        GlassLocalModRow first = page.ModItems[a.StorageKey], second = page.ModItems[b.StorageKey], unmapped = page.ModItems[unknown.StorageKey];
        foreach (GlassLocalModRow row in page.ModItems.Values) { row.Refresh(); s.Observe(row); }
        s.LocalStar = LocalFavorite(first); s.OtherStar = LocalFavorite(second);
        Native.Check(s.LocalStar.Visibility == Visibility.Visible && LocalFavorite(unmapped).Visibility == Visibility.Collapsed, "Unmatched local mod exposes favorites");
        CheckLocalActionLayout(first, unmapped);
        Click(LocalFavorite(unmapped)); Native.Check(!File.Exists(s.File), "Unmatched file became a project favorite");
        int serial = Serial(); s.EmptyIcon = LocalIcon(s.LocalStar); Click(s.LocalStar);
        Native.Check(s.Store.Snapshot.Count == 1 && LocalIcon(s.LocalStar) != s.EmptyIcon && LocalIcon(s.OtherStar) == LocalIcon(s.LocalStar), "Different local files/versions created separate favorites");
        SaveLocalActionProof(first, "local-mod-favorite-saved.png");
        var snapshot = s.Store.Snapshot;
        for (int i = 0; i < 5000; i++) Native.Check(s.Store.Contains(snapshot[0].Identity) && ReferenceEquals(snapshot, s.Store.Snapshot), "Read reparsed the store");
        string secondRoot = Path.Combine(s.Root, "instance-b"); Directory.CreateDirectory(secondRoot);
        LauncherInstanceContext.Instance = new ModMinecraft.McInstance(secondRoot); second.Refresh();
        Native.Check(LocalIcon(s.OtherStar) == LocalIcon(s.LocalStar), "Favorites were instance-scoped"); LauncherInstanceContext.Instance = owner;
        unknown.Project = RichProject("Other123", ResourcePlatforms.Modrinth, "Local cache fixture", "fixture", Array.Empty<string>(), ModLoaders.None);
        unknown.ProjectVersion = Version("LocalFileVersion"); unknown.ProjectVersion.GameVersions = new List<string> { "1.20.1" };
        Wait(() => LocalFavorite(unmapped).Visibility == Visibility.Visible);
        Native.Check(Grid.GetColumn(LocalFavorite(unmapped)) == 3, "A late platform match failed to reserve its independent favorite action.");
        Click(LocalFavorite(unmapped));
        var localMetadata = s.Store.Snapshot.Single(entry => entry.Identity.ProjectId == "Other123");
        Native.Check(localMetadata.KnownGameVersions.SequenceEqual(new[] { "1.20.1" }) && localMetadata.ModLoaders == ModLoaders.Fabric, "Trusted local platform version metadata was not retained as project compatibility");
        Click(LocalFavorite(unmapped));
        Native.Check(Serial() == serial && ReferenceEquals(page.ModItems[a.StorageKey], first), "Favorite/matcher rebuilt local rows");
        var online = new GlassResourceProjectRow(a.Project, new GlassPageResources(page), true, true, null);
        s.Observe(online);
        var star = (Button)Native.Get(online, "_favorite")!;
        Click(star);
        Native.Check(s.Store.Snapshot.Count == 0 && LocalIcon(s.LocalStar) == s.EmptyIcon && LocalIcon(s.OtherStar) == s.EmptyIcon,
            "Native cloud removal did not synchronize both local files.");
        Click(star);
        // Holding the cross-process writer lock must leave the dispatcher responsive.
        using (var held = new FileStream(s.File + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            SendClick(s.OtherStar); bool heartbeat = false;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => heartbeat = true)); Drain();
            Native.Check(heartbeat && Pending == 1 && !s.OtherStar.IsEnabled && s.Store.Snapshot.Count == 1, "A waiting favorite writer blocked UI/snapshot reads");
            SendClick(s.LocalStar); Native.Check(Pending == 1, "Repeated same-project clicks were admitted");
        }
        Wait(() => Pending == 0); Native.Check(s.Store.Snapshot.Count == 0, "Pending lock save did not finish");
        Click(s.LocalStar);
        second.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); string unloadedIcon = LocalIcon(s.OtherStar);
        Click(s.LocalStar); Native.Check(LocalIcon(s.OtherStar) == unloadedIcon, "An unloaded row still received background favorite callbacks");
        second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Native.Check(LocalIcon(s.OtherStar) == s.EmptyIcon, "A reloaded row missed the shared snapshot");
        Click(s.LocalStar);
        int Serial() => (int)Native.Property(controller.GetType(), "ListRebuildSerial").GetValue(controller)!;
    }
    private static Button LocalFavorite(GlassLocalModRow row) => (Button)Native.Get(row, "_favorite")!;
    private static string CanonicalGlyph(string path) => Geometry.Parse(path).ToString();
    private static string LocalIcon(Button button) => ((System.Windows.Shapes.Path)button.Content).Data.ToString();

    private static void CheckLocalActionLayout(GlassLocalModRow row, GlassLocalModRow unknown) {
        Button favorite = LocalFavorite(row);
        var actions = (StackPanel)Native.Get(row, "_actions")!;
        Native.Check(ReferenceEquals(favorite.Parent, row) && favorite.Opacity == 1 && favorite.Width >= 25 &&
            actions.Visibility == Visibility.Collapsed && favorite.Visibility == Visibility.Visible &&
            LocalFavorite(unknown).Visibility == Visibility.Collapsed, "The favorite slot was hidden with hover actions or displayed for an unmatched file.");
        ((TextBlock)Native.Get(row, "_title")!).Text = "A long recognized mod name that must leave room for its project favorite";
        Native.Call(row.GetType(), "RefreshActionVisibility", row, true);
        row.Measure(new Size(640,54)); row.Arrange(new Rect(0,0,640,54)); row.UpdateLayout();
        double favoriteLeft = favorite.TranslatePoint(new Point(), row).X;
        var title = (Button)Native.Get(row, "_body")!;
        Native.Check(title.TranslatePoint(new Point(title.ActualWidth,0), row).X <= favoriteLeft &&
            actions.TranslatePoint(new Point(actions.ActualWidth,0), row).X <= favoriteLeft,
            "The title or native hover toolbar overlaps the favorite action.");
        SaveLocalActionProof(row, "local-mod-favorite-empty.png");
        Native.Call(row.GetType(), "RefreshActionVisibility", row, false);
    }
    private static void SaveLocalActionProof(GlassLocalModRow row, string name) {
        int option = Array.IndexOf(Environment.GetCommandLineArgs(), "--favorites-proof");
        if (option < 0) return;
        string directory = Path.GetFullPath(Environment.GetCommandLineArgs()[option + 1]);
        Directory.CreateDirectory(directory);
        row.Measure(new Size(640,54)); row.Arrange(new Rect(0,0,640,54)); row.UpdateLayout();
        var image = new RenderTargetBitmap(640,54,96,96,PixelFormats.Pbgra32);
        image.Render(row);
        VisualAssetChecks.Save(image, Path.Combine(directory, name));
    }

    private static ResourceProject RichProject(string id, ResourcePlatforms platform, string name, string tag, string[] versions, ModLoaders loader) {
        JObject json = Project(id, platform: platform).ToJson(); json["RawName"] = name; json["Tags"] = new JArray(tag);
        json["KnownGameVersions"] = new JArray(versions); json["ModLoaders"] = (int)loader; return new ResourceProject(json);
    }
    private static void SelectTag(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture) == tag);
    private sealed class Scope : IDisposable {
        private readonly Dictionary<string, object?> globals;
        private readonly ModMinecraft.McInstance previousInstance = LauncherInstanceContext.Instance;
        private readonly Dictionary<string, ModAnimation.AniGroupEntry> groups = ModAnimation.AniGroups;
        private readonly int suppression = ModAnimation.AniControlEnabled;
        private readonly List<DictionaryEntry> versions = new(), projects = new();
        private readonly IDictionary projectCache = (IDictionary)Native.Field(typeof(ResourceProject), "Cache").GetValue(null)!;
        private readonly List<FrameworkElement> observed = new();
        private readonly Dictionary<string, bool> settings = new[] { "LivingModpackMode", "LivingModpackExported", "UiHiddenPageFavorites" }.ToDictionary(key => key, key => Settings.Get<bool>(key));
        private readonly object networkHost;
        private readonly object? request;
        private readonly object searchHost;
        private readonly object? searchRequest;
        private int requests;
        internal readonly IDictionary VersionCache = (IDictionary)Native.Field(typeof(ResourceVersion), "ProjectFilesCache").GetValue(null)!;
        internal readonly string Root, File;
        internal readonly ResourceFavoriteStore Store;
        internal readonly Dictionary<string, byte[]> ModBytes = new();
        internal Button LocalStar = null!, OtherStar = null!; 
        internal string EmptyIcon = "", Legacy = ""; internal byte[] LegacyBytes = Array.Empty<byte>();
        internal Scope(FixtureEnvironment environment) {
            Root = Path.Combine(environment.Root, "resource-favorites"); Directory.CreateDirectory(Root);
            File = Path.Combine(Paths.Base, "PCL", "ResourceFavorites.json");
            Native.Check(Native.Full(File).StartsWith(Native.Full(environment.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(File), "Refusing non-fixture/pre-existing favorite data");
            Store = (ResourceFavoriteStore)Native.Property(Native.Type("PCL.ResourceFavorites"), "Store").GetValue(null)!;
            Native.Check(Store.Snapshot.Count == 0, "Refusing pre-existing cached favorites");
            globals = new[] { "FrmMain", "FrmInstanceMod" }.ToDictionary(name => name, name => Native.Field(typeof(ModMain), name).GetValue(null));
            foreach (DictionaryEntry pair in VersionCache) versions.Add(pair); foreach (DictionaryEntry pair in projectCache) projects.Add(pair);
            object pipeline = Native.Field(Native.Type("PCL.ResourceCatalogRuntime"), "Pipeline").GetValue(null)!;
            networkHost = Native.Get(pipeline, "host")!; request = Native.Get(networkHost, "Request");
            Native.Set(networkHost, "Request", new Func<string, System.Net.Http.HttpMethod, string, string, JToken>((_, _, _, _) => {
                Interlocked.Increment(ref requests); throw new InvalidOperationException("Favorite fixture must use cached platform data");
            }));
            object search = Native.Field(typeof(ResourceSearcher), "Engine").GetValue(null)!;
            searchHost = Native.Get(search, "host")!; searchRequest = Native.Get(searchHost, "Request");
            Native.Set(searchHost, "Request", new Func<string, JToken>(_ => { Interlocked.Increment(ref requests); throw new InvalidOperationException("Favorites must never request remote search"); }));
            ModAnimation.AniGroups = new(); ModAnimation.AniControlEnabled = 1;
        }
        internal LocalResourceFile Mod(string name) {
            string path = Path.Combine(Root, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open())) writer.Write("{\"schemaVersion\":1,\"id\":\"fixture\",\"version\":\"1\"}");
            ModBytes[path] = System.IO.File.ReadAllBytes(path); return new LocalResourceFile(path) { Display = name };
        }
        internal void Observe(FrameworkElement row) { row.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); observed.Add(row); }
        public void Dispose() {
            Wait(() => Pending == 0);
            foreach (FrameworkElement row in observed) row.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Drain();
            Native.Check(requests == 0, "Favorites unexpectedly requested a platform endpoint"); Native.Set(networkHost, "Request", request);
            Native.Set(searchHost, "Request", searchRequest);
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File); Store.Reload();
            VersionCache.Clear(); foreach (DictionaryEntry pair in versions) VersionCache[pair.Key] = pair.Value;
            projectCache.Clear(); foreach (DictionaryEntry pair in projects) projectCache[pair.Key] = pair.Value;
            foreach (var pair in globals) Native.Field(typeof(ModMain), pair.Key).SetValue(null, pair.Value);
            LauncherInstanceContext.Instance = previousInstance; foreach (var pair in settings) Native.Cache(pair.Key, pair.Value);
            ModAnimation.AniGroups = groups; ModAnimation.AniControlEnabled = suppression;
        }
    }
    private static ResourceProject Project(string id, ResourceTypes type = ResourceTypes.Mod, ResourcePlatforms platform = ResourcePlatforms.Modrinth) => new(new JObject {
        ["Platform"] = (int)platform, ["Types"] = (int)type, ["Id"] = id, ["Slug"] = "fixture", ["RawName"] = "Fixture " + id,
        ["Description"] = "Offline resource fixture", ["LastUpdate"] = new DateTime(2024, 1, 1), ["DownloadCount"] = 42,
        ["Tags"] = new JArray("fixture"), ["Drops"] = new JArray(210), ["KnownGameVersions"] = new JArray("1.21.1"), ["ModLoaders"] = (int)((type & ResourceTypes.Mod) != 0 ? ModLoaders.Fabric : ModLoaders.None), ["Website"] = "https://example.invalid/project"
    });
    private static ResourceVersion Version(string id) => new() {
        Id = id, Display = id, FileName = id + ".jar", DownloadUrls = new List<string> { "https://example.invalid/" + id },
        GameVersions = new List<string> { "1.21.1" }, ResourceType = ResourceTypes.Mod, ModLoaders = ModLoaders.Fabric,
        ReleaseType = ResourceVersion.ReleaseTypes.Release, ReleaseDate = new DateTime(2024, 1, 1), Version = "1"
    };
    private static T Part<T>(FrameworkElement root, string name) where T : class {
        return root.FindName(name) as T ?? throw new InvalidOperationException("Missing native part " + name);
    }
    private static void SendClick(FrameworkElement element) {
        if (element is Button button) {
            if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }
    }
    private static void Click(FrameworkElement element) { SendClick(element); Wait(() => Pending == 0); }
    private static void Drain() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition) { var time = Stopwatch.StartNew(); while (!condition()) { if (time.ElapsedMilliseconds > 5000) throw new TimeoutException("Favorite fixture callbacks did not settle"); Drain(); Thread.Sleep(5); } Drain(); }
}
