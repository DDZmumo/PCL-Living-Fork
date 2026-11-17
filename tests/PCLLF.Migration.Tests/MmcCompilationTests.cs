using System.Collections.Immutable;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLLF.MMCC.Core;

internal static class MmcCompilationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC compiler applies final nonempty entry and main JAR after ordinary libraries", Entrypoint);
        yield return ("MMC compiler preserves JVM array tokens and moves repeated tweaker groups", Arguments);
        yield return ("MMC native legacy game arguments split literal spaces before macro expansion with quotes slashes and tabs retained", LegacyGameArguments);
        yield return ("MMC compiler only accepts Minecraft game assets and retains provenance", AssetOwnership);
        yield return ("MMC compiler replaces higher library versions in the original slot", LibraryVersions);
        yield return ("MMC compiler keeps mavenFiles download-only with duplicates", MavenFiles);
        yield return ("MMC compiler uses runtime Java classifier and ordered platform rules", Rules);
        yield return ("MMC compiler resolves native classifier bitness and extraction inputs", Natives);
        yield return ("MMC compiler keeps local flat names separate from shared Maven sources", LocalArtifacts);
        yield return ("MMC compiler rejects unsafe artifact paths URLs digests and conflicting bytes", ArtifactRejections);
        yield return ("MMC compiler keeps Java candidates discrete and distinguishes missing installed Java", JavaPolicy);
        yield return ("MMC compiler models logging configuration as an owned auxiliary artifact", Logging);
        yield return ("MMC compiler tracks local launch override effects and deterministic fingerprints", Provenance);
        yield return ("MMC compiler only flags a complete builtin replacement with final adopted launch contributions", CompleteOverrides);
        yield return ("MMC command string tokenizer preserves empty arguments quotes and trailing backslashes", Tokenization);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dictionary<string, object?> Library(string name, string? hint = null) {
        var value = new Dictionary<string, object?> { ["name"] = name };
        if (hint is not null) value["MMC-hint"] = hint;
        return value;
    }
    private static Dictionary<string, object?> Patch(string uid, params (string Key, object? Value)[] fields) {
        var value = new Dictionary<string, object?> { ["uid"] = uid, ["version"] = "1" };
        foreach (var field in fields) value[field.Key] = field.Value;
        return value;
    }
    private static MmcCompilationResult Compile(Dictionary<string, object?>? baseChanges = null,
        Dictionary<string, object?>[]? patches = null, MmcRuntimeEnvironment? environment = null, MmcJavaConstraint[]? constraints = null) {
        var foundation = Patch("net.minecraft", ("mainClass", "fixture.Main"), ("type", "release"),
            ("mainJar", Library("fixture:minecraft:1:client")), ("assetIndex", new { id = "fixture", url = "https://example.invalid/assets.json" }));
        if (baseChanges is not null) foreach (var change in baseChanges) foundation[change.Key] = change.Value;
        var all = new[] { foundation }.Concat(patches ?? []).ToArray();
        Dictionary<string, string> content = new() { ["mmc-pack.json"] = JsonSerializer.Serialize(new {
            formatVersion = 1, components = all.Select(p => new { uid = p["uid"], version = "1" }).ToArray()
        }) };
        foreach (var patch in all) content["patches/" + patch["uid"] + ".json"] = JsonSerializer.Serialize(patch);
        var parsed = MmcPackageReader.Parse(new(content.Select(p => new MmcArchiveEntry(p.Key)).ToArray(), content));
        var resolved = MmcComponentResolver.Resolve(parsed, []);
        if (!resolved.IsComplete) return new(null, resolved.Diagnostics);
        return MmcProfileCompiler.Compile(resolved.Profile!, environment ?? new("windows", "amd64"), constraints);
    }
    private static MmcCompiledProfile Good(MmcCompilationResult result) {
        Require(!result.HasErrors && result.Profile is not null, string.Join(" | ", result.Diagnostics.Where(d => d.Severity == MmcDiagnosticSeverity.Error).Select(d => d.Code + " " + d.Message)));
        return result.Profile!;
    }
    private static void Error(MmcCompilationResult result, string code) => Require(result.HasErrors && result.Diagnostics.Any(d => d.Code == code), "Expected " + code + ": " + string.Join(",", result.Diagnostics.Select(d => d.Code)));
    private static void Entrypoint() {
        var profile = Good(Compile(patches: [Patch("bootstrap", ("mainClass", "fixture.Boot"), ("libraries", new[] { Library("fixture:early:1") })), Patch("final", ("mainClass", ""), ("mainJar", null))]));
        Require(profile.MainClass == "fixture.Boot" && profile.Classpath[^1].Role == MmcArtifactRole.MainJar && profile.Classpath[0].Coordinate!.Artifact == "early", "Entry/mainJar ordering changed.");
        Error(Compile(new() { ["mainClass"] = "", ["mainJar"] = null }), "MMC_INCOMPLETE_LAUNCH");
    }
    private static void Arguments() {
        var profile = Good(Compile(new() { ["minecraftArguments"] = "--arg \"\" --path '中文 path'", ["+jvmArgs"] = new[] { "-Dpath=中文 space", "", "-Dsame=1", "-Dsame=1" }, ["+tweakers"] = new[] { "A", "B" } },
            [Patch("second", ("+jvmArgs", new[] { "--add-opens", "java.base/java.lang=ALL-UNNAMED" }), ("+tweakers", new[] { "A", "A" }))]));
        Require(profile.JvmArguments.Select(a => a.Value).SequenceEqual(["-Dpath=中文 space", "", "-Dsame=1", "-Dsame=1", "--add-opens", "java.base/java.lang=ALL-UNNAMED"]), "Array tokens were split or deduplicated.");
        Require(profile.Tweakers.SequenceEqual(["B", "A", "A"]) && profile.GameArguments.Select(value => value.Value).SequenceEqual(
            ["--arg", "\"\"", "--path", "'中文", "path'", "--tweakClass", "B", "--tweakClass", "A", "--tweakClass", "A"]), "Native legacy game tokens or tweaker move changed.");
        Error(Compile(new() { ["+jvmArgs"] = new[] { "bad\0argument" } }), "MMC_ARGUMENTS");
    }
    private static void LegacyGameArguments() {
        // Fixed Prism processMinecraftArgs splits ASCII ' ' before replacing macros.
        // This expected vector is native data, independently of the cfg/JVM tokenizer.
        const string raw = "  --quote \"two words\" --single 'two words' --slash C:\\tail\\ --tab left\tright --newline left\nright --empty ${client_token} --literal \"\" --open \"unterminated   ";
        string[] expected = ["--quote", "\"two", "words\"", "--single", "'two", "words'", "--slash", "C:\\tail\\", "--tab", "left\tright", "--newline", "left\nright", "--empty", "${client_token}", "--literal", "\"\"", "--open", "\"unterminated"];
        var compiled = Good(Compile(new() { ["minecraftArguments"] = raw }));
        Require(compiled.GameArguments.Select(value => value.Value).SequenceEqual(expected), "Native game splitting interpreted shell quotes, escaped a slash, split non-ASCII-space whitespace or removed a macro token.");
        Require(Good(Compile(new() { ["minecraftArguments"] = "    " })).GameArguments.IsEmpty &&
            Good(Compile(new() { ["minecraftArguments"] = "" })).GameArguments.IsEmpty, "Empty/space-only legacy game input created synthetic tokens.");
        Error(Compile(new() { ["minecraftArguments"] = "--bad invalid\0token" }), "MMC_ARGUMENTS");
        Require(MmcArgumentTokenizer.Tokenize("-Dvalue=\"two words\" '' --tab\tvalue").SequenceEqual(["-Dvalue=two words", "", "--tab", "value"]),
            "Fixing native game arguments changed cfg/JVM quote and whitespace tokenization.");
    }
    private static void AssetOwnership() {
        var result = Compile(patches: [Patch("custom", ("assetIndex", new { id = "wrong", url = "https://example.invalid/wrong" }), ("type", "wrong"))]);
        var profile = Good(result);
        Require(profile.Assets.Id == "fixture" && profile.MinecraftVersionType == "release" && result.Diagnostics.Any(d => d.Code == "MMC_IGNORED_GAME_METADATA"), "Foreign patch replaced game assets/type.");
    }
    private static void LibraryVersions() {
        var profile = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:a:1.2"), Library("fixture:b:1"), Library("fixture:c:1.0") } },
            [Patch("second", ("libraries", new[] { Library("fixture:a:1.10"), Library("fixture:c:1.0-alpha"), Library("fixture:a:1.1") }), ("+libraries", new[] { Library("fixture:a:1.10:other") }))]));
        Require(profile.Classpath.Take(4).Select(a => a.Coordinate!.Name).SequenceEqual(["fixture:a:1.10", "fixture:b:1", "fixture:c:1.0", "fixture:a:1.10:other"]), "Version comparison replaced/moved the wrong slot.");
    }
    private static void MavenFiles() {
        var profile = Good(Compile(new() { ["mavenFiles"] = new[] { Library("fixture:tool:1"), Library("fixture:tool:1"), Library("fixture:tool:2") } }));
        Require(profile.DownloadOnlyArtifacts.Length == 3 && profile.Classpath.Length == 1, "mavenFiles were deduplicated or added to classpath.");
    }
    private static void Rules() {
        var wide = Library("fixture:wide:1"); wide["rules"] = new[] { new { action = "allow", os = new { name = "windows-x86_64", arch = "x86", version = "[" } } };
        var late = Library("fixture:late:1"); late["rules"] = new object[] { new { action = "disallow" }, new { action = "allow", os = new { name = "windows" } } };
        var result = Compile(new() { ["libraries"] = new[] { wide, late } });
        Require(Good(result).Classpath.Length == 3 && result.Diagnostics.Any(d => d.Code == "MMC_RULE_IGNORED_OS_ARCH") && result.Diagnostics.Any(d => d.Code == "MMC_RULE_IGNORED_OS_VERSION"), "Ignored R2 conditions silently changed matching.");
        var narrow = Good(Compile(new() { ["libraries"] = new[] { wide, late } }, environment: new("windows", "i386", JavaBitness: 32)));
        Require(narrow.Classpath.Length == 2 && narrow.Classpath[0].Coordinate!.Artifact == "late", "Real Java classifier ignored in rules.");
    }
    private static Dictionary<string, object?> Native() {
        var library = Library("fixture:platform:1");
        library["natives"] = new { windows = "natives-windows-${arch}" };
        library["extract"] = new { exclude = new[] { "META-INF/" } };
        library["downloads"] = new { classifiers = new Dictionary<string, object> { ["natives-windows-32"] = new { url = "https://example.invalid/32.jar" }, ["natives-windows-64"] = new { url = "https://example.invalid/64.jar" } } };
        return library;
    }
    private static void Natives() {
        var profile = Good(Compile(new() { ["libraries"] = new[] { Native() } }, environment: new("windows", "x86", 8, 32)));
        Require(profile.NativeArtifacts is [{ NativeClassifier: "natives-windows-32" }] && profile.NativeArtifacts[0].RelativePath.EndsWith("platform-1-natives-windows-32.jar", StringComparison.Ordinal) && profile.Classpath.Length == 1, "Native input/classpath/bitness changed.");
        var arm = Good(Compile(new() { ["libraries"] = new[] { Native() } }, environment: new("windows", "aarch64", 21, 64)));
        Require(arm.NativeArtifacts.IsEmpty, "Legacy OS native fallback widened to ARM.");
    }
    private static void LocalArtifacts() {
        var local = Library("fixture:early:1", "local"); local["MMC-filename"] = "custom.jar";
        var profile = Good(Compile(new() { ["libraries"] = new[] { local } }));
        Require(profile.Classpath[0] is { Scope: MmcArtifactScope.InstanceLocal, RelativePath: "custom.jar" } && profile.Classpath[0].DownloadUrls.IsEmpty, "Local jar became a shared Maven download.");
        Require(profile.ArtifactInventory.Any(d => d.Role == MmcArtifactRole.Classpath), "Source artifact inventory missing.");
    }
    private static void ArtifactRejections() {
        var traversal = Library("fixture:a:1", "local"); traversal["MMC-filename"] = "../evil.jar";
        Error(Compile(new() { ["libraries"] = new[] { traversal } }), "MMC_ARTIFACT_PATH");
        var url = Library("fixture:a:1"); url["MMC-absoluteUrl"] = "file:///bad";
        Error(Compile(new() { ["libraries"] = new[] { url } }), "MMC_ARTIFACT_URL");
        var missingUrl = Library("fixture:no-url:1"); missingUrl["downloads"] = new { artifact = new { size = 1 } };
        Error(Compile(new() { ["libraries"] = new[] { missingUrl } }), "MMC_LIBRARY_DOWNLOAD");
        var first = Library("fixture:a:1"); first["downloads"] = new { artifact = new { url = "https://example.invalid/a", sha1 = new string('1', 40) } };
        var second = Library("fixture:a:1"); second["downloads"] = new { artifact = new { url = "https://example.invalid/a", sha1 = new string('2', 40) } };
        Error(Compile(new() { ["libraries"] = new[] { first, second } }), "MMC_LIBRARY_CONFLICT");
    }
    private static void JavaPolicy() {
        var profile = Good(Compile(new() { ["compatibleJavaMajors"] = new[] { 8 }, ["compatibleJavaName"] = "old" },
            [Patch("modern", ("compatibleJavaMajors", new[] { 17, 21, 17 }), ("compatibleJavaName", "new")), Patch("empty", ("compatibleJavaMajors", Array.Empty<int>()), ("compatibleJavaName", ""))]));
        Require(profile.JavaPolicy.AllowedMajors.SequenceEqual([8, 17, 21]) && profile.JavaPolicy.RecommendedRuntimeName == "new" && !profile.JavaPolicy.Allows(18), "Java candidates became an interval/intersection or empty name cleared recommendation.");
        Require(MmcJavaPolicyEvaluator.FilterInstalled(profile.JavaPolicy, [new("wrong", 18, "x86_64")]).State == MmcJavaSelectionState.NoInstalledCandidate, "Missing installation confused with unsatisfiable policy.");
        var unspecified = Good(Compile()).JavaPolicy;
        Require(unspecified.Kind == MmcJavaPolicyKind.Unspecified && unspecified.Allows(8), "Absent declarations were treated as empty allowed set.");
        Error(Compile(new() { ["compatibleJavaMajors"] = new[] { 17, 21 } }, constraints: [new("platform", MaximumMajor: 8)]), "MMC_JAVA_UNSATISFIABLE");
        Error(Compile(new() { ["compatibleJavaMajors"] = new[] { 17, 21 } }, environment: new("windows", "amd64", 8)), "MMC_JAVA_SELECTION");
        var future = Good(Compile(new() { ["compatibleJavaMajors"] = new[] { 27, 31 } }, environment: new("windows", "x86_64", 27)));
        var selected = MmcJavaPolicyEvaluator.FilterInstalled(future.JavaPolicy, [new("other", 28, "x86_64"), new("newer", 31, "x86_64"), new("declared", 27, "x86_64")]);
        Require(selected.State == MmcJavaSelectionState.CompatibleCandidates && selected.Candidates.Select(value => value.Major).SequenceEqual([31, 27]) && !future.JavaPolicy.Allows(28),
            "Future declared Java majors were hardcoded away, reordered, or widened into an interval.");
        Error(Compile(new() { ["compatibleJavaMajors"] = new[] { 27 } }, constraints: [new("host", MaximumMajor: 26)]), "MMC_JAVA_UNSATISFIABLE");
    }
    private static void Logging() {
        var profile = Good(Compile(new() { ["logging"] = new { argument = "-Dlog4j.configurationFile=${path}", type = "log4j2-xml", file = new { id = "fixture.xml", url = "https://example.invalid/log.xml", sha1 = new string('a', 40), size = 10 } } }));
        Require(profile.AuxiliaryArtifacts.Any(a => a.Role == MmcArtifactRole.LoggingConfiguration && a.RelativePath == "logging/fixture.xml") && profile.JvmArguments[^1].Value == "-Dlog4j.configurationFile=${mmc_logging_config}", "Logging file/argument binding lost.");
    }
    private static void Provenance() {
        var first = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:a:1") } }));
        var again = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:a:1") } }));
        var differentRuntime = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:a:1") } }, environment: new("windows", "x86", JavaBitness: 32)));
        Require(first.ProfileFingerprint == again.ProfileFingerprint && first.RuntimePlanFingerprint == again.RuntimePlanFingerprint && first.ProfileFingerprint == differentRuntime.ProfileFingerprint && first.RuntimePlanFingerprint != differentRuntime.RuntimePlanFingerprint, "Structural/runtime fingerprints lost boundaries.");
        Require(first.OverrideEffects.Any(e => e.ComponentUid == "net.minecraft" && e.ReplacesBuiltinLaunchDefinition) && first.FieldProvenance.Any(p => p.Field == "mainClass"), "Override/source provenance missing.");
    }
    private static void Tokenization() {
        Require(MmcArgumentTokenizer.Tokenize("\"\" plain '中文 path' C:\\trailing\\").SequenceEqual(["", "plain", "中文 path", "C:\\trailing\\"]), "Token boundaries changed.");
        try { MmcArgumentTokenizer.Tokenize("\"unterminated"); throw new Exception("Missing quote error"); } catch (FormatException) { }
    }
    private static void CompleteOverrides() {
        var emptyEntry = Good(Compile(patches: [Patch("net.minecraftforge", ("mainClass", ""), ("libraries", Array.Empty<object>()))]));
        Require(!emptyEntry.OverrideEffects.Single(e => e.ComponentUid == "net.minecraftforge").ReplacesBuiltinLaunchDefinition, "Empty Forge fields were mistaken for a complete launch definition.");
        var emptyLibraries = Good(Compile(patches: [Patch("net.minecraftforge", ("mainClass", "fixture.Forge"), ("libraries", Array.Empty<object>()))]));
        Require(!emptyLibraries.OverrideEffects.Single(e => e.ComponentUid == "net.minecraftforge").ReplacesBuiltinLaunchDefinition, "Empty Forge library list proved completeness.");
        var nullJar = Good(Compile(new() { ["mainJar"] = null, ["libraries"] = new[] { Library("fixture:base:1") } }, [Patch("jar-provider", ("mainJar", Library("fixture:main:1")))]));
        Require(!nullJar.OverrideEffects.Single(e => e.ComponentUid == "net.minecraft").ReplacesBuiltinLaunchDefinition, "Null Minecraft mainJar proved completeness.");
        var laterEntry = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:base:1") } }, [Patch("partial", ("mainClass", "fixture.Partial"))]));
        Require(laterEntry.OverrideEffects.All(e => !e.ReplacesBuiltinLaunchDefinition), "Later partial entry inherited an earlier complete-chain flag.");
        var replacedLibrary = Good(Compile(new() { ["libraries"] = new[] { Library("fixture:base:1") } }, [Patch("partial", ("libraries", new[] { Library("fixture:base:2") }))]));
        Require(!replacedLibrary.OverrideEffects.Single(e => e.ComponentUid == "net.minecraft").ReplacesBuiltinLaunchDefinition, "A superseded library contribution was counted as adopted.");
    }
}
