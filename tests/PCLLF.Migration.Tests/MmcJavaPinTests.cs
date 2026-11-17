using System.Text;
using PCLCS;

internal static class MmcJavaPinTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC Java durable pin survives disposal and requires a matching verified receipt", DurablePin);
        yield return ("MMC Java maintenance excludes pin acquisition and honors cancellation", Maintenance);
        yield return ("MMC Java corrupted pin is preserved and blocks launcher maintenance", Corruption);
        yield return ("MMC Java maintenance rejects descendant links before writes or cleanup", MaintenanceLinks);
    }
    private static void DurablePin() {
        using Fixture f = new();
        using (var pin = f.Store.Acquire(f.Session, f.Image, f.Instance, f.VersionRoot)) { }
        Throws<IOException>(() => new MmcJavaPinStore(f.Pins).EnterMaintenance(f.Home, TimeSpan.Zero));
        var receipt = f.Receipt;
        Throws<IOException>(() => f.Store.ReleaseVerified(receipt with { InstanceId = Guid.NewGuid() }));
        f.Store.ReleaseVerified(receipt);
        using var allowed = f.Store.EnterMaintenance(f.Home, TimeSpan.Zero);
    }
    private static void Maintenance() {
        using Fixture f = new();
        using var maintenance = f.Store.EnterMaintenance(f.Home, TimeSpan.Zero);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(80));
        Throws<OperationCanceledException>(() => f.Store.Acquire(f.Session, f.Image, f.Instance, f.VersionRoot, cancel.Token));
        if (Directory.Exists(Path.Combine(f.Pins, "pins")) && Directory.GetFiles(Path.Combine(f.Pins, "pins"), "*.json", SearchOption.AllDirectories).Length != 0)
            throw new Exception("Canceled acquisition published a Java pin.");
    }
    private static void Corruption() {
        using Fixture f = new();
        using var pin = f.Store.Acquire(f.Session, f.Image, f.Instance, f.VersionRoot);
        string path = Directory.GetFiles(Path.Combine(f.Pins, "pins"), "*.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(path, "{}");
        Throws<IOException>(pin.CompleteVerifiedExit);
        Throws<IOException>(() => f.Store.EnterMaintenance(f.Home, TimeSpan.Zero));
        if (File.ReadAllText(path) != "{}") throw new Exception("Unconfirmed pin was silently deleted.");
    }
    private static void MaintenanceLinks() {
        using Fixture f = new();
        string outside = Path.Combine(f.Pins, "outside"), link = Path.Combine(f.Home, "linked");
        Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        if (OperatingSystem.IsWindows()) {
            string script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Junction fixture did not start.");
            if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Junction fixture timed out."); }
            if (process.ExitCode != 0) throw new Exception("Junction fixture creation failed.");
        } else Directory.CreateSymbolicLink(link, outside);
        try {
            using var maintenance = f.Store.EnterMaintenance(f.Home, TimeSpan.Zero);
            Throws<IOException>(() => MmcJavaPinStore.ValidateMaintenanceTarget(f.Home));
            Throws<IOException>(() => f.Store.EnterMaintenance(link, TimeSpan.Zero));
            if (File.ReadAllText(Path.Combine(outside, "keep.txt")) != "keep") throw new Exception("Link target was changed.");
        } finally { Directory.Delete(link, false); }
    }
    private static void Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Fixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pcllf-java-pin-" + Guid.NewGuid().ToString("N"));
        internal string Home => Path.Combine(root, "jdk");
        internal string Pins => Path.Combine(root, "pins");
        internal string VersionRoot => Path.Combine(root, "versions", "managed");
        internal Guid Session { get; } = Guid.NewGuid();
        internal Guid Instance { get; } = Guid.NewGuid();
        internal MmcExecutableIdentity Image { get; }
        internal MmcJavaPinStore Store { get; }
        internal MmcSessionReceipt Receipt => new(1, Session, Instance, VersionRoot, 1, new string('a', 64),
            new string('b', 32), Image, Image);
        internal Fixture() {
            Directory.CreateDirectory(Path.Combine(Home, "bin")); Directory.CreateDirectory(VersionRoot);
            string file = Path.Combine(Home, "bin", "java.exe"); File.WriteAllText(file, "inert image fixture", Encoding.ASCII);
            Image = MmcSessions.ObserveExecutable(file); Store = new(Pins);
        }
        public void Dispose() {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "pcllf-java-pin-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid Java pin fixture cleanup root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
