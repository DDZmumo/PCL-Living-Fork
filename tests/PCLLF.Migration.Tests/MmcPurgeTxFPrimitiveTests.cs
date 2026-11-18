using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PCLCS;

// Original LF Windows proof fixtures. No game, JVM, GUI, service, ACL or volume setting is changed.
internal static class MmcPurgeTxFPrimitiveTests {
    internal static IEnumerable<(string Name, Action Run)> SmokeCases() {
        yield return ("MMC TxF writer excludes share-delete ADS changes before and after entry-handle closure", FileWriterExclusion);
    }
    internal static IEnumerable<(string Name, Action Run)> BoundaryCases() {
        yield return ("MMC TxF default-only validation preserves preexisting file/directory/root ADS", ExistingStreams);
        yield return ("MMC TxF directory and root deletion retains cross-stream exclusion until commit", DirectoryWriterExclusion);
        yield return ("MMC TxF refuses an already open share-delete named-stream writer", ExistingStreamWriter);
        yield return ("MMC killed TxF producers roll back before commit and only delete approved bytes after commit", KilledTransactions);
    }
    internal static IEnumerable<(string Name, Action Run)> LongPathCases() {
        yield return ("MMC native and transactional purge handles preserve checked logical paths beyond MAX_PATH", LongLocalPath);
    }
    private static void LongLocalPath() {
        using Fixture fixture = new();
        string directory = System.IO.Path.Combine(fixture.Root, new string('a', 80), new string('b', 80), new string('c', 80));
        System.IO.Directory.CreateDirectory(directory); string path = System.IO.Path.Combine(directory, "owned-long-entry.bin");
        System.IO.File.WriteAllText(path, "owned long-path bytes");
        if (path.Length <= 260) throw new Exception("Long-path fixture did not exceed the Win32 MAX_PATH boundary.");
        Console.WriteLine($"LONG_PATH length={path.Length} logical={path}");
        using var parents = new MmcPurgeDirectoryPins(directory);
        string identity; string hash;
        using (var pin = MmcPurgeHandle.Open(path, false)) {
            Equal(path, pin.Path); pin.AssertDefaultDataStreamOnly(); identity = pin.Identity; hash = pin.Hash(CancellationToken.None);
        }
        using (var transaction = new MmcPurgeNtfsTransaction()) {
            using (var writer = transaction.OpenWriter(path, false)) {
                Equal(path, writer.Path); Equal(identity, writer.Identity); Equal(hash, writer.Hash(CancellationToken.None));
                writer.AssertDefaultDataStreamOnly(); writer.StageTransactedDelete();
            }
            transaction.Commit();
        }
        Equal(false, System.IO.File.Exists(path));
    }
    private static void FileWriterExclusion() {
        using Fixture fixture = new(); string path = fixture.File;
        // An existing ADS is not modified or silently adopted, even when an outside writer
        // explicitly shares DELETE and would be compatible with an ordinary file DELETE handle.
        fixture.WriteAds(path, "existing", "unknown stream bytes");
        using (var transaction = new MmcPurgeNtfsTransaction()) using (var writer = transaction.OpenWriter(path, false)) {
            Equal(true, writer.TransactedWriter); Blocked(fixture, path, "existing"); Blocked(fixture, path, "new");
            Denied(writer.AssertDefaultDataStreamOnly);
        }
        Equal("unknown stream bytes", fixture.ReadAds(path, "existing"));
        fixture.DeleteAds(path, "existing");
        using (var transaction = new MmcPurgeNtfsTransaction()) {
            using (var writer = transaction.OpenWriter(path, false)) {
                writer.AssertDefaultDataStreamOnly(); Equal(fixture.BodyHash, writer.Hash(CancellationToken.None));
                Blocked(fixture, path, "new"); writer.StageTransactedDelete();
            }
            // The entry handle is genuinely closed. No ordinary handle remains to deny an
            // ADS writer by share mode; the active KTM transaction must still exclude it.
            Equal(true, System.IO.File.Exists(path)); Blocked(fixture, path, "after-handle-close");
            transaction.Commit();
        }
        Equal(false, System.IO.File.Exists(path));
    }
    private static void ExistingStreams() {
        foreach (string role in new[] { "file", "directory", "root" }) {
            using Fixture fixture = new(); string path = role == "file" ? fixture.File : fixture.EmptyDirectory(role);
            fixture.WriteAds(path, "foreign", "do not delete this stream");
            using (var transaction = new MmcPurgeNtfsTransaction()) using (var writer = transaction.OpenWriter(path, role != "file")) {
                Denied(writer.AssertDefaultDataStreamOnly); Blocked(fixture, path, "foreign");
            }
            Equal("do not delete this stream", fixture.ReadAds(path, "foreign"));
            Equal(true, role == "file" ? System.IO.File.Exists(path) : System.IO.Directory.Exists(path));
        }
    }
    private static void DirectoryWriterExclusion() {
        foreach (string role in new[] { "directory", "root" }) {
            using Fixture fixture = new(); string path = fixture.EmptyDirectory(role);
            using (var transaction = new MmcPurgeNtfsTransaction()) {
                using (var writer = transaction.OpenWriter(path, true)) {
                    writer.AssertDefaultDataStreamOnly(); Blocked(fixture, path, "late");
                    Equal(false, System.IO.Directory.EnumerateFileSystemEntries(path).Any()); writer.StageTransactedDelete();
                }
                Blocked(fixture, path, "after-handle-close");
                // A staged empty-directory deletion must not commit an unapproved child.
                bool created = false;
                try { System.IO.Directory.CreateDirectory(System.IO.Path.Combine(path, "late-child")); created = true; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (created) throw new Exception("An external child appeared after transacted directory deletion; transaction is rolled back, never committed.");
                transaction.Commit();
            }
            Equal(false, System.IO.Directory.Exists(path));
        }
    }
    private static void ExistingStreamWriter() {
        using Fixture fixture = new(); fixture.WriteAds(fixture.File, "busy", "open foreign writer");
        bool blocked = false;
        using (var existing = fixture.OpenAdsWriter(fixture.File, "busy", out int error)) {
            Equal(0, error);
            try { using var transaction = new MmcPurgeNtfsTransaction(); using var writer = transaction.OpenWriter(fixture.File, false); }
            catch (IOException) { blocked = true; }
        }
        Equal(true, blocked); Equal("open foreign writer", fixture.ReadAds(fixture.File, "busy"));
    }
    internal static bool TryRunChild(string[] args, out int code) {
        code = 0; if (args.Length == 0 || args[0] != "--mmc-purge-txf-child") return false;
        try {
            if (args.Length != 5) throw new IOException("Bad owned TxF primitive child arguments.");
            string root = System.IO.Path.GetFullPath(args[1]); string leaf = System.IO.Path.GetFileName(root);
            if (!leaf.StartsWith(Fixture.Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(leaf[Fixture.Prefix.Length..], "N", out _) ||
                System.IO.File.ReadAllText(System.IO.Path.Combine(root, "owner.txt")) != args[2] || System.IO.Path.GetDirectoryName(args[4]) != root)
                throw new IOException("TxF primitive child is outside its parent's GUID/nonce fixture.");
            string path = System.IO.Path.Combine(root, "entry.bin");
            using var transaction = new MmcPurgeNtfsTransaction();
            using (var writer = transaction.OpenWriter(path, false)) {
                if (args[3] == "writer") Hold();
                writer.AssertDefaultDataStreamOnly(); writer.StageTransactedDelete();
            }
            if (args[3] == "staged-closed") Hold();
            transaction.Commit(); if (args[3] == "committed") Hold();
            code = 97;
            void Hold() {
                string pending = args[4] + "." + args[2] + ".tmp";
                System.IO.File.WriteAllText(pending, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                System.IO.File.Move(pending, args[4]);
                using ManualResetEventSlim hold = new(); hold.Wait();
            }
        } catch (Exception error) { Console.Error.WriteLine(error); code = 98; }
        return true;
    }
    private static void KilledTransactions() {
        foreach (string phase in new[] { "writer", "staged-closed", "committed" }) {
            using Fixture fixture = new(); if (phase == "writer") fixture.WriteAds(fixture.File, "unapproved", "must survive child death");
            string ready = System.IO.Path.Combine(fixture.Root, "ready.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "--mmc-purge-txf-child", fixture.Root, fixture.Nonce, phase, ready }) start.ArgumentList.Add(value);
            using var child = Process.Start(start) ?? throw new IOException("Owned TxF primitive child did not start."); var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            try {
                var timer = Stopwatch.StartNew();
                while (!System.IO.File.Exists(ready)) {
                    if (child.HasExited) throw new IOException("Owned TxF primitive child exited: " + errors.GetAwaiter().GetResult());
                    if (timer.ElapsedMilliseconds > 20000) throw new TimeoutException("Owned TxF primitive child did not reach its checkpoint."); Thread.Sleep(2);
                }
                Equal(child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), System.IO.File.ReadAllText(ready));
                if (phase != "committed") Blocked(fixture, fixture.File, phase == "writer" ? "unapproved" : "late");
                // Only this Process.Start result is ever terminated; no user Main PID is discovered or used.
                child.Kill(); if (!child.WaitForExit(5000)) throw new IOException("Owned TxF primitive child did not exit.");
                if (phase == "committed") Equal(false, System.IO.File.Exists(fixture.File));
                else {
                    Equal(fixture.BodyHash, Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(fixture.File))));
                    if (phase == "writer") Equal("must survive child death", fixture.ReadAds(fixture.File, "unapproved"));
                    fixture.WriteAds(fixture.File, "after-rollback", "writer lock released");
                }
            } finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
        }
    }
    private static void Blocked(Fixture fixture, string path, string stream) {
        using var handle = fixture.OpenAdsWriter(path, stream, out int error);
        if (!handle.IsInvalid || error is not (32 or 6800)) throw new Exception($"Native FILE_SHARE_READ|WRITE|DELETE ADS write was not transactionally excluded: {stream}, error={error}.");
        Console.WriteLine($"TXF_ADS_BLOCKED stream={stream} win32={error} share=Read|Write|Delete");
    }
    private static void Denied(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Unknown NTFS stream was accepted by a default-only preview."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private sealed class Fixture : IDisposable {
        internal const string Prefix = "pcllf-mmc-purge-txf-";
        internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        internal readonly string Nonce = Guid.NewGuid().ToString("N");
        internal string File => System.IO.Path.Combine(Root, "entry.bin");
        internal string BodyHash { get; }
        internal Fixture() {
            System.IO.Directory.CreateDirectory(Root); System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "owner.txt"), Nonce);
            System.IO.File.WriteAllText(File, "owned primitive file bytes"); BodyHash = Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(File)));
        }
        internal string EmptyDirectory(string role) { string path = System.IO.Path.Combine(Root, role); System.IO.Directory.CreateDirectory(path); return path; }
        private string StreamPath(string path, string name) {
            string full = System.IO.Path.GetFullPath(path);
            if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new IOException("ADS proof escaped its owned GUID fixture.");
            return full + ":" + name;
        }
        internal SafeFileHandle OpenAdsWriter(string path, string stream, out int error) {
            var handle = CreateFileW(StreamPath(path, stream), 0x40000000, 7, IntPtr.Zero, 4, 0, IntPtr.Zero); error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0; return handle;
        }
        internal void WriteAds(string path, string stream, string text) {
            using var handle = OpenAdsWriter(path, stream, out int error);
            if (handle.IsInvalid) throw new IOException("Cannot write an owned ADS proof fixture.", new Win32Exception(error));
            RandomAccess.Write(handle, Encoding.UTF8.GetBytes(text), 0);
        }
        internal string ReadAds(string path, string stream) => System.IO.File.ReadAllText(StreamPath(path, stream));
        internal void DeleteAds(string path, string stream) => System.IO.File.Delete(StreamPath(path, stream));
        public void Dispose() {
            string full = System.IO.Path.GetFullPath(Root);
            if (System.IO.Path.GetDirectoryName(full) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()) ||
                !Guid.TryParseExact(System.IO.Path.GetFileName(full)[Prefix.Length..], "N", out _) || System.IO.File.ReadAllText(System.IO.Path.Combine(full, "owner.txt")) != Nonce)
                throw new IOException("Unsafe TxF primitive fixture cleanup root.");
            System.IO.Directory.Delete(full, true);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
