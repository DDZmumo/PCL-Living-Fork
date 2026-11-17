using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PCL;

namespace PCLLF.Native.Facade.Tests;

// The genuine LF downloader starts three intentionally perpetual foreground
// manager threads. UX-P1..P4 and dependency downloads keep their complete platform
// context in a dedicated process until the owning parent ends that lifetime.
// No production loop, background flag, cancellation policy or timeout changes.
internal static class ModpackDownloadProcess
{
    internal const string ReceiptName = "modpack-download-complete.json";
    internal const string DependencyFlag = "--mod-dependency-download-child";
    private const string Kind = "PCLLF.NativeModpackDownloadCompletedWithRetainedManagers";
    private const string DependencyKind = "PCLLF.NativeModDependencyDownloadCompletedWithRetainedManagers";
    private static readonly string[] ManagerNames = ["NetManager ThreadStarter 0", "NetManager ThreadStarter 1", "NetManager StatRefresher"];
    private static readonly string[] Checks = ["UX-P1", "UX-P2", "UX-P2-bytes", "UX-P3", "UX-P4", "task-and-network-threads-stopped", "exclusive-file-reopen", "loopback-listener-and-responses-stopped", "no-forbidden-io", "no-windows"];
    private static readonly string[] DependencyChecks = ["required-and-pinned-parser", "compatible-transitive-conflict-cancel-plan", "native-modal-and-switch", "unchanged-cancel", "download-install-enable", "primary-only-switch-off", "task-and-network-threads-stopped", "loopback-listener-and-responses-stopped", "no-forbidden-io", "no-windows"];
    private static FixtureEnvironment? retainedEnvironment;

    internal static void RunParent()
    {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        string nonce = Guid.NewGuid().ToString("N");
        Native.Check(Program.RunDownloadChild(nonce) == 0, "UX dedicated download lifetime failed.");
    }
    internal static void RunDependencyParent() {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        Native.Check(Program.RunDownloadChild(Guid.NewGuid().ToString("N"), dependencies: true) == 0, "Dependency download lifetime failed.");
    }

    internal static int RunChild(string[] args)
    {
        Native.Check(args.Length == 5 && (args[0] is "--modpack-download-child" or DependencyFlag) && args[2] == "--isolated" && args[3] == "--fixture", "Invalid dedicated download child arguments.");
        bool dependencies = args[0] == DependencyFlag;
        string nonce = args[1], root = args[4];
        Native.Check(System.Text.RegularExpressions.Regex.IsMatch(nonce, "^[a-f0-9]{32}$"), "Invalid download ownership nonce.");
        Program.ValidateRoot(root);
        ModpackUpdateCleanup.VerifyIsolatedChild();
        retainedEnvironment = new FixtureEnvironment(root);
        // Observe the existing platform primitive without replacing any work or
        // thread metadata. All algorithm and real foreground creation remain LF.
        object host = Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
        FieldInfo factoryField = Native.Field(host.GetType(), "RunNewThread");
        var original = (Func<Action, string, ThreadPriority, Thread>)factoryField.GetValue(host)!;
        ConcurrentBag<Thread> threads = new();
        factoryField.SetValue(host, new Func<Action, string, ThreadPriority, Thread>((work, name, priority) =>
        {
            Thread thread = original(work, name, priority);
            threads.Add(thread);
            return thread;
        }));
        // Do not restore this primitive or any FixtureEnvironment guard while
        // the real permanent manager loops still use the same host/settings.
        if (dependencies) {
            ModDownloadDependencyChecks.Run(retainedEnvironment);
            VisualAssetChecks.Pump(() => threads.Where(thread => !ManagerNames.Contains(thread.Name, StringComparer.Ordinal)).All(thread => !thread.IsAlive),
                "Dependency download transient threads did not exit.", 5000);
        } else ModpackUpdateUxChecks.DownloadProgress(Path.Combine(retainedEnvironment.Data, "loopback"));
        retainedEnvironment.AssertNoForbiddenIo();
        Native.Check(System.Windows.Application.Current.Windows.Count == 0, "Download child left a window open.");
        Native.Check(Volatile.Read(ref ModNet.NetTaskThreadCount) == 0 && ModNet.NetManager.FileRemain == 0 &&
            ModNet.NetManager.Tasks.All(task => task.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed), "Download manager retained actual unfinished work.");
        Thread[] observed = threads.ToArray();
        Thread[] managers = observed.Where(thread => ManagerNames.Contains(thread.Name, StringComparer.Ordinal)).ToArray();
        Native.Check(managers.Length == 3 && managers.Select(thread => thread.Name).Distinct(StringComparer.Ordinal).Count() == 3 &&
            managers.All(thread => thread.IsAlive && !thread.IsBackground) &&
            observed.Except(managers).All(thread => !thread.IsAlive), "Download child has unknown/live transient threads or did not retain exactly three original foreground managers.");
        Receipt result = new(dependencies ? DependencyKind : Kind, nonce, Environment.ProcessId, Native.Full(root), MainHash(), Native.Main.ManifestModule.ModuleVersionId,
            dependencies ? DependencyChecks : Checks, managers.Select(thread => new Manager(thread.Name!, thread.ManagedThreadId, thread.IsAlive, thread.IsBackground)).ToArray(), observed.Length - managers.Length);
        string temporary = Path.Combine(root, ReceiptName + ".pending");
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, result);
            output.Flush(true);
        }
        Console.WriteLine((dependencies ? "DIAG dedicated dependency download completed workflow and IO/listener barriers; retained original foreground managers=" : "DIAG dedicated download completed all original UX assertions and IO/handle/listener barriers; retained original foreground managers=") + string.Join(",", managers.Select(thread => thread.Name)));
        Console.Out.Flush(); Console.Error.Flush();
        File.Move(temporary, Path.Combine(root, ReceiptName));
        // Parent must validate the bound receipt and own termination. Never
        // return through IDisposable and restore a live manager's context.
        using ManualResetEventSlim lifetime = new(false);
        lifetime.Wait();
        throw new InvalidOperationException("Dedicated download lifetime gate unexpectedly returned.");
    }

    internal static void RequireReceipt(string path, string nonce, int processId, string root, bool dependencies = false)
    {
        FileInfo file = new(path);
        Native.Check(file.Length is > 0 and <= 65536 && (file.Attributes & FileAttributes.ReparsePoint) == 0, "Invalid dedicated download receipt file.");
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Receipt result = JsonSerializer.Deserialize<Receipt>(input) ?? throw new InvalidDataException("Empty dedicated download receipt.");
        Native.Check(result.Kind == (dependencies ? DependencyKind : Kind) && result.Nonce == nonce && result.ProcessId == processId && result.Root == Native.Full(root) &&
            result.MainSha256 == MainHash() && result.MainMvid == Native.Main.ManifestModule.ModuleVersionId, "Dedicated download receipt is not bound to this owned child and actual Main.");
        Native.Check(result.Checks is not null && result.Checks.SequenceEqual(dependencies ? DependencyChecks : Checks) && result.StoppedTransientThreads > 0 &&
            result.Managers is { Length: 3 } && result.Managers.Select(manager => manager.Name).Order(StringComparer.Ordinal).SequenceEqual(ManagerNames.Order(StringComparer.Ordinal)) &&
            result.Managers.All(manager => manager.ManagedThreadId > 0 && manager.Alive && !manager.Background) &&
            result.Managers.Select(manager => manager.ManagedThreadId).Distinct().Count() == 3, "Dedicated download receipt lacks exact original assertions/barriers/three retained managers.");
    }

    private static string MainHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Native.Main.Location))).ToLowerInvariant();
    private sealed record Manager(string Name, int ManagedThreadId, bool Alive, bool Background);
    private sealed record Receipt(string Kind, string Nonce, int ProcessId, string Root, string MainSha256, Guid MainMvid, string[] Checks, Manager[] Managers, int StoppedTransientThreads);
}
