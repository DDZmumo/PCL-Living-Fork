using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;
using Application = System.Windows.Application;
using Controller = PCLCS.InstanceNavigationController<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;
using Host = PCLCS.InstanceNavigationHost<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;

namespace PCLLF.Native.Facade.Tests;

// Actual Main BAML and controller. Only the navigation/refresh/Loaded host ports
// are isolated; real radio selection, bindings, mode visibility and palette run.
internal static class InstanceSidebarChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var visual = new VisualAssetChecks.Scope(environment, "instance-sidebar");
        var restore = new Stack<Action>();
        Window? window = null;
        PageInstanceLeft? page = null;
        try
        {
            foreach (string name in new[] { "FrmInstanceLeft", "FrmInstanceOverall", "FrmDownloadModify", "FrmInstanceMod", "FrmInstanceModDisabled" })
            {
                FieldInfo field = Native.Field(typeof(ModMain), name); object? previous = field.GetValue(null);
                restore.Push(() => field.SetValue(null, previous)); field.SetValue(null, null);
            }
            var previousInstance = PageInstanceLeft.Instance; restore.Push(() => PageInstanceLeft.Instance = previousInstance); PageInstanceLeft.Instance = null!;
            PropertyInfo backup = Native.Property(Native.Type("PCL.InstanceClonerUi"), "BeforeMinecraftChangeHandler"); object? oldBackup = backup.GetValue(null);
            restore.Push(() => backup.SetValue(null, oldBackup));
            ModAnimation.AniControlEnabled++;
            Native.Cache("LivingModpackExported", true); Native.Cache("LivingModpackMode", false);
            page = new PageInstanceLeft(); ModMain.FrmInstanceLeft = page;
            var controller = (Controller)Native.Property(page.GetType(), "Controller").GetValue(page)!;
            var host = (Host)Native.Get(controller, "host")!;
            var panel = Part<StackPanel>(page, "PanItem");
            var items = panel.Children.OfType<MyListItem>().ToArray();
            string[] order = ["0", "1", "2", "11", "16", "15", "5", "12", "14", "6", "7", "3", "4", "8", "9", "10"];
            Native.Check(items.Select(item => item.Tag.ToString()).SequenceEqual(order), "Actual sidebar order/tag identity changed.");
            Native.Check(items.All(item => ReferenceEquals(item.Parent, panel)), "Radio items no longer share their native parent.");
            Native.Check(panel.Children.OfType<TextBlock>().Select(label => label.Text).SequenceEqual(new[] { "版本配置", "版本工具", "游戏资源" }), "Native sidebar group order differs.");
            CheckIcons(page);
            // Run the production initializer again: it must retain BAML glyphs
            // and a single correctly routed refresh button per resource/tool.
            controller.InitializeButtons(); controller.InitializeButtons(); CheckIcons(page);
            foreach (int tag in new[] { 6, 7, 8, 9, 10, 11, 12, 14 })
            {
                var item = Item(controller, tag); var buttons = item.Buttons.ToArray();
                Native.Check(buttons.Length == 1 && Convert.ToInt32(buttons[0].Tag) == tag && Equals(buttons[0].ToolTip, "刷新"), "Initializer duplicated or misbound native refresh: " + tag);
            }

            int loaded = 0;
            host.HiddenRefresh = () => loaded++;
            var backdrop = new Grid(); backdrop.SetResourceReference(Panel.BackgroundProperty, "ColorBrushWhite"); backdrop.Children.Add(page);
            window = new Window { Width = 230, Height = 660, SizeToContent = SizeToContent.Manual, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = backdrop };
            window.Show();
            VisualAssetChecks.Pump(() => page.IsLoaded && loaded == 1, "Real sidebar Loaded did not reach its owned host.");
            CheckIcons(page);
            foreach (int tag in order.Select(int.Parse))
            {
                controller.SyncSelection((FormMain.PageSubType)tag);
                Native.Check(page.PageID == (FormMain.PageSubType)tag && Item(controller, tag).Checked && items.Count(item => item.Checked) == 1, "Tag lookup/single selection changed: " + tag);
            }
            CheckRefreshAndLock(page, controller, host, environment);
            CheckGroups(page, controller);
            CheckMode(page, controller);
            CheckLicense();

            var scroll = Part<MyScrollViewer>(page, "ScrollSidebar");
            string? destination = Environment.GetEnvironmentVariable("PCLLF_INSTANCE_SIDEBAR_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(destination)) Directory.CreateDirectory(destination);
            foreach (int mode in new[] { 0, 1 })
            {
                Native.Cache("UiLauncherColorMode", mode); Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
                window.Height = 660; scroll.ScrollToTop(); window.UpdateLayout(); VisualAssetChecks.Drain();
                Native.Check(((SolidColorBrush)Part<TextBlock>(page, "LabVersionTools").Foreground).Color == ((SolidColorBrush)Application.Current.Resources["ColorBrush1"]).Color, "Group label lost its actual theme role.");
                Save(backdrop, destination, mode == 0 ? "sidebar-light.png" : "sidebar-dark.png");
                window.Height = 360; window.UpdateLayout(); scroll.ScrollToEnd();
                VisualAssetChecks.Pump(() => scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "Small native sidebar cannot scroll to its last item.");
                var last = Part<MyListItem>(page, "ItemSchematics");
                Rect bounds = new(last.TranslatePoint(new Point(), page), last.RenderSize);
                Native.Check(bounds.Top >= 0 && bounds.Bottom <= page.ActualHeight + 1 && bounds.Right <= page.ActualWidth + 1, "Last sidebar item is clipped at the scroll end.");
                Save(backdrop, destination, mode == 0 ? "sidebar-small-light-bottom.png" : "sidebar-small-dark-bottom.png");
            }
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS instance-sidebar: actual BAML Initialized/reinitialize/Loaded glyphs, original Tag/radio/refresh/lock routes, live empty-group and modpack visibility, embedded MIT notice and light/dark small-window last-item scrolling; no player profile/instance or network.");
        }
        finally
        {
            if (window != null) { window.Close(); window.Content = null; VisualAssetChecks.Drain(); }
            while (restore.Count > 0) restore.Pop()();
        }
    }

    private static T Part<T>(FrameworkElement page, string name) where T : class => page.FindName(name) as T ?? throw new MissingMemberException(page.GetType().Name, name);
    private static MyListItem Item(Controller controller, int tag) => controller.NavigationItem((FormMain.PageSubType)tag);
    private static void CheckIcons(PageInstanceLeft page)
    {
        var expected = new Dictionary<string, string> { ["ItemKeybindings"] = InstanceSidebarIcons.Keyboard, ["ItemRestorePoints"] = InstanceSidebarIcons.History, ["ItemClone"] = InstanceSidebarIcons.Copy, ["ItemExport"] = InstanceSidebarIcons.Export };
        Native.Check(expected.Values.Distinct().Count() == 4, "Semantic sidebar glyphs are duplicates.");
        foreach (var pair in expected)
        {
            var item = Part<MyListItem>(page, pair.Key);
            Native.Check(item.Logo == pair.Value && item.LogoScale == 1 && item.PathLogo is System.Windows.Shapes.Path path && path.Data.GetArea() > 0 && BindingOperations.IsDataBound(path, System.Windows.Shapes.Path.FillProperty), "Actual BAML/initializer replaced its semantic themed glyph: " + pair.Key);
        }
        Geometry keyboard = Geometry.Parse(expected["ItemKeybindings"]), export = Geometry.Parse(expected["ItemExport"]);
        Native.Check(Part<MyListItem>(page, "ItemPackUpdate").Logo == ModBase.Logo.IconButtonRefresh, "Pack update lost its original PCL refresh glyph.");
        Native.Check(keyboard.FillContains(new Point(12, 6)) && keyboard.FillContains(new Point(6, 10)) && !keyboard.FillContains(new Point(12, 12)), "Stock keyboard stroke/caps became a filled rectangle or lost its keys.");
        Native.Check(export.FillContains(new Point(5, 10)) && !export.FillContains(new Point(9, 10)), "Stock export outline became a solid document.");
    }

    private static void CheckRefreshAndLock(PageInstanceLeft page, Controller controller, Host host, FixtureEnvironment environment)
    {
        var view = new MyPageRight(); int refresh = 0, warnings = 0;
        var reads = host.ReadPage; var specials = host.RefreshSpecial; var instance = host.Instance; var locked = host.Locked; var blocks = host.BlocksNavigation; var hint = host.HintRed; var transitions = host.Transitions;
        try
        {
            host.ReadPage = new(reads) { [11] = () => view, [12] = () => view, [14] = () => view };
            host.RefreshSpecial = new(specials) { [11] = _ => refresh = 11, [12] = _ => refresh = 12, [14] = _ => refresh = 14 };
            var owner = new ModMinecraft.McInstance(Path.Combine(environment.Data, "sidebar-synthetic-instance"));
            host.Instance = () => owner; host.Locked = _ => true; host.BlocksNavigation = _ => false; host.HintRed = _ => warnings++;
            controller.SyncSelection((FormMain.PageSubType)14); controller.PageChange((FormMain.PageSubType)11);
            Native.Check(page.PageID == (FormMain.PageSubType)14 && Item(controller, 14).Checked && !Item(controller, 11).Checked && warnings == 1, "Reordered editor navigation bypassed the native lock or lost selection.");
            host.Locked = _ => false;
            host.Transitions = new() { StopAnimation = _ => { }, PageRight = () => view, PanelChild = () => view, SetPageRight = _ => { }, SetPanelChild = _ => { }, PageExit = _ => { }, PageForceExit = _ => { }, PageEnter = _ => { }, AnimatePage = (first, _, second, _, _, _) => { first(); second(); } };
            foreach (int tag in new[] { 11, 12, 14 })
            {
                var button = Item(controller, tag).Buttons.Single();
                Native.Call(typeof(MyIconButton), "OnClick", button, button, EventArgs.Empty);
                Native.Check(refresh == tag && Item(controller, tag).Checked, "Actual refresh button no longer routes its original Tag: " + tag);
            }
        }
        finally { host.ReadPage = reads; host.RefreshSpecial = specials; host.Instance = instance; host.Locked = locked; host.BlocksNavigation = blocks; host.HintRed = hint; host.Transitions = transitions; }
    }

    private static void CheckGroups(PageInstanceLeft page, Controller controller)
    {
        foreach (var (name, tags) in new[] { ("LabVersionConfiguration", new[] { 1, 2, 11, 16 }), ("LabVersionTools", new[] { 15, 5, 12, 14 }), ("LabGameResources", new[] { 6, 7, 3, 4, 8, 9, 10 }) })
        {
            var label = Part<TextBlock>(page, name); var values = tags.Select(tag => (Item: Item(controller, tag), Visibility: Item(controller, tag).Visibility)).ToArray();
            var binding = BindingOperations.GetMultiBindingExpression(label, UIElement.VisibilityProperty) ?? throw new InvalidOperationException("Header is not bound to actual item visibility.");
            foreach (var pair in values) pair.Item.Visibility = Visibility.Collapsed;
            binding.UpdateTarget(); Native.Check(label.Visibility == Visibility.Collapsed, "Empty native group left a floating heading: " + name);
            values[0].Item.Visibility = Visibility.Hidden; binding.UpdateTarget(); Native.Check(label.Visibility == Visibility.Collapsed, "Hidden item kept its empty heading: " + name);
            values[0].Item.Visibility = Visibility.Visible; binding.UpdateTarget(); Native.Check(label.Visibility == Visibility.Visible, "Returning item did not restore its group heading: " + name);
            foreach (var pair in values) pair.Item.Visibility = pair.Visibility;
            binding.UpdateTarget();
        }
    }

    private static void CheckMode(PageInstanceLeft page, Controller controller)
    {
        var label = Part<TextBlock>(page, "LabVersionTools"); var binding = BindingOperations.GetMultiBindingExpression(label, UIElement.VisibilityProperty)!;
        var export = Part<MyListItem>(page, "ItemExport");
        var packUpdate = Part<MyListItem>(page, "ItemPackUpdate");
        var packVisibility = packUpdate.Visibility;
        Item(controller, 12).Visibility = Visibility.Collapsed; Item(controller, 14).Visibility = Visibility.Collapsed;
        try
        {
            Native.Cache("LivingModpackMode", true); Native.Static("PCL.ModModpackMode", "ApplyInstanceVisibility"); binding.UpdateTarget();
            Native.Check(packUpdate.Visibility == Visibility.Visible && label.Visibility == Visibility.Visible, "Visible pack update lost its tools heading in actual modpack mode.");
            packUpdate.Visibility = Visibility.Collapsed; binding.UpdateTarget();
            Native.Check(export.Visibility == Visibility.Collapsed && label.Visibility == Visibility.Collapsed && ReferenceEquals(Item(controller, 5), Part<MyListItem>(page, "ItemOverall")), "Actual modpack mode left an empty tools group, exposed export or lost its overview fallback.");
            Native.Cache("LivingModpackMode", false); Native.Static("PCL.ModModpackMode", "ApplyInstanceVisibility"); binding.UpdateTarget();
            Native.Check(export.Visibility == Visibility.Visible && label.Visibility == Visibility.Visible && ReferenceEquals(Item(controller, 5), export), "Leaving actual modpack mode did not restore export/tools heading and its original Tag route.");
        }
        finally { Native.Cache("LivingModpackMode", false); packUpdate.Visibility = packVisibility; Item(controller, 12).Visibility = Visibility.Visible; Item(controller, 14).Visibility = Visibility.Visible; binding.UpdateTarget(); }
    }

    private static void CheckLicense()
    {
        using Stream stream = Application.GetResourceStream(new Uri("/LivingPCL;component/resources/tabler-icons-license.txt", UriKind.Relative))!.Stream;
        using var reader = new StreamReader(stream); string text = reader.ReadToEnd();
        Native.Check(text.Contains("MIT License") && text.Contains("Paweł Kuna") && text.Contains("not described as original PCL"), "Supplemental Tabler source/notice was not shipped in the actual Main resources.");
    }

    private static void Save(FrameworkElement view, string? directory, string file)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var pixels = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32); pixels.Render(view);
        VisualAssetChecks.Save(pixels, Path.Combine(directory, file));
    }
}
