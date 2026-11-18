using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Original LF facade checks; repository LICENCE. Explicit installed Java paths, no install/catalog/account writes.
internal static class MmcLaunchJavaChecks {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC launch Java prefers the published exact environment and freshly checks stale catalog metadata", ExactView);
        yield return ("MMC launch Java keeps forced new environments explicit and preserves package/account/range constraints", Preferences);
        yield return ("MMC launch Java clean probes ignore inherited option injection without changing process environment", CleanEnvironment);
    }
    private static string Java8 => Tool("PCLLF_MMC_TEST_JAVA8");
    private static string Java21 => Tool("PCLLF_MMC_TEST_JAVA21");
    private static string Tool(string name) { string? path = Environment.GetEnvironmentVariable(name); if (path is null || !File.Exists(path)) throw new InvalidOperationException("Required real Java test executable is missing: " + name); return path; }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Fixture : IDisposable {
        internal string Root = Path.Combine(Path.GetTempPath(), "PCLLF-MmcJavaSelection", Guid.NewGuid().ToString("N"));
        internal ModMinecraft.McInstance Instance;
        internal MmcResolvedProfile Sources;
        internal MmcInstanceDefinition Definition;
        internal Fixture() {
            string versionRoot = Path.Combine(Root, "versions", "fixture"); Directory.CreateDirectory(Path.Combine(versionRoot, "minecraft"));
            Instance = new(versionRoot);
            const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
            const string patch = "{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\",\"mainClass\":\"fixture.Main\",\"mainJar\":{\"name\":\"fixture:minecraft:1.7.10:client\"},\"libraries\":[{\"name\":\"fixture:bootstrap:1\"}],\"compatibleJavaMajors\":[8,21]}";
            var parsed = MmcPackageReader.Parse(new([new("mmc-pack.json"), new("patches/net.minecraft.json")], new Dictionary<string, string> { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = patch }));
            var resolved = MmcComponentResolver.Resolve(parsed, []); Require(resolved.IsComplete, "Source fixture resolution failed."); Sources = resolved.Profile!;
            var compiled = MmcProfileCompiler.Compile(Sources, new("windows", "x86_64", 8)); Require(!compiled.HasErrors, "Source fixture compilation failed.");
            string bundle = new('b', 64), prefix = "PCL/MmcSource/" + bundle + "/";
            Definition = new(1, "Mmc", "prism-v1", Guid.NewGuid(), 1, new(new string('a', 64), "fixture.zip"),
                new("", bundle, Sources.SourceFingerprint, [new("mmc-pack.json", Hash(manifest), prefix + "mmc-pack.json", MmcSourceRecordKind.Manifest),
                    new("patches/net.minecraft.json", Hash(patch), prefix + "patches/net.minecraft.json", MmcSourceRecordKind.Component, "net.minecraft", "1.7.10", MmcComponentSourceKind.PackageLocal)]),
                new("minecraft"), [], [], [], new(compiled.Profile!.ProfileFingerprint, Hash("projection"), 1, compiled.Profile.RuntimePlanFingerprint, new string('0', 64)));
            Definition = Definition with { InstalledProjection = Definition.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(Definition) } };
        }
        public void Dispose() {
            string root = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCLLF-MmcJavaSelection"));
            Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(root), "N", out _), "Java fixture cleanup escaped owned data.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static Java Catalog(string executable, int cachedMajor) => new(Path.GetDirectoryName(executable)!, new Version(cachedMajor, 0));
    private static MmcLaunchJavaSelection Select(Fixture fixture, MmcLaunchJavaPreference preference, IReadOnlyList<Java> candidates, ImmutableArray<MmcJavaConstraint> account = default) =>
        MmcLaunchJava.SelectFromCandidatesAsync(fixture.Instance, fixture.Definition, fixture.Sources, preference, candidates, CancellationToken.None, account).GetAwaiter().GetResult();
    private static void ExactView() {
        using Fixture fixture = new();
        Java stale21 = Catalog(Java21, 8), stale8 = Catalog(Java8, 21);
        var chosen = Select(fixture, new(MmcLaunchPreferenceMode.InheritGlobal, null, false), [stale21, stale8]);
        Require(chosen.State == MmcLaunchJavaState.CurrentView && chosen.Java.Version!.Major == 8 && chosen.RuntimeJava.Major == 8 && chosen.RuntimeJava.Architecture == "x86_64" && chosen.RuntimeJava.Bitness == 64,
            "Published environment/fresh PE-probe identity was replaced by cached metadata.");
        Require(stale21.Version!.Major == 8 && stale8.Version!.Major == 21 && !ReferenceEquals(chosen.Java, stale8), "Read-only catalogue objects were mutated instead of using fresh checks.");
        Require(chosen.RuntimeJava.ExecutableSha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Java8))) && chosen.RuntimeJava.IdentitySha256.Length == 64, "Actual executable identity was not bound.");
        var repeated = Select(fixture, new(MmcLaunchPreferenceMode.UsePackage, Java21, false), [stale21, stale8]);
        Require(repeated.State == MmcLaunchJavaState.CurrentView && repeated.Java.Version!.Major == 8, "Package JavaPath recommendation displaced an existing adopted view.");
    }
    private static void Preferences() {
        using Fixture fixture = new();
        var forced = Select(fixture, new(MmcLaunchPreferenceMode.Custom, Java21, true), [Catalog(Java8, 8)]);
        Require(forced.RequiresVariantAdoption && forced.RuntimeJava.Major == 21 && forced.Environment.JavaMajor == 21 && forced.Diagnostics.Any(d => d.Code == "MMC_JAVA_VARIANT_REQUIRED"),
            "A legal forced new Java environment was silently bound to the old view or pinned to MC1.7.10 Java8.");
        Expect("MMC_JAVA_PREFERENCE_INCOMPATIBLE", () => Select(fixture, new(MmcLaunchPreferenceMode.Custom, Java21, true), [], [new("account", MaximumMajor: 8)]));
        var range = Select(fixture, new(MmcLaunchPreferenceMode.Custom, null, false) { VersionRange = "[21,22)" }, [Catalog(Java8, 8), Catalog(Java21, 8)]);
        Require(range.RuntimeJava.Major == 21 && range.RequiresVariantAdoption, "Explicit full-version range did not filter actual candidate metadata.");
        Expect("MMC_JAVA_NO_COMPATIBLE", () => Select(fixture, new(MmcLaunchPreferenceMode.Custom, null, false) { InstanceFolderOnly = true }, [Catalog(Java8, 8)]));
    }
    private static void CleanEnvironment() {
        using Fixture fixture = new(); string[] keys = ["JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH"];
        var previous = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try {
            Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-javaagent:nonexistent-test-injection.jar");
            Environment.SetEnvironmentVariable("JDK_JAVA_OPTIONS", "@nonexistent-test-injection.args");
            Environment.SetEnvironmentVariable("_JAVA_OPTIONS", "-XX:DefinitelyInvalidTestOption");
            Environment.SetEnvironmentVariable("CLASSPATH", "nonexistent-test-injection-path");
            var chosen = Select(fixture, new(MmcLaunchPreferenceMode.Custom, Java8, true), []);
            Require(chosen.State == MmcLaunchJavaState.CurrentView && chosen.Java.Version!.Major == 8 && Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS") == "-javaagent:nonexistent-test-injection.jar",
                "Clean probing either inherited hidden execution options or modified the parent environment.");
        } finally { foreach (string key in keys) Environment.SetEnvironmentVariable(key, previous[key]); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action) { try { action(); } catch (MmcLaunchJavaException error) when (error.Code == code) { return; } throw new Exception("Expected Java selection refusal " + code); }
}
