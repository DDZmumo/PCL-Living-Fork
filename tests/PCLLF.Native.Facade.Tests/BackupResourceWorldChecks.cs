using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

// Real streamed ZIPs and original WorldBackups transaction/session failure points.
internal static class BackupResourceWorldChecks
{
    internal static void Run(Scope scope)
    {
        object service = Native.Field(Native.Type("PCL.WorldBackups"), "Service").GetValue(null)!;
        Native.Check(service.GetType().Assembly.GetName().Name == "PCLCS" && service.GetType().GetGenericTypeDefinition().FullName == "PCLCS.WorldBackupEngine`1" && service.GetType().GetGenericArguments()[0] == typeof(ModMinecraft.McInstance), "World-backup facade is not bound to the actual Core engine and Main model.");
        string game = Path.Combine(scope.Root, "engine/minecraft/versions/fixture"), world = Path.Combine(game, "saves/World A"), other = Path.Combine(game, "saves/World B");
        ModMinecraft.McInstance instance = Instance(game);
        Cache(instance, "VersionWorldBackupRetention", 50); Cache(instance, "VersionWorldBackupAutoEnabled", true);
        Put(Path.Combine(world, "level.dat"), "world-old"); Put(Path.Combine(world, "region/r.0.0.mca"), "region-old");
        Put(Path.Combine(world, "session.lock"), "fixture-lock"); Directory.CreateDirectory(Path.Combine(world, "empty-folder"));
        Put(Path.Combine(other, "level.dat"), "other-world-keep");
        string first = Capture(game, "World A", retain: 5), otherId = Capture(game, "World B", retain: 5);
        List<JObject> points = Points(game); JObject point = points.Single(p => (string?)p["id"] == first);
        Native.Check(points.Count == 2 && (string?)point["world"] == "World A" && (string?)point["reason"] == "manual" && (long)point["bytes"]! == 19 && point["created"] is not null, "Backup preview metadata is incomplete.");
        Native.Check(point["files"]!["session.lock"] is null, "Runtime session.lock was archived.");
        string store = (string)World("Store", game)!;
        Native.Check(!Native.Full(store).StartsWith(Native.Full(game), StringComparison.OrdinalIgnoreCase), "Backup store is inside exportable game contents.");
        Put(Path.Combine(world, "level.dat"), "world-progress"); Put(Path.Combine(world, "region/r.0.0.mca"), "region-progress"); Put(Path.Combine(world, "added/new.txt"), "later-file");
        string undo = Restore(game, first, 5);
        Text(Path.Combine(world, "level.dat"), "world-old"); Text(Path.Combine(world, "region/r.0.0.mca"), "region-old"); Text(Path.Combine(other, "level.dat"), "other-world-keep");
        Native.Check(!File.Exists(Path.Combine(world, "added/new.txt")) && Directory.Exists(Path.Combine(world, "empty-folder")) && !string.IsNullOrEmpty(undo), "Restore lost removals, empty folders or safety snapshot.");
        Restore(game, undo, 5); Text(Path.Combine(world, "level.dat"), "world-progress"); Text(Path.Combine(world, "added/new.txt"), "later-file");
        foreach (int step in new[] { 1, 2 }) {
            IOException expected = new("injected transaction failure");
            Exception observed = Reject(() => Restore(game, first, commit: index => { if (index == step) throw expected; }), "restore failure at swap " + step);
            Native.Check(ReferenceEquals(observed, expected), "Restore changed the injected exception identity.");
            Text(Path.Combine(world, "level.dat"), "world-progress"); Text(Path.Combine(world, "added/new.txt"), "later-file");
        }
        using (CancellationTokenSource cancel = new()) {
            Exception observed = Reject(() => Restore(game, first, token: cancel.Token, commit: index => { if (index == 1) cancel.Cancel(); }), "canceled restore");
            Native.Check(observed is OperationCanceledException, "Restore cancellation changed exception category.");
            Text(Path.Combine(world, "level.dat"), "world-progress");
        }
        Put(Path.Combine(world, "session.lock"), "fixture-lock");
        using (FileStream held = new(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Reject(() => Capture(game, "World A", "locked"), "capture of a live session lock");
            Reject(() => Restore(game, first), "restore of a live session lock");
        }
        using (Process self = Process.GetCurrentProcess()) {
            string session = Path.Combine(scope.Profile, "world-backup-sessions/fixture.json");
            Put(session, new JObject { ["gameFolder"] = game, ["pid"] = self.Id, ["started"] = self.StartTime.ToUniversalTime().Ticks }.ToString());
            Native.Check((bool)World("HasActiveGame", game, null)!, "Persisted live process session was ignored.");
            Reject(() => Restore(game, first), "restoring a persisted running game"); File.Delete(session);
            WatcherSession(scope, instance, game, self);
        }
        int count = Points(game).Count;
        Native.Check(((IEnumerable)World("BeforeMinecraftChange", instance, "1.20.1", "1.20.1", None)!).Cast<object>().Count() == 0 && Points(game).Count == count, "Unchanged Minecraft version triggered backups.");
        Native.Check(((IEnumerable)World("BeforeMinecraftChange", instance, "1.20.1", "1.21.1", None)!).Cast<object>().Count() == 2 && Points(game).Count == count + 2, "Minecraft upgrade did not capture both worlds first.");
        Cache(instance, "VersionWorldBackupAutoEnabled", false);
        try { Native.Check(((IEnumerable)World("BeforeMinecraftChange", instance, "1.20.1", "1.21.1", None)!).Cast<object>().Count() == 0, "Automatic-backup disable was ignored."); }
        finally { Cache(instance, "VersionWorldBackupAutoEnabled", true); }
        Recovery(game, world, store, first);
        string[] before = Points(game).Select(p => (string)p["id"]!).ToArray();
        using (CancellationTokenSource canceled = new()) {
            canceled.Cancel(); Native.Check(Reject(() => Capture(game, "World A", "canceled", 1, canceled.Token), "pre-canceled capture") is OperationCanceledException, "Capture cancellation changed exception category.");
        }
        using (CancellationTokenSource streaming = new()) {
            bool entered = false;
            Action<double> report = value => { if (value > 0.03 && value < 0.97) { entered = true; streaming.Cancel(); } };
            Exception observed = Reject(() => World("CaptureWorldWithProgress", game, "World A", "canceled-during-io", 1, streaming.Token, report, null), "streaming capture cancellation");
            Native.Check(entered && observed is OperationCanceledException && before.SequenceEqual(Points(game).Select(p => (string)p["id"]!)), "Canceled streaming capture published data or pruned an existing point.");
            Native.Check(!Directory.EnumerateDirectories(store, ".pcl-backup-*").Any(), "Canceled streaming capture retained staging data.");
        }
        string blocked = Path.Combine(scope.Root, "blocked-store"); Put(blocked, "not-a-directory");
        Native.Property(Native.Type("PCL.WorldBackups"), "TestRoot").SetValue(null, blocked);
        try { Reject(() => Capture(game, "World A", "filesystem failure", 1), "file instead of backup-store directory"); }
        finally { Native.Property(Native.Type("PCL.WorldBackups"), "TestRoot").SetValue(null, scope.Backups); }
        Native.Check(before.SequenceEqual(Points(game).Select(p => (string)p["id"]!)), "Failed/canceled capture pruned existing backups.");
        Reject(() => Capture(game, "../escape", "traversal"), "world path traversal");
        Reject(() => Restore(game, "../escape"), "backup ID traversal");
        Reject(() => Capture(game, "World A", "invalid retention", 0), "invalid retention");
        Junction(scope, game);
        string corrupt = Capture(game, "World A", "corrupt-fixture"); Put(Path.Combine(store, "points", corrupt, "world.zip"), "broken zip");
        count = Points(game).Count; Reject(() => Restore(game, corrupt), "corrupt ZIP restore");
        Native.Check(Points(game).Count == count, "Corrupt restore saved or pruned a point before validating its ZIP."); Text(Path.Combine(world, "level.dat"), "world-progress");
        string bad = Capture(game, "World A", "malicious-path-fixture"), manifest = Path.Combine(store, "points", bad, "manifest.json");
        JObject damaged = JObject.Parse(File.ReadAllText(manifest)); JProperty property = ((JObject)damaged["files"]!).Properties().First(); JToken value = property.Value.DeepClone(); property.Remove(); ((JObject)damaged["files"]!).Add("../escape.txt", value); Put(manifest, damaged.ToString());
        Reject(() => Restore(game, bad), "tampered path manifest");
        Native.Check(!File.Exists(Path.Combine(scope.Root, "escape.txt")), "Manifest path traversal escaped staging.");
        Retention(game, world, store, first, otherId);
        Native.Check(!Directory.EnumerateDirectories(Path.Combine(game, "saves"), ".pcl-world-*").Any(), "Backup checks left restore staging directories.");
        Console.WriteLine("PASS backup-resources engine: real archives/restore/undo, failure/cancel rollback, locks/live/exited processes, upgrade scope, recovery ownership, corruption/traversal, retention and recoverable deletion.");
    }

    private static void WatcherSession(Scope scope, ModMinecraft.McInstance instance, string game, Process self)
    {
        Type module = Native.Type("PCL.ModWatcher"), type = Native.Type("PCL.ModWatcher+Watcher");
        IList watchers = (IList)Native.Field(module, "McWatcherList").GetValue(null)!;
        object watch = RuntimeHelpers.GetUninitializedObject(type);
        var native = Native.Field(type, "Native");
        native.SetValue(watch, Activator.CreateInstance(native.FieldType, watch, Native.Field(module, "Host").GetValue(null)!, Native.Field(module, "Group").GetValue(null)!));
        Native.Set(watch, "GameProcess", self); Native.Set(watch, "Instance", instance);
        watchers.Add(watch);
        try { Native.Check((bool)World("HasActiveGame", game, null)!, "Active genuine Watcher was ignored."); }
        finally { watchers.Remove(watch); }
        object? oldFlag = Native.Field(module, "HasRunningMinecraft").GetValue(null);
        using IdleProcess child = new();
        try {
            World("Track", instance, child.Value);
            Put(Path.Combine(scope.Profile, "world-backup-sessions", child.Value.Id + "-1.json"), new JObject { ["gameFolder"] = Path.Combine(scope.Root, "stale-pid-game"), ["pid"] = child.Value.Id, ["started"] = 1 }.ToString());
            Native.Check((bool)World("HasActiveGame", game, null)!, "Tracked live process did not block capture.");
            child.Finish(); Native.Set(watch, "GameProcess", child.Value); watchers.Add(watch); Native.Field(module, "HasRunningMinecraft").SetValue(null, true);
            Native.Check(!(bool)World("HasActiveGame", game, child.Value)!, "Exited Watcher was treated as a live game.");
            int count = Points(game).Count;
            Cache(instance, "VersionArgumentIndieV2", false);
            try { World("OnProcessStopped", instance, child.Value, true); }
            finally { Cache(instance, "VersionArgumentIndieV2", true); }
            Native.Check(Points(game).Count == count + 2, "Normal exit lost the launched folder when current isolation changed, stale PID record existed and legacy running flag remained set.");
            World("OnProcessStopped", instance, child.Value, false);
            Native.Check(Points(game).Count == count + 2, "Abnormal exit triggered automatic backups.");
        } finally { watchers.Remove(watch); Native.Field(module, "HasRunningMinecraft").SetValue(null, oldFlag); }
    }

    private static void Recovery(string game, string world, string store, string first)
    {
        string transaction = Guid.NewGuid().ToString("N"), previous = Path.Combine(game, "saves/.pcl-world-" + transaction + ".before"), stage = Path.Combine(game, "saves/.pcl-world-" + transaction + ".stage");
        Directory.CreateDirectory(stage);
        JObject saved = (JObject)World("Metadata", game, first)!;
        World("ReadArchive", Path.Combine(store, "points", first), saved, None, stage);
        string path = Path.Combine(store, "transactions", transaction + ".json");
        JObject journal = new() { ["gameFolder"] = game, ["world"] = "World A", ["point"] = first, ["existed"] = true, ["complete"] = false };
        Put(path, journal.ToString()); Directory.Move(world, previous); Directory.Move(stage, world);
        using (IdleProcess owner = new()) {
            journal["ownerPid"] = owner.Value.Id; journal["ownerStarted"] = owner.Value.StartTime.ToUniversalTime().Ticks; Put(path, journal.ToString());
            Reject(() => World("RecoverBeforeLaunch", game, None), "taking over a live foreign restore owner");
            Native.Check(Directory.Exists(previous), "Active foreign restore was modified."); owner.Finish();
        }
        World("RecoverBeforeLaunch", game, None);
        Text(Path.Combine(world, "level.dat"), "world-progress"); Text(Path.Combine(world, "added/new.txt"), "later-file");
        Native.Check(!Directory.Exists(previous) && !Directory.Exists(stage) && !File.Exists(path), "Interrupted restore rollback did not clean its own journal/staging.");
    }

    private static void Junction(Scope scope, string game)
    {
        string outside = Path.GetFullPath(Path.Combine(scope.Root, "outside")).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string junction = Path.GetFullPath(Path.Combine(game, "saves", "Linked")).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // cmd builtins parse '/' inside unquoted paths as switches; normalize and
        // quote both paths explicitly. Reject cmd expansion before any process starts.
        foreach (string path in new[] { junction, outside }) Native.Check(path.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) < 0, "Unsafe junction fixture command path.");
        Put(Path.Combine(outside, "level.dat"), "outside-keep");
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = "/D /S /V:OFF /C \"mklink /J \"" + junction + "\" \"" + outside + "\"\"";
        Process? process = null;
        try {
            process = Process.Start(start) ?? throw new IOException("Could not create the original directory-junction fixture.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) throw new TimeoutException("Owned junction command exceeded five seconds.");
            Native.Check(Task.WaitAll([output, error], 5000), "Owned junction command output did not close.");
            Native.Check(process.ExitCode == 0, "Actual junction fixture failed: " + output.Result + error.Result);
            Native.Check((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "Fixture is not a real reparse point.");
            Reject(() => Capture(game, "Linked", "junction"), "junction world");
        } finally {
            try {
                if (process is not null) {
                    try { if (!process.HasExited) { process.Kill(); Native.Check(process.WaitForExit(5000), "Owned junction command did not stop after cleanup kill."); } }
                    finally { process.Dispose(); }
                }
            } finally {
                // Inspect the link itself and delete only this directory entry.
                // Never recurse/follow its target, including failed/timeout paths.
                try { _ = File.GetAttributes(junction); Directory.Delete(junction, recursive: false); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        Text(Path.Combine(outside, "level.dat"), "outside-keep");
    }

    private static void Retention(string game, string world, string store, string first, string otherId)
    {
        string legacy = Path.Combine(store, "points", first, "manifest.json"); JObject old = JObject.Parse(File.ReadAllText(legacy)); old.Remove("manual"); old["reason"] = "手动备份"; Put(legacy, old.ToString());
        string manual = Capture(game, "World A", "手动备份", 2);
        for (int index = 1; index <= 3; index++) { Put(Path.Combine(world, "level.dat"), "retention-" + index); Capture(game, "World A", "retention", 2); }
        List<JObject> points = Points(game), worlds = points.Where(p => (string?)p["world"] == "World A").ToList();
        Native.Check(worlds.Count(p => (string?)p["id"] != first && (string?)p["id"] != manual) == 2 && points.Any(p => (string?)p["id"] == otherId), "Automatic retention crossed world boundaries or kept the wrong count.");
        foreach (string id in new[] { first, manual }) Native.Check(worlds.Any(p => (string?)p["id"] == id), "Retention removed a current or legacy manual backup.");
        string[] selected = worlds.Select(p => (string)p["id"]!).ToArray();
        Reject(() => World("DeletePoints", game, new[] { selected[0], "../invalid" }), "invalid deletion batch");
        Native.Check(Points(game).Any(p => (string?)p["id"] == selected[0]), "Invalid batch partly deleted a valid backup.");
        string deleted = (string)World("DeletePoints", game, new[] { selected[0] })!;
        Native.Check(!Directory.Exists(Path.Combine(deleted, "points", selected[0])) && !Directory.Exists(Path.Combine(deleted, "deleted")), "Deleted backup payload was retained."); Text(Path.Combine(world, "level.dat"), "retention-3");
    }
}
