using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLLF.MMCC.Core;

internal static class MmcPackageLocalScopeTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC package-local scope changes only measured author local keys and preserves original patch revisions", ScopedKeys);
        yield return ("MMC absent package-local scope keeps the historical serialized shape and explicit-null compilation", LegacyNull);
    }
    private static void ScopedKeys() {
        var package = Package(); var before = Resolve(package); var after = Resolve(package with { PackageLocalScopeSha256 = new string('a', 64) });
        Require(before.MainJar.Key != after.MainJar.Key && before.MainJar.SourceRevision == after.MainJar.SourceRevision && before.MainJar.RelativePath == after.MainJar.RelativePath,
            "Measured local keys must include the package scope without rewriting the original patch SHA.");
        Require(before.Classpath.Single(value => value.Coordinate?.Artifact == "shared").Key == after.Classpath.Single(value => value.Coordinate?.Artifact == "shared").Key,
            "A new ZIP scope must not re-adopt same-declaration remote artifacts.");
        Require(before.Classpath.Single(value => value.ComponentUid == "fixture.user").Key == after.Classpath.Single(value => value.ComponentUid == "fixture.user").Key,
            "Retained user-patch local libraries must not acquire new author-package authority.");
        Require(before.SourceFingerprint != after.SourceFingerprint && before.ProfileFingerprint != after.ProfileFingerprint && before.RuntimePlanFingerprint != after.RuntimePlanFingerprint,
            "The optional namespace must be covered by resolver/profile/runtime fingerprints.");
        var different = Resolve(package with { PackageLocalScopeSha256 = new string('b', 64) });
        Require(different.MainJar.Key != after.MainJar.Key, "Different measured packages reused a package-local artifact identity.");
        var invalid = MmcComponentResolver.Resolve(package with { PackageLocalScopeSha256 = "invalid" }, []);
        Require(invalid.HasErrors, "Invalid package-local scope reached metadata/compilation.");
    }
    private static void LegacyNull() {
        var package = Package(); var before = Resolve(package); var explicitNull = Resolve(package with { PackageLocalScopeSha256 = null });
        Require(before.MainJar.Key == explicitNull.MainJar.Key && before.MainJar.SourceRevision == explicitNull.MainJar.SourceRevision && before.SourceFingerprint == explicitNull.SourceFingerprint &&
            before.ProfileFingerprint == explicitNull.ProfileFingerprint && before.RuntimePlanFingerprint == explicitNull.RuntimePlanFingerprint,
            "An absent namespace changed historical compilation.");
        var source = new MmcSourceBundle("", new string('a', 64), new string('b', 64), []);
        Require(!JsonSerializer.Serialize(source).Contains("PackageLocalScopeSha256", StringComparison.Ordinal), "Historical JSON gained a null field.");
        Require(JsonSerializer.Serialize(source with { PackageLocalScopeSha256 = new string('c', 64) }).Contains("PackageLocalScopeSha256", StringComparison.Ordinal), "Explicit scope was not serialized.");
        var definition = MmcDefinitionTests.WithArtifact();
        var wrong = definition with { SourceBundle = definition.SourceBundle with { PackageLocalScopeSha256 = new string('e', 64) } };
        Require(!MmcDefinitionCodec.Validate(wrong, MmcDefinitionValidationMode.Draft).IsEmpty, "Scope did not bind the measured origin package.");
    }
    private static MmcPackage Package() {
        const string manifest = """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},{"uid":"fixture.user","version":"1"}]}""";
        const string game = """{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Main","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"game.jar"},"+libraries":[{"name":"fixture:shared:1","url":"https://fixture.invalid/"}],"compatibleJavaMajors":[8]}""";
        const string user = """{"formatVersion":1,"uid":"fixture.user","version":"1"}""";
        Dictionary<string, string> metadata = new() { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = game, ["patches/fixture.user.json"] = user };
        var parsed = MmcPackageReader.Parse(new(metadata.Keys.Select(value => new MmcArchiveEntry(value)).ToArray(), metadata));
        if (parsed.HasErrors || parsed.Package is null) throw new Exception(string.Join("; ", parsed.Diagnostics)); return parsed.Package;
    }
    private static MmcCompiledProfile Resolve(MmcPackage package) {
        const string patch = """{"formatVersion":1,"uid":"fixture.user","version":"1","+libraries":[{"name":"fixture:user:1","MMC-hint":"local","MMC-filename":"user.jar"}]}""";
        var resolved = MmcComponentResolver.Resolve(package, [], [new("fixture.user", PatchContentUtf8: patch)]);
        if (!resolved.IsComplete) throw new Exception(string.Join("; ", resolved.Diagnostics));
        var result = MmcProfileCompiler.Compile(resolved.Profile!, new("windows", "x86_64", 8));
        return !result.HasErrors && result.Profile is not null ? result.Profile : throw new Exception(string.Join("; ", result.Diagnostics));
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
