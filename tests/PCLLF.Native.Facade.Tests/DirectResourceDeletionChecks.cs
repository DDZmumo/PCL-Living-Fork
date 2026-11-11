using System.Collections;
using System.Windows;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class DirectResourceDeletionChecks {
    internal static void Run(MapInstallResourceScope scope) {
        NativePage(scope);
        string game = Path.Combine(scope.Root, "upgrade-game"), cache = Path.Combine(scope.Root, "shared-cache", "PCL");
        void Put(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); }
        string[] old = { Path.Combine(game, "PCL/ResourceRecycle/Saves/corrupt/content"), Path.Combine(cache, "ResourceRecycle/unknown/content"), Path.Combine(cache, "nested/game/PCL/ResourceRecycle/content") };
        foreach (string path in old) Put(path);
        string backup = Path.Combine(game, "PCL/WorldBackups/keep.zip"); Put(backup);
        Thread worker = (Thread)Native.Static("PCL.LegacyResourceRecycleCleanup", "Schedule", new[] { game }, new[] { cache })!;
        scope.WaitUntil(() => !worker.IsAlive);
        Native.Check(old.All(path => !File.Exists(path)) && File.Exists(backup), "Real scheduled upgrade cleanup missed a bin or touched backups.");

        string outside = Path.Combine(scope.Root, "outside-cache"), keep = Path.Combine(outside, "PCL/ResourceRecycle/keep"); Put(keep);
        EnvironmentHistoryChecks.WithJunction(Path.Combine(cache, "linked-cache"), outside, () => {
            PermanentResourceDeletion.CleanupLegacyCache(cache, default);
            Native.Check(File.Exists(keep), "Cache scan followed an external junction.");
        });
        string parent = Path.Combine(cache, "linked-bin/PCL"); Directory.CreateDirectory(parent);
        EnvironmentHistoryChecks.WithJunction(Path.Combine(parent, "ResourceRecycle"), outside, () => {
            Native.Check(PermanentResourceDeletion.CleanupLegacyCache(cache, default) == 1 && File.Exists(keep), "Bin junction was not unlinked without traversing its target.");
        });
        string gameWithLink = Path.Combine(scope.Root, "linked-game"); Directory.CreateDirectory(gameWithLink);
        EnvironmentHistoryChecks.WithJunction(Path.Combine(gameWithLink, "PCL"), outside, () => {
            try { PermanentResourceDeletion.CleanupLegacy(gameWithLink, default); throw new Exception("Linked PCL ancestor accepted."); }
            catch (IOException) { }
            Native.Check(File.Exists(keep), "Game cleanup followed a linked ancestor.");
        });
        string resources = Path.Combine(scope.Root, "direct-link-resources"); Directory.CreateDirectory(resources);
        EnvironmentHistoryChecks.WithJunction(Path.Combine(resources, "world"), outside, () => {
            PermanentResourceDeletion.Delete(resources, new[] { Path.Combine(resources, "world") }, default);
            Native.Check(File.Exists(keep), "Direct world deletion followed a nested junction.");
        });
        Console.WriteLine("PASS native direct resource deletion: genuine page confirmation/cancel/delete/toast, retired bin entry, scheduled game/cache cleanup and real junction isolation.");
    }

    private static void NativePage(MapInstallResourceScope scope) {
        object kind = Native.Enum("PCL.InstanceResourceKind", "Schematics")!;
        string root = Path.Combine(scope.Root, "direct-resource-page"), path = Path.Combine(root, "selected.schem"), trash = Path.Combine(scope.Root, "unused-recycle");
        Directory.CreateDirectory(root); File.WriteAllText(path, "schematic-fixture");
        FrameworkElement page = (FrameworkElement)Activator.CreateInstance(Native.Type("PCL.PageInstanceResources"), Native.Flags, null, new[] { kind }, null)!;
        scope.ObservePage(page);
        Native.Property(page.GetType(), "Owner").SetValue(page, scope.Instance());
        IList entries = (IList)Native.Static("PCL.InstanceResourceStore", "Scan", root, root, kind, CancellationToken.None)!;
        Native.Call(page.GetType(), "ShowResources", page, root, root, entries);
        object controller = Native.Get(page, "Controller")!, host = Native.Get(controller, "Host")!;
        Native.Check(page.FindName("BtnRestore") is null && page.FindName("BtnRecycleBack") is null && page.GetType().GetMethod("RestoreDeleted", Native.Flags) is null,
            "The retired recycle entry can still be opened.");
        Native.Call(page.GetType(), "ChangeAllSelected", page, true);
        object? message = Native.Get(host, "Message"); int answer = 2, prompts = 0;
        Native.Set(host, "Message", new Func<string, string, string, string, bool, int>((text, title, first, second, warning) => {
            Native.Check(warning && text.Contains("永久删除", StringComparison.Ordinal) && text.Contains("无法从启动器恢复", StringComparison.Ordinal) && first == "删除", "Direct deletion confirmation lost its permanent-delete wording.");
            prompts++; return answer;
        }));
        try {
            Native.Call(controller.GetType(), "DeleteSelected", controller);
            Native.Check(File.Exists(path) && !(bool)Native.Get(controller, "Busy")!, "Canceling deletion changed the selected resource.");
            answer = 1; Native.Call(controller.GetType(), "DeleteSelected", controller);
            scope.WaitUntil(() => !(bool)Native.Get(controller, "Busy")!);
            Native.Check(prompts == 2 && !File.Exists(path) && !Directory.Exists(trash), "Confirmed native deletion retained payloads or created a recycle bin.");
            object hints = Native.Field(typeof(ModMain), "HintWaiting").GetValue(null)!;
            Array values = (Array)Native.Call(hints.GetType(), "ToArray", hints)!;
            Native.Check(values.Cast<object>().Any(value => (string)Native.Get(value, "Text")! == "已永久删除所选资源") &&
                ((FrameworkElement)page.FindName("LabOperation")).Visibility == Visibility.Collapsed, "Successful deletion did not use the native completion toast.");
        } finally { scope.WaitUntil(() => !(bool)Native.Get(controller, "Busy")!); Native.Set(host, "Message", message); }
    }
}
