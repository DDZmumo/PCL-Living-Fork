using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows.Threading;

namespace PCLLF.Native.Facade.Tests;
// Original LF safe tooling fixtures, pinned inputs/line IDs. The actual Main is
// referenced by the Native host; no CLR4 process attempts to load a .NET10 DLL.
internal static class ToolboxChecks
{
    private static readonly Dictionary<string, HashSet<int>> Expected = new(StringComparer.Ordinal)
    {
        ["Test-Toolbox.ps1"] = new() { 40, 43, 44, 45, 53, 61, 69, 76, 79, 83, 85, 106, 142, 157, 183, 185, 186, 190, 192, 198, 205, 207, 210, 214, 216, 219, 232, 247 },
        ["Test-ToolboxMemory.ps1"] = new() { 61, 87, 89, 97, 100, 104, 106, 108, 112, 118, 121, 127, 129, 131, 133, 135, 138, 140, 142 },
        ["Test-ToolboxDownload.ps1"] = new() { 40, 42, 44, 46, 47, 48, 52, 54, 59, 60, 64, 67, 68, 69, 71, 76, 83, 84, 91, 96, 97, 104, 105, 107, 114 },
        ["Test-InstalledComponentLoading.ps1"] = new() { 60, 61, 62, 63, 71, 75, 78, 83 },
    };
    private static readonly Dictionary<string, HashSet<int>> Seen = new(StringComparer.Ordinal);
    private static int assertions;
    private static bool verifiedChild;
    internal static void Run(FixtureEnvironment environment)
    {
        VerifyChild(environment);
        ToolboxSurpriseChecks.Run();
        DailyLuckChecks.Run();
        Seen.Clear(); assertions = 0;
        foreach (string script in Expected.Keys) Seen.Add(script, new());
        ToolboxCleanupChecks.Run(Native.Main);
        ToolboxMemoryChecks.Run(Native.Main);
        ToolboxDownloadChecks.Run(Native.Main);
        ToolboxComponentsChecks.Run(environment);
        foreach (var pair in Expected)
            Native.Check(Seen[pair.Key].SetEquals(pair.Value), "Incomplete original toolbox coverage: " + pair.Key + " missing " + string.Join(',', pair.Value.Except(Seen[pair.Key])));
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native toolbox: " + Expected.Sum(pair => pair.Value.Count) + " original assertion sites, " + assertions + " executions; real Main memory seams/readonly token, cleaner/junctions, staged download and installed-component loader. No UAC, system memory command, external service or game.");
    }
    internal static void Check(string script, int line, bool value, string message)
    {
        Native.Check(Expected[script].Contains(line), "Unknown original toolbox assertion " + script + ":" + line);
        Seen[script].Add(line); assertions++;
        Native.Check(value, script + ":" + line + " " + message);
    }
    private static void VerifyChild(FixtureEnvironment environment)
    {
        string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args, "--fixture");
        Native.Check(Assembly.GetEntryAssembly() == typeof(ToolboxChecks).Assembly && typeof(ToolboxChecks).Assembly.GetName().Name == "PCLLF.Native.Facade.Tests" && args.Contains("--isolated", StringComparer.Ordinal) && option >= 0 && option + 1 < args.Length, "Toolbox runs only in its genuine isolated test child.");
        string root = args[option + 1]; Program.ValidateRoot(root);
        Native.Check(Native.Full(root) == Native.Full(environment.Root) && Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(root, "output")), "Toolbox child/fixture/output identities differ.");
        verifiedChild = true;
    }
    internal static void Drain(DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Toolbox component drain exceeded its shared deadline.");
        DispatcherFrame frame = new(); bool drained = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherTimer timer = new(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds)) };
        EventHandler expired = (_, _) => frame.Continue = false; timer.Tick += expired;
        DispatcherOperation idle = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { drained = true; frame.Continue = false; }));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= expired; if (idle.Status == DispatcherOperationStatus.Pending) idle.Abort(); }
        if (!drained || DateTime.UtcNow >= deadline) throw new TimeoutException("Actual toolbox component posts did not drain before the shared deadline.");
    }
    [DoesNotReturn]
    internal static void FailClosed(Exception error)
    {
        if (!verifiedChild) throw new InvalidOperationException("No genuine isolated toolbox-child proof; refusing process termination.", error);
        // Run verified the current actual test child before starting any IO.
        Console.Error.WriteLine("FAIL native toolbox component cleanup; only current verified test child PID " + Environment.ProcessId + " exits with roots/caches retained: " + error);
        Console.Error.Flush(); Console.Out.Flush(); Environment.Exit(95);
        Environment.FailFast("The isolated toolbox test child unexpectedly survived Environment.Exit.");
    }
}
