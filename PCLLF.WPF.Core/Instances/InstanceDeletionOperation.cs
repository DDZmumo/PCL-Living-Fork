using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MeloongCore;
using ThrottleDebounce;

namespace PCLCS;

/// <summary>Runs confirmed filesystem deletion away from the dispatcher.</summary>
public static class InstanceDeletionOperation
{
    private static readonly object Gate = new();
    private static readonly HashSet<IWorker> Active = new();
    private static int shutdownReservations;
    public static bool HasActiveOperations { get { lock (Gate) return Active.Count != 0; } }
    public static IDisposable? TryReserveShutdown()
    {
        lock (Gate)
        {
            if (Active.Count != 0) return null;
            shutdownReservations++;
            return new ShutdownReservation();
        }
    }
    private sealed class ShutdownReservation : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (Gate) shutdownReservations--;
        }
    }
    public static IWorker? Start(Action delete, Func<IWorker, bool> acquire, Action<IWorker> release,
        Action<Action> post, Action completed, Action<Exception> failed, Action canceled)
    {
        Exception? failure = null;
        bool wasCanceled = false;
        var worker = new RedoableWorker(delete);
        // The reservation's Stopped subscription must precede the UI callback.
        lock (Gate)
        {
            if (shutdownReservations != 0 || !acquire(worker)) return null;
            Active.Add(worker);
        }
        worker.Failed += error => failure = error;
        worker.Canceled += () => wasCanceled = true;
        worker.Stopped += () =>
        {
            lock (Gate) Active.Remove(worker);
            post(() =>
            {
                if (failure is not null) failed(failure);
                else if (wasCanceled) canceled();
                else if (worker.LastSucceeded) completed();
            });
        };
        try { worker.Start(); }
        catch { lock (Gate) Active.Remove(worker); release(worker); throw; }
        return worker;
    }
}

/// <summary>LF-owned deletion policy; never falls back from recycling to permanent deletion.</summary>
public static class InstanceFolderDeletion
{
    public static void Delete(string path, bool recycle, Action<string> sendToRecycleBin)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Instance folder is required.", nameof(path));
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("Device paths are not instance folders.", nameof(path));
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
        string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string disk = (Path.GetPathRoot(target) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Length == 0 || string.Equals(target, disk, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Cannot delete a volume root.");
        foreach (string critical in new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
        {
            if (string.IsNullOrWhiteSpace(critical)) continue;
            string protectedPath = Path.GetFullPath(critical).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(target, protectedPath, StringComparison.OrdinalIgnoreCase) || protectedPath.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Cannot delete a protected directory or its parent.");
        }
        if (!Directory.Exists(target)) return;
        if (recycle) { sendToRecycleBin(target); return; }
        Retrier.Attempt(delay: _ => TimeSpan.FromMilliseconds(200), isRetryAllowed: error => error is IOException,
            action: _ =>
            {
                var directory = new DirectoryInfo(target);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(target, recursive: false); return; }
                PrepareForDelete(directory);
                Directory.Delete(target, recursive: true);
            });
    }
    private static void PrepareForDelete(FileSystemInfo entry)
    {
        FileAttributes attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry.FullName, recursive: false);
            else File.Delete(entry.FullName);
            return;
        }
        if ((attributes & FileAttributes.ReadOnly) != 0) entry.Attributes = attributes & ~FileAttributes.ReadOnly;
        if (entry is DirectoryInfo directory)
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos()) PrepareForDelete(child);
    }
}
