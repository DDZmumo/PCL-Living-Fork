using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.EnvironmentHistoryChecks;

namespace PCLLF.Native.Facade.Tests;

internal static class EnvironmentHistoryEngineChecks
{
    internal static void Run(Scope scope)
    {
        string mc = Path.Combine(scope.Root, "engine/minecraft"), version = Path.Combine(mc, "versions/example"), library = Path.Combine(mc, "libraries/fixture/core.jar");
        Put(library, "library-old"); Dictionary<string, string> dependencies = new() { ["libraries/fixture/core.jar"] = library };
        const string oldJson = "{\"id\":\"example\",\"inheritsFrom\":\"1.20.1\",\"futureVersionMetadata\":{\"opaque\":[7,\"old-unknown\"]}}";
        const string newJson = "{\"id\":\"example\",\"inheritsFrom\":\"1.21.1\",\"futureVersionMetadata\":{\"opaque\":[8,\"new-unknown\"]}}";
        const string oldConfig = "{\"MinecraftInstallManifest\":{\"loader\":\"old\"},\"unknownFutureLauncherData\":{\"preserve\":true}}";
        const string newConfig = "{\"MinecraftInstallManifest\":{\"loader\":\"new\"},\"unknownFutureLauncherData\":{\"preserve\":false}}";
        Put(Path.Combine(version, "example.json"), oldJson); Put(Path.Combine(version, "example.jar"), "minecraft-old"); Put(Path.Combine(version, "PCL/config.json"), oldConfig); Put(Path.Combine(version, "PCL/Setup.ini"), "VersionArgumentIndieV2:True");
        foreach (var file in new[] { ("mods/old.jar", "mod-old"), ("mods/disabled.jar.disabled", "disabled"), ("config/author.toml", "old-config"), ("options.txt", "old-keybinds"), ("saves/world/level.dat", "world-current"), ("screenshots/keep.png", "screenshot"), ("config/xaero/waypoints.txt", "map-current") }) Put(Path.Combine(version, file.Item1), file.Item2);
        string first = Capture(version, version, "before update", dependencies, "Minecraft 1.20.1 + old loader");
        JObject point = Points(version)[0]; Check("E036", !(bool)point["healthy"]!);
        string manifest = PointFile(version, first); JObject record = JObject.Parse(File.ReadAllText(manifest)); JObject unknown = new() { ["future"] = new JArray(1, "unchanged", true) }; record["futurePointMetadata"] = unknown.DeepClone(); Put(manifest, record.ToString());
        History("MarkHealthy", version, first, false); Check("E038", !(bool)Points(version)[0]["healthy"]!);
        History("MarkHealthy", version, first, true); Check("E040", (bool)Points(version)[0]["healthy"]!);
        Native.Check(JToken.DeepEquals(JObject.Parse(File.ReadAllText(manifest))["futurePointMetadata"], unknown), "History status write dropped unknown future point data.");
        int BlobCount() => Directory.EnumerateFiles(scope.History, "*", SearchOption.AllDirectories).Count(file => Path.GetFileName(Path.GetDirectoryName(file)) == "objects");
        int before = BlobCount(); string second = Capture(version, version, "same environment", dependencies, "same"); Check("E044", BlobCount() == before);
        Put(Path.Combine(version, "example.json"), newJson); Put(Path.Combine(version, "example.jar"), "minecraft-new"); Put(Path.Combine(version, "PCL/config.json"), newConfig);
        foreach (var file in new[] { ("mods/old.jar", "mod-new"), ("mods/new.jar", "added-mod"), ("config/author.toml", "new-config"), ("config/new.toml", "added-config"), ("options.txt", "new-keybinds"), ("saves/world/level.dat", "world-progress"), ("config/xaero/waypoints.txt", "map-progress") }) Put(Path.Combine(version, file.Item1), file.Item2);
        File.Delete(library); string undo = Restore(version, version, mc, first);
        foreach (var file in new[] { ("example.jar", "minecraft-old"), ("mods/old.jar", "mod-old"), ("config/author.toml", "old-config"), ("options.txt", "old-keybinds"), ("saves/world/level.dat", "world-progress"), ("screenshots/keep.png", "screenshot"), ("config/xaero/waypoints.txt", "map-progress") }) Check("E057", File.ReadAllText(Path.Combine(version, file.Item1)) == file.Item2);
        Check("E058", !File.Exists(Path.Combine(version, "mods/new.jar")) && !File.Exists(Path.Combine(version, "config/new.toml")));
        Check("E059", File.ReadAllText(library) == "library-old");
        Check("E060", File.ReadAllText(Path.Combine(version, "example.json")).Contains("1.20.1", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(version, "PCL/config.json")).Contains("old", StringComparison.Ordinal));
        Native.Check(File.ReadAllText(Path.Combine(version, "example.json")) == oldJson && File.ReadAllText(Path.Combine(version, "PCL/config.json")) == oldConfig, "Environment restore changed unknown JSON fields or exact source bytes.");
        Restore(version, version, mc, undo); Check("E062", File.ReadAllText(Path.Combine(version, "mods/new.jar")) == "added-mod" && File.ReadAllText(Path.Combine(version, "options.txt")) == "new-keybinds");
        Native.Check(File.ReadAllText(Path.Combine(version, "example.json")) == newJson && File.ReadAllText(Path.Combine(version, "PCL/config.json")) == newConfig, "Undo dropped unknown JSON fields.");
        IOException injected = new("injected failure");
        Exception observed = Reject("E064", () => Restore(version, version, mc, first, before: index => { if (index == 1) throw injected; }));
        Native.Check(ReferenceEquals(observed, injected), "History transaction changed the original injected exception identity.");
        Check("E065", File.ReadAllText(Path.Combine(version, "example.jar")) == "minecraft-new" && File.ReadAllText(Path.Combine(version, "mods/new.jar")) == "added-mod"); NotRestoring();
        using (CancellationTokenSource cancel = new()) { cancel.Cancel(); Native.Check(Reject("E067", () => Restore(version, version, mc, first, cancel.Token)) is OperationCanceledException, "History pre-cancellation changed exception kind."); }
        Reject("E068", () => Restore(version, Path.Combine(scope.Root, "other-game"), mc, first)); NotRestoring();
        FileLocksAndCancellation(version, mc, first);
        int blobs = BlobCount(); Reject("E071", () => History("DeletePoints", version, new[] { first, "../invalid" })); Check("E072", Points(version).Any(p => (string?)p["id"] == first));
        string deleted = (string)History("DeletePoints", version, new[] { second })!;
        Check("E074", !Points(version).Any(p => (string?)p["id"] == second)); Check("E075", !File.Exists(Path.Combine(deleted, "points", second + ".json")) && !Directory.Exists(Path.Combine(deleted, "deleted"))); Check("E076", BlobCount() <= blobs && Points(version).All(point => ((JObject)point["files"]!).Properties().All(file => File.Exists(Blob(version, point, file.Name))))); Check("E077", File.ReadAllText(Path.Combine(version, "mods/new.jar")) == "added-mod");
        JObject firstPoint = Points(version).Single(p => (string?)p["id"] == first); Put(Blob(version, firstPoint, "v/example.jar"), "corrupted");
        int pointsBeforeCorruption = Points(version).Count; Reject("E083", () => Restore(version, version, mc, first)); Check("E084", File.ReadAllText(Path.Combine(version, "example.jar")) == "minecraft-new");
        Native.Check(Points(version).Count == pointsBeforeCorruption, "Corrupt restore published a safety snapshot before validating all payloads."); NotRestoring();
        string actual = Path.Combine(mc, "versions/adapter"); ModMinecraft.McInstance instance = Instance(actual, "1.20.1"); Put(Path.Combine(actual, "adapter.jar"), "fixture-client");
        string adapter = (string)History("CaptureInstance", instance, "adapter", None)!; Check("E095", !string.IsNullOrEmpty(adapter));
        Console.WriteLine("PASS environment-history engine: exact source/unknown-data restoration, personal-file preservation, offline dependencies, status/dedup, undo, transaction rollback, cancellation, file locks and genuine instance adapter.");
    }

    private static void FileLocksAndCancellation(string version, string mc, string first)
    {
        string file = Path.Combine(version, "mods/new.jar"); int count = Points(version).Count;
        using (FileStream held = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Native.Check(Failure(() => Capture(version, version, "locked fixture"), "capture with exclusive file lock") is IOException, "Capture ignored a real exclusive file handle.");
            Native.Check(Failure(() => Restore(version, version, mc, first), "restore with exclusive file lock") is IOException, "Restore ignored a real exclusive file handle.");
        }
        Native.Check(Points(version).Count == count && File.ReadAllText(file) == "added-mod", "Rejected locked operation changed files or committed a point."); NotRestoring();
        using (CancellationTokenSource cancel = new()) {
            bool reached = false; Action<double> progress = value => { if (value > 0.02 && value < 0.98) { reached = true; cancel.Cancel(); } };
            Native.Check(Failure(() => History("CaptureWithProgress", version, version, "cancel during I/O", cancel.Token, progress, null, ""), "streaming capture cancellation") is OperationCanceledException && reached && Points(version).Count == count, "Canceled streaming capture published incomplete metadata.");
        }
        using (CancellationTokenSource cancel = new()) {
            bool reached = false; Action<int> before = index => { if (index == 1) { reached = true; cancel.Cancel(); } };
            Native.Check(Failure(() => Restore(version, version, mc, first, cancel.Token, before), "cancel during apply") is OperationCanceledException && reached, "Restore did not observe cancellation at its real apply boundary.");
            Native.Check(File.ReadAllText(file) == "added-mod" && File.ReadAllText(Path.Combine(version, "example.jar")) == "minecraft-new", "Mid-apply cancellation did not roll back the environment."); NotRestoring();
        }
        string store = (string)History("Store", version)!;
        Native.Check(!Directory.EnumerateFiles(store, "*.tmp-*", SearchOption.AllDirectories).Any(), "Failed/canceled history capture left temporary blobs.");
    }
}
