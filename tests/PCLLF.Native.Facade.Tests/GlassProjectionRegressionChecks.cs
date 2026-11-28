// SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Wpf.Glass;
using ExportController = PCLCS.ModpackExportPageController<PCL.ModMinecraft.McInstance, PCL.ExportOption, System.Windows.Controls.CheckBox, PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.ModLoader.LoaderCombo<string>>;
using ExportHost = PCLCS.ModpackExportPageHost<PCL.ModMinecraft.McInstance, PCL.ExportOption, PCL.ModLoader.LoaderCombo<string>>;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassProjectionRegressionChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        SettingsCardInsets();
        KeybindingColumns(environment.Root);
        ExportOptionCaptions(environment.Root);
        FloatingSelectionDockLayout();
        ModRowStateMarkers(environment.Root);
        environment.AssertNoForbiddenIo();
    }

    private static void ModRowStateMarkers(string root)
    {
        var previous = LauncherInstanceContext.Instance;
        try {
            var instance = BackupResourceChecks.Instance(System.IO.Path.Combine(root, "mod-row-style"));
            LauncherInstanceContext.Instance = instance;
            ((IDictionary)Native.Get(Native.Entries["VersionAdvanceDisableModUpdate"]!, "ValueCache")!)[instance.PathVersion] = false;
            var entry = new LocalResourceFile(System.IO.Path.Combine(root, "example.jar.disabled")) {
                ProjectVersion = new ResourceVersion { FileName = "example.jar", Version = "1.0", ReleaseDate = DateTime.Today.AddDays(-5) },
                UpdateFile = new ResourceVersion { FileName = "new.jar", Version = "1.1", ReleaseDate = DateTime.Today }
            };
            var row = new PageInstanceMod().CreateItem(entry, false);
            row.Refresh();
            row.Measure(new Size(600, 120)); row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height)); row.UpdateLayout();
            var title = (TextBlock)Native.Get(row, "_title")!;
            var update = (Button)Native.Get(row, "_update")!;
            var badge = (Border)Native.Get(row, "_disabledBadge")!;
            Native.Check(title.TextDecorations == TextDecorations.Strikethrough && badge is { Visibility: Visibility.Visible, Width: 20, Height: 20, Child: Border } &&
                update.Visibility == Visibility.Visible && update.Width == 21 && update.Height == 21 && update.Opacity == .4 &&
                update.TranslatePoint(new Point(), row).X >= title.TranslatePoint(new Point(title.ActualWidth, 0), row).X - 2.1,
                "The disabled title or trailing PCL update action lost its style.");
            entry.UpdateFile = null;
            row.Refresh();
            Native.Check(update.Visibility == Visibility.Collapsed, "The update action remained after clearing the available update.");
        } finally { LauncherInstanceContext.Instance = previous; }
    }

    internal static void ReportedRegressions(FixtureEnvironment environment)
    {
        GlassPresentationChecks.DirectResourceSearch();
        ModifyMinecraftControl();
        ResourceBackdrop();
        ProgressDecimals();
        environment.AssertNoForbiddenIo();
    }

    private static void ResourcePager(FormMain owner)
    {
        const int size = 15;
        Native.Check(LauncherResourceBrowser.PageSize == size, "The default online page size changed.");
        var category = ResourceBrowserCatalog.Categories[0];
        using var page = new GlassResourceBrowserPage(owner, category);
        page.Open(category, null, loadData: false);
        var search = (LauncherResourceBrowser)Native.Get(page, "_search")!;
        var previous = (Button)Native.Get(page, "_previous")!;
        var next = (Button)Native.Get(page, "_next")!;
        var engine = Native.Field(typeof(ResourceSearcher), "Engine").GetValue(null)!;
        var host = (ResourceSearchHost<ResourceProject>)Native.Get(engine, "host")!;
        var original = host.Request;
        using var secondRequested = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var workers = new System.Collections.Concurrent.ConcurrentBag<Thread>();
        host.Request = url =>
        {
            workers.Add(Thread.CurrentThread);
            Native.Check(url.StartsWith("https://api.modrinth.com/v2/search?", StringComparison.Ordinal), "Unexpected fixture provider.");
            bool second = url.Contains("&offset=15&", StringComparison.Ordinal);
            if (second)
            {
                secondRequested.Set();
                if (!releaseSecond.Wait(8000)) throw new TimeoutException("The fixture held the second page too long.");
            }
            return new JObject { ["total_hits"] = size + 1, ["hits"] = new JArray(Enumerable.Range(second ? size : 0, second ? 1 : size)
                .Select(index => new JObject { ["project_id"] = "PagerFixture" + index, ["slug"] = "pager-fixture-" + index,
                    ["title"] = "Pager fixture " + index, ["description"] = "Offline fixture", ["project_type"] = "mod",
                    ["date_modified"] = new DateTime(2025, 1, 1), ["downloads"] = 20, ["categories"] = new JArray() })) };
        };
        try
        {
            search.Start(new("", null, "", ModLoaders.None, ResourcePlatforms.Modrinth), size);
            VisualAssetChecks.Pump(() => !search.Snapshot.Loading, "the actual pager finishes page one");
            Native.Check(search.Snapshot.Results.Count == size && next.IsEnabled && !previous.IsEnabled,
                "The real pager did not enable Next for a full first page with remaining results.");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            VisualAssetChecks.Pump(() => secondRequested.IsSet, "the actual pager starts page two");
            Native.Check(search.Snapshot.Page == 1 && search.Snapshot.Loading && previous.IsEnabled && !next.IsEnabled,
                "The real pager disabled Previous or enabled Next while an underfilled second page was loading.");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            VisualAssetChecks.Pump(() => !search.Snapshot.Loading, "the actual pager returns to the cached first page");
            Native.Check(search.Snapshot.Page == 0 && search.Snapshot.Results.Count == size && workers.Count == 2 && next.IsEnabled && !previous.IsEnabled,
                "The real pager did not reuse the cached first page after the Previous click.");
        }
        finally
        {
            releaseSecond.Set();
            try { foreach (Thread worker in workers) Native.Check(worker.Join(8000), "A fixture provider did not exit."); }
            finally { host.Request = original; }
        }
    }

    private static void ResourceBackdrop()
    {
        var owner = FileTaskShell.Install();
        foreach (var category in new ResourceBrowserCategory?[] { ResourceBrowserCatalog.Categories[0], null })
        {
            using var page = new GlassResourceBrowserPage(owner, category);
            var plate = Tree(page).OfType<Grid>().Single(element => element.Name == "ResourceResultsPlate");
            Native.Check(plate.Children.Count == 2 && plate.Children[0] is GlassSurface material &&
                material.Child is null && !material.IsHitTestVisible && material.BorderThickness == new Thickness(0) &&
                material.Effect is System.Windows.Media.Effects.BlurEffect { Radius: 14, KernelType: System.Windows.Media.Effects.KernelType.Gaussian } &&
                plate.Children[1] is ScrollEdgeFade foreground && foreground.Effect is null && Tree(foreground).Contains(page.Scroll),
                "Resource browsing must soften the background alone and keep the scrolling results on a separate interactive layer.");
        }
        ResourcePager(owner);
    }

    private static void ModifyMinecraftControl()
    {
        var page = new PageDownloadModify();
        var pending = (ModLoader.LoaderBase)Native.Get(page, "InitLoader")!;
        Native.Check(pending.State == ModBase.LoadState.Waiting, "Constructing Modify activated its installed-components loader.");
        var service = Native.Property(typeof(PageDownloadModify), "ModifyService").GetValue(page)!;
        object host = Native.Get(service, "host")!;
        int fullPageCalls = 0, reloads = 0;
        string? requestedForge = null;
        Native.Set(host, "ModpackEnabled", (Func<bool>)(() => false));
        Native.Set(host, "CanChangeMinecraft", (Func<string?, string?, bool>)((_, _) => true));
        Native.Set(host, "BaseShowMinecraftList", (Action)(() => fullPageCalls++));
        Native.Set(host, "ComponentError", (Func<DownloadInstallComponent, string?>)(_ => null));

        static JObject Entry(string id) => new() {
            ["id"] = id, ["url"] = $"https://example.invalid/{id}.json",
            ["type"] = "release", ["releaseTime"] = "2025-01-02T00:00:00Z"
        };
        var original = Entry("1.21.11"); var target = Entry("1.20.1");

        object installHost = GlassPresentationChecks.InstallHost(page);
        GlassPresentationChecks.FreezeInstallFeeds(installHost);
        Native.Set(installHost, "StartForge", (Action<string>)(value => requestedForge = value));
        Native.Set(installHost, "ReloadSelected", (Action)(() => {
            reloads++;
            Native.Call(service.GetType(), "RefreshComparisons", service);
        }));
        var comparison = (Func<int, DownloadModifyComparison>)Native.Get(host, "Comparison")!;
        comparison(-1).Restore.Tag = original;
        comparison((int)DownloadInstallComponent.Fabric).Restore.Tag = "0.18.0";
        Native.Set(host, "FabricOutput", (Func<JObject>)(() => new JObject {
            ["game"] = new JArray(new JObject { ["version"] = "1.21.11" }, new JObject { ["version"] = "1.20.1" }),
            ["loader"] = new JArray(new JObject { ["version"] = "0.18.0" })
        }));

        var options = (FrameworkElement)page.FindName("PanSelect");
        var catalog = (FrameworkElement)page.FindName("PanMinecraft");
        var change = (Grid)page.FindName("PanMinecraftChange");
        var content = (FrameworkElement)page.Child;
        page.Child = null;
        content.Resources.MergedDictionaries.Add(page.Resources);
        NameScope.SetNameScope(content, NameScope.GetNameScope(page));
        var window = Host(content);
        try {
            page.IsInSelectPage = true;
            page.VanillaName = (string)original["id"]!;
            page.VanillaData = original;
            page.SelectedFabric = "0.18.0";
            page.MinecraftVersions = new JArray(original, target);
            Native.Property(typeof(PageDownloadModify), "InitialTarget").SetValue(page, page.GetSelectedComponents());
            options.Visibility = Visibility.Visible;
            options.IsHitTestVisible = true;
            options.Opacity = 1;
            options.RenderTransform = new TranslateTransform();
            catalog.Visibility = Visibility.Collapsed;
            ((FrameworkElement)page.FindName("PanAllBack")).Visibility = Visibility.Visible;
            ((FrameworkElement)page.FindName("PanLoad")).Visibility = Visibility.Collapsed;
            var card = (Border)page.FindName("CardMinecraft");
            var label = (TextBlock)page.FindName("LabMinecraftVersion");
            label.Text = "1.21.11";
            var expander = (Expander)page.FindName("MinecraftVersionPicker");
            Native.Check(Tree(card).OfType<TextBlock>().Any(item => item.Text == "1.21.11"),
                "The native Minecraft card omitted the selected version.");
            expander.IsExpanded = true;
            var list = (ListBox)page.FindName("MinecraftVersionOptions");
            Native.Check(list.Items.Count == 2 && fullPageCalls == 0 && catalog.Visibility == Visibility.Collapsed && options.Visibility == Visibility.Visible,
                "Expanding Minecraft replaced the modification screen with the full-page catalog.");
            var search = (TextBox)page.FindName("MinecraftVersionSearch");
            search.Text = "1.20";
            Native.Check(list.Items.Count == 1 && page.VanillaName == "1.21.11" && reloads == 0, "Filtering the inline list altered the selected version.");
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var choice = Visuals(list).OfType<Button>().Single(item => Equals(item.Content, "1.20.1"));
            choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, choice));
            Native.Check(page.VanillaName == "1.20.1" && ReferenceEquals(page.VanillaData, target) &&
                requestedForge == "1.20.1" && reloads == 1 && fullPageCalls == 0 && !expander.IsExpanded &&
                page.IsInSelectPage && options.Visibility == Visibility.Visible && catalog.Visibility == Visibility.Collapsed,
                "Choosing the native inline version did not update its catalog and selection in place.");

            var restore = (Button)comparison(-1).Restore;
            Native.Check(restore.Visibility == Visibility.Visible, "A changed Minecraft selection had no native restore command.");
            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, restore));
            Native.Check(page.VanillaName == "1.21.11" && page.SelectedFabric == "0.18.0" && fullPageCalls == 0 &&
                restore.Visibility == Visibility.Collapsed,
                "Restoring Minecraft did not recover the installed component selection.");

            Native.Set(host, "ModpackEnabled", (Func<bool>)(() => true));
            Native.Call(typeof(PageDownloadModify), "RefreshModpackRestrictions", page);
            Native.Check(change.Visibility == Visibility.Collapsed && !card.IsHitTestVisible &&
                !(bool)Native.Call(typeof(PageDownloadModify), "ChooseMinecraftInPlace", page, target)!,
                "A restricted modpack exposed Minecraft version changes.");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void ProgressDecimals()
    {
        var type = Native.Type("PCL.GlassTideProgress");
        foreach (bool poster in new[] { false, true })
        {
            var tide = (FrameworkElement)Activator.CreateInstance(type, Native.Flags, null, [11d, null], null)!;
            Native.Property(type, "PosterFigure").SetValue(tide, poster);
            foreach ((double value, string expected) in new[] { (.375, "37.50"), (.5715, "57.15"), (.999999, "99.99"), (1d, "100.00") })
            {
                Native.Call(type, "Update", tide, true, value, "进度");
                Native.Check(AutomationProperties.GetName(tide).EndsWith(expected + "%", StringComparison.Ordinal) &&
                    (!poster || Equals(Native.Get(tide, "_posterText"), expected)), "The shared progress surface lost its two decimal digits.");
            }
        }
    }

    private static void SettingsCardInsets()
    {
        var overview = new PageInstanceOverall();
        var adoption = (Button)overview.FindName("BtnMmcLegacyAdoption");
        var personal = new PageSetupPersonal();
        foreach (string name in new[] { "BtnImport", "BtnSaveGlobal" })
            ((UIElement)personal.FindName(name)).Visibility = Visibility.Collapsed;
        var scope = (TextBlock)personal.FindName("LabEditScope");
        scope.Text = "跟随全局时只读。选择不同步后可编辑本版本。";
        scope.Visibility = Visibility.Visible;
        ((TextBlock)personal.FindName("LabSummary")).Text = "已选 41 项 · 方案有值 41 项 · 本版本可用 41 项";
        var updated = (TextBlock)personal.FindName("LabUpdated");
        updated.Text = "更新于 2026-11-17 17:58";
        updated.Visibility = Visibility.Visible;

        static void Layout(FrameworkElement page, double width)
        {
            var size = new Size(width, 840);
            page.Measure(size);
            page.Arrange(new Rect(size));
            page.UpdateLayout();
        }
        static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        static Border Frame(FrameworkElement page, string name)
        {
            var card = (Border)page.FindName(name);
            var content = (FrameworkElement)card.Child;
            Rect bounds = Bounds(content, card);
            double bottom = card.ActualHeight - bounds.Bottom;
            Native.Check(Math.Abs(bottom - bounds.Top) <= 1.1 && bounds.Top >= 12,
                $"Uneven insets for {name}: top={bounds.Top:0.##}, bottom={bottom:0.##}.");
            return card;
        }

        foreach (int width in new[] { 820, 480 }) {
            foreach (Visibility visible in new[] { Visibility.Visible, Visibility.Collapsed }) {
                adoption.Visibility = visible;
                Layout(overview, width);
                Frame(overview, "PanFolder");
                Frame(overview, "PanManage");
            }
            Layout(personal, width);
            var surface = Frame(personal, "CardSearch");
            var search = (TextBox)personal.FindName("TextSearch");
            Rect field = Bounds(search, surface);
            double remaining = surface.ActualWidth - field.Right;
            Native.Check(remaining >= 14 && Math.Abs(field.Left - remaining) <= 1.1,
                $"The personal settings search is clipped at width {width}: left={field.Left:0.##}, right={remaining:0.##}.");
            Rect At(string name) => Bounds((FrameworkElement)personal.FindName(name), personal);
            Rect save = At("BtnApply"), refresh = At("BtnRefresh"), policy = At("ComboPolicy"), note = At("LabEditScope");
            Native.Check(Math.Abs(save.Top - refresh.Top) <= 1 && refresh.Left - save.Right is >= 5 and <= 10 &&
                note.Top >= policy.Bottom - 1 && note.Bottom <= save.Top + 1,
                $"Personal settings actions or policy help are out of alignment at width {width}.");
            Native.Check(At("LabSummary").Right <= personal.ActualWidth + 1 && At("LabUpdated").Right <= personal.ActualWidth + 1,
                $"The personal settings summary overflows at width {width}.");
        }
    }

    private static void KeybindingColumns(string fixtureRoot)
    {
        var page = new PageInstanceKeybindings();
        ((FrameworkElement)page.FindName("PanBack")).Visibility = Visibility.Visible;
        var entriesProperty = Native.Property(typeof(PageInstanceKeybindings), "Entries");
        var entries = (IList)Activator.CreateInstance(entriesProperty.PropertyType)!;
        Type bindingType = entriesProperty.PropertyType.GetGenericArguments()[0];
        foreach (var (key, name, owner, physicalKey, count) in new[]
        {
            ("key_key.left", "向左", "Minecraft", "a", 2),
            ("key_debug.example", "调试按键", "调试模组", "a", 2),
            ("key_key.jump", "跳跃", "Minecraft", "space", 1)
        })
        {
            var entry = (InstanceKeybindingData)Activator.CreateInstance(bindingType, true)!;
            entry.Key = key;
            entry.Name = name;
            entry.Owners = owner;
            entry.Value = entry.Normalized = "key.keyboard." + physicalKey;
            entry.Saved = true;
            entry.SharedCount = count;
            entries.Add(entry);
        }
        entriesProperty.SetValue(page, entries);
        object controller = Native.Get(page, "Controller")!;
        object host = Native.Get(controller, "host")!;
        Native.Set(controller, "Owner", new ModMinecraft.McInstance(Path.Combine(fixtureRoot, "keybinding-layout-fixture")));
        Native.Set(host, "PersonalBindingAnnotations", (Func<ModMinecraft.McInstance, IReadOnlyDictionary<string, KeybindingPersonalState>>)(_ =>
            new Dictionary<string, KeybindingPersonalState> { ["key_key.left"] = new("Q", true), ["key_key.jump"] = new(null, false) }));
        Native.Set(host, "HasRunningGame", (Func<bool>)(() => false));
        Native.Set(host, "IsLocked", (Func<ModMinecraft.McInstance, bool>)(_ => false));
        Native.Call(typeof(PageInstanceKeybindings), "RenderList", page);

        var list = (StackPanel)page.FindName("PanBindings");
        ((Panel)list.Parent).Children.Remove(list);
        list.Resources.MergedDictionaries.Add(page.Resources);
        Window? window = null;
        try
        {
            window = Host(list);
            Button KeyButton(object entry) => Visuals(list).OfType<Button>().Single(button => ReferenceEquals(button.Tag, entry));
            void Layout()
            {
                window.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
            }
            static Point Origin(FrameworkElement control, FrameworkElement root) => control.TransformToAncestor(root).Transform(new Point());
            foreach (int width in new[] { 820, 420 })
            {
                window.Width = width;
                ((RadioButton)page.FindName("BtnFilterAll")).IsChecked = true;
                Layout();
                Button normal = KeyButton(entries[0]!);
                var columns = (Grid)normal.Parent;
                var profile = columns.Children.OfType<TextBlock>().Single(text => text.Text == "Q");
                var sync = columns.Children.OfType<Border>().Single(badge => badge.Child is TextBlock { Text: "参与" });
                var names = columns.Children.OfType<StackPanel>().Single();
                double rowHeight = ((Border)columns.Parent).ActualHeight;
                double buttonX = Origin(normal, list).X;
                Native.Check(rowHeight >= 40 && Origin(names, columns).X + names.ActualWidth <= Origin(profile, columns).X + 1 &&
                    Origin(profile, columns).X + profile.ActualWidth <= Origin(sync, columns).X + 1 &&
                    Origin(sync, columns).X + sync.ActualWidth <= Origin(normal, columns).X + 1 &&
                    !Visuals(names).OfType<TextBlock>().Any(t => t.Text.Contains("个人方案", StringComparison.Ordinal)),
                    $"Personal key and sync status overlap or fall below the keybinding name at width {width}.");
                Native.Check(((Grid)KeyButton(entries[2]!).Parent).Children.OfType<TextBlock>().Any(text => text.Text == "未保存"),
                    "An unset personal binding was confused with an enabled sync state.");

                ((RadioButton)page.FindName("BtnFilterConflicts")).IsChecked = true;
                Layout();
                Button grouped = KeyButton(entries[0]!);
                var groupedColumns = (Grid)grouped.Parent;
                Native.Check(Math.Abs(Origin(grouped, list).X - buttonX) <= 1 &&
                    Math.Abs(((Border)groupedColumns.Parent).ActualHeight - rowHeight) <= 1 &&
                    groupedColumns.Children.OfType<TextBlock>().Any(text => text.Text == "Q") &&
                    Visuals(list).OfType<TextBlock>().Any(text => text.Text.Contains("与 1 项共用", StringComparison.Ordinal)),
                    $"The conflict list lost its grouping or diverges from the normal row geometry at width {width}.");
                Invoke(grouped);
                Native.Check(ReferenceEquals(Native.Get(controller, "Captured"), entries[0]), "The native key button did not enter the original capture flow.");
                Native.Call(controller.GetType(), "CancelCapture", controller);
            }
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void ExportOptionCaptions(string fixtureRoot)
    {
        string game = Path.Combine(fixtureRoot, "export-captions", "versions", "CaptionFixture");
        var instance = BackupResourceChecks.Instance(game);
        File.WriteAllText(Path.Combine(game, "options.txt"), "fixture options");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        File.WriteAllText(Path.Combine(game, "mods", "fixture.jar"), "synthetic mod");
        Directory.CreateDirectory(Path.Combine(game, "resourcepacks"));
        File.WriteAllText(Path.Combine(game, "resourcepacks", "Fixture Pack.zip"), "synthetic resource pack");
        var page = new PageInstanceExport();
        var controller = (ExportController)Native.Property(typeof(PageInstanceExport), "Controller").GetValue(page)!;
        var host = (ExportHost)Native.Get(controller, "host")!;
        host.Instance = () => instance; host.GameRoot = _ => game + Path.DirectorySeparatorChar;
        host.Name = _ => "导出显示夹具"; host.VersionFolder = _ => game;
        host.VersionDescription = _ => "1.21.1 · Fabric"; host.Version = _ => new McVersion("1.21.1");
        host.Modable = _ => true; host.JavaType = _ => 0; host.Info = _ => { };
        host.ModpackEnabled = () => false; host.TaskNames = () => Array.Empty<string>();
        int saveRequests = 0;
        host.SaveFile = (_, _, _, _) => { saveRequests++; return null; };
        host.ErrorToast = (error, message) => throw new InvalidOperationException(message, error);
        controller.RefreshAll();
        var layout = (FrameworkElement)page.Child;
        page.Child = null;
        NameScope.SetNameScope(layout, NameScope.GetNameScope(page));
        layout.Resources.MergedDictionaries.Add(page.Resources);
        Window? window = null;
        try {
            window = Host(layout);
            foreach (CheckBox option in controller.AllOptions(false)) {
                var caption = option.Content as TextBlock;
                string expected = caption is null ? "" : new TextRange(caption.ContentStart, caption.ContentEnd).Text;
                Native.Check(!string.IsNullOrWhiteSpace(expected) &&
                    AutomationProperties.GetName(option) == caption?.Text,
                    "The native export option label or accessible name is missing: " + expected);
            }
            Native.Check(Visuals(layout).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "导出内容列表"),
                "The export card lost its inline heading.");
            ((CheckBox)page.FindName("CheckOptionsResourcePacks")).IsChecked = true;
            File.WriteAllText(Path.Combine(game, "resourcepacks", "Added Pack.zip"), "another synthetic resource pack");
            controller.ReloadAllSubOptions(); controller.RefreshOptions(); window.UpdateLayout(); VisualAssetChecks.Drain();
            var packs = (Panel)page.FindName("PanOptionsResourcePacks");
            var added = packs.Children.OfType<CheckBox>().SingleOrDefault(option => option.Tag is ExportOption { Title: "Added Pack.zip" });
            string? addedCaption = added?.Content is TextBlock text
                ? new TextRange(text.ContentStart, text.ContentEnd).Text
                : added?.Content?.ToString();
            Native.Check(added is { Visibility: Visibility.Visible } && packs.Visibility == Visibility.Visible &&
                addedCaption == "Added Pack.zip",
                $"Refreshing export resources did not update the native labels. Rows={packs.Children.Count}, group={packs.Visibility}, option={added?.Visibility}, caption={addedCaption}.");
            controller.RulesOverrides = new List<string> { "options.txt" }; window.UpdateLayout();
            Native.Check(Visuals(layout).OfType<TextBlock>().Any(text => text.IsVisible && text.Text.Contains("从配置文件中读取", StringComparison.Ordinal)),
                "The export override heading did not follow the actual controller's inline update.");
            Button reset = Visuals(layout).OfType<Button>().Single(button => button.Name == "BtnOverrideCancel");
            Invoke(reset); window.UpdateLayout();
            Native.Check(controller.RulesOverrides is null && Visuals(layout).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "导出内容列表"),
                "The native export reset button did not restore the original options workflow.");
            Button export = Visuals(layout).OfType<Button>().Single(button => button.Name == "BtnExport");
            Invoke(export); Native.Check(saveRequests == 1, "The native export button did not reach the actual SaveFile workflow exactly once.");
            Native.Check(!page.IsLoaded, "Inspecting export labels unexpectedly ran the page scan.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); VisualAssetChecks.Drain();
    }

    private static void FloatingSelectionDockLayout()
    {
        var page = new PageInstanceMod();
        var layout = (Grid)page.Child;
        page.Child = null;
        layout.Resources.MergedDictionaries.Add(page.Resources);
        var dock = (GlassSelectionDock)page.FindName("CardSelect");
        var label = dock.Status;
        var actions = Tree(dock).OfType<Button>().ToDictionary(button => button.Name);
        var update = actions["BtnSelectUpdate"];
        var enable = actions["BtnSelectEnable"];
        var disable = actions["BtnSelectDisable"];
        var delete = actions["BtnSelectDelete"];
        var cancel = actions["BtnSelectCancel"];
        label.Text = "已选择 3 个文件";
        dock.ShowSelection(3, false);
        int cancelClicks = 0;
        cancel.Click += (_, _) => cancelClicks++;

        Window? window = null;
        try {
            window = Host(layout);
            CheckSelectionDockFrost(dock, (Grid)page.FindName("PanAllBack"), layout);
            foreach (int width in new[] { 820, 620 }) {
                window.Width = width;
                window.UpdateLayout();
                double gap = layout.ActualHeight - dock.TranslatePoint(new Point(0, dock.ActualHeight), layout).Y;
                Native.Check(gap >= 16 && dock.HorizontalAlignment == HorizontalAlignment.Center &&
                    dock.VerticalAlignment == VerticalAlignment.Bottom, "The native selection dock lost its elevated alignment.");
                Native.Check(dock.ActualHeight is >= 38 and <= 50, $"The selection dock stopped fitting on one row at width {width}.");
                double Center(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), dock).Y;
                Native.Check(new FrameworkElement[] { update, delete, cancel }.All(button => Math.Abs(Center(button) - Center(label)) <= 1.5),
                    $"Selection dock actions lost vertical alignment at width {width}.");
                Native.Check(delete.Foreground is SolidColorBrush fill &&
                    delete.FindResource("GlassPresentationDangerText") is SolidColorBrush danger && fill.Color == danger.Color,
                    "The destructive command lost its danger color.");
            }

            foreach (var mutation in new[] { update, enable, disable, delete }) mutation.Visibility = Visibility.Collapsed;
            window.UpdateLayout();
            int dividers = Visuals(dock).OfType<Border>().Count(border => border.Width == 1 && border.IsVisible);
            Native.Check(dividers == 1, $"Expected one divider before Cancel, found {dividers}.");
            Invoke(cancel);
            Native.Check(cancelClicks == 1, "The native Cancel button failed to trigger its command once.");
        } finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void CheckSelectionDockFrost(GlassSelectionDock dock, Grid scene, FrameworkElement root)
    {
        if (!GlassMaterial.AllowsTransparency(dock)) return;
        var sample = new Border { Background = Brushes.Red };
        Panel.SetZIndex(sample, 99);
        scene.Children.Add(sample);
        try {
            root.UpdateLayout();
            VisualAssetChecks.Drain();
            var frost = Visuals(dock).OfType<GlassSceneFrost>().Single();
            Native.Check(frost.IsVisible && frost.ActualWidth > 80, "Selection dock has no live backdrop.");
            (int R, int G, int B) Read()
            {
                root.UpdateLayout();
                VisualAssetChecks.Drain();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)frost.ActualWidth,
                    (int)frost.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(frost);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                return (pixel[2], pixel[1], pixel[0]);
            }
            var red = Read();
            sample.Background = Brushes.Blue;
            var blue = Read();
            Native.Check(red.R > red.B + 100 && blue.B > blue.R + 100, "Dock sampled the wrong layer or retained the previous frame.");
            var stripes = new DrawingGroup();
            using (var dc = stripes.Open()) {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 4, 4));
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 2, 4));
            }
            sample.Background = new DrawingBrush(stripes) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 4, 4) };
            var mixed = Read();
            Native.Check(mixed.R is > 48 and < 208, $"Expected fine stripes to blur together, got {mixed.R}.");
            Console.WriteLine($"Selection dock tracked red/blue page content and blurred black/white stripes to {mixed.R}/255.");
        } finally { scene.Children.Remove(sample); }
    }

    private static Window Host(FrameworkElement view)
    {
        var window = new Window { Content = view, Width = 1000, Height = 680, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
        return window;
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count != 0)
        {
            DependencyObject item = pending.Pop(); if (!seen.Add(item)) continue;
            yield return item;
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
            if (item is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
