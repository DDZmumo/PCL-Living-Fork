using System.Collections;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Pinned LF Test-ModpackUpdate.ps1 (294d73fe...) through the actual Native Main
// facades and their original DTOs. IDs MP-* map to every original observable
// assertion; the controlled download/request delegates are LF's existing seams.
// LF f4aa0fae... + 50b028da..., PCL 5ef81e49...; no CE transaction/policy reused.
// Observable assertion map, original Test-ModpackUpdate.ps1 line -> ID:
// 43 -> MP-01; 44 -> MP-02; 49 -> MP-03;
// 51, all six exact path/text pairs -> MP-04:<path>;
// 52 -> MP-05; 53 -> MP-06; 56 -> MP-07; 60 -> MP-08;
// 67 -> MP-09; 68, both original files -> MP-10;
// 69, all seven unsafe inputs -> MP-11:<input>; 71 -> MP-12;
// 81 -> MP-13; 89 -> MP-14; 91, original bytes + no pending -> MP-15;
// 97, exact CF endpoint/POST -> MP-16; 99, identity + SHA1 -> MP-17;
// 104 -> MP-18. MP-19/20 and named -bytes/-boundary/-request/-handles
// assertions add evidence; they do not replace or relax any original assertion.
// Original synthetic/GUID I/O and finally cleanup are supplied by the genuine
// existing FixtureEnvironment + isolated Native host, not a product startup.
internal static class ModpackUpdateChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Data, "modpack-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ModpackArchiveEncodingChecks.Run(root);
        TransactionFixtures(root);
        Console.WriteLine("PASS native modpack-update transaction: MP-01..MP-20 (real archive/JSON/merge/backup/recovery and path guards).");
        ModpackUpdateUxChecks.Run(root);
        ModpackUpdateFlowChecks.Run(environment, root);
        environment.AssertNoForbiddenIo();
    }

    // These independent cases retain their original assertions and actual shared setup,
    // with their own unchanged Native 60-second process budget.
    internal static void RunOrigins(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Data,"modpack-update-origins-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ModpackUpdateFlowChecks.RunOrigins(environment,root);
        environment.AssertNoForbiddenIo();
    }

    private static void TransactionFixtures(string root)
    {
        string game = Path.Combine(root, "game"), version = Path.Combine(root, "version");
        Dictionary<string, string> oldFiles = new()
        {
            ["config/author.txt"] = "old", ["config/custom.txt"] = "author",
            ["options.txt"] = "keys-old", ["xaerowaypoints/server/map.txt"] = "old-waypoint",
            ["mods/removed.jar"] = "old-mod", ["mods/disabled.jar"] = "disabled-by-player"
        };
        object old = Read(Pack(root, "old", oldFiles, new() { ["mods/stable.jar"] = "stable" }), Path.Combine(root, "old-stage"));
        JObject baseline = (JObject)Update("Manifest", old)!;
        foreach ((string path, string text) in oldFiles)
            if (path != "mods/disabled.jar") Put(Path.Combine(game, path), text);
        Put(Path.Combine(game, "mods/disabled.jar.disabled"), "disabled-by-player");
        Put(Path.Combine(game, "mods/stable.jar"), "stable");
        Put(Path.Combine(game, "mods/personal.jar"), "personal");
        Put(Path.Combine(game, "config/custom.txt"), "player-change");
        Put(Path.Combine(game, "options.txt"), "personal-keys");
        object next = Read(Pack(root, "new", new()
        {
            ["config/author.txt"] = "new", ["config/custom.txt"] = "new-author", ["options.txt"] = "keys-new",
            ["xaerowaypoints/server/map.txt"] = "new-waypoint", ["mods/disabled.jar"] = "new-disabled"
        }, new() { ["mods/stable.jar"] = "stable", ["mods/new.jar"] = "download-new" }), Path.Combine(root, "new-stage"));
        object plan = Update("Plan", game, baseline, next)!;
        Check(plan.GetType().Assembly == Native.Main, "MP-identity", "plan must be the real Main DTO, not a fixture model");
        string[] originalPreserved = { "config/custom.txt", "options.txt", "xaerowaypoints/server/map.txt" };
        object[] originalConflicts = Changes(plan).Where(change => originalPreserved.Contains(Text(change, "Relative"), StringComparer.OrdinalIgnoreCase)).ToArray();
        Check(originalConflicts.Length == 3 && originalConflicts.All(Conflict), "MP-01", "personal config/options/waypoints were not preserved");
        object disabledUpdate = Changes(plan).Single(change => Text(change, "Relative") == "mods/disabled.jar");
        Check(!Conflict(disabledUpdate) && (bool)Native.Property(disabledUpdate.GetType(), "ApplyAuthor").GetValue(disabledUpdate)! &&
            (bool)Get(disabledUpdate, "KeepDisabled")!, "MP-01-disabled-update", "Disabled author content must update separately from its enable state.");
        object personalAddition = Changes(plan).Single(change => Text(change, "Relative") == "mods/personal.jar");
        Check(Changes(plan).Count(Conflict) == 4 && Conflict(personalAddition) && (bool)Get(personalAddition, "DisplayOnly")! &&
            Get(personalAddition, "Ownership")!.ToString() == "PersonalAdded" && !(bool)personalAddition.GetType().GetProperty("ApplyAuthor")!.GetValue(personalAddition)!,
            "MP-01-personal-preview", "The additional personal mod must be shown once as retained, with personal configuration checks preserved");
        Check(!Changes(plan).Any(change => Text(change, "Relative") == "mods/stable.jar"), "MP-02", "unchanged file scheduled for download");
        int downloads = 0;
        Action<List<ModNet.NetFile>, CancellationToken> download = (files, token) =>
        {
            token.ThrowIfCancellationRequested();
            foreach (ModNet.NetFile file in files) { downloads++; Put(file.LocalPath, "download-new"); }
        };
        Update("DownloadChanges", plan, Path.Combine(root, "downloads"), CancellationToken.None, download);
        Check(downloads == 1, "MP-03", "unchanged or conflicting files downloaded");
        // Historical transaction fixtures deliberately use text as .jar bytes.
        // Preserve that byte-level oracle, while requiring explicit acknowledgement
        // rather than pretending these files have verified loader metadata.
        var graph = (PCLCS.PackDependencyGraphResult)Get(plan, "DependencyCheck")!;
        Check(graph.HasUnknown && !graph.HasErrors, "MP-graph-unknown", "Text JAR fixture must be reported as unverified.");
        Reject(() => Update("Commit", plan, version, CancellationToken.None, null, null), "MP-graph-confirm", "Unverified loader metadata committed without confirmation.");
        Set(plan, "AllowUnverifiedDependencies", true);
        string backup = (string)Update("Commit", plan, version, CancellationToken.None, null, null)!;
        (string path, string text)[] expected =
        {
            ("mods/personal.jar", "personal"), ("config/custom.txt", "player-change"),
            ("options.txt", "personal-keys"), ("xaerowaypoints/server/map.txt", "old-waypoint"),
            ("config/author.txt", "new"), ("mods/new.jar", "download-new")
        };
        foreach ((string path, string text) in expected)
            Check(File.ReadAllText(Path.Combine(game, path)) == text, "MP-04:" + path, "incorrect committed bytes");
        Check(!File.Exists(Path.Combine(game, "mods/removed.jar")) && !File.Exists(Path.Combine(game, "mods/disabled.jar")), "MP-05", "removed/disabled mod handling failed");
        Check(File.ReadAllText(Path.Combine(game, "mods/disabled.jar.disabled")) == "new-disabled", "MP-05-disabled-bytes", "Disabled author content was not updated or was reenabled.");
        Check(File.Exists(Path.Combine(backup, "transaction.json")), "MP-06", "durable update backup missing");
        JObject saved = (JObject)Update("ReadManifest", version)!;
        object repeated = Update("Plan", game, saved, next)!;
        Check(!Changes(repeated).Any(change => !Conflict(change)), "MP-07", "repeat update did not preserve personal changes");

        object stale = Update("Plan", game, saved, Read(Pack(root, "stale", new() { ["config/author.txt"] = "third" }), Path.Combine(root, "stale-stage")))!;
        Put(Path.Combine(game, "config/author.txt"), "external-edit");
        Reject(() => Update("Commit", stale, version, CancellationToken.None, null, null), "MP-08", "stale preview overwrote an external edit");
        Check(File.ReadAllText(Path.Combine(game, "config/author.txt")) == "external-edit", "MP-08-bytes", "stale preview changed externally edited bytes");

        string clean = Path.Combine(root, "rollback-game"), rollbackVersion = Path.Combine(root, "rollback-version");
        Put(Path.Combine(clean, "a.txt"), "old-a"); Put(Path.Combine(clean, "b.txt"), "old-b");
        object oldRollback = Read(Pack(root, "rollback-old", new() { ["a.txt"] = "old-a", ["b.txt"] = "old-b" }), Path.Combine(root, "rollback-old-stage"));
        object newRollback = Read(Pack(root, "rollback-new", new() { ["a.txt"] = "new-a", ["b.txt"] = "new-b" }), Path.Combine(root, "rollback-new-stage"));
        object rollback = Update("Plan", clean, Update("Manifest", oldRollback), newRollback)!;
        IOException failure = new("Synthetic second-operation pack commit failure.");
        Action<int> failSecond = index => { if (index == 1) throw failure; };
        bool failed = false;
        try { Update("Commit", rollback, rollbackVersion, CancellationToken.None, null, failSecond); }
        catch (IOException error) when (ReferenceEquals(error, failure)) { failed = true; }
        Check(failed, "MP-09", "injected commit failure ignored or reclassified");
        Check(File.ReadAllText(Path.Combine(clean, "a.txt")) == "old-a" && File.ReadAllText(Path.Combine(clean, "b.txt")) == "old-b", "MP-10", "reverse rollback did not restore both original files");
        foreach (string path in new[] { "a.txt", "b.txt" })
        { using FileStream exclusive = new(Path.Combine(clean, path), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        Check(!Directory.EnumerateFiles(clean, "*.pcl-update-*", SearchOption.AllDirectories).Any(), "MP-10-handles", "rollback left staged files");
        foreach (string unsafePath in new[] { "../outside", "/absolute", "C:/outside", "mods/../outside", "mods/con.jar", "mods/file.jar:stream", "mods/a." })
            Reject(() => Update("RelativePath", unsafePath), "MP-11:" + unsafePath, "unsafe pack path accepted");
        string bad = Pack(root, "traversal", new() { ["../escaped.txt"] = "bad" });
        Reject(() => Read(bad, Path.Combine(root, "bad-stage")), "MP-12", "archive traversal accepted");
        Check(!File.Exists(Path.Combine(root, "escaped.txt")), "MP-12-boundary", "archive extraction escaped the stage");

        string personal = Path.Combine(game, "mods/personal-id.jar"), incoming = Path.Combine(root, "incoming.jar");
        FabricJar(personal, "personal"); FabricJar(incoming, "author-new");
        object collisionPackage = Read(Pack(root, "collision", new()), Path.Combine(root, "collision-stage"));
        object mod = New("PackUpdateFile");
        Set(mod, "Relative", "mods/new-id.jar"); Set(mod, "Hash", Digest(incoming)); Set(mod, "Payload", incoming);
        ((IDictionary)Get(collisionPackage, "Files")!).Add("mods/new-id.jar", mod);
        object collision = Update("Plan", game, saved, collisionPackage)!;
        Update("DownloadChanges", collision, Path.Combine(root, "collision-download"), CancellationToken.None, download);
        object duplicate = Changes(collision).Single(change => Text(change, "Relative") == "mods/new-id.jar");
        Check(!Conflict(duplicate) && (bool)Native.Property(duplicate.GetType(), "ApplyAuthor").GetValue(duplicate)! &&
            Text(duplicate, "LocalRelative") == "mods/personal-id.jar" && (bool)Get(duplicate, "PersonalComponentMatched")! &&
            !Changes(collision).Any(change => Get(change, "Operation")!.ToString() == "DisablePersonal"),
            "MP-13", "The uniquely matched personal mod and new author component were not paired into one upgrade.");
        Check(Native.Call(Native.Type("PCL.ModpackUpdatePreviewView"), "Classify", null, duplicate)!.ToString() == "Updated",
            "MP-13-category", "The real Main preview did not classify the paired component as an upgrade.");

        string recoverRoot = Path.Combine(root, "recover-root"), recoverVersion = Path.Combine(root, "recover-version");
        Put(Path.Combine(recoverRoot, "a.txt"), "old-a"); Put(Path.Combine(recoverRoot, "b.txt"), "old-b");
        object recovery = Update("Plan", recoverRoot, Update("Manifest", oldRollback), newRollback)!;
        string recoverBox = (string)Update("Commit", recovery, recoverVersion, CancellationToken.None, null, null)!;
        string logPath = Path.Combine(recoverBox, "transaction.json");
        JObject log = JObject.Parse(File.ReadAllText(logPath)); log["complete"] = false; Put(logPath, log.ToString());
        Check(((IEnumerable)Update("Pending", recoverVersion)!).Cast<object>().Count() == 1, "MP-14", "interrupted transaction not detected");
        Update("Recover", recoverVersion, recoverRoot);
        Check(File.ReadAllText(Path.Combine(recoverRoot, "a.txt")) == "old-a" && !((IEnumerable)Update("Pending", recoverVersion)!).Cast<object>().Any(), "MP-15", "interrupted transaction recovery failed");
        Check(File.ReadAllText(Path.Combine(recoverRoot, "b.txt")) == "old-b", "MP-15-second", "recovery did not restore second file");

        string cf = Path.Combine(root, "fixture-cf.zip");
        using (ZipArchive zip = ZipFile.Open(cf, ZipArchiveMode.Create))
            ZipText(zip, "manifest.json", "{\"name\":\"CF fixture\",\"version\":\"2\",\"minecraft\":{\"version\":\"1.20.1\",\"modLoaders\":[{\"id\":\"fabric-0.16.0\",\"primary\":true}]},\"files\":[{\"projectID\":123,\"fileID\":456,\"required\":true}],\"overrides\":\"overrides\"}");
        string sha = Digest(Path.Combine(game, "mods/stable.jar")).Split(':')[1];
        int requests = 0;
        Func<string, HttpMethod, string, JToken> request = (url, method, body) =>
        {
            Check(url == "https://api.curseforge.com/v1/mods/files" && method.Method == "POST", "MP-16", "unexpected CurseForge metadata request");
            Check(JObject.Parse(body)["fileIds"] is JArray ids && ids.Count == 1 && (int)ids[0] == 456, "MP-16-body", "wrong confirmed author file id");
            requests++;
            return new JObject { ["data"] = new JArray(new JObject {
                ["id"] = 456, ["gameId"] = 432, ["modId"] = 123, ["displayName"] = "Fixture mod", ["fileName"] = "fixture.jar",
                ["fileDate"] = "2026-01-01T00:00:00Z", ["releaseType"] = 1, ["downloadCount"] = 1,
                ["downloadUrl"] = "https://example.invalid/fixture.jar", ["fileLength"] = 6,
                ["gameVersions"] = new JArray("1.20.1", "Fabric"), ["dependencies"] = new JArray(),
                ["modules"] = new JArray(new JObject { ["name"] = "fabric.mod.json" }),
                ["hashes"] = new JArray(new JObject { ["algo"] = 1, ["value"] = sha }) }) };
        };
        object cfPack = Update("ReadPackage", cf, Path.Combine(root, "cf-stage"), CancellationToken.None, request)!;
        object cfFile = ((IDictionary)Get(cfPack, "Files")!)["mods/fixture.jar"]!;
        Check(Text(cfFile, "Identity") == "curseforge:123" && Text(cfFile, "Hash") == "sha1:" + sha, "MP-17", "CurseForge author identity/hash missing");
        Check(requests == 1, "MP-17-request", "metadata resolution did not remain one confirmed request");
        object optionalPackage = Read(Pack(root, "optional", new()), Path.Combine(root, "optional-stage"));
        object optional = New("PackUpdateFile");
        Set(optional, "Relative", "mods/optional.jar"); Set(optional, "Hash", Get(mod, "Hash")); Set(optional, "Payload", incoming); Set(optional, "OptionalFile", true);
        ((IDictionary)Get(optionalPackage, "Files")!).Add("mods/optional.jar", optional);
        object optionalPlan = Update("Plan", game, saved, optionalPackage)!;
        Check(Conflict(Changes(optionalPlan).Single(change => Text(change, "Relative") == "mods/optional.jar")), "MP-18", "previously unselected optional mod enabled");
        Check(!File.Exists(Path.Combine(game, "mods/optional.jar")), "MP-18-bytes", "optional planning wrote a file");
        Check(JObject.Parse(File.ReadAllText(Path.Combine(backup, "transaction.json")))["complete"]?.Value<bool>() == true, "MP-19", "completed durable transaction journal changed");
        Check(!((IEnumerable)Update("Pending", version)!).Cast<object>().Any(), "MP-20", "completed/stale-rejected update left a pending transaction");
    }

    internal static string Pack(string root, string name, Dictionary<string, string> overrides, Dictionary<string, string>? downloads = null, bool fabric = true)
    {
        string path = Path.Combine(root, name + ".mrpack");
        JArray files = new();
        foreach ((string relative, string text) in downloads ?? new Dictionary<string, string>())
        {
            string input = Path.Combine(root, "hash-input"); Put(input, text);
            files.Add(new JObject { ["path"] = relative, ["hashes"] = new JObject { ["sha1"] = Digest(input).Split(':')[1] }, ["downloads"] = new JArray("https://example.invalid/" + relative), ["fileSize"] = Encoding.UTF8.GetByteCount(text) });
        }
        JObject dependencies = new() { ["minecraft"] = "1.20.1" };
        if (fabric) dependencies["fabric-loader"] = "0.16.0";
        JObject manifest = new() { ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = name, ["name"] = "Fixture pack", ["dependencies"] = dependencies, ["files"] = files };
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            ZipText(zip, "modrinth.index.json", manifest.ToString());
            foreach ((string relative, string text) in overrides) ZipText(zip, "overrides/" + relative, text);
        }
        return path;
    }
    private static void FabricJar(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        ZipText(zip, "fabric.mod.json", new JObject { ["schemaVersion"] = 1, ["id"] = "fixture_same_mod", ["version"] = version }.ToString());
    }
    internal static object? Update(string name, params object?[] arguments) => Native.Static("PCL.ModpackUpdate", name, arguments);
    internal static object Read(string path, string stage) => Update("ReadPackage", path, stage, CancellationToken.None, null)!;
    internal static string Digest(string path) => (string)Update("Digest", path, "sha1")!;
    internal static object New(string name) => Activator.CreateInstance(Native.Type("PCL." + name), true)!;
    internal static object? Get(object target, string name) => Native.Get(target, name);
    internal static string? Text(object target, string name) => (string?)Get(target, name);
    internal static void Set(object target, string name, object? value) => Native.Set(target, name, value);
    internal static List<object> Changes(object plan) => ((IEnumerable)Get(plan, "Changes")!).Cast<object>().ToList();
    internal static bool Conflict(object change) => (bool)Get(change, "Conflict")!;
    internal static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    internal static void ZipText(ZipArchive zip, string path, string text) { using StreamWriter writer = new(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
    internal static void Check(bool condition, string id, string message) => Native.Check(condition, id + ": " + message);
    private static void Reject(Action action, string id, string message)
    {
        bool rejected = false;
        try { action(); } catch (Exception) { rejected = true; }
        Check(rejected, id, message);
    }
}
