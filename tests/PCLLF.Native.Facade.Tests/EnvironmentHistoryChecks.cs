using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Pinned LF f4/main50 fixture behavior; no CE format/policy is imported.
internal static class EnvironmentHistoryChecks
{
    internal static readonly CancellationToken None = CancellationToken.None;
    internal static void Run(FixtureEnvironment fixture)
    {
        EnvironmentHistorySourceChecks.Reset();
        using Scope scope = new(fixture);
        EnvironmentHistoryEngineChecks.Run(scope); fixture.AssertNoForbiddenIo();
        EnvironmentHistoryRetentionChecks.Run(scope); fixture.AssertNoForbiddenIo();
        EnvironmentRecentChangesChecks.Run(scope); fixture.AssertNoForbiddenIo();
        EnvironmentCoverageChecks.Run(scope); fixture.AssertNoForbiddenIo();
        EnvironmentHistorySourceChecks.Complete();
    }
    internal sealed class Scope : IDisposable
    {
        internal readonly string Root, History;
        private readonly object? previousHistory, previousProfile;
        internal Scope(FixtureEnvironment fixture)
        {
            Root = Path.Combine(fixture.Data, "environment-history"); History = Path.Combine(Root, "history");
            Native.Check(!Directory.Exists(Root), "Refusing to adopt an existing environment fixture."); Directory.CreateDirectory(Root);
            previousHistory = Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").GetValue(null);
            previousProfile = Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").GetValue(null);
            object service = Native.Field(Native.Type("PCL.EnvironmentHistory"), "Service").GetValue(null)!;
            Native.Check(service.GetType().Assembly.GetName().Name == "PCLCS" && service.GetType().GetGenericTypeDefinition().FullName == "PCLCS.EnvironmentHistoryEngine`1" && service.GetType().GetGenericArguments()[0] == typeof(ModMinecraft.McInstance), "Actual Main history facade does not use its genuine Core/Main model closure.");
            Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").SetValue(null, History);
            Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, Path.Combine(Root, "profile"));
        }
        public void Dispose()
        {
            try { NotRestoring(); Native.Check(!(bool)Native.Property(Native.Type("PCL.EnvironmentHistoryUi"), "IsBusy").GetValue(null)!, "History fixture left UI work running."); }
            finally {
                Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").SetValue(null, previousHistory);
                Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, previousProfile);
            }
        }
    }
    internal static object? History(string name, params object?[] args) => Native.Static("PCL.EnvironmentHistory", name, args);
    internal static object Build(string version, string game, CancellationToken token = default) => Native.Static("PCL.RecentEnvironmentChanges", "Build", version, game, token)!;
    internal static List<RecentEnvironmentChangeData> Changes(object report) => ((IEnumerable)Native.Get(report, "Changes")!).Cast<RecentEnvironmentChangeData>().ToList();
    internal static string Analysis(object report) => (string)Native.Call(report.GetType(), "ToAnalysisText", report, 30)!;
    internal static List<JObject> Points(string version) => (List<JObject>)History("Points", version)!;
    internal static string Capture(string version, string game, string label, Dictionary<string, string>? dependencies = null, string description = "", CancellationToken token = default) => (string)History("Capture", version, game, label, token, dependencies, description)!;
    internal static string Restore(string version, string game, string mc, string id, CancellationToken token = default, Action<int>? before = null, Func<string>? captureBefore = null) => (string)History("Restore", version, game, mc, id, token, before, captureBefore)!;
    internal static string PointFile(string version, string id) => Path.Combine((string)History("Store", version)!, "points", id + ".json");
    internal static string Blob(string version, JObject point, string key) => Path.Combine((string)History("Store", version)!, "objects", point["files"]![key]!.ToString()[5..]);
    internal static void Put(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
    internal static void Check(string id, bool condition) => EnvironmentHistorySourceChecks.Check(id, condition);
    internal static Exception Reject(string id, Action action)
    {
        Exception? failure = null; try { action(); } catch (Exception caught) { failure = caught; }
        Check(id, failure is not null); return failure!;
    }
    internal static Exception Failure(Action action, string label)
    {
        try { action(); } catch (Exception failure) { return failure; }
        throw new InvalidOperationException("Actual history operation unexpectedly accepted " + label);
    }
    internal static void NotRestoring() => Native.Check((int)Native.Property(Native.Type("PCL.EnvironmentHistory"), "Restoring").GetValue(null)! == 0, "Actual history restore count leaked after return/failure/cancellation.");
    internal static T Part<T>(FrameworkElement owner, string name) where T : class => owner.FindName(name) as T ?? throw new MissingMemberException(owner.GetType().FullName, name);
    internal static object New(string type) => Activator.CreateInstance(Native.Type(type), true)!;
    internal static ModMinecraft.McInstance Instance(string folder, string version = "1.21.11")
    {
        string name = Path.GetFileName(folder);
        Put(Path.Combine(folder, name + ".json"), new JObject { ["id"] = name, ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main", ["libraries"] = new JArray(), ["downloads"] = new JObject(), ["arguments"] = new JObject { ["game"] = new JArray(), ["jvm"] = new JArray() } }.ToString());
        Put(Path.Combine(folder, "PCL/Setup.ini"), "VersionArgumentIndieV2:True");
        ModMinecraft.McInstance instance = new(folder) { Version = new McVersion(version, true) };
        foreach (DictionaryEntry item in Native.Entries) { object entry = item.Value!; if (Native.Get(entry, "Source")!.ToString() == "Instance") ((IDictionary)Native.Get(entry, "ValueCache")!)[instance.PathVersion] = Native.Get(entry, "DefaultValue"); }
        Cache(instance, "VersionArgumentIndieV2", true); return instance;
    }
    // Preserve the original fixture call sites, but exercise the supported preference boundary:
    // provider attachment and INI publication may legitimately invalidate private ValueCache rows.
    internal static object? Cache(ModMinecraft.McInstance instance, string key) => Settings.Get(key, instance);
    internal static void Cache(ModMinecraft.McInstance instance, string key, object value) {
        Settings.Set(key, value, instance);
        Native.Check(Equals(Settings.Get(key, instance), value), "Owned history preference did not survive its real Set/Get path: " + key);
        string expected = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        string[] persisted = File.ReadAllLines(Path.Combine(instance.PathVersion, "PCL", "Setup.ini"));
        Native.Check(persisted.Any(line => string.Equals(line.Trim(), key + ":" + expected, StringComparison.OrdinalIgnoreCase)), "Owned history preference was not persisted to its instance INI: " + key);
    }
    internal static void PageState(object page, string name, object value)
    {
        Native.Check(page.GetType().FullName == "PCL.PageInstanceRestorePoints" && name is "Owner" or "RetentionDirty" or "Entries", "Unreviewed history-page state hook.");
        PropertyInfo property = Native.Property(page.GetType(), name);
        Native.Check(property.PropertyType == (name == "Owner" ? typeof(ModMinecraft.McInstance) : name == "RetentionDirty" ? typeof(bool) : typeof(List<JObject>)), "History page forwarding state type changed.");
        property.SetValue(page, value);
    }
    internal static void WithJunction(string junction, string target, Action verify)
    {
        junction = Path.GetFullPath(junction).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        target = Path.GetFullPath(target).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // cmd builtins parse '/' inside unquoted paths as switches; normalize and
        // quote both paths explicitly. Reject cmd expansion before any process starts.
        foreach (string path in new[] { junction, target }) Native.Check(path.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) < 0, "Unsafe junction fixture command path.");
        Native.Check(!Directory.Exists(junction) && !File.Exists(junction), "Refusing an existing junction fixture.");
        Process? process = null;
        try {
            ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Arguments = "/D /S /V:OFF /C \"mklink /J \"" + junction + "\" \"" + target + "\"\"";
            process = Process.Start(start) ?? throw new IOException("Could not start owned junction command.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) throw new TimeoutException("Owned junction command timed out.");
            Native.Check(Task.WaitAll([output, error], 5000), "Owned junction command output did not close.");
            Native.Check(process.ExitCode == 0, "Owned junction command failed: " + output.Result + error.Result);
            Native.Check((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "Fixture is not an actual junction."); verify();
        } finally {
            try { if (process is not null) { try { if (!process.HasExited) { process.Kill(); Native.Check(process.WaitForExit(5000), "Owned junction command did not stop."); } } finally { process.Dispose(); } } }
            finally { try { _ = File.GetAttributes(junction); Directory.Delete(junction, recursive: false); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
        }
    }
}
