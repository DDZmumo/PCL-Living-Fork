using PCLCS;

internal static class PermanentResourceDeletionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("direct resource deletion removes selected file and nested world without recycle copies", DeleteSelection);
        yield return ("direct resource deletion preflights every selection and rejects root outside and missing paths", SelectionBoundary);
        yield return ("direct resource deletion honors cancellation before mutation", Cancellation);
        yield return ("upgrade removes every legacy resource kind including malformed records but preserves backups", LegacyCleanup);
        yield return ("shared cache cleanup finds all PCL ResourceRecycle directories and is repeatable", SharedCacheCleanup);
        if (OperatingSystem.IsWindows()) {
            yield return ("shared cache cleanup continues after a locked bin and retries it later", LockedCacheRetry);
            yield return ("shared cache cleanup tolerates a directory disappearing during an error callback", DisappearingCacheDirectory);
            yield return ("direct resource transaction rejects stale selections before deleting any source", TransactionPreflight);
        }
    }
    private static void DisappearingCacheDirectory() {
        using Fixture f = new(); string cache = f.Dir("cache"), locked = f.Put("cache/racing/PCL/ResourceRecycle/content");
        string other = f.Put("cache/other/PCL/ResourceRecycle/content");
        using FileStream held = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        List<Exception> errors = new();
        PermanentResourceDeletion.CleanupLegacyCache(cache, default, (error, folder) => {
            errors.Add(error); held.Dispose();
            if (Directory.Exists(folder)) PermanentResourceDeletion.Delete(f.Root, new[] { folder }, default);
        });
        Check(errors.Count==1 && errors.All(error=>error is not FileNotFoundException and not DirectoryNotFoundException),
            "A vanished temporary cache directory generated repeated missing-path warnings.");
        Check(!File.Exists(other), "The cache race stopped cleanup of other recycle directories.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action work) {
        try { work(); } catch (IOException) { return; }
        throw new Exception("Unsafe deletion was accepted.");
    }
    private static void DeleteSelection() {
        using Fixture f = new();
        string root = f.Dir("resources"), file = f.Put("resources/selected.zip"), world = f.Dir("resources/world");
        string nested = f.Put("resources/world/region/data.bin"), keep = f.Put("resources/keep.zip");
        File.SetAttributes(nested, FileAttributes.ReadOnly);
        PermanentResourceDeletion.Delete(root, new[] { file, world, nested }, default);
        Check(!File.Exists(file) && !Directory.Exists(world), "Selected resources survived direct deletion.");
        Check(File.Exists(keep) && !Directory.Exists(Path.Combine(f.Root, "PCL", "ResourceRecycle")), "Direct deletion changed an unselected resource or created a recycle bin.");
    }
    private static void SelectionBoundary() {
        using Fixture f = new(); string root = f.Dir("resources"), keep = f.Put("resources/keep.zip"), outside = f.Put("resources-extra/foreign.zip");
        foreach (string bad in new[] { root, outside, Path.Combine(root, "missing.zip") }) {
            Reject(() => PermanentResourceDeletion.Delete(root, new[] { keep, bad }, default));
            Check(File.Exists(keep) && File.Exists(outside), "Invalid later member caused a partial deletion.");
        }
    }
    private static void Cancellation() {
        using Fixture f = new(); string root = f.Dir("resources"), file = f.Put("resources/keep.zip");
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        try { PermanentResourceDeletion.Delete(root, new[] { file }, cancel.Token); throw new Exception("Canceled deletion succeeded."); }
        catch (OperationCanceledException) { }
        Check(File.Exists(file), "Canceled deletion modified its source.");
    }
    private static void LegacyCleanup() {
        using Fixture f = new(); string game = f.Dir("game");
        foreach (string kind in new[] { "Saves", "Screenshots", "ResourcePacks", "ShaderPacks", "Schematics" })
            f.Put("game/PCL/ResourceRecycle/" + kind + "/malformed-record/content");
        f.Put("game/PCL/ResourceRecycle/unrecognized/resource.json", "{broken and obsolete}");
        string world = f.Put("game/saves/current/level.dat"), backup = f.Put("game/PCL/WorldBackups/backup.zip"), point = f.Put("game/PCL/EnvironmentHistory/point.json");
        Check(PermanentResourceDeletion.CleanupLegacy(game, default), "Old recycle store was not removed.");
        Check(!Directory.Exists(Path.Combine(game, "PCL", "ResourceRecycle")), "Old payload or corrupt records survived.");
        Check(new[] { world, backup, point }.All(File.Exists), "Cleanup touched current worlds or backup/history data.");
        Check(!PermanentResourceDeletion.CleanupLegacy(game, default), "Repeated cleanup was not idempotent.");
    }
    private static void SharedCacheCleanup() {
        using Fixture f = new(); string cache = f.Dir("cache/PCL");
        foreach (string bin in new[] { "cache/PCL/ResourceRecycle", "cache/PCL/extract/one/PCL/ResourceRecycle", "cache/PCL/other/two/pcl/ResourceRecycle" }) f.Put(bin + "/bad-record/content");
        string unrelated = f.Put("cache/PCL/other/ResourceRecycle/keep"), download = f.Put("cache/PCL/Download/keep.zip"), backup = f.Put("cache/PCL/extract/one/PCL/WorldBackups/keep.zip"), external = f.Put("outside/PCL/ResourceRecycle/keep");
        List<Exception> errors = new();
        Check(PermanentResourceDeletion.CleanupLegacyCache(cache, default, (error, _) => errors.Add(error)) == 3 && errors.Count == 0, "Cache cleanup missed a bin or failed.");
        Check(new[] { unrelated, download, backup, external }.All(File.Exists), "Cache cleanup exceeded its explicit scope.");
        Check(PermanentResourceDeletion.CleanupLegacyCache(cache, default) == 0, "Repeated cache cleanup changed data.");
    }
    private static void LockedCacheRetry() {
        using Fixture f = new(); string cache = f.Dir("cache"), locked = f.Put("cache/one/PCL/ResourceRecycle/content"), other = f.Put("cache/two/PCL/ResourceRecycle/content");
        List<Exception> errors = new();
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Check(PermanentResourceDeletion.CleanupLegacyCache(cache, default, (error, _) => errors.Add(error)) == 1, "An occupied bin stopped cleanup of other bins.");
            Check(File.Exists(locked) && !File.Exists(other) && errors.Count > 0, "Occupied bin failure was not retained and reported.");
        }
        Check(PermanentResourceDeletion.CleanupLegacyCache(cache, default) == 1 && !File.Exists(locked), "Later cleanup failed to retry an occupied bin.");
    }
    private static void TransactionPreflight() {
        using Fixture f = new(); string root = f.Dir("resources"), first = f.Put("resources/first.schem"), second = f.Put("resources/second.schem");
        Entry a = Snapshot(first), b = Snapshot(second); File.Delete(second);
        Reject(() => new InstanceResourceTransactions(text => text).DeleteMany(root, new[] { a, b }, default));
        Check(File.Exists(first), "Stale selection deleted an earlier valid source.");
    }
    private sealed class Entry : InstanceResourceEntryData { }
    private static Entry Snapshot(string path) => new() { FullPath = path, Name = Path.GetFileName(path), Length = new FileInfo(path).Length, ModifiedUtc = File.GetLastWriteTimeUtc(path) };
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PCLLF-DirectDelete-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        private string Owned(string relative) {
            string path = Path.GetFullPath(Path.Combine(Root, relative));
            Check(path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Unsafe deletion fixture path."); return path;
        }
        internal string Dir(string relative) { string path = Owned(relative); Directory.CreateDirectory(path); return path; }
        internal string Put(string relative, string content = "keep-or-remove") { string path = Owned(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
        public void Dispose() {
            Check(Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-DirectDelete-[0-9a-f]{32}\z"), "Unsafe deletion fixture cleanup.");
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
