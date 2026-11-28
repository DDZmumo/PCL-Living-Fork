using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;
internal static partial class LauncherLifecycleChecks
{
    private sealed class ProducerScope : IDisposable
    {
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState> host;
        private readonly Action<Action> original;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Thread, byte> threads = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<ModLoader.LoaderBase> roots = new();
        private readonly List<ManualResetEventSlim> stopped = new();
        private int pending, generation, clean;
        internal ProducerScope()
        {
            string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "--fixture");
            Native.Check(args.Contains("--isolated", StringComparer.Ordinal) && i >= 0 && i + 1 < args.Length && Assembly.GetEntryAssembly() == typeof(FixtureEnvironment).Assembly,
                "Lifecycle cleanup termination is limited to its actual isolated test child.");
            string root = Native.Full(args[i + 1]);
            Native.Check(root.StartsWith(Native.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^PclNativeFacade-[a-f0-9]{32}$") && Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(root, "output")), "Invalid isolated lifecycle output root.");
            host = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            original = host.RunInThread;
            host.RunInThread = action =>
            {
                Interlocked.Increment(ref pending); Interlocked.Increment(ref generation); int entered = 0;
                try
                {
                    original(() =>
                    {
                        Interlocked.Exchange(ref entered, 1); Observe(Thread.CurrentThread);
                        try { action(); } finally { Interlocked.Decrement(ref pending); }
                    });
                }
                catch { if (Volatile.Read(ref entered) == 0) Interlocked.Decrement(ref pending); throw; }
            };
        }
        internal void Observe(Thread? thread) { if (thread is not null && threads.TryAdd(thread, 0)) Interlocked.Increment(ref generation); }
        internal void Root(ModLoader.LoaderBase root) => roots.Add(root);
        internal void Stopped(ManualResetEventSlim signal) => stopped.Add(signal);
        private void ObserveLoaders()
        {
            foreach (var root in roots) Visit(root);
            void Visit(ModLoader.LoaderBase node)
            {
                FieldInfo? field = node.GetType().GetField("LastRunningThread", Native.Flags); if (field is not null) Observe((Thread?)field.GetValue(node));
                FieldInfo? children = node.GetType().GetField("Loaders", Native.Flags);
                if (children?.GetValue(node) is IEnumerable<ModLoader.LoaderBase> list) foreach (var child in list) Visit(child);
            }
        }
        [DoesNotReturn] private static void FailClosed(string message, Exception? error = null)
        {
            Console.Error.WriteLine("FAIL isolated lifecycle producer cleanup; context retained until child exit: " + message + (error is null ? "" : " / " + error));
            Console.Error.Flush(); Environment.Exit(1); throw new InvalidOperationException("Isolated lifecycle exit returned.");
        }
        public void Dispose()
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int Remaining() => Math.Max(0, 10000 - checked((int)elapsed.ElapsedMilliseconds));
            using var watchdog = new System.Threading.Timer(_ => { if (Volatile.Read(ref clean) == 0) FailClosed("Shared 10s producer/UI deadline expired."); }, null, 10000, Timeout.Infinite);
            try
            {
                int stable = 0;
                while (true)
                {
                    ObserveLoaders(); int before = Volatile.Read(ref generation);
                    DispatcherFrame frame = new(); bool drained = false;
                    var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, Remaining())) };
                    timer.Tick += (_, _) => frame.Continue = false;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { drained = true; frame.Continue = false; }));
                    timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
                    if (!drained || Remaining() == 0) FailClosed("Actual posted lifecycle UI callbacks did not drain.");
                    ObserveLoaders();
                    bool quiet = Volatile.Read(ref pending) == 0 && stopped.All(signal => signal.IsSet) &&
                        roots.All(root => root.State != ModBase.LoadState.Loading) &&
                        threads.Keys.All(thread => ReferenceEquals(thread, Thread.CurrentThread) || thread.Join(0));
                    stable = quiet && before == Volatile.Read(ref generation) ? stable + 1 : 0;
                    if (stable == 2) break;
                    if (Remaining() == 0) FailClosed("Actual loader/worker threads or queued producers did not terminate.");
                    Thread.Yield();
                }
            }
            catch (Exception error) { FailClosed("Refusing platform/context restoration after unsafe cleanup.", error); }
            Volatile.Write(ref clean, 1); watchdog.Change(Timeout.Infinite, Timeout.Infinite); host.RunInThread = original;
        }
    }
}
