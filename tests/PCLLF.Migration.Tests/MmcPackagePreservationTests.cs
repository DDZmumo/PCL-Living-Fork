using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;

internal static class MmcPackagePreservationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC local custom patch can be unversioned without adopting cached or invented versions", UnversionedLocal);
        yield return ("MMC orphan patch files are opaque package metadata while referenced patches remain strict", OrphanMetadata);
        yield return ("MMC persisted local metadata preserves missing raw versions and remote metadata remains exact", MetadataIdentity);
        yield return ("MMC external author baseline pins complete file membership and preserves only root-local libraries", BaselineMembership);
        yield return ("MMC external author baseline rejects missing checksums wrong roles forged members and duplicate JSON", BaselineRejections);
        yield return ("MMC binder adopts independent package originals using measured baseline membership", PackageBinding);
        yield return ("MMC binder rejects unproven game files and altered package-original locks", PackageBindingRejections);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static MmcParseResult Package(string manifest, Dictionary<string, string> patches, bool omitOrphanContent = false) {
        Dictionary<string, string> content = new() { ["mmc-pack.json"] = manifest };
        foreach (var patch in patches) content["patches/" + patch.Key + ".json"] = patch.Value;
        var entries = content.Select(p => new MmcArchiveEntry(p.Key, Encoding.UTF8.GetByteCount(p.Value))).ToArray();
        if (omitOrphanContent) content.Remove("patches/backup.json");
        return MmcPackageReader.Parse(new(entries, content));
    }
    private static void UnversionedLocal() {
        const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"custom\",\"cachedVersion\":\"hint-only\"}]}";
        var package = Package(manifest, new() { ["custom"] = "{\"uid\":\"custom\",\"+jvmArgs\":[\"-Dfixture=true\"]}" });
        var resolved = MmcComponentResolver.Resolve(package, []);
        Require(!package.HasErrors && resolved.IsComplete && resolved.Profile!.Components is [{ Version: null }] && resolved.MissingMetadata.IsEmpty,
            "Local UID+hash identity became a fabricated version or a remote metadata request.");
        var exact = Package("{\"formatVersion\":1,\"components\":[{\"uid\":\"consumer\",\"version\":\"1\"},{\"uid\":\"custom\"}]}", new() {
            ["consumer"] = "{\"uid\":\"consumer\",\"version\":\"1\",\"requires\":[{\"uid\":\"custom\",\"equals\":\"1\"}]}", ["custom"] = "{\"uid\":\"custom\"}"
        });
        Require(exact.HasErrors && exact.Diagnostics.Any(d => d.Code == "MMC_EXACT_VERSION"), "Unknown local version falsely satisfied a concrete exact version.");
    }
    private static void OrphanMetadata() {
        const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"custom\"}]}";
        var package = Package(manifest, new() { ["custom"] = "{\"uid\":\"custom\"}", ["backup"] = "{\"uid\":\"custom\",\"formatVersion\":99,\"futureLaunch\":true,\"libraries\":{\"not\":\"active\"}}" });
        Require(!package.HasErrors && package.Package!.Components.Length == 1 && package.Package.PackageMetadata is [{ RelativePath: "patches/backup.json", ContentSha256: not null }], "Orphan backup JSON contributed or blocked active component semantics.");
        var invalidJson = Package(manifest, new() { ["custom"] = "{\"uid\":\"custom\"}", ["backup"] = "opaque malformed backup {" });
        Require(!invalidJson.HasErrors, "Unreferenced package backup was parsed as executable metadata.");
        var missing = Package(manifest, new() { ["custom"] = "{\"uid\":\"custom\"}", ["backup"] = "opaque" }, omitOrphanContent: true);
        Require(!missing.HasErrors && missing.Package!.PackageMetadata is [{ ContentSha256: null }], "Opaque file bytes must be preserved by the package baseline, not forced through semantic UTF-8 parsing.");
        var referenced = Package(manifest, new() { ["custom"] = "{\"uid\":\"custom\",\"futureLaunch\":true}" });
        Require(referenced.HasErrors, "Referenced patch capability validation was bypassed.");
    }
    private static void MetadataIdentity() {
        var draft = MmcDefinitionTests.Draft();
        string prefix = "PCL/MmcSource/" + draft.SourceBundle.BundleSha256 + "/";
        var local = new MmcSourceMetadataRecord("patches/custom.json", MmcDefinitionTests.Hash('4'), prefix + "patches/custom.json", MmcSourceRecordKind.Component,
            "custom", null, MmcComponentSourceKind.PackageLocal, "package:patches/custom.json");
        var orphan = new MmcSourceMetadataRecord("patches/backup.json", MmcDefinitionTests.Hash('5'), prefix + "patches/backup.json", MmcSourceRecordKind.PackageMetadata);
        draft = draft with { SourceBundle = draft.SourceBundle with { MetadataRecords = draft.SourceBundle.MetadataRecords.Add(local).Add(orphan) } };
        Require(MmcDefinitionCodec.Validate(draft, MmcDefinitionValidationMode.Draft).IsEmpty, "UID+raw patch hash local source identity was refused.");
        var remote = local with { RelativePath = "metadata/custom.json", ComponentSourceKind = MmcComponentSourceKind.RemoteMetadata, SourceIdentity = "https://example.invalid/custom.json" };
        Require(!MmcDefinitionCodec.Validate(draft with { SourceBundle = draft.SourceBundle with { MetadataRecords = [draft.SourceBundle.MetadataRecords[0], remote] } }, MmcDefinitionValidationMode.Draft).IsEmpty,
            "Remote source lost its mandatory exact version identity.");
    }
    internal static (MmcValidatedPackageBaseline Validated, MmcPackageBaselineInput Input) BaselineFixture() {
        string[] names = ["pack/mmc-pack.json", "pack/libraries/private.jar", "pack/minecraft/libraries/game.jar", "pack/patches/backup.json", "pack/PCL/Setup.ini"];
        var rows = names.Select((path, index) => {
            byte[] payload = Encoding.UTF8.GetBytes("self-authored-file-" + index);
            var role = MmcPackageFileBaselineCodec.Classify("pack/", path);
            return new MmcPackageFileRecord(path, role.Role, role.TargetRelativePath,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(payload)), payload.Length, Crc(payload));
        }).ToImmutableArray();
        var entries = ImmutableArray.Create(new MmcPackageArchiveDescriptor("pack/", true, 0, 0)).AddRange(rows.Select(r => new MmcPackageArchiveDescriptor(r.RelativePath, false, r.SizeBytes, r.Crc32)));
        var document = new MmcPackageFileBaseline(1, MmcDefinitionTests.Hash('a'), "pack/", entries, rows);
        byte[] bytes = MmcPackageFileBaselineCodec.Serialize(document);
        var reference = Reference(bytes, rows.Length);
        var input = new MmcPackageBaselineInput(bytes);
        var result = MmcPackageFileBaselineCodec.Read(input, reference, document.PackageSha256, "pack/");
        Require(!result.HasErrors && result.Baseline is not null, "Synthetic package baseline failed to read.");
        return (result.Baseline!, input);
    }
    private static MmcExternalBundleReference Reference(byte[] content, int count) => new(
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), "PCL/MmcSource/" + MmcDefinitionTests.Hash('b') + "/author-files.json", count);
    private static void BaselineMembership() {
        var fixture = BaselineFixture();
        var members = MmcPackageFilePreservation.LibraryMembers(fixture.Validated);
        var artifacts = MmcPackageFilePreservation.CreateLibraryArtifacts(fixture.Validated);
        Require(members is [{ SourceRelativePath: "pack/libraries/private.jar", LibraryRelativePath: "private.jar" }] && artifacts is [{ ComponentUid: "", Role: MmcArtifactRole.DownloadOnly, Scope: MmcArtifactScope.InstanceLocal, PackageFileOrigin: not null }], "Package originals invented a component UID or treated game-root libraries/metadata as executable inputs.");
        Require(members[0].ArtifactKey.Length == 64 && members[0].SourceRevision.Length == 64 && members[0].ArtifactKey != members[0].SourceRevision && members[0].BaselineReference.EntryCount == 5, "Independent package-file origin protocol was not preserved.");
    }
    private static void BaselineRejections() {
        var fixture = BaselineFixture();
        var reference = fixture.Validated.Reference;
        var corrupted = fixture.Input.Content.ToArray(); corrupted[0] ^= 1;
        Require(MmcPackageFileBaselineCodec.Read(new(corrupted), reference, fixture.Validated.Baseline.PackageSha256, "pack/").HasErrors, "Changed baseline bytes kept their original membership authority.");
        Require(MmcPackageFileBaselineCodec.Read(fixture.Input, reference, MmcDefinitionTests.Hash('f'), "pack/").HasErrors, "Wrong original ZIP identity accepted.");
        foreach (Action<System.Text.Json.Nodes.JsonObject> change in new Action<System.Text.Json.Nodes.JsonObject>[] {
            root => root["files"]![1]!.AsObject().Remove("sha256"),
            root => { root["files"]![2]!["role"] = "LocalLibrary"; root["files"]![2]!["targetRelativePath"] = "libraries/evil.jar"; },
            root => root["files"]![1]!["relativePath"] = "pack/libraries/forged.jar",
            root => root["files"]![1]!["crc32"] = 1
        }) {
            var root = System.Text.Json.Nodes.JsonNode.Parse(fixture.Input.Content.Span)!.AsObject(); change(root);
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            Require(MmcPackageFileBaselineCodec.Read(new(bytes), Reference(bytes, 5), fixture.Validated.Baseline.PackageSha256, "pack/").HasErrors, "Malformed role/checksum/member baseline accepted.");
        }
        string duplicate = Encoding.UTF8.GetString(fixture.Input.Content.Span).Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal);
        byte[] duplicateBytes = Encoding.UTF8.GetBytes(duplicate);
        Require(MmcPackageFileBaselineCodec.Read(new(duplicateBytes), Reference(duplicateBytes, 5), fixture.Validated.Baseline.PackageSha256, "pack/").HasErrors, "Duplicate baseline root property accepted.");
        Require(MmcPackageFileBaselineCodec.Read(fixture.Input, reference, fixture.Validated.Baseline.PackageSha256, "pack/", new() { MaxDocumentBytes = 10 }).HasErrors, "Baseline document budget ignored.");
    }
    private static uint Crc(byte[] bytes) {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        return ~crc;
    }
    private sealed record BindingFixture(MmcCompiledProfile Plan, MmcInstanceDefinition Draft, MmcPackageBaselineInput Input,
        ImmutableArray<MmcMaterializedArtifactFact> ActiveFacts, MmcMaterializedArtifactFact Original);
    private static BindingFixture BindingData() {
        Dictionary<string, byte[]> files = new() {
            ["pack/mmc-pack.json"] = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1\"}]}"),
            ["pack/patches/net.minecraft.json"] = Encoding.UTF8.GetBytes("{\"uid\":\"net.minecraft\",\"version\":\"1\",\"mainClass\":\"fixture.Main\",\"mainJar\":{\"name\":\"fixture:client:1\"},\"libraries\":[{\"name\":\"fixture:common:1\"}]}"),
            ["pack/libraries/private.jar"] = [1, 2, 3], ["pack/minecraft/config/fixture.cfg"] = Encoding.UTF8.GetBytes("author configuration")
        };
        byte[] zipBytes;
        using (var stream = new MemoryStream()) {
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true)) {
                archive.CreateEntry("pack/");
                foreach (var file in files) { using var output = archive.CreateEntry(file.Key).Open(); output.Write(file.Value); }
            }
            zipBytes = stream.ToArray();
        }
        string packageSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zipBytes));
        var rows = files.Select(file => {
            var placement = MmcPackageFileBaselineCodec.Classify("pack/", file.Key);
            return new MmcPackageFileRecord(file.Key, placement.Role, placement.TargetRelativePath,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Value)), Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(file.Value)), file.Value.Length, Crc(file.Value));
        }).ToImmutableArray();
        var descriptors = ImmutableArray.Create(new MmcPackageArchiveDescriptor("pack/", true, 0, 0)).AddRange(rows.Select(file => new MmcPackageArchiveDescriptor(file.RelativePath, false, file.SizeBytes, file.Crc32)));
        var baseline = new MmcPackageFileBaseline(1, packageSha, "pack/", descriptors, rows);
        byte[] baselineBytes = MmcPackageFileBaselineCodec.Serialize(baseline);
        var reference = Reference(baselineBytes, rows.Length);
        var input = new MmcPackageBaselineInput(baselineBytes);
        var read = MmcPackageFileBaselineCodec.Read(input, reference, packageSha, "pack/");
        Require(!read.HasErrors, "Coherent synthetic baseline failed.");
        var metadata = files.Where(file => file.Key.EndsWith(".json", StringComparison.Ordinal)).ToDictionary(file => file.Key, file => Encoding.UTF8.GetString(file.Value));
        var parsed = MmcPackageReader.Parse(new(descriptors.Select(d => new MmcArchiveEntry(d.Path, d.SizeBytes, d.IsDirectory)).ToArray(), metadata));
        var resolved = MmcComponentResolver.Resolve(parsed, []);
        Require(resolved.IsComplete, "Coherent synthetic source package failed resolution.");
        var compilation = MmcProfileCompiler.Compile(resolved.Profile!, new("windows", "amd64", 17));
        Require(!compilation.HasErrors && compilation.Profile is not null, "Coherent synthetic source package failed compile.");
        var plan = compilation.Profile!;
        var draft = MmcDefinitionTests.Draft(plan.SourceFingerprint);
        string prefix = "PCL/MmcSource/" + draft.SourceBundle.BundleSha256 + "/";
        draft = draft with { Origin = draft.Origin with { PackageSha256 = packageSha }, SourceBundle = draft.SourceBundle with { RelativeRoot = "pack/", MetadataRecords = [
            new("mmc-pack.json", rows[0].Sha256, prefix + "mmc-pack.json", MmcSourceRecordKind.Manifest),
            new("patches/net.minecraft.json", rows[1].Sha256, prefix + "patches/net.minecraft.json", MmcSourceRecordKind.Component, "net.minecraft", "1", MmcComponentSourceKind.PackageLocal)
        ] }, AuthorFileBaseline = reference };
        string view = MmcDefinitionTests.Hash('9');
        var active = plan.Classpath.Select(artifact => {
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("verified active fixture:" + artifact.Key)));
            return new MmcMaterializedArtifactFact(artifact.Key, artifact.SourceRevision, hash, 10, "PCL/MmcArtifacts/sha256/" + hash, "PCL/MmcRuntime/" + view + "/libraries/" + artifact.RelativePath);
        }).ToImmutableArray();
        var member = MmcPackageFilePreservation.LibraryMembers(read.Baseline!)[0];
        return new(plan, draft, input, active, new(member.ArtifactKey, member.SourceRevision, member.Sha256, member.SizeBytes, "PCL/MmcArtifacts/sha256/" + member.Sha256, "libraries/" + member.LibraryRelativePath, member.Sha1));
    }
    private static MmcBindOptions InitialOptions() => new(1, 1, MmcBindingIntent.InitialImport, MmcDefinitionTests.Hash('2'), 1);
    private static void PackageBinding() {
        var fixture = BindingData();
        var result = MmcPlanBinder.Bind(fixture.Draft, fixture.Plan, fixture.ActiveFacts.Add(fixture.Original), InitialOptions(), packageBaseline: fixture.Input);
        Require(!result.HasErrors && result.Definition!.PackageFileInventory is [{ SourceRelativePath: "pack/libraries/private.jar" }] && result.Definition.PackageFileLocks is [{ TargetRelativePath: "libraries/private.jar" }] &&
            result.Definition.ArtifactInventory.All(item => item.ArtifactKey != fixture.Original.ArtifactKey), "Package original impersonated a component or did not acquire an independent lock: " + string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var repaired = MmcPlanBinder.Bind(result.Definition!, fixture.Plan, fixture.ActiveFacts.Add(fixture.Original), new(1, 1, MmcBindingIntent.RepairExisting, InitialOptions().OutputJsonSha256, 1));
        Require(!repaired.HasErrors && MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(repaired.Definition!)).Status == MmcDefinitionReadStatus.Valid, "Known original required the ZIP/baseline again instead of immutable published authority.");
    }
    private static void PackageBindingRejections() {
        var fixture = BindingData();
        Require(MmcPlanBinder.Bind(fixture.Draft, fixture.Plan, fixture.ActiveFacts.Add(fixture.Original), InitialOptions()).HasErrors, "Unproven private file fact was accepted without baseline bytes.");
        var wrongDigest = fixture.Original with { ObservedSha256 = MmcDefinitionTests.Hash('7'), RecoveryObjectRelativePath = "PCL/MmcArtifacts/sha256/" + MmcDefinitionTests.Hash('7') };
        Require(MmcPlanBinder.Bind(fixture.Draft, fixture.Plan, fixture.ActiveFacts.Add(wrongDigest), InitialOptions(), packageBaseline: fixture.Input).HasErrors, "Measured package-original contents were changed during adoption.");
        string gamePath = "pack/minecraft/config/fixture.cfg";
        var gameFact = fixture.Original with { ArtifactKey = MmcPackageFilePreservation.ComputeArtifactKey(fixture.Draft.AuthorFileBaseline!.ContentSha256, fixture.Draft.Origin.PackageSha256, gamePath),
            SourceRevision = MmcPackageFilePreservation.ComputeSourceRevision(fixture.Draft.AuthorFileBaseline.ContentSha256, fixture.Draft.Origin.PackageSha256, gamePath) };
        Require(MmcPlanBinder.Bind(fixture.Draft, fixture.Plan, fixture.ActiveFacts.Add(gameFact), InitialOptions(), packageBaseline: fixture.Input).HasErrors, "Author game/config data became a package-library original.");
        var result = MmcPlanBinder.Bind(fixture.Draft, fixture.Plan, fixture.ActiveFacts.Add(fixture.Original), InitialOptions(), packageBaseline: fixture.Input);
        Require(!result.HasErrors, "Valid prior package-original binding failed.");
        var published = result.Definition!;
        var tampered = published with { PackageFileLocks = [published.PackageFileLocks[0] with { AdoptedSha256 = wrongDigest.ObservedSha256, RecoveryObjectRelativePath = wrongDigest.RecoveryObjectRelativePath }] };
        Require(MmcPlanBinder.Bind(tampered, fixture.Plan, fixture.ActiveFacts.Add(wrongDigest), new(1, 1, MmcBindingIntent.RepairExisting, InitialOptions().OutputJsonSha256, 1)).HasErrors, "Changed package-original old lock was washed through repair.");
        var artifact = MmcPackageFilePreservation.CreateLibraryArtifacts(MmcPackageFileBaselineCodec.Read(fixture.Input, fixture.Draft.AuthorFileBaseline!, fixture.Draft.Origin.PackageSha256, "pack/").Baseline!)[0];
        var altered = fixture.Plan with { DownloadOnlyArtifacts = [artifact] };
        var fingerprints = MmcProfileFingerprints.Compute(altered);
        altered = altered with { ProfileFingerprint = fingerprints.ProfileFingerprint, RuntimePlanFingerprint = fingerprints.RuntimePlanFingerprint };
        Require(MmcPlanBinder.Bind(fixture.Draft, altered, fixture.ActiveFacts.Add(fixture.Original), InitialOptions(), packageBaseline: fixture.Input).HasErrors, "A refreshed fingerprint promoted a preservation-only input into active execution/download authority.");
    }
}
