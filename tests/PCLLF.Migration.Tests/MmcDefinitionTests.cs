using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PCLLF.MMCC.Core;

internal static class MmcDefinitionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC definition uses strict camelCase immutable source references and roundtrips", Roundtrip);
        yield return ("MMC definition unknown managed schema stays unsupported and damaged JSON stays invalid", UnsupportedAndDamaged);
        yield return ("MMC definition rejects self parent invalid IDs generation and duplicate keys", Identity);
        yield return ("MMC definition separates original sources from user patches and preferences", SourceOwnership);
        yield return ("MMC definition validates artifact recovery targets states and declared digests", ArtifactLocks);
        yield return ("MMC definition pins fingerprints and projection generation", Projection);
        yield return ("MMC definition bounds arrays text and external author file baselines", Budgets);
        yield return ("MMC asset index reader binds raw parent bytes before accepting object membership", AssetIndexMembership);
        yield return ("MMC derived input protocols bind ordered native sources exclusions and context", DerivedProtocols);
        yield return ("MMC definition keeps native output authority under immutable parent locks", DerivedPersistence);
        yield return ("MMC historical schema-1 bindings retain their exact fingerprint without runtime environment", HistoricalEnvironment);
        yield return ("MMC published runtime environment roundtrips strictly and binds every recorded dimension", PublishedEnvironment);
    }
    internal static string Hash(char digit) => new(digit, 64);
    internal static MmcInstanceDefinition Draft(string? resolutionFingerprint = null) {
        string bundle = Hash('b');
        return new(1, "Mmc", "prism-v1", Guid.Parse("8a0dacb4-38be-4cf7-8c07-99f9c535c07b"), 1,
            new(Hash('a'), "Fixture.zip"), new("fixture/", bundle, resolutionFingerprint ?? Hash('c'),
                [new("mmc-pack.json", Hash('d'), "PCL/MmcSource/" + bundle + "/mmc-pack.json", MmcSourceRecordKind.Manifest)]),
            new("minecraft"), [], [], []);
    }
    internal static MmcInstanceDefinition Published(MmcInstanceDefinition? draft = null) {
        var value = (draft ?? Draft()) with { InstalledProjection = new(Hash('1'), Hash('2'), (draft ?? Draft()).Generation, Hash('3'), Hash('0')) };
        return value with { InstalledProjection = value.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(value) } };
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Invalid(MmcInstanceDefinition value) => Require(!MmcDefinitionCodec.Validate(value).IsEmpty, "Invalid definition passed structural validation.");
    private static void Roundtrip() {
        var original = Published();
        string json = MmcDefinitionCodec.Serialize(original);
        var result = MmcDefinitionCodec.Read(json);
        Require(result.Status == MmcDefinitionReadStatus.Valid && result.Definition!.InstanceId == original.InstanceId && result.Definition.SourceBundle.MetadataRecords[0].ObjectRelativePath == original.SourceBundle.MetadataRecords[0].ObjectRelativePath, "Definition source identity did not roundtrip.");
        Require(json.Contains("\"schemaVersion\":1", StringComparison.Ordinal) && !json.Contains("SchemaVersion", StringComparison.Ordinal), "Persistence is not strict camelCase.");
        Require(MmcDefinitionCodec.Validate(Draft(), MmcDefinitionValidationMode.Draft).IsEmpty && !MmcDefinitionCodec.Validate(Draft()).IsEmpty, "Unbound draft was mistaken for a published definition.");
    }
    private static void UnsupportedAndDamaged() {
        string json = MmcDefinitionCodec.Serialize(Published());
        Require(MmcDefinitionCodec.Read(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)).Status == MmcDefinitionReadStatus.Unsupported, "Unknown schema fell back to a usable definition.");
        Require(MmcDefinitionCodec.Read("{bad").Status == MmcDefinitionReadStatus.Invalid, "Malformed JSON did not remain invalid managed data.");
        var withUnknown = JsonNode.Parse(json)!.AsObject(); withUnknown["accessToken"] = "not-a-real-account";
        Require(MmcDefinitionCodec.Read(withUnknown.ToJsonString()).Status == MmcDefinitionReadStatus.Invalid, "Unknown authentication/preference field was silently retained.");
    }
    private static void Identity() {
        var definition = Published();
        Invalid(definition with { InstanceId = Guid.Empty }); Invalid(definition with { ParentInstanceId = definition.InstanceId });
        Invalid(definition with { ParentInstanceId = Guid.Empty }); Invalid(definition with { Generation = 0 });
        string json = MmcDefinitionCodec.Serialize(definition);
        Require(MmcDefinitionCodec.Read(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal)).Status == MmcDefinitionReadStatus.Invalid, "Duplicate key accepted.");
        Require(MmcDefinitionCodec.Read(json.Replace(definition.InstanceId.ToString("D"), definition.InstanceId.ToString("N"), StringComparison.Ordinal)).Status == MmcDefinitionReadStatus.Invalid, "Non-D GUID spelling accepted.");
    }
    private static void SourceOwnership() {
        var definition = Published();
        Invalid(definition with { Origin = definition.Origin with { OriginalName = "C:/player.zip" } });
        var original = definition.SourceBundle.MetadataRecords[0];
        Invalid(definition with { SourceBundle = definition.SourceBundle with { MetadataRecords = [original with { ObjectRelativePath = "saves/world/source.json" }] } });
        Invalid(definition with { ComponentOverrides = [new("fixture", PatchReference: new(Hash('4'), original.ObjectRelativePath))] });
        Invalid(definition with { SourceBundle = definition.SourceBundle with { MetadataRecords = [original, new("metadata/fixture.json", Hash('4'), "PCL/MmcSource/" + definition.SourceBundle.BundleSha256 + "/remote.json", MmcSourceRecordKind.Component, "fixture", "1", MmcComponentSourceKind.RemoteMetadata, "https://user:secret@example.invalid/file")] } });
    }
    internal static MmcInstanceDefinition WithArtifact() {
        string key = Hash('4'), content = Hash('5'), revision = Hash('6');
        return Draft() with {
            ArtifactInventory = [new(key, "fixture", MmcArtifactRole.Classpath, MmcArtifactScope.InstanceLocal, "windows-x86_64", revision, "fixture.jar", MmcArtifactAdoptionState.Adopted, DeclaredSha256: content)],
            ArtifactLocks = [new(key, revision, content, 10, "PCL/MmcArtifacts/sha256/" + content, "libraries/fixture.jar")]
        };
    }
    private static void ArtifactLocks() {
        var definition = Published(WithArtifact());
        Require(MmcDefinitionCodec.Validate(definition).IsEmpty, "Valid local artifact lock was rejected.");
        var locked = definition.ArtifactLocks[0];
        Invalid(definition with { ArtifactLocks = [locked, locked] });
        Invalid(definition with { ArtifactLocks = [locked with { RecoveryObjectRelativePath = "libraries/original.jar" }] });
        Invalid(definition with { ArtifactLocks = [locked with { TargetRelativePath = "minecraft/libraries/fixture.jar" }] });
        Invalid(definition with { ArtifactInventory = [definition.ArtifactInventory[0] with { State = MmcArtifactAdoptionState.Unadopted }] });
        Invalid(definition with { ArtifactLocks = [locked with { AdoptedSha256 = Hash('7'), RecoveryObjectRelativePath = "PCL/MmcArtifacts/sha256/" + Hash('7') }] });
        Invalid(definition with { ArtifactInventory = [definition.ArtifactInventory[0] with { DeclaredSha1 = new string('a', 40) }] });
    }
    private static void Projection() {
        var definition = Published();
        Invalid(definition with { InstalledProjection = definition.InstalledProjection! with { ManifestGeneration = 2 } });
        Invalid(definition with { InstalledProjection = definition.InstalledProjection! with { BoundFingerprint = Hash('9') } });
        Invalid(definition with { Origin = definition.Origin with { OriginalName = "Changed.zip" } });
    }
    private static void HistoricalEnvironment() {
        var old = Published();
        const string historicalFingerprint = "863f0acf400e957d0cb467302097b105a7108eb1ec5d076727439a235f1f99bb";
        Require(old.InstalledProjection!.BoundFingerprint == historicalFingerprint,"Historical binding protocol changed when the optional environment was absent.");
        string json=MmcDefinitionCodec.Serialize(old);
        Require(!json.Contains("runtimeEnvironment",StringComparison.Ordinal),"An absent historical environment was silently written into its JSON.");
        var read=MmcDefinitionCodec.Read(json);
        Require(read.Status==MmcDefinitionReadStatus.Valid && read.Definition!.InstalledProjection!.RuntimeEnvironment is null &&
            read.Definition.InstalledProjection.BoundFingerprint==historicalFingerprint && MmcDefinitionCodec.Serialize(read.Definition)==json,
            "Historical no-environment schema-1 JSON/fingerprint failed its exact roundtrip.");
    }
    private static void PublishedEnvironment() {
        var environment=new MmcRuntimeEnvironment("windows","x86_64",21,64,"10.0.22631");
        var definition=WithEnvironment(environment);
        string json=MmcDefinitionCodec.Serialize(definition);
        var read=MmcDefinitionCodec.Read(json);
        Require(read.Status==MmcDefinitionReadStatus.Valid && read.Definition!.InstalledProjection!.RuntimeEnvironment==environment,
            "Published environment dimensions were omitted or normalized during persistence.");
        var changed=new MmcRuntimeEnvironment?[] { null, environment with { OperatingSystem="linux" },environment with { Architecture="arm64" },
            environment with { JavaMajor=17 },environment with { Architecture="x86",JavaBitness=32 },environment with { OsVersion=null },environment with { OsVersion="10.0.26100" } };
        foreach(var value in changed) {
            var tampered=definition with { InstalledProjection=definition.InstalledProjection! with { RuntimeEnvironment=value } };
            Require(MmcDefinitionCodec.ComputeBoundFingerprint(tampered)!=definition.InstalledProjection!.BoundFingerprint,"A published environment dimension was outside its binding fingerprint.");
            Invalid(tampered);
        }
        foreach(var invalid in new[] { environment with { OperatingSystem="Windows" },environment with { Architecture="amd64" },
            environment with { JavaMajor=0 },environment with { JavaMajor=-1 },environment with { JavaBitness=16 },environment with { JavaBitness=32 },
            environment with { OsVersion="" },environment with { OsVersion=" " },environment with { OsVersion="10\n0" },environment with { OsVersion=new string('a',257) } })
            Invalid(WithEnvironment(invalid));
        foreach(string required in new[]{"operatingSystem","architecture","javaMajor","javaBitness"}) {
            var broken=JsonNode.Parse(json)!.AsObject();broken["installedProjection"]!["runtimeEnvironment"]!.AsObject().Remove(required);
            Require(MmcDefinitionCodec.Read(broken.ToJsonString()).Status==MmcDefinitionReadStatus.Invalid,"Missing explicit environment field was silently defaulted: "+required);
        }
        var extra=JsonNode.Parse(json)!.AsObject();extra["installedProjection"]!["runtimeEnvironment"]!["javaPath"]="not-a-runtime-descriptor";
        Require(MmcDefinitionCodec.Read(extra.ToJsonString()).Status==MmcDefinitionReadStatus.Invalid,"An unknown runtime path field entered the structural definition.");
        Require(MmcDefinitionCodec.Read(json.Replace("\"javaMajor\":21","\"javaMajor\":21,\"javaMajor\":21",StringComparison.Ordinal)).Status==MmcDefinitionReadStatus.Invalid,
            "Duplicate environment dimensions were accepted.");
    }
    private static MmcInstanceDefinition WithEnvironment(MmcRuntimeEnvironment environment) {
        var value=Published();value=value with { InstalledProjection=value.InstalledProjection! with { RuntimeEnvironment=environment } };
        return value with { InstalledProjection=value.InstalledProjection! with { BoundFingerprint=MmcDefinitionCodec.ComputeBoundFingerprint(value) } };
    }
    private static void Budgets() {
        string json = MmcDefinitionCodec.Serialize(Published());
        Require(MmcDefinitionCodec.Read(json, new() { MaxMetadataBytes = 10 }).Status == MmcDefinitionReadStatus.Invalid, "Definition byte budget ignored.");
        var draft = Draft() with { ComponentOverrides = [new("one"), new("two")] };
        Require(!MmcDefinitionCodec.Validate(draft, MmcDefinitionValidationMode.Draft, new() { MaxComponents = 1 }).IsEmpty, "Array count budget ignored.");
        var bundle = Draft();
        var external = bundle with { AuthorFileBaseline = new(Hash('7'), "PCL/MmcSource/" + bundle.SourceBundle.BundleSha256 + "/author-files.json", 100000) };
        Require(MmcDefinitionCodec.Validate(external, MmcDefinitionValidationMode.Draft).IsEmpty, "Large author-file list envelope must remain external.");
        Require(!MmcDefinitionCodec.Validate(external with { AuthorFileBaseline = external.AuthorFileBaseline! with { EntryCount = 100001 } }, MmcDefinitionValidationMode.Draft).IsEmpty, "External list count budget ignored.");
    }
    private static void AssetIndexMembership() {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("{\"objects\":{\"sounds/a.ogg\":{\"hash\":\"" + new string('a', 40) + "\",\"size\":5}},\"virtual\":true}");
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var result = MmcAssetIndexReader.Read(new(Hash('4'), content), digest);
        Require(!result.HasErrors && result.Index is { Virtual: true } && result.Index.Objects is [{ Name: "sounds/a.ogg", SizeBytes: 5 }], "Verified asset-index membership was lost.");
        Require(MmcAssetIndexReader.Read(new(Hash('4'), content), Hash('0')).HasErrors, "Changed raw asset index was accepted under the old parent digest.");
        byte[] unsafeContent = System.Text.Encoding.UTF8.GetBytes("{\"objects\":{\"../escape\":{\"hash\":\"" + new string('a', 40) + "\",\"size\":5}}}");
        string unsafeDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(unsafeContent));
        Require(MmcAssetIndexReader.Read(new(Hash('4'), unsafeContent), unsafeDigest).HasErrors, "Unsafe asset logical name accepted.");
        Require(MmcAssetIndexReader.Read(new(Hash('4'), content), digest, new() { MaxMetadataBytes = 10 }).HasErrors, "Asset-index byte budget ignored.");
    }
    private static void DerivedProtocols() {
        var source = new MmcFieldSource("fixture", "1", "package:patches/fixture.json", Hash('6'), "libraries", 0);
        var artifact = new MmcArtifactReference(Hash('4'), "fixture", Hash('6'), MmcArtifactRole.Native, MmcArtifactScope.Shared,
            new("fixture", "native", "1", "", "jar"), "fixture/native/1/native.jar", [], null, null, 10, "natives-windows-64", ["META-INF/"], source);
        MmcArtifactDigestFact fact = new(artifact.Key, artifact.SourceRevision, Hash('5'), 10);
        string original = MmcDerivedProofs.ComputeNativeSourceSetFingerprint([artifact], [fact]);
        string repeated = MmcDerivedProofs.ComputeNativeSourceSetFingerprint([artifact], [fact]);
        string changed = MmcDerivedProofs.ComputeNativeSourceSetFingerprint([artifact with { ExtractExcludes = [] }], [fact]);
        Require(original == repeated && original != changed, "Native input/exclusion fingerprint lost determinism.");
        Require(MmcDerivedProofs.ComputeDerivedContextFingerprint(Hash('1'), original, "bounded-native-v1") != MmcDerivedProofs.ComputeDerivedContextFingerprint(Hash('2'), original, "bounded-native-v1"), "Derived context omitted runtime plan identity.");
    }
    private static void DerivedPersistence() {
        var draft = WithArtifact();
        string view = Hash('9'), sourceSet = Hash('8'), runtime = Hash('3');
        var parentInventory = draft.ArtifactInventory[0] with { Role = MmcArtifactRole.Native, Scope = MmcArtifactScope.Shared, RelativePath = "fixture/native.jar" };
        var parent = draft.ArtifactLocks[0] with { TargetRelativePath = "PCL/MmcRuntime/" + view + "/libraries/" + parentInventory.RelativePath };
        var output = new MmcDerivedOutputRecord(MmcDerivedOutputKind.NativeFile, parent.ArtifactKey, parent.AdoptedSha256, "bounded-native-v1", sourceSet, runtime,
            MmcDerivedProofs.ComputeDerivedContextFingerprint(runtime, sourceSet, "bounded-native-v1"), view, "native.dll", "PCL/MmcRuntime/" + view + "/natives/native.dll", Hash('7'), 2);
        var definition = Published(draft with { ArtifactInventory = [parentInventory], ArtifactLocks = [parent], DerivedOutputs = [output] });
        Require(MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(definition)).Status == MmcDefinitionReadStatus.Valid, "Native output authority did not roundtrip.");
        Require(!MmcDefinitionCodec.Validate(definition with { DerivedOutputs = [output with { ParentSha256 = Hash('0') }] }, MmcDefinitionValidationMode.Draft).IsEmpty, "Native output detached from its parent lock.");
        Require(!MmcDefinitionCodec.Validate(definition with { DerivedOutputs = [output with { TargetRelativePath = "libraries/native.dll" }] }, MmcDefinitionValidationMode.Draft).IsEmpty, "Native output escaped its runtime view.");
    }
}
