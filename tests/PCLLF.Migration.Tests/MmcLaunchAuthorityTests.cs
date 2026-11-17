using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLLF.MMCC.Core;

internal static class MmcLaunchAuthorityTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC binder publishes a new immutable definition and retains exact input digests", InitialBinding);
        yield return ("MMC binder requires all active facts source objects and declared checksum evidence", BindingRejections);
        yield return ("MMC binder prevents existing adopted keys from changing bytes during repair or adoption", ImmutableAdoption);
        yield return ("MMC binder requires explicit new generations for new variants", Generations);
        yield return ("MMC binder binds native output batches to locked parents exclusions and context", NativeBinding);
        yield return ("MMC binder validates dynamic asset membership against raw adopted index bytes", AssetBinding);
        yield return ("MMC binder rejects arbitrary extra facts unowned targets and prior-lock omissions", Ownership);
        yield return ("MMC aggregate launch proof accepts a sourced local final entry while retaining target-specific heuristics", AggregateAuthority);
        yield return ("MMC aggregate launch proof remains conservative for missing class locks candidates or bound agents", ConservativeAuthority);
        yield return ("MMC aggregate launch proof follows a bound higher-version library replacement without requiring the obsolete JAR", SupersededLibraryAuthority);
        yield return ("MMC aggregate proof independently requires raw final game tweakers and every ordered absence observation", RawGameTweakerAuthority);
        yield return ("MMC aggregate proof includes host extra required classes without weakening plan requirements", HostClassAuthority);
        yield return ("MMC aggregate proof uses actual user-before-component JVM system-loader precedence", SystemLoaderPrecedence);
        yield return ("MMC binder preserves published baseline authority across explicit candidate drafts", PublishedAuthority);
        yield return ("MMC execution fingerprints reject stale labels for arguments classpath order and Java policy", PlanFingerprints);
        yield return ("MMC binder follows virtual-first asset layout without unrequested resource writes", BothAssetLayouts);
        yield return ("MMC binder records exact published environments and migrates old bindings only in a new generation", PublishedEnvironment);
        yield return ("MMC clone binding derives generation one from a selected generation three without losing historical locks", CloneAuthority);
        yield return ("MMC clone binding rejects changed source identity plans destinations and measured contents", CloneRejections);
        yield return ("MMC clone binding retains old environment authority and requires current native observations", CloneNative);
        yield return ("MMC clone binding revalidates asset membership rather than copying arbitrary child authority", CloneAssets);
    }
    private sealed record Fixture(MmcCompiledProfile Plan, MmcInstanceDefinition Definition,
        ImmutableArray<MmcMaterializedArtifactFact> Facts, MmcResolvedProfile Sources, MmcAssetIndexInput? AssetIndex = null);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string View => new('9', 64);
    private static Fixture Create(bool native = false, string? assetJson = null, bool mavenDuplicates = false, bool declaredJava = false, bool lateEntrypoint = false, bool storedOverride = false, bool supersededLibrary = false,string? rawGame = null) {
        Dictionary<string, object?> patch = new() {
            ["uid"] = "net.minecraft", ["version"] = "1", ["mainClass"] = "fixture.Main",
            ["mainJar"] = new Dictionary<string, object?> { ["name"] = "fixture:main:1:client", ["MMC-hint"] = "local" },
            ["libraries"] = new object[] { new Dictionary<string, object?> { ["name"] = "fixture:library:1", ["MMC-hint"] = "local" } }
        };
        if (supersededLibrary) patch["libraries"] = ((object[])patch["libraries"]!).Append(new { name = "fixture:shared:1" }).ToArray();
        if (native) patch["+libraries"] = new[] { new { name = "fixture:native:1", natives = new { windows = "native-64" }, extract = new { exclude = new[] { "META-INF/" } } } };
        if (assetJson is not null) patch["assetIndex"] = new { id = "fixture", url = "https://example.invalid/fixture.json" };
        if (mavenDuplicates) patch["mavenFiles"] = new[] { new { name = "fixture:tool:1" }, new { name = "fixture:tool:1" } };
        if (declaredJava) patch["compatibleJavaMajors"] = new[] { 17, 21 };
        if(rawGame is not null)patch["minecraftArguments"]=rawGame;
        string manifest = lateEntrypoint ? "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1\"},{\"uid\":\"bootstrap\",\"version\":\"1\"}]}" : "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1\"}]}";
        string component = JsonSerializer.Serialize(patch);
        Dictionary<string, string> content = new() { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = component };
        if (lateEntrypoint) content["patches/bootstrap.json"] = "{\"uid\":\"bootstrap\",\"version\":\"1\",\"mainClass\":\"fixture.Boot\",\"+jvmArgs\":[\"-Djava.system.class.loader=fixture.Loader\"],\"+tweakers\":[\"fixture.Tweaker\"]}";
        if (supersededLibrary) {
            var boot = System.Text.Json.Nodes.JsonNode.Parse(content["patches/bootstrap.json"])!.AsObject();
            boot["libraries"] = JsonSerializer.SerializeToNode(new[] { new { name = "fixture:shared:2" } });
            content["patches/bootstrap.json"] = boot.ToJsonString();
        }
        var parsed = MmcPackageReader.Parse(new(content.Select(c => new MmcArchiveEntry(c.Key)).ToArray(), content));
        var resolved = MmcComponentResolver.Resolve(parsed, [],overrides:storedOverride ? [new("net.minecraft",Enabled:true)] : []);
        Require(resolved.IsComplete, "Fixture resolution failed.");
        var compiled = MmcProfileCompiler.Compile(resolved.Profile!, new("windows", "amd64", 17));
        Require(!compiled.HasErrors && compiled.Profile is not null, "Fixture compilation failed.");
        var plan = compiled.Profile!;
        var draft = MmcDefinitionTests.Draft(plan.SourceFingerprint);
        string prefix = "PCL/MmcSource/" + draft.SourceBundle.BundleSha256 + "/";
        var metadataRecords = ImmutableArray.CreateBuilder<MmcSourceMetadataRecord>();
        metadataRecords.Add(new("mmc-pack.json", Hash(manifest), prefix + "mmc-pack.json", MmcSourceRecordKind.Manifest));
        foreach (var source in resolved.Profile!.Components) metadataRecords.Add(new("patches/" + source.Uid + ".json", source.Patch.ContentSha256, prefix + "patches/" + source.Uid + ".json",
            MmcSourceRecordKind.Component, source.Uid, source.Version, MmcComponentSourceKind.PackageLocal, "package:patches/" + source.Uid + ".json"));
        draft = draft with { SourceBundle = draft.SourceBundle with { MetadataRecords = metadataRecords.ToImmutable() } };
        if (storedOverride) draft=draft with { ComponentOverrides=[new("net.minecraft",Enabled:true)] };
        var artifacts = plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).GroupBy(a => a.Key).Select(g => g.First()).ToArray();
        var facts = artifacts.Select(a => new MmcMaterializedArtifactFact(a.Key, a.SourceRevision,
            a.Role == MmcArtifactRole.AssetIndex ? Hash(assetJson!) : Hash("verified fixture bytes:" + a.Key),
            a.Role == MmcArtifactRole.AssetIndex ? Encoding.UTF8.GetByteCount(assetJson!) : 10,
            "PCL/MmcArtifacts/sha256/" + (a.Role == MmcArtifactRole.AssetIndex ? Hash(assetJson!) : Hash("verified fixture bytes:" + a.Key)),
            a.Scope == MmcArtifactScope.InstanceLocal ? "libraries/" + a.RelativePath : "PCL/MmcRuntime/" + View + "/" + (a.Role == MmcArtifactRole.AssetIndex ? "assets/" : a.Role == MmcArtifactRole.LoggingConfiguration ? "" : "libraries/") + a.RelativePath,
            a.Sha1)).ToImmutableArray();
        return new(plan, draft, facts, resolved.Profile!, assetJson is null ? null : new(plan.Assets.Artifact!.Key, Encoding.UTF8.GetBytes(assetJson)));
    }
    private static MmcBindOptions Options(long expected = 1, long next = 1, MmcBindingIntent intent = MmcBindingIntent.InitialImport) => new(expected, next, intent, Hash("projection"), next);
    private static MmcBindingResult Bind(Fixture fixture, MmcInstanceDefinition? definition = null, IReadOnlyList<MmcMaterializedArtifactFact>? facts = null,
        MmcBindOptions? options = null, IReadOnlyList<MmcDerivedOutputRecord>? derived = null) =>
        MmcPlanBinder.Bind(definition ?? fixture.Definition, fixture.Plan, facts ?? fixture.Facts, options ?? Options(), derivedOutputs: derived,
            assetIndexes: fixture.AssetIndex is null ? [] : [fixture.AssetIndex]);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static MmcBindingResult Good(MmcBindingResult value) {
        Require(!value.HasErrors && value.Definition is not null && value.Plan is not null, string.Join(";", value.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return value;
    }
    private static void Error(MmcBindingResult value, string code) => Require(value.HasErrors && value.Diagnostics.Any(d => d.Code == code), "Missing " + code + ": " + string.Join(",", value.Diagnostics.Select(d => d.Code)));
    private static void InitialBinding() {
        var fixture = Create(mavenDuplicates: true);
        var result = Good(Bind(fixture));
        Require(fixture.Definition.ArtifactLocks.IsEmpty && fixture.Definition.InstalledProjection is null && result.Definition!.ArtifactLocks.Length == fixture.Facts.Length,
            "Binding mutated input definition or lost duplicated download-only semantics.");
        Require(MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(result.Definition!)).Status == MmcDefinitionReadStatus.Valid && result.Plan!.BoundFingerprint == result.Definition!.InstalledProjection!.BoundFingerprint, "Bound projection did not persist coherently.");
    }
    private static void PublishedEnvironment() {
        var fixture=Create();
        var environment=fixture.Plan.Environment with { OsVersion="10.0.22631" };
        var compilation=MmcProfileCompiler.Compile(fixture.Sources,environment);
        Require(!compilation.HasErrors && compilation.Profile is not null,"Explicit environment fixture failed compilation.");
        fixture=fixture with { Plan=compilation.Profile! };
        var initial=Good(Bind(fixture)).Definition!;
        Require(initial.InstalledProjection!.RuntimeEnvironment==environment,"New publication did not save every exact environment dimension.");
        var old=initial with { InstalledProjection=initial.InstalledProjection with { RuntimeEnvironment=null } };
        old=old with { InstalledProjection=old.InstalledProjection! with { BoundFingerprint=MmcDefinitionCodec.ComputeBoundFingerprint(old) } };
        Require(MmcDefinitionCodec.Validate(old).IsEmpty,"Historical binding fixture was not valid.");
        string before=MmcDefinitionCodec.Serialize(old);
        var same=Good(Bind(fixture,old,options:Options(intent:MmcBindingIntent.RepairExisting))).Definition!;
        Require(same.InstalledProjection!.RuntimeEnvironment is null && same.InstalledProjection.BoundFingerprint==old.InstalledProjection!.BoundFingerprint &&
            MmcDefinitionCodec.Serialize(old)==before,"Read-only historical rebind silently changed its environment authority or input definition.");
        var upgraded=Good(Bind(fixture,old,options:Options(next:2,intent:MmcBindingIntent.RepairExisting))).Definition!;
        Require(upgraded.Generation==2 && upgraded.InstalledProjection!.RuntimeEnvironment==environment &&
            upgraded.InstalledProjection.BoundFingerprint!=old.InstalledProjection!.BoundFingerprint && MmcDefinitionCodec.Serialize(old)==before,
            "Explicit new-generation repair did not migrate its exact environment while retaining immutable old authority.");
        var repeat=Good(Bind(fixture,upgraded,options:Options(expected:2,next:2,intent:MmcBindingIntent.RepairExisting))).Definition!;
        Require(repeat.InstalledProjection!.RuntimeEnvironment==environment && repeat.InstalledProjection.BoundFingerprint==upgraded.InstalledProjection!.BoundFingerprint,
            "Same-generation repair of a recorded environment changed its binding.");
        var unknown=MmcProfileCompiler.Compile(fixture.Sources,environment with { JavaMajor=0 });
        Require(!unknown.HasErrors && unknown.Profile is not null,"Compiler must remain usable for an unselected Java analysis profile.");
        Require(Bind(fixture with { Plan=unknown.Profile! }).HasErrors,"An unselected Java major was published as an exact runtime environment.");
    }
    private static MmcCloneBindOptions CloneOptions(MmcInstanceDefinition source) =>
        new(source.InstanceId,source.Generation,source.InstalledProjection!.BoundFingerprint,Guid.Parse("2f5656be-ec81-44d4-b4a7-eeb46ebaf7b1"),Hash("clone projection"));
    private static void CloneAuthority() {
        var fixture=Create(declaredJava:true,storedOverride:true);
        var first=Good(Bind(fixture)).Definition!;
        var variant=MmcProfileCompiler.Compile(fixture.Sources,new("windows","x86",17,32));
        Require(!variant.HasErrors && variant.Profile is not null,"Historical variant fixture failed compile.");
        var variantFacts=variant.Profile!.Classpath.Select(artifact=>fixture.Facts.Single(f=>f.TargetRelativePath=="libraries/"+artifact.RelativePath) with {
            ArtifactKey=artifact.Key,SourceRevision=artifact.SourceRevision
        }).ToImmutableArray();
        var second=Good(Bind(fixture with { Plan=variant.Profile,Facts=variantFacts },first,options:Options(next:2,intent:MmcBindingIntent.AdoptDeclaredVariant))).Definition!;
        var source=Good(Bind(fixture,second,options:Options(expected:2,next:3,intent:MmcBindingIntent.AdoptDeclaredVariant))).Definition!;
        Require(source.Generation==3 && source.ArtifactLocks.Length>fixture.Facts.Length,"Source fixture did not retain inactive historical variant locks.");
        string before=MmcDefinitionCodec.Serialize(source);
        var clone=Good(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,CloneOptions(source))).Definition!;
        Require(clone.InstanceId!=source.InstanceId && clone.ParentInstanceId==source.InstanceId && clone.Generation==1 && clone.InstalledProjection!.ManifestGeneration==1,
            "Clone identity/generation/parent did not derive from its selected published source.");
        Require(clone.Origin==source.Origin && clone.SourceBundle==source.SourceBundle && clone.Layout==source.Layout && clone.AuthorFileBaseline==source.AuthorFileBaseline &&
            clone.ComponentOverrides.SequenceEqual(source.ComponentOverrides) && clone.ArtifactInventory.SequenceEqual(source.ArtifactInventory) &&
            clone.ArtifactLocks.SequenceEqual(source.ArtifactLocks) && clone.PackageFileInventory.SequenceEqual(source.PackageFileInventory) &&
            clone.PackageFileLocks.SequenceEqual(source.PackageFileLocks) && clone.DerivedOutputs.SequenceEqual(source.DerivedOutputs),
            "Clone dropped or changed original references, overrides or historical adopted authority.");
        Require(clone.InstalledProjection!.RuntimeEnvironment==fixture.Plan.Environment && clone.InstalledProjection.RuntimePlanFingerprint==source.InstalledProjection!.RuntimePlanFingerprint &&
            clone.InstalledProjection.OutputJsonSha256==Hash("clone projection") && clone.InstalledProjection.BoundFingerprint!=source.InstalledProjection.BoundFingerprint &&
            MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(clone)).Status==MmcDefinitionReadStatus.Valid && MmcDefinitionCodec.Serialize(source)==before,
            "Clone failed its new structural binding or mutated the selected source.");
    }
    private static void CloneRejections() {
        var fixture=Create();var source=Good(Bind(fixture)).Definition!;var options=CloneOptions(source);
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { ExpectedSourceInstanceId=Guid.NewGuid() }),"MMC_CLONE_SOURCE");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { ExpectedSourceGeneration=2 }),"MMC_CLONE_SOURCE");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { ExpectedSourceBoundFingerprint=Hash("wrong source") }),"MMC_CLONE_SOURCE");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { NewInstanceId=source.InstanceId }),"MMC_CLONE_IDENTITY");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { NewInstanceId=Guid.Empty }),"MMC_CLONE_IDENTITY");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan with { MainClass="fixture.Unbound" },fixture.Facts,options),"MMC_CLONE_PLAN");
        var other=MmcProfileCompiler.Compile(fixture.Sources,fixture.Plan.Environment with { JavaMajor=21 }).Profile!;
        Error(MmcPlanBinder.BindClone(source,other,fixture.Facts,options),"MMC_CLONE_PLAN");
        var changed=fixture.Facts[0] with { ObservedSha256=Hash("different bytes"),RecoveryObjectRelativePath="PCL/MmcArtifacts/sha256/"+Hash("different bytes") };
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts.SetItem(0,changed),options),"MMC_BIND_REBIND");
        Require(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts.SetItem(0,fixture.Facts[0] with { SizeBytes=11 }),options).HasErrors,"Clone changed an adopted artifact size.");
        Require(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts.SetItem(0,fixture.Facts[0] with { TargetRelativePath="libraries/moved.jar" }),options).HasErrors,"Clone moved an active owned target.");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts.Skip(1).ToArray(),options),"MMC_BIND_FACT_MISSING");
        Require(MmcPlanBinder.BindClone(source with { Generation=2 },fixture.Plan,fixture.Facts,options).HasErrors,"Clone accepted an invalid published source.");
    }
    private static void CloneNative() {
        var fixture=Create(native:true);var artifact=fixture.Plan.NativeArtifacts[0];var parent=fixture.Facts.Single(f=>f.ArtifactKey==artifact.Key);
        string sources=MmcDerivedProofs.ComputeNativeSourceSetFingerprint(fixture.Plan.NativeArtifacts,fixture.Facts.Select(f=>new MmcArtifactDigestFact(f.ArtifactKey,f.SourceRevision,f.ObservedSha256,f.SizeBytes)).ToArray());
        var completion=new MmcDerivationCompletion("bounded-native-v1",sources,fixture.Plan.RuntimePlanFingerprint,View,1,true);
        var output=new MmcDerivedOutputRecord(MmcDerivedOutputKind.NativeFile,parent.ArtifactKey,parent.ObservedSha256,"bounded-native-v1",sources,fixture.Plan.RuntimePlanFingerprint,
            MmcDerivedProofs.ComputeDerivedContextFingerprint(fixture.Plan.RuntimePlanFingerprint,sources,"bounded-native-v1"),View,"fixture.dll","PCL/MmcRuntime/"+View+"/natives/fixture.dll",Hash("native bytes"),8);
        var source=Good(Bind(fixture,options:Options() with { NativeDerivation=completion },derived:[output])).Definition!;
        source=source with { InstalledProjection=source.InstalledProjection! with { RuntimeEnvironment=null } };
        source=source with { InstalledProjection=source.InstalledProjection! with { BoundFingerprint=MmcDefinitionCodec.ComputeBoundFingerprint(source) } };
        string before=MmcDefinitionCodec.Serialize(source);
        var options=CloneOptions(source) with { NativeDerivation=completion };
        var clone=Good(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options,derivedOutputs:[output])).Definition!;
        Require(clone.InstalledProjection!.RuntimeEnvironment==fixture.Plan.Environment && clone.DerivedOutputs.SequenceEqual(source.DerivedOutputs) &&
            source.InstalledProjection!.RuntimeEnvironment is null && MmcDefinitionCodec.Serialize(source)==before,"Clone lost current native authority or silently migrated its old source.");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options with { NativeDerivation=completion with { OutputCount=0 } },derivedOutputs:[]),"MMC_CLONE_DERIVED_MISSING");
        Require(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,options,derivedOutputs:[output with { Sha256=Hash("wrong native") }]).HasErrors,"Clone accepted different extracted native bytes.");
    }
    private static void CloneAssets() {
        string sha1=Convert.ToHexStringLower(SHA1.HashData([1,2,3])),sha256=Convert.ToHexStringLower(SHA256.HashData([1,2,3]));
        var fixture=Create(assetJson:"{\"objects\":{\"sound/test.ogg\":{\"hash\":\""+sha1+"\",\"size\":3}}}");
        var parent=fixture.Facts.Single(f=>f.ArtifactKey==fixture.Plan.Assets.Artifact!.Key);
        string key=MmcDerivedProofs.ComputeAssetObjectKey(parent.ArtifactKey,parent.ObservedSha256,sha1,3);
        var fact=new MmcMaterializedArtifactFact(key,parent.ObservedSha256,sha256,3,"PCL/MmcArtifacts/sha256/"+sha256,"PCL/MmcRuntime/"+View+"/assets/objects/"+sha1[..2]+"/"+sha1,sha1);
        var facts=fixture.Facts.Add(fact);var source=Good(Bind(fixture,facts:facts)).Definition!;
        var clone=Good(MmcPlanBinder.BindClone(source,fixture.Plan,facts,CloneOptions(source),assetIndexes:[fixture.AssetIndex!])).Definition!;
        Require(clone.DerivedOutputs.SequenceEqual(source.DerivedOutputs),"Clone lost indexed asset-object originals or digests.");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,fixture.Facts,CloneOptions(source),assetIndexes:[fixture.AssetIndex!]),"MMC_BIND_ASSET_OBJECT");
        Error(MmcPlanBinder.BindClone(source,fixture.Plan,facts,CloneOptions(source),assetIndexes:[]),"MMC_BIND_ASSET_INDEX");
    }
    private static void BindingRejections() {
        var fixture = Create();
        Error(Bind(fixture, facts: fixture.Facts.Skip(1).ToArray()), "MMC_BIND_FACT_MISSING");
        Error(Bind(fixture, definition: fixture.Definition with { SourceBundle = fixture.Definition.SourceBundle with { MetadataRecords = [fixture.Definition.SourceBundle.MetadataRecords[0]] } }), "MMC_BIND_SOURCE");
        Error(Bind(fixture, facts: fixture.Facts.SetItem(0, fixture.Facts[0] with { SourceRevision = new string('0', 64) })), "MMC_BIND_DIGEST");
    }
    private static void ImmutableAdoption() {
        var fixture = Create();
        var original = Good(Bind(fixture)).Definition!;
        var changed = fixture.Facts[0] with { ObservedSha256 = Hash("different bytes"), RecoveryObjectRelativePath = "PCL/MmcArtifacts/sha256/" + Hash("different bytes") };
        Error(Bind(fixture, original, fixture.Facts.SetItem(0, changed), Options(intent: MmcBindingIntent.RepairExisting)), "MMC_BIND_REBIND");
        Error(Bind(fixture, original, fixture.Facts.SetItem(0, changed), Options(next: 2, intent: MmcBindingIntent.AdoptDeclaredVariant)), "MMC_BIND_REBIND");
        Good(Bind(fixture, original, options: Options(intent: MmcBindingIntent.RepairExisting)));
    }
    private static void Generations() {
        var fixture = Create();
        Error(Bind(fixture, options: Options(next: 2)), "MMC_BIND_GENERATION");
        Error(Bind(fixture, options: Options(intent: MmcBindingIntent.RepairExisting)), "MMC_BIND_AUTHORITY");
        var original = Good(Bind(fixture)).Definition!;
        var adopted = Good(MmcPlanBinder.Bind(original with { InstalledProjection = null }, fixture.Plan, fixture.Facts,
            Options(next: 2, intent: MmcBindingIntent.AdoptDeclaredVariant), publishedBaseline: original));
        Require(adopted.Definition!.Generation == 2, "Explicit adoption did not advance generation.");
    }
    private static void NativeBinding() {
        var fixture = Create(native: true);
        Error(Bind(fixture), "MMC_BIND_NATIVE_INCOMPLETE");
        var artifact = fixture.Plan.NativeArtifacts[0]; var parent = fixture.Facts.Single(f => f.ArtifactKey == artifact.Key);
        string sources = MmcDerivedProofs.ComputeNativeSourceSetFingerprint(fixture.Plan.NativeArtifacts,
            fixture.Facts.Select(f => new MmcArtifactDigestFact(f.ArtifactKey, f.SourceRevision, f.ObservedSha256, f.SizeBytes)).ToArray());
        var completion = new MmcDerivationCompletion("bounded-native-v1", sources, fixture.Plan.RuntimePlanFingerprint, View, 1, true);
        var output = new MmcDerivedOutputRecord(MmcDerivedOutputKind.NativeFile, parent.ArtifactKey, parent.ObservedSha256, "bounded-native-v1", sources, fixture.Plan.RuntimePlanFingerprint,
            MmcDerivedProofs.ComputeDerivedContextFingerprint(fixture.Plan.RuntimePlanFingerprint, sources, "bounded-native-v1"), View,
            "fixture.dll", "PCL/MmcRuntime/" + View + "/natives/fixture.dll", Hash("dll bytes"), 8);
        var result = Good(Bind(fixture, options: Options() with { NativeDerivation = completion }, derived: [output]));
        Require(result.Definition!.DerivedOutputs.Length == 1, "Native output authority missing.");
        Error(Bind(fixture, options: Options() with { NativeDerivation = completion }, derived: [output with { ParentSha256 = Hash("wrong") }]), "MMC_STRUCTURE");
        Error(Bind(fixture, options: Options() with { NativeDerivation = completion with { SourceSetFingerprint = Hash("wrong") } }, derived: [output]), "MMC_BIND_DERIVED_CONTEXT");
    }
    private static void AssetBinding() {
        string objectSha1 = Convert.ToHexStringLower(SHA1.HashData([1, 2, 3]));
        string objectSha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        string json = "{\"objects\":{\"sound/test.ogg\":{\"hash\":\"" + objectSha1 + "\",\"size\":3}}}";
        var fixture = Create(assetJson: json);
        var parent = fixture.Facts.Single(f => f.ArtifactKey == fixture.Plan.Assets.Artifact!.Key);
        string key = MmcDerivedProofs.ComputeAssetObjectKey(parent.ArtifactKey, parent.ObservedSha256, objectSha1, 3);
        var objectFact = new MmcMaterializedArtifactFact(key, parent.ObservedSha256, objectSha256, 3,
            "PCL/MmcArtifacts/sha256/" + objectSha256, "PCL/MmcRuntime/" + View + "/assets/objects/" + objectSha1[..2] + "/" + objectSha1, objectSha1);
        var result = Good(Bind(fixture, facts: fixture.Facts.Add(objectFact)));
        Require(result.Definition!.DerivedOutputs is [{ Kind: MmcDerivedOutputKind.AssetObject }] && result.Definition.ArtifactLocks.Length == fixture.Facts.Length, "Dynamic asset object was not parent-bound independently of static inputs.");
        Error(Bind(fixture), "MMC_BIND_ASSET_OBJECT");
        Error(Bind(fixture, facts: fixture.Facts.Add(objectFact with { ArtifactKey = "PCLLF:asset-object:v1:" + Hash("unlisted") })), "MMC_BIND_ASSET_OBJECT");
        var changedInput = fixture with { AssetIndex = fixture.AssetIndex! with { Content = Encoding.UTF8.GetBytes("{\"objects\":{}}") } };
        Error(Bind(changedInput, facts: fixture.Facts.Add(objectFact)), "MMC_BIND_ASSET_INDEX");
        Good(Bind(fixture, result.Definition, fixture.Facts.Add(objectFact), Options(intent: MmcBindingIntent.RepairExisting)));
        var damagedJson = System.Text.Json.Nodes.JsonNode.Parse(MmcDefinitionCodec.Serialize(result.Definition!))!.AsObject();
        var derived = damagedJson["derivedOutputs"]!.AsArray();
        var second = derived[0]!.DeepClone().AsObject();
        derived[0]!["sha256"] = null;
        second["viewKey"] = Hash("another view"); second["runtimePlanFingerprint"] = Hash("another runtime");
        second["contextFingerprint"] = MmcDerivedProofs.ComputeDerivedContextFingerprint(Hash("another runtime"), second["sourceSetFingerprint"]!.GetValue<string>(), "bounded-assets-v1");
        second["targetRelativePath"] = "PCL/MmcRuntime/" + Hash("another view") + "/assets/" + second["relativePath"]!.GetValue<string>();
        derived.Add(second);
        Require(MmcDefinitionCodec.Read(damagedJson.ToJsonString()).Status == MmcDefinitionReadStatus.Invalid, "Malformed duplicate derived object threw or escaped as valid.");
    }
    private static void Ownership() {
        var fixture = Create();
        Error(Bind(fixture, facts: fixture.Facts.Add(fixture.Facts[0] with { ArtifactKey = Hash("unlisted") })), "MMC_BIND_UNDECLARED");
        Error(Bind(fixture, facts: fixture.Facts.SetItem(0, fixture.Facts[0] with { TargetRelativePath = "minecraft/libraries/escape.jar" })), "MMC_STRUCTURE");
        var original = Good(Bind(fixture)).Definition!;
        var result = MmcPlanBinder.Bind(original, fixture.Plan, fixture.Facts, Options(intent: MmcBindingIntent.RepairExisting), priorLocks: []);
        Error(result, "MMC_BIND_PRIOR_LOCK");
    }
    private static MmcHostLaunchChain Host() => new(Hash("verified Java identity"), Hash("verified wrapper identity"), Hash("non-sensitive host arguments"));
    private static ImmutableArray<MmcJarClassObservation> ClassObservations(Fixture fixture) {
        string? systemLoader = fixture.Plan.JvmArguments.Select(a => a.Value).LastOrDefault(a => a.StartsWith("-Djava.system.class.loader=", StringComparison.Ordinal));
        string[] names = new[] { fixture.Plan.MainClass }.Concat(systemLoader is null ? [] : new[] { systemLoader["-Djava.system.class.loader=".Length..] }).Concat(fixture.Plan.Tweakers).Distinct().ToArray();
        return fixture.Plan.Classpath.GroupBy(a => a.Key).Select(g => g.First()).Select((artifact, position) => new MmcJarClassObservation(artifact.Key,
            fixture.Facts.Single(f => f.ArtifactKey == artifact.Key).ObservedSha256,
            names.Select(name => new MmcJarClassPresence(name, position == 0, position == 0 ? name.Replace('.', '/') + ".class" : null)).ToImmutableArray())).ToImmutableArray();
    }
    private static void AggregateAuthority() {
        var fixture = Create(declaredJava: true, lateEntrypoint: true);
        var bound = Good(Bind(fixture));
        Require(!fixture.Plan.OverrideEffects.Single(e => e.ComponentUid == "net.minecraft").ReplacesBuiltinLaunchDefinition, "Fixture must exercise a final entry from a later local component.");
        var proof = MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(), ClassObservations(fixture));
        Require(proof.HasAuthority && proof.Retires("net.minecraft", MmcRetiredHeuristicKind.BuiltinMinecraftJavaInference) && !proof.Retires("other", MmcRetiredHeuristicKind.BuiltinMinecraftJavaInference) &&
            !proof.Retires("net.minecraftforge", MmcRetiredHeuristicKind.BuiltinForgeJavaInference) && proof.Proof!.ClassBindings.Length == 3,
            "Aggregate proof lost local final entry/classloader/tweaker structure or widened retired heuristics.");
        var differentJava = MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host() with { JavaIdentitySha256 = Hash("other Java identity") }, ClassObservations(fixture));
        Require(differentJava.HasAuthority && differentJava.Proof!.ProofFingerprint != proof.Proof!.ProofFingerprint, "Authority fingerprint did not bind Java identity.");
    }
    private static void SupersededLibraryAuthority() {
        var fixture = Create(declaredJava: true, lateEntrypoint: true, supersededLibrary: true);
        var bound = Good(Bind(fixture));
        var selected = fixture.Plan.Classpath.Single(value => value.Coordinate?.Artifact == "shared");
        Require(selected.Coordinate!.Version == "2" && !fixture.Plan.Classpath.Any(value => value.Coordinate?.Artifact == "shared" && value.Coordinate.Version == "1"),
            "Fixture did not resolve the ordinary version replacement.");
        var observations = ClassObservations(fixture);
        var proof = MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(), observations);
        Require(proof.HasAuthority && proof.Retires("net.minecraft", MmcRetiredHeuristicKind.BuiltinMinecraftJavaInference),
            "A bound higher-version library replacement still required the obsolete JAR: " + string.Join(";", proof.Diagnostics.Select(value => value.Message)));
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(),
            observations.Where(value => value.ArtifactKey != selected.Key).ToArray()).HasAuthority,
            "Replacement authority omitted its ordered JAR observation.");
        int replacementIndex = Enumerable.Range(0, observations.Length).Single(index => observations[index].ArtifactKey == selected.Key);
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(),
            observations.SetItem(replacementIndex, observations[replacementIndex] with { ArtifactSha256 = Hash("unrelated replacement") })).HasAuthority,
            "Replacement authority accepted different bytes.");
    }
    private static void ConservativeAuthority() {
        var fixture = Create(declaredJava: true);
        var bound = Good(Bind(fixture));
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(), ClassObservations(fixture).RemoveAt(0)).HasAuthority, "Missing ordered class observations authorized heuristic retirement.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host() with { AdditionalJvmArguments = ["-javaagent:unbound.jar"] }, ClassObservations(fixture)).HasAuthority, "Unbound agent chain authorized heuristic retirement.");
        var probes = ClassObservations(fixture);
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host(), probes.SetItem(0, probes[0] with { ArtifactSha256 = Hash("different jar") })).HasAuthority, "Unmatched JAR bytes authorized retirement.");
        var unspecified = Create(); var unknown = Good(Bind(unspecified));
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(unknown.Definition!, unknown.Plan!, unspecified.Sources, Host(), ClassObservations(unspecified)).HasAuthority, "No Java declaration authorized retirement.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan! with { DefinitionGeneration = 2 }, fixture.Sources, Host(), probes).HasAuthority, "Stale generation authorized retirement.");
        foreach (string input in new[] { "@custom.args", "-jar", "-m", "--module=fixture/module", "-p", "--module-path=modules", "-Djava.class.path=unbound", "-Djava.ext.dirs=unbound" })
            Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, Host() with { AdditionalJvmArguments = [input] }, probes).HasAuthority, "Unbound Java execution form authorized retirement: " + input);
    }
    private static ImmutableArray<MmcJarClassObservation> ClassMatrix(Fixture fixture,params string[] classes)=>fixture.Plan.Classpath.Select((artifact,index)=>
        new MmcJarClassObservation(artifact.Key,fixture.Facts.Single(value=>value.ArtifactKey==artifact.Key).ObservedSha256,
            classes.Select(name=>new MmcJarClassPresence(name,index==0,index==0?name.Replace('.','/')+".class":null)).ToImmutableArray())).ToImmutableArray();
    private static void RawGameTweakerAuthority(){
        var fixture=Create(declaredJava:true,rawGame:"--tweakClass fixture.Raw --tweakClass=fixture.Equals");var bound=Good(Bind(fixture));
        Require(fixture.Plan.Tweakers.IsEmpty,"Raw fixture accidentally used declared Tweakers.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),ClassObservations(fixture)).HasAuthority,"Core trusted omitted raw-game tweaker probes.");
        var matrix=ClassMatrix(fixture,"fixture.Main","fixture.Raw","fixture.Equals");
        var proof=MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),matrix);
        Require(proof.HasAuthority&&proof.Proof!.ClassBindings.Select(value=>value.ClassName).SequenceEqual(new[]{"fixture.Main","fixture.Raw","fixture.Equals"}),"Raw final game tweakers were not independently bound in order.");
        var missing=matrix.SetItem(1,matrix[1]with{Classes=matrix[1].Classes.Where(value=>value.ClassName!="fixture.Raw").ToImmutableArray()});
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),missing).HasAuthority,"Missing explicit later-JAR absence authorized a tweaker.");
        var laterPresent=matrix.SetItem(1,matrix[1]with{Classes=matrix[1].Classes.Select(value=>value.ClassName=="fixture.Raw"?value with{Present=true,EntryPath="fixture/Raw.class"}:value).ToImmutableArray()});
        var changedMatrix=MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),laterPresent);
        Require(changedMatrix.HasAuthority&&changedMatrix.Proof!.ClassBindings.SequenceEqual(proof.Proof!.ClassBindings)&&changedMatrix.Proof.ProofFingerprint!=proof.Proof.ProofFingerprint,"Later-JAR class facts were absent from the proof fingerprint.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),matrix.SetItem(0,matrix[0]with{ArtifactSha256=Hash("other JAR bytes")})).HasAuthority,"Raw tweaker proof accepted a different JAR digest.");
        foreach(string raw in new[]{"--tweakClass","--tweakClass=","--tweakClass bad/name"}){
            var bad=Create(declaredJava:true,rawGame:raw);var value=Good(Bind(bad));Require(!MmcLaunchAuthorityEvaluator.Evaluate(value.Definition!,value.Plan!,bad.Sources,Host(),ClassObservations(bad)).HasAuthority,"Malformed raw tweaker gained authority.");
        }
    }
    private static void HostClassAuthority(){
        var fixture=Create(declaredJava:true,rawGame:"--tweakClass fixture.Required");var bound=Good(Bind(fixture));
        var host=Host()with{AdditionalRequiredClasses=["fixture.HostExtra"]};
        var required=ClassMatrix(fixture,"fixture.Main","fixture.Required");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,host,required).HasAuthority,"Host extra class had no explicit ordered matrix.");
        var all=ClassMatrix(fixture,"fixture.Main","fixture.Required","fixture.HostExtra");var proof=MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,host,all);
        Require(proof.HasAuthority&&proof.Proof!.ClassBindings.Any(value=>value.ClassName=="fixture.HostExtra"),"Host extra class was omitted from authority.");
        var noExtra=MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host(),all);
        Require(noExtra.HasAuthority&&noExtra.Proof!.ProofFingerprint!=proof.Proof!.ProofFingerprint,"Additional class facts did not affect the authority fingerprint.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,host,ClassMatrix(fixture,"fixture.Main","fixture.HostExtra")).HasAuthority,"Host classes replaced the independent plan requirements.");
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!,bound.Plan!,fixture.Sources,Host()with{AdditionalRequiredClasses=Enumerable.Range(0,257).Select(index=>"fixture.TooMany"+index).ToImmutableArray()},all).HasAuthority,"Unbounded host class set was accepted.");
    }
    private static void SystemLoaderPrecedence() {
        var fixture = Create(declaredJava: true, lateEntrypoint: true);
        var bound = Good(Bind(fixture));
        var host = Host() with { AdditionalJvmArguments = ["-Djava.system.class.loader=fixture.OverriddenUserLoader"] };
        var proof = MmcLaunchAuthorityEvaluator.Evaluate(bound.Definition!, bound.Plan!, fixture.Sources, host, ClassObservations(fixture));
        Require(proof.HasAuthority && proof.Proof!.ClassBindings.Any(value => value.ClassName == "fixture.Loader") &&
            !proof.Proof.ClassBindings.Any(value => value.ClassName == "fixture.OverriddenUserLoader"), "Core required a superseded user loader rather than the actual final component loader.");
        var onlyUser = Create(declaredJava: true); var userBound = Good(Bind(onlyUser));
        var userHost = Host() with { AdditionalJvmArguments = ["-Djava.system.class.loader=fixture.UserLoader"] };
        Require(!MmcLaunchAuthorityEvaluator.Evaluate(userBound.Definition!, userBound.Plan!, onlyUser.Sources, userHost, ClassObservations(onlyUser)).HasAuthority,
            "An effective user loader escaped the required class matrix.");
        Require(MmcLaunchAuthorityEvaluator.Evaluate(userBound.Definition!, userBound.Plan!, onlyUser.Sources, userHost,
            ClassMatrix(onlyUser, "fixture.Main", "fixture.UserLoader")).HasAuthority, "A valid effective user loader was not bound.");
    }
    private static void PublishedAuthority() {
        var fixture = Create();
        var original = Good(Bind(fixture)).Definition!;
        var lockB = original.ArtifactLocks[0] with { AdoptedSha256 = Hash("replacement B"), RecoveryObjectRelativePath = "PCL/MmcArtifacts/sha256/" + Hash("replacement B") };
        var factsB = fixture.Facts.Select(f => f.ArtifactKey == lockB.ArtifactKey ? f with { ObservedSha256 = lockB.AdoptedSha256, RecoveryObjectRelativePath = lockB.RecoveryObjectRelativePath } : f).ToArray();
        var tampered = original with { ArtifactLocks = original.ArtifactLocks.SetItem(0, lockB) };
        Error(Bind(fixture, tampered, factsB, Options(intent: MmcBindingIntent.RepairExisting)), "MMC_BIND_AUTHORITY");
        Error(Bind(fixture, tampered with { InstalledProjection = null }, factsB, Options(intent: MmcBindingIntent.RepairExisting)), "MMC_BIND_AUTHORITY");
        Error(MmcPlanBinder.Bind(tampered with { InstalledProjection = null }, fixture.Plan, factsB, Options(intent: MmcBindingIntent.RepairExisting), publishedBaseline: original), "MMC_BIND_AUTHORITY");
        var variantResult = MmcProfileCompiler.Compile(fixture.Sources, new("windows", "x86", 17, 32));
        Require(!variantResult.HasErrors && variantResult.Profile is not null, "Variant fixture failed compile.");
        var variant = variantResult.Profile!;
        var variantFacts = variant.Classpath.Select(artifact => {
            var old = fixture.Facts.Single(f => f.TargetRelativePath == "libraries/" + artifact.RelativePath);
            return old with { ArtifactKey = artifact.Key, SourceRevision = artifact.SourceRevision };
        }).ToArray();
        var next = Good(MmcPlanBinder.Bind(original with { InstalledProjection = null }, variant, variantFacts, Options(next: 2, intent: MmcBindingIntent.AdoptDeclaredVariant), publishedBaseline: original));
        Require(next.Definition!.Generation == 2 && next.Definition.ArtifactLocks.Length > original.ArtifactLocks.Length, "Explicit variant draft did not preserve and extend the published locks.");
        var staleBaseline = MmcDefinitionTests.Published(original with { Generation = 2 });
        Error(MmcPlanBinder.Bind(original with { InstalledProjection = null }, variant, variantFacts, Options(next: 2, intent: MmcBindingIntent.AdoptDeclaredVariant), publishedBaseline: staleBaseline), "MMC_BIND_AUTHORITY");
        Error(MmcPlanBinder.Bind(original with { InstalledProjection = null }, fixture.Plan, fixture.Facts, Options(next: 2, intent: MmcBindingIntent.UpdateDefinition), publishedBaseline: original), "MMC_BIND_SOURCE_CHANGE");
        Error(MmcPlanBinder.Bind(original, fixture.Plan, fixture.Facts, Options(intent: MmcBindingIntent.RepairExisting) with { OutputJsonSha256 = Hash("changed projection") }), "MMC_BIND_GENERATION");
    }
    private static void PlanFingerprints() {
        var fixture = Create(declaredJava: true);
        var original = Good(Bind(fixture));
        var altered = new[] {
            fixture.Plan with { JvmArguments = [new("-Dchanged=true", fixture.Plan.FieldProvenance[0])] },
            fixture.Plan with { Classpath = fixture.Plan.Classpath.Reverse().ToImmutableArray() },
            fixture.Plan with { JavaPolicy = fixture.Plan.JavaPolicy with { AllowedMajors = [17] } }
        };
        foreach (var plan in altered) {
            Error(MmcPlanBinder.Bind(fixture.Definition, plan, fixture.Facts, Options()), "MMC_BIND_PLAN_FINGERPRINT");
            Require(!MmcLaunchAuthorityEvaluator.Evaluate(original.Definition!, original.Plan! with { CompiledProfile = plan }, fixture.Sources, Host(), ClassObservations(fixture)).HasAuthority, "Modified execution fields with stale labels authorized heuristic retirement.");
        }
        var cold = JsonSerializer.Deserialize<MmcCompiledProfile>(JsonSerializer.Serialize(fixture.Plan))!;
        Require(MmcProfileFingerprints.Matches(cold), "Cold serialized plan lost canonical semantic fingerprints.");
    }
    private static void BothAssetLayouts() {
        string sha1 = Convert.ToHexStringLower(SHA1.HashData([1, 2, 3])), sha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        string json = "{\"objects\":{\"sound/test.ogg\":{\"hash\":\"" + sha1 + "\",\"size\":3}},\"virtual\":true,\"map_to_resources\":true}";
        var fixture = Create(assetJson: json);
        var parent = fixture.Facts.Single(f => f.ArtifactKey == fixture.Plan.Assets.Artifact!.Key);
        string key = MmcDerivedProofs.ComputeAssetObjectKey(parent.ArtifactKey, parent.ObservedSha256, sha1, 3);
        var fact = new MmcMaterializedArtifactFact(key, parent.ObservedSha256, sha256, 3, "PCL/MmcArtifacts/sha256/" + sha256, "PCL/MmcRuntime/" + View + "/assets/objects/" + sha1[..2] + "/" + sha1, sha1);
        string sources = MmcDerivedProofs.ComputeAssetSourceSetFingerprint(parent.ArtifactKey, parent.ObservedSha256, parent.SizeBytes);
        string context = MmcDerivedProofs.ComputeDerivedContextFingerprint(fixture.Plan.RuntimePlanFingerprint, sources, "bounded-assets-v1");
        var virtualFile = new MmcDerivedOutputRecord(MmcDerivedOutputKind.AssetVirtualFile, key, sha256, "bounded-assets-v1", sources, fixture.Plan.RuntimePlanFingerprint, context, View,
            "virtual/fixture/sound/test.ogg", "PCL/MmcRuntime/" + View + "/assets/virtual/fixture/sound/test.ogg", sha256, 3, ObservedSha1: sha1);
        var mapped = virtualFile with { Kind = MmcDerivedOutputKind.AssetMappedResource, RelativePath = "sound/test.ogg", TargetRelativePath = "minecraft/resources/sound/test.ogg" };
        Require(Good(Bind(fixture, facts: fixture.Facts.Add(fact), derived: [virtualFile])).Definition!.DerivedOutputs.Length == 2,
            "prism-v1 virtual priority was not retained when both raw flags were set.");
        Error(Bind(fixture, facts: fixture.Facts.Add(fact), derived: [mapped]), "MMC_BIND_ASSET_DERIVED");
        Require(Bind(fixture, facts: fixture.Facts.Add(fact), derived: [virtualFile, mapped]).HasErrors,
            "Unrequested mapped resources were accepted despite virtual-first precedence.");
    }
}
