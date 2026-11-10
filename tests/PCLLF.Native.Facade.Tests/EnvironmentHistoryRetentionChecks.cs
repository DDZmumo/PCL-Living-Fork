using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.EnvironmentHistoryChecks;

namespace PCLLF.Native.Facade.Tests;

internal static class EnvironmentHistoryRetentionChecks
{
    internal static void Run(Scope scope)
    {
        string mc = Path.Combine(scope.Root, "retention/minecraft"), version = Path.Combine(mc, "versions/example"); ModMinecraft.McInstance instance = Instance(version); Put(Path.Combine(version, "example.jar"), "shared-client");
        Check("T051", Equals(Native.Get(Native.Entries["VersionEnvironmentHistoryAutoCleanup"]!, "DefaultValue"), false)); Cache(instance, "VersionEnvironmentHistoryRetention", 2);
        List<JObject> Current() => Points(instance.PathVersion);
        string CaptureInstance(string label) => (string)History("CaptureInstance", instance, label, None)!;
        bool Manual(JObject point) => (bool)History("IsManualPoint", point)!;
        Put(Path.Combine(version, "mods/library.jar"), "manual-one"); string manualOne = CaptureInstance("手动还原点");
        string manualFile = PointFile(instance.PathVersion, manualOne); JObject legacy = JObject.Parse(File.ReadAllText(manualFile)); legacy.Remove("manual"); Put(manualFile, legacy.ToString());
        List<string> automatic = []; for (int i = 1; i <= 4; i++) { Put(Path.Combine(version, "mods/library.jar"), "auto-" + i); automatic.Add(CaptureInstance("游戏启动前")); }
        Check("T058", Current().Count(p => !Manual(p)) == 4);
        JObject old = Current().Single(p => (string?)p["id"] == automatic[0]); string oldBlob = Blob(instance.PathVersion, old, "g/mods/library.jar"); old.Remove("manual"); Put(PointFile(instance.PathVersion, automatic[0]), old.ToString());
        Put(Path.Combine(version, "mods/library.jar"), "manual-two"); string manualTwo = CaptureInstance("手动还原点"); Cache(instance, "VersionEnvironmentHistoryAutoCleanup", true);
        Put(Path.Combine(version, "mods/library.jar"), "auto-five"); string latest = CaptureInstance("更新模组前");
        List<JObject> points = Current(), autos = points.Where(p => !Manual(p)).ToList();
        Check("T066", autos.Count == 2 && autos.Any(p => (string?)p["id"] == latest) && autos.Any(p => (string?)p["id"] == automatic[3]));
        foreach (string id in new[] { manualOne, manualTwo }) Check("T067", points.Any(p => (string?)p["id"] == id));
        Check("T068", !File.Exists(oldBlob)); foreach (JObject point in points) foreach (JProperty hash in ((JObject)point["files"]!).Properties()) Check("T069", File.Exists(Blob(instance.PathVersion, point, hash.Name)));
        Put(Path.Combine(version, "mods/library.jar"), "recoverable-deleted"); string removed = CaptureInstance("整合包更新前"); JObject deletedPoint = Current().Single(p => (string?)p["id"] == removed); string deletedBlob = Blob(instance.PathVersion, deletedPoint, "g/mods/library.jar");
        string deletedFolder = (string)History("DeletePoints", instance.PathVersion, new[] { removed })!;
        for (int i = 6; i <= 8; i++) { Put(Path.Combine(version, "mods/library.jar"), "auto-" + i); CaptureInstance("游戏启动前"); }
        Check("T074", !File.Exists(deletedBlob) && !File.Exists(Path.Combine(deletedFolder, "points", removed + ".json")) && !Directory.Exists(Path.Combine(deletedFolder, "deleted")));
        int count = Current().Count;
        using (CancellationTokenSource cancel = new()) { cancel.Cancel(); Native.Check(Reject("T077", () => History("CaptureInstance", instance, "游戏启动前", cancel.Token)) is OperationCanceledException, "Retention capture cancellation changed exception kind."); }
        Reject("T078", () => History("PruneAutomatic", instance.PathVersion, 0, latest, None)); Check("T079", Current().Count == count);
        JObject target = Current().Last(p => !Manual(p)); string targetId = (string)target["id"]!; Cache(instance, "VersionEnvironmentHistoryRetention", 1);
        Put(Path.Combine(version, "mods/library.jar"), "before-restore"); Func<string> captureBefore = () => CaptureInstance("恢复前自动保存");
        string undo = Restore(instance.PathVersion, instance.PathIndie, mc, targetId, captureBefore: captureBefore); Check("T086", File.ReadAllText(Path.Combine(version, "mods/library.jar")) == "auto-7");
        History("ApplyRetention", instance, undo, None); Check("T088", Current().Count(p => !Manual(p)) == 1 && Current().Any(p => (string?)p["id"] == undo));
        Restore(instance.PathVersion, instance.PathIndie, mc, undo, captureBefore: captureBefore); Check("T090", File.ReadAllText(Path.Combine(version, "mods/library.jar")) == "before-restore");
        string valid = File.ReadAllText(manualFile); JObject bad = JObject.Parse(valid); bad["files"]!["g/mods/library.jar"] = "sha1:invalid"; Put(manualFile, bad.ToString()); count = Current().Count;
        try { Reject("T093", () => History("PruneAutomatic", instance.PathVersion, 1, undo, None)); Check("T094", Current().Count == count); }
        finally { Put(manualFile, valid); }
        RetentionPage(instance, mc, Current);
        Console.WriteLine("PASS environment-history retention: opt-in/defaults, legacy manuals, newest automatic limit, permanently deleted/shared objects, cancel/invalid-prune atomicity, target/undo protection and real owner-scoped page.");
    }

    private static void RetentionPage(ModMinecraft.McInstance instance, string mc, Func<List<JObject>> points)
    {
        FrameworkElement page = (FrameworkElement)New("PCL.PageInstanceRestorePoints"); PageState(page, "Owner", instance); Cache(instance, "VersionEnvironmentHistoryAutoCleanup", false);
        Native.Call(page.GetType(), "BindRetention", page);
        MyCheckBox cleanup = Part<MyCheckBox>(page, "CheckAutoCleanup"); MyTextBox retention = Part<MyTextBox>(page, "TextRetention");
        Check("T100", !cleanup.Checked && Part<FrameworkElement>(page, "PanRetention").Visibility == Visibility.Collapsed);
        int count = points().Count; cleanup.Checked = true; Native.Call(page.GetType(), "AutoCleanupChanged", page, null, true);
        Check("T103", (bool)Cache(instance, "VersionEnvironmentHistoryAutoCleanup")! && Part<FrameworkElement>(page, "PanRetention").Visibility == Visibility.Visible && points().Count == count);
        retention.Text = "3"; PageState(page, "RetentionDirty", true); Check("T105", (bool)Native.Call(page.GetType(), "CommitRetention", page)! && (int)Cache(instance, "VersionEnvironmentHistoryRetention")! == 3);
        retention.Text = "0"; PageState(page, "RetentionDirty", true); Check("T107", !(bool)Native.Call(page.GetType(), "CommitRetention", page)! && retention.Text == "3");
        _ = Part<MyCard>(page, "CardRetention").MainTextBlock;
        ModMinecraft.McInstance other = Instance(Path.Combine(mc, "versions/other")); Native.Check(!(bool)Cache(other, "VersionEnvironmentHistoryAutoCleanup")! && (int)Cache(other, "VersionEnvironmentHistoryRetention")! == 10, "Retention edits or cleanup opt-in escaped their instance owner.");
        List<JObject> actual = points(); string healthy = (string)actual[0]["id"]!; History("MarkHealthy", instance.PathVersion, healthy, true); actual = points();
        PageState(page, "Entries", actual); Native.Call(page.GetType(), "RenderList", page);
        object controller = Native.Get(page, "Controller")!;
        Native.Check(controller.GetType().Assembly.GetName().Name == "PCLCS" && ReferenceEquals(Native.Get(controller, "Entries"), actual), "Restore-point page copied backend state instead of using its real controller.");
        StackPanel rows = Part<StackPanel>(page, "PanEntries"); Native.Check(rows.Children.Count == actual.Count && rows.Children.Cast<object>().All(item => item is MyLocalModItem row && row.Buttons.Count() == 4 && actual.Any(point => ReferenceEquals(point, Native.Get(row, "RestorePoint")))), "Restore-point rendering lost real rows/actions/point identity.");
        Native.Call(page.GetType(), "ChangeAllSelected", page, true); Native.Check(rows.Children.OfType<MyLocalModItem>().All(row => row.Checked) && Part<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Visible, "Restore-point bulk selection did not reach actual rows/footer.");
        Native.Call(page.GetType(), "ChangeAllSelected", page, false); Native.Check(Part<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Collapsed, "Restore-point deselection retained destructive targets.");
        MyLocalModItem first = (MyLocalModItem)rows.Children[0]; first.Checked = true; Native.Check(Part<FrameworkElement>(page, "BtnRestore").IsEnabled, "Single point selection did not enable the real restore entry.");
        MySearchBox search = Part<MySearchBox>(page, "SearchBox"); search.Text = "unmatched-environment-fixture";
        Native.Check(rows.Children.Count == 0 && Part<TextBlock>(page, "LabEmpty").Visibility == Visibility.Visible && Part<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Collapsed, "Restore-point search/empty state retained hidden selected points."); search.Text = "";
        Part<MyRadioButton>(page, "FilterHealthy").Checked = true; Native.Check(rows.Children.Count == 1 && (bool)((JObject)Native.Get(rows.Children[0], "RestorePoint")!)["healthy"]!, "Healthy-only filter did not select the actual marked point.");
        Part<MyRadioButton>(page, "FilterAll").Checked = true; Native.Check(rows.Children.Count == actual.Count, "Restoring the all-points filter lost rows.");
    }
}
