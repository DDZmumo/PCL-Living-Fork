using System.Collections;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCL;

namespace PCLLF.Native.Facade.Tests;

// Close the existing fixture against the REAL Main nested DTOs. Local/Online
// cases stay separate and are never invoked from this launch-dependencies group.
internal static class LaunchDependencyChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string[] names = ["InspectionContext", "ModInfo", "Dependency", "DependencyEdge", "InspectionIssue", "RemovalImpact", "InspectionReport", "OnlineProjectInfo", "OnlineDependency", "OnlineFileInfo"];
        Type[] types = names.Select(name => Native.Type("PCL.ModDependencyInspector+" + name))
            .Concat([Native.Type("PCL.ModDependencyOnline+LookupOptions"), Native.Type("PCL.ModDependencyOnline+LookupCache")]).ToArray();
        foreach (Type type in types) Native.Check(type.Assembly == Native.Main, "Launch fixture must use real Main DTOs: " + type);
        Native.Call(typeof(DependencyCases<,,,,,,,,,,,>).MakeGenericType(types), "Launch", null, environment);
        PackLaunchOriginChecks.Run(environment);
    }
}

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static readonly Type LaunchAutoFill = Native.Type("PCL.ModDependencyAutoFill"), LaunchSession = Native.Type("PCL.LaunchDependencyCheck");
    private static readonly HashSet<string> LaunchSeen = new(StringComparer.Ordinal);
    private static int launchAssertions;
    private static void LD(int line, bool value, string? detail = null) => LaunchAssert("launch", line, value, detail);
    private static void AD(int line, bool value, string? detail = null) => LaunchAssert("activation", line, value, detail);
    private static void TD(int line, bool value, string? detail = null) => LaunchAssert("toggles", line, value, detail);
    private static void LaunchAssert(string script, int line, bool value, string? detail = null)
    {
        string id = script + ":" + line;
        launchAssertions++; LaunchSeen.Add(id);
        Native.Check(value, id + " " + LaunchDependencyOriginalChecks.Assertions[id] + " " + detail);
    }
    private static void LaunchReject(string script, int line, Action action)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        LaunchAssert(script, line, rejected);
    }
    private static IList PlanList(object plan, string name) => (IList)Native.Get(plan, name)!;
    private static string[] PlanNotes(object plan) => ((IEnumerable<string>)Native.Get(plan, "Notes")!).ToArray();
    private static TReport PlanReport(object plan) => (TReport)Native.Get(plan, "Report")!;
    private static ResourceVersion PlanVersion(object item) => (ResourceVersion)Native.Get(item, "Version")!;
    private static int LaunchInstall(object plan, CancellationToken token = default, Action<List<ModNet.NetFile>, CancellationToken>? download = null)
        => (int)Native.Call(LaunchAutoFill, "Install", null, plan, token, download)!;
    private static object LaunchNew(string name) => Activator.CreateInstance(Native.Type(name), true)!;
    private static object LaunchConstruct(string name, params object?[] args)
    {
        Type type = Native.Type(name);
        ConstructorInfo constructor = type.GetConstructors(Native.Flags).Single(ctor => ctor.GetParameters().Length == args.Length);
        return constructor.Invoke(args);
    }
    private static string LaunchFabric(string path, string id, JObject? dependencies = null, string version = "1.0.0", string? displayName = null)
        => Fabric(Path.GetDirectoryName(path)!, Path.GetFileName(path), id, version, new JObject { ["depends"] = dependencies ?? new JObject(), ["name"] = displayName ?? id });
    private static bool LaunchAccepted(string folder, TReport report) => (bool)Native.Call(LaunchSession, "WasAccepted", null, folder, report)!;
    private static object LaunchPlan(string root, TReport report, TOptions options)
        => Native.Call(LaunchAutoFill, "BuildPlan", null, root, report, CancellationToken.None, options)!;
    private static object ActivationPlan(string root, TReport report)
        => Native.Static("PCL.ModDependencyActivation", "Prepare", root, report, CancellationToken.None)!;

    private static void Launch(FixtureEnvironment environment)
    {
        LaunchSeen.Clear(); launchAssertions = 0;
        object previousCheck = Settings.Entries["LaunchModDependencyCheck"].ValueCache[""];
        object previousAutoFill = Settings.Entries["LaunchModDependencyAutoFill"].ValueCache[""];
        string name = "PclLaunchDependencies-" + Guid.NewGuid().ToString("N");
        Scope scope = new(environment, name);
        try
        {
            Native.Call(LaunchSession, "SettingsChanged", null);
            LaunchLoginPort();
            LaunchFixture fixture = new(scope.Root);
            object unavailable = LaunchRepair(fixture);
            LaunchViews(fixture, unavailable);
            LaunchActivation(scope.Root);
            LaunchPlatformActivation(scope.Root);
            LaunchToggles();
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            Native.Call(LaunchSession, "SettingsChanged", null);
            Native.Cache("LaunchModDependencyCheck", previousCheck);
            Native.Cache("LaunchModDependencyAutoFill", previousAutoFill);
            LaunchDrainUi();
            scope.Dispose();
            string full = Path.GetFullPath(scope.Root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            LD(261, full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(Path.GetFileName(full), "\\APclLaunchDependencies-[a-f0-9]{32}\\z"));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        string[] missing = LaunchDependencyOriginalChecks.Assertions.Keys.Except(LaunchSeen).ToArray();
        Native.Check(missing.Length == 0, "Launch original observable IDs not reached: " + string.Join(", ", missing));
        Console.WriteLine("PASS native launch-dependencies: " + launchAssertions + " assertions across all " + LaunchSeen.Count + " original observable IDs; real Main login port, repair/activation/launch adapters and WPF controls. No game/authentication/HTTP started.");
        Console.WriteLine("PENDING activation:77,80: optional external real Fabric API JAR requires a pinned supplied artifact; nested synthetic real-ZIP/rename/recheck assertions all run here.");
    }

    private sealed class LaunchFixture
    {
        internal readonly string Root, Mods, Source, Library, Base;
        internal readonly TContext Context = new() { MinecraftVersion = "1.21.1", LoaderKnown = true };
        internal readonly Dictionary<string, JObject> VersionMap = new(StringComparer.Ordinal);
        internal readonly List<string> Requests = new();
        internal JObject ParentData, LibraryData, BaseData;
        internal JObject? FallbackData;
        internal bool BadLoader, PinFixture, CurseForge;
        internal LaunchFixture(string root)
        {
            Root = root; Mods = Path.Combine(root, "mods"); Source = Path.Combine(Mods, "consumer.jar");
            Library = Path.Combine(root, "packages/library.jar"); Base = Path.Combine(root, "packages/base.jar");
            LaunchFabric(Source, "consumer", new JObject { ["lib_core"] = ">=1.0.0" });
            LaunchFabric(Library, "lib_core", new JObject { ["base_core"] = ">=1.0.0" }); LaunchFabric(Base, "base_core");
            Context.LoaderVersions.Add("fabricloader", "0.16.10");
            ParentData = Version(Source, "parent01", "parentv1", new JArray(Required("libproj1"), new JObject { ["project_id"] = "optional", ["dependency_type"] = "optional" }));
            LibraryData = Version(Library, "libproj1", "libver01", new JArray(Required("basepr01")));
            BaseData = Version(Base, "basepr01", "basevr01");
            foreach (JObject data in new[] { ParentData, LibraryData, BaseData }) VersionMap[(string)data["files"]![0]!["hashes"]!["sha1"]!] = data;
        }
        internal TReport Scan() => DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>.Scan(Mods, Context);
        internal TOptions Options() => new() { Request = Request, Fingerprint = _ => 123u, Cache = new TCache() };
        internal object Plan() => LaunchPlan(Mods, Scan(), Options());
        internal static JObject Required(string project, string? version = null) => new() { ["project_id"] = project, ["version_id"] = version, ["dependency_type"] = "required" };
        internal static JObject Version(string path, string project, string version, JArray? dependencies = null) => new()
        {
            ["id"] = version, ["project_id"] = project, ["name"] = "1.0.0", ["version_number"] = "1.0.0", ["date_published"] = "2026-11-01T00:00:00Z",
            ["version_type"] = "release", ["downloads"] = 10, ["game_versions"] = new JArray("1.21.1"), ["loaders"] = new JArray("fabric"),
            ["dependencies"] = dependencies ?? new JArray(),
            ["files"] = new JArray(new JObject { ["primary"] = true, ["filename"] = project + ".jar", ["url"] = "https://cdn.modrinth.com/data/" + project + ".jar", ["size"] = new FileInfo(path).Length, ["hashes"] = new JObject { ["sha1"] = Hash(path) } })
        };
        private static JObject CfData(string path, int project, int id, params int[] required) => new()
        {
            ["gameId"] = 432, ["modId"] = project, ["id"] = id, ["displayName"] = "1.0.0", ["fileDate"] = "2026-11-01T00:00:00Z", ["releaseType"] = 1, ["downloadCount"] = 10,
            ["fileName"] = project + ".jar", ["fileLength"] = new FileInfo(path).Length, ["fileFingerprint"] = 123, ["isAvailable"] = true,
            ["downloadUrl"] = "https://edge.forgecdn.net/files/2000/" + id + "/" + project + ".jar", ["gameVersions"] = new JArray("1.21.1", "Fabric"),
            ["hashes"] = new JArray(new JObject { ["algo"] = 1, ["value"] = Hash(path) }),
            ["dependencies"] = new JArray(required.Select(value => new JObject { ["modId"] = value, ["relationType"] = 3 }))
        };
        private JToken Request(string url, HttpMethod method, string? body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(url);
            if (CurseForge)
            {
                if (url.Contains("modrinth.com", StringComparison.Ordinal)) return new JObject();
                if (url.Contains("/fingerprints/", StringComparison.Ordinal)) return new JObject { ["data"] = new JObject { ["exactMatches"] = new JArray(new JObject { ["id"] = 1001, ["file"] = CfData(Source, 1001, 2000001, 1002) }) } };
                JArray projects = new(Enumerable.Range(1001, 3).Select(id => new JObject { ["id"] = id, ["gameId"] = 432, ["classId"] = 6, ["name"] = id == 1001 ? "Consumer" : id == 1002 ? "Core library" : "Base library" }));
                if (url == "https://api.curseforge.com/v1/mods") return new JObject { ["data"] = projects };
                Match match = Regex.Match(url, "/mods/(1002|1003)(/files\\?|$)");
                if (match.Success)
                {
                    int id = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return new JObject { ["data"] = url.Contains("/files?", StringComparison.Ordinal) ? new JArray(id == 1002 ? CfData(Library, 1002, 2000002, 1003) : CfData(Base, 1003, 2000003)) : projects.OfType<JObject>().Single(item => (int)item["id"]! == id) };
                }
                throw new InvalidOperationException("Original launch:113 unexpected CurseForge fixture URL " + url);
            }
            if (url.Contains("curseforge.com", StringComparison.Ordinal)) return new JObject { ["data"] = new JObject { ["exactMatches"] = new JArray() } };
            if (url.Contains("/version_files", StringComparison.Ordinal))
            {
                JObject response = new();
                foreach (string? value in JObject.Parse(body!)["hashes"]!.Values<string>())
                {
                    string hash = value ?? throw new InvalidDataException("Fixture received a non-string hash.");
                    if (VersionMap.TryGetValue(hash, out JObject? data)) response[hash] = data.DeepClone();
                }
                return response;
            }
            if (url.Contains("/projects?", StringComparison.Ordinal)) return new JArray(new[] { ("parent01", "Consumer"), ("pinned01", "Pinned consumer"), ("libproj1", "Core library"), ("basepr01", "Base library") }.Select(pair => new JObject { ["id"] = pair.Item1, ["project_type"] = "mod", ["title"] = pair.Item2 }));
            if (url.EndsWith("/version/libver01", StringComparison.Ordinal)) return LibraryData.DeepClone();
            Match project = Regex.Match(url, "/project/(libproj1|basepr01)(/version\\?|$)");
            if (project.Success)
            {
                string id = project.Groups[1].Value;
                if (!url.Contains("/version?", StringComparison.Ordinal)) return new JObject { ["id"] = id, ["project_type"] = "mod", ["title"] = id == "libproj1" ? "Core library" : "Base library" };
                JObject data = (JObject)(id == "libproj1" ? LibraryData : BaseData).DeepClone();
                if (BadLoader) data["loaders"] = new JArray("forge");
                if (FallbackData is not null && id == "libproj1") return new JArray(FallbackData.DeepClone(), data);
                if (PinFixture && id == "libproj1") { JObject newer = (JObject)data.DeepClone(); newer["id"] = "libver02"; newer["date_published"] = "2026-11-02T00:00:00Z"; return new JArray(newer, data); }
                return new JArray(data);
            }
            throw new InvalidOperationException("Original launch:79 unexpected Modrinth fixture URL " + url);
        }
    }
}
