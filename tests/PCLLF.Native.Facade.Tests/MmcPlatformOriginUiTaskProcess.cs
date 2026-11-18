using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace PCLLF.Native.Facade.Tests;

// The original LF NetManager loops are foreground lifetime threads. Keep their guards until a bound parent owns process termination.
internal static class MmcPlatformOriginUiTaskProcess {
    private const string ChildSwitch = "--mmc-platform-task-child", Receipt = "mmc-platform-task-complete.record";
    private static MmcPlatformOriginUiTaskChecks.Scope? retained;
    internal static void Run(FixtureEnvironment environment, string mode) {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        string group = "mmc-platform-online-" + mode;
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray(); int index = Array.IndexOf(args, ChildSwitch);
        if (index < 0) { RunParent(group, mode); return; }
        Native.Check(args.Length == 7 && index == 2 && args[0] == "--groups" && args[1] == group && Guid.TryParseExact(args[3], "N", out _) &&
            args[4] == "--isolated" && args[5] == "--fixture" && Native.Full(args[6]) == Native.Full(environment.Root), "Platform task child arguments are not bound.");
        try {
            retained = MmcPlatformOriginUiTaskChecks.Online(environment, mode);
            Native.Check(Application.Current.Windows.Count == 0, "A platform task left a real window open.");
            string expected = Proof(args[3], Environment.ProcessId, environment.Root, group);
            string path = Path.Combine(environment.Root, Receipt), temporary = path + ".pending";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(Encoding.UTF8.GetBytes(expected)); file.Flush(true); }
            Console.Out.Flush(); Console.Error.Flush(); File.Move(temporary, path);
            using ManualResetEventSlim lifetime = new(); lifetime.Wait();
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(environment.Root, Receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("Actual platform UI task failed with owned guards retained: " + error);
        }
    }
    private static string Proof(string nonce, int pid, string root, string group) => string.Join("\n", nonce, pid, Native.Full(root), group,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location))), "actual-main-http-producers-drained");
    private static void RunParent(string group, string mode) {
        string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N")), nonce = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root); Program.ValidateRoot(root);
        Process? child = null; Task<string>? stdout = null, stderr = null; Exception? primary = null;
        try {
            string output = Path.Combine(root, "output"); Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
            var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "--groups", group, ChildSwitch, nonce, "--isolated", "--fixture", root }) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new IOException("The owned platform UI child did not start.");
            stdout = child.StandardOutput.ReadToEndAsync(); stderr = child.StandardError.ReadToEndAsync();
            Stopwatch watch = Stopwatch.StartNew(); int Remaining() => Math.Max(0, 45000 - checked((int)watch.ElapsedMilliseconds));
            string receipt = Path.Combine(root, Receipt);
            while (!File.Exists(receipt)) {
                if (File.Exists(receipt + ".failed")) throw new IOException("Owned platform UI assertion failed: " + File.ReadAllText(receipt + ".failed"));
                if (Remaining() == 0) throw new TimeoutException("Owned platform UI " + mode + " exceeded its bounded 45-second work window.");
                if (child.WaitForExit(Math.Min(20, Remaining()))) throw new IOException("Owned platform UI child exited before its receipt: " + child.ExitCode);
            }
            Native.Check(File.ReadAllText(receipt) == Proof(nonce, child.Id, root, group) && !child.HasExited, "The platform UI receipt does not bind the owned nonce/PID/root/group/Main image.");
            child.Kill(true);
            Native.Check(child.WaitForExit(Remaining()) && Task.WaitAll([stdout, stderr], Remaining()) && watch.ElapsedMilliseconds < 45000,
                "Owned platform UI process/output did not stop within the same work deadline.");
            Native.Check(child.ExitCode == -1 && stderr.GetAwaiter().GetResult().Length == 0, "The completed owned platform UI child reported an unexpected termination/error.");
            Console.Write(stdout.GetAwaiter().GetResult());
        } catch (Exception error) { primary = error; throw; }
        finally {
            List<Exception> errors = []; bool stopped = child is null, settled = child is null;
            void Attempt(Action work) { try { work(); } catch (Exception error) { errors.Add(error); } }
            if (child is not null) {
                Stopwatch cleanup = Stopwatch.StartNew(); int Remaining() => Math.Max(0, 5000 - checked((int)cleanup.ElapsedMilliseconds));
                Task[] drains = new[] { stdout, stderr }.OfType<Task>().ToArray();
                Attempt(() => { if (!child.HasExited) child.Kill(true); });
                Attempt(() => stopped = child.WaitForExit(Remaining()));
                if (stopped) Attempt(() => Task.WaitAll(drains, Math.Min(250, Remaining())));
                try { Attempt(child.StandardOutput.Dispose); } finally { Attempt(child.StandardError.Dispose); }
                Attempt(() => Task.WaitAll(drains, Remaining()));
                settled = stdout is not null && stderr is not null && drains.All(task => task.IsCompletedSuccessfully);
                if (!stopped || !settled || cleanup.ElapsedMilliseconds >= 5000) errors.Add(new IOException("Owned platform UI cleanup did not prove both process and output completion; fixture retained."));
                Attempt(child.Dispose);
            }
            if (stopped && settled && errors.Count == 0) Attempt(() => { Program.ValidateRoot(root); Directory.Delete(root, true); });
            else Console.Error.WriteLine("Owned platform UI context retained: " + root);
            if (errors.Count != 0) {
                if (primary is null) throw new AggregateException("Owned platform UI cleanup failed.", errors);
                Console.Error.WriteLine("Owned platform UI cleanup failed after " + primary.GetType().Name + ": " + string.Join(" | ", errors.Select(error => error.Message)));
            }
        }
    }
}
