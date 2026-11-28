using PCLCS;

internal static class LocalResourceSortTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("local mod default sort preserves search relevance without file access", DefaultIsReadFree);
        yield return ("local mod time sort uses creation rather than last-write time in both directions", CreationNotModification);
        yield return ("local mod missing or unreadable timestamps stay last in both directions", UnknownLast);
        yield return ("local mod same-time and missing candidates have deterministic filename/path ties", StableTies);
        yield return ("local mod time sort refreshes cached metadata and handles disabled rename", CachedAndDisabled);
        yield return ("local mod sorting preserves bytes, metadata and source collection", ReadOnly);
        yield return ("local resource file-name sort compares numbers by value, ignores case and reverses exactly", NameOrder);
        yield return ("local mod sort rejects invalid orders before enumerating or reading files", InvalidOrder);
        yield return ("local resource timestamp keys sort known values first with stable ties and single reads", TimestampKeys);
        yield return ("local resource creation sorting accepts real directories and missing directory entries", DirectoryEntries);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Require(expected.SequenceEqual(actual), "Unexpected mod order.");
    private static void DefaultIsReadFree() {
        string[] relevance = ["second-best-match.jar", "exact-match.jar", "first-alphabetically.jar"];
        string[] before = relevance.ToArray();
        var ordered = LocalResourceSort.Order(relevance, LocalResourceSortOrder.Default, _ => throw new Exception("Default touched the filesystem callback."));
        Equal(before, ordered); Equal(before, relevance);
    }

    private static void CreationNotModification() {
        using Fixture f = new();
        FileInfo old = f.File("z-old.jar"), recent = f.File("a-recent.jar");
        f.SetCreated(old, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        f.SetCreated(recent, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(old.FullName, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(recent.FullName, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        old.Refresh(); recent.Refresh();
        FileInfo[] ascending = old.CreationTimeUtc < recent.CreationTimeUtc ? [old, recent] :
            old.CreationTimeUtc > recent.CreationTimeUtc ? [recent, old] : [recent, old];
        FileInfo[] descending = old.CreationTimeUtc > recent.CreationTimeUtc ? [old, recent] :
            old.CreationTimeUtc < recent.CreationTimeUtc ? [recent, old] : [recent, old];
        Equal(ascending, LocalResourceSort.Order(new[] { old, recent }, LocalResourceSortOrder.AddedOldest, item => item));
        Equal(descending, LocalResourceSort.Order(new[] { old, recent }, LocalResourceSortOrder.AddedNewest, item => item));
        Require(old.LastWriteTimeUtc > recent.LastWriteTimeUtc, "Fixture failed to reverse modification-time order.");
        if (OperatingSystem.IsWindows()) {
            Equal(new[] { old, recent }, ascending);
            Equal(new[] { recent, old }, descending);
        }
    }

    private static void UnknownLast() {
        using Fixture f = new();
        FileInfo known = f.File("z-known.jar"), missing = new(Path.Combine(f.Root, "a-missing.jar"));
        foreach (LocalResourceSortOrder mode in new[] { LocalResourceSortOrder.AddedNewest, LocalResourceSortOrder.AddedOldest }) {
            Equal(new[] { known, missing }, LocalResourceSort.Order(new[] { missing, known }, mode, item => item));
            string[] values = ["io", "denied", "known"];
            var sorted = LocalResourceSort.Order(values, mode, item => item switch {
                "io" => throw new IOException("Synthetic metadata read failure"),
                "denied" => throw new UnauthorizedAccessException("Synthetic access denial"),
                _ => known
            });
            Equal(new[] { "known", "io", "denied" }, sorted);
            Require(!missing.Exists, "Sorting created a missing file.");
        }
    }

    private static void StableTies() {
        using Fixture f = new();
        FileInfo sameA = new(Path.Combine(f.Root, "a", "same.jar"));
        FileInfo sameZ = new(Path.Combine(f.Root, "z", "same.jar"));
        FileInfo alpha = new(Path.Combine(f.Root, "alpha.jar"));
        FileInfo[] source = [sameZ, sameA, sameZ, alpha];
        foreach (LocalResourceSortOrder mode in new[] { LocalResourceSortOrder.AddedNewest, LocalResourceSortOrder.AddedOldest })
            Equal(new[] { alpha, sameA, sameZ, sameZ }, LocalResourceSort.Order(source, mode, item => item));
        FileInfo first = f.File("b.jar"), second = f.File("A.jar");
        DateTime shared = new(2023, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        f.SetCreated(first, shared); f.SetCreated(second, shared);
        first.Refresh(); second.Refresh();
        var sorted = LocalResourceSort.Order(new[] { first, second }, LocalResourceSortOrder.AddedNewest, item => item);
        Require(sorted.Count == 2 && sorted.Contains(first) && sorted.Contains(second), "A tie dropped a file.");
        if (first.CreationTimeUtc == second.CreationTimeUtc) Equal(new[] { second, first }, sorted);
        else Require(sorted[0].CreationTimeUtc > sorted[1].CreationTimeUtc, "Filesystem-reported times were not respected.");
    }

    private static void CachedAndDisabled() {
        using Fixture f = new();
        FileInfo file = f.File("old.jar"), other = f.File("other.jar");
        _ = file.CreationTimeUtc; // Cache the original FileInfo snapshot.
        f.SetCreated(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        f.SetCreated(other, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        FileInfo[] before = LocalResourceSort.Order(new[] { file, other }, LocalResourceSortOrder.AddedOldest, item => item).ToArray();
        File.Move(file.FullName, file.FullName + ".disabled");
        FileInfo disabled = new(file.FullName + ".disabled");
        var after = LocalResourceSort.Order(new[] { file, disabled, other }, LocalResourceSortOrder.AddedOldest, item => item);
        Require(ReferenceEquals(after[^1], file), "Stale cached FileInfo remained a known file after rename.");
        Require(after.Take(2).Contains(disabled) && after.Take(2).Contains(other), "Disabled files were hidden or lost.");
        if (OperatingSystem.IsWindows()) {
            Equal(new[] { file, other }, before);
            Equal(new[] { disabled, other, file }, after);
        } else {
            Require(after[0].CreationTimeUtc <= after[1].CreationTimeUtc, "Reported creation order regressed after a disabled rename.");
        }
    }

    private static void ReadOnly() {
        using Fixture f = new();
        FileInfo[] source = [f.File("b.jar"), f.File("a.jar")];
        FileInfo[] original = source.ToArray();
        var snapshots = source.ToDictionary(file => file.FullName, file => (Bytes: File.ReadAllBytes(file.FullName), Created: file.CreationTimeUtc, Written: file.LastWriteTimeUtc));
        int callbacks = 0;
        _ = LocalResourceSort.Order(source, LocalResourceSortOrder.AddedNewest, file => { callbacks++; return file; });
        Require(callbacks == source.Length, "Metadata was read repeatedly during comparisons.");
        Equal(original, source);
        foreach (FileInfo file in source) {
            file.Refresh(); var before = snapshots[file.FullName];
            Require(before.Bytes.SequenceEqual(File.ReadAllBytes(file.FullName)) && before.Created == file.CreationTimeUtc && before.Written == file.LastWriteTimeUtc,
                "Sorting changed file content or timestamps.");
        }
        Require(Directory.GetFiles(f.Root).Length == source.Length, "Sorting created a cache or metadata file.");
    }

    private static void NameOrder() {
        string[] names = ["mod10.jar", "Mod2.jar", "alpha.jar", "mod1.jar", "beta.jar.disabled"];
        var ascending = LocalResourceSort.ByName(names, true, name => name, name => name);
        Require(ascending.SequenceEqual(["alpha.jar", "beta.jar.disabled", "mod1.jar", "Mod2.jar", "mod10.jar"]),
            "File-name order did not compare numbers by value or ignore case: " + string.Join(", ", ascending));
        var descending = LocalResourceSort.ByName(names, false, name => name, name => name);
        Require(descending.SequenceEqual(ascending.Reverse()), "Reverse file-name order was not the exact reverse.");
    }

    private static void InvalidOrder() {
        foreach (int value in new[] { -1, 5, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => LocalResourceSort.Order(new[] { "anything" }, (LocalResourceSortOrder)value, _ => throw new Exception("Invalid mode read metadata.")));
        Throws<ArgumentNullException>(() => LocalResourceSort.Order<string>(null!, LocalResourceSortOrder.Default, _ => null!));
        Throws<ArgumentNullException>(() => LocalResourceSort.Order(new[] { "anything" }, LocalResourceSortOrder.Default, null!));
    }

    private static void TimestampKeys() {
        DateTime old = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), recent = old.AddDays(3);
        (string Name, string Path, DateTime? Time)[] values = [
            ("unknown", "unknown", null), ("same", "z/same", old), ("new", "new", recent),
            ("same", "a/same", old), ("same", "a/same", old)
        ];
        int stamps = 0, names = 0, paths = 0;
        var latest = LocalResourceSort.ByTime(values, true, value => { stamps++; return value.Time; }, value => { names++; return value.Name; }, value => { paths++; return value.Path; });
        Equal(new[] { values[2], values[3], values[4], values[1], values[0] }, latest);
        Require(stamps == values.Length && names == values.Length && paths == values.Length, "Timestamp/name/path keys were not captured exactly once.");
        Equal(new[] { values[3], values[4], values[1], values[2], values[0] }, LocalResourceSort.ByTime(values, false, value => value.Time, value => value.Name, value => value.Path));
        Require(values[0].Time is null, "Source collection was changed.");
    }

    private static void DirectoryEntries() {
        using Fixture f = new();
        DirectoryInfo known = Directory.CreateDirectory(Path.Combine(f.Root, "world"));
        DirectoryInfo absent = new(Path.Combine(f.Root, "missing-world"));
        foreach (LocalResourceSortOrder mode in new[] { LocalResourceSortOrder.AddedNewest, LocalResourceSortOrder.AddedOldest })
            Equal<FileSystemInfo>(new FileSystemInfo[] { known, absent }, LocalResourceSort.Order<FileSystemInfo>(new FileSystemInfo[] { absent, known }, mode, item => item));
        Require(known.Exists && !absent.Exists, "Directory sorting altered directory presence.");
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclLocalResourceSort-" + Guid.NewGuid().ToString("N"));
        internal Fixture() { Directory.CreateDirectory(Root); }
        internal FileInfo File(string name) {
            string path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "sort fixture bytes: " + name);
            return new FileInfo(path);
        }
        internal void SetCreated(FileInfo file, DateTime value) {
            try { System.IO.File.SetCreationTimeUtc(file.FullName, value); }
            catch (PlatformNotSupportedException) when (!OperatingSystem.IsWindows()) { }
            DateTime actual = System.IO.File.GetCreationTimeUtc(file.FullName);
            if (OperatingSystem.IsWindows()) Require(actual == value, "Windows fixture did not accept the requested creation time.");
            else Require(actual > DateTime.FromFileTimeUtc(0), "The filesystem provided no usable creation timestamp.");
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
