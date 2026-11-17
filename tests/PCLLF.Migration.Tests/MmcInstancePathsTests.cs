using PCLCS;

internal static class MmcInstancePathsTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC paths missing game tree selects minecraft without creating files", DefaultGameRoot);
        yield return ("MMC paths only dot minecraft remains the selected game tree", DotMinecraft);
        yield return ("MMC paths both trees prefer minecraft and preserve inactive tree", BothTrees);
        yield return ("MMC paths persisted layout wins over later directory discovery", PersistedLeaf);
        yield return ("MMC paths instance libraries and game libraries retain different scopes", DistinctLibraries);
        yield return ("MMC paths reject ordinary files occupying directory roles", FileConflicts);
        yield return ("MMC paths reject unsafe leaves relative roots and nonversion roots", InvalidRoots);
        yield return ("MMC paths role-relative targets reject traversal ADS and devices", RelativeBoundaries);
        yield return ("MMC paths reject linked roles and linked descendants", LinkedPaths);
        yield return ("MMC paths recheck replaced ancestor junctions after repeated checks and before publication", ReplacedAncestorJunction);
    }
    private static void DefaultGameRoot() {
        using Fixture fixture = new();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        Equal(fixture.Root, paths.InstanceRoot); Equal(fixture.Root, paths.VersionRoot);
        Equal(Path.Combine(fixture.Root, "minecraft"), paths.GameRoot);
        Equal(false, paths.HasInactiveGameTree); Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }
    private static void DotMinecraft() {
        using Fixture fixture = new(); Directory.CreateDirectory(Path.Combine(fixture.Root, ".minecraft"));
        Equal(".minecraft", MmcInstancePaths.Resolve(fixture.Root).GameRootLeaf);
    }
    private static void BothTrees() {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "minecraft")); Directory.CreateDirectory(Path.Combine(fixture.Root, ".minecraft"));
        File.WriteAllText(Path.Combine(fixture.Root, ".minecraft", "keep.txt"), "inactive");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        Equal("minecraft", paths.GameRootLeaf); Equal(true, paths.HasInactiveGameTree);
        Equal("inactive", File.ReadAllText(Path.Combine(fixture.Root, ".minecraft", "keep.txt")));
    }
    private static void PersistedLeaf() {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "minecraft"));
        Equal(".minecraft", MmcInstancePaths.Resolve(fixture.Root, ".minecraft").GameRootLeaf);
        Equal(false, Directory.Exists(Path.Combine(fixture.Root, ".minecraft")));
    }
    private static void DistinctLibraries() {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "libraries"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "minecraft", "libraries"));
        File.WriteAllText(Path.Combine(fixture.Root, "libraries", "a.jar"), "launcher-local");
        File.WriteAllText(Path.Combine(fixture.Root, "minecraft", "libraries", "a.jar"), "game-data");
        MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        Equal("launcher-local", File.ReadAllText(paths.ResolveRelative(MmcPathRole.LocalLibraries, "a.jar")));
        Equal("game-data", File.ReadAllText(paths.ResolveRelative(MmcPathRole.Game, "libraries/a.jar")));
        Equal(Path.Combine(fixture.Root, "libraries", "a.jar"), paths.ResolveRelative(MmcPathRole.Instance, "libraries/a.jar"));
    }
    private static void FileConflicts() {
        foreach (string relative in new[] { "minecraft", ".minecraft", "libraries", "PCL", "PCL/MmcRuntime" }) {
            using Fixture fixture = new();
            string path = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "conflict");
            Throws<IOException>(() => MmcInstancePaths.Resolve(fixture.Root));
        }
    }
    private static void InvalidRoots() {
        using Fixture fixture = new();
        Throws<IOException>(() => MmcInstancePaths.Resolve("relative/versions/test"));
        Throws<IOException>(() => MmcInstancePaths.Resolve(fixture.Scope));
        foreach (string leaf in new[] { "..", "../minecraft", "minecraft/child", "Minecraft", "", "libraries" })
            Throws<IOException>(() => MmcInstancePaths.Resolve(fixture.Root, leaf));
    }
    private static void RelativeBoundaries() {
        using Fixture fixture = new(); MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        Equal(Path.Combine(paths.GameRoot, "mods", "测试 模组.jar"), paths.ResolveRelative(MmcPathRole.Game, "mods\\测试 模组.jar"));
        foreach (string relative in new[] { "", "../other", "a/../../b", "a/./b", "/root", "\\root", "C:\\outside", "a:stream", "a//b", "a/", "a. ", "NUL.jar", "COM1", "LPT\u00b9.txt", "a/*" })
            Throws<IOException>(() => paths.ResolveRelative(MmcPathRole.Game, relative));
    }
    private static void LinkedPaths() {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.Scope, "outside"); Directory.CreateDirectory(outside);
        string game = Path.Combine(fixture.Root, "minecraft");
        CreateDirectoryLink(game, outside);
        try { Throws<IOException>(() => MmcInstancePaths.Resolve(fixture.Root)); }
        finally { Directory.Delete(game); }
        Directory.CreateDirectory(game);
        MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        string link = Path.Combine(game, "mods"); CreateDirectoryLink(link, outside);
        try { Throws<IOException>(() => paths.ResolveRelative(MmcPathRole.Game, "mods/a.jar")); }
        finally { Directory.Delete(link); }
        Equal(true, Directory.Exists(outside));
    }
    private static void ReplacedAncestorJunction() {
        using Fixture fixture = new();
        MmcInstancePaths paths = MmcInstancePaths.Resolve(fixture.Root);
        string parent = Path.Combine(paths.InstanceRoot, "parent");
        string objects = Path.Combine(parent, "objects");
        string original = Path.Combine(objects, "original.bin");
        string newTarget = Path.Combine(objects, "new.bin");
        string validSibling = Path.Combine(paths.InstanceRoot, "valid-parent", "objects", "valid.bin");
        string outside = Path.Combine(fixture.Scope, "outside-approved-instance");
        string outsideObjects = Path.Combine(outside, "objects");
        string outsideOriginal = Path.Combine(outsideObjects, "original.bin");
        string outsideNew = Path.Combine(outsideObjects, "new.bin");
        string sentinel = Path.Combine(outsideObjects, "sentinel.txt");
        string preserved = Path.Combine(fixture.Scope, "preserved-original");

        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(Path.GetDirectoryName(validSibling)!);
        Directory.CreateDirectory(outsideObjects);
        File.WriteAllText(original, "matching-fixture-bytes");
        File.WriteAllText(outsideOriginal, "matching-fixture-bytes");
        File.WriteAllText(sentinel, "preserve-outside");
        File.WriteAllText(validSibling, "valid-sibling-bytes");

        MmcInstancePaths.EnsureNoLinks(original);
        Equal(original, paths.ResolveRelative(MmcPathRole.Instance, "parent/objects/original.bin"));
        _ = MmcArtifactIo.HashSha256(original, 1024, CancellationToken.None);
        _ = MmcArtifactIo.HashSha256(validSibling, 1024, CancellationToken.None);

        Directory.Move(parent, preserved);
        CreateDirectoryLink(parent, outside);
        try {
            IOException ensureError = AssertThrowsIo(() => MmcInstancePaths.EnsureNoLinks(original));
            Equal("MMC 路径包含链接或重解析点。", ensureError.Message);
            Equal("MMC 路径包含链接或重解析点。", AssertThrowsIo(() => paths.ResolveRelative(MmcPathRole.Instance, "parent/objects/original.bin")).Message);
            Equal("MMC 路径包含链接或重解析点。", AssertThrowsIo(() => MmcArtifactIo.HashSha256(original, 1024, CancellationToken.None)).Message);
            Equal("MMC 路径包含链接或重解析点。", AssertThrowsIo(() => MmcArtifactIo.Directory(objects)).Message);
            Equal("MMC 路径包含链接或重解析点。", AssertThrowsIo(() => MmcInstancePaths.EnsureNoLinks(newTarget)).Message);
            Equal(false, File.Exists(outsideNew));
            Equal("preserve-outside", File.ReadAllText(sentinel));
            Equal("matching-fixture-bytes", File.ReadAllText(outsideOriginal));
            Equal(validSibling, paths.ResolveRelative(MmcPathRole.Instance, "valid-parent/objects/valid.bin"));
            _ = MmcArtifactIo.HashSha256(validSibling, 1024, CancellationToken.None);
        } finally {
            // Only remove the exact junction created by this fixture; never recurse through it.
            if (Directory.Exists(parent)) Directory.Delete(parent, false);
            if (Directory.Exists(preserved)) Directory.Move(preserved, parent);
        }
        Equal(original, paths.ResolveRelative(MmcPathRole.Instance, "parent/objects/original.bin"));
        _ = MmcArtifactIo.HashSha256(original, 1024, CancellationToken.None);
    }
    private static IOException AssertThrowsIo(Action action) {
        try { action(); } catch (IOException error) { return error; }
        throw new Exception("Expected IOException.");
    }
    internal static void CreateDirectoryLink(string path, string target) {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        // Junction fixtures need no symlink privilege. Encoded PowerShell uses quoted data arguments,
        // does not invoke a shell command assembled from unescaped paths, and never touches player data.
        string script = "New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        System.Diagnostics.ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not create junction fixture.");
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Junction fixture creation timed out."); }
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new Exception("Could not create junction fixture: " + error);
    }
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Fixture : IDisposable {
        public string Scope { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-paths-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public Fixture() { Root = Path.Combine(Scope, "versions", "sample"); Directory.CreateDirectory(Root); }
        public void Dispose() { if (Directory.Exists(Scope)) Directory.Delete(Scope, true); }
    }
}
