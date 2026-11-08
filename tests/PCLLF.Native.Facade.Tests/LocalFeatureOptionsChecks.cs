using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.LocalFeatureChecks;

namespace PCLLF.Native.Facade.Tests;
internal static class LocalFeatureOptionsChecks
{
    private static void L(int line, bool value, string? detail = null) => Check("local", line, value, detail);
    internal static void Run(Scope scope)
    {
        ModMinecraft.McInstance source = Instance(scope, "source", "1.20.1"), target = Instance(scope, "target", "1.20.1"), legacy = Instance(scope, "legacy", "1.12.2");
        Put(Path.Combine(source.PathVersion, "options.txt"), "mouseSensitivity:0.8\r\nfov:0.3\r\nkey_key.forward:key.keyboard.r\r\nshowSubtitles:true\r\nrenderDistance:9\r\nresourcePacks:[\"author-source.zip\"]\r\n");
        const string original = "mouseSensitivity:0.2\r\nfov:0.0\r\nkey_key.forward:key.keyboard.w\r\nshowSubtitles:false\r\nrenderDistance:32\r\nresourcePacks:[\"author-target.zip\"]\r\nkey_mod.magic:key.keyboard.v\r\n";
        string targetFile = Path.Combine(target.PathVersion, "options.txt"); Put(targetFile, original);
        scope.SetGlobal("LaunchPersonalOptionsSync", true);
        Options("ImportFrom", source);
        FirstLaunch(scope);
        object session = Options("Prepare", target)!;
        L(43, session is not null && session.GetType().FullName == "PCL.PersonalOptionsSession");
        foreach (string name in new[] { "Id", "VersionPath", "GameFolder", "Baseline", "Keys", "Tracked" }) L(44, session!.GetType().GetField(name, Native.Flags) is not null, name);
        L(45, (string)Native.Get(session!, "VersionPath")! == target.PathVersion && (string)Native.Get(session!, "GameFolder")! == target.PathIndie && !(bool)Native.Get(session!, "Tracked")!);
        string merged = File.ReadAllText(targetFile);
        // The user-requested basic-only scope supersedes the historical subtitle-copy expectation.
        L(47, merged.Contains("mouseSensitivity:0.8", StringComparison.Ordinal) && merged.Contains("key_key.forward:key.keyboard.r", StringComparison.Ordinal) && merged.Contains("showSubtitles:false", StringComparison.Ordinal), "Basic-only policy must preserve version-specific subtitle settings.");
        foreach (string line in new[] { "renderDistance:32", "resourcePacks:[\"author-target.zip\"]", "key_mod.magic:key.keyboard.v" }) L(48, merged.Contains(line, StringComparison.Ordinal), line);
        L(49, merged.EndsWith("\r\n", StringComparison.Ordinal) && File.ReadAllText(targetFile + ".pcl-backup") == original);
        Put(targetFile, merged.Replace("mouseSensitivity:0.8", "mouseSensitivity:0.6").Replace("renderDistance:32", "renderDistance:20"));
        Options("Finish", session);
        JObject profile = (JObject)Data("ReadJson", "personal-options.json")!;
        L(53, (string?)profile["values"]!["mouseSensitivity"] == "0.6" && profile["values"]!["renderDistance"] is null);
        Cache(target, "VersionPersonalOptionsSync", 1); L(55, Options("Prepare", target) is null); Cache(target, "VersionPersonalOptionsSync", 0);
        Put(Path.Combine(legacy.PathVersion, "options.txt"), "key_key.forward:17\nmouseSensitivity:0.1\n"); Options("Prepare", legacy);
        L(59, File.ReadAllText(Path.Combine(legacy.PathVersion, "options.txt")).Contains("key_key.forward:19", StringComparison.Ordinal));
        HashSet<string> keys = new(StringComparer.Ordinal) { "key_key.forward" }; JObject unknown = JObject.Parse("{\"key_key.forward\":\"key.keyboard.unrecognized\"}");
        L(62, keys is not null && unknown is not null);
        L(63, Options("ForVersion", "key_key.forward", "key.keyboard.unrecognized", false) is null);
        L(65, (string)Options("MergeText", "key_key.forward:17\n", unknown, false, keys, false)! == "key_key.forward:17\n");
        HashSet<string> viewKeys = new(StringComparer.Ordinal) { "fov" };
        L(68, (string)Options("MergeText", "fov:70\n", JObject.Parse("{\"fov\":\"0.5\"}"), true, viewKeys, true)! == "fov:90\n");
        L(70, (string)Options("MergeText", "fov:0.0\n", JObject.Parse("{\"fov\":\"90\"}"), false, viewKeys, false)! == "fov:0.5\n");
        JObject baseline = LocalFeatureLegacyOracleChecks.Measure();
        L(72, (int)baseline["RuntimeMajor"]! == 4 && (string?)baseline["PSEdition"] == "Desktop");
        foreach (JObject sample in (JArray)baseline["Fov"]!) {
            string raw = (string)sample["Raw"]!; bool fallback = (bool)sample["Fallback"]!;
            L(81, (string)Options("MergeText", "fov:" + raw + "\n", JObject.Parse("{\"fov\":\"90\"}"), false, viewKeys, fallback)! == "fov:" + (string)sample["Expected"]! + "\n", "CLR4 raw=" + raw + " fallback=" + fallback);
        }
        object tracked = Options("Prepare", target)!;
        using (Process self = Process.GetCurrentProcess()) Options("Track", tracked, self);
        L(88, (bool)Native.Get(tracked, "Tracked")!); L(89, Options("Prepare", target) is null);
        string journalName = "options-sessions/" + (string)Native.Get(tracked, "Id")! + ".json";
        JObject journal = (JObject)Data("ReadJson", journalName)!; journal["pid"] = 2147483646; journal["started"] = 1L; Data("WriteJson", journalName, journal);
        Put(targetFile, File.ReadAllText(targetFile).Replace("mouseSensitivity:0.6", "mouseSensitivity:0.7")); Options("RecoverExitedSessions");
        profile = (JObject)Data("ReadJson", "personal-options.json")!; L(96, (string?)profile["values"]!["mouseSensitivity"] == "0.7");
        L(98, Rejected(() => Data("AtomicWrite", targetFile, Encoding.UTF8.GetBytes("unexpected-new-data"), "incorrect-old-hash", true)) is IOException);
        foreach (var (version, expected) in new[] { ("1.19.4", false), ("1.20.1", true), ("26.2", true) }) L(99, (bool)Quick("Supported", Instance(scope, "version-" + version, version))! == expected);
        string world = Path.Combine(target.PathVersion, "saves", "World with spaces"); Put(Path.Combine(world, "level.dat"), "synthetic-world"); Put(Path.Combine(world, "region", "keep.txt"), "world-data-preserve");
        foreach (string? id in new[] { null, "", " ", ".", "..", "../outside", "..\\outside", "bad:name", "\0" }) L(101, Rejected(() => Quick("WorldPath", target.PathVersion, id)) is not null, id);
        L(102, (string)Quick("WorldPath", target.PathVersion, "World with spaces")! == world);
        foreach (JObject sample in (JArray)baseline["Paths"]!) {
            string identifier = "A" + (char)(int)sample["Code"]! + "B";
            L(111, Rejected(() => Quick("WorldPath", null, identifier)).GetType().FullName == (string)sample["Error"]!, "CLR4 codepoint=" + (int)sample["Code"]!);
        }
        L(116, Rejected(() => Quick("WorldPath", target.PathVersion, "missing-quickplay-leaf")) is IOException);
        L(117, !Directory.Exists(Path.Combine(target.PathVersion, "saves", "missing-quickplay-leaf")));
        List<string> arguments = new() { "prefix", "--quickPlaySingleplayer", "old world", "--quickPlayMultiplayer", "example.invalid", "--quickPlayRealms", "realm", "--server", "-single-hyphen-value", "--port", "25565", "--server", "--kept", "value", "--SERVER", "upper-kept", "--quickPlayPath=custom.json", "--server=equals-kept", "--quickPlaySingleplayer" };
        object context = Quick("Prepare", target, "World with spaces", arguments, false)!;
        L(121, !arguments.Contains("--quickPlayMultiplayer") && !arguments.Contains("--server") && arguments.Contains("--quickPlaySingleplayer") && arguments[arguments.IndexOf("--quickPlaySingleplayer") + 1] == "World with spaces");
        string[] expectedArguments = { "prefix", "--kept", "value", "--SERVER", "upper-kept", "--quickPlayPath=custom.json", "--server=equals-kept", "--quickPlaySingleplayer", "World with spaces" };
        L(123, arguments.Count == expectedArguments.Length + 2 && arguments[expectedArguments.Length] == "--quickPlayPath");
        for (int i = 0; i < expectedArguments.Length; i++) L(124, arguments[i] == expectedArguments[i], "index=" + i);
        string log = (string)Native.Get(context, "LogFile")!;
        Put(log, "[{\"type\":\"singleplayer\",\"identifier\":\"World with spaces\",\"name\":\"A recent world\",\"lastPlayedTime\":\"2026-10-01T12:00:00Z\"}]"); Quick("Finish", context);
        IList recent = (IList)Quick("Recent")!;
        L(129, recent.Count == 1 && (string)Native.Get(recent[0]!, "VersionPath")! == target.PathVersion && (string)Native.Get(recent[0]!, "Identifier")! == "World with spaces");
        L(130, File.ReadAllText(Path.Combine(world, "region", "keep.txt")) == "world-data-preserve"); File.Delete(Path.Combine(world, "level.dat")); L(132, ((IList)Quick("Recent")!).Count == 0);
        BindingFiles(target, targetFile);
        Console.WriteLine("PASS local-features personal/QuickPlay: basic-only options policy plus session/file/argument/mod binding assertions and independent CLR4 FOV/path comparisons.");
    }
    private static void FirstLaunch(Scope scope)
    {
        foreach (string mode in new[] { "missing", "launcher-stub", "yosbr" }) {
            ModMinecraft.McInstance fresh = Instance(scope, "first-" + mode, "1.20.1");
            string file = Path.Combine(fresh.PathIndie, "options.txt");
            string defaults = Path.Combine(fresh.PathIndie, "config/yosbr/options.txt");
            if (mode == "launcher-stub") Put(file, "lang:zh_cn\nfullscreen:false\n");
            if (mode == "yosbr") Put(defaults, "renderDistance:17\nkey_mod.action:key.keyboard.z\n");
            Native.Check(Options("Prepare", fresh) is not null, "First launch did not create a personal options session: " + mode);
            string actual = File.ReadAllText(file);
            Native.Check(actual.Contains("mouseSensitivity:0.8", StringComparison.Ordinal) && actual.Contains("key_key.forward:key.keyboard.r", StringComparison.Ordinal),
                "First launch skipped selected basic settings: " + mode);
            if (mode == "launcher-stub") Native.Check(actual.Contains("lang:zh_cn", StringComparison.Ordinal) && File.Exists(file + ".pcl-backup"), "Launcher defaults or original-file backup were lost.");
            else Native.Check(!File.Exists(file + ".pcl-backup"), "A previously absent options file acquired a fictitious backup.");
            if (mode == "yosbr") Native.Check(actual.Contains("renderDistance:17", StringComparison.Ordinal) && actual.Contains("key_mod.action:key.keyboard.z", StringComparison.Ordinal) &&
                File.ReadAllText(defaults) == "renderDistance:17\nkey_mod.action:key.keyboard.z\n", "First launch discarded or rewrote modpack defaults.");
            File.Delete(file);
            IList bindings = (IList)Bindings("ReadBindings", fresh.PathIndie, CancellationToken.None)!;
            Native.Check(bindings.Count == 31 && bindings.Cast<object>().All(row => !(bool)Native.Get(row, "Saved")!) && !File.Exists(file),
                "Reading deleted options must return unsaved vanilla defaults without recreating the file.");
        }
    }

    private static void BindingFiles(ModMinecraft.McInstance target, string file)
    {
        Put(file, File.ReadAllText(file) + "key_fixture.action:key.keyboard.r\r\nkey_other.action:key.keyboard.r\r\nkey_unknown.action:key.keyboard.t\r\n");
        foreach (string mod in new[] { "fixture", "other" }) {
            string jar = Path.Combine(target.PathVersion, "mods", mod + ".jar"); Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            using ZipArchive archive = ZipFile.Open(jar, ZipArchiveMode.Create);
            using (StreamWriter writer = new(archive.CreateEntry("fabric.mod.json").Open())) writer.Write("{\"id\":\"" + mod + "\",\"name\":\"Example " + mod + "\"}");
            using (StreamWriter writer = new(archive.CreateEntry("assets/" + mod + "/lang/en_us.json").Open())) writer.Write("{\"" + mod + ".action\":\"Example\\r\\nline\\u000bvertical\"}");
        }
        IList rows = (IList)Bindings("ReadBindings", target.PathVersion, CancellationToken.None)!;
        object entry = rows.Cast<object>().Single(row => (string)Native.Get(row, "Key")! == "key_fixture.action");
        L(145, (int)Native.Get(entry, "SharedCount")! == 3 && ((string)Native.Get(entry, "Owners")!).Contains("Example fixture", StringComparison.Ordinal));
        L(146, (string)Native.Get(entry, "Name")! == "Example line\u000bvertical");
        object unknown = rows.Cast<object>().Single(row => (string)Native.Get(row, "Key")! == "key_unknown.action");
        L(148, (int)Native.Get(unknown, "SharedCount")! == 2 && !((string)Native.Get(unknown, "Owners")!).Contains("Example", StringComparison.Ordinal));
        string before = File.ReadAllText(file); Bindings("Save", target.PathVersion, "key_fixture.action", "key.keyboard.r", "key.keyboard.y");
        L(151, File.ReadAllText(file) == before.Replace("key_fixture.action:key.keyboard.r", "key_fixture.action:key.keyboard.y"));
        L(152, Rejected(() => Bindings("Save", target.PathVersion, "key_fixture.action", "key.keyboard.r", "key.keyboard.u")) is IOException);
        byte[] same = File.ReadAllBytes(file); Put(file + ".pcl-backup", "same-value-backup-sentinel"); Bindings("Save", target.PathVersion, "key_fixture.action", "key.keyboard.y", "key.keyboard.y");
        foreach (string path in new[] { file, file + ".pcl-backup" }) L(156, File.ReadAllBytes(path).SequenceEqual(same), path);
        L(157, (string)Options("NormalizeBinding", "19:ALT")! == "key.keyboard.r:ALT" && (string)Options("BindingForVersion", "key.keyboard.r:ALT", false)! == "19:ALT");
    }
}
