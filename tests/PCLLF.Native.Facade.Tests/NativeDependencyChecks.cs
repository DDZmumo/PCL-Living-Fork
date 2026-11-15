using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using Transport = ModDependencyOnlineFixtureNetwork;

namespace PCLLF.Native.Facade.Tests;
// Real Main's internal DTOs close this bounded fixture through their actual Core base classes.
// All algorithms are invoked through the original Main facade; no substitute DTOs/engines.
internal static class NativeDependencyChecks
{
    internal static void Local(FixtureEnvironment environment) { Invoke("Local", environment); NativeModDownloadChecks.Run(environment); }
    internal static void Online(FixtureEnvironment environment) => Invoke("Online", environment);
    internal static int IndexChild(FixtureEnvironment environment, string[] args) => (int)Invoke("IndexChild", environment, args)!;
    private static object? Invoke(string method, params object[] args)
    {
        string prefix = "PCL.ModDependencyInspector+";
        string[] names = ["InspectionContext", "ModInfo", "Dependency", "DependencyEdge", "InspectionIssue", "RemovalImpact", "InspectionReport", "OnlineProjectInfo", "OnlineDependency", "OnlineFileInfo"];
        Type[] types = names.Select(name => Native.Type(prefix + name)).Concat(new[] { Native.Type("PCL.ModDependencyOnline+LookupOptions"), Native.Type("PCL.ModDependencyOnline+LookupCache") }).ToArray();
        foreach (var type in types)
            Native.Check(type.Assembly == Native.Main, "Dependency fixture does not use the genuine Main DTO: " + type);
        Type closed = typeof(DependencyCases<,,,,,,,,,,, >).MakeGenericType(types);
        return Native.Call(closed, method, null, args);
    }
}

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
    where TContext : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ContextData, new()
    where TMod : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ModData, new()
    where TDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DependencyData, new()
    where TEdge : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EdgeData, new()
    where TIssue : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IssueData, new()
    where TImpact : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ImpactData, new()
    where TReport : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ReportData, new()
    where TOnlineProject : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineProjectData, new()
    where TOnlineDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineDependencyData, new()
    where TOnlineFile : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineFileData, new()
    where TOptions : ModDependencyLookupOptions<TCache>, new()
    where TCache : ModDependencyLookupCache, new()
{
    private static readonly Type Inspector = Native.Type("PCL.ModDependencyInspector"), Index = Native.Type("PCL.ModDependencyIndex"), OnlineApi = Native.Type("PCL.ModDependencyOnline");
    private static readonly HashSet<int> LocalSeen = new(), OnlineSeen = new();
    private static int localCount, onlineCount;
    private static void L(int line, bool condition, string? detail = null)
    {
        localCount++;
        LocalSeen.Add(line);
        Native.Check(condition, "LF50 Test-ModDependencies.ps1:" + line + " " + NativeDependencyOriginalAssertions.Local[line] + " " + detail);
    }

    private static void O(int line, bool condition, string? detail = null)
    {
        onlineCount++;
        OnlineSeen.Add(line);
        Native.Check(condition, "LF50 Test-ModDependencyOnline.ps1:" + line + " " + NativeDependencyOriginalAssertions.Online[line] + " " + detail);
    }

    private static TContext Context(string loader = "fabricloader", string version = "0.16.0")
    {
        var context = new TContext
        {
            MinecraftVersion = loader == "neoforge" ? "1.21.1" : "1.20.1",
            JavaVersion = "17.0.9",
            LoaderKnown = true
        };
        if (loader != "")
            context.LoaderVersions.Add(loader, version);
        return context;
    }

    private static TReport Scan(string folder, TContext? context = null, CancellationToken token = default) => (TReport)Native.Call(Inspector, "Inspect", null, folder, context ?? Context(), token)!;
    private static TIssue[] Issues(TReport report, string kind, string owner = "") => report.Issues.Where(issue => Native.Get(issue, "Kind")!.ToString() == kind && (owner == "" || issue.Owner.Id == owner)).ToArray();
    private static List<TImpact> Impacts(TReport report, params string[] paths) => (List<TImpact>)Native.Call(Inspector, "GetImpacts", null, report, paths)!;
    private static List<TMod> Embedded(TReport report, string path) => (List<TMod>)Native.Call(Inspector, "EmbeddedModsForDisplay", null, report, path)!;
    private static string DescribeImpacts(TReport report, params string[] paths) => (string)Native.Call(Inspector, "DescribeImpacts", null, report, paths)!;
    private static string Extended(string path) => @"\\?\" + Path.GetFullPath(path);
    private static void ClearIndex() => Native.Call(Index, "ClearMemory", null);
    private static TReport Rebuild(string folder, TContext context) => (TReport)Native.Call(Inspector, "RebuildIndex", null, folder, context, CancellationToken.None)!;
    private static string Warning(string folder, string file, TContext? context = null) => (string)Native.Call(Inspector, "LoaderWarningFor", null, folder, file, context ?? Context())!;
    private static string Hash(string file, string algorithm = "SHA1") => Convert.ToHexString(algorithm == "MD5" ? MD5.HashData(File.ReadAllBytes(file)) : algorithm == "SHA256" ? SHA256.HashData(File.ReadAllBytes(file)) : SHA1.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static JObject Obj(string text) => JObject.Parse(text);
    private static string Json(object value) => JsonConvert.SerializeObject(value);
    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
    private static string Jar(string folder, string file, IDictionary<string, object> entries, CompressionLevel compression = CompressionLevel.Optimal)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, file);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in entries)
        {
            using var stream = archive.CreateEntry(pair.Key, compression).Open();
            byte[] bytes = pair.Value is byte[] data ? data : Bytes((string)pair.Value);
            stream.Write(bytes);
        }

        return path;
    }

    private static Dictionary<string, object> Entries(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    private static string Fabric(string folder, string file, string id, string version = "1.0", JObject? extra = null)
    {
        JObject data = new()
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = id,
            ["version"] = version
        };
        if (extra != null)
            foreach (var p in extra.Properties())
                data[p.Name] = p.Value.DeepClone();
        return Jar(folder, file, Entries(("fabric.mod.json", data.ToString(Formatting.None))));
    }

    private static string Quilt(string folder, string file, string id, JObject? extra = null, JObject? rootExtra = null)
    {
        JObject loader = new()
        {
            ["group"] = "fixture",
            ["id"] = id,
            ["version"] = "1.0",
            ["metadata"] = new JObject
            {
                ["name"] = id
            }
        };
        if (extra != null)
            foreach (var p in extra.Properties())
                loader[p.Name] = p.Value.DeepClone();
        JObject data = new()
        {
            ["schema_version"] = 1,
            ["quilt_loader"] = loader
        };
        if (rootExtra != null)
            foreach (var p in rootExtra.Properties())
                data[p.Name] = p.Value.DeepClone();
        return Jar(folder, file, Entries(("quilt.mod.json", data.ToString(Formatting.None))));
    }

    private static byte[] LegacyClass(params (string Key, string Value)[] values)
    {
        List<string> pool = ["Lnet/minecraftforge/fml/common/Mod;", "RuntimeVisibleAnnotations", "fixture/Mod", "java/lang/Object"];
        foreach (var pair in values)
        {
            pool.Add(pair.Key);
            pool.Add(pair.Value);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void U2(int v)
        {
            writer.Write((byte)((v >> 8) & 255));
            writer.Write((byte)(v & 255));
        }

        void U4(long v)
        {
            U2((int)((v >> 16) & 65535));
            U2((int)(v & 65535));
        }

        U4(3405691582);
        U2(0);
        U2(52);
        U2(pool.Count + 3);
        foreach (string text in pool)
        {
            byte[] bytes = Bytes(text);
            writer.Write((byte)1);
            U2(bytes.Length);
            writer.Write(bytes);
        }

        writer.Write((byte)7);
        U2(3);
        writer.Write((byte)7);
        U2(4);
        U2(33);
        U2(pool.Count + 1);
        U2(pool.Count + 2);
        U2(0);
        U2(0);
        U2(0);
        U2(1);
        U2(2);
        U4(6 + 5 * values.Length);
        U2(1);
        U2(1);
        U2(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            U2(5 + i * 2);
            writer.Write((byte)'s');
            U2(6 + i * 2);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static TOptions Options(Transport network, TCache? cache = null) => new()
    {
        Request = network.Request,
        Clock = network.Clock,
        Cache = cache ?? new TCache(),
        MaxSearchFiles = 0
    };
    private static TReport Enrich(TReport report, string folder, TOptions options, CancellationToken token = default) => (TReport)Native.Call(OnlineApi, "Enrich", null, report, folder, token, options)!;
    private static TOnlineFile Info(TReport report, string path) => report.OnlineFiles.First(file => string.Equals(file.FullPath, path, StringComparison.OrdinalIgnoreCase));
    private static JObject MrVersion(string path, string project, string version, JArray? dependencies = null) => new()
    {
        ["id"] = version,
        ["project_id"] = project,
        ["version_number"] = "1.0",
        ["dependencies"] = dependencies ?? new JArray(),
        ["files"] = new JArray(new JObject { ["hashes"] = new JObject { ["sha1"] = Hash(path) }, ["size"] = new FileInfo(path).Length })
    };
    private static JObject CfFile(string path, int project, int version, JArray? dependencies = null) => new()
    {
        ["id"] = version,
        ["modId"] = project,
        ["gameId"] = 432,
        ["fileLength"] = new FileInfo(path).Length,
        ["fileFingerprint"] = Transport.Fingerprint(path),
        ["displayName"] = "CF version",
        ["dependencies"] = dependencies ?? new JArray(),
        ["hashes"] = new JArray(new JObject { ["algo"] = 1, ["value"] = Hash(path) })
    };
    private static string OnlineJar(string folder, string name, string id = "") => Jar(folder, name, Entries((id == "" ? "fixture.txt" : "fabric.mod.json", id == "" ? "unrecognised fixture " + name : Json(new { schemaVersion = 1, id, version = "1.0" }))));
    private sealed class Scope : IDisposable
    {
        internal readonly string Root;
        internal string Folder(string name)
        {
            string result = Path.Combine(Root, name);
            Directory.CreateDirectory(result);
            return result;
        }

        private readonly Stack<Action> restore = new();
        private int forbidden;
        internal Scope(FixtureEnvironment environment, string name)
        {
            Root = Path.Combine(environment.Root, name);
            Directory.CreateDirectory(Root);
            try
            {
                var cache = Native.Property(Index, "CacheDirectory");
                object? previousCache = cache.GetValue(null);
                restore.Push(() => cache.SetValue(null, previousCache));
                cache.SetValue(null, Path.Combine(Root, "dependency-index"));
                restore.Push(ClearIndex);
                ClearIndex();
                object pipeline = Native.Field(OnlineApi, "Pipeline").GetValue(null)!;
                var requestField = Native.Field(pipeline.GetType(), "defaultRequest");
                object? previousRequest = requestField.GetValue(pipeline);
                Native.Check(previousRequest is Delegate, "The genuine default transport primitive is unavailable.");
                restore.Push(() => requestField.SetValue(pipeline, previousRequest));
                requestField.SetValue(pipeline, new Func<string, System.Net.Http.HttpMethod, string?, CancellationToken, JToken?>((_, _, _, _) =>
                {
                    Interlocked.Increment(ref forbidden);
                    throw new InvalidOperationException("Live dependency HTTP forbidden in actual Main fixture.");
                }));
            }
            catch
            {
                while (restore.Count > 0)
                    restore.Pop()();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                Native.Check(Volatile.Read(ref forbidden) == 0, "Production attempted default HTTP even if it caught the fixture guard.");
            }
            finally
            {
                while (restore.Count > 0)
                    restore.Pop()();
            }
        }
    }
}
