using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLLF.MMCC.Core;

internal static class MmcResolutionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC resolution requests exact snapshots then completes without network", BatchResolution);
        yield return ("MMC resolution inserts nested missing providers before first consumers", DependencyInsertion);
        yield return ("MMC resolution collects disabled dependencies but rejects enabled consumers of disabled providers", DisabledGraph);
        yield return ("MMC resolution keeps local overrides and refuses conflicting explicit versions", LocalPriority);
        yield return ("MMC resolution reselects only automatic providers for consistent exact requirements", AutomaticReselection);
        yield return ("MMC resolution discards dependencies and false conflicts from superseded automatic metadata", SupersededDependencies);
        yield return ("MMC resolution uses first source suggestion and rejects conflicting exact versions", Suggestions);
        yield return ("MMC resolution adopts a unique index recommendation and fixes its content digest", Recommendations);
        yield return ("MMC resolution rejects forged or ambiguous metadata identities and hashes", SnapshotIdentity);
        yield return ("MMC resolution enforces cycles conflicts metadata and component budgets", GraphBudgets);
        yield return ("MMC resolution enabling unknown inactive capabilities fails closed", OverrideCapabilities);
        yield return ("MMC resolution fingerprints are deterministic and retain index source revisions", Fingerprints);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static MmcParseResult Package(object[] components, Dictionary<string, string>? patches = null) {
        var values = new Dictionary<string, string> { ["mmc-pack.json"] = JsonSerializer.Serialize(new { formatVersion = 1, components }) };
        if (patches is not null) foreach (var pair in patches) values["patches/" + pair.Key + ".json"] = pair.Value;
        return MmcPackageReader.Parse(new(values.Select(p => new MmcArchiveEntry(p.Key, System.Text.Encoding.UTF8.GetByteCount(p.Value))).ToArray(), values));
    }
    private static string Metadata(string uid, string version = "1", object[]? requires = null, object[]? conflicts = null) => JsonSerializer.Serialize(new {
        formatVersion = 1, uid, version, requires = requires ?? [], conflicts = conflicts ?? [], mainClass = "fixture.Main"
    });
    private static MmcMetadataSnapshot Snapshot(string uid, string version = "1", object[]? requires = null, object[]? conflicts = null) =>
        new(uid, version, "fixture:" + uid + "/" + version, Metadata(uid, version, requires, conflicts));
    private static MmcResolutionResult Resolve(MmcParseResult package, MmcMetadataSnapshot[] snapshots, MmcComponentOverride[]? changes = null, MmcReadLimits? limits = null) =>
        MmcComponentResolver.Resolve(package, snapshots, changes, limits);
    private static void Error(MmcResolutionResult result, string code) => Require(result.HasErrors && result.Diagnostics.Any(d => d.Code == code), "Expected " + code + ": " + string.Join(",", result.Diagnostics.Select(d => d.Code)));
    private static void BatchResolution() {
        var package = Package([new { uid = "base", version = "1" }]);
        var pending = Resolve(package, []);
        Require(!pending.HasErrors && !pending.IsComplete && pending.MissingMetadata is [{ Uid: "base", Version: "1" }], "Missing batch request.");
        var result = Resolve(package, [Snapshot("base")]);
        Require(result.IsComplete && result.Profile!.Components[0].Patch.Fields["mainClass"].GetString() == "fixture.Main", "Snapshot continuation failed.");
    }
    private static void DependencyInsertion() {
        var package = Package([new { uid = "early", version = "1" }, new { uid = "late", version = "1" }]);
        var snapshots = new[] { Snapshot("early", requires: [new { uid = "dep", equals = "1" }]), Snapshot("late", requires: [new { uid = "dep", equals = "1" }]),
            Snapshot("dep", requires: [new { uid = "foundation", equals = "1" }]), Snapshot("foundation") };
        var result = Resolve(package, snapshots);
        Require(result.IsComplete && result.Profile!.Components.Select(c => c.Uid).SequenceEqual(["foundation", "dep", "early", "late"]), "New providers were not inserted before earliest consumers.");
    }
    private static void DisabledGraph() {
        var disabled = Package([new { uid = "disabled", version = "1", disabled = true }]);
        var complete = Resolve(disabled, [Snapshot("disabled", requires: [new { uid = "needed", equals = "1" }]), Snapshot("needed")]);
        Require(complete.IsComplete && complete.Profile!.Components[0].Uid == "needed" && !complete.Profile.Components[1].Enabled, "Disabled source dependency collection lost.");
        var invalid = Package([new { uid = "consumer", version = "1" }, new { uid = "provider", version = "1", disabled = true }]);
        Error(Resolve(invalid, [Snapshot("consumer", requires: [new { uid = "provider", equals = "1" }]), Snapshot("provider")]), "MMC_DISABLED_DEPENDENCY");
    }
    private static void LocalPriority() {
        var package = Package([new { uid = "local", version = "requested" }], new() { ["local"] = Metadata("local", "actual") });
        var result = Resolve(package, [Snapshot("local", "requested")]);
        Require(result.IsComplete && result.Profile!.Components[0] is { Version: "actual", SourceKind: MmcComponentSourceKind.PackageLocal }, "Remote replaced local patch.");
        var conflict = Package([new { uid = "a", version = "1" }, new { uid = "b", version = "explicit" }]);
        Error(Resolve(conflict, [Snapshot("a", requires: [new { uid = "b", equals = "2" }]), Snapshot("b", "explicit")]), "MMC_EXACT_VERSION");
    }
    private static void AutomaticReselection() {
        var package = Package([new { uid = "consumer", version = "1" }, new { uid = "provider", version = "old", dependencyOnly = true }]);
        var result = Resolve(package, [Snapshot("consumer", requires: [new { uid = "provider", equals = "new" }]), Snapshot("provider", "old"), Snapshot("provider", "new")]);
        Require(result.IsComplete && result.Profile!.Components[1].Version == "new", "Automatic exact reselect failed.");
        Error(Resolve(package, [Snapshot("consumer", requires: [new { uid = "provider", equals = "new" }]), Snapshot("provider", "old"), Snapshot("provider", "new")], [new("provider", Version: "old")]), "MMC_EXACT_VERSION");
    }
    private static void Suggestions() {
        var package = Package([new { uid = "a", version = "1" }, new { uid = "b", version = "1" }]);
        var result = Resolve(package, [Snapshot("a", requires: [new { uid = "dep", suggests = "first" }]), Snapshot("b", requires: [new { uid = "dep", suggests = "second" }]), Snapshot("dep", "first")]);
        Require(result.IsComplete && result.Profile!.Components[0].Version == "first", "First suggestion ordering changed.");
        Error(Resolve(package, [Snapshot("a", requires: [new { uid = "dep", equals = "one" }]), Snapshot("b", requires: [new { uid = "dep", equals = "two" }])]), "MMC_EXACT_VERSION");
    }
    private static void SupersededDependencies() {
        var package = Package([new { uid = "consumer", version = "1" }, new { uid = "a", version = "1", dependencyOnly = true }]);
        var obsolete = Resolve(package, [Snapshot("consumer", requires: [new { uid = "a", equals = "2" }]),
            Snapshot("a", "1", requires: [new { uid = "obsolete", equals = "1" }]), Snapshot("a", "2")]);
        Require(obsolete.IsComplete && obsolete.Profile!.Components.All(c => c.Uid != "obsolete") && obsolete.MissingMetadata.IsEmpty, "Superseded dependency was retained or requested.");
        var changed = Resolve(package, [Snapshot("consumer", requires: [new { uid = "a", equals = "2" }]),
            Snapshot("a", "1", requires: [new { uid = "dep", equals = "1" }]), Snapshot("a", "2", requires: [new { uid = "dep", equals = "2" }]), Snapshot("dep", "2")]);
        Require(changed.IsComplete && changed.Profile!.Components.Single(c => c.Uid == "dep").Version == "2", "Superseded exact dependency was not replaced.");
        var noFalseConflict = Resolve(package, [Snapshot("consumer", requires: [new { uid = "a", equals = "2" }, new { uid = "b", equals = "2" }]),
            Snapshot("a", "1", requires: [new { uid = "b", equals = "1" }]), Snapshot("a", "2"), Snapshot("b", "2")]);
        Require(noFalseConflict.IsComplete && noFalseConflict.Profile!.Components.Single(c => c.Uid == "b").Version == "2", "Old metadata created a false exact conflict.");
        // A transitive consumer is only discovered on the next pass; its
        // requirement must also evict an earlier discovered obsolete subtree.
        var transitive = Package([new { uid = "consumer", version = "1" }, new { uid = "a", version = "1", dependencyOnly = true }]);
        var pruned = Resolve(transitive, [Snapshot("consumer", requires: [new { uid = "bridge", equals = "1" }]), Snapshot("bridge", requires: [new { uid = "a", equals = "2" }]),
            Snapshot("a", "1", requires: [new { uid = "obsolete", equals = "1" }]), Snapshot("a", "2"), Snapshot("obsolete")]);
        Require(pruned.IsComplete && pruned.Profile!.Components.All(c => c.Uid != "obsolete"), "Discovered obsolete subtree survived reachability rebuilding.");
    }
    private static void Recommendations() {
        var package = Package([new { uid = "base" }]);
        Require(Resolve(package, []).MissingMetadata is [{ Kind: MmcMetadataRequestKind.Recommendation }], "Missing index request.");
        var version = Snapshot("base", "fixed");
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version.ContentUtf8)));
        var index = new MmcMetadataSnapshot("base", "", "fixture:index", JsonSerializer.Serialize(new {
            formatVersion = 1, uid = "base", versions = new[] { new { version = "fixed", recommended = true, sha256 = hash } }
        }), Kind: MmcMetadataRequestKind.Recommendation);
        var result = Resolve(package, [index, version]);
        Require(result.IsComplete && result.Profile!.AdoptedRecommendations is [{ Version: "fixed", VersionSha256: not null }], "Index adoption failed.");
        Error(Resolve(package, [index, version with { ContentUtf8 = Metadata("base", "different") }]), "MMC_METADATA_DIGEST");
        var noRecommendation = index with { ContentUtf8 = JsonSerializer.Serialize(new { formatVersion = 1, uid = "base", versions = new[] { new { version = "x", recommended = false } } }) };
        Error(Resolve(package, [noRecommendation]), "MMC_VERSION_REQUIRED");
    }
    private static void SnapshotIdentity() {
        var package = Package([new { uid = "base", version = "1" }]);
        Error(Resolve(package, [Snapshot("base") with { ExpectedSha256 = new string('0', 64) }]), "MMC_METADATA_DIGEST");
        Error(Resolve(package, [Snapshot("base") with { ContentUtf8 = Metadata("wrong") }]), "MMC_METADATA_IDENTITY");
        Error(Resolve(package, [Snapshot("base"), Snapshot("base") with { Source = "another:source" }]), "MMC_METADATA_AMBIGUOUS");
    }
    private static void GraphBudgets() {
        var package = Package([new { uid = "a", version = "1" }]);
        Error(Resolve(package, [Snapshot("a", requires: [new { uid = "b", equals = "1" }]), Snapshot("b", requires: [new { uid = "a", equals = "1" }])]), "MMC_DEPENDENCY_CYCLE");
        Error(Resolve(package, [Snapshot("a", requires: [new { uid = "b", equals = "1" }]), Snapshot("b", conflicts: [new { uid = "a" }])]), "MMC_COMPONENT_CONFLICT");
        Error(Resolve(package, [Snapshot("a")], limits: new() { MaxTotalMetadataBytes = 10 }), "MMC_METADATA_BUDGET");
        Error(Resolve(package, [Snapshot("a", requires: [new { uid = "b", equals = "1" }])], limits: new() { MaxComponents = 1 }), "MMC_COMPONENT_BUDGET");
    }
    private static void OverrideCapabilities() {
        var package = Package([new { uid = "disabled", version = "1", disabled = true }], new() { ["disabled"] = "{\"uid\":\"disabled\",\"version\":\"1\",\"futureLaunch\":true}" });
        Error(Resolve(package, [], [new("disabled", Enabled: true)]), "MMC_UNSUPPORTED_REQUIRED");
    }
    private static void Fingerprints() {
        var package = Package([new { uid = "a", version = "1" }]);
        var first = Resolve(package, [Snapshot("a")]);
        var repeated = Resolve(package, [Snapshot("a")]);
        var changed = Resolve(package, [Snapshot("a") with { Source = "changed:source" }]);
        Require(first.IsComplete && first.Profile!.SourceFingerprint == repeated.Profile!.SourceFingerprint && first.Profile.SourceFingerprint != changed.Profile!.SourceFingerprint, "Source fingerprint lost determinism or provenance.");
    }
}
