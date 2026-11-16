// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
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
using PCLLF.Presentation.Foundation;
using PCLLF.Presentation.Controls;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassPresentationChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "glass-presentation");
        int animation = ModAnimation.AniControlEnabled;
        ModAnimation.AniControlEnabled++;
        try
        {
            Controls();
            ExtraTextStart(environment);
            GroupedRadioVeto();
            RetiredActions();
            ResourceRows();
            VirtualizedRows();
            ActualSettingsPage(scope.Root);
        }
        finally { ModAnimation.AniControlEnabled = animation; }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS glass presentation: distinct native WPF tree, real source click/check/preview/text/selection/value pipelines, reverse state, dynamic membership and disposal; genuine settings BAML with explicit old-brand exclusions.");
    }

    private static void Controls()
    {
        var source = new StackPanel();
        var button = new MyButton { Name = "ProjectedAction", Text = "实际按钮", Height = 35 };
        var check = new MyCheckBox { Name = "ProjectedToggle", Text = "实际设置", Height = 22 };
        var input = new MyTextBox { Name = "ProjectedInput", Text = "原始文本", HintText = "提示" };
        var combo = new MyComboBox { Name = "ProjectedChoice" };
        combo.Items.Add(new MyComboBoxItem { Content = "第一项" }); combo.Items.Add(new MyComboBoxItem { Content = "第二项" }); combo.SelectedIndex = 0;
        var slider = new MySlider { Name = "ProjectedRange", MaxValue = 100, Value = 25 };
        var icon = new MyIconButton { Name = "ProjectedGlyph", ToolTip = "延迟图标" };
        var iconText = new MyIconTextButton { Name = "ProjectedGlyphText", Text = "实际图标按钮" };
        foreach (UIElement control in new UIElement[] { button, check, input, combo, slider, icon, iconText }) source.Children.Add(control);
        int clicks = 0, checks = 0, customs = 0; bool veto = true;
        button.Click += (_, _) => clicks++;
        check.PreviewChange += (_, args) => args.Handled = veto;
        check.Change += (_, _) => checks++;
        ControlServiceHost host = ControlServices.Current;
        Action<DependencyObject> previousCustom = host.RaiseCustomEvent;
        host.RaiseCustomEvent = sender => { if (ReferenceEquals(sender, button)) customs++; };
        GlassPresentationSession? session = null; Window? window = null;
        try
        {
            session = GlassPresentation.Create(source);
            window = Host(source, session.View);
            NoSharedVisuals(source, session.View);
            Button glyphButton = Find<Button>(session.View, "ProjectedGlyph");
            var glyph = Descendants(glyphButton).OfType<System.Windows.Shapes.Path>().Single();
            var textGlyph = Descendants(Find<Button>(session.View, "ProjectedGlyphText")).OfType<System.Windows.Shapes.Path>().Single();
            Native.Check(glyph.Data is null && textGlyph.Data is null, "Unset source glyphs should have safe empty new paths.");
            icon.Logo = "M0,0 H10 V10 H0 Z"; iconText.Logo = "M0,0 L10,5 0,10 Z"; session.Refresh();
            Native.Check(ReferenceEquals(glyph.Data, icon.Path.Data) && ReferenceEquals(textGlyph.Data, iconText.ShapeLogo.Data) &&
                ReferenceEquals(glyphButton, Find<Button>(session.View, "ProjectedGlyph")), "Delayed source glyphs did not update the existing new actions.");
            Button action = Find<Button>(session.View, "ProjectedAction");
            Native.Check(action.GetType() == typeof(Button) && action.ActualHeight >= 36, "Button is not a new native WPF action with a modern target size.");
            Invoke(action); Native.Check(clicks == 1 && customs == 1, "New action bypassed or duplicated the original click/custom pipeline.");
            CheckBox toggle = Find<CheckBox>(session.View, "ProjectedToggle");
            Toggle(toggle); Native.Check(!check.Checked && toggle.IsChecked == false && checks == 0, "A vetoed original checkbox changed or was not read back by the new toggle.");
            veto = false; Toggle(toggle); Native.Check(check.Checked && toggle.IsChecked == true && checks == 1, "New native toggle did not call the original user-change pipeline exactly once.");
            check.SetChecked(false, false); session.Refresh(); Native.Check(toggle.IsChecked == false, "Original controller checkbox changes did not update the new view.");
            TextBox text = Find<TextBox>(session.View, "ProjectedInput");
            text.Text = "新文本"; VisualAssetChecks.Drain(); Native.Check(input.Text == "新文本", "New input did not reach the genuine validating text control.");
            input.Text = "控制器更新"; session.Refresh(); Native.Check(text.Text == "控制器更新", "Original text state did not update the new input.");
            ComboBox choice = Find<ComboBox>(session.View, "ProjectedChoice");
            choice.SelectedIndex = 1; Native.Check(combo.SelectedIndex == 1 && ReferenceEquals(combo.SelectedItem, combo.Items[1]), "New selection did not preserve the original item identity.");
            combo.SelectedIndex = 0; session.Refresh(); Native.Check(choice.SelectedIndex == 0, "Original selection did not update the new ComboBox.");
            Slider range = Find<Slider>(session.View, "ProjectedRange");
            range.Value = 60; Native.Check(slider.Value == 60, "New slider did not update the original semantic value.");
            slider.Value = 42; session.Refresh(); Native.Check(range.Value == 42, "Original slider did not update the new range control.");
            var later = new MyButton { Name = "DynamicAction", Text = "动态操作" }; source.Children.Add(later); session.Refresh();
            Native.Check(Find<Button>(session.View, "DynamicAction") != null, "A dynamically added original action was not projected.");
            source.Children.Remove(later); session.Refresh(); Native.Check(!Descendants(session.View).OfType<Button>().Any(item => item.Name == "DynamicAction"), "A removed original action remained visible.");
            session.Dispose(); Native.Check(((ContentControl)session.View).Content is null, "Disposal retained the rendered tree.");
        }
        finally
        {
            session?.Dispose(); if (window != null) { window.Content = null; window.Close(); }
            host.RaiseCustomEvent = previousCustom;
            VisualAssetChecks.Drain();
        }
    }

    private static void ExtraTextStart(FixtureEnvironment environment)
    {
        // Exercise the genuine page constructor, BAML and Initialized path. Its normal loader
        // Start sees a genuine finished in-memory cache and therefore creates no worker.
        var originalClient = ModDownload.DlClientListLoader;
        PropertyInfo refreshed = Native.Property(typeof(ModDownloadLib), "IsForgeRecommendedRefreshed");
        object? previousRefresh = refreshed.GetValue(null);
        int unexpectedLoads = 0;
        var cached = new ModLoader.LoaderTask<string, ModDownload.DlClientListResult>("Projection cached client list", _ => { unexpectedLoads++; throw new InvalidOperationException("Cached projection fixture started a worker."); });
        cached.Output = new ModDownload.DlClientListResult { Value = new Newtonsoft.Json.Linq.JObject { ["versions"] = new Newtonsoft.Json.Linq.JArray(
            new Newtonsoft.Json.Linq.JObject { ["id"] = "1.21.1", ["type"] = "release", ["releaseTime"] = new DateTime(2024, 8, 8, 0, 0, 0, DateTimeKind.Utc), ["url"] = "https://invalid.example/release.json" },
            new Newtonsoft.Json.Linq.JObject { ["id"] = "26w01a", ["type"] = "snapshot", ["releaseTime"] = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), ["url"] = "https://invalid.example/snapshot.json" }) } };
        cached.State = ModBase.LoadState.Finished;
        GlassPresentationSession? session = null; Window? window = null;
        try
        {
            ModDownload.DlClientListLoader = cached;
            refreshed.SetValue(null, true);
            var page = new PageDownloadInstall { VanillaName = "1.21.1", VanillaData = new Newtonsoft.Json.Linq.JObject { ["url"] = "https://invalid.example/fixture.json" } };
            Native.Check(page.GetType() == typeof(PageDownloadInstall) && unexpectedLoads == 0 && cached.LastRunningThread is null && cached.State == ModBase.LoadState.Finished,
                "The exact page constructor did not stay on its genuine finished-loader cache path.");
            var start = (MyExtraTextButton)page.FindName("BtnStart");
            ((MyTextBox)page.FindName("TextSelectName")).Text = "ProjectionInstallFixture";
            object controller = Native.Property(typeof(PageDownloadInstall), "Service").GetValue(page)!;
            object host = Native.Get(controller, "host")!;
            int requested = 0, clicked = 0;
            ModDownloadLib.McInstallRequest? captured = null;
            MouseButtonEventArgs? clickArgs = null;
            object? clickSender = null;
            Native.Set(host, "MinecraftFolder", (Func<string>)(() => environment.Data + Path.DirectorySeparatorChar));
            Native.Set(host, "StartRequested", (Action<ModDownloadLib.McInstallRequest>)(request => { requested++; captured = request; }));
            start.Click += (sender, args) => { clicked++; clickSender = sender; clickArgs = args; };
            start.Show = true; start.IsEnabled = true;
            session = GlassPresentation.Create(start);
            window = new Window { Content = session.View, Width = 420, Height = 180, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Button action = Find<Button>(session.View, "BtnStart");
            Native.Check(!page.IsLoaded && !start.IsLoaded && action.IsVisible, "The isolated real install button unexpectedly loaded its backend page.");
            Invoke(action);
            Native.Check(clicked == 1 && requested == 1 && captured?.NewInstanceName == "ProjectionInstallFixture" && captured.MinecraftJson == "https://invalid.example/fixture.json",
                "The new install action did not reach the actual page Click/start-request path exactly once.");
            Native.Check(ReferenceEquals(clickSender, start.PanClick) && clickArgs?.RoutedEvent == UIElement.MouseLeftButtonUpEvent &&
                ReferenceEquals(clickArgs.Source, start.PanClick) && clickArgs.Handled,
                "The projected install action lost its original logical sender, routed-event identity or handled completion.");
            start.Show = false; session.Refresh();
            Native.Check(action.Visibility == Visibility.Collapsed, "An unavailable ExtraText action remained visible after its source Show changed.");
            Native.Check(unexpectedLoads == 0 && cached.LastRunningThread is null, "Isolated install-button activation created loader work.");
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            session?.Dispose();
            if (window != null) { window.Content = null; window.Close(); }
            VisualAssetChecks.Drain();
            ModDownload.DlClientListLoader = originalClient;
            refreshed.SetValue(null, previousRefresh);
        }
    }

    private static void GroupedRadioVeto()
    {
        var source = new StackPanel();
        var first = new MyRadioBox { Name = "RadioBefore", Text = "原选择" };
        var second = new MyRadioBox { Name = "RadioAfter", Text = "候选选择" };
        source.Children.Add(first); source.Children.Add(second); first.SetChecked(true, false);
        bool veto = true; int previews = 0, checks = 0;
        second.PreviewCheck += (_, args) => { previews++; args.Handled = veto; };
        second.Check += (_, _) => checks++;
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View);
            RadioButton before = Find<RadioButton>(session.View, "RadioBefore"), after = Find<RadioButton>(session.View, "RadioAfter");
            Select(after);
            Native.Check(first.Checked && !second.Checked && before.IsChecked == true && after.IsChecked == false && previews == 1 && checks == 0,
                "A vetoed grouped radio did not restore every visible peer to the actual source selection.");
            veto = false; Select(after);
            Native.Check(!first.Checked && second.Checked && before.IsChecked == false && after.IsChecked == true && previews == 2 && checks == 1,
                "A successful grouped radio activation duplicated preview/check or lost source/view grouping.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void RetiredActions()
    {
        var source = new StackPanel(); int clicked = 0, commands = 0, menuClicks = 0;
        var action = new MyButton { Name = "RetiredAction", Text = "可移除操作" };
        action.Click += (_, _) => clicked++;
        var input = new MyTextBox { Name = "RetiredInput", Text = "保留值" };
        var range = new Slider { Name = "RetiredRange", Minimum = 0, Maximum = 100, Value = 12 };
        var toggle = new CheckBox { Name = "RetiredToggle", IsChecked = false };
        var password = new PasswordBox { Name = "RetiredPassword", Password = "fixture-only" };
        var command = new CountingCommand(() => commands++);
        var native = new Button { Name = "RetiredNative", Content = "原生命令", Command = command };
        var menu = new ContextMenu();
        var originalItem = new MenuItem { Header = "可移除菜单", IsCheckable = true, Command = command };
        originalItem.Click += (_, _) => menuClicks++; menu.Items.Add(originalItem); native.ContextMenu = menu;
        foreach (UIElement item in new UIElement[] { action, input, range, toggle, password, native }) source.Children.Add(item);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View); session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Button removed = Find<Button>(session.View, "RetiredAction");
            IInvokeProvider removedInvoke = InvokePattern(removed);
            source.Children.Remove(action); session.Refresh();
            TryRetiredInvoke(removedInvoke); removed.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, removed));
            Native.Check(clicked == 0, "A retained removed-node action still reached its source callback.");
            TextBox oldInput = Find<TextBox>(session.View, "RetiredInput");
            Slider oldRange = Find<Slider>(session.View, "RetiredRange");
            CheckBox oldToggle = Find<CheckBox>(session.View, "RetiredToggle");
            PasswordBox oldPassword = Find<PasswordBox>(session.View, "RetiredPassword");
            Button oldNative = Find<Button>(session.View, "RetiredNative");
            var oldMenu = oldNative.ContextMenu!;
            var oldItem = (MenuItem)oldMenu.Items[0];
            ICommand oldMenuCommand = oldItem.Command!;
            IInvokeProvider pending = InvokePattern(oldNative);
            pending.Invoke(); int beforeDispose = commands;
            session.Dispose(); VisualAssetChecks.Drain();
            TryRetiredInvoke(pending);
            oldInput.Text = "退休后写入"; oldRange.Value = 99; oldToggle.IsChecked = true; oldPassword.Password = "discarded-fixture";
            oldNative.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, oldNative));
            oldMenuCommand.Execute(null); oldItem.IsChecked = true;
            oldItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, oldItem));
            VisualAssetChecks.Drain();
            Native.Check(input.Text == "保留值" && range.Value == 12 && toggle.IsChecked == false && password.Password == "fixture-only",
                "A retired nested input/binding still wrote to the original source state.");
            Native.Check(commands == beforeDispose && menuClicks == 0 && !originalItem.IsChecked && !oldMenuCommand.CanExecute(null),
                "A retained/queued command or menu activation survived owner disposal.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private sealed class CountingCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
    private static IInvokeProvider InvokePattern(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        return (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!;
    }
    private static void TryRetiredInvoke(IInvokeProvider invoke)
    {
        try { invoke.Invoke(); } catch (ElementNotEnabledException) { }
        VisualAssetChecks.Drain();
    }
    private static void Select(RadioButton radio)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(radio) ?? new RadioButtonAutomationPeer(radio);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select(); VisualAssetChecks.Drain();
    }

    private static void ActualSettingsPage(string fixtureRoot)
    {
        var page = new PageSetupUI();
        using GlassPresentationSession session = GlassPresentation.Create(page);
        session.View.Width = 1000; session.View.Height = 650;
        session.View.Measure(new Size(1000, 650)); session.View.Arrange(new Rect(0, 0, 1000, 650)); session.View.UpdateLayout();
        session.Refresh(); session.View.UpdateLayout();
        VisualAssetChecks.Drain();
        // The source BAML/controller objects are retained as semantics, not inserted into this view.
        NoSharedVisuals(page, session.View);
        Native.Check(Descendants(session.View).OfType<ComboBox>().Any(combo => combo.Name == "ComboLauncherColorMode" && combo.GetType() == typeof(ComboBox)), "The actual settings page did not create a new native appearance selector.");
        Native.Check(!Descendants(session.View).OfType<FrameworkElement>().Any(element => element.Name == "PanOfficialTheme"), "The new settings view retained the old official-purchase panel.");
        Native.Check(!Descendants(session.View).OfType<FrameworkElement>().Any(element => element.Name is "CardLogo" or "CheckBackgroundColorful"), "The new settings view retained unused legacy titlebar/rainbow controls.");
        Native.Check(!Descendants(session.View).OfType<CheckBox>().Any(check => AutomationProperties.GetName(check) == "打开启动器时显示 PCL 图标"), "The new settings view retained the old PCL-logo option.");
        Native.Check(Descendants(session.View).OfType<Border>().Any(border => border.Name == "CardLauncherSurface" && border.CornerRadius == new CornerRadius(14)), "The actual settings group did not separate its heading from the new field surface.");
        ComboBox appearance = Find<ComboBox>(session.View, "ComboLauncherColorMode");
        Native.Check(appearance.ActualHeight >= 34 && appearance.ActualHeight <= 38 && Math.Abs(appearance.ActualWidth - 220) < 1,
            "Settings editor lost its compact fixed-height/right-column proportions.");
        FrameworkElement section = Descendants(session.View).OfType<FrameworkElement>().Single(element => element.Name == "CardLauncher");
        Native.Check(section.ActualWidth > 0 && section.ActualWidth <= 800, "Settings sections exceeded the reference-scale content measure.");
        Native.Check(!Descendants(session.View).OfType<PCLLF.Wpf.Glass.GlassSurface>().Any(), "A field/card retained the retired blurred material primitive.");
        Native.Check(session.UnsupportedTypes.Count == 0, "Actual settings page contains unhandled presentation leaves: " + string.Join(", ", session.UnsupportedTypes));
        Native.Check(!page.IsLoaded, "Inspecting new settings presentation ran the original page's Loaded workflow.");
        foreach (int width in new[] { 1120, 810 }) RenderSettings(session.View, fixtureRoot, width);
    }

    private static void VirtualizedRows()
    {
        int first = 0, last = 0;
        var rows = new StackPanel();
        rows.Children.Add(new MyVirtualizingElement(() => { first++; return new MyButton { Name = "RealizedFirst", Text = "首个实际操作" }; }) { Height = 44 });
        rows.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1200, Fill = Brushes.Transparent });
        rows.Children.Add(new MyVirtualizingElement(() => { last++; return new MyButton { Name = "RealizedLast", Text = "末个实际操作" }; }) { Height = 44 });
        var source = new ScrollViewer { Content = rows, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        using GlassPresentationSession session = GlassPresentation.Create(source);
        session.View.Width = 500; session.View.Height = 120;
        Window? window = null;
        try
        {
            Native.Check(first == 0 && last == 0, "Constructing presentation eagerly realized hidden original rows.");
            window = Host(source, session.View);
            VisualAssetChecks.Pump(() => first == 1, "New visible viewport did not realize the first original semantic row.");
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(last == 0 && Find<Button>(session.View, "RealizedFirst") != null, "Offscreen original rows were eagerly initialized or the realized action was not projected.");
            ScrollViewer scroll = Descendants(session.View).OfType<ScrollViewer>().First();
            scroll.ScrollToBottom(); window.UpdateLayout();
            VisualAssetChecks.Pump(() => last == 1, "Scrolling the new viewport did not realize the trailing original row.");
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(first == 1 && last == 1 && Find<Button>(session.View, "RealizedLast") != null, "Semantic virtualization initialized a row more than once or lost its actual new action.");
            NoSharedVisuals(source, session.View);
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void ResourceRows()
    {
        var source = new StackPanel();
        var search = new MySearchBox { Name = "ResourceSearch", HintText = "搜索资源", Text = "实际关键词" };
        var version = new MyListItem { Name = "ResourceVersionRow", Type = MyListItem.CheckType.Clickable, Title = "实际文件", Info = "文件说明，更新于 3 天前，正式版", Tag = new ResourceVersion { ReleaseDate = new DateTime(2026, 7, 20, 12, 0, 0) } };
        var resource = new MyResourceItem { Name = "ResourceProjectRow", Title = "实际资源项目", Description = "实际资源说明" };
        ((TextBlock)resource.FindName("LabTime")).Text = "2026-07-20";
        object controller = Native.Property(typeof(MyResourceItem), "Service").GetValue(resource)!;
        object host = Native.Get(controller, "host")!;
        int opened = 0, clicks = 0;
        Native.Set(host, "CurrentPage", (Func<int>)(() => 0));
        Native.Set(host, "CurrentInstance", (Func<ModMinecraft.McInstance>)(() => null!));
        Native.Set(host, "CurrentArguments", (Func<object[]>)(() => null!));
        Native.Set(host, "OpenDetail", (Action<object[]>)(_ => opened++));
        resource.Click += (_, _) => clicks++;
        source.Children.Add(search); source.Children.Add(version); source.Children.Add(resource);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View);
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Find<FrameworkElement>(session.View, "ResourceSearch").ActualHeight == 32, "Search is not a compact32px native field.");
            FrameworkElement fileRow = Find<FrameworkElement>(session.View, "ResourceVersionRow");
            Native.Check(fileRow.ActualHeight >= 48 && fileRow.ActualHeight <= 56, "Version row no longer uses the reference-scale flat list height.");
            Native.Check(Descendants(fileRow).OfType<TextBlock>().Any(text => text.Text == "2026-07-20") && !Descendants(fileRow).OfType<TextBlock>().Any(text => text.Text.Contains("更新于 3 天前", StringComparison.Ordinal)), "The actual typed release date was not separated into trailing metadata.");
            Button detail = Descendants(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == resource.Title);
            Invoke(detail); Native.Check(opened == 1 && clicks == 1, "New resource row did not execute the genuine detail/Click path exactly once.");
            resource.CanInteraction = false; session.Refresh(); resource.PerformClick();
            Native.Check(!detail.IsEnabled && opened == 1 && clicks == 1, "A noninteractive resource row bypassed the original guard.");
            Button clear = Descendants(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "清空搜索");
            Invoke(clear); Native.Check(search.Text == string.Empty, "The compact clear action did not use the actual search backend.");
            NoSharedVisuals(source, session.View);
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void RenderSettings(FrameworkElement presentation, string fixtureRoot, int width)
    {
        string evidence = Environment.GetEnvironmentVariable("PCLLF_GLASS_EVIDENCE_DIR") ?? fixtureRoot;
        if (evidence != fixtureRoot)
        {
            string full = Path.GetFullPath(evidence);
            DirectoryInfo? directory = new(full); bool allowed = false;
            for (; directory != null; directory = directory.Parent)
                if (directory.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent &&
                    (File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")))) { allowed = true; break; }
            Native.Check(allowed, "Presentation evidence must stay under a Git workspace's artifacts directory.");
            evidence = full;
        }
        Directory.CreateDirectory(evidence);
        var wallpaper = new DrawingImage(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(158, 179, 182), Color.FromRgb(217, 222, 209), 35), null, new RectangleGeometry(new Rect(0, 0, width, 720)))); wallpaper.Freeze();
        var host = new Grid { Width = width, Height = 720 };
        var background = new Image { Source = wallpaper, Stretch = Stretch.Fill, Clip = new RectangleGeometry(new Rect(24, 24, width - 48, 672), 44, 44) };
        host.Children.Add(background);
        var surface = new Border { Margin = new Thickness(24), Padding = new Thickness(24), CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(223, 245, 246, 248)), Child = presentation };
        host.Children.Add(surface);
        presentation.Width = double.NaN; presentation.Height = double.NaN;
        host.Measure(new Size(width, 720)); host.Arrange(new Rect(0, 0, width, 720)); host.UpdateLayout();
        VisualAssetChecks.Drain(); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        string path = Path.Combine(evidence, $"settings-new-{width}.png"); VisualAssetChecks.Save(bitmap, path);
        Console.WriteLine("Actual settings presentation render (synthetic wallpaper; not native backdrop proof): " + path);
        surface.Child = null;
    }

    private static Window Host(FrameworkElement source, FrameworkElement presentation)
    {
        var root = new Grid();
        root.Children.Add(new ContentControl { Content = source, Visibility = Visibility.Hidden, IsHitTestVisible = false });
        root.Children.Add(presentation);
        var window = new Window { Content = root, Width = 850, Height = 650, Left = -30000, Top = -30000, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
        Native.Check(source.IsLoaded && !source.IsVisible && presentation.IsLoaded && presentation.IsVisible, "The semantic backend and new presentation did not have the intended independent lifecycles.");
        return window;
    }
    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void NoSharedVisuals(FrameworkElement source, FrameworkElement presentation)
    {
        var originals = Descendants(source).ToHashSet(ReferenceEqualityComparer.Instance);
        Native.Check(Descendants(presentation).All(item => !originals.Contains(item)), "The new presentation reused an original source UIElement.");
        Native.Check(!Descendants(presentation).OfType<FrameworkElement>().Any(item => item.GetType().Assembly == typeof(MyButton).Assembly && item is not GlassAccountPanel), "An original PCL visual control remains in the new render tree.");
    }
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); VisualAssetChecks.Drain();
    }
    private static void Toggle(CheckBox checkbox)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(checkbox) ?? new CheckBoxAutomationPeer(checkbox);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
        VisualAssetChecks.Drain();
    }
}
