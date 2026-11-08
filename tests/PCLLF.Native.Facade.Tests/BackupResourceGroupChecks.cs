using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

// Observable Test-WorldBackupGroups behavior, without invoking external viewers or destructive UI commands.
internal static class BackupResourceGroupChecks
{
    private static int Selected(object group) => ((IEnumerable)ReadProperty(group, "SelectedIds")).Cast<string>().Count();
    internal static void Run(Scope scope)
    {
        string game = Path.Combine(scope.Root, "groups/minecraft/versions/fixture"), otherGame = Path.Combine(scope.Root, "groups/other/versions/fixture");
        ModMinecraft.McInstance owner = Instance(game), otherOwner = Instance(otherGame);
        Put(Path.Combine(game, "saves/Folder A/level.dat"), "fixture-world-a");
        Put(Path.Combine(game, "saves/Folder B/level.dat"), "fixture-world-b");
        Put(Path.Combine(otherGame, "saves/Folder A/level.dat"), "fixture-world-a");
        string before = File.ReadAllText(Path.Combine(game, "PCL/Setup.ini")), otherBefore = File.ReadAllText(Path.Combine(otherGame, "PCL/Setup.ini"));
        List<JObject> points = [Point("Folder A"), Point("Folder A", 1), Point("Folder B")];
        FrameworkElement group = (FrameworkElement)New("PCL.WorldBackupGroup");
        MyCard bar = new();
        Native.Property(group.GetType(), "SelectionBar").SetValue(group, bar);
        Native.Call(group.GetType(), "Bind", group, owner, "Folder A", points);
        MyCard card = Part<MyCard>(group, "CardBackups");
        StackPanel rows = Part<StackPanel>(group, "PanEntries");
        Native.Check(card.CanSwap && card.IsSwapped && ReferenceEquals(card.SwapControl, Part<FrameworkElement>(group, "PanContent")) && card.Title == "备份（2）" && rows.Children.Count == 0, "Backup group lost collapsed native MyCard and lazy rows.");
        Native.Check((string)ReadProperty(group, "FolderName") == "Folder A" && Part<FrameworkElement>(group, "BtnCreate").IsEnabled, "Per-world identity/create action unavailable.");
        card.IsSwapped = false;
        Native.Check(rows.Children.Count == 2 && rows.Children.Cast<object>().All(row => row.GetType().FullName == "PCL.MyLocalModItem"), "Expanded backup rows do not use actual Main MyLocalModItem.");
        foreach (MyLocalModItem row in rows.Children) {
            Native.Check(ReferenceEquals(Native.Get(row, "SelectionBar"), bar), "Parent selection bar identity changed.");
            JObject record = (JObject)Native.Get(row, "RestorePoint")!;
            Native.Check((string?)record["world"] == "Folder A" && row.Buttons.Count() == 4, "Equal display names mixed physical worlds or lost row actions.");
        }
        int events = 0;
        Delegate selection = Subscribe(group, "SelectionChanged", () => events++);
        try {
            MyLocalModItem row = (MyLocalModItem)rows.Children[0];
            row.Checked = true;
            Native.Check(Selected(group) == 1 && events == 1, "Backup selection did not notify its parent exactly once.");
            Native.Call(group.GetType(), "Bind", group, owner, "Folder A", points.Select(point => (JObject)point.DeepClone()).ToList());
            Native.Check(ReferenceEquals(row, rows.Children[0]) && row.Checked && Selected(group) == 1 && events == 1 && !card.IsSwapped,
                "Refreshing unchanged backup records rebuilt native rows or lost selection/expansion.");
            Native.Call(group.GetType(), "ClearSelection", group, false);
            Native.Check(Selected(group) == 0 && !row.Checked && events == 1, "Silent backup deselection was not silent or failed to clear rows.");
            row.Checked = true;
            object snapshot = ReadProperty(group, "SelectedIds");
            Native.Call(group.GetType(), "ClearSelection", group, true);
            Native.Check(Selected(group) == 0 && ((IEnumerable)snapshot).Cast<string>().Count() == 1 && events == 3, "Selected IDs were a mutable alias or notified deselection failed.");
        } finally { Unsubscribe(group, "SelectionChanged", selection); }
        Native.Call(group.GetType(), "RememberExpansion", group);
        FrameworkElement Rebind(ModMinecraft.McInstance instance, string name, List<JObject> data) {
            FrameworkElement result = (FrameworkElement)New("PCL.WorldBackupGroup"); Native.Call(result.GetType(), "Bind", result, instance, name, data); return result;
        }
        FrameworkElement rebound = Rebind(owner, "Folder A", points);
        Native.Check(!Part<MyCard>(rebound, "CardBackups").IsSwapped && Part<StackPanel>(rebound, "PanEntries").Children.Count == 2, "Expansion did not survive a group rebuild.");
        FrameworkElement independent = Rebind(otherOwner, "Folder A", points);
        Native.Check(Part<MyCard>(independent, "CardBackups").IsSwapped, "Expansion leaked to a different game folder.");
        FrameworkElement worldB = Rebind(owner, "Folder B", points);
        Native.Check(Part<MyCard>(worldB, "CardBackups").IsSwapped && Part<MyCard>(worldB, "CardBackups").Title == "备份（1）", "Expansion or grouping leaked between worlds.");
        FrameworkElement ghost = Rebind(owner, "Deleted folder", [Point("Deleted folder")]);
        Native.Check(!Part<FrameworkElement>(ghost, "BtnCreate").IsEnabled && Part<TextBlock>(ghost, "LabState").Text.Contains("已不存在", StringComparison.Ordinal), "Backup-only world allows a capture or lost missing-world state.");
        Part<MyCard>(ghost, "CardBackups").IsSwapped = false;
        Native.Check(Part<StackPanel>(ghost, "PanEntries").Children.Count == 1 && ((MyLocalModItem)Part<StackPanel>(ghost, "PanEntries").Children[0]).Buttons.Count() == 4, "Deleted-world backups lost restore actions.");

        FrameworkElement settings = (FrameworkElement)New("PCL.WorldBackupSettings");
        Native.Call(settings.GetType(), "BindOwner", settings, owner);
        MyCard settingCard = Part<MyCard>(settings, "CardSettings");
        MyCheckBox automatic = Part<MyCheckBox>(settings, "CheckAuto");
        MyTextBox retention = Part<MyTextBox>(settings, "TextRetention");
        Native.Check(settingCard.CanSwap && settingCard.IsSwapped && ReferenceEquals(settingCard.SwapControl, Part<FrameworkElement>(settings, "PanSettings")), "Backup settings lost the collapsed native card.");
        Native.Check(settings.IsEnabled && automatic.Checked && retention.Text == "5", "World-backup instance defaults failed to bind.");
        retention.Text = "9";
        Native.Call(settings.GetType(), "CommitPending", settings);
        automatic.Checked = false;
        Native.Call(settings.GetType(), "AutoChanged", settings, automatic, false);
        Native.Check((int)Cache(owner, "VersionWorldBackupRetention")! == 5 && (bool)Cache(owner, "VersionWorldBackupAutoEnabled")!, "Programmatic settings changes wrote instance settings.");
        Native.Call(settings.GetType(), "BindOwner", settings, otherOwner);
        Native.Call(settings.GetType(), "BindOwner", settings, owner);
        Text(Path.Combine(game, "PCL/Setup.ini"), before); Text(Path.Combine(otherGame, "PCL/Setup.ini"), otherBefore);
        // Same dirty flag as the original offscreen fixture; no fake keyboard-focus state.
        retention.Text = "7"; Native.Set(settings, "RetentionDirty", true);
        Native.Check((bool)Native.Call(settings.GetType(), "CommitPending", settings)! && (int)Cache(owner, "VersionWorldBackupRetention")! == 7 && (int)Cache(otherOwner, "VersionWorldBackupRetention")! == 5, "User retention change escaped its owner.");
        retention.Text = "51"; Native.Set(settings, "RetentionDirty", true);
        Native.Check(!(bool)Native.Call(settings.GetType(), "CommitPending", settings)! && retention.Text == "7" && (int)Cache(owner, "VersionWorldBackupRetention")! == 7, "Invalid backup retention was persisted.");
        automatic.Checked = false;
        Native.Call(settings.GetType(), "AutoChanged", settings, automatic, true);
        Native.Check(!(bool)Cache(owner, "VersionWorldBackupAutoEnabled")! && (bool)Cache(otherOwner, "VersionWorldBackupAutoEnabled")!, "User automatic-backup switch affected another owner.");
        Native.Call(settings.GetType(), "BindOwner", settings, new object?[] { null });
        Native.Check(!settings.IsEnabled, "Unbound backup settings remain interactive.");
        var parameter = Native.Method(Native.Type("PCL.WorldBackupsUi"), "Start").GetParameters()[^1];
        Native.Check(parameter.Name == "WorldIdentifier" && parameter.IsOptional, "Per-world backup entry point lost its optional ABI.");
        Native.Check(Native.Get((MyLocalModItem)rows.Children[0], "RestorePoint") is JObject && group.FindName("CardSelect") is null, "Backup group replaced native records or duplicated parent selection UI.");
        Console.WriteLine("PASS backup-resources groups: original lazy/native rows, immutable selection, exact notifications, expansion scopes, orphan actions, read-only binding, user-only owner-scoped setting commits and optional ABI.");
    }
}
