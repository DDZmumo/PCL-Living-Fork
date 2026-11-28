using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS;
using PCLLF.Presentation.Windowing;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

internal static class InstanceDeletionChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.GetFullPath(Path.Combine(environment.Data, "instance-delete-" + Guid.NewGuid().ToString("N")));
        Native.Check(root.StartsWith(Path.GetFullPath(environment.Data) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Deletion fixture escaped its owned root.");
        Directory.CreateDirectory(root);
        Bindings();
        NativeDeleteHeartbeat(root);
        FailureAndCancellation(root);
        SafeFolderPolicy(root);
        ShutdownReservation();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS instance deletion: real Main bindings and worker/UI heartbeat, shutdown blocked while deleting, update reservation, actual junction external-target preservation, recycling cancel/error without permanent fallback, readonly deletion and protected roots.");
    }

    private static void Bindings()
    {
        object selection = Native.Call(typeof(GlassInstanceSelectionPage), "CreateHost", new GlassInstanceSelectionPage(FileTaskShell.Install()))!;
        object page = RuntimeHelpers.GetUninitializedObject(typeof(PageInstanceOverall));
        object overall = Native.Call(typeof(PageInstanceOverall), "CreateHost", page)!;
        foreach (object host in new[] { selection, overall })
        {
            var action = (Delegate)Native.Get(host, "DeleteConfirmed")!;
            Native.Check(action.Method.DeclaringType == typeof(LauncherInstanceDeletion) && action.Method.Name == "Start", "A deletion entry bypasses the real background operation.");
        }
    }

    private static void NativeDeleteHeartbeat(string root)
    {
        string target = Path.Combine(root, "target"), sibling = Path.Combine(root, "keep");
        Directory.CreateDirectory(target); Directory.CreateDirectory(sibling);
        string file = Path.Combine(target, "locked.bin"), keep = Path.Combine(sibling, "keep.txt");
        File.WriteAllText(file, "delete fixture"); File.WriteAllText(keep, "unchanged");
        var instance = new ModMinecraft.McInstance(target + Path.DirectorySeparatorChar);
        bool completed = false, releasedAtCompletion = false, heartbeat = false;
        int uiThread = Environment.CurrentManagedThreadId, completionThread = -1;
        FileStream? held = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
        IWorker? active = null;
        try
        {
            var worker = (IWorker?)Native.Static("PCL.LauncherInstanceDeletion", "Begin", instance, false, (Action)(() =>
            {
                completionThread = Environment.CurrentManagedThreadId;
                releasedAtCompletion = !ModMinecraft.McInstanceLock.IsLocked(instance);
                completed = true;
            }));
            active = worker;
            Native.Check(worker is not null && ModMinecraft.McInstanceLock.IsLocked(instance), "Deletion did not reserve its exact instance.");
            Native.Check(InstanceDeletionOperation.HasActiveOperations && InstanceDeletionOperation.TryReserveShutdown() is null, "Shutdown/update reserved an active deletion.");
            CloseWhileDeleting();
            Native.Check(Native.Static("PCL.LauncherInstanceDeletion", "Begin", instance, false, (Action)(() => { })) is null, "Duplicate deletion acquired an occupied instance.");
            Application.Current.Dispatcher.BeginInvoke((Action)(() => heartbeat = true), DispatcherPriority.Normal);
            VisualAssetChecks.Pump(() => heartbeat, "Deletion blocked dispatcher heartbeat.");
            Native.Check(!completed && Directory.Exists(target) && ModMinecraft.McInstanceLock.IsLocked(instance), "File workload or its reservation ended before the locked file was released.");
            held.Dispose(); held = null;
            VisualAssetChecks.Pump(() => completed, "Native deletion did not complete after the file lease ended.", 15000);
            Native.Check(!Directory.Exists(target) && releasedAtCompletion && completionThread == uiThread, "Native deletion completion: exists=" + Directory.Exists(target) + "; released=" + releasedAtCompletion + "; UI=" + uiThread + "; callback=" + completionThread);
            Native.Check(File.ReadAllText(keep) == "unchanged", "Deletion touched a sibling directory.");
            Native.Check(!InstanceDeletionOperation.HasActiveOperations, "Finished deletion still blocks shutdown.");
        }
        finally {
            held?.Dispose();
            if (active is not null && !active.WaitIfRunning(15000)) {
                Console.Error.WriteLine("Deletion fixture retained an active filesystem worker; refusing to restore its guards.");
                Environment.Exit(95);
            }
        }
    }

    private static void CloseWhileDeleting()
    {
        object main = RuntimeHelpers.GetUninitializedObject(typeof(FormMain));
        object host = Native.Call(typeof(FormMain), "CreateWindowHost", main)!;
        Native.Check(((Func<bool>)Native.Get(host, "HasBlockingFileOperation")!)(), "Main shutdown is not bound to active deletion state.");
        int hints = 0, exits = 0;
        Native.Set(host, "Hint", (Action<string, LegacyMainHint>)((text, _) => { Native.Check(text.Contains("文件处理"), "Unexpected busy shutdown message."); hints++; }));
        Native.Set(host, "ForceExit", (Action)(() => exits++));
        Type controllerType = Native.Field(typeof(FormMain), "Controller").FieldType;
        object controller = Activator.CreateInstance(controllerType, new object?[] { null, host })!;
        Native.Call(controllerType, "EndProgram", controller, true);
        Native.Call(controllerType, "EndProgram", controller, false);
        Native.Check(hints == 2 && exits == 0, "Close/update shutdown interrupted an active deletion.");
    }

    private static void SafeFolderPolicy(string root)
    {
        string version = Path.Combine(root, "junction-version"), external = Path.Combine(root, "external-keep");
        Directory.CreateDirectory(version); Directory.CreateDirectory(external);
        string marker = Path.Combine(external, "marker.txt"); File.WriteAllText(marker, "must survive");
        File.SetAttributes(marker, File.GetAttributes(marker) | FileAttributes.ReadOnly);
        string link = Path.Combine(version, "linked-save");
        try { EnvironmentHistoryChecks.WithJunction(link, external, () =>
        {
            InstanceFolderDeletion.Delete(version, false, _ => throw new InvalidOperationException("Permanent delete called recycling."));
            Native.Check(!Directory.Exists(version) && File.ReadAllText(marker) == "must survive" && (File.GetAttributes(marker) & FileAttributes.ReadOnly) != 0,
                "Instance deletion followed a junction or changed external attributes.");
        }); }
        finally { if (File.Exists(marker)) File.SetAttributes(marker, FileAttributes.Normal); }
        string recycle = Path.Combine(root, "recycle-policy"); Directory.CreateDirectory(recycle);
        string keep = Path.Combine(recycle, "keep.txt"); File.WriteAllText(keep, "preserve");
        foreach (Exception expected in new Exception[] { new OperationCanceledException("recycle canceled"), new IOException("recycle unavailable") })
        {
            int calls = 0; Exception? actual = null;
            try { InstanceFolderDeletion.Delete(recycle, true, path => { Native.Check(Native.Full(path) == Native.Full(recycle), "Recycling changed the instance path."); calls++; throw expected; }); }
            catch (Exception error) { actual = error; }
            Native.Check(ReferenceEquals(actual, expected) && calls == 1 && File.ReadAllText(keep) == "preserve", "Recycling cancellation/error fell through to permanent deletion.");
        }
        File.SetAttributes(keep, FileAttributes.ReadOnly);
        InstanceFolderDeletion.Delete(recycle, false, _ => throw new InvalidOperationException("Unexpected recycle"));
        Native.Check(!Directory.Exists(recycle), "Readonly instance content could not be deleted.");
        bool blocked = false;
        try { InstanceFolderDeletion.Delete(Path.GetPathRoot(root)!, false, _ => { }); }
        catch (UnauthorizedAccessException) { blocked = true; }
        Native.Check(blocked, "Volume root deletion was permitted.");
    }

    private static void ShutdownReservation()
    {
        using (IDisposable? reservation = InstanceDeletionOperation.TryReserveShutdown())
        {
            Native.Check(reservation is not null, "Idle shutdown could not reserve the deletion barrier.");
            bool acquired = false;
            IWorker? worker = InstanceDeletionOperation.Start(() => throw new InvalidOperationException("Reserved shutdown ran deletion"),
                _ => { acquired = true; return true; }, _ => { }, action => action(), () => { }, _ => { }, () => { });
            Native.Check(worker is null && !acquired, "Deletion started after shutdown/update reserved its barrier.");
        }
        using IDisposable? next = InstanceDeletionOperation.TryReserveShutdown();
        Native.Check(next is not null, "Disposed shutdown reservation leaked its barrier.");
    }

    private static void FailureAndCancellation(string root)
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;
        foreach (bool cancel in new[] { false, true })
        {
            string path = Path.Combine(root, cancel ? "cancel" : "failure");
            Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "keep.txt"), "preserve");
            var locks = new LegacyMinecraftInstanceLocks(new ConcurrentDictionary<string, LegacyMinecraftInstanceLease>(StringComparer.OrdinalIgnoreCase));
            bool observed = false, completed = false;
            Exception? failure = null;
            var worker = InstanceDeletionOperation.Start(
                () => { if (cancel) throw new OperationCanceledException("synthetic cancellation"); throw new IOException("synthetic deletion failure"); },
                value => locks.TryAcquire(value, path), value => locks.Release(value, path),
                work => dispatcher.BeginInvoke(work), () => completed = true,
                error => { Native.Check(!locks.IsLocked(path), "Failed deletion released its lease after UI notification."); failure = error; observed = true; },
                () => { Native.Check(!locks.IsLocked(path), "Canceled deletion retained its lease during UI notification."); observed = true; });
            Native.Check(worker is not null, "Synthetic deletion did not reserve its fixture.");
            VisualAssetChecks.Pump(() => observed, "Deletion failure/cancel did not return to the UI.");
            Native.Check(!completed && (cancel ? failure is null : failure is IOException) && File.ReadAllText(Path.Combine(path, "keep.txt")) == "preserve", "Failed/canceled deletion reported success or lost original data.");
        }
    }
}
