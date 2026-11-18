using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLCS;
using PCLLF.MMCC.Core;

// Original LF host acceptance fixture; repository LICENCE applies.
internal sealed record FixtureOwner(string Root, Guid Nonce, int ParentPid, long ParentStartedUtcTicks, string ParentImage);
internal sealed record ScenarioOwner(string Root, Guid RootNonce, string Directory, Guid Nonce, string Name,
    MmcTransactionCheckpoint Checkpoint, string FinalName);
internal sealed record ChildIdentity(string Root, Guid RootNonce, string ScenarioRoot, Guid ScenarioNonce,
    int ParentPid, long ParentStartedUtcTicks, string ParentImage, int Pid, long StartedUtcTicks, string Image);
internal sealed record ExpectedTarget(int Index, string RelativePath, string? Sha256, long SizeBytes, string? EvidenceName);
internal sealed record CheckpointReceipt(ChildIdentity Child, string Checkpoint, string OperationDirectory, string TemporaryRoot,
    string FinalRoot, Guid InstanceId, Guid TransactionId, string PreparedSha256, string? DecisionSha256,
    string PackageSha256, string ProducerUseSha256, ExpectedTarget[] Targets);

internal static class CrashIo {
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool Same(string first, string second) => string.Equals(Full(first), Full(second), StringComparison.OrdinalIgnoreCase);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] ReadBytes(string path) {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        Check(length <= 32 * 1024 * 1024, "Observed fixture file exceeds its read budget.");
        byte[] bytes = new byte[checked((int)length)]; stream.ReadExactly(bytes);
        Check(stream.Length == length, "Observed fixture file length changed during the snapshot.");
        stream.Position = 0;
        Check(Hash(bytes) == Convert.ToHexStringLower(SHA256.HashData(stream)) && stream.Length == length,
            "Observed fixture bytes or length changed across two stable-checkpoint reads.");
        return bytes;
    }
    internal static string HashFile(string path) => Hash(ReadBytes(path));
    internal static string CurrentImage => Full(Environment.ProcessPath ?? throw new InvalidOperationException("No actual process image."));
    internal static long Started(Process process) => process.StartTime.ToUniversalTime().Ticks;
    internal static void NoLinks(string path) {
        for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current)) {
            try { Check((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked fixture path is not authorized: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static void RequireRoot(string root) {
        string full = Full(root), prefix = Full(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        const string label = "PCLLF-MmcRealCrash-";
        string leaf = Path.GetFileName(full);
        Check(full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.StartsWith(label, StringComparison.Ordinal) &&
            Guid.TryParseExact(leaf[label.Length..], "N", out var id) && id != Guid.Empty, "Not an owned GUID crash fixture root.");
        NoLinks(full);
    }
    internal static string Under(string root, string relative) {
        Check(!Path.IsPathRooted(relative) && relative.Split('/', '\\').All(part => part is not ("" or "." or "..")), "Unsafe fixture-relative path.");
        string full = Full(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Check(full.StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture path escaped its root.");
        NoLinks(full); return full;
    }
    internal static void WriteBytes(string path, byte[] bytes) {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough);
        output.Write(bytes); output.Flush(true);
    }
    internal static void WriteJson<T>(string path, T value) {
        // Readers never observe a partially written readiness signal.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path);
    }
    internal static T ReadJson<T>(string path) {
        NoLinks(path);
        Check(new FileInfo(path).Length <= 16 * 1024 * 1024, "Fixture JSON exceeded its budget.");
        return JsonSerializer.Deserialize<T>(ReadBytes(path)) ?? throw new IOException("Empty fixture JSON.");
    }
    internal static (JsonElement Value, string Hash) Record(string path) {
        // Read-only observation of the real durable records; never feeds substitute records to production.
        byte[] bytes = ReadBytes(path), prefix = Encoding.ASCII.GetBytes("PCLLF-MMC-RECORD-1\n");
        Check(bytes.Length >= prefix.Length + 65 && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix) && bytes[prefix.Length + 64] == '\n', "Invalid real durable record header.");
        string expected = Encoding.ASCII.GetString(bytes, prefix.Length, 64);
        byte[] payload = bytes[(prefix.Length + 65)..]; Check(Hash(payload) == expected, "Real durable record checksum mismatch.");
        using var json = JsonDocument.Parse(payload); return (json.RootElement.Clone(), expected);
    }
    internal static string Tree(string root) {
        if (!Directory.Exists(root)) return "<missing>";
        List<string> entries = []; Stack<string> directories = new(); directories.Push(root);
        while (directories.Count > 0) {
            string directory = directories.Pop(); NoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                NoLinks(path); string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Directory.Exists(path)) { entries.Add("D " + relative); directories.Push(path); }
                else entries.Add("F " + relative + " " + new FileInfo(path).Length + " " + HashFile(path));
            }
        }
        return string.Join('\n', entries.Order(StringComparer.Ordinal));
    }
}

internal sealed class CrashFixture : IDisposable {
    internal FixtureOwner Owner { get; }
    internal string Root => Owner.Root;
    internal string Apphost => CrashIo.Under(AppContext.BaseDirectory, "PCLLF.Mmc.RealCrash.Tests.exe");
    internal CrashFixture() {
        string root = Path.Combine(Path.GetTempPath(), "PCLLF-MmcRealCrash-" + Guid.NewGuid().ToString("N"));
        CrashIo.RequireRoot(root); CrashIo.Check(!Directory.Exists(root), "New fixture root already exists.");
        Directory.CreateDirectory(root); using var self = Process.GetCurrentProcess();
        Owner = new(root, Guid.NewGuid(), self.Id, CrashIo.Started(self), CrashIo.CurrentImage);
        CrashIo.Check(CrashIo.Same(Owner.ParentImage, Apphost), "Run the built .exe apphost directly so child image ownership is exact.");
        CrashIo.WriteJson(Path.Combine(root, "fixture.json"), Owner);
        Log("fixture-created", new { Owner, ProductionAssembly = typeof(MmcImportRecovery).Assembly.Location,
            ProductionSha256 = CrashIo.HashFile(typeof(MmcImportRecovery).Assembly.Location),
            CoreAssembly = typeof(MmcDefinitionCodec).Assembly.Location, CoreSha256 = CrashIo.HashFile(typeof(MmcDefinitionCodec).Assembly.Location) });
    }
    internal void Log(string name, object details) {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Event = name, Details = details }) + "\n");
        using (var log = new FileStream(Path.Combine(Root, "acceptance.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough)) {
            log.Write(bytes); log.Flush(true);
        }
        Console.WriteLine(name + " " + JsonSerializer.Serialize(details));
    }
    public void Dispose() => InstanceConfigProviders.FlushAndDisposeWithin(Root);
}

internal sealed class CrashScenario : IDisposable {
    internal ScenarioOwner Owner { get; }
    internal string Root => Owner.Directory;
    internal string Game => CrashIo.Under(Root, "minecraft");
    internal string Final => CrashIo.Under(Game, "versions/" + Owner.FinalName);
    internal string Archive => CrashIo.Under(Root, "source/original.zip");
    internal string Startup => CrashIo.Under(Root, "child-started.json");
    internal string Checkpoint => CrashIo.Under(Root, "checkpoint.json");
    internal string ExpectedRoot => CrashIo.Under(Root, "expected-bytes");
    internal string Neighbor => CrashIo.Under(Game, "versions/neighbor-" + Owner.Nonce.ToString("N"));
    internal string Sibling => CrashIo.Under(Root, "sibling-player-data");
    internal byte[] PlayerBytes => Encoding.UTF8.GetBytes("author player bytes " + Owner.Nonce.ToString("N"));
    internal byte[] WorldBytes => Encoding.UTF8.GetBytes("author world bytes " + Owner.Nonce.ToString("N"));
    internal MmcRuntimeEnvironment Environment { get; } = new("windows", "x86_64", 8);
    internal int NetworkAttempts => transport.Attempts;
    private readonly NoNetwork transport = new();
    private readonly HttpClient client;
    private static readonly byte[] Index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
    private readonly string? guardSnapshot, sourceHash;
    internal CrashScenario(FixtureOwner fixture, string name) {
        Owner = new(fixture.Root, fixture.Nonce, CrashIo.Under(fixture.Root, name + "-" + Guid.NewGuid().ToString("N")), Guid.NewGuid(), name,
            name == "prepared" ? MmcTransactionCheckpoint.Prepared : MmcTransactionCheckpoint.CommitDecided, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root); client = new(transport);
        CrashIo.WriteJson(Path.Combine(Root, "scenario.json"), Owner); WritePackage(Archive, PlayerBytes);
        CrashIo.WriteBytes(CrashIo.Under(Neighbor, "minecraft/saves/protected-world/region/r.0.0.mca"), Encoding.UTF8.GetBytes("neighbor world " + Owner.Nonce));
        CrashIo.WriteBytes(CrashIo.Under(Neighbor, "unknown-keep.bin"), Encoding.UTF8.GetBytes("neighbor unknown " + Owner.Nonce));
        CrashIo.WriteBytes(CrashIo.Under(Sibling, "saves/world/level.dat"), Encoding.UTF8.GetBytes("sibling player world " + Owner.Nonce));
        guardSnapshot = Guards(); sourceHash = CrashIo.HashFile(Archive);
        CrashIo.WriteJson(Path.Combine(Root, "protected-before.json"), new { GuardSnapshot = guardSnapshot, SourceSha256 = sourceHash });
    }
    private CrashScenario(ScenarioOwner owner) { Owner = owner; client = new(transport); }
    internal static (FixtureOwner Fixture, CrashScenario Scenario) OpenChild(string root, Guid rootNonce, string directory, Guid nonce) {
        CrashIo.RequireRoot(root); var fixture = CrashIo.ReadJson<FixtureOwner>(Path.Combine(root, "fixture.json"));
        CrashIo.Check(CrashIo.Same(root, fixture.Root) && fixture.Nonce == rootNonce && rootNonce != Guid.Empty, "Child root nonce mismatch.");
        string scenario = CrashIo.Under(root, Path.GetFileName(CrashIo.Full(directory)));
        CrashIo.Check(CrashIo.Same(scenario, directory), "Child scenario escaped the GUID fixture.");
        var owner = CrashIo.ReadJson<ScenarioOwner>(Path.Combine(scenario, "scenario.json"));
        CrashIo.Check(CrashIo.Same(owner.Root, root) && owner.RootNonce == rootNonce && CrashIo.Same(owner.Directory, scenario) && owner.Nonce == nonce && nonce != Guid.Empty &&
            owner.Name is "prepared" or "committed" or "foreign" && owner.Checkpoint == (owner.Name == "prepared" ? MmcTransactionCheckpoint.Prepared : MmcTransactionCheckpoint.CommitDecided), "Child scenario ownership mismatch.");
        CrashIo.Check(owner.FinalName.StartsWith("import-", StringComparison.Ordinal) && Guid.TryParseExact(owner.FinalName[7..], "N", out _), "Child final name is not an owned GUID.");
        using var parent = Process.GetProcessById(fixture.ParentPid);
        CrashIo.Check(!parent.HasExited && CrashIo.Started(parent) == fixture.ParentStartedUtcTicks && CrashIo.Same(parent.MainModule!.FileName, fixture.ParentImage), "Child's actual parent identity changed.");
        return (fixture, new(owner));
    }
    internal MmcInstanceImportRequest Request(string? archive = null, string? final = null) => new(archive ?? Archive, final ?? Final, Path.GetFileName(final ?? Final), Environment);
    internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(new MmcMetadataStore(CrashIo.Under(Root, "metadata"), new Uri("https://fixture.invalid/meta/"), client),
        new(CrashIo.Under(Root, "cache/libraries"), CrashIo.Under(Root, "cache/assets"), CrashIo.Under(Root, "cache/sources")), value => value,
        async (files, token) => {
            foreach (var file in files) {
                CrashIo.Check(file.Urls.SequenceEqual(new[] { "https://fixture.invalid/index" }), "Unexpected fixture artifact request.");
                await File.WriteAllBytesAsync(file.DownloadPath, Index, token);
            }
        });
    internal MmcImportRecoveryEntry Entry(string? operation = null) => MmcImportRecovery.Read(Game).Entries.Single(value => operation is null || CrashIo.Same(value.OperationDirectory, operation));
    internal string Guards() => CrashIo.Tree(Neighbor) + "\n---sibling---\n" + CrashIo.Tree(Sibling);
    internal void AssertGuards(string? archive = null) {
        CrashIo.Check(guardSnapshot is not null && sourceHash is not null && Guards() == guardSnapshot && CrashIo.HashFile(archive ?? Archive) == sourceHash,
            "Source archive, neighboring unknown files, or protected worlds changed.");
        CrashIo.Check(NetworkAttempts == 0, "A real HTTP request escaped the controlled artifact delegate.");
        CrashIo.WriteJson(Path.Combine(Root, "protected-after.json"), new { GuardSnapshot = Guards(), SourceSha256 = CrashIo.HashFile(archive ?? Archive), NetworkAttempts });
    }
    internal void WritePackage(string path, byte[] player) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add("mmc-pack.json", Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}"));
        Add("patches/net.minecraft.json", JsonSerializer.SerializeToUtf8Bytes(new {
            uid = "net.minecraft", version = "1.7.10", mainClass = "fixture.Main", compatibleJavaMajors = new[] { 8 },
            mainJar = new Dictionary<string, object> { ["name"] = "fixture:main:1", ["MMC-hint"] = "local", ["MMC-filename"] = "main.jar" },
            assetIndex = new { id = "fixture", url = "https://fixture.invalid/index", sha1 = Convert.ToHexStringLower(SHA1.HashData(Index)), size = Index.Length }
        }));
        Add("libraries/main.jar", Encoding.UTF8.GetBytes("inert original main library"));
        Add("minecraft/config/player.cfg", player); Add("minecraft/saves/author-world/level.dat", WorldBytes);
        void Add(string name, byte[] bytes) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
    }
    internal void AssertAuthorPayload(string versionRoot) {
        CrashIo.Check(CrashIo.ReadBytes(CrashIo.Under(versionRoot, "minecraft/config/player.cfg")).SequenceEqual(PlayerBytes) &&
            CrashIo.ReadBytes(CrashIo.Under(versionRoot, "minecraft/saves/author-world/level.dat")).SequenceEqual(WorldBytes), "Real author payload bytes changed.");
    }
    public void Dispose() { InstanceConfigProviders.FlushAndDisposeWithin(Root); client.Dispose(); }
    private sealed class NoNetwork : HttpMessageHandler {
        internal int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Interlocked.Increment(ref Attempts); throw new IOException("No real network is allowed in crash recovery acceptance.");
        }
    }
}
