using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MeloongCore;
using PCL;
using PCLCS;
using Controller = PCLCS.InstanceSettingsController<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.MyListItem, PCL.MyIconButton>;
using Host = PCLCS.InstanceSettingsHost<PCL.ModMinecraft.McInstance>;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcJavaControlsChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        string source = Path.Combine(files.Root, "java-controls"), game = Path.Combine(source, "minecraft");
        Directory.CreateDirectory(game);
        files.SelectMinecraft(game);
        string installed = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, source, game)!;
        var owner = new ModMinecraft.McInstance(installed).Load();
        PageInstanceLeft.Instance = owner;
        var page = new PageInstanceSetup();
        ModMain.FrmInstanceSetup = page;
        var controller = (Controller)Native.Property(typeof(PageInstanceSetup), "Controller").GetValue(page)!;
        var host = (Host)Native.Get(controller, "host")!;
        host.ManagedJavaPreview = (_, _) => new(InstanceJavaPreviewState.Selected,
            "将会使用：Java 26（26.0.2.0）：C:\\Program Files\\Java\\jdk-26.0.2\\bin\\\n" +
            "整合包声明的候选主版本：17、21、25、26。自定义范围仅进一步限制候选，实际选择仍需通过组件、账户类型与平台检查。");
        var kind = (ComboBox)page.FindName("ComboArgumentJava");
        var selection = (FrameworkElement)page.FindName("PanArgumentJavaSelect");
        var hint = (FrameworkElement)page.FindName("HintArgumentJava");
        var grid = (Grid)selection.Parent;
        int animation = ModAnimation.AniControlEnabled;
        try {
            ModAnimation.AniControlEnabled++;
            foreach (int mode in new[] { 0, 1, 2, 3 }) {
                kind.SelectedIndex = mode;
                controller.UpdateJavaUi();
                grid.Measure(new Size(620, double.PositiveInfinity));
                grid.Arrange(new Rect(0, 0, 620, grid.DesiredSize.Height));
                grid.UpdateLayout();
                Native.Check(hint.Visibility == Visibility.Visible, "MMC Java declaration/status disappeared for mode " + mode);
                if (mode == 3) {
                    var choiceBounds = new Rect(selection.TranslatePoint(new Point(), grid), selection.RenderSize);
                    var hintBounds = new Rect(hint.TranslatePoint(new Point(), grid), hint.RenderSize);
                    Native.Check(selection.Visibility == Visibility.Visible && !choiceBounds.IntersectsWith(hintBounds) && hintBounds.Top >= choiceBounds.Bottom,
                        "MMC preview overlaps the native specified-Java selector instead of flowing below it.");
                }
            }
        } finally { ModAnimation.AniControlEnabled = animation; }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-java-controls: genuine settings BAML modes keep the native Java selector and MMC declaration in separate measured rows.");
    }

    internal static void RunSelector(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        string source = Path.Combine(files.Root, "java-selector"), game = Path.Combine(source, "minecraft");
        Directory.CreateDirectory(game); files.SelectMinecraft(game);
        string installed = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, source, game)!;
        var owner = new ModMinecraft.McInstance(installed).Load();
        ConfigProviders.Default = new JsonConfigProvider(Path.Combine(source, "profile.json"));
        var java8 = new Java(Path.Combine(source, "jdk8", "bin"), new Version(8, 0, 491));
        var java26 = new Java(Path.Combine(source, "jdk26", "bin"), new Version(26, 0, 2));
        var java27 = new Java(Path.Combine(source, "jdk27", "bin"), new Version(27, 0, 1));
        Configs.JavaList.Set(new ConcurrentList<Java>([java8, java26, java27]));
        Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
        PageInstanceLeft.Instance = owner;
        Native.Static("PCL.MmcLaunchPreferences", "SetForcedJava", owner, java8);
        var page = new PageInstanceSetup(); ModMain.FrmInstanceSetup = page;
        var controller = (Controller)Native.Property(typeof(PageInstanceSetup), "Controller").GetValue(page)!;
        var host = (Host)Native.Get(controller, "host")!;
        int reloads = 0;
        host.ReloadCurrentJava = () => reloads++;
        host.ManagedJavaPreview = (_, _) => new(InstanceJavaPreviewState.Selected, "MMC Java 预览");
        var kind = (ComboBox)page.FindName("ComboArgumentJava");
        var select = (ComboBox)page.FindName("ComboArgumentJavaSelect");
        var grid = (Grid)((FrameworkElement)page.FindName("PanArgumentJavaSelect")).Parent;
        ((Panel)grid.Parent).Children.Remove(grid);
        int animation = ModAnimation.AniControlEnabled;
        Window? window = null;
        try {
            ModAnimation.AniControlEnabled++;
            kind.SelectedIndex = 3; controller.UpdateJavaUi();
            window = new Window { Content = grid, Width = 700, Height = 270, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            VisualAssetChecks.Pump(() => select.IsLoaded, "Native Java dropdown fragment did not load.");
            controller.UpdateJavaList();
            var choices = select.Items.OfType<MyListItem>().Where(item => item.Tag is Java).ToArray();
            Native.Check(choices.Length == 3 && choices.Any(item => ((Java)item.Tag).Version?.Major == 27),
                "Native Java list silently hid installed future Java versions.");
            int beforeChoice = reloads;
            Click(choices.Single(item => ReferenceEquals(item.Tag, java26)));
            Native.Check(Configs.JavaForced.Get(owner.Config)?.Folder == java26.Folder,
                "Native Java dropdown did not save the MMC forced choice: " + Configs.JavaForced.Get(owner.Config)?.Folder);
            Native.Check(reloads == beforeChoice + 1, "Native Java dropdown preview reload delta: " + (reloads - beforeChoice));
            var paths = MmcInstanceInspector.Read(installed).Paths!;
            var definition = MmcSavedSourceStore.ReadPrimary(paths).Definition;
            var saved = MmcSavedSourceStore.LoadAsync(paths, definition).GetAwaiter().GetResult();
            var preference = (MmcLaunchJavaPreference)Native.Static("PCL.MmcLaunchPreferences", "CaptureJavaPreference", owner, saved.PackageConfiguration)!;
            Native.Check(preference.Force && preference.Path == java26.Folder,
                "Launch preference did not read the Java selected by the existing native control.");
            using var presentation = GlassPresentation.Create(grid);
            var projectedSelect = ProjectedChildren(presentation.View).OfType<ComboBox>().Single(item => item.Name == select.Name);
            var original27 = select.Items.OfType<MyListItem>().Single(item => ReferenceEquals(item.Tag, java27));
            int choiceIndex = select.Items.IndexOf(original27);
            Native.Check(AutomationProperties.GetName((ComboBoxItem)projectedSelect.Items[choiceIndex]) == original27.Title,
                "The glass Java menu did not display the runtime name.");
            projectedSelect.SelectedIndex = choiceIndex;
            Native.Check(projectedSelect.SelectedIndex == -1 && Configs.JavaForced.Get(owner.Config)?.Folder == java27.Folder,
                "Selecting Java in the glass dropdown did not reach the instance's forced-Java setting.");
            saved = MmcSavedSourceStore.LoadAsync(paths, definition).GetAwaiter().GetResult();
            preference = (MmcLaunchJavaPreference)Native.Static("PCL.MmcLaunchPreferences", "CaptureJavaPreference", owner, saved.PackageConfiguration)!;
            Native.Check(preference.Force && preference.Path == java27.Folder, "Launch did not read the glass dropdown choice.");
            var stale = select.Items.OfType<MyListItem>().Single(item => ReferenceEquals(item.Tag, java8));
            int beforeStale = reloads;
            PageInstanceLeft.Instance = new ModMinecraft.McInstance(installed).Load();
            Click(stale);
            Native.Check(reloads == beforeStale && Configs.JavaForced.Get(owner.Config)?.Folder == java27.Folder,
                "A Java dropdown belonging to an old instance selection wrote into the newly selected owner.");
        } finally {
            window?.Close(); if (window is not null) window.Content = null;
            ModAnimation.AniControlEnabled = animation; VisualAssetChecks.Drain();
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-java-selector: actual native dropdown includes Java27, saves through MMC preference capture, reloads preview and rejects a stale owner.");
    }

    private static IEnumerable<DependencyObject> ProjectedChildren(DependencyObject root) {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in ProjectedChildren(child)) yield return descendant;
    }

    private static void Click(MyListItem item) => Native.Call(typeof(MyListItem), "OnClick", item, item,
        new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
}
