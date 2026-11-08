using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Actual LF Main facades/DTOs plus the existing real Core controller fixture.
// All mutable IO is under the enclosing GUID host; identity/Registry/INI guards stay installed.
internal static class LocalFeatureChecks
{
    private static readonly Dictionary<string, HashSet<int>> Seen = new(StringComparer.Ordinal);
    private static int executed;
    internal static void Run(FixtureEnvironment fixture)
    {
        Native.Check(OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "Local features require actual Windows STA and the genuine Main.");
        Native.Check(Native.Type("PCL.PersonalGameSettings").Assembly == Native.Main && Native.Main == typeof(PCL.Application).Assembly,
            "Local features cannot run against stand-in production types.");
        Seen.Clear(); executed = 0;
        foreach (string family in LocalFeatureOriginalChecks.Cases.Keys) Seen.Add(family, new());
        using (Scope scope = new(fixture, "local")) LocalFeatureOptionsChecks.Run(scope);
        using (Scope scope = new(fixture, "regression")) LocalFeatureRegressionChecks.Run(scope);
        Native.Static("PCL.ModSecret", "ThemeRefresh", 0); // Original UI fixtures use the real palette setup.
        using (Scope scope = new(fixture, "groups")) LocalFeatureKeybindingChecks.Run(scope);
        using (Scope scope = new(fixture, "recent")) LocalFeatureRecentChecks.Run(scope);
        LaunchMoreSettingsChecks.Run(fixture);
        foreach (var (family, original) in LocalFeatureOriginalChecks.Cases)
            Native.Check(Seen[family].SetEquals(original.Keys), "Incomplete original local-feature assertion selection: " + family + " missing " + string.Join(',', original.Keys.Except(Seen[family])));
        PersonalOptionsPageChecks.Run(fixture);
        var about = new PageOtherAbout();
        string notices = ((MyTextBox)about.FindName("LabThirdPartyLicenses")).Text;
        Native.Check(notices.Contains("Mozilla Public License Version 2.0",StringComparison.Ordinal) &&
            notices.Contains("licenses/MPL-COVERED-FILES.txt",StringComparison.Ordinal) &&
            notices.Contains("MeloongCore",StringComparison.Ordinal) && notices.Contains("MICROSOFT .NET LIBRARY",StringComparison.Ordinal) &&
            notices.Contains("GNU GENERAL PUBLIC LICENSE",StringComparison.Ordinal), "The actual about page is missing offline scope or third-party license texts.");
        fixture.AssertNoForbiddenIo();
        Console.WriteLine("PASS local-features: mapped source assertion IDs covered (" + LocalFeatureOriginalChecks.Cases.Sum(p => p.Value.Count) + " sites, " + executed + " executions), including the approved basic-only sync policy; actual Main files/DTO/UI, real Core controller and independent Windows CLR4 FOV/path API oracle. No real game/account/network.");
    }
    internal static void Check(string family, int line, bool condition, string? detail = null)
    {
        string original = LocalFeatureOriginalChecks.Cases[family][line];
        Seen[family].Add(line); executed++;
        Native.Check(condition, family + " original:" + line + " " + original + " " + detail);
    }
    internal sealed class Scope : IDisposable
    {
        internal readonly string Root;
        private readonly object? originalRoot;
        private readonly Stack<Action> restoreCaches = new();
        private readonly HashSet<(IDictionary Cache, string Key)> leasedKeys = new();
        internal Scope(FixtureEnvironment fixture, string name)
        {
            Root = Path.Combine(fixture.Data, "local-features-" + name);
            Native.Check(!Directory.Exists(Root), "Refusing to adopt an existing local-feature fixture.");
            Directory.CreateDirectory(Root);
            originalRoot = Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").GetValue(null);
            Native.Check(!(bool)Native.Field(Native.Type("PCL.ModWatcher"), "HasRunningMinecraft").GetValue(null)!, "A real/previous game state cannot be adopted by this fixture.");
            Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, Path.Combine(Root, "profile"));
        }
        internal void PreserveCache(IDictionary cache, string key)
        {
            if (!leasedKeys.Add((cache, key))) return;
            bool existed = cache.Contains(key); object? previous = existed ? cache[key] : null;
            restoreCaches.Push(() => {
                if (existed) cache[key] = previous; else cache.Remove(key);
                Native.Check(cache.Contains(key) == existed && (!existed || ReferenceEquals(cache[key], previous)), "Owned local-feature cache key did not restore its original presence/value.");
            });
        }
        internal void SetGlobal(string key, object value)
        {
            IDictionary cache = (IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!;
            PreserveCache(cache, ""); cache[""] = value;
        }
        public void Dispose()
        {
            List<Exception> failures = new();
            while (restoreCaches.Count > 0) { try { restoreCaches.Pop()(); } catch (Exception error) { failures.Add(error); } }
            try { Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, originalRoot); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Local-feature state restoration failed.", failures);
        }
    }
    internal static object? Data(string name, params object?[] args) => Native.Static("PCL.LocalGameData", name, args);
    internal static object? Options(string name, params object?[] args) => Native.Static("PCL.PersonalGameSettings", name, args);
    internal static object? Quick(string name, params object?[] args) => Native.Static("PCL.QuickPlayWorlds", name, args);
    internal static object? Bindings(string name, params object?[] args) => Native.Static("PCL.InstanceKeybindings", name, args);
    internal static void Put(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false)); }
    internal static object New(string name) => Activator.CreateInstance(Native.Type(name), true)!;
    internal static T Part<T>(FrameworkElement owner, string name) where T : class => owner.FindName(name) as T ?? throw new MissingMemberException(owner.GetType().FullName, name);
    internal static object? Cache(ModMinecraft.McInstance owner, string key) => ((IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!)[owner.PathVersion];
    internal static void Cache(ModMinecraft.McInstance owner, string key, object value) => ((IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!)[owner.PathVersion] = value;
    internal static ModMinecraft.McInstance Instance(Scope scope, string name, string version)
    {
        string folder = Path.Combine(scope.Root, ".minecraft", "versions", name); Directory.CreateDirectory(folder);
        ModMinecraft.McInstance owner = new(folder) { Version = new McVersion(version, true) };
        foreach (DictionaryEntry item in Native.Entries) {
            object entry = item.Value!;
            if (Native.Get(entry, "Source")!.ToString() == "Instance") {
                IDictionary cache = (IDictionary)Native.Get(entry, "ValueCache")!;
                scope.PreserveCache(cache, owner.PathVersion); cache[owner.PathVersion] = Native.Get(entry, "DefaultValue");
            }
        }
        Cache(owner, "VersionArgumentIndieV2", true); Put(Path.Combine(folder, "PCL/Setup.ini"), "VersionArgumentIndieV2:True");
        return owner;
    }
    internal static Exception Rejected(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Actual Main unexpectedly accepted the rejected local-feature operation.");
    }
}
