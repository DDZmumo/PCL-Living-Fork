// Original LF evidence adapter; governed by the repository LICENCE.
// It calls the actual Core and never implements Prism's parsing or merge rules.
using System.Security.Cryptography;
using System.Text.Json;
using PCLLF.MMCC.Core;

if (args.Length != 1) { Console.Error.WriteLine("Usage: PCLLF.Mmc.Prism.Differential CASE.json"); return 2; }
using var document = JsonDocument.Parse(File.ReadAllBytes(args[0]));
var input = document.RootElement;
var metadata = input.GetProperty("metadata").EnumerateObject().ToDictionary(p => p.Name, p => File.ReadAllText(p.Value.GetString()!));
var entries = input.GetProperty("archiveEntries").EnumerateArray().Select(e => new MmcArchiveEntry(
    e.GetProperty("path").GetString()!, e.GetProperty("size").GetInt64(), e.GetProperty("directory").GetBoolean())).ToArray();
var snapshots = input.GetProperty("remoteMetadata").EnumerateArray().Select(e => new MmcMetadataSnapshot(
    e.GetProperty("uid").GetString()!, e.GetProperty("version").GetString()!, e.GetProperty("source").GetString()!,
    File.ReadAllText(e.GetProperty("path").GetString()!), e.GetProperty("sha256").GetString())).ToArray();
var parsed = MmcPackageReader.Parse(new(entries, metadata));
var resolved = MmcComponentResolver.Resolve(parsed, snapshots);
if (!resolved.IsComplete) {
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "PCLLFCoreReferenceAdapter", stage = "resolution", success = false, diagnostics = resolved.Diagnostics }));
    return 3;
}
var runtime = input.GetProperty("runtime");
var resolvedProfile = resolved.Profile!;
var result = MmcProfileCompiler.Compile(resolvedProfile, new(runtime.GetProperty("system").GetString()!,
    runtime.GetProperty("javaRealArchitecture").GetString()!, runtime.GetProperty("javaMajor").GetInt32(), int.Parse(runtime.GetProperty("javaArchitecture").GetString()!)));
if (result.Profile is not { } profile || result.HasErrors) {
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "PCLLFCoreReferenceAdapter", stage = "compilation", success = false, diagnostics = result.Diagnostics }));
    return 4;
}
object Artifact(MmcArtifactReference a) => new {
    name = a.Coordinate?.Name, local = a.Scope == MmcArtifactScope.InstanceLocal,
    path = (a.Scope == MmcArtifactScope.InstanceLocal ? "instance/" : "global/") + a.RelativePath.Replace('\\', '/'),
    role = a.Role.ToString(), classifier = a.NativeClassifier, excludes = a.ExtractExcludes,
    urls = a.DownloadUrls, sha1 = a.Sha1, sourceSha256 = a.SourceRevision
};
var core = typeof(MmcProfileCompiler).Assembly.Location;
var output = new {
    kind = "PCLLFCoreReferenceAdapter", success = true,
    coreSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(core))),
    components = resolvedProfile.Components.Select(c => new { uid = c.Uid, version = c.Version, enabled = c.Enabled, sourceSha256 = c.Patch.ContentSha256 }),
    mainClass = profile.MainClass, mainJar = Artifact(profile.MainJar), minecraftVersion = profile.MinecraftVersion,
    gameArguments = profile.GameArguments.Select(t => t.Value), jvmArguments = profile.JvmArguments.Select(t => t.Value),
    tweakers = profile.Tweakers, compatibleJavaMajors = profile.JavaPolicy.AllowedMajors,
    compatibleJavaName = profile.JavaPolicy.RecommendedRuntimeName, traits = profile.Traits.Order(StringComparer.Ordinal),
    classpath = profile.Classpath.Select(Artifact), nativeJars = profile.NativeArtifacts.Select(Artifact),
    mavenFiles = profile.DownloadOnlyArtifacts.Select(Artifact), diagnostics = result.Diagnostics,
    profileFingerprint = profile.ProfileFingerprint, runtimeFingerprint = profile.RuntimePlanFingerprint,
    scope = "Actual Core parse/resolve/compile; no import, materialization, host binding, Java selection process, credentials or game."
};
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
return 0;
