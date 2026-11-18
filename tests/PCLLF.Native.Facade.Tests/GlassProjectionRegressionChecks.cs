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
using PCL;
using PCLCS;
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
        try { LaunchSettingsViewport(); ExportOptionCaptions(environment.Data); InlineTitlesAndContextActions(); LazyHelpCardToggle(); FirstManagementInteractions(environment); FloatingSelectionDockLayout(); Main150ControlsProjection(); }
        finally { ModAnimation.AniControlEnabled = animation; }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS projection regressions: genuine launch settings viewport, export option/header text and actions, source inline titles, original instance/mod right-click actions and disposal.");
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
            foreach (int width in new[] { 1000, 810 })
            {
                window.Width = width; window.UpdateLayout(); session.Refresh(); VisualAssetChecks.Drain(); window.UpdateLayout();
                TextBox input = Visuals(session.View).OfType<TextBox>().Single(text => ReferenceEquals(BindingOperations.GetBinding(text, TextBox.TextProperty)?.Source, original));
                input.UpdateLayout();
                Rect glyph = input.GetRectFromCharacterIndex(0, true);
                var viewport = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Native.Check(input.Text == "PCL" && input.ActualHeight >= 34 && input.ActualHeight <= 38 &&
                    !glyph.IsEmpty && glyph.Height > 10 && viewport.ViewportHeight >= glyph.Height - .75,
                    $"Actual launch information text is vertically clipped at {width}: glyph={glyph.Height}, viewport={viewport.ViewportHeight}.");
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
                Invoke(Action(rows[0], "详情")); Invoke(Action(rows[0], "打开文件位置")); Invoke(Action(rows[0], "查看模组依赖关系"));
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
        var about = new PageOtherAbout();
        using GlassPresentationSession aboutSession = GlassPresentation.Create(about);
        Window? window = null;
        try
        {
            window = Host(aboutSession.View);
            Expander[] expanders = Visuals(aboutSession.View).OfType<Expander>().ToArray();
            Native.Check(expanders.Length >= 7 && expanders.All(item => !item.IsExpanded && item.Header is TextBlock { Text.Length: > 0 }),
                $"PageOtherAbout 1.5.0 offline license Expanders did not project collapsed with headers: count={expanders.Length}.");

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
            window.Content = controlSession.View;
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
