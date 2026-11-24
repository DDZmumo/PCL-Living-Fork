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
using ExportController = PCLCS.ModpackExportPageController<PCL.ModMinecraft.McInstance, PCL.ExportOption, PCL.MyCheckBox, PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.ModLoader.LoaderCombo<string>>;
using ExportHost = PCLCS.ModpackExportPageHost<PCL.ModMinecraft.McInstance, PCL.ExportOption, PCL.ModLoader.LoaderCombo<string>>;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassProjectionRegressionChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "glass-projection-regressions");
        int animation = ModAnimation.AniControlEnabled;
        ModAnimation.AniControlEnabled++;
        try
        {
            SettingsCardInsets();
            KeybindingColumns(environment.Data);
            LaunchSettingsViewport();
            ExportOptionCaptions(environment.Data);
            InlineTitlesAndContextActions();
            LazyHelpCardToggle();
            FirstManagementInteractions(environment);
            FloatingSelectionDockLayout();
            Main150ControlsProjection();
        }
        finally { ModAnimation.AniControlEnabled = animation; }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS projection regressions: genuine launch settings viewport, export option/header text and actions, source inline titles, original instance/mod right-click actions and disposal.");
    }

    internal static void ReportedRegressions(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "glass-reported-regressions");
        var errors = new List<string>();
        foreach (Action check in new Action[] { HintWallpaperMaterial, LightSecondaryTextContrast, AboutCollapsedProjection, PersonalSyncLayout, InstanceResourceScroll, ModifyMinecraftControl, ProgressDecimals })
        {
            try { check(); }
            catch (Exception error) { errors.Add(error.Message); }
        }
        environment.AssertNoForbiddenIo();
        Native.Check(errors.Count == 0, string.Join("; ", errors));
        Console.WriteLine("PASS reported glass regressions: frosted hints, light-theme secondary text contrast, lazy About sections, aligned sync rows, resource scrollbar drag, inline Minecraft version selection and two-digit progress.");
    }

    private static void HintWallpaperMaterial()
    {
        var source = new StackPanel {
            Children = {
                new MyHint { Theme = MyHint.Themes.Blue, Text = "信息" },
                new MyHint { Theme = MyHint.Themes.Yellow, Text = "注意" },
                new MyHint { Theme = MyHint.Themes.Red, Text = "警告" }
            }
        };
        using var session = GlassPresentation.Create(source);
        var surfaces = Tree(session.View).OfType<PCLLF.Wpf.Glass.GlassSurface>().ToArray();
        Native.Check(surfaces.Length == 3 && surfaces.All(surface => surface.PaintsWallpaper && surface.BlurRadius > 0),
            "Projected hints must paint frosted wallpaper beneath their text and icons.");
    }

    private static void LightSecondaryTextContrast()
    {
        if (SystemParameters.HighContrast) return;
        const string status = "正在读取文件清单……", note = "组件、补丁与运行库", heading = "MMC 实例工具";
        var source = new StackPanel {
            Children = {
                new TextBlock { Text = status, Opacity = .65 },
                new MyHint { Theme = MyHint.Themes.Blue, Text = note }
            }
        };
        source.Resources["ColorBrush1"] = Brushes.Black;
        using var session = GlassPresentation.Create(source);
        using var shell = new GlassLauncherView();
        shell.SetState(new GlassLauncherViewState { IsHome = false, SelectedNavigationId = 7, UseDarkText = true, ReduceMotion = true });
        shell.SetSidebar([new GlassSidebarItem { Label = heading, IsHeader = true }]);
        var labels = Tree(session.View).OfType<TextBlock>().Where(text => text.Text == status || text.Text == note).ToList();
        labels.Add(((Panel)shell.FindName("SidebarPanel")).Children.OfType<TextBlock>().Single(text => text.Text == heading));
        Native.Check(labels.Count == 3, "Missing export or sidebar text in the contrast fixture.");
        // Representative tinted card colors sampled from the reported export page.
        Color[] backgrounds = [Color.FromRgb(152, 148, 180), Color.FromRgb(174, 136, 147)];
        foreach (TextBlock label in labels)
            Native.Check(label.Opacity == 1 && label.Foreground is SolidColorBrush { Opacity: 1, Color.A: 255 } brush &&
                backgrounds.All(background => (Luminance(background) + .05) / (Luminance(brush.Color) + .05) >= 4.5),
                "Light-theme secondary text is hard to read over the reported card colors: " + label.Text);

        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        static double Linear(byte channel) {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
    }

    private static void AboutCollapsedProjection()
    {
        var page = new PageOtherAbout();
        var create = typeof(GlassPresentation).GetMethod("Create",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var elapsed = Stopwatch.StartNew();
        using var session = (GlassPresentationSession)create.Invoke(null, [page, null, true])!;
        double firstBatchMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        Native.Check(!Tree(session.View).OfType<Expander>().Any(), "The About page built offscreen section headers in its first batch.");
        VisualAssetChecks.Pump(() => Tree(session.View).OfType<Expander>().Count() == 4, "About section batches arrive");
        var sections = Tree(session.View).OfType<Expander>().Where(item => !item.IsExpanded).ToArray();
        Native.Check(sections.Length == 4, "The About page lost one of its collapsible sections.");
        int eager = sections.Sum(item => Tree((DependencyObject)item.Content).OfType<FrameworkElement>().Count() - 1);
        Console.WriteLine($"About first projection batch: {firstBatchMilliseconds:0.0} ms, {eager} controls inside folded sections");
        Native.Check(eager == 0, $"About built {eager} offscreen controls inside its four folded sections before they could be seen.");
        var offline = (TextBox)page.FindName("LabThirdPartyLicenses");
        Native.Check(offline.Text.Length == 0, "The offline license text was loaded before its section was opened.");
        var offlineSection = sections.Single(item => item.Header is TextBlock { Text: "离线许可与来源" });
        offlineSection.IsExpanded = true;
        VisualAssetChecks.Drain();
        Native.Check(offline.Text.Length > 0 && Tree((DependencyObject)offlineSection.Content).OfType<TextBox>().Any(box => box.Text == offline.Text),
            "Opening About's offline section did not load and show the bundled license text.");
        var licenses = sections.Single(item => item.Header is TextBlock { Text: "许可与版权声明" });
        licenses.IsExpanded = true;
        VisualAssetChecks.Drain();
        Native.Check(Tree((DependencyObject)licenses.Content).OfType<Button>().Any(item => item.Content as string == "查看许可文档" ||
            Tree(item).OfType<TextBlock>().Any(label => label.Text == "查看许可文档")), "Opening About's license section did not realize its document actions.");
    }

    private static void PersonalSyncLayout()
    {
        var page = new PageSetupPersonal();
        foreach (string name in new[] { "BtnImport", "BtnSaveGlobal" })
            ((UIElement)page.FindName(name)).Visibility = Visibility.Collapsed;
        foreach (string name in new[] { "BtnWriteBack", "BtnSyncFromGlobal" })
            ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
        using var session = GlassPresentation.Create(page);
        var window = Host(session.View);
        try
        {
            foreach (int width in new[] { 820, 420 })
            {
                window.Width = width; window.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
                Rect Bounds(string name)
                {
                    var element = Tree(session.View).OfType<FrameworkElement>().Single(item => item.Name == name);
                    return element.TransformToAncestor(session.View).TransformBounds(new Rect(element.RenderSize));
                }
                Rect policy = Bounds("ComboPolicy"), push = Bounds("BtnWriteBack"), pull = Bounds("BtnSyncFromGlobal");
                Native.Check(push.Width > 0 && pull.Width > 0 && !push.IntersectsWith(pull) &&
                    Math.Min(push.Top, pull.Top) >= policy.Bottom && Math.Max(push.Right, pull.Right) <= session.View.ActualWidth,
                    $"Instance sync actions clipped or overlapped at {width} DIP: {push}, {pull}.");
            }
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void InstanceResourceScroll()
    {
        var page = new PageInstanceResources();
        ((FrameworkElement)page.FindName("PanAllBack")).Visibility = Visibility.Visible;
        ((FrameworkElement)page.FindName("PanAllBack")).Opacity = 1;
        var list = (StackPanel)page.FindName("PanList");
        ((TextBlock)page.FindName("LabEmpty")).Visibility = Visibility.Collapsed;
        var prepared = new int[45];
        for (int index = 0; index < prepared.Length; index++)
        {
            int item = index;
            var row = new MyLocalModItem { Name = "ViewportResource" + item };
            LazyLoadBehavior.OnFirstEnterScrollViewerViewport(row, () => { prepared[item]++; row.Title = "Resource " + item; });
            list.Children.Add(row);
        }
        var source = new Grid { Name = "PanMainRight", Children = { page } };
        using var session = GlassPresentation.Create(source);
        var window = Host(session.View);
        try
        {
            var scroll = Visuals(session.View).OfType<PCLLF.Wpf.Glass.ScrollEdgeFade>().First().ScrollViewer;
            scroll.ApplyTemplate();
            var thumb = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            VisualAssetChecks.Pump(() => prepared[0] == 1, "The first resource row did not hydrate without a global refresh.");
            Native.Check(prepared[^1] == 0, "Preparing the initial viewport eagerly loaded all resource rows.");
            Native.Check(scroll.ScrollableHeight > 0 && thumb.IsVisible && thumb.ActualWidth >= 10 && scroll.OpacityMask is null &&
                (SystemParameters.HighContrast || viewport.OpacityMask is not null), $"The resource list overflow did not show an unfaded scrollbar: scroll={scroll.ActualHeight:0}/{scroll.ExtentHeight:0} ({scroll.Visibility}/{scroll.IsVisible}), bar={thumb.Visibility}/{thumb.IsVisible}, width={thumb.ActualWidth:0}, viewport mask={viewport.OpacityMask is not null}.");
            thumb.ApplyTemplate();
            var track = (Track)thumb.Template.FindName("PART_Track", thumb);
            double travel = thumb.ActualHeight * .45;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, travel) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, travel, false) { RoutedEvent = Thumb.DragCompletedEvent });
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(scroll.VerticalOffset > 0,
                $"Dragging the resource scrollbar did not move its viewport (bar={thumb.Value:0}/{thumb.Maximum:0}, track={track.ActualHeight:0}, offset={scroll.VerticalOffset:0}).");
            VisualAssetChecks.Pump(() => {
                var visibleRows = Visuals(scroll).OfType<Grid>().Where(grid => grid.Name.StartsWith("ViewportResource", StringComparison.Ordinal))
                    .Where(grid => { double y = grid.TranslatePoint(new Point(), viewport).Y; return y < viewport.ActualHeight && y + grid.ActualHeight > 0; }).ToArray();
                return visibleRows.Length > 0 && visibleRows.All(row => {
                    int index = int.Parse(row.Name.Substring("ViewportResource".Length));
                    return prepared[index] == 1 && Visuals(row).OfType<TextBlock>().Any(text => text.Text == "Resource " + index);
                });
            }, "Rows entering the resource viewport remained blank until the page refreshed.");
            Native.Check(prepared[^1] == 0 && prepared.All(count => count < 2), "Scrolling realized distant resource rows or prepared the same row again.");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void ModifyMinecraftControl()
    {
        var page = new PageDownloadModify();
        // The inherited BAML queues an initial loader. This fixture supplies an already-loaded plan.
        var pending = Native.Get(page, "InitLoader") as ModLoader.LoaderBase;
        pending?.Cancel();
        var service = Native.Property(typeof(PageDownloadModify), "ModifyService").GetValue(page)!;
        object host = Native.Get(service, "host")!;
        int fullPageCalls = 0, reloads = 0;
        string? requestedForge = null;
        Native.Set(host, "ModpackEnabled", (Func<bool>)(() => false));
        Native.Set(host, "CanChangeMinecraft", (Func<string?, string?, bool>)((_, _) => true));
        Native.Set(host, "BaseShowMinecraftList", (Action)(() => fullPageCalls++));

        JObject Entry(string id) => new()
        {
            ["id"] = id, ["url"] = $"https://example.invalid/{id}.json",
            ["type"] = "release", ["releaseTime"] = "2025-01-02T00:00:00Z"
        };
        var original = Entry("1.21.11"); var target = Entry("1.20.1");

        // Exercise the actual selection/update methods, with each catalog source held in Loading.
        object install = Native.Property(typeof(PageDownloadInstall), "Service").GetValue(page)!;
        object installHost = Native.Get(install, "host")!;
        Native.Set(installHost, "StartForge", (Action<string>)(value => requestedForge = value));
        Native.Set(installHost, "ReloadSelected", (Action)(() => reloads++));
        foreach (string feed in new[] { "OptiFine", "LiteLoader", "NeoForge", "Fabric", "FabricApi", "OptiFabric" })
        {
            object loader = Native.Get(installHost, feed)!;
            Native.Set(loader, "Finished", (Func<bool>)(() => false));
            Native.Set(loader, "Start", (Action)(() => { }));
        }

        var options = (FrameworkElement)page.FindName("PanSelect");
        var legacyCatalog = (FrameworkElement)page.FindName("PanMinecraft");
        var change = (Grid)page.FindName("PanMinecraftChange");
        using var session = GlassPresentation.Create(page);
        var window = Host(session.View);
        try
        {
            if (pending is not null)
            {
                pending.Cancel();
                VisualAssetChecks.Pump(() => Native.Get(pending, "LastRunningThread") is not Thread { IsAlive: true },
                    "drain the original modify-page initializer");
            }
            VisualAssetChecks.Drain();
            page.IsInSelectPage = true;
            page.VanillaName = (string)original["id"]!;
            page.VanillaData = original;
            page.MinecraftVersions = new JArray(original, target);
            Native.Set(page, "InitialTarget", page.GetSelectedComponents());
            options.Visibility = Visibility.Visible; options.IsHitTestVisible = true; options.Opacity = 1;
            legacyCatalog.Visibility = Visibility.Collapsed;
            var content = (FrameworkElement)page.FindName("PanAllBack");
            content.Visibility = Visibility.Visible; content.IsHitTestVisible = true; content.Opacity = 1;
            ((MyCard)page.FindName("CardMinecraft")).Visibility = Visibility.Visible;
            ((TextBlock)page.FindName("LabMinecraftVersion")).Text = "1.21.11";
            change.Visibility = Visibility.Visible;
            session.Refresh();
            var expander = Tree(session.View).OfType<Expander>().Single(item => item.Name == "MinecraftVersionPicker");
            Native.Check(Tree((DependencyObject)expander.Header).OfType<TextBlock>().Any(item => item.Text == "1.21.11"),
                "The Minecraft picker omitted the selected version.");
            expander.IsExpanded = true;
            var list = Tree((DependencyObject)expander.Content).OfType<ListBox>().Single();
            Native.Check(list.Items.Count == 2 && fullPageCalls == 0 && legacyCatalog.Visibility == Visibility.Collapsed && options.Visibility == Visibility.Visible,
                "Expanding Minecraft replaced the modification screen with the full-page catalog.");
            var search = Tree((DependencyObject)expander.Content).OfType<TextBox>().Single();
            search.Text = "1.20";
            Native.Check(list.Items.Count == 1 && page.VanillaName == "1.21.11" && reloads == 0, "Filtering the inline list altered the selected version.");
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var choice = Visuals(list).OfType<Button>().Single(item => Equals(item.Content, "1.20.1"));
            Native.Check(page.IsInSelectPage && page.VanillaName == "1.21.11", $"Projection changed the fixture before selection (Minecraft={page.VanillaName}, inside options={page.IsInSelectPage}).");
            choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, choice));
            Native.Check(page.VanillaName == "1.20.1" && ReferenceEquals(page.VanillaData, target) &&
                requestedForge == "1.20.1" && reloads == 1 && fullPageCalls == 0 && !expander.IsExpanded &&
                page.IsInSelectPage && options.Visibility == Visibility.Visible && legacyCatalog.Visibility == Visibility.Collapsed,
                $"Choosing the inline version failed: Minecraft={page.VanillaName}, same entry={ReferenceEquals(page.VanillaData, target)}, Forge={requestedForge}, reloads={reloads}, legacy calls={fullPageCalls}, expanded={expander.IsExpanded}, enabled={expander.IsEnabled}, inside options={page.IsInSelectPage}, options={options.Visibility}, catalog={legacyCatalog.Visibility}.");
            change.Visibility = Visibility.Collapsed;
            session.Refresh();
            Native.Check(!expander.IsEnabled && !expander.IsExpanded, "A restricted modpack exposed Minecraft version changes.");
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
        ((FrameworkElement)overview.FindName("PanBack")).Visibility = Visibility.Visible;
        var adoption = (MyButton)overview.FindName("BtnMmcLegacyAdoption");
        using var overviewSession = GlassPresentation.Create(overview);
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
        using var personalSession = GlassPresentation.Create(personal);
        var differences = new List<string>();
        Window? window = null;
        void Layout()
        {
            window!.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
        }
        void CheckInsets(FrameworkElement view, string cardName, Func<FrameworkElement, bool> isContent)
        {
            var surface = Tree(view).OfType<PCLLF.Wpf.Glass.GlassSurface>()
                .SingleOrDefault(element => element.Name == cardName || element.Name == cardName + "Surface")
                ?? throw new InvalidOperationException($"Projected card is missing: {cardName}");
            Rect[] content = Visuals(surface).OfType<FrameworkElement>()
                .Where(element => element.IsVisible && isContent(element))
                .Select(element => element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize)))
                .ToArray();
            double top = content.Min(rect => rect.Top);
            double bottom = surface.ActualHeight - content.Max(rect => rect.Bottom);
            if (Math.Abs(top - bottom) > 1.1 || Math.Min(top, bottom) < 12)
                differences.Add($"{cardName} at {window!.Width}: top={top:0.##}, bottom={bottom:0.##}");
        }
        try
        {
            window = Host(overviewSession.View);
            foreach (int width in new[] { 820, 480 })
            {
                window.Width = width;
                foreach (Visibility visibility in new[] { Visibility.Visible, Visibility.Collapsed })
                {
                    adoption.Visibility = visibility;
                    overviewSession.Refresh(); Layout();
                    CheckInsets(overviewSession.View, "PanFolder", element => element is Button && element.Name.StartsWith("BtnFolder", StringComparison.Ordinal));
                    CheckInsets(overviewSession.View, "PanManage", element => element is Button && (element.Name.StartsWith("BtnManage", StringComparison.Ordinal) || element.Name == adoption.Name));
                }
            }
            window.Content = personalSession.View;
            foreach (int width in new[] { 820, 480 })
            {
                window.Width = width;
                Layout();
                CheckInsets(personalSession.View, "CardSearch", element => element is TextBox { Name: "TextSearchInput" });
                var surface = Visuals(personalSession.View).OfType<Border>().Single(element => element.Name == "CardSearchSurface");
                var search = Visuals(surface).OfType<TextBox>().Single(element => element.Name == "TextSearchInput");
                Rect field = search.TransformToAncestor(surface).TransformBounds(new Rect(search.RenderSize));
                double remaining = surface.ActualWidth - field.Right;
                Native.Check(remaining >= 14 && Math.Abs(field.Left - remaining) <= 1.1,
                    $"The personal settings search is horizontally clipped at width {width}: left={field.Left:0.##}, right={remaining:0.##}.");
                Rect Bounds(string name)
                {
                    var element = Visuals(personalSession.View).OfType<FrameworkElement>().Single(item => item.Name == name);
                    return element.TransformToAncestor(personalSession.View).TransformBounds(new Rect(element.RenderSize));
                }
                Rect save = Bounds("BtnApply"), refresh = Bounds("BtnRefresh"), policy = Bounds("ComboPolicy"), note = Bounds("LabEditScope");
                Native.Check(Math.Abs(save.Top - refresh.Top) <= 1 && refresh.Left - save.Right is >= 5 and <= 10 &&
                    note.Top >= policy.Bottom - 1 && note.Bottom <= save.Top + 1,
                    $"Personal settings buttons are too far apart or their help text is separated from the policy at width {width}.");
                Native.Check(Bounds("LabSummary").Right <= personalSession.View.ActualWidth + 1 && Bounds("LabUpdated").Right <= personalSession.View.ActualWidth + 1,
                    $"The personal settings summary overflows at width {width}.");
            }
            Native.Check(differences.Count == 0, "Uneven settings card insets: " + string.Join("; ", differences));
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
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

        using var session = GlassPresentation.Create(page);
        Window? window = null;
        try
        {
            window = Host(session.View);
            Button KeyButton(object entry) => Visuals(session.View).OfType<Button>().Single(button => ReferenceEquals(button.Tag, entry));
            void Layout()
            {
                session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
            }
            static Point Origin(FrameworkElement control, FrameworkElement root) => control.TransformToAncestor(root).Transform(new Point());
            foreach (int width in new[] { 820, 420 })
            {
                window.Width = width;
                ((MyRadioButton)page.FindName("BtnFilterAll")).Checked = true;
                Layout();
                Button normal = KeyButton(entries[0]!);
                var columns = (Grid)normal.Parent;
                var profile = columns.Children.OfType<TextBlock>().Single(text => text.Text == "Q");
                var sync = columns.Children.OfType<Border>().Single(badge => badge.Child is TextBlock { Text: "参与" });
                var names = columns.Children.OfType<StackPanel>().Single();
                double rowHeight = ((FrameworkElement)columns.Parent).ActualHeight;
                double buttonX = Origin(normal, session.View).X;
                Native.Check(rowHeight >= 40 && Origin(names, columns).X + names.ActualWidth <= Origin(profile, columns).X + 1 &&
                    Origin(profile, columns).X + profile.ActualWidth <= Origin(sync, columns).X + 1 &&
                    Origin(sync, columns).X + sync.ActualWidth <= Origin(normal, columns).X + 1 &&
                    !Visuals(names).OfType<TextBlock>().Any(t => t.Text.Contains("个人方案", StringComparison.Ordinal)),
                    $"Personal key and sync status overlap or fall below the keybinding name at width {width}.");
                Native.Check(((Grid)KeyButton(entries[2]!).Parent).Children.OfType<TextBlock>().Any(text => text.Text == "未保存"),
                    "An unset personal binding was confused with an enabled sync state.");

                ((MyRadioButton)page.FindName("BtnFilterConflicts")).Checked = true;
                Layout();
                Button grouped = KeyButton(entries[0]!);
                var groupedColumns = (Grid)grouped.Parent;
                Native.Check(Math.Abs(Origin(grouped, session.View).X - buttonX) <= 1 &&
                    Math.Abs(((FrameworkElement)groupedColumns.Parent).ActualHeight - rowHeight) <= 1 &&
                    groupedColumns.Children.OfType<TextBlock>().Any(text => text.Text == "Q") &&
                    Visuals(session.View).OfType<TextBlock>().Any(text => text.Text.Contains("与 1 项共用", StringComparison.Ordinal)),
                    $"The conflict list lost its grouping or diverges from the normal row geometry at width {width}.");
                Invoke(grouped);
                Native.Check(ReferenceEquals(Native.Get(controller, "Captured"), entries[0]), "The projected key button did not enter the original capture flow.");
                Native.Call(controller.GetType(), "CancelCapture", controller);
            }
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void LaunchSettingsViewport()
    {
        var page = new PageSetupLaunch();
        MyTextBox original = Tree(page).OfType<MyTextBox>().Single(input => SettingService.GetKey(input) == "LaunchArgumentInfo");
        using GlassPresentationSession session = GlassPresentation.Create(page);
        Window? window = null;
        try
        {
            window = Host(session.View);
            window.Width = 1000; window.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
            FrameworkElement argumentCard = Visuals(session.View).OfType<FrameworkElement>().First(item => item.Name == "CardArgument");
            Native.Check(argumentCard.ActualWidth >= 750,
                $"Settings section stack stayed shrink-wrapped after initial 0 -> 1000 layout pass without manual Refresh(): width={argumentCard.ActualWidth:0.##}.");
            foreach (int width in new[] { 1000, 810 })
            {
                window.Width = width; window.UpdateLayout(); VisualAssetChecks.Drain(); window.UpdateLayout();
                TextBox input = Visuals(session.View).OfType<TextBox>().Single(text => ReferenceEquals(BindingOperations.GetBinding(text, TextBox.TextProperty)?.Source, original));
                input.UpdateLayout();
                Rect glyph = input.GetRectFromCharacterIndex(0, true);
                var viewport = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Native.Check(input.Text == "PCL" && input.ActualHeight >= 34 && input.ActualHeight <= 38 &&
                    Math.Abs(input.ActualWidth - 220) < 2 &&
                    !glyph.IsEmpty && glyph.Height > 10 && viewport.ViewportHeight >= glyph.Height - .75,
                    $"Actual launch information text is vertically clipped or misaligned at {width}: width={input.ActualWidth}, glyph={glyph.Height}, viewport={viewport.ViewportHeight}.");
            }
            Native.Check(!page.IsLoaded && !original.IsLoaded, "Measuring the new launch settings ran the source page's Loaded workflow.");
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
        // Match the footer's own Loaded visibility guard without loading the source page.
        ((MyExtraTextButton)page.FindName("BtnExport")).Show = true;
        var original = (MyCheckBox)page.FindName("CheckOptionsOptions");
        string inlineText = new TextRange(original.LabText.ContentStart, original.LabText.ContentEnd).Text;
        Native.Check(string.IsNullOrEmpty(original.Text) && inlineText.Contains("游戏本体设置", StringComparison.Ordinal),
            $"The export fixture did not exercise inline-only captions: Text='{original.Text}', LabText='{original.LabText.Text}', range='{inlineText}', visibility={original.Visibility}.");
        using GlassPresentationSession session = GlassPresentation.Create(page);
        Window? window = null;
        try {
            window = Host(session.View);
            foreach (MyCheckBox source in controller.AllOptions(false)) {
                CheckBox toggle = Visuals(session.View).OfType<CheckBox>().Single(item =>
                    ReferenceEquals(BindingOperations.GetBinding(item, ToggleButton.IsCheckedProperty)?.Source, source));
                string expected = new TextRange(source.LabText.ContentStart, source.LabText.ContentEnd).Text;
                string actual = (toggle.Content as TextBlock)?.Text ?? string.Empty;
                Native.Check(!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual) &&
                    (source.Name == "CheckOptionsPclCustom" ? !actual.Contains("功能隐藏", StringComparison.Ordinal) : actual == expected) && AutomationProperties.GetName(toggle) == actual,
                    "The export option label/description or accessible name is missing: " + expected);
            }
            Native.Check(Visuals(session.View).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "导出内容列表"),
                "The export card lost the actual controller's inline-only heading.");
            File.WriteAllText(Path.Combine(game, "resourcepacks", "Added Pack.zip"), "another synthetic resource pack");
            controller.ReloadAllSubOptions(); controller.RefreshOptions(); session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Visuals(session.View).OfType<TextBlock>().Any(text => text.Text == "Added Pack.zip"),
                "Refreshing original export resources did not update the new labels.");
            controller.RulesOverrides = new List<string> { "options.txt" }; session.Refresh(); window.UpdateLayout();
            Native.Check(Visuals(session.View).OfType<TextBlock>().Any(text => text.IsVisible && text.Text.Contains("从配置文件中读取", StringComparison.Ordinal)),
                "The export override heading did not follow the actual controller's inline update.");
            Button reset = Visuals(session.View).OfType<Button>().Single(button => button.Name == "BtnOverrideCancel");
            Invoke(reset); session.Refresh(); window.UpdateLayout();
            Native.Check(controller.RulesOverrides is null && Visuals(session.View).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "导出内容列表"),
                "The new export reset button did not restore the original options workflow.");
            Button export = Visuals(session.View).OfType<Button>().Single(button => button.Name == "BtnExport");
            Invoke(export); Native.Check(saveRequests == 1, "The new export button did not reach the actual SaveFile workflow exactly once.");
            Native.Check(!page.IsLoaded, "Inspecting export labels unexpectedly ran the original page's Loaded workflow.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); VisualAssetChecks.Drain();
    }

    private static void FirstManagementInteractions(FixtureEnvironment environment)
    {
        using var scope = new MapInstallResourceScope(environment, "first-management-interaction");
        var instance = scope.Instance();
        var page = new PageInstanceMod();
        object controller = Native.Get(page, "Native")!, host = Native.Get(controller, "host")!;
        var entries = new List<LocalResourceFile>();
        var rows = new List<MyLocalModItem>();
        var initialized = new Dictionary<MyLocalModItem, int>();
        var files = new Dictionary<string, byte[]>();
        var source = (Panel)page.FindName("PanList"); source.Visibility = Visibility.Visible;
        for (int index = 0; index < 3; index++) {
            string file = Path.Combine(scope.Root, "mods", "first-" + index + ".jar");
            Package(file, "fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"fixture_" + index + "\",\"version\":\"1.0\",\"name\":\"First interaction\"}");
            files[file] = File.ReadAllBytes(file);
            var entry = new LocalResourceFile(file) { Display = "首次模组 " + index };
            var row = (MyLocalModItem)Native.Call(typeof(PageInstanceMod), "CreateItem", page, entry, false)!;
            row.Name = "FirstMod" + index; row.Title = entry.Display;
            Action<MyLocalModItem, EventArgs> original = row.ButtonHandler;
            initialized[row] = 0;
            row.ButtonHandler = (sender, args) => { initialized[row]++; original(sender, args); };
            page.ModItems[entry.StorageKey] = row; entries.Add(entry); rows.Add(row); source.Children.Add(row);
        }
        int messages = 0, opens = 0, background = 0;
        var openedPaths = new List<string>();
        Native.Set(host, "Output", (Func<List<LocalResourceFile>>)(() => entries));
        Native.Set(host, "Instance", (Func<ModMinecraft.McInstance>)(() => instance));
        Native.Set(host, "AnimationControl", (Func<int>)(() => 0));
        Native.Set(host, "Hint", (Action<string, int, bool>)((_, _, _) => { }));
        Native.Set(host, "Message", (Func<string, string, string, string, bool, int>)((_, _, _, _, _) => { messages++; return 2; }));
        Native.Set(host, "Explorer", (Action<string>)(path => { openedPaths.Add(path); opens++; }));
        Native.Set(host, "Website", (Action<string>)(_ => throw new InvalidOperationException("Mod interaction attempted a real website.")));
        Native.Set(host, "Background", (Action<Action, string, ThreadPriority>)((_, name, _) => { Native.Check(name == "Mod Dependency Details", "Unexpected mod background operation."); background++; }));
        Native.Set(host, "LoaderRun", (Func<int, bool>)(_ => throw new InvalidOperationException("First interaction started a mod scan.")));
        Native.Set(host, "RefreshLoaderWarning", (Action<MyLocalModItem, ModMinecraft.McInstance>)((_, _) => { }));
        using (GlassPresentationSession session = GlassPresentation.Create(source)) {
            Window? window = null;
            try {
                Native.Check(initialized.Values.All(count => count == 0), "Projecting the mod list eagerly initialized every original row.");
                window = Host(session.View);
                FrameworkElement View(MyLocalModItem row) => Visuals(session.View).OfType<FrameworkElement>().Single(item => item.Name == row.Name);
                Button Action(MyLocalModItem row, string caption) => Visuals(View(row)).OfType<Button>().Single(button => AutomationProperties.GetName(button) == caption);
                Native.Check(rows.All(row => !row.IsMouseOver), "The first-interaction regression unexpectedly hovered an original row.");
                CheckBox choice = Visuals(View(rows[0])).OfType<CheckBox>().Single();
                var checkPeer = UIElementAutomationPeer.CreatePeerForElement(choice) ?? new CheckBoxAutomationPeer(choice);
                ((IToggleProvider)checkPeer.GetPattern(PatternInterface.Toggle)!).Toggle(); VisualAssetChecks.Drain();
                Native.Check(rows[0].Checked && page.SelectedMods.SequenceEqual(new[] { entries[0].StorageKey }), "The first native mod selection did not reach the real page selection collection.");
                Invoke(Action(rows[1], rows[1].Title));
                Native.Check(rows[1].Checked && page.SelectedMods.Count == 2 && page.SelectedMods.Contains(entries[1].StorageKey), "First native mod Invoke did not run its original selection callback once.");
                Right(Action(rows[2], rows[2].Title));
                Native.Check(messages == 1 && !rows[2].Checked && page.SelectedMods.Count == 2, "The first mod context action lost the actual detail callback or changed selection.");
                string[] expected = { "详情", "查看模组依赖关系", "打开文件位置", "禁用", "删除" };
                foreach (MyLocalModItem row in rows) {
                    row.EnsureActions(); row.EnsureActions(); session.Refresh();
                    Native.Check(initialized[row] == 1 && row.ButtonHandler is null && row.Buttons.Select(button => button.ToolTip as string).SequenceEqual(expected), "Mod commands were missing or the original row was initialized more than once.");
                }
                var rowView = View(rows[0]);
                var accessories = Visuals(rowView).OfType<StackPanel>().Single(panel => AutomationProperties.GetName(panel) == "资源操作");
                Native.Check(accessories.Visibility == Visibility.Collapsed, "An idle resource row showed hover-only actions.");
                rowView.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Native.Check(accessories.Visibility == Visibility.Visible, "Hovering the row did not reveal resource actions.");
                Invoke(Action(rows[0], "详情")); Invoke(Action(rows[0], "打开文件位置")); Invoke(Action(rows[0], "查看模组依赖关系"));
                rowView.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                session.Refresh();
                Native.Check(accessories.Visibility == Visibility.Collapsed, "Resource actions stayed behind after the cursor and refresh.");
                Native.Check(messages == 2 && opens == 1 && background == 1, $"Generated native mod command port counts: message={messages}, explorer={opens}, background={background}.");
                Native.Check(openedPaths.All(path => files.ContainsKey(MeloongCore.PathUtils.RemoveExtendedPrefix(path))), "Mod action lost its actual fixture path: " + string.Join(", ", openedPaths));
                Native.Check(files.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "First mod interactions modified a fixture file.");
                Native.Check(!page.IsLoaded, "Testing projected mod rows ran the original page's Loaded/scan workflow.");
            }
            finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
        }
        foreach (string kind in new[] { "ResourcePacks", "ShaderPacks" }) {
            string folder = Path.Combine(scope.Root, kind), file = Path.Combine(folder, "Fixture.zip");
            Package(file, kind == "ResourcePacks" ? "pack.mcmeta" : "shaders/fixture.vsh", kind == "ResourcePacks" ? "{\"pack\":{\"pack_format\":46,\"description\":\"first interaction\"}}" : "// fixture");
            byte[] before = File.ReadAllBytes(file);
            object type = Native.Enum("PCL.InstanceResourceKind", kind)!;
            var resources = (PageInstanceResources)Activator.CreateInstance(typeof(PageInstanceResources), Native.Flags, null, new[] { type }, null)!;
            scope.ObservePage(resources);
            object resourceController = Native.Get(resources, "Controller")!, ports = Native.Get(resourceController, "Host")!;
            int details = 0, explorer = 0, processes = 0;
            Native.Set(ports, "Message", (Func<string, string, string, string, bool, int>)((text, caption, _, _, _) => { Native.Check(caption == "资源详情" && text.Contains("Fixture.zip", StringComparison.Ordinal), "Resource details lost the actual entry."); details++; return 1; }));
            Native.Set(ports, "OpenExplorer", (Action<string>)(path => { Native.Check(path == file, "Resource open lost its actual file path."); explorer++; }));
            Native.Set(ports, "OpenProcess", (Action<ProcessStartInfo>)(_ => { processes++; throw new InvalidOperationException("Package interaction must not open a real process."); }));
            IList scanned = (IList)Native.Static("PCL.InstanceResourceStore", "Scan", folder, folder, type, CancellationToken.None)!;
            Native.Call(typeof(PageInstanceResources), "ShowResources", resources, folder, folder, scanned);
            var list = (Panel)resources.FindName("PanList");
            MyLocalModItem row = list.Children.OfType<MyLocalModItem>().Single(); row.Name = kind + "FirstRow";
            using GlassPresentationSession session = GlassPresentation.Create(list);
            Window? window = null;
            try {
                window = Host(session.View);
                CheckBox choice = Visuals(session.View).OfType<CheckBox>().Single();
                var peer = UIElementAutomationPeer.CreatePeerForElement(choice) ?? new CheckBoxAutomationPeer(choice);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle(); VisualAssetChecks.Drain();
                Native.Check(row.Checked && ((HashSet<string>)Native.Get(resourceController, "SelectedPaths")!).Contains(file), kind + " first selection did not reach the original controller.");
                Button Action(string caption) => Visuals(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == caption);
                Invoke(Action("详情")); Invoke(Action("打开文件位置"));
                Native.Check(details == 1 && explorer == 1 && processes == 0 && File.ReadAllBytes(file).SequenceEqual(before), kind + " projected actions bypassed, duplicated, or mutated their original resource entry.");
                Native.Check(!resources.IsLoaded, "Package interaction started the original page's Loaded workflow.");
            }
            finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
        }
        static void Package(string file, string entry, string content) {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using ZipArchive archive = ZipFile.Open(file, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open()); writer.Write(content);
        }
    }

    private static void InlineTitlesAndContextActions()
    {
        var source = new StackPanel();
        var instance = new MyListItem { Name = "InlineInstance", Type = MyListItem.CheckType.Clickable };
        // The real McInstance.ToListItem builder uses Inlines and leaves Title unset.
        instance.Inlines.Clear(); instance.Inlines.Add(new Run("实例甲")); instance.Inlines.Add(new Run(" | 1.21.1"));
        var mod = new MyLocalModItem { Name = "ContextMod", Title = "本地模组", Description = "右键查看信息" };
        source.Children.Add(instance); source.Children.Add(mod);
        int instanceContext = 0, modContext = 0, left = 0; bool veto = false;
        instance.Click += (_, _) => left++;
        instance.PreviewMouseRightButtonUp += (_, args) => { if (veto) args.Handled = true; };
        instance.MouseRightButtonUp += (sender, args) => {
            Native.Check(ReferenceEquals(sender, instance) && ReferenceEquals(args.Source, instance), "The original instance context action lost its real source sender.");
            instanceContext++; args.Handled = true;
        };
        mod.MouseRightButtonUp += (sender, args) => {
            Native.Check(ReferenceEquals(sender, mod) && ReferenceEquals(args.Source, mod), "The original mod info action lost its real source sender.");
            modContext++; args.Handled = true;
        };
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(session.View);
            Button row = Visuals(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "实例甲 | 1.21.1");
            Native.Check(string.IsNullOrEmpty(instance.Title) && Visuals(row).OfType<TextBlock>().Any(text => text.Text == "实例甲 | 1.21.1"),
                "The original inline-only instance name disappeared from the new row or its accessible name.");
            instance.Inlines.Clear(); instance.Inlines.Add(new Run("重命名实例")); session.Refresh();
            Native.Check(AutomationProperties.GetName(row) == "重命名实例", "A source inline title update did not reach the existing native row.");
            Right(row);
            Native.Check(instanceContext == 1 && left == 0, "Right-clicking the new instance row lost its original context action or activated its left action.");
            veto = true; Right(row);
            Native.Check(instanceContext == 1, "A handled original context preview still executed the original release action.");
            Button modBody = Visuals(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == mod.Title);
            Right(modBody);
            Native.Check(modContext == 1 && left == 0, "Right-clicking the new mod row did not invoke the original information action exactly once.");
            session.Dispose(); Right(row); Right(modBody);
            Native.Check(instanceContext == 1 && modContext == 1 && left == 0, "A retired projected context action still reached its source.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void Right(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = element
        });
        VisualAssetChecks.Drain();
    }

    private static void LazyHelpCardToggle()
    {
        var source = new StackPanel(); int built = 0, swapped = 0; bool veto = true;
        var host = new LegacyAuxiliaryHelpHost<string, int> {
            Release = () => true, ShowPublic = _ => true, ShowSnapshot = _ => true,
            Types = _ => new List<string> { "Minecraft 百科" }, NewCard = () => new MyCard(),
            ToItem = entry => { built++; return new MyListItem { Name = "LazyHelpEntry", Title = entry, Type = MyListItem.CheckType.Clickable }; }
        };
        var controller = new LegacyAuxiliaryHelpController<string, int>(new LegacyAuxiliaryHelpRuntime<string, int>(host),
            new LegacyAuxiliaryHelpView { List = () => source, Scroll = () => new ScrollViewer() });
        controller.HelpListLoad(new List<string> { "真实帮助条目" });
        MyCard card = source.Children.OfType<MyCard>().Single();
        card.PreviewSwap += (_, args) => args.Handled = veto;
        card.Swap += (_, _) => swapped++;
        Native.Check(!card.CanSwap && card.SwapControl is StackPanel && card.IsSwapped && built == 0,
            "The actual help helper did not create its implicit-swappable lazy collapsed card.");
        using GlassPresentationSession session = GlassPresentation.Create(source);
        var layer = new Grid(); layer.Children.Add(new Grid { Visibility = Visibility.Hidden, IsHitTestVisible = false, Children = { source } }); layer.Children.Add(session.View);
        Window? window = null;
        try
        {
            window = Host(layer);
            Native.Check(card.IsLoaded && !card.IsVisible && card.Height == 40 && card.SwapControl!.Visibility == Visibility.Collapsed,
                "The lazy help regression did not retain the actual loaded/hidden collapsed semantic card.");
            Expander expander = Visuals(session.View).OfType<Expander>().Single();
            ToggleButton toggle = Visuals(expander).OfType<ToggleButton>().Single();
            void ActivateHeader()
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(toggle) ?? new ToggleButtonAutomationPeer(toggle);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
                VisualAssetChecks.Drain(); session.Refresh(); window.UpdateLayout();
            }
            ActivateHeader();
            Native.Check(card.IsSwapped && !expander.IsExpanded && built == 0 && swapped == 0,
                "Clicking the native header ignored the original help-card swap veto.");
            veto = false; ActivateHeader();
            VisualAssetChecks.Pump(() => Visuals(session.View).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "真实帮助条目" && button.IsVisible), "The actual help header TogglePattern did not reveal its lazy content.");
            Button entry = Visuals(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "真实帮助条目");
            Native.Check(!card.IsSwapped && expander.IsExpanded && built == 1 && swapped == 1,
                "The implicit-swappable original help card did not open/build exactly once through its native header.");
            ActivateHeader(); ActivateHeader();
            Native.Check(!card.IsSwapped && expander.IsExpanded && built == 1 && swapped == 3 &&
                ReferenceEquals(entry, Visuals(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "真实帮助条目")),
                "Reversing the actual help header rebuilt or lost its existing lazy action.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void FloatingSelectionDockLayout()
    {
        var page = new PageInstanceMod();
        ((FrameworkElement)page.FindName("PanMain")).Visibility = Visibility.Visible;
        ((FrameworkElement)page.FindName("PanLoad")).Visibility = Visibility.Collapsed;
        var panList = (Panel)page.FindName("PanList");
        panList.Visibility = Visibility.Visible;
        foreach (string modTitle in new[] { "钠 (Sodium)", "Zoomify", "Iris Shaders", "Lithium" })
        {
            panList.Children.Add(new MyLocalModItem { Title = modTitle, Checked = modTitle != "Lithium" });
        }
        var cardSelect = (MyCard)page.FindName("CardSelect");
        var labSelect = (TextBlock)page.FindName("LabSelect");
        var btnUpdate = (MyIconTextButton)page.FindName("BtnSelectUpdate");
        var btnEnable = (MyIconTextButton)page.FindName("BtnSelectEnable");
        var btnDisable = (MyIconTextButton)page.FindName("BtnSelectDisable");
        var btnDelete = (MyIconTextButton)page.FindName("BtnSelectDelete");
        var btnCancel = (MyIconTextButton)page.FindName("BtnSelectCancel");
        labSelect.Text = "已选择 3 个文件";
        cardSelect.Visibility = Visibility.Visible;
        cardSelect.Opacity = 1;
        btnUpdate.Visibility = Visibility.Visible;
        btnEnable.Visibility = Visibility.Visible;
        btnDisable.Visibility = Visibility.Visible;
        btnDelete.Visibility = Visibility.Visible;
        btnCancel.Visibility = Visibility.Visible;
        int cancelClicks = 0;
        btnCancel.Click += (_, _) => cancelClicks++;

        using GlassPresentationSession session = GlassPresentation.Create(page);
        session.View.Resources["GlassTextBrush"] = new SolidColorBrush(Color.FromRgb(244, 244, 244));
        session.View.Resources["GlassAccentBrush"] = new SolidColorBrush(Color.FromRgb(244, 244, 244));
        Window? window = null;
        try
        {
        var backdrop = new Grid
        {
            Background = new LinearGradientBrush(Color.FromRgb(26, 34, 48), Color.FromRgb(58, 42, 68), new Point(0, 0), new Point(1, 1)),
        };
        PCLLF.Wpf.Glass.GlassMaterial.SetIsEnabled(backdrop, true);
        PCLLF.Wpf.Glass.GlassMaterial.SetDesktopBackdropEnabled(backdrop, true);
        backdrop.Children.Add(session.View);
            window = Host(backdrop);
            foreach (int width in new[] { 820, 620 })
            {
                window.Width = width;
                window.UpdateLayout();
                session.Refresh();
                VisualAssetChecks.Drain();
                window.UpdateLayout();

                FrameworkElement projectedDock = Visuals(session.View).OfType<FrameworkElement>().Single(item => item.Name == "CardSelect");
                TextBlock projectedLabel = Visuals(projectedDock).OfType<TextBlock>().Single(item => item.Name == "LabSelect");
                Button projectedUpdate = Visuals(projectedDock).OfType<Button>().Single(item => item.Name == "BtnSelectUpdate");
                Button projectedDelete = Visuals(projectedDock).OfType<Button>().Single(item => item.Name == "BtnSelectDelete");
                Button projectedCancel = Visuals(projectedDock).OfType<Button>().Single(item => item.Name == "BtnSelectCancel");

                Native.Check(projectedDock.Margin.Bottom >= 16 && projectedDock.HorizontalAlignment == HorizontalAlignment.Center &&
                    projectedDock.VerticalAlignment == VerticalAlignment.Bottom,
                    $"CardSelect lost its elevated floating dock margins/alignment: margin={projectedDock.Margin}.");
                Native.Check(projectedDock.ActualHeight >= 38 && projectedDock.ActualHeight <= 50,
                    $"CardSelect was not laid out as a single-row compact floating dock at width {width}: height={projectedDock.ActualHeight}.");

                double labelCenterY = projectedLabel.TranslatePoint(new Point(0, projectedLabel.ActualHeight / 2), projectedDock).Y;
                double updateCenterY = projectedUpdate.TranslatePoint(new Point(0, projectedUpdate.ActualHeight / 2), projectedDock).Y;
                double deleteCenterY = projectedDelete.TranslatePoint(new Point(0, projectedDelete.ActualHeight / 2), projectedDock).Y;
                double cancelCenterY = projectedCancel.TranslatePoint(new Point(0, projectedCancel.ActualHeight / 2), projectedDock).Y;
                Native.Check(Math.Abs(labelCenterY - updateCenterY) <= 1.5 &&
                    Math.Abs(updateCenterY - deleteCenterY) <= 1.5 &&
                    Math.Abs(deleteCenterY - cancelCenterY) <= 1.5,
                    $"CardSelect items did not align on one horizontal row at width {width}: labelY={labelCenterY}, updateY={updateCenterY}, deleteY={deleteCenterY}, cancelY={cancelCenterY}.");

                var dangerText = session.View.Resources["GlassPresentationDangerText"] as SolidColorBrush;
                var deleteLabel = Visuals(projectedDelete).OfType<TextBlock>().Single();
                Native.Check(dangerText is not null && (deleteLabel.Foreground as SolidColorBrush)?.Color == dangerText.Color,
                    "CardSelect destructive delete button did not receive semantic danger foreground.");
            }

            // Dynamic visibility: collapsing all mutation actions leaves at most one separator before Cancel.
            btnUpdate.Visibility = Visibility.Collapsed;
            btnEnable.Visibility = Visibility.Collapsed;
            btnDisable.Visibility = Visibility.Collapsed;
            btnDelete.Visibility = Visibility.Collapsed;
            VisualAssetChecks.Drain();
            window.UpdateLayout();
            FrameworkElement dockAfterCollapse = Visuals(session.View).OfType<FrameworkElement>().Single(item => item.Name == "CardSelect");
            int visibleDividers = Visuals(dockAfterCollapse).OfType<Border>().Count(border => border.Width == 1 && border.IsVisible);
            Native.Check(visibleDividers == 1, $"Expected exactly 1 visible dock divider when only Cancel remains, found {visibleDividers}.");

            Button cancelButton = Visuals(dockAfterCollapse).OfType<Button>().Single(item => item.Name == "BtnSelectCancel");
            Invoke(cancelButton);
            Native.Check(cancelClicks == 1, "Invoking projected BtnSelectCancel did not trigger the underlying Cancel click handler.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void Main150ControlsProjection()
    {
        Window? window = null;
        try
        {
            var composite = new StackPanel();
            var uniform = new UniformGrid { Rows = 1, Columns = 3 };
            uniform.Children.Add(new MyButton { Text = "全部" });
            uniform.Children.Add(new MyButton { Text = "新增" });
            uniform.Children.Add(new MyButton { Text = "修改" });
            composite.Children.Add(uniform);

            var grid = new DataGrid { Height = 220, IsReadOnly = true, AutoGenerateColumns = false };
            grid.Columns.Add(new DataGridTextColumn { Header = "游戏文件", Binding = new Binding("Key"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding("Value"), Width = 120 });
            grid.ItemsSource = new[] { new KeyValuePair<string, string>("mods/sodium.jar", "保留") };
            composite.Children.Add(grid);

            using GlassPresentationSession controlSession = GlassPresentation.Create(composite);
            window = Host(controlSession.View);
            window.UpdateLayout();
            VisualAssetChecks.Drain();

            UniformGrid projectedUniform = Visuals(controlSession.View).OfType<UniformGrid>().Single();
            DataGrid projectedGrid = Visuals(controlSession.View).OfType<DataGrid>().Single();
            Native.Check(projectedUniform.Columns == 3 && projectedUniform.Children.Count == 3,
                "UniformGrid filter bar from 1.5.0 ModpackDialogController did not preserve column count and children.");
            Native.Check(projectedGrid.Columns.Count == 2 && projectedGrid.Items.Count == 1 && Math.Abs(projectedGrid.ActualHeight - 220) <= 2,
                "DataGrid from 1.5.0 MmcLegacyAdoptionPreviewView did not project columns, items, and bounded height.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
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
