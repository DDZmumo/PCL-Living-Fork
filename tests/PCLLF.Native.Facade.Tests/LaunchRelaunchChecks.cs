using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class LaunchRelaunchChecks {
    private enum State { Loading, Running, Crashed, Ended, Canceled }
    private sealed class Watched : ILegacyWatcherData<string, Process, State> {
        public Process CoreGameProcess { get; set; } = null!;
        public string CoreInstance { get; set; } = "";
        public Process CoreLoader { get; set; } = null!;
        public State CoreState { get => Watcher.State; set => Watcher.State = value; }
        public List<string> CoreWaitingLog { get; set; } = [];
        public ConcurrentQueue<string> CoreLatestLog { get; set; } = new();
        public LegacyWatcher<Watched, string, Process, State> Watcher = null!;
    }

    internal static void Run(FixtureEnvironment environment) {
        string java8 = Java("PCLLF_MMC_TEST_JAVA8"), modern = Java("PCLLF_MMC_TEST_JAVA21");
        string javac = Path.Combine(Path.GetDirectoryName(modern)!, "javac.exe");
        Native.Check(File.Exists(javac), "The modern JDK requires javac.");
        string directory = Path.Combine(environment.Data, "java-handoff");
        Directory.CreateDirectory(directory);
        Compile(javac, directory, "Bootstrap", BootstrapSource, "8");
        Compile(javac, directory, "Relay", RelaySource, "21");
        NativeWindowEnumeration();
        RunCase(directory, java8, modern, 0, direct: true);
        RunCase(directory, java8, modern, 0);
        RunCase(directory, java8, modern, 23);
        RunCase(directory, java8, modern, null);
        RunCase(directory, java8, modern, 0, startSuccessor: false);
        Console.WriteLine("PASS real Java handoff: bootstrap → relay → game, owned window, no inherited game logs, actual exit code, exact-once callbacks and cancellation.");
    }

    private static void NativeWindowEnumeration() {
        var application = System.Windows.Application.Current;
        var previousShutdown = application.ShutdownMode;
        var previousWindow = application.MainWindow;
        Window FixtureWindow() => new() { Width = 80, Height = 80, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var first = FixtureWindow();
        var second = FixtureWindow();
        try {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            first.Show(); second.Show();
            var handles = new HashSet<IntPtr>();
            Native.Static("PCL.ModWatcher", "NativeEnumWindows", (Action<IntPtr, int>)((hwnd, _) => handles.Add(hwnd)), 0);
            Native.Check(handles.Contains(new WindowInteropHelper(first).Handle) && handles.Contains(new WindowInteropHelper(second).Handle),
                "The native EnumWindows adapter stopped before it had visited both owned top-level windows.");
        } finally {
            first.Close(); second.Close();
            application.MainWindow = previousWindow;
            application.ShutdownMode = previousShutdown;
        }
    }

    private static void RunCase(string classes, string java8, string modern, int? gameExit, bool startSuccessor = true, bool direct = false) {
        string root = Path.Combine(classes, direct ? "direct" : startSuccessor ? gameExit?.ToString() ?? "cancel" : "no-successor"), outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        using var entry = direct ? Start(modern, root, "-cp", classes, "Relay", root, "game", "0") :
            Start(java8, root, "-cp", classes, "Bootstrap", root, modern, classes);
        using var unrelated = Start(modern, outside, "-cp", classes, "Relay", outside, "game", "0");
        int relayPid = 0, titlePid = 0, stopped = 0, crashes = 0;
        int publishWindowAfterScan = 0;
        long windowPublishedAt = 0;
        bool normal = false, active = false;
        int stoppedAnchor = 0;
        var owner = new Watched();
        List<Watched> watchers = [];
        using var finished = new ManualResetEventSlim();
        var host = new LegacyWatcherHost<Watched, string, Process, State> {
            Loading = State.Loading, Running = State.Running, Crashed = State.Crashed, Ended = State.Ended, Canceled = State.Canceled,
            ReadWatchers = () => watchers, WriteWatchers = values => watchers = values, WriteHasRunningMinecraft = value => Volatile.Write(ref active, value),
            LaunchLog = (_, _) => { }, RunInUi = action => action(), RunInNewThread = (action, name) => {
                if (name.StartsWith("Minecraft Watcher", StringComparison.Ordinal)) new Thread(() => action()) { IsBackground = true }.Start();
            },
            UpdateRecentWorldVisibility = () => { }, RefreshRecentWorlds = () => { }, RefreshShutdownButton = () => { },
            EndProgram = () => { }, RestoreLauncher = () => { }, MusicStop = () => false, MusicStart = () => false,
            LauncherVisibility = () => 1, WindowType = () => 1, LoaderInput = process => process,
            LoaderCanceled = _ => false, LoaderProgress = (_, _) => { }, ReleaseTime = _ => new DateTime(2014, 1, 1),
            ProcessStopped = (_, anchor, exitedNormally) => {
                stoppedAnchor = anchor.Id;
                normal = exitedNormally;
                Interlocked.Increment(ref stopped);
            },
            MarkHealthy = (_, _, _) => { }, InstancePath = path => path,
            CrashHint = () => Interlocked.Increment(ref crashes), FeedbackInfo = () => { },
            EnumWindows = (visit, _) => {
                visit(new IntPtr(1), 0);
                if (Volatile.Read(ref relayPid) != 0) visit(new IntPtr(2), 0);
                if (Volatile.Read(ref titlePid) != 0) {
                    visit(new IntPtr(4), 0); // same JVM's FML splash, before the actual game window
                    visit(new IntPtr(5), 0); // same JVM, but not yet visible
                    visit(new IntPtr(3), 0);
                }
                // Make the fixture window visible just after a scan has missed it.
                int next = Interlocked.Exchange(ref publishWindowAfterScan, 0);
                if (next != 0)
                {
                    Volatile.Write(ref windowPublishedAt, Stopwatch.GetTimestamp());
                    Volatile.Write(ref titlePid, next);
                }
            },
            GetClassName = (window, buffer, _) => Fill(buffer, window.ToInt64() == 2 ? "SunAwtFrame" : "GLFW30"),
            GetWindowText = (window, buffer, _) => Fill(buffer, window.ToInt64() switch {
                1 => "Unrelated Minecraft", 2 => "Java relay console", 4 => "FML Loading", 5 => "Hidden Minecraft", _ => "Owned Minecraft"
            }),
            GetWindowProcessId = window => window.ToInt64() switch {
                1 => unrelated.Id, 2 => Volatile.Read(ref relayPid), _ => Volatile.Read(ref titlePid)
            },
            IsWindowVisible = window => window.ToInt64() != 5
        };
        var group = new LegacyWatcherGroup<Watched, string, Process, State>(host);
        owner.Watcher = new(owner, host, group);

        try {
            owner.Watcher.Initialize(entry, root, "", OnMonitoringFinished: () => finished.Set());
            if (direct) {
                WaitFor(() => TryReadPid(root, "game.pid", out _), "The silent entry JVM did not start.");
                ConfirmWindow(entry.Id);
                Native.Check(owner.CoreLatestLog.IsEmpty, "A stdout line completed the direct-window fixture instead of the window probe.");
                Signal(root, "quit");
                WaitFor(() => finished.IsSet, "The direct game process did not exit.");
                Native.Check(stopped == 1 && normal && crashes == 0 && owner.CoreState == State.Ended,
                    "Faster readiness changed direct-launch exit handling.");
                return;
            }
            File.WriteAllText(Path.Combine(root, "bootstrap.pid"), entry.Id.ToString());
            if (!startSuccessor) Signal(root, "stop-relay");
            Signal(root, "start");
            Native.Check(entry.WaitForExit(6000) && entry.ExitCode == 0, "The Java 8 bootstrap did not complete the handoff.");
            if (!startSuccessor) {
                WaitFor(() => finished.IsSet, "The empty launch did not settle.");
                Native.Check(owner.CoreState == State.Crashed && crashes == 1 && stopped == 1 && !normal,
                    "A zero exit without a game or owned successor was misclassified as a successful startup.");
                return;
            }
            WaitFor(() => TryReadPid(root, "relay.pid", out _), "Relay JVM did not start.");
            TryReadPid(root, "relay.pid", out int relay);
            Volatile.Write(ref relayPid, relay);
            // Allow several watcher polls with both an unrelated window and the
            // relay's AWT console present, but no actual game window.
            Thread.Sleep(450);
            Native.Check(owner.CoreState == State.Loading && Volatile.Read(ref stopped) == 0 && Volatile.Read(ref crashes) == 0 &&
                Volatile.Read(ref active), "A successful bootstrap exit caused a false crash or a foreign window was adopted.");
            if (gameExit is null) {
                owner.Watcher.Kill();
                WaitFor(() => finished.IsSet, "Cancelled relay still holds the launch session.");
                using Process? process = TryOpen(relay);
                Native.Check((process is null || process.HasExited) && !unrelated.HasExited &&
                    owner.CoreState == State.Canceled && stopped == 1 && !normal && crashes == 0, "Cancellation crossed the launch job or failed to settle.");
                return;
            }

            File.WriteAllText(Path.Combine(root, "exit-code"), gameExit.Value.ToString());
            Signal(root, "spawn");
            WaitFor(() => TryReadPid(root, "game.pid", out _), "The successor game JVM did not start.");
            TryReadPid(root, "game.pid", out int game);
            ConfirmWindow(game);
            Native.Check(stopped == 0 && crashes == 0, "Callbacks finished while the successor game was still active.");
            Signal(root, "quit");
            WaitFor(() => finished.IsSet, "The successor's exit did not settle the launch session.");
            Native.Check(stoppedAnchor == entry.Id && stopped == 1 && normal == (gameExit == 0) && crashes == (gameExit == 0 ? 0 : 1) &&
                owner.CoreState == (gameExit == 0 ? State.Ended : State.Crashed), "The successor's exit was not attributed to the original session.");
        } finally {
            Signal(root, "start");
            Signal(root, "quit");
            Signal(root, "stop-relay");
            Signal(outside, "quit");
            if (!entry.WaitForExit(1500)) entry.Kill(entireProcessTree: true);
            if (!unrelated.WaitForExit(1500)) unrelated.Kill(entireProcessTree: true);
            finished.Wait(2000);
        }

        void ConfirmWindow(int game)
        {
            var waiter = Task.Run(() => owner.Watcher.WaitForStartupState(6000));
            Volatile.Write(ref publishWindowAfterScan, game);
            Native.Check(waiter.Wait(7000) && waiter.Result == State.Running && owner.CoreGameProcess.Id == game,
                "The visible owned game window did not notify the startup waiter.");
            Console.WriteLine($"Java {(direct ? "direct" : "handoff")} window to waiter: {Stopwatch.GetElapsedTime(Volatile.Read(ref windowPublishedAt)).TotalMilliseconds:0.0} ms after a missed scan");
        }
    }

    private static int Fill(StringBuilder buffer, string value) { buffer.Append(value); return value.Length; }
    private static bool TryReadPid(string directory, string name, out int pid) {
        string path = Path.Combine(directory, name);
        pid = 0;
        try { return File.Exists(path) && int.TryParse(File.ReadAllText(path), out pid) && pid > 0; }
        catch (IOException) { return false; } // The test JVM may still be publishing its PID.
    }
    private static Process? TryOpen(int pid) {
        try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
    }
    private static void Signal(string directory, string name) => File.WriteAllText(Path.Combine(directory, name), "");
    private static void WaitFor(Func<bool> test, string message) => Native.Check(SpinWait.SpinUntil(test, 7000), message);
    private static string Java(string key) {
        string? path = Environment.GetEnvironmentVariable(key);
        Native.Check(path is not null && Path.IsPathFullyQualified(path) && File.Exists(path), "Set the explicit installed JVM " + key);
        return path!;
    }
    private static Process Start(string executable, string directory, params string[] arguments) {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string token in arguments) info.ArgumentList.Add(token);
        foreach (string name in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) info.Environment.Remove(name);
        return Process.Start(info) ?? throw new IOException("Could not start test JVM.");
    }
    private static void Compile(string javac, string directory, string name, string contents, string release) {
        string file = Path.Combine(directory, name + ".java");
        File.WriteAllText(file, contents);
        using Process process = Start(javac, directory, "--release", release, "-d", directory, file);
        Native.Check(process.WaitForExit(10_000) && process.ExitCode == 0, "Could not compile relay fixture " + name);
    }

    private const string BootstrapSource = """
import java.io.*;
import java.nio.file.*;
public class Bootstrap {
    public static void main(String[] args) throws Exception {
        Path root = Paths.get(args[0]);
        for (int i = 0; i < 500 && !Files.exists(root.resolve("start")); i++) Thread.sleep(20);
        if (!Files.exists(root.resolve("start")) || Files.exists(root.resolve("stop-relay"))) return;
        System.out.println("Preparing a new Java runtime");
        new ProcessBuilder(args[1], "-cp", args[2], "Relay", args[0], "relay", args[1], args[2])
            .redirectErrorStream(true).redirectOutput(root.resolve("relay.log").toFile()).start();
    }
}
""";

    private const string RelaySource = """
import java.nio.file.*;
public class Relay {
    private static boolean await(Path root, String file) throws Exception {
        for (int i = 0; i < 1000 && !Files.exists(root.resolve(file)); i++) {
            if (Files.exists(root.resolve("stop-relay"))) return false;
            Thread.sleep(20);
        }
        return Files.exists(root.resolve(file));
    }
    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        long pid = ProcessHandle.current().pid();
        if (args[1].equals("game")) {
            Files.writeString(root.resolve("game.pid"), Long.toString(pid));
            await(root, "quit");
            System.exit(Integer.parseInt(args[2]));
        }
        Files.writeString(root.resolve("relay.pid"), Long.toString(pid));
        long parent = Long.parseLong(Files.readString(root.resolve("bootstrap.pid")));
        ProcessHandle.of(parent).ifPresent(process -> process.onExit().join());
        if (!await(root, "spawn")) return;
        String exit = Files.readString(root.resolve("exit-code"));
        new ProcessBuilder(args[2], "-cp", args[3], "Relay", args[0], "game", exit)
            .redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD).start();
        await(root, "stop-relay");
    }
}
""";
}
