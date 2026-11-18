using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>Durable cooperative pins. External JDK installers are outside this service's exclusion guarantee.</summary>
public sealed class MmcJavaPinStore {
    private readonly string root;
    private sealed record PinRecord(int SchemaVersion, Guid SessionId, Guid InstanceId, string InstanceRoot,
        string JavaHome, MmcExecutableIdentity Identity);

    public MmcJavaPinStore(string directory) {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Java pin directory must be absolute.", nameof(directory));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        MmcInstancePaths.EnsureNoLinks(root);
    }

    public IMmcJavaSessionPin Acquire(Guid sessionId, MmcExecutableIdentity expected,
        Guid instanceId, string instanceRoot, CancellationToken cancellationToken = default) {
        if (sessionId == Guid.Empty || instanceId == Guid.Empty) throw new ArgumentException("Pin identities cannot be empty.");
        string home = JavaHome(expected.ExecutablePath);
        using FileStream gate = EnterGate(home, TimeSpan.FromSeconds(10), cancellationToken);
        if (!MmcSessions.SameExecutable(MmcSessions.ObserveExecutable(expected.ExecutablePath), expected))
            throw new IOException("Java changed before its protected identity check.");
        string path = PinPath(home, sessionId);
        var record = new PinRecord(1, sessionId, instanceId, MmcInstancePaths.Resolve(instanceRoot).InstanceRoot, home, expected);
        WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(record));
        return new Pin(this, record, path);
    }

    /// <summary>Held through launcher-owned writes/deletes to this installation, including cancellation drain.</summary>
    public IDisposable EnterMaintenance(string javaHome, TimeSpan timeout, CancellationToken cancellationToken = default) {
        string home = NormalizeHome(javaHome);
        FileStream gate = EnterGate(home, timeout, cancellationToken);
        try {
            string pins = PinsDirectory(home);
            MmcInstancePaths.EnsureNoLinks(pins);
            if (Directory.Exists(pins)) {
                if (Directory.EnumerateFileSystemEntries(pins).Any())
                    throw new IOException("This Java installation has an active or unconfirmed MMC session.");
            } else if (File.Exists(pins)) throw new IOException("Java pin storage has an invalid directory role.");
            return gate;
        } catch { gate.Dispose(); throw; }
    }

    /// <summary>Read-only revalidation at maintenance I/O; the caller must retain its maintenance lease.</summary>
    public static void ValidateMaintenanceTarget(string javaHome) {
        string home = NormalizeHome(javaHome);
        if (!Directory.Exists(home)) return;
        Stack<string> directories = new(); directories.Push(home);
        int entries = 0;
        while (directories.TryPop(out string? directory)) {
            MmcInstancePaths.EnsureNoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                if (++entries > 65_536) throw new IOException("Java maintenance target exceeds its entry budget.");
                MmcInstancePaths.EnsureNoLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) directories.Push(path);
            }
        }
    }

    /// <summary>Only the session reconciler may call this after positive exit proof.</summary>
    public void ReleaseVerified(MmcSessionReceipt receipt) {
        ArgumentNullException.ThrowIfNull(receipt);
        string home = JavaHome(receipt.JavaIdentity.ExecutablePath);
        using FileStream gate = EnterGate(home, TimeSpan.FromSeconds(10), CancellationToken.None);
        string path = PinPath(home, receipt.SessionId);
        PinRecord? record = Read(path);
        if (record is null) return;
        if (record.SessionId != receipt.SessionId || record.InstanceId != receipt.InstanceId ||
            !MmcSessions.SameExecutable(record.Identity, receipt.JavaIdentity) ||
            !SamePath(record.InstanceRoot, receipt.CanonicalRoot) || !SamePath(record.JavaHome, home))
            throw new IOException("Java pin does not match the verified session receipt.");
        File.Delete(path);
    }

    private void Complete(PinRecord expected, string path) {
        using FileStream gate = EnterGate(expected.JavaHome, TimeSpan.FromSeconds(10), CancellationToken.None);
        PinRecord? actual = Read(path);
        if (actual is null) return;
        if (actual != expected) throw new IOException("Java pin was modified; preserving it for recovery.");
        File.Delete(path);
    }

    private FileStream EnterGate(string home, TimeSpan timeout, CancellationToken token) {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        string folder = Path.Combine(root, "gates");
        MmcInstancePaths.EnsureNoLinks(folder); Directory.CreateDirectory(folder); MmcInstancePaths.EnsureNoLinks(folder);
        string path = Path.Combine(folder, HomeKey(home) + ".lock");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) {
                if (timeout != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout) throw new IOException("Java installation is being maintained by another launcher operation.");
                if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
            }
        }
    }

    private string PinPath(string home, Guid id) => Path.Combine(PinsDirectory(home), id.ToString("N") + ".json");
    private string PinsDirectory(string home) => Path.Combine(root, "pins", HomeKey(home));
    private static string HomeKey(string home) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? home.ToUpperInvariant() : home)));
    private static string JavaHome(string executable) {
        string file = MmcCanonicalPaths.ForExistingFile(Path.GetFullPath(executable));
        string directory = Path.GetDirectoryName(file) ?? throw new IOException("Java executable directory is missing.");
        return NormalizeHome(string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)! : directory);
    }
    private static string NormalizeHome(string home) {
        if (!Path.IsPathFullyQualified(home)) throw new IOException("Java home must be absolute.");
        string input = Path.GetFullPath(home);
        MmcInstancePaths.EnsureNoLinks(input);
        string full = MmcCanonicalPaths.ForTrustedRoot(input);
        MmcInstancePaths.EnsureNoLinks(full); return full;
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static PinRecord? Read(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        if (MmcInstancePaths.Attributes(path) is null) return null;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024) throw new IOException("Java pin exceeds its record budget.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        try {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            PinRecord record = JsonSerializer.Deserialize<PinRecord>(bytes) ?? throw new IOException("Java pin is empty.");
            if (record.SchemaVersion != 1 || record.SessionId == Guid.Empty || record.InstanceId == Guid.Empty ||
                record.Identity is null || !MmcDurableFiles.IsHash(record.Identity.Sha256) ||
                string.IsNullOrWhiteSpace(record.Identity.ExecutablePath) || !Path.IsPathFullyQualified(record.Identity.ExecutablePath) ||
                !Path.IsPathFullyQualified(record.InstanceRoot) || !Path.IsPathFullyQualified(record.JavaHome))
                throw new IOException("Java pin is invalid or unsupported.");
            return record;
        } catch (JsonException error) { throw new IOException("Java pin JSON is invalid.", error); }
    }
    private static void RejectDuplicates(JsonElement value) {
        if (value.ValueKind != JsonValueKind.Object) return;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) {
            if (!seen.Add(property.Name)) throw new IOException("Java pin has duplicate fields.");
            RejectDuplicates(property.Value);
        }
    }
    private static void WriteNew(string path, byte[] bytes) {
        MmcInstancePaths.EnsureNoLinks(path);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder); MmcInstancePaths.EnsureNoLinks(folder);
        string temporary = Path.Combine(folder, ".pin-" + Guid.NewGuid().ToString("N"));
        try {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, false);
        } finally { MmcInstancePaths.EnsureNoLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Pin(MmcJavaPinStore owner, PinRecord record, string path) : IMmcJavaSessionPin {
        private bool completed;
        public MmcExecutableIdentity Identity => record.Identity;
        public void KeepForRecovery() { }
        public void CompleteVerifiedExit() { if (completed) return; owner.Complete(record, path); completed = true; }
        public void Dispose() { /* An uncertain session always retains its durable pin. */ }
    }
}
