using System.Collections;
using Expression = System.Linq.Expressions.Expression;
using Application = System.Windows.Application;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

// All observable Test-SaveBackupIntegration assertions, using actual Native Main/BAML.
internal static class BackupResourcePageChecks
{
    internal static void Run(Scope scope)
    {
        string game = Path.Combine(scope.Root, "page/minecraft/versions/example"), saves = Path.Combine(game, "saves");
        ModMinecraft.McInstance owner = Instance(game);
        foreach (string name in new[] { "Folder A", "Folder B" }) Put(Path.Combine(saves, name, "level.dat"), "synthetic-world");
        List<JObject> points = [Point("Folder A"), Point("Folder B"), Point("Removed Folder")];
        FrameworkElement page = (FrameworkElement)New("PCL.PageInstanceResources", Kind("Saves"));
        Native.Property(page.GetType(), "Owner").SetValue(page, owner);
        object controller = Native.Get(page, "Controller")!;
        Native.Check(controller.GetType().Assembly.GetName().Name == "PCLCS", "Resources are not controlled by the real migrated backend.");
        Native.Set(controller, "WorldBackupPoints", points);
        Native.Call(page.GetType(), "ShowResources", page, saves, saves, Scan(saves, "Saves"));
        IDictionary groups = (IDictionary)Native.Get(controller, "BackupGroups")!;
        StackPanel worlds = Part<StackPanel>(page, "PanWorlds");
        Native.Check(groups.Count == 3 && worlds.Children.Count == 3, "World and orphan-backup groups were not integrated.");
        FrameworkElement Element(string name) => (FrameworkElement)ReadProperty(groups[name]!, "Element");
        FrameworkElement group = Element("Folder A");
        MyCard card = Part<MyCard>(group, "CardBackups"); card.IsSwapped = false; card.BuildContent();
        MyLocalModItem backupRow = (MyLocalModItem)Part<StackPanel>(group, "PanEntries").Children[0];
        backupRow.Checked = true;
        FrameworkElement restore = Part<FrameworkElement>(page, "BtnBackupRestore"), export = Part<FrameworkElement>(page, "BtnExport");
        Native.Check(restore.Visibility == Visibility.Visible && restore.IsEnabled && export.Visibility == Visibility.Collapsed, "Backup selection did not route the shared footer to backup actions.");
        Native.Check((int)ReadProperty(Native.Get(controller, "SelectedPaths")!, "Count") == 0, "World and backup destructive selections were mixed.");
        MyLocalModItem worldRow = worlds.Children.OfType<StackPanel>().First().Children.OfType<MyLocalModItem>().First();
        worldRow.Checked = true;
        Native.Check(((IEnumerable)ReadProperty(group, "SelectedIds")).Cast<string>().Count() == 0 && restore.Visibility == Visibility.Collapsed && export.Visibility == Visibility.Visible, "World selection retained backup deletion targets.");
        Native.Call(page.GetType(), "ChangeAllSelected", page, true);
        Native.Check((int)ReadProperty(Native.Get(controller, "SelectedPaths")!, "Count") == 2, "Select-all included group containers or backup records.");
        MySearchBox search = Part<MySearchBox>(page, "SearchBox"); search.Text = "Removed Folder";
        Native.Check(worlds.Children.Count == 1 && Part<FrameworkElement>(page, "PanEmpty").Visibility == Visibility.Collapsed, "Backup-only worlds became inaccessible to search.");
        FrameworkElement orphan = Element("Removed Folder");
        Native.Check(!Part<FrameworkElement>(orphan, "BtnCreate").IsEnabled && Part<MyCard>(orphan, "CardBackups").Title.Contains("Removed Folder", StringComparison.Ordinal), "Orphan group permits backing up a missing world or lost its title.");
        search.Text = "";
        PageInstanceLeft left = new();
        Native.Check(left.FindName("ItemBackups") is null, "Separate backup sidebar still exists.");
        object legacy = Native.Enum("PCL.FormMain+PageSubType", "InstanceBackups")!;
        object target = Native.Call(left.GetType(), "PageGet", left, legacy)!;
        object navigation = Native.Call(left.GetType(), "NavigationItem", left, legacy)!;
        Native.Check(target.GetType() == page.GetType() && (string)ReadProperty(navigation, "Title") == "存档", "Legacy backup navigation no longer resolves to saves.");
        Part<FrameworkElement>(page, "PanLoad").Visibility = Visibility.Collapsed;
        Part<FrameworkElement>(page, "PanAllBack").Visibility = Visibility.Visible;
        Part<FrameworkElement>(page, "PanAllBack").Opacity = 1;
        group = Element("Folder A");
        Part<MyCard>(group, "CardBackups").IsSwapped = false; Part<MyCard>(group, "CardBackups").BuildContent();
        foreach (object adapter in groups.Values.Cast<object>()) _ = Part<MyCard>((FrameworkElement)ReadProperty(adapter, "Element"), "CardBackups").MainTextBlock;
        _ = Part<MyCard>(Part<FrameworkElement>(page, "BackupSettings"), "CardSettings").MainTextBlock;
        Grid panel = new() { Background = new SolidColorBrush(Color.FromRgb(228, 239, 250)) };
        panel.Children.Add(page); panel.Measure(new Size(780, 760)); panel.Arrange(new Rect(0, 0, 780, 760)); panel.UpdateLayout();
        Native.Check(worlds.Children.Count == 3, "Actual integrated saves/backup controls failed layout.");
        panel.Children.Clear();
        CheckChangedResources(page, controller, owner, saves, points);
        Console.WriteLine("PASS backup-resources page: every original save/backup integration assertion, shared actions, selection exclusivity, orphan search, native headers/settings and legacy navigation.");
    }

    private static void CheckChangedResources(FrameworkElement page, object controller, ModMinecraft.McInstance owner, string saves, List<JObject> points)
    {
        object host = Native.Get(controller, "Host")!, view = Native.Get(controller, "View")!;
        int scans = 0, fullLoads = 0;
        Delegate scan = (Delegate)Native.Get(host, "Scan")!;
        IList entries = (IList)scan.DynamicInvoke(saves, saves, 0, CancellationToken.None)!;
        Native.Call(page.GetType(), "ShowResources", page, saves, saves, entries);
        Native.Set(host, "CurrentInstance", new Func<ModMinecraft.McInstance>(() => owner));
        Native.Set(host, "BackupPoints", new Func<string, List<JObject>>(_ => points.Select(point => (JObject)point.DeepClone()).ToList()));
        Native.Set(host, "Background", new Action<Action, string>((work, _) => work()));
        Native.Set(host, "Dispatch", new Action<Action>(work => work()));
        Native.Set(host, "Warn", new Action<Exception, string>((error, message) => throw new InvalidOperationException(message, error)));
        // Keep the real cached scan and controller; count whether the full overlay loader is invoked.
        var scanField = Native.Field(host.GetType(), "Scan");
        var args = scanField.FieldType.GetMethod("Invoke")!.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        Native.Set(host, "Scan", Expression.Lambda(scanField.FieldType, Expression.Block(Expression.Invoke(Expression.Constant(new Action(() => scans++))), Expression.Invoke(Expression.Constant(scan), args)), args).Compile());
        var startField = Native.Field(host.GetType(), "StartLoader");
        var request = Expression.Parameter(startField.FieldType.GenericTypeArguments[0]);
        Native.Set(host, "StartLoader", Expression.Lambda(startField.FieldType, Expression.Invoke(Expression.Constant(new Action(() => fullLoads++))), request).Compile());
        StackPanel worlds = Part<StackPanel>(page, "PanWorlds");
        MyLocalModItem RowB() => worlds.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<MyLocalModItem>()).Single(row => ((InstanceResourceEntryData)Native.Get(row, "ResourceEntry")!).Name == "Folder B");
        MyLocalModItem unchanged = RowB(); unchanged.Checked = true;
        object originalEntry = Native.Get(unchanged, "ResourceEntry")!;
        Border root = new();
        Window previous = Application.Current.MainWindow;
        Window window = new() { Content = root, Width = 80, Height = 80, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        Native.Set(view, "Root", root); // Load only the lifecycle sentinel; real page/account workflows stay detached.
        try {
            window.Show(); VisualAssetChecks.Pump(() => root.IsLoaded, "World refresh lifecycle sentinel failed to load.");
            for (int i = 0; i < 3; i++) Native.Call(controller.GetType(), "RefreshChangedResources", controller);
            VisualAssetChecks.Pump(() => scans == 1, "Coalesced world refresh did not complete.");
            Native.Check(fullLoads == 0 && ReferenceEquals(unchanged, RowB()) && ReferenceEquals(originalEntry, Native.Get(RowB(), "ResourceEntry")) && RowB().Checked,
                "Unchanged world refresh reloaded the overlay, metadata, native row or selection.");
            Directory.Move(Path.Combine(saves, "Folder A"), Path.Combine(Path.GetDirectoryName(saves)!, "recycled-A"));
            points.RemoveAll(point => (string?)point["world"] == "Folder A");
            Native.Call(controller.GetType(), "RefreshChangedResources", controller);
            VisualAssetChecks.Pump(() => scans == 2, "World deletion did not refresh the listing.");
            Native.Check(fullLoads == 0 && ((IEnumerable)Native.Get(controller, "Entries")!).Cast<InstanceResourceEntryData>().Count() == 1 && ReferenceEquals(unchanged, RowB()) && RowB().Checked,
                "Deleting a world reloaded unrelated rows or left a deleted world in the listing.");
            Native.Call(controller.GetType(), "RefreshChangedResources", controller);
            Native.Call(controller.GetType(), "PageUnloaded", controller);
            VisualAssetChecks.Pump(() => !(bool)Native.Get(controller, "worldRefreshQueued")!, "Unloading failed to cancel a queued refresh.");
            Native.Check(scans == 2, "A queued world refresh ran after the page was unloaded.");
        } finally {
            window.Close(); window.Content = null; Application.Current.MainWindow = previous;
            Native.Set(view, "Root", page);
        }
    }
}
