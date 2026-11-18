using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcArchiveTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC source reader inspects one nested root before reading bounded metadata", () => Run(Nested));
        yield return ("MMC source reader keeps safe directory aliases and both game trees", () => Run(Aliases));
        yield return ("MMC source reader rejects traversal ambiguous roots and reserved management data", () => Run(Unsafe));
        yield return ("MMC source reader verifies metadata CRC and actual bytes", () => Run(Crc));
        yield return ("MMC source reader supports explicit non-EFS legacy filename encodings", () => Run(Encoding));
        yield return ("MMC source reader enforces pre-index and per-metadata byte budgets", () => Run(Budgets));
        yield return ("MMC source entry leases serialize actual reads and safely defer source disposal", () => Run(StreamLeases));
        yield return ("MMC package staging separates both library roots and never publishes player or foreign settings files", () => Run(PackageStaging));
    }
    private const string Manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static async Task Nested() {
        using Fixture fixture = new();
        string path = fixture.Zip([("Pack/mmc-pack.json", Manifest), ("Pack/.minecraft/config/a.cfg", "data")]);
        using MmcArchiveSource source = await MmcArchiveSourceReader.ReadAsync(path);
        Require(source.Package.RelativeRoot == "Pack/" && source.Package.GameRootLeaf == ".minecraft" && source.Metadata.Length == 1,
            "Host source facts differ from the actual Core inspection.");
        Require(source.PackageSha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), "Original archive hash was not retained.");
        using StreamReader reader = new(source.OpenFile(".minecraft/config/a.cfg"));
        Require(await reader.ReadToEndAsync() == "data", "A fixed-root source file was lost.");
        if (OperatingSystem.IsWindows()) {
            try { using var unexpected = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new Exception("Source write lock was absent."); }
            catch (IOException) { }
        }
        Require(Directory.GetFiles(fixture.Root).Length == 1, "Reading a package extracted payloads.");
    }
    private static async Task Aliases() {
        using Fixture fixture = new();
        string path = fixture.Zip([("mmc-pack.json", Manifest), ("minecraft/A/one", "1"), ("minecraft/a/two", "2"), (".minecraft/unused", "3")]);
        using var source = await MmcArchiveSourceReader.ReadAsync(path);
        Require(source.Package.GameRootLeaf == "minecraft" && source.Files.Any(x => x.RelativePath == ".minecraft/unused") &&
            source.Diagnostics.Any(d => d.Code == "MMC_DIRECTORY_ALIAS"), "Reader merged or dropped a distinct source tree.");
    }
    private static async Task Unsafe() {
        foreach (var extra in new[] { ("../escape", "x"), ("Other/mmc-pack.json", Manifest), ("PCL/MmcRuntime/data", "x") }) {
            using Fixture fixture = new(); string path = fixture.Zip([("mmc-pack.json", Manifest), extra]);
            try { using var rejected = await MmcArchiveSourceReader.ReadAsync(path); throw new Exception("Expected unsafe source refusal."); }
            catch (Exception error) when (error is IOException or InvalidDataException) { }
        }
    }
    private static async Task Crc() {
        using Fixture fixture = new(); string path = fixture.Zip([("mmc-pack.json", Manifest)]);
        byte[] bytes = File.ReadAllBytes(path);
        int central = -1;
        for (int i = 0; i + 46 <= bytes.Length; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2) { central = i; break; }
        Require(central >= 0, "Fixture central directory is absent.");
        bytes[central + 16] ^= 0x40;
        File.WriteAllBytes(path, bytes);
        await Throws<InvalidDataException>(() => MmcArchiveSourceReader.ReadAsync(path));
    }
    private static async Task Encoding() {
        using Fixture fixture = new();
        var names = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
        string path = fixture.Zip([("mmc-pack.json", Manifest), (".minecraft/config/caf\u00e9.txt", "1")], names);
        using var source = await MmcArchiveSourceReader.ReadAsync(path, legacyEntryNameEncoding: names);
        Require(source.Files.Any(x => x.RelativePath == ".minecraft/config/caf\u00e9.txt"), "Non-EFS entry names were decoded with a different policy than preflight.");
    }
    private static async Task Budgets() {
        using Fixture fixture = new(); string path = fixture.Zip([("mmc-pack.json", Manifest), (".minecraft/file", "x")]);
        await Throws<IOException>(() => MmcArchiveSourceReader.ReadAsync(path, new MmcReadLimits { MaxArchiveEntries = 1 }));
        await Throws<InvalidDataException>(() => MmcArchiveSourceReader.ReadAsync(path, new MmcReadLimits { MaxMetadataBytes = 8 }));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => MmcArchiveSourceReader.ReadAsync(path, cancellationToken: cancel.Token));
    }
    private static async Task StreamLeases() {
        using Fixture fixture = new();
        string bodyA = new('A', 131072), bodyB = new('B', 131072);
        string path = fixture.Zip([("mmc-pack.json", Manifest), ("minecraft/a.bin", bodyA), ("minecraft/b.bin", bodyB)]);
        using var source = await MmcArchiveSourceReader.ReadAsync(path);
        Stream first = source.OpenFile("minecraft/a.bin");
        try { using var rejected = source.OpenFile("minecraft/b.bin"); throw new Exception("Expected overlapping synchronous read refusal."); }
        catch (InvalidOperationException) { }
        Task<Stream> secondTask = source.OpenFileAsync("minecraft/b.bin").AsTask();
        Require(!secondTask.IsCompleted, "Second entry bypassed the full-lifetime read lease.");
        using MemoryStream a = new(); await first.CopyToAsync(a); await first.DisposeAsync();
        await using Stream second = await secondTask;
        source.Dispose(); // The active entry retains the source handle until its own disposal.
        using MemoryStream b = new(); await second.CopyToAsync(b);
        Require(a.ToArray().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(bodyA)) &&
            b.ToArray().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(bodyB)), "Leased entry reads crossed source seek positions.");
        await Throws<ObjectDisposedException>(async () => { using Stream rejected = await source.OpenFileAsync("minecraft/a.bin"); });
    }
    private static async Task PackageStaging() {
        using Fixture fixture = new();
        string path = fixture.Zip([("mmc-pack.json", Manifest), ("libraries/shared.jar", "launcher"),
            (".minecraft/libraries/shared.jar", "game"), (".minecraft/saves/test/level.dat", "world"), ("PCL/config.json", "foreign")]);
        using var source = await MmcArchiveSourceReader.ReadAsync(path);
        var paths = MmcInstancePaths.Resolve(Path.Combine(fixture.Root, "versions", "staged"), ".minecraft");
        using (var staged = await MmcPackageFileStager.PrepareInitialAsync(source, paths)) {
            Require(staged.Files.Length == 5 && staged.InitialGameMutations.Length == 2 && staged.LocalLibrarySources.Count == 1,
                "Source data, local libraries and foreign launcher metadata were conflated.");
            Require(File.ReadAllText(staged.LocalLibrarySources["shared.jar"].FilePath) == "launcher" &&
                !File.Exists(Path.Combine(paths.GameRoot, "libraries", "shared.jar")) &&
                !File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "config.json")), "Preparing source data published a live payload or foreign settings.");
        }
        Directory.CreateDirectory(Path.Combine(paths.GameRoot, "libraries"));
        string existing = Path.Combine(paths.GameRoot, "libraries", "shared.jar"); File.WriteAllText(existing, "player");
        await Throws<IOException>(() => MmcPackageFileStager.PrepareInitialAsync(source, paths));
        Require(File.ReadAllText(existing) == "player", "Initial package staging replaced existing player bytes.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-source-" + Guid.NewGuid().ToString("N"));
        internal string Zip(IEnumerable<(string Name, string Text)> files, Encoding? names = null) {
            Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, "source.zip");
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using ZipArchive zip = new(stream, ZipArchiveMode.Create, false, names ?? new UTF8Encoding(false, true));
            foreach (var item in files) {
                using Stream output = zip.CreateEntry(item.Name, CompressionLevel.NoCompression).Open();
                output.Write(System.Text.Encoding.UTF8.GetBytes(item.Text));
            }
            return path;
        }
        public void Dispose() {
            string path = Path.GetFullPath(Root);
            if (!path.StartsWith(Path.Combine(Path.GetTempPath(), "pcllf-mmc-source-"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected source fixture cleanup root.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
