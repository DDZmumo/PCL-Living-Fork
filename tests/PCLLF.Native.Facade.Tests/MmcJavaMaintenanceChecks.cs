using System.Text;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcJavaMaintenanceChecks {
    internal static void Run(FixtureEnvironment environment) {
        string runtime = Path.Combine(environment.Data, "runtime"), home = Path.Combine(runtime, "fixture-jre");
        string bin = Path.Combine(home, "bin"), instance = Path.Combine(environment.Data, "versions", "pinned");
        Directory.CreateDirectory(bin); Directory.CreateDirectory(instance);
        string executable = Path.Combine(bin, "java.exe");
        File.WriteAllText(executable, "inert maintenance fixture image", Encoding.ASCII);
        var pins = new MmcJavaPinStore(Path.Combine(runtime, ".pcllf-mmc-pins"));
        var identity = MmcSessions.ObserveExecutable(executable);
        using var pin = pins.Acquire(Guid.NewGuid(), identity, Guid.NewGuid(), instance);
        var plan = new LegacyJavaDownloadPlan { BaseDirectory = home };
        var blocked = Operation(plan, pins, runtime);
        Throws<IOException>(() => blocked.Wait(default, null));
        Native.Check(File.ReadAllText(executable) == "inert maintenance fixture image", "Pinned Java was changed or cleaned.");
        pin.CompleteVerifiedExit();

        // Cancellation before admission cannot clean an existing installation or start NetManager.
        var canceled = Operation(plan, pins, runtime);
        canceled.Cancel();
        Throws<OperationCanceledException>(() => canceled.Wait(default, null));
        Native.Check(File.Exists(executable), "Canceled-before-start Java operation deleted existing files.");
        using (pins.EnterMaintenance(home, TimeSpan.Zero)) { }
        Throws<InvalidOperationException>(() => canceled.Wait(default, null));

        var escape = new LegacyJavaDownloadPlan { BaseDirectory = Path.Combine(environment.Data, "outside-runtime") };
        Throws<IOException>(() => Operation(escape, pins, runtime));
        plan.Files.Add(new LegacyJavaDownloadFile { Destination = Path.Combine(runtime, "other", "java.exe"), Sources = [] });
        Throws<IOException>(() => Operation(plan, pins, runtime));
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-java-maintenance: genuine Main Java callback blocks pinned homes, preserves pre-start cancellation, and rejects target escapes; no Java process or network started.");
    }

    private static LegacyJavaDownloadOperation Operation(LegacyJavaDownloadPlan plan, MmcJavaPinStore pins, string runtime) =>
        (LegacyJavaDownloadOperation)Native.Call(typeof(ModJava), "CreateProtectedJavaDownload", null, plan, pins, runtime)!;
    private static void Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
