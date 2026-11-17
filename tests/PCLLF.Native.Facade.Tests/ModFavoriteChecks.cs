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
        WindowRoutes(scope);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native resource favorites: original PageResource search/result/paging BAML, default online-controller host contract, native search/Enter/reset/source/tag/exact-version/loader/category combinations and last-item pagination; zero remote request callbacks and an unstarted favorites search loader; project identity, online/detail/local stars, cache metadata, writer heartbeat/failure/corruption and minimum-window/theme policies. Hosted component, not product cold entry.");
    }
    private static void LocalRows(Scope s) {
        string firstRoot = Path.Combine(s.Root, "instance-a");
        s.Legacy = Path.Combine(firstRoot, "PCL", "Setup.ini"); Directory.CreateDirectory(Path.GetDirectoryName(s.Legacy)!);
        File.WriteAllText(s.Legacy, "UiModFavorite:Alpha.jar|Beta.jar\r\nKeep:保留\r\n"); s.LegacyBytes = File.ReadAllBytes(s.Legacy);
        var owner = new ModMinecraft.McInstance(firstRoot); PageInstanceLeft.Instance = owner;
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
        Native.Check(Part<Panel>(page,"PanList").Children.Count==2,"Exact same-project duplicates were not grouped in the native page.");
        Native.Call(controller.GetType(),"FilterChanged",controller,0);
        MyLocalModItem first = page.ModItems[a.StorageKey], second = page.ModItems[b.StorageKey], unmapped = page.ModItems[unknown.StorageKey];
        foreach (MyLocalModItem row in page.ModItems.Values) { row.Refresh(); s.Observe(row); }
        s.LocalStar = Part<MyIconButton>(first, "BtnFavorite"); s.OtherStar = Part<MyIconButton>(second, "BtnFavorite");
        Native.Check(s.LocalStar.Visibility == Visibility.Visible && Part<MyIconButton>(unmapped, "BtnFavorite").Visibility == Visibility.Collapsed, "Unmatched local mod exposes favorites");
        CheckLocalActionLayout(first, unmapped);
        Click(Part<MyIconButton>(unmapped, "BtnFavorite")); Native.Check(!File.Exists(s.File), "Unmatched file became a project favorite");
        int serial = Serial(); s.EmptyIcon = s.LocalStar.Logo; Click(s.LocalStar);
        Native.Check(s.Store.Snapshot.Count == 1 && s.LocalStar.Logo != s.EmptyIcon && s.OtherStar.Logo == s.LocalStar.Logo, "Different local files/versions created separate favorites");
        SaveLocalActionProof(first, "local-mod-favorite-saved.png");
        var snapshot = s.Store.Snapshot;
        for (int i = 0; i < 5000; i++) Native.Check(s.Store.Contains(snapshot[0].Identity) && ReferenceEquals(snapshot, s.Store.Snapshot), "Read reparsed the store");
        string secondRoot = Path.Combine(s.Root, "instance-b"); Directory.CreateDirectory(secondRoot);
        PageInstanceLeft.Instance = new ModMinecraft.McInstance(secondRoot); second.Refresh();
        Native.Check(s.OtherStar.Logo == s.LocalStar.Logo, "Favorites were instance-scoped"); PageInstanceLeft.Instance = owner;
        unknown.Project = RichProject("Other123", ResourcePlatforms.Modrinth, "Local cache fixture", "fixture", Array.Empty<string>(), ModLoaders.None);
        unknown.ProjectVersion = Version("LocalFileVersion"); unknown.ProjectVersion.GameVersions = new List<string> { "1.20.1" };
        Wait(() => Part<MyIconButton>(unmapped, "BtnFavorite").Visibility == Visibility.Visible);
        Native.Check(Part<ColumnDefinition>(unmapped, "ColumnPaddingRight").Width.Value == 34, "A late platform match failed to reserve its independent favorite action.");
        Click(Part<MyIconButton>(unmapped, "BtnFavorite"));
        var localMetadata = s.Store.Snapshot.Single(entry => entry.Identity.ProjectId == "Other123");
        Native.Check(localMetadata.KnownGameVersions.SequenceEqual(new[] { "1.20.1" }) && localMetadata.ModLoaders == ModLoaders.Fabric, "Trusted local platform version metadata was not retained as project compatibility");
        Click(Part<MyIconButton>(unmapped, "BtnFavorite"));
        Native.Check(Serial() == serial && ReferenceEquals(page.ModItems[a.StorageKey], first), "Favorite/matcher rebuilt local rows");
        s.Online = a.Project.ToResourceItem(true, true).Init(); s.Observe(s.Online);
        Native.Check(Part<MyIconButton>(s.Online, "BtnFavorite").Logo == s.LocalStar.Logo, "Cloud row missed local favorite");
        Click(Part<MyIconButton>(s.Online, "BtnFavorite"));
        Native.Check(s.Store.Snapshot.Count == 0 && s.LocalStar.Logo == s.EmptyIcon && s.OtherStar.Logo == s.EmptyIcon, "Cloud removal did not synchronize both local files");
        Click(Part<MyIconButton>(s.Online, "BtnFavorite"));
        // Holding the cross-process writer lock must leave the dispatcher responsive.
        using (var held = new FileStream(s.File + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            SendClick(s.OtherStar); bool heartbeat = false;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => heartbeat = true)); Drain();
            Native.Check(heartbeat && Pending == 1 && !s.OtherStar.IsEnabled && s.Store.Snapshot.Count == 1, "A waiting favorite writer blocked UI/snapshot reads");
            SendClick(s.LocalStar); Native.Check(Pending == 1, "Repeated same-project clicks were admitted");
        }
        Wait(() => Pending == 0); Native.Check(s.Store.Snapshot.Count == 0, "Pending lock save did not finish");
        Click(s.LocalStar);
        second.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); string unloadedIcon = s.OtherStar.Logo;
        Click(s.LocalStar); Native.Check(s.OtherStar.Logo == unloadedIcon, "An unloaded row still received background favorite callbacks");
        second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Native.Check(s.OtherStar.Logo == s.EmptyIcon, "A reloaded row missed the shared snapshot");
        Click(s.LocalStar);
        int Serial() => (int)Native.Property(controller.GetType(), "ListRebuildSerial").GetValue(controller)!;
    }
    private static void CheckLocalActionLayout(MyLocalModItem row, MyLocalModItem unknown) {
        MyIconButton favorite = Part<MyIconButton>(row, "BtnFavorite");
        Native.Call(row.GetType(), "InitLate", row, row, EventArgs.Empty);
        Native.Check(ReferenceEquals(favorite.Parent, row) && favorite.Theme == MyIconButton.Themes.Color && favorite.Opacity == 1 && favorite.Width == 25,
            "Local project favorite is still a faint title annotation.");
        Native.Check(row.Buttons.Any() && row.ButtonStack is StackPanel && row.ButtonStack.Opacity == 0 && row.ButtonStack.Margin.Right == 35 && favorite.Visibility == Visibility.Visible,
            "Native hover actions replaced or hid the independent favorite action.");
        Native.Check(Part<ColumnDefinition>(row, "ColumnPaddingRight").Width.Value == 34 && Part<ColumnDefinition>(unknown, "ColumnPaddingRight").Width.Value == 4,
            "Recognized/unknown rows do not reserve the appropriate favorite slot.");
        row.Title = "A long recognized mod name that must leave room for its project favorite";
        row.Measure(new Size(640,44)); row.Arrange(new Rect(0,0,640,44)); row.UpdateLayout();
        double favoriteLeft = favorite.TranslatePoint(new Point(), row).X;
        FrameworkElement title = Part<FrameworkElement>(row, "PanTitle");
        Native.Check(title.TranslatePoint(new Point(title.ActualWidth,0), row).X <= favoriteLeft &&
            row.ButtonStack.TranslatePoint(new Point(row.ButtonStack.ActualWidth,0), row).X <= favoriteLeft,
            "The title or native hover toolbar overlaps the favorite action.");
        SaveLocalActionProof(row, "local-mod-favorite-empty.png");
    }
    private static void SaveLocalActionProof(MyLocalModItem row, string name) {
        string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args, "--favorites-proof");
        if (option < 0) return;
        string directory = Path.GetFullPath(args[option+1]); Directory.CreateDirectory(directory);
        double oldOpacity = row.ButtonStack.Opacity; GridLength padding = Part<ColumnDefinition>(row, "ColumnPaddingRight").Width;
        try {
            // Expose the existing native actions only for this hosted rendering.
            // No desktop pointer or real mod operation is performed.
            row.ButtonStack.Opacity = 1;
            foreach (MyIconButton button in row.Buttons.Append(Part<MyIconButton>(row, "BtnFavorite"))) button.RefreshAnim();
            Part<ColumnDefinition>(row, "ColumnPaddingRight").Width = new GridLength(row.ButtonStack.Margin.Right+row.Buttons.Sum(button => button.Width));
            row.Measure(new Size(640,44)); row.Arrange(new Rect(0,0,640,44)); row.UpdateLayout();
            var background = new DrawingVisual();
            using (DrawingContext context = background.RenderOpen()) context.DrawRectangle((Brush)Application.Current.Resources["ColorBrushWhite"], null, new Rect(0,0,640,44));
            var image = new RenderTargetBitmap(640,44,96,96,PixelFormats.Pbgra32); image.Render(background); image.Render(row);
            VisualAssetChecks.Save(image, Path.Combine(directory,name));
        } finally { row.ButtonStack.Opacity = oldOpacity; Part<ColumnDefinition>(row, "ColumnPaddingRight").Width = padding; row.UpdateLayout(); }
    }
    private static void WindowRoutes(Scope s) {
        FormMain window = s.Window = CreateMain();
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Loaded"));
        window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, Native.Method(typeof(FormMain), "FormMain_Closing"));
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000; window.Width = 870; window.Height = 520; window.Opacity = 1; window.Show();
        Part<RotateTransform>(window, "TransformRotate").Angle = 0;
        Part<TranslateTransform>(window, "TransformPos").X = Part<TranslateTransform>(window, "TransformPos").Y = 0;
        MyRadioButton title = Part<MyRadioButton>(window, "BtnTitleSelect5"); Click(title);
        Panel top = Part<Panel>(window, "PanTitleSelect");
        Native.Check(top.Children.IndexOf(title) == top.Children.IndexOf(Part<MyRadioButton>(window, "BtnTitleSelect1")) + 1 && title.Tag.Equals("10"), "Favorites must sit immediately right of Download without renumbering its PageType");
        Native.Check((int)FormMain.PageType.Favorites == 10 && (int)LegacyMainPage.Favorites == 10 && (int)FormMain.PageType.ResourceDetail == 8 && !string.IsNullOrEmpty(title.Logo), "Stable menu identity/vector missing");
        Native.Check(window.PageCurrent.Page == FormMain.PageType.Favorites && window.PageRight is PageFavorites && title.Checked && window.PageStack.Count == 0,
            "Real title click did not open favorites: " + window.PageCurrent.Page + "/" + window.PageRight?.GetType().Name + "/" + title.Checked + "/" + window.PageStack.Count);
        var page = s.Favorites = (PageFavorites)window.PageRight!; WaitPage(page);
        Native.Check(page.FindName("Content") is PageResource && title.Logo == s.EmptyIcon, "Favorites must reuse the download PageResource and original hollow favorite glyph");
        OnlineSearchBindings(s, page);
        Native.Check(ReferenceEquals(Part<Border>(window, "PanMainRight").Child, page) && Part<Panel>(page, "PanResults").Children.Count == 1, "Route did not attach/render the cached project");
        ResourceRows(s, page);
        ResourceTypes[] kinds = { ResourceTypes.ModPack, ResourceTypes.Map, ResourceTypes.DataPack, ResourceTypes.ResourcePack, ResourceTypes.Shader };
        foreach (var kind in kinds) { var row = Project("Type" + (int)kind, kind).ToResourceItem(false, false).Init(); Click(Part<MyIconButton>(row, "BtnFavorite")); }
        Native.Check(s.Store.Snapshot.Count == 6 && window.PageCurrent.Page == FormMain.PageType.Favorites, "Cloud stars lost categories or bubbled into details");
        // Names, numeric-looking IDs and case variants must never merge providers/projects.
        var identities = new[] { Project("123", platform: ResourcePlatforms.CurseForge), Project("123"), Project("Case1234"), Project("case1234") };
        foreach (var item in identities) Click(Part<MyIconButton>(item.ToResourceItem(false, false).Init(), "BtnFavorite"));
        Native.Check(s.Store.Snapshot.Count == 10, "Native cloud actions merged provider identities or Modrinth case");
        s.VersionCache[ResourceProjectKey.Get(ResourcePlatforms.CurseForge, "123")] = new List<ResourceVersion> { Version("CF-file") };
        s.VersionCache[ResourceProjectKey.Get(ResourcePlatforms.Modrinth, "123")] = new List<ResourceVersion> { Version("MR-file") };
        foreach (ResourceProject identity in new[] { identities[0], identities[1], identities[0] }) {
            MyResourceItem row = identity.ToResourceItem(true, true).Init();
            Native.Call(typeof(MyResourceItem), "MyResourceItem_Click", row, row, EventArgs.Empty);
            var providerHeader = DetailHeader(s); var details = Native.Get(ModMain.FrmDownloadResourceDetail, "Controller")!;
            string expected = identity.Platform == ResourcePlatforms.CurseForge ? "CF-file" : "MR-file";
            Native.Check(((ResourceProject)providerHeader.Tag).Platform == identity.Platform && ((ResourceProject)Native.Get(details, "Project")!).Platform == identity.Platform && s.DetailLoader!.Output.Single().Id == expected,
                "Sequential actual detail navigation reused the other provider's project/version for the same raw ID");
            window.PageBack(); WaitPage(page);
        }
        foreach (var item in identities) Click(Part<MyIconButton>(item.ToResourceItem(false, false).Init(), "BtnFavorite"));
        foreach (MyListItem button in Part<Panel>(ModMain.FrmFavoritesLeft, "PanItem").Children.OfType<MyListItem>()) { Click(button); Native.Check(Part<Panel>(page, "PanResults").Children.Count == (button.Tag.Equals("0") ? 6 : 1), "Category binding has the wrong scope: " + button.Tag + "/" + button.Checked + "/" + ModMain.FrmFavoritesLeft.PageID + "/" + Part<Panel>(page, "PanResults").Children.Count); }
        Click(Part<Panel>(ModMain.FrmFavoritesLeft, "PanItem").Children.OfType<MyListItem>().First());
        SharedSearchFilters(s, page);
        var liveRow = Part<Panel>(page, "PanResults").Children.Cast<FrameworkElement>().ToArray().Select(MyVirtualizingElement.TryInit).OfType<MyResourceItem>().First();
        int bubbled = 0; MyResourceItem.ClickEventHandler rowClick = (_, _) => bubbled++; liveRow.Click += rowClick;
        Native.Set(Native.Get(liveRow, "Controller")!, "mouseDown", true); // A preceding row press must not turn a star release into navigation.
        Click(Part<MyIconButton>(liveRow, "BtnFavorite"));
        Native.Check(bubbled == 0 && window.PageCurrent.Page == FormMain.PageType.Favorites && s.Store.Snapshot.Count == 5, "The real online star release bubbled into the row/detail route");
        Click(Part<MyIconButton>(liveRow, "BtnFavorite")); liveRow.Click -= rowClick;
        Part<MyTextBox>(page, "TextSearchName").Text = "Type64"; Click(Part<MyButton>(page, "BtnSearchRun")); Native.Check(Part<Panel>(page, "PanResults").Children.Count == 1, "Search binding failed");
        Part<MyTextBox>(page, "TextSearchName").Text = "missing"; Click(Part<MyButton>(page, "BtnSearchRun")); Native.Check(Part<Panel>(page, "PanResults").Children.OfType<TextBlock>().Any() && Part<TextBlock>(page, "LabStatus").Visibility == Visibility.Visible, "Empty search state missing");
        Part<MyTextBox>(page, "TextSearchName").Text = ""; Click(Part<MyButton>(page, "BtnSearchRun"));
        byte[] small = File.ReadAllBytes(s.File);
        var many = s.Store.Snapshot.Select(entry => new JObject { ["SavedAt"] = entry.SavedAt, ["Project"] = entry.ProjectJson }).ToList();
        many.AddRange(Enumerable.Range(0, 41).Select(i => new JObject { ["SavedAt"] = DateTime.UtcNow, ["Project"] = Project("Page" + i).ToJson() }));
        File.WriteAllText(s.File, new JObject { ["Schema"] = 1, ["Projects"] = new JArray(many) }.ToString()); page.Refresh(); WaitPage(page);
        Click(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemMod"));
        Part<MyTextBox>(page, "TextSearchName").Text = "Page"; SelectTag(Part<ComboBox>(page, "ComboSearchSource"), "2");
        SelectTag(Part<ComboBox>(page, "ComboSearchTag"), "fixture"); Part<ComboBox>(page, "TextSearchVersion").Text = "1.21.1";
        SelectTag(Part<ComboBox>(page, "ComboSearchLoader"), "4"); Click(Part<MyButton>(page, "BtnSearchRun"));
        Native.Check(Part<Panel>(page, "PanResults").Children.Count == 40 && Part<FrameworkElement>(page, "CardPages").Visibility == Visibility.Visible, "Large favorites did not use native paging");
        Click(Part<MyIconButton>(page, "BtnPageRight")); Native.Check(Part<Panel>(page, "PanResults").Children.Count == 1, "Next page lost combined metadata filters");
        var last = Part<Panel>(page, "PanResults").Children.Cast<FrameworkElement>().ToArray().Select(MyVirtualizingElement.TryInit).OfType<MyResourceItem>().Single();
        Click(Part<MyIconButton>(last, "BtnFavorite")); Native.Check(Part<Panel>(page, "PanResults").Children.Count == 40 && !Part<MyIconButton>(page, "BtnPageRight").IsEnabled, "Removing the last item on a filtered page did not clamp pagination");
        Click(Part<MyIconButton>(page, "BtnPageFirst")); Native.Check(Part<Panel>(page, "PanResults").Children.Count == 40, "First page binding failed");
        File.WriteAllBytes(s.File, small); page.Refresh(); WaitPage(page);
        Click(Part<MyButton>(page, "BtnSearchReset")); Click(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemInstall"));
        LastFavorite(s, page);
        Part<MyTextBox>(page, "TextSearchName").Text = "missing"; Click(Part<MyButton>(page, "BtnSearchRun"));
        foreach (bool dark in new[] { false, true }) {
            object previous = Application.Current.Resources["ColorBrushGray3"];
            try {
                var brush = new SolidColorBrush(dark ? Colors.LightGray : Colors.DarkSlateGray); Application.Current.Resources["ColorBrushGray3"] = brush;
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Native.Check(Part<TextBlock>(page, "LabStatus").Foreground == brush && title.ActualWidth > 30 && Part<MyTextBox>(page, "TextSearchName").ActualWidth > 60, "Minimum-window/dynamic-theme layout failed: " + Part<MyTextBox>(page, "TextSearchName").ActualWidth);
                Native.Check(ReferenceEquals(window.PageLeft, ModMain.FrmFavoritesLeft) && ModMain.FrmFavoritesLeft.ActualWidth > 100, "Favorites did not retain the native download sidebar");
            } finally { Application.Current.Resources["ColorBrushGray3"] = previous; }
        }
        Click(Part<MyButton>(page, "BtnSearchReset"));
        SaveProof(window);
        var hybrid = Project("Hybrid12", ResourceTypes.ModOrDataPack);
        Click(Part<MyIconButton>(hybrid.ToResourceItem(false, false).Init(), "BtnFavorite"));
        Click(Part<Panel>(ModMain.FrmFavoritesLeft, "PanItem").Children.OfType<MyListItem>().Single(button => button.Tag.Equals("4")));
        s.VersionCache[ResourceProjectKey.Get(hybrid.Platform, hybrid.Id!)] = new List<ResourceVersion>();
        OpenHybrid(); Native.Check((ResourceTypes)((object[])window.PageCurrent.Additional)[4] == ResourceTypes.DataPack, "Data-pack category lost the hybrid project's download context");
        DetailHeader(s); window.PageBack(); WaitPage(page);
        Native.Check(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemDataPack").Checked && (int)window.PageCurrentSub == 4, "Returning from details lost the cached sidebar category/highlight");
        Click(Part<Panel>(ModMain.FrmFavoritesLeft, "PanItem").Children.OfType<MyListItem>().First());
        OpenHybrid(); Native.Check((ResourceTypes)((object[])window.PageCurrent.Additional)[4] == ResourceTypes.Mod, "All-category hybrid project opened with an unusable combined download type");
        Click(Part<MyIconButton>(DetailHeader(s), "BtnFavorite")); window.PageBack(); WaitPage(page);
        window.PageChange(FormMain.PageType.Launch); window.PageChange(FormMain.PageType.Favorites);
        Native.Check(ReferenceEquals(page, window.PageRight) && title.Checked && window.PageStack.Count == 0, "Tag=10 lookup/cached page route failed");
        // Existing real detail loader consumes a fixture cached platform response.
        s.VersionCache[ResourceProjectKey.Get(ResourcePlatforms.Modrinth, "AbCd1234")] = new List<ResourceVersion>();
        Native.Call(typeof(MyResourceItem), "MyResourceItem_Click", s.Online, s.Online, EventArgs.Empty);
        Native.Check(window.PageCurrent.Page == FormMain.PageType.ResourceDetail && window.PageStack.Count == 1 && window.PageStack[0].Page == FormMain.PageType.Favorites, "Existing detail/back route failed");
        var header = DetailHeader(s);
        Native.Check(!header.CanInteraction && Part<MyIconButton>(header, "BtnFavorite").Visibility == Visibility.Visible, "Real detail header lost its star"); Click(Part<MyIconButton>(header, "BtnFavorite"));
        Native.Check(s.Store.Snapshot.Count == 5 && s.LocalStar.Logo == s.EmptyIcon, "Detail favorite did not synchronize local rows");
        window.PageBack(); Native.Check(ReferenceEquals(window.PageRight, page) && title.Checked && window.PageStack.Count == 0, "Back lost page/highlight"); WaitPage(page);
        Native.Cache("LivingModpackExported", true); Native.Cache("LivingModpackMode", true); Native.Static("PCL.ModModpackMode", "ApplyVisibility", window);
        Native.Check(title.Visibility == Visibility.Collapsed && (bool)Native.Static("PCL.ModModpackMode", "BlocksPage", FormMain.PageType.Favorites)!, "Pack mode exposes favorites");
        Native.Cache("LivingModpackMode", false); Native.Cache("LivingModpackExported", false); Native.Cache("UiHiddenPageFavorites", true); PageSetupUI.HiddenRefresh(); Native.Check(title.Visibility == Visibility.Collapsed, "Hidden policy missed favorites");
        Native.Cache("UiHiddenPageFavorites", false); PageSetupUI.HiddenRefresh(); Native.Check(title.Visibility == Visibility.Visible, "Favorites did not restore after hiding");
        byte[] original = File.ReadAllBytes(s.File); string icon = s.OtherStar.Logo;
        using (var held = new FileStream(s.File, FileMode.Open, FileAccess.Read, FileShare.Read)) Click(s.OtherStar);
        Native.Check(s.OtherStar.Logo == icon && File.ReadAllBytes(s.File).SequenceEqual(original), "Rejected atomic save changed native state/bytes");
        File.WriteAllText(s.File, "damaged favorites"); page.Refresh(); WaitPage(page);
        Native.Check(Part<FrameworkElement>(page, "HintError").Visibility == Visibility.Visible && File.ReadAllText(s.File) == "damaged favorites", "Damaged file was hidden/overwritten");
        File.WriteAllBytes(s.File, original); page.Refresh(); WaitPage(page);
        Native.Check(File.ReadAllBytes(s.Legacy).SequenceEqual(s.LegacyBytes), "Legacy filename favorites/other settings changed");
        Native.Check(((PageResource)page.FindName("Content")).Loader.State == ModBase.LoadState.Waiting, "A local favorite interaction started the online search loader");
        foreach (var pair in s.ModBytes) Native.Check(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "Favorite action changed a mod file");
        void OpenHybrid() {
            var row = Part<Panel>(page, "PanResults").Children.Cast<FrameworkElement>().ToArray().Select(MyVirtualizingElement.TryInit).OfType<MyResourceItem>()
                .Single(item => ((ResourceProject)item.Tag).Id == hybrid.Id);
            Native.Call(typeof(MyResourceItem), "MyResourceItem_Click", row, row, EventArgs.Empty);
        }
    }
    private static MyResourceItem DetailHeader(Scope s) {
        s.DetailLoader = (ModLoader.LoaderTask<int, List<ResourceVersion>>)Native.Get(ModMain.FrmDownloadResourceDetail, "ResourceFileLoader")!;
        Wait(() => s.DetailLoader.State == ModBase.LoadState.Finished && (s.DetailLoader.LastRunningThread is null || !s.DetailLoader.LastRunningThread.IsAlive));
        return (MyResourceItem)Native.Get(Native.Get(ModMain.FrmDownloadResourceDetail, "Controller")!, "ResourceItem")!;
    }
    private static void OnlineSearchBindings(Scope s, PageFavorites favorites) {
        var wrapper = new PageDownloadMod(); var online = (PageResource)wrapper.FindName("Content");
        object controller = Native.Get(online, "Controller")!, host = Native.Get(controller, "host")!;
        int initialized = 0, starts = 0; ResourceSearcher.SearchRequest? captured = null;
        Native.Set(host, "TargetInstance", new Func<ModMinecraft.McInstance?>(() => null));
        Native.Set(host, "AllDrops", new Func<List<int>?>(() => null));
        Native.Set(host, "InitializeLoader", new Action(() => initialized++));
        Native.Set(host, "Start", new Action(() => starts++));
        Native.Set(host, "ShouldStart", new Func<ResourceSearcher.SearchRequest, bool>(value => { captured = value; return true; }));
        Native.Set(host, "RunInThread", new Action<Action>(work => work()));
        Native.Set(host, "RunInUi", new Action<Action>(work => work())); Native.Set(host, "BackToTop", new Action(() => { }));
        Border body = Part<Border>(s.Window!, "PanMainRight"); var before = body.Child;
        try {
            body.Child = wrapper; Drain();
            Native.Check(initialized == 1 && online.Loader.State == ModBase.LoadState.Waiting && Native.Get(online, "FavoriteStore") is null, "The default download constructor no longer follows the original online initializer");
            Part<MyTextBox>(online, "TextSearchName").Text = "online fixture"; Part<ComboBox>(online, "TextSearchVersion").Text = "1.21.1";
            SelectTag(Part<ComboBox>(online, "ComboSearchSource"), "1"); SelectTag(Part<ComboBox>(online, "ComboSearchLoader"), "4");
            Part<ComboBox>(online, "ComboSearchTag").SelectedIndex = 1;
            Click(Part<MyButton>(online, "BtnSearchRun"));
            Native.Check(starts == 1 && captured is not null && captured.Type == ResourceTypes.Mod && captured.GameVersion == "1.21.1" && captured.Sources == ResourcePlatforms.CurseForge && captured.ModLoaders == ModLoaders.Fabric && captured.Tag != "", "Original online search bindings did not produce their native request");
            Enter(Part<MyTextBox>(online, "TextSearchName")); Native.Check(starts == 2, "Online Enter no longer reaches the original controller");
            Click(Part<MyIconButton>(online, "BtnPageRight")); Native.Check(starts == 3 && online.Page == 1, "Original online pagination did not use its existing host");
            Click(Part<MyButton>(online, "BtnSearchReset"));
            Native.Check(Part<MyTextBox>(online, "TextSearchName").Text == "" && Part<ComboBox>(online, "ComboSearchSource").SelectedIndex == 0 && Part<ComboBox>(online, "ComboSearchTag").SelectedIndex == 0 && Part<ComboBox>(online, "ComboSearchLoader").Visibility == Visibility.Collapsed, "Online reset behavior changed");
            Console.WriteLine("PASS shared PageResource default online mode: original initializer, native search request/Enter/reset/paging delegates; controlled host starts, no real HTTP.");
        } finally { body.Child = before; Drain(); WaitPage(favorites); }
    }
    private static void SharedSearchFilters(Scope s, PageFavorites page) {
        var cacheOnly = RichProject("Cache123", ResourcePlatforms.Modrinth, "Cached version fixture", "fixture", Array.Empty<string>(), ModLoaders.None);
        var knownFile = Version("CachedFileVersion"); knownFile.GameVersions = new List<string> { "1.18.2" }; knownFile.ModLoaders = ModLoaders.Forge;
        s.VersionCache[ResourceProjectKey.Get(cacheOnly.Platform, cacheOnly.Id!)] = new List<ResourceVersion> { knownFile };
        var cacheRow = cacheOnly.ToResourceItem(true, true).Init(); Click(Part<MyIconButton>(cacheRow, "BtnFavorite"));
        var cached = s.Store.Snapshot.Single(entry => entry.Identity.ProjectId == "Cache123");
        Native.Check(cached.KnownGameVersions.SequenceEqual(new[] { "1.18.2" }) && cached.ModLoaders == ModLoaders.Forge, "Already-cached catalog versions were ignored or treated as favorite file identities");
        Click(Part<MyIconButton>(cacheRow, "BtnFavorite"));
        var rich = new[] {
            RichProject("701", ResourcePlatforms.CurseForge, "Needle Forge", "科技", new[] { "1.21.1" }, ModLoaders.Forge),
            RichProject("QwEr1234", ResourcePlatforms.Modrinth, "Needle Fabric", "优化", new[] { "1.21.11" }, ModLoaders.Fabric),
            RichProject("UnKn1234", ResourcePlatforms.Modrinth, "Needle Unknown", "科技", Array.Empty<string>(), ModLoaders.None)
        };
        foreach (var project in rich) Click(Part<MyIconButton>(project.ToResourceItem(true, true).Init(), "BtnFavorite"));
        Click(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemMod"));
        Native.Check(((PageResource)page.FindName("Content")).PageType == ResourceTypes.Mod, "The local resource view lost its selected project category");
        var name = Part<MyTextBox>(page, "TextSearchName"); var source = Part<ComboBox>(page, "ComboSearchSource");
        var tag = Part<ComboBox>(page, "ComboSearchTag"); var version = Part<ComboBox>(page, "TextSearchVersion"); var loader = Part<ComboBox>(page, "ComboSearchLoader");
        Native.Check(tag.Items.Cast<ComboBoxItem>().Any(item => item.Content.Equals("科技")) && version.Items.Cast<ComboBoxItem>().Any(item => item.Content.Equals("1.21.11")), "Shared choices were not built from this category's cached metadata");
        name.Text = "Needle"; SelectTag(source, "1"); SelectTag(tag, "科技"); version.Text = "1.21.1"; SelectTag(loader, "1");
        Native.Check(loader.Visibility == Visibility.Visible, "Mod/version did not reveal the original loader control");
        Click(Part<MyButton>(page, "BtnSearchRun")); ExpectProject(page, "701");
        name.Text = "not applied"; ExpectProject(page, "701"); Enter(name);
        Native.Check(Part<Panel>(page, "PanResults").Children.OfType<TextBlock>().Any(), "Enter did not apply the local name filter");
        Click(Part<MyButton>(page, "BtnSearchReset"));
        Native.Check(name.Text == "" && source.SelectedIndex == 0 && tag.SelectedIndex == 0 && version.SelectedIndex == 0 && loader.SelectedIndex == 0 && loader.Visibility == Visibility.Collapsed && Part<Panel>(page, "PanResults").Children.Count == 4, "Local reset did not restore defaults and the selected category");
        name.Text = "Needle"; SelectTag(source, "2"); SelectTag(tag, "优化"); version.Text = "1.21.11"; SelectTag(loader, "4");
        Click(Part<MyButton>(page, "BtnSearchRun")); ExpectProject(page, "QwEr1234");
        version.Text = "1.21.1"; Enter(version);
        Native.Check(Part<Panel>(page, "PanResults").Children.OfType<TextBlock>().Any(), "A different patch in the same Drop family falsely matched");
        SelectTag(tag, "科技"); SelectTag(loader, "0"); Click(Part<MyButton>(page, "BtnSearchRun"));
        Native.Check(Part<Panel>(page, "PanResults").Children.OfType<TextBlock>().Any(), "A Drop-only project was treated as exact compatibility");
        version.SelectedIndex = 0; version.Text = "全部 (也可自行输入)"; Click(Part<MyButton>(page, "BtnSearchRun")); ExpectProject(page, "UnKn1234");
        Click(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemResourcePack"));
        Native.Check(loader.Visibility == Visibility.Collapsed && Part<Panel>(page, "PanResults").Children.OfType<TextBlock>().Any(), "Category switching discarded the name/source filter or showed Mod loaders for packs");
        Click(Part<MyButton>(page, "BtnSearchReset")); Native.Check(Part<Panel>(page, "PanResults").Children.Count == 1, "Reset lost resource-pack category browsing");
        Click(Part<MyListItem>(ModMain.FrmFavoritesLeft, "ItemInstall"));
        foreach (var project in rich) Click(Part<MyIconButton>(project.ToResourceItem(true, true).Init(), "BtnFavorite"));
        Native.Check(s.Store.Snapshot.Count == 6, "Metadata filters changed project identity or retained fixture favorites");
        Console.WriteLine("PASS shared favorites search card: actual buttons and name/version Enter, reset, CF/MR/tag/exact-patch/loader combinations, unknown compatibility exclusion and category changes.");
    }
    private static void LastFavorite(Scope s, PageFavorites page) {
        byte[] original = File.ReadAllBytes(s.File); var entry = s.Store.Snapshot.First();
        File.WriteAllText(s.File, new JObject { ["Schema"] = 1, ["Projects"] = new JArray(new JObject { ["SavedAt"] = entry.SavedAt, ["Project"] = entry.ProjectJson }) }.ToString());
        page.Refresh(); WaitPage(page);
        var row = (MyResourceItem)MyVirtualizingElement.TryInit(Part<Panel>(page, "PanResults").Children.Cast<FrameworkElement>().Single());
        Click(Part<MyIconButton>(row, "BtnFavorite"));
        Native.Check(s.Store.Snapshot.Count == 0 && Part<TextBlock>(page, "LabStatus").Text.Contains("还没有收藏") && Part<FrameworkElement>(page, "CardPages").Visibility == Visibility.Collapsed, "Removing the last favorite did not use the shared result-card empty state");
        File.WriteAllBytes(s.File, original); page.Refresh(); WaitPage(page);
    }
    private static ResourceProject RichProject(string id, ResourcePlatforms platform, string name, string tag, string[] versions, ModLoaders loader) {
        JObject json = Project(id, platform: platform).ToJson(); json["RawName"] = name; json["Tags"] = new JArray(tag);
        json["KnownGameVersions"] = new JArray(versions); json["ModLoaders"] = (int)loader; return new ResourceProject(json);
    }
    private static void SelectTag(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture) == tag);
    private static void ExpectProject(PageFavorites page, string id) {
        var rows = Part<Panel>(page, "PanResults").Children.Cast<FrameworkElement>().ToArray().Select(MyVirtualizingElement.TryInit).OfType<MyResourceItem>().ToArray();
        Native.Check(rows.Length == 1 && ((ResourceProject)rows[0].Tag).Id == id, "Shared search returned the wrong project: " + id);
    }
    private static void Enter(UIElement element) {
        var source = PresentationSource.FromVisual(element) ?? throw new InvalidOperationException("Search input is not in the hosted native window");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent }); Drain();
    }
    private sealed class Scope : IDisposable {
        private readonly Dictionary<string, object?> globals;
        private readonly ModMinecraft.McInstance previousInstance = PageInstanceLeft.Instance;
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
        internal FormMain? Window; internal PageFavorites? Favorites;
        internal ModLoader.LoaderTask<int, List<ResourceVersion>>? DetailLoader;
        internal MyIconButton LocalStar = null!, OtherStar = null!; internal MyResourceItem Online = null!;
        internal string EmptyIcon = "", Legacy = ""; internal byte[] LegacyBytes = Array.Empty<byte>();
        internal Scope(FixtureEnvironment environment) {
            Root = Path.Combine(environment.Root, "resource-favorites"); Directory.CreateDirectory(Root);
            File = Path.Combine(Paths.Base, "PCL", "ResourceFavorites.json");
            Native.Check(Native.Full(File).StartsWith(Native.Full(environment.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(File), "Refusing non-fixture/pre-existing favorite data");
            Store = (ResourceFavoriteStore)Native.Property(Native.Type("PCL.ResourceFavorites"), "Store").GetValue(null)!;
            Native.Check(Store.Snapshot.Count == 0, "Refusing pre-existing cached favorites");
            globals = new[] { "FrmMain", "FrmFavorites", "FrmFavoritesLeft", "FrmInstanceMod", "FrmDownloadResourceDetail" }.ToDictionary(name => name, name => Native.Field(typeof(ModMain), name).GetValue(null));
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
            if (ModMain.FrmFavorites is { } favoritePage && !ReferenceEquals(favoritePage, globals["FrmFavorites"])) {
                WaitPage(favoritePage);
                var loader = ((PageResource)favoritePage.FindName("Content")).Loader;
                if (loader.State == ModBase.LoadState.Loading) loader.Cancel();
                Wait(() => loader.LastRunningThread is null || !loader.LastRunningThread.IsAlive);
            }
            if (DetailLoader is null && ModMain.FrmDownloadResourceDetail is { } detailPage && !ReferenceEquals(detailPage, globals["FrmDownloadResourceDetail"]))
                DetailLoader = (ModLoader.LoaderTask<int, List<ResourceVersion>>)Native.Get(detailPage, "ResourceFileLoader")!;
            if (DetailLoader is not null) { if (DetailLoader.State == ModBase.LoadState.Loading) DetailLoader.Cancel(); Wait(() => DetailLoader.LastRunningThread is null || !DetailLoader.LastRunningThread.IsAlive); }
            Window ??= Application.Current.Windows.OfType<FormMain>().SingleOrDefault(window => !ReferenceEquals(window, globals["FrmMain"]));
            if (Window is not null) {
                Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, Native.Method(typeof(FormMain), "FormMain_Loaded"));
                Window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), Window, Native.Method(typeof(FormMain), "FormMain_Closing"));
                Window.Close();
            }
            foreach (FrameworkElement row in observed) row.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Drain();
            Native.Check(Window is null || !Window.IsVisible, "Hosted FormMain did not close");
            Native.Check(requests == 0, "Favorites unexpectedly requested a platform endpoint"); Native.Set(networkHost, "Request", request);
            Native.Set(searchHost, "Request", searchRequest);
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File); Store.Reload();
            VersionCache.Clear(); foreach (DictionaryEntry pair in versions) VersionCache[pair.Key] = pair.Value;
            projectCache.Clear(); foreach (DictionaryEntry pair in projects) projectCache[pair.Key] = pair.Value;
            foreach (var pair in globals) Native.Field(typeof(ModMain), pair.Key).SetValue(null, pair.Value);
            PageInstanceLeft.Instance = previousInstance; foreach (var pair in settings) Native.Cache(pair.Key, pair.Value);
            ModAnimation.AniGroups = groups; ModAnimation.AniControlEnabled = suppression;
        }
    }
    private static FormMain CreateMain() {
        var left = new MyPageLeft(); var right = new MyPageRight { Child = new StackPanel() };
        Action<MainHost> configure = host => {
            host.CreateLaunchLeft = () => { }; host.CreateLaunchRight = () => { };
            var originalLeft = host.LeftPage; var originalRight = host.RightPage;
            host.LeftPage = page => page == 0 ? new LegacyMainLeftPage { Element = left } : originalLeft(page); host.RightPage = page => page == 0 ? right : originalRight(page);
            host.ReadInteger = key => key == "SystemLastVersionReg" ? ModBase.VersionCode : Settings.Get<int>(key);
            host.HasSaved = _ => true; host.ThemeRefresh = _ => { }; host.StartFolderLoader = () => { }; host.IsAdmin = () => false; host.UiPosted = (work, _) => work();
            host.Animations.Code = (work, _, _) => { work(); return default; }; host.Animations.Start = (_, _, _) => { }; host.Animations.Stop = _ => { };
            host.Animations.Opacity = (_, _, _, _, _, _) => default; host.Animations.X = (_, _, _, _, _, _) => default;
            host.Animations.Width = (_, _, _, _, _, _) => default; host.Animations.Double = (_, _, _, _, _, _) => default;
        };
        return (FormMain)typeof(FormMain).GetConstructor(Native.Flags, null, new[] { typeof(Action<MainHost>) }, null)!.Invoke(new object[] { configure });
    }
    private static void SaveProof(FormMain window) {
        string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args, "--favorites-proof");
        if (option < 0) return;
        string directory = Path.GetFullPath(args[option + 1]); Directory.CreateDirectory(directory);
        ResourceDictionary original = Application.Current.Resources;
        var cloned = new ResourceDictionary();
        foreach (DictionaryEntry entry in original) cloned[entry.Key] = entry.Value is Freezable freezable ? freezable.CloneCurrentValue() : entry.Value;
        foreach (ResourceDictionary merged in original.MergedDictionaries) cloned.MergedDictionaries.Add(merged);
        var colors = Native.Type("PCL.ModSecret").GetFields(Native.Flags).Where(field => field.IsStatic && !field.IsInitOnly && !field.IsLiteral && field.Name.StartsWith("Color", StringComparison.Ordinal)).ToDictionary(field => field, field => field.GetValue(null));
        int mode = Settings.Get<int>("UiLauncherColorMode"); bool darkBefore = LegacyThemePalette.IsDarkTheme;
        var lastTick = Native.Field(typeof(ModAnimation), "AniLastTick"); object? previousTick = lastTick.GetValue(null);
        double width = window.Width, height = window.Height;
        Application.Current.Resources = cloned;
        try {
            foreach (bool dark in new[] { false, true }) {
                Native.Cache("UiLauncherColorMode", dark ? 1 : 0); Native.Static("PCL.ModSecret", "ThemeRefresh", -1);
                foreach (bool minimum in new[] { false, true }) {
                    window.Width = minimum ? window.MinWidth : Settings.Get<int>("WindowWidth"); window.Height = minimum ? window.MinHeight : Settings.Get<int>("WindowHeight");
                    window.UpdateLayout(); Drain();
                    for (int tick = 0; tick < 8; tick++) { lastTick.SetValue(null, long.MaxValue); ModAnimation.AniTimer(10000); Drain(); }
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                    string path = Path.Combine(directory, "favorites-" + (dark ? "dark" : "light") + "-" + (minimum ? "minimum" : "default") + ".png");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
                    Console.WriteLine("Saved hosted PageFavorites proof: " + path);
                }
            }
        } finally {
            Native.Cache("UiLauncherColorMode", mode); Native.Static("PCL.ModTheme", "PrepareThemeTones", darkBefore);
            foreach (var pair in colors) pair.Key.SetValue(null, pair.Value);
            Application.Current.Resources = original; window.Width = width; window.Height = height; window.UpdateLayout();
            Native.Static("PCL.ModTheme", "NotifyThemeChanged");
            lastTick.SetValue(null, previousTick);
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
        if (root is PageFavorites favorites) {
            root = (PageResource)favorites.FindName("Content");
            if (name == "PanResults") name = "PanProjects";
            if (name == "LabStatus") return Native.Get(root, "FavoriteStatus") as T ?? throw new InvalidOperationException("Missing local status");
        }
        return root.FindName(name) as T ?? throw new InvalidOperationException("Missing native part " + name);
    }
    private static void SendClick(FrameworkElement element) {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = element is MyListItem ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.MouseLeftButtonDownEvent });
        // This window is deliberately offscreen: retain the real native PreviewMouseUp,
        // Click and Check bindings while supplying only its predecessor press flag.
        if (element is MyListItem) Native.Field(typeof(MyListItemView), "isMouseDown").SetValue(element, true);
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = element is MyListItem ? UIElement.PreviewMouseLeftButtonUpEvent : UIElement.MouseLeftButtonUpEvent });
    }
    private static void Click(FrameworkElement element) { SendClick(element); Wait(() => Pending == 0); }
    private static void WaitPage(PageFavorites page) => Wait(() => !(bool)Native.Get(page, "reading")!);
    private static void Drain() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition) { var time = Stopwatch.StartNew(); while (!condition()) { if (time.ElapsedMilliseconds > 5000) throw new TimeoutException("Favorite fixture callbacks did not settle"); Drain(); Thread.Sleep(5); } Drain(); }
}
