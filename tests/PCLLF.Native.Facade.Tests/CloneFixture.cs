using System.Buffers.Binary;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;
internal sealed class CloneFixture : IDisposable
{
    private readonly Stack<Action> restore = new();
    private readonly HashSet<string> links = new(StringComparer.OrdinalIgnoreCase);
    private readonly FixtureEnvironment environment;
    private readonly HashSet<ModLoader.LoaderBase> originalTasks;
    private readonly List<ManualResetEventSlim> cloneStopped = new();
    private readonly HashSet<ModLoader.LoaderBase> createdRoots = new(), createdLoaders = new();
    private readonly object producerGate = new();
    private readonly HashSet<Thread> producerThreads = new();
    private int pendingLoaderRuns, producerGeneration;
    internal readonly string Root, Profile;
    internal int Assertions;
    internal readonly Dictionary<string, HashSet<int>> Covered = new(StringComparer.Ordinal);
    internal CloneFixture(FixtureEnvironment environment)
    {
        Native.Check(Environment.GetCommandLineArgs().Contains("--isolated", StringComparer.Ordinal), "Clone fixture cleanup may terminate only its dedicated isolated test child.");
        this.environment = environment;
        originalTasks = new(ModLoader.LoaderTaskbar);
        Root = Path.Combine(environment.Data, "instance-clone");
        Profile = Path.Combine(Root, "profile");
        Directory.CreateDirectory(Root);
        foreach (string name in new[]
        {
            "PCL.LocalGameData",
            "PCL.WorldBackups",
            "PCL.EnvironmentHistory"
        }

        )
        {
            PropertyInfo property = Native.Property(Native.Type(name), "TestRoot");
            object? previous = property.GetValue(null);
            restore.Push(() => property.SetValue(null, previous));
            property.SetValue(null, name == "PCL.LocalGameData" ? Profile : Path.Combine(Root, name.Split('.').Last()));
        }

        foreach (string name in new[]
        {
            "_McFolderSelected",
            "_McInstanceSelected",
            "McInstanceList"
        }

        )
            Save(typeof(ModMinecraft), name);
        Save(typeof(LauncherInstanceContext), "Instance");
        foreach (Settings.Setting entry in Settings.Entries.Values)
        {
            IDictionary cache = (IDictionary)entry.ValueCache;
            var values = new List<DictionaryEntry>();
            foreach (DictionaryEntry item in cache)
                values.Add(item);
            restore.Push(() =>
            {
                cache.Clear();
                foreach (var item in values)
                    cache[item.Key] = item.Value;
            });
        }

        object service = Native.Field(Native.Type("PCL.InstanceClonerUi"), "Service").GetValue(null)!;
        var host = (InstanceCloneWorkflowHost<ModMinecraft.McInstance>)Native.Get(service, "host")!;
        Action<ModMinecraft.McInstance, InstanceCloneWork> start = host.StartWork;
        restore.Push(() => host.StartWork = start);
        host.StartWork = (instance, work) =>
        {
            // Observe the ORIGINAL callback after the real Worker.Stopped
            // invokes it; busy=false/lease release alone precede its UI post.
            ManualResetEventSlim stopped = new(false);
            cloneStopped.Add(stopped);
            Action original = work.Stopped;
            work.Stopped = () => { original(); stopped.Set(); };
            start(instance, work);
        };
        // Combo Update/Cancel have no public thread field. Observe the real
        // shared loader scheduling primitive without changing inline/new-thread
        // behavior, so a queued-but-not-started callback also holds this barrier.
        var loaderHost = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action<Action> runInThread = loaderHost.RunInThread;
        restore.Push(() => loaderHost.RunInThread = runInThread);
        loaderHost.RunInThread = action =>
        {
            Interlocked.Increment(ref pendingLoaderRuns);
            int entered = 0;
            try
            {
                runInThread(() =>
                {
                    Interlocked.Exchange(ref entered, 1);
                    ObserveThread(Thread.CurrentThread);
                    try { action(); }
                    finally { Interlocked.Decrement(ref pendingLoaderRuns); }
                });
            }
            catch
            {
                if (Volatile.Read(ref entered) == 0) Interlocked.Decrement(ref pendingLoaderRuns);
                throw;
            }
        };
        ModBase.RunInUi();
    }

    private void Save(Type owner, string name)
    {
        var field = Native.State(owner, name);
        object? value = field.GetValue(null);
        restore.Push(() => field.SetValue(null, value));
    }

    internal void SelectMinecraft(string path) => Native.State(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, Path.GetFullPath(path) + Path.DirectorySeparatorChar);
    internal string Folder(string name)
    {
        string path = Path.GetFullPath(Path.Combine(Root, name));
        Directory.CreateDirectory(path);
        return path;
    }

    internal void Check(string script, int line, bool value, string message)
    {
        Assertions++;
        if (!Covered.TryGetValue(script, out var ids))
            Covered.Add(script, ids = new());
        ids.Add(line);
        Native.Check(value, script + ":" + line + " " + message);
    }

    internal void Reject(string script, int line, Action action, string message)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch
        {
            rejected = true;
        }

        Check(script, line, rejected, message);
    }

    internal void Reject<TException>(string script, int line, Action action, string message, Func<TException, bool>? accepts = null) where TException : Exception
    {
        try { action(); }
        catch (TException error)
        {
            Check(script, line, accepts is null || accepts(error), message + " (wrong injected exception identity or callback)");
            return;
        }
        Check(script, line, false, message + " (expected " + typeof(TException).Name + ")");
    }

    internal void RequireCoverage(string script, int[] expected)
    {
        Native.Check(Covered.TryGetValue(script, out var actual) && actual.SetEquals(expected), "Clone fixture did not execute its complete original source-ID ledger: " + script);
        Console.WriteLine("PASS native clone source coverage: " + script + " all " + expected.Length + " observable source IDs, including every loop execution.");
    }

    internal static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static Dictionary<string, string> Snapshot(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(path => path.Substring(folder.Length), Hash, StringComparer.Ordinal);
    internal static bool Same(Dictionary<string, string> before, Dictionary<string, string> after) => before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out string? value) && value == item.Value);
    internal static bool SamePath(string first, string second) => string.Equals(Native.Full(first), Native.Full(second), StringComparison.OrdinalIgnoreCase);
    internal void Junction(string link, string target)
    {
        Native.Check(Native.Full(link).StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Native.Full(target).StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Junction fixture escaped its GUID directory.");
        links.Add(link);
        Directory.CreateDirectory(link);
        string full = Path.GetFullPath(target), substitute = full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\??\\UNC\\" + full.Substring(2) : "\\??\\" + full;
        byte[] name = Encoding.Unicode.GetBytes(substitute), print = Encoding.Unicode.GetBytes(full);
        byte[] data = new byte[16 + name.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)name.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(name.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)print.Length));
        name.CopyTo(data, 16);
        print.CopyTo(data, 16 + name.Length + 2);
        using SafeFileHandle handle = CreateFileW(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Actual fixture junction creation failed.");
        Native.Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "The fixture did not create an actual reparse junction.");
    }

    internal void Unlink(string link)
    {
        if (Directory.Exists(link))
            Directory.Delete(link);
        links.Remove(link);
    }

    [DoesNotReturn]
    internal static void FailClosed(string message, Exception? error = null)
    {
        // Do not unwind outer FixtureEnvironment.Dispose while a real worker
        // or posted refresh may still read its guarded roots/platform ports.
        Console.Error.WriteLine("FAIL isolated native clone cleanup; context retained until child exit: " + message + (error is null ? "" : " / " + error));
        Console.Error.Flush();
        Environment.Exit(1);
        throw new InvalidOperationException("Isolated clone child exit unexpectedly returned.");
    }

    private static void DrainPostedUi(DateTime deadline)
    {
        DispatcherFrame frame = new();
        bool drained = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds))
        };
        timer.Tick += (_, _) => frame.Continue = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { drained = true; frame.Continue = false; }));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!drained || DateTime.UtcNow >= deadline)
            FailClosed("The actual posted UI refresh queue did not drain before its bounded deadline.");
    }

    private void ObserveThread(Thread? thread)
    {
        if (thread is null) return;
        lock (producerGate)
            if (producerThreads.Add(thread)) producerGeneration++;
    }

    private void ObserveCreatedLoaders()
    {
        foreach (var root in ModLoader.LoaderTaskbar.Where(task => !originalTasks.Contains(task)).ToArray())
        {
            createdRoots.Add(root);
            Visit(root);
        }
        // Preserve all references seen BEFORE cancellation as well as later
        // replacements. Cancel/interrupt may clear a LastRunningThread field.
        foreach (var loader in createdLoaders.ToArray()) Visit(loader);
        ObserveThread(ModMinecraft.McInstanceListLoader.LastRunningThread);
        void Visit(ModLoader.LoaderBase loader)
        {
            createdLoaders.Add(loader);
            FieldInfo? thread = loader.GetType().GetField("LastRunningThread", Native.Flags);
            if (thread is not null) ObserveThread((Thread?)thread.GetValue(loader));
            FieldInfo? children = loader.GetType().GetField("Loaders", Native.Flags);
            if (children is not null)
                foreach (ModLoader.LoaderBase child in (IEnumerable<ModLoader.LoaderBase>)children.GetValue(loader)!)
                    Visit(child);
        }
    }

    private bool ProducersQuiet()
    {
        if (cloneStopped.Any(stopped => !stopped.IsSet) || Volatile.Read(ref pendingLoaderRuns) != 0 ||
            (bool)Native.Property(Native.Type("PCL.InstanceClonerUi"), "IsBusy").GetValue(null)! ||
            (bool)Native.Property(typeof(ModMinecraft.McInstanceLock), "IsAnyLocked").GetValue(null)! ||
            createdRoots.Any(task => task.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) ||
            createdLoaders.Any(task => task.State == ModBase.LoadState.Loading) ||
            ModMinecraft.McInstanceListLoader.State == ModBase.LoadState.Loading)
            return false;
        lock (producerGate) return producerThreads.All(thread => !thread.IsAlive);
    }

    private void FinishActualTasks()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        HashSet<ModLoader.LoaderBase> canceled = new();
        while (true)
        {
            ObserveCreatedLoaders();
            foreach (var task in createdRoots)
                if (canceled.Add(task) && task.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading)
                    task.Cancel();
            ObserveCreatedLoaders();
            // Workload's finally count and loader terminal State both precede
            // the Task thread's final State/UI/thread notifications. Wait for
            // every actual Task/Combo producer thread, not only the list thread.
            if (ProducersQuiet())
            {
                int generation, loadCount = createdLoaders.Count;
                lock (producerGate) generation = producerGeneration;
                Thread? listThread = ModMinecraft.McInstanceListLoader.LastRunningThread;
                DrainPostedUi(deadline);
                ObserveCreatedLoaders();
                bool stable;
                lock (producerGate) stable = generation == producerGeneration;
                if (stable && loadCount == createdLoaders.Count &&
                    ReferenceEquals(listThread, ModMinecraft.McInstanceListLoader.LastRunningThread) && ProducersQuiet())
                    break;
            }
            if (DateTime.UtcNow >= deadline)
                FailClosed("The actual clone Stopped/Task/Combo/list producer threads and final posted callbacks did not finish under fixture roots.");
            DrainPostedUi(deadline);
            Thread.Sleep(1);
        }
        foreach (var task in createdRoots) ModLoader.LoaderTaskbar.Remove(task);
        foreach (var stopped in cloneStopped) stopped.Dispose();
    }

    public void Dispose()
    {
        try
        {
            FinishActualTasks();
            foreach (string link in links.ToArray())
                Unlink(link);
        }
        catch (Exception error)
        {
            FailClosed("Refusing root/settings/platform restoration after unsafe clone cleanup.", error);
        }
        // This is deliberately NOT in finally. If quiescence fails, process
        // termination leaves the isolation guards in force until the child exits.
        while (restore.Count > 0)
            restore.Pop()();
        environment.AssertNoForbiddenIo();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputBytes, IntPtr output, int outputBytes, out int returned, IntPtr overlapped);
}
