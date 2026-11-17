using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcArtifactTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC artifacts stage owned copies without publishing shared execution paths", () => Run(SharedCopies));
        yield return ("MMC artifacts keep local launcher libraries separate from game libraries", () => Run(LocalScopes));
        yield return ("MMC artifacts preserve inactive sources without executing them", () => Run(InactiveSources));
        yield return ("MMC artifact offline repair restores locked original bytes", () => Run(OfflineRepair));
        yield return ("MMC artifact repair rejects unknown adoption and altered sources", () => Run(RepairRejects));
        yield return ("MMC artifact declared hashes sizes and copy budgets reject invalid bytes", () => Run(IntegrityAndBudgets));
        yield return ("MMC artifact cancellation cleans only its owned stage", () => Run(Cancellation));
        yield return ("MMC artifact source boundaries and runtime target collisions fail closed", () => Run(BoundariesAndCollisions));
        yield return ("MMC artifact view keys depend on content not absolute target paths", () => Run(ViewIdentity));
        yield return ("MMC artifact damaged originals require a staged repair transaction", () => Run(OriginalRepair));
        yield return ("MMC native jars extract into owned staged views with excludes and digests", () => Run(NativeExtraction));
        yield return ("MMC native jars reject traversal duplicate targets and links", () => Run(NativeUnsafe));
        yield return ("MMC native jars enforce entry declared and actual output budgets", () => Run(NativeBudgets));
        yield return ("MMC native collisions across jars reject different contents", () => Run(NativeCollisions));
        yield return ("MMC assets isolate index objects and legacy virtual aliases", () => Run(AssetViews));
        yield return ("MMC assets map resources only inside the instance game root", () => Run(AssetResourceMapping));
        yield return ("MMC assets repair offline from adopted originals", () => Run(AssetOfflineRepair));
        yield return ("MMC assets reject ambiguous malformed and unsafe index data", () => Run(AssetInvalidIndexes));
        yield return ("MMC assets enforce metadata object and aggregate byte budgets", () => Run(AssetBudgets));
        yield return ("MMC materialization observations are measured even without declared SHA1", () => Run(ObservedDigests));
        yield return ("MMC native hot views verify trusted facts without extracting unchanged jars", () => Run(NativeHotCache));
        yield return ("MMC prepared adapter binds actual initial native and asset observations", () => Run(BindingAdapterInitial));
        yield return ("MMC prepared adapter repairs from published authority without losing locks", () => Run(BindingAdapterRepair));
        yield return ("MMC prefetch uses one static and one asset batch with cache reuse", () => Run(PrefetchBatches));
        yield return ("MMC prefetch deduplicates source targets and rejects hash conflicts", () => Run(PrefetchDeduplication));
        yield return ("MMC prefetch reuses a matching original version main jar", () => Run(PrefetchMainJar));
        yield return ("MMC hashless prefetch isolates Maven paths and preserves actual source pins", () => Run(PrefetchHashless));
        yield return ("MMC prefetch cancellation drains the supplied runner and cleans fresh candidates", () => Run(PrefetchCancellation));
        yield return ("MMC artifacts reject replaced ancestor junctions during materialization and prefetch", () => Run(AncestorJunctionRegression));
    }

    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static async Task SharedCopies() {
        using var fixture = new Fixture();
        byte[] bytes = Encoding.UTF8.GetBytes("verified library");
        var artifact = Artifact("shared", "example/library/1/library.jar", bytes);
        fixture.Source(artifact.Key, bytes);
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([artifact]), fixture.Access());
        var binding = prepared.OrderedBindings.Single();
        Equal(false, File.Exists(binding.TargetPath));
        Equal(true, binding.TargetPath.StartsWith(fixture.Paths.RuntimeRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Equal(false, binding.TargetPath.StartsWith(fixture.Cache, StringComparison.Ordinal));
        Equal(Hash(bytes), Hash(File.ReadAllBytes(binding.VerifiedInputPath)));
        fixture.Source(artifact.Key, Encoding.UTF8.GetBytes("ordinary launcher overwrites global cache"));
        Equal(Hash(bytes), Hash(File.ReadAllBytes(binding.VerifiedInputPath)));
        Equal(Hash(bytes), Hash(File.ReadAllBytes(fixture.Resolve(prepared.AdoptedLocks.Single().RecoveryObjectRelativePath))));
        Equal(true, prepared.StagedMutations.All(m => m.NewSourceRelativePath.StartsWith("PCL/MmcRuntime/.staging/", StringComparison.Ordinal)));
    }

    private static async Task LocalScopes() {
        using var fixture = new Fixture();
        byte[] bytes = Encoding.UTF8.GetBytes("launcher local");
        var artifact = Artifact("local", "early.jar", bytes, MmcArtifactScope.InstanceLocal);
        fixture.Write(Path.Combine(fixture.Paths.LocalLibraryRoot, "early.jar"), bytes);
        string gameFile = Path.Combine(fixture.Paths.GameRoot, "libraries", "early.jar");
        fixture.Write(gameFile, Encoding.UTF8.GetBytes("game data with identical filename"));
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([artifact]));
        Equal(Path.Combine(fixture.Paths.LocalLibraryRoot, "early.jar"), prepared.OrderedBindings.Single().TargetPath);
        Equal(Hash(bytes), prepared.AdoptedLocks.Single().AdoptedSha256);
        Equal("game data with identical filename", File.ReadAllText(gameFile));
    }

    private static async Task InactiveSources() {
        using var fixture = new Fixture();
        byte[] active = [1, 2], inactive = [3, 4];
        var a = Artifact("active", "active.jar", active);
        var b = Artifact("inactive-variant", "inactive.jar", inactive, MmcArtifactScope.InstanceLocal);
        fixture.Source(a.Key, active); fixture.Source(b.Key, inactive);
        var request = fixture.Request([a]) with { AdditionalPreservedArtifacts = [b] };
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(request, fixture.Access());
        Equal(1, prepared.OrderedBindings.Length); Equal(2, prepared.AdoptedLocks.Length);
        var record = prepared.AdoptedLocks.Single(x => x.ArtifactKey == b.Key);
        Equal(Hash(inactive), Hash(File.ReadAllBytes(fixture.Resolve(record.RecoveryObjectRelativePath))));
        Equal(false, File.Exists(Path.Combine(fixture.Paths.LocalLibraryRoot, "inactive.jar")));
    }

    private static async Task OfflineRepair() {
        using var fixture = new Fixture();
        byte[] bytes = [10, 20, 30];
        var a = Artifact("local", "repair.jar", bytes, MmcArtifactScope.InstanceLocal);
        fixture.Source(a.Key, bytes);
        ImmutableArray<MmcArtifactLock> locks;
        using (var imported = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access())) locks = imported.AdoptedLocks;
        fixture.ClearSources();
        using var repaired = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a], locks, MmcArtifactIntent.RepairExisting));
        Equal(1, repaired.StagedMutations.Length);
        Equal("libraries/repair.jar", repaired.StagedMutations[0].TargetRelativePath);
        Equal(Hash(bytes), Hash(File.ReadAllBytes(repaired.StagedMutations[0].StagedPath)));
        Equal(false, File.Exists(Path.Combine(fixture.Paths.LocalLibraryRoot, "repair.jar")));
        Equal(locks.Single().AdoptedSha256, repaired.AdoptedLocks.Single().AdoptedSha256);
    }

    private static async Task RepairRejects() {
        using var fixture = new Fixture();
        byte[] bytes = [1, 2, 3];
        var a = Artifact("library", "a.jar", bytes) with { Sha256 = null, Sha1 = null, Size = null };
        fixture.Source(a.Key, bytes);
        ImmutableArray<MmcArtifactLock> locks;
        using (var adopted = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access())) locks = adopted.AdoptedLocks;
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a], [], MmcArtifactIntent.RepairExisting), fixture.Access()));
        fixture.Write(fixture.Resolve(locks[0].RecoveryObjectRelativePath), [9, 9, 9]);
        fixture.Source(a.Key, [9, 9, 9]);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a], locks, MmcArtifactIntent.RepairExisting), fixture.Access()));
        Equal(Hash(bytes), locks[0].AdoptedSha256);
        Equal(0, fixture.ActiveStages());
    }

    private static async Task IntegrityAndBudgets() {
        using var fixture = new Fixture();
        byte[] bytes = [1, 2, 3];
        var a = Artifact("bad", "a.jar", bytes);
        fixture.Source(a.Key, [4, 5, 6]);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access()));
        fixture.Source(a.Key, bytes);
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxArtifactBytes: 2)).PrepareAsync(fixture.Request([a]), fixture.Access()));
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a with { Size = 4 }]), fixture.Access()));
        Equal(0, fixture.ActiveStages());
    }

    private static async Task Cancellation() {
        using var fixture = new Fixture();
        byte[] bytes = new byte[1024 * 1024];
        var a = Artifact("cancel", "large.jar", bytes);
        fixture.Source(a.Key, bytes);
        string sibling = Path.Combine(fixture.Paths.RuntimeRoot, "preserve-view", "keep"); fixture.Write(sibling, [7]);
        using var cancel = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access(), cancel.Token,
            new InlineProgress(_ => cancel.Cancel())));
        Equal(0, fixture.ActiveStages()); Equal(true, File.Exists(sibling)); Equal(true, File.Exists(fixture.Sources[a.Key]));
    }

    private static async Task BoundariesAndCollisions() {
        using var fixture = new Fixture();
        var a = Artifact("a", "same.jar", [1]); var b = Artifact("b", "same.jar", [2]);
        fixture.Source(a.Key, [1]); fixture.Source(b.Key, [2]);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a, b]), fixture.Access()));
        var wrongBoundary = new MmcArtifactSourceAccess((reference, token) => ValueTask.FromResult<MmcArtifactSource?>(new(fixture.Root, fixture.Sources[reference.Key])));
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), wrongBoundary));
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a with { RelativePath = "../escape.jar" }]), fixture.Access()));
        Equal(0, fixture.ActiveStages());
    }

    private static async Task ViewIdentity() {
        using var first = new Fixture(); using var second = new Fixture();
        var a = Artifact("stable", "same.jar", [1, 2]);
        first.Source(a.Key, [1, 2]); second.Source(a.Key, [1, 2]);
        using var p = await new MmcArtifactMaterializer().PrepareAsync(first.Request([a]), first.Access());
        using var q = await new MmcArtifactMaterializer().PrepareAsync(second.Request([a]), second.Access());
        Equal(p.ViewKey, q.ViewKey);
        Equal(false, p.OrderedBindings[0].TargetPath == q.OrderedBindings[0].TargetPath);
        using var changed = await new MmcArtifactMaterializer().PrepareAsync(first.Request([a]) with { LogicalContext = "changed-java-or-compiler" }, first.Access());
        Equal(false, changed.ViewKey == p.ViewKey);
    }

    private static async Task OriginalRepair() {
        using var fixture = new Fixture();
        byte[] bytes = [1, 2, 3]; var a = Artifact("repair-object", "a.jar", bytes);
        fixture.Source(a.Key, bytes);
        ImmutableArray<MmcArtifactLock> locks;
        using (var initial = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access())) locks = initial.AdoptedLocks;
        string original = fixture.Resolve(locks[0].RecoveryObjectRelativePath);
        fixture.Write(original, [9, 9, 9]);
        using var repaired = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a], locks, MmcArtifactIntent.RepairExisting), fixture.Access());
        Equal(Hash([9, 9, 9]), Hash(File.ReadAllBytes(original)));
        var mutation = repaired.StagedMutations.Single(m => m.TargetRelativePath == locks[0].RecoveryObjectRelativePath);
        Equal(Hash(bytes), Hash(File.ReadAllBytes(mutation.StagedPath)));
        Equal(Hash([9, 9, 9]), mutation.ExpectedExistingSha256);
    }

    private static async Task NativeExtraction() {
        using var fixture = new Fixture();
        byte[] jar = Jar(("META-INF/notice", [9]), ("x64/example.dll", [1, 2, 3]));
        var a = Artifact("native", "example/native.jar", jar) with { Role = MmcArtifactRole.Native, NativeClassifier = "windows-64", ExtractExcludes = ["META-INF/"] };
        fixture.Source(a.Key, jar);
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access());
        Equal(true, prepared.NativeExtractionComplete); Equal(1, prepared.NativeFiles.Length);
        var native = prepared.NativeFiles.Single();
        Equal("x64/example.dll", native.RelativePath); Equal(Hash([1, 2, 3]), native.Sha256);
        Equal(false, File.Exists(native.TargetPath));
        var mutation = prepared.StagedMutations.Single(x => x.TargetRelativePath.EndsWith("/natives/x64/example.dll", StringComparison.Ordinal));
        Equal(Hash([1, 2, 3]), Hash(File.ReadAllBytes(mutation.StagedPath)));
        Equal(true, prepared.NativeDirectory!.StartsWith(fixture.Paths.RuntimeRoot, StringComparison.Ordinal));
    }

    private static async Task NativeUnsafe() {
        foreach (var entries in new[] {
            new[] { ("../escape.dll", new byte[] { 1 }) },
            new[] { ("META-INF/../../escape.dll", new byte[] { 1 }) },
            new[] { ("a.dll", new byte[] { 1 }), ("A.dll", new byte[] { 1 }) },
            new[] { ("folder", new byte[] { 1 }), ("folder/a.dll", new byte[] { 2 }) }
        }) {
            using var fixture = new Fixture(); byte[] jar = Jar(entries);
            var a = Artifact("unsafe-native", "native.jar", jar) with { Role = MmcArtifactRole.Native, ExtractExcludes = ["META-INF/"] };
            fixture.Source(a.Key, jar);
            await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a]), fixture.Access()));
            Equal(0, fixture.ActiveStages());
        }
        using var linkFixture = new Fixture();
        byte[] linked;
        using (var bytes = new MemoryStream()) {
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
                var entry = zip.CreateEntry("link.dll"); entry.ExternalAttributes = unchecked((int)(0xa1ffU << 16));
                using var content = entry.Open(); content.Write([1]);
            }
            linked = bytes.ToArray();
        }
        var linkArtifact = Artifact("link-native", "native.jar", linked) with { Role = MmcArtifactRole.Native };
        linkFixture.Source(linkArtifact.Key, linked);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(linkFixture.Request([linkArtifact]), linkFixture.Access()));
    }

    private static async Task NativeBudgets() {
        using var fixture = new Fixture(); byte[] jar = Jar(("one.dll", [1, 2]), ("two.dll", [3, 4]));
        var a = Artifact("bounded-native", "native.jar", jar) with { Role = MmcArtifactRole.Native };
        fixture.Source(a.Key, jar);
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxNativeEntries: 1)).PrepareAsync(fixture.Request([a]), fixture.Access()));
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxNativeBytes: 3)).PrepareAsync(fixture.Request([a]), fixture.Access()));
        byte[] malformed = Jar(("bomb.dll", new byte[1024]));
        int central = FindSignature(malformed, [0x50, 0x4b, 0x01, 0x02]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(central + 24, 4), 1);
        var bomb = Artifact("actual-output-native", "bomb.jar", malformed) with { Role = MmcArtifactRole.Native };
        fixture.Source(bomb.Key, malformed);
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxNativeBytes: 16)).PrepareAsync(fixture.Request([bomb]), fixture.Access()));
        Equal(0, fixture.ActiveStages());
    }

    private static async Task NativeCollisions() {
        using var fixture = new Fixture();
        byte[] left = Jar(("same.dll", [1])), right = Jar(("same.dll", [2]));
        var a = Artifact("native-a", "a.jar", left) with { Role = MmcArtifactRole.Native };
        var b = Artifact("native-b", "b.jar", right) with { Role = MmcArtifactRole.Native };
        fixture.Source(a.Key, left); fixture.Source(b.Key, right);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a, b]), fixture.Access()));
        Equal(0, fixture.ActiveStages());
        using var roleFixture = new Fixture();
        byte[] parentJar = Jar(("parent", [1])), childJar = Jar(("parent/child.dll", [2]));
        var parent = Artifact("parent-native", "parent.jar", parentJar) with { Role = MmcArtifactRole.Native };
        var child = Artifact("child-native", "child.jar", childJar) with { Role = MmcArtifactRole.Native };
        roleFixture.Source(parent.Key, parentJar); roleFixture.Source(child.Key, childJar);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(roleFixture.Request([parent, child]), roleFixture.Access()));
        using var sameFixture = new Fixture();
        var sameA = Artifact("same-a", "a.jar", left) with { Role = MmcArtifactRole.Native };
        var sameB = Artifact("same-b", "b.jar", left) with { Role = MmcArtifactRole.Native };
        sameFixture.Source(sameA.Key, left); sameFixture.Source(sameB.Key, left);
        using var sharedBytes = await new MmcArtifactMaterializer().PrepareAsync(sameFixture.Request([sameA, sameB]), sameFixture.Access());
        Equal(1, sharedBytes.NativeFiles.Length); Equal(1, sharedBytes.NativeCache!.CompleteFiles.Length);
    }

    private static async Task AssetViews() {
        using var fixture = new Fixture(); byte[] bytes = [3, 4, 5];
        var index = AssetIndex(IndexJson(new Dictionary<string, byte[]> { ["sounds/a.ogg"] = bytes, ["sounds/b.ogg"] = bytes }, virtualAssets: true));
        fixture.Source(index.Key, IndexJson(new Dictionary<string, byte[]> { ["sounds/a.ogg"] = bytes, ["sounds/b.ogg"] = bytes }, virtualAssets: true));
        fixture.AssetSource(bytes);
        var request = fixture.Request([index]) with { Assets = new("example", index, index.Provenance) };
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(request, fixture.Access());
        Equal(true, prepared.AssetsMaterialized); Equal(2, prepared.AssetFiles.Length); Equal(2, prepared.AdoptedLocks.Length);
        Equal(1, prepared.OrderedBindings.Length);
        Equal(prepared.AssetFiles[0].ObjectPath, prepared.AssetFiles[1].ObjectPath);
        Equal(true, prepared.AssetsRoot!.StartsWith(fixture.Paths.RuntimeRoot, StringComparison.Ordinal));
        Equal(true, prepared.GameAssetsRoot!.EndsWith(Path.Combine("assets", "virtual", "example"), StringComparison.Ordinal));
        Equal(false, File.Exists(prepared.AssetFiles[0].ObjectPath));
        Equal(true, prepared.AssetFiles.All(x => x.VirtualPath is not null && x.MappedResourcePath is null));
        Equal(index.Sha256, prepared.AssetIndexContents.Single().ObservedSha256);
        Equal(index.Sha256, Hash(prepared.AssetIndexContents.Single().Utf8Content.ToArray()));
        var objectMutation = prepared.StagedMutations.Single(x => x.TargetRelativePath.Contains("/assets/objects/", StringComparison.Ordinal));
        Equal(Hash(bytes), Hash(File.ReadAllBytes(objectMutation.StagedPath)));
        fixture.AssetSource([9, 9]);
        Equal(Hash(bytes), Hash(File.ReadAllBytes(objectMutation.StagedPath)));
        Equal(Convert.ToHexStringLower(SHA1.HashData(bytes)), prepared.AssetFiles[0].Sha1);
    }

    private static async Task AssetResourceMapping() {
        using var fixture = new Fixture(); byte[] bytes = [1, 3, 5];
        byte[] json = IndexJson(new Dictionary<string, byte[]> { ["sound/a.ogg"] = bytes }, mapToResources: true);
        var index = AssetIndex(json); fixture.Source(index.Key, json); fixture.AssetSource(bytes);
        string existing = Path.Combine(fixture.Paths.GameRoot, "resources", "sound", "a.ogg"); fixture.Write(existing, [7, 8]);
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([index]), fixture.Access());
        Equal(Path.Combine(fixture.Paths.GameRoot, "resources"), prepared.MappedResourcesRoot);
        Equal(prepared.MappedResourcesRoot, prepared.GameAssetsRoot);
        Equal(existing, prepared.AssetFiles.Single().MappedResourcePath);
        Equal(Hash([7, 8]), Hash(File.ReadAllBytes(existing)));
        var mutation = prepared.StagedMutations.Single(x => x.TargetRelativePath == "minecraft/resources/sound/a.ogg");
        Equal(Hash([7, 8]), mutation.ExpectedExistingSha256); Equal(Hash(bytes), mutation.NewSha256);
    }

    private static async Task AssetOfflineRepair() {
        using var fixture = new Fixture(); byte[] bytes = [8, 6, 4];
        byte[] json = IndexJson(new Dictionary<string, byte[]> { ["textures/a.png"] = bytes }, virtualAssets: true);
        var index = AssetIndex(json); fixture.Source(index.Key, json); fixture.AssetSource(bytes);
        ImmutableArray<MmcArtifactLock> locks; string view;
        using (var initial = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([index]), fixture.Access())) { locks = initial.AdoptedLocks; view = initial.ViewKey; }
        fixture.ClearSources();
        using var repair = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([index], locks, MmcArtifactIntent.RepairExisting));
        Equal(view, repair.ViewKey); Equal(true, repair.AssetsMaterialized); Equal(1, repair.AssetFiles.Length);
        Equal(Hash(bytes), repair.AssetFiles[0].Sha256);
        Equal(2, repair.Observations.Length);
    }

    private static async Task AssetInvalidIndexes() {
        foreach (string text in new[] {
            "{\"objects\":{},\"objects\":{}}", "{\"objects\":{},\"virtual\":\"true\"}", "{\"objects\":{},\"unknownInstallAction\":true}",
            "{\"objects\":{\"../escape\":{\"hash\":\"" + new string('a', 40) + "\",\"size\":0}}}",
            "{\"objects\":{\"a\":{\"hash\":\"" + new string('a', 40) + "\",\"size\":-1}}}",
            "{\"objects\":{\"a\":{\"hash\":\"" + new string('a', 40) + "\",\"size\":false}}}"
        }) {
            using var fixture = new Fixture(); byte[] json = Encoding.UTF8.GetBytes(text); var index = AssetIndex(json); fixture.Source(index.Key, json);
            await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([index]), fixture.Access()));
            Equal(0, fixture.ActiveStages());
        }
        foreach (var names in new[] { new[] { "a", "a/b" }, new[] { "Name", "name" } }) {
            using var fixture = new Fixture(); byte[] json = IndexJson(names.ToDictionary(x => x, x => new byte[] { 1 }));
            var index = AssetIndex(json); fixture.Source(index.Key, json);
            await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(fixture.Request([index]), fixture.Access()));
        }
        using var unresolved = new Fixture();
        var missing = new MmcAssetIndex("legacy", null, new("net.minecraft", "1", "fixture", "digest", "assets", 0));
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(unresolved.Request([]) with { Assets = missing }));
    }

    private static async Task AssetBudgets() {
        using var fixture = new Fixture(); byte[] json = IndexJson(new Dictionary<string, byte[]> { ["a"] = [1], ["b"] = [2] });
        var index = AssetIndex(json); fixture.Source(index.Key, json); fixture.AssetSource([1]); fixture.AssetSource([2]);
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxAssetObjects: 1)).PrepareAsync(fixture.Request([index]), fixture.Access()));
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxAssetIndexBytes: 8)).PrepareAsync(fixture.Request([index]), fixture.Access()));
        await Throws<IOException>(() => new MmcArtifactMaterializer(new(MaxCopiedBytes: json.Length + 1)).PrepareAsync(fixture.Request([index]), fixture.Access()));
        Equal(0, fixture.ActiveStages());
    }

    private static async Task ObservedDigests() {
        using var fixture = new Fixture(); byte[] bytes = [1, 4, 9];
        var artifact = Artifact("measured", "a.jar", bytes) with { Sha1 = null };
        fixture.Source(artifact.Key, bytes);
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([artifact]), fixture.Access());
        string expected = Convert.ToHexStringLower(SHA1.HashData(bytes));
        Equal(expected, prepared.OrderedBindings.Single().ObservedSha1); Equal(expected, prepared.Observations.Single().ObservedSha1);
    }

    private static async Task NativeHotCache() {
        using var fixture = new Fixture(); byte[] jar = Jar(("same.dll", [1, 2, 3]));
        var native = Artifact("cached-native", "native.jar", jar) with { Role = MmcArtifactRole.Native };
        fixture.Source(native.Key, jar);
        ImmutableArray<MmcArtifactLock> locks; MmcNativeViewCache cache;
        using (var first = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([native]), fixture.Access())) {
            locks = first.AdoptedLocks; cache = first.NativeCache!;
            // Isolated fixture publication only; actual transaction/crash behavior is verified by its own host tests.
            foreach (var mutation in first.StagedMutations) fixture.Write(fixture.Resolve(mutation.TargetRelativePath), File.ReadAllBytes(mutation.StagedPath));
        }
        fixture.ClearSources(); int extracted = 0;
        var request = fixture.Request([native], locks, MmcArtifactIntent.RepairExisting) with { TrustedNativeCache = cache };
        using (var hot = await new MmcArtifactMaterializer().PrepareAsync(request, progress: new InlineProgress(x => { if (x.Phase == "native-extract") extracted++; }))) {
            Equal(true, hot.NativeCacheReused); Equal(0, hot.StagedMutations.Length); Equal(0, extracted);
            fixture.Write(hot.NativeFiles.Single().TargetPath, [9]);
        }
        using var repaired = await new MmcArtifactMaterializer().PrepareAsync(request);
        Equal(false, repaired.NativeCacheReused);
        Equal(true, repaired.StagedMutations.Any(x => x.TargetRelativePath.EndsWith("/natives/same.dll", StringComparison.Ordinal)));
        Equal(Hash([1, 2, 3]), repaired.NativeFiles.Single().Sha256);
        fixture.Write(Path.Combine(repaired.NativeDirectory!, "unrecorded.dll"), [5]);
        await Throws<IOException>(() => new MmcArtifactMaterializer().PrepareAsync(request));
        Equal(true, File.Exists(Path.Combine(repaired.NativeDirectory!, "unrecorded.dll")));
    }

    private static async Task BindingAdapterInitial() {
        using var binding = new BindingFixture(native: true, assets: true);
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(binding.Request(), binding.Files.Access());
        var input = MmcArtifactBindingAdapter.Create(binding.Request(), binding.Plan, prepared);
        Equal(true, input.NativeCompletion?.Complete == true);
        Equal(1, input.AssetIndexes.Length);
        Equal(true, input.DerivedOutputs.Any(x => x.Kind == MmcDerivedOutputKind.NativeFile));
        Equal(true, input.DerivedOutputs.Any(x => x.Kind == MmcDerivedOutputKind.AssetObject));
        Equal(true, input.DerivedOutputs.Any(x => x.Kind == MmcDerivedOutputKind.AssetVirtualFile));
        var bound = MmcPlanBinder.Bind(binding.Definition, binding.Plan, input.Facts,
            input.ApplyTo(new(1, 1, MmcBindingIntent.InitialImport, Hash(Encoding.UTF8.GetBytes("projection")), 1)),
            derivedOutputs: input.DerivedOutputs, assetIndexes: input.AssetIndexes);
        if (bound.HasErrors) throw new Exception(string.Join("; ", bound.Diagnostics.Select(x => x.Code + ": " + x.Message)));
        Equal(MmcDefinitionReadStatus.Valid, MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(bound.Definition!)).Status);
        Equal(true, input.Facts.All(x => x.ObservedSha1 is not null));
    }

    private static async Task BindingAdapterRepair() {
        using var binding = new BindingFixture(native: true, assets: true);
        MmcInstanceDefinition published;
        ImmutableArray<MmcArtifactLock> locks;
        using (var prepared = await new MmcArtifactMaterializer().PrepareAsync(binding.Request(), binding.Files.Access())) {
            locks = prepared.AdoptedLocks;
            var inputs = MmcArtifactBindingAdapter.Create(binding.Request(), binding.Plan, prepared);
            var result = MmcPlanBinder.Bind(binding.Definition, binding.Plan, inputs.Facts,
                inputs.ApplyTo(new(1, 1, MmcBindingIntent.InitialImport, Hash(Encoding.UTF8.GetBytes("projection")), 1)),
                derivedOutputs: inputs.DerivedOutputs, assetIndexes: inputs.AssetIndexes);
            if (result.HasErrors) throw new Exception(string.Join("; ", result.Diagnostics.Select(x => x.Code + ": " + x.Message)));
            published = result.Definition!;
        }
        binding.Files.ClearSources();
        var request = binding.Request() with { Intent = MmcArtifactIntent.RepairExisting, ExistingLocks = locks };
        using var repaired = await new MmcArtifactMaterializer().PrepareAsync(request);
        var input = MmcArtifactBindingAdapter.Create(request, binding.Plan, repaired);
        var rebound = MmcPlanBinder.Bind(published, binding.Plan, input.Facts,
            input.ApplyTo(new(1, 1, MmcBindingIntent.RepairExisting, Hash(Encoding.UTF8.GetBytes("projection")), 1)),
            derivedOutputs: input.DerivedOutputs, assetIndexes: input.AssetIndexes, publishedBaseline: published);
        if (rebound.HasErrors) throw new Exception(string.Join("; ", rebound.Diagnostics.Select(x => x.Code + ": " + x.Message)));
        Equal(published.ArtifactLocks.Length, rebound.Definition!.ArtifactLocks.Length);
        Equal(published.DerivedOutputs.Length, rebound.Definition.DerivedOutputs.Length);
    }

    private static MmcArtifactCacheRoots CacheRoots(Fixture files) => new(Path.Combine(files.Scope, "shared-libraries"), Path.Combine(files.Scope, "shared-assets"),
        Path.Combine(files.Scope, "mmc-source-cache"), Path.Combine(files.Scope, "shared-versions"));
    private static async Task PrefetchBatches() {
        using var fixture = new BindingFixture(native: true, assets: true);
        var roots = CacheRoots(fixture.Files); var batches = new List<ImmutableArray<MmcArtifactDownloadSpec>>();
        var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
            batches.Add(files);
            foreach (var file in files) { token.ThrowIfCancellationRequested(); fixture.Files.Write(file.DownloadPath, fixture.Files.Payload(file.Artifact)); }
            return Task.CompletedTask;
        });
        var result = await adapter.PrefetchAsync(fixture.Plan, fixture.Request());
        Equal(2, batches.Count); Equal(2, result.BatchCount); Equal(5, result.RequestedFileCount);
        Equal(true, batches[0].Any(x => x.Artifact.Role == MmcArtifactRole.AssetIndex && x.DownloadPath.StartsWith(roots.AssetsRoot, StringComparison.Ordinal)));
        Equal(true, batches[1].All(x => MmcArtifactMaterializer.IsAssetObjectReference(x.Artifact) && x.DownloadPath.StartsWith(roots.AssetsRoot, StringComparison.Ordinal)));
        using var prepared = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request(), result.CreateAccess());
        Equal(true, prepared.AssetsMaterialized); Equal(true, prepared.NativeExtractionComplete);
        var hot = await adapter.PrefetchAsync(fixture.Plan, fixture.Request());
        Equal(0, hot.BatchCount); Equal(2, batches.Count);
    }

    private static async Task PrefetchDeduplication() {
        using var fixture = new BindingFixture(duplicateDownloadOnly: true);
        var roots = CacheRoots(fixture.Files); var received = new List<MmcArtifactDownloadSpec>();
        var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
            received.AddRange(files);
            foreach (var file in files) fixture.Files.Write(file.DownloadPath, fixture.Files.Payload(file.Artifact));
            return Task.CompletedTask;
        });
        var result = await adapter.PrefetchAsync(fixture.Plan, fixture.Request());
        Equal(1, result.BatchCount); Equal(2, received.Count);
        Equal(fixture.Plan.Classpath[0].Key, received[0].Artifact.Key);
        Equal(2, received[0].ArtifactKeys.Length);
        var library = fixture.Plan.Classpath[0];
        var conflict = library with { Key = Hash(Encoding.UTF8.GetBytes("conflict")), Sha1 = Convert.ToHexStringLower(SHA1.HashData([9])), Size = 1 };
        await Throws<IOException>(() => adapter.PrefetchAsync(fixture.Plan, fixture.Request() with { AdditionalPreservedArtifacts = [conflict] }));
        Equal(2, received.Count);
    }

    private static async Task PrefetchMainJar() {
        using var fixture = new BindingFixture(); var roots = CacheRoots(fixture.Files);
        var library = fixture.Plan.Classpath.First(x => x.Role == MmcArtifactRole.Classpath);
        fixture.Files.Write(Path.Combine(roots.LibrariesRoot, library.RelativePath), fixture.Files.Payload(library));
        string original = Path.Combine(roots.VersionsRoot!, fixture.Plan.MinecraftVersion, fixture.Plan.MinecraftVersion + ".jar");
        fixture.Files.Write(original, fixture.Files.Payload(fixture.Plan.MainJar));
        var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => throw new Exception("No network batch is needed."));
        var result = await adapter.PrefetchAsync(fixture.Plan, fixture.Request());
        Equal(0, result.BatchCount); Equal(original, result.Sources[fixture.Plan.MainJar.Key].FilePath);
    }

    private static async Task PrefetchHashless() {
        using var fixture = new BindingFixture(hashlessLibrary: true); var roots = CacheRoots(fixture.Files);
        var library = fixture.Plan.Classpath.First(x => x.Role == MmcArtifactRole.Classpath);
        fixture.Files.Write(Path.Combine(roots.LibrariesRoot, library.RelativePath), [9, 9]);
        fixture.Files.Write(Path.Combine(roots.LibrariesRoot, fixture.Plan.MainJar.RelativePath), fixture.Files.Payload(fixture.Plan.MainJar));
        int batches = 0; byte[] remote = fixture.Files.Payload(library);
        var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
            batches++; foreach (var file in files) fixture.Files.Write(file.DownloadPath, remote); return Task.CompletedTask;
        });
        var first = await adapter.PrefetchAsync(fixture.Plan, fixture.Request());
        Equal(1, batches); Equal(true, first.Sources[library.Key].FilePath.StartsWith(roots.SourceCacheRoot, StringComparison.Ordinal));
        Equal(Hash(remote), Hash(File.ReadAllBytes(first.Sources[library.Key].FilePath)));
        var hot = await adapter.PrefetchAsync(fixture.Plan, fixture.Request()); Equal(1, batches); Equal(0, hot.BatchCount);
        string pin = Directory.GetFiles(roots.SourceCacheRoot, "pin.json", SearchOption.AllDirectories).Single();
        string originalPin = File.ReadAllText(pin);
        fixture.Files.Write(first.Sources[library.Key].FilePath, [8, 8]); remote = [8, 8];
        var adopted = new MmcArtifactLock(library.Key, library.SourceRevision, Hash([1, 2]), 2, "PCL/MmcArtifacts/sha256/" + Hash([1, 2]));
        await Throws<IOException>(() => adapter.PrefetchAsync(fixture.Plan, fixture.Request() with { ExistingLocks = [adopted], Intent = MmcArtifactIntent.RepairExisting }));
        Equal(originalPin, File.ReadAllText(pin));
    }

    private static async Task PrefetchCancellation() {
        using var fixture = new BindingFixture(hashlessLibrary: true); var roots = CacheRoots(fixture.Files);
        fixture.Files.Write(Path.Combine(roots.LibrariesRoot, fixture.Plan.MainJar.RelativePath), fixture.Files.Payload(fixture.Plan.MainJar));
        using var cancel = new CancellationTokenSource(); bool drained = false;
        var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
            fixture.Files.Write(files[0].DownloadPath, [1]); cancel.Cancel(); drained = true;
            return Task.FromCanceled(token);
        });
        await Throws<OperationCanceledException>(() => adapter.PrefetchAsync(fixture.Plan, fixture.Request(), cancel.Token));
        Equal(true, drained);
        Equal(0, Directory.GetFiles(roots.SourceCacheRoot, "payload-*.bin", SearchOption.AllDirectories).Length);
        Equal(0, Directory.GetFiles(roots.SourceCacheRoot, "pin.json", SearchOption.AllDirectories).Length);
    }

    private static async Task AncestorJunctionRegression() {
        using (var fixture = new Fixture()) {
            byte[] bytesA = Encoding.UTF8.GetBytes("alpha-verified-artifact");
            byte[] bytesB = Encoding.UTF8.GetBytes("beta-verified-artifact");
            var a = Artifact("alpha", "example/a/1/a.jar", bytesA);
            var b = Artifact("beta", "example/b/1/b.jar", bytesB);
            fixture.Source(a.Key, bytesA);
            fixture.Source(b.Key, bytesB);
            string sha256Root = Path.Combine(fixture.Paths.InstanceRoot, "PCL", "MmcArtifacts", "sha256");
            string parkedSha256 = Path.Combine(fixture.Scope, "parked-sha256");
            string outside = Path.Combine(fixture.Scope, "outside-sha256");
            string outsideA = Path.Combine(outside, Hash(bytesA));
            string outsideB = Path.Combine(outside, Hash(bytesB));
            string sentinel = Path.Combine(outside, "sentinel.txt");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(outsideA, bytesA);
            File.WriteAllText(sentinel, "preserve-outside-sha256");
            bool replaced = false;
            var guardedAccess = new MmcArtifactSourceAccess((reference, token) => {
                if (reference.Key == b.Key && !replaced) {
                    replaced = true;
                    Directory.Move(sha256Root, parkedSha256);
                    MmcInstancePathsTests.CreateDirectoryLink(sha256Root, outside);
                }
                return ValueTask.FromResult<MmcArtifactSource?>(new(fixture.Cache, fixture.Sources[reference.Key]));
            });
            try {
                IOException? rejected = null;
                try {
                    await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a, b]), guardedAccess);
                } catch (IOException error) { rejected = error; }
                Equal(true, replaced);
                Equal("MMC 路径包含链接或重解析点。", rejected?.Message);
                Equal(false, File.Exists(outsideB));
                Equal("preserve-outside-sha256", File.ReadAllText(sentinel));
                Equal(true, bytesA.SequenceEqual(File.ReadAllBytes(outsideA)));
                Equal(0, fixture.ActiveStages());
            } finally {
                if (Directory.Exists(sha256Root)) Directory.Delete(sha256Root, false);
                if (Directory.Exists(parkedSha256)) Directory.Move(parkedSha256, sha256Root);
            }
            using var clean = await new MmcArtifactMaterializer().PrepareAsync(fixture.Request([a, b]), fixture.Access());
            Equal(2, clean.OrderedBindings.Length);
        }

        using (var binding = new BindingFixture(hashlessLibrary: true)) {
            var roots = CacheRoots(binding.Files);
            var library = binding.Plan.Classpath.First(x => x.Role == MmcArtifactRole.Classpath);
            binding.Files.Write(Path.Combine(roots.LibrariesRoot, binding.Plan.MainJar.RelativePath), binding.Files.Payload(binding.Plan.MainJar));
            byte[] remote = binding.Files.Payload(library);
            string bySourceRoot = Path.Combine(roots.SourceCacheRoot, "by-source", library.SourceRevision);
            string parkedBySource = Path.Combine(binding.Files.Scope, "parked-by-source");
            string outsidePrefetch = Path.Combine(binding.Files.Scope, "outside-prefetch");
            string outsideSentinel = Path.Combine(outsidePrefetch, "sentinel.txt");
            Directory.CreateDirectory(outsidePrefetch);
            File.WriteAllText(outsideSentinel, "preserve-outside-prefetch");
            bool replaced = false;
            var adapter = new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
                foreach (var file in files) {
                    binding.Files.Write(file.DownloadPath, remote);
                    string uriDir = Path.GetFileName(Path.GetDirectoryName(file.DownloadPath)!)!;
                    string matchingOutsidePayload = Path.Combine(outsidePrefetch, uriDir, Path.GetFileName(file.DownloadPath));
                    binding.Files.Write(matchingOutsidePayload, remote);
                }
                if (!replaced) {
                    replaced = true;
                    Directory.Move(bySourceRoot, parkedBySource);
                    MmcInstancePathsTests.CreateDirectoryLink(bySourceRoot, outsidePrefetch);
                }
                return Task.CompletedTask;
            });
            try {
                await Throws<IOException>(() => adapter.PrefetchAsync(binding.Plan, binding.Request()));
                Equal(true, replaced);
                Equal("preserve-outside-prefetch", File.ReadAllText(outsideSentinel));
                Equal(0, Directory.GetFiles(outsidePrefetch, "pin.json", SearchOption.AllDirectories).Length);
                Equal(0, Directory.GetFiles(outsidePrefetch, ".pin-*.tmp", SearchOption.AllDirectories).Length);
            } finally {
                if (Directory.Exists(bySourceRoot)) Directory.Delete(bySourceRoot, false);
                if (Directory.Exists(parkedBySource)) Directory.Move(parkedBySource, bySourceRoot);
            }
            var healthy = await new MmcArtifactPrefetchAdapter<MmcArtifactDownloadSpec>(roots, x => x, (files, token) => {
                foreach (var file in files) binding.Files.Write(file.DownloadPath, remote);
                return Task.CompletedTask;
            }).PrefetchAsync(binding.Plan, binding.Request());
            Equal(1, healthy.Sources.Count(pair => pair.Key == library.Key));
        }
    }

    private sealed class BindingFixture : IDisposable {
        internal Fixture Files { get; } = new();
        internal MmcCompiledProfile Plan { get; }
        internal MmcInstanceDefinition Definition { get; }
        internal BindingFixture(bool native = false, bool assets = false, bool duplicateDownloadOnly = false, bool hashlessLibrary = false) {
            byte[] main = [7, 8], library = [1, 2], nativeBytes = Jar(("META-INF/notice", [0]), ("fixture.dll", [3, 4]));
            byte[] assetBytes = [4, 5, 6];
            byte[] indexBytes = IndexJson(new Dictionary<string, byte[]> { ["sound/fixture.ogg"] = assetBytes }, virtualAssets: true);
            object Download(byte[] bytes, string name) => new { url = "https://example.invalid/" + name, sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), size = bytes.Length };
            object libraryDownload = hashlessLibrary ? new { url = "https://example.invalid/library.jar", size = library.Length } : Download(library, "library.jar");
            object libraryEntry = new { name = "fixture:library:1", downloads = new { artifact = libraryDownload } };
            var libraries = new List<object> { libraryEntry };
            if (native) libraries.Add(new {
                name = "fixture:native:1", natives = new { windows = "natives-windows-${arch}" },
                downloads = new { classifiers = new Dictionary<string, object> { ["natives-windows-64"] = Download(nativeBytes, "native.jar") } },
                extract = new { exclude = new[] { "META-INF/" } }
            });
            var patch = new Dictionary<string, object?> {
                ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = "1", ["mainClass"] = "fixture.Main",
                ["mainJar"] = new { name = "fixture:main:1:client", downloads = new { artifact = Download(main, "client.jar") } },
                ["libraries"] = libraries
            };
            if (assets) patch["assetIndex"] = new { id = "fixture", url = "https://example.invalid/fixture.json", sha1 = Convert.ToHexStringLower(SHA1.HashData(indexBytes)), size = indexBytes.Length };
            if (duplicateDownloadOnly) patch["mavenFiles"] = new[] { libraryEntry, libraryEntry };
            string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1\"}]}";
            string component = JsonSerializer.Serialize(patch);
            var metadata = new Dictionary<string, string> { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = component };
            var parsed = MmcPackageReader.Parse(new(metadata.Select(x => new MmcArchiveEntry(x.Key, Encoding.UTF8.GetByteCount(x.Value))).ToArray(), metadata));
            var resolution = MmcComponentResolver.Resolve(parsed.Package!, []);
            if (!resolution.IsComplete) throw new Exception(string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
            var result = MmcProfileCompiler.Compile(resolution.Profile!, new("windows", "x86_64", 17));
            if (result.HasErrors) throw new Exception(string.Join("; ", result.Diagnostics.Select(x => x.Message)));
            Plan = result.Profile!;
            foreach (var artifact in Plan.Classpath.Concat(Plan.NativeArtifacts).Concat(Plan.DownloadOnlyArtifacts).Concat(Plan.AuxiliaryArtifacts))
                Files.Source(artifact.Key, artifact.Role switch { MmcArtifactRole.MainJar => main, MmcArtifactRole.Native => nativeBytes, MmcArtifactRole.AssetIndex => indexBytes, _ => library });
            if (assets) Files.AssetSource(assetBytes);
            string bundle = Hash(Encoding.UTF8.GetBytes(manifest + component)), prefix = "PCL/MmcSource/" + bundle + "/";
            var records = new[] {
                new MmcSourceMetadataRecord("mmc-pack.json", Hash(Encoding.UTF8.GetBytes(manifest)), prefix + "mmc-pack.json", MmcSourceRecordKind.Manifest),
                new MmcSourceMetadataRecord("patches/net.minecraft.json", Hash(Encoding.UTF8.GetBytes(component)), prefix + "patches/net.minecraft.json",
                    MmcSourceRecordKind.Component, "net.minecraft", "1", MmcComponentSourceKind.PackageLocal)
            };
            Definition = new(1, "Mmc", "prism-v1", Guid.NewGuid(), 1, new(bundle, "self-authored-binding-fixture"),
                new("", bundle, resolution.Profile!.SourceFingerprint, records.ToImmutableArray()), new("minecraft"), [], [], []);
        }
        internal MmcArtifactRequest Request() => Files.Request(Plan.Classpath.Concat(Plan.NativeArtifacts).Concat(Plan.DownloadOnlyArtifacts).Concat(Plan.AuxiliaryArtifacts).ToImmutableArray())
            with { LogicalContext = Plan.RuntimePlanFingerprint, Assets = Plan.Assets.Artifact is null ? null : Plan.Assets };
        public void Dispose() => Files.Dispose();
    }

    private static byte[] IndexJson(Dictionary<string, byte[]> files, bool virtualAssets = false, bool mapToResources = false) =>
        JsonSerializer.SerializeToUtf8Bytes(new { objects = files.ToDictionary(x => x.Key, x => new { hash = Convert.ToHexStringLower(SHA1.HashData(x.Value)), size = x.Value.Length }),
            @virtual = virtualAssets, map_to_resources = mapToResources });
    private static MmcArtifactReference AssetIndex(byte[] bytes) => Artifact("asset-index", "indexes/example.json", bytes) with { Role = MmcArtifactRole.AssetIndex };

    private static byte[] Jar(params (string Name, byte[] Content)[] entries) {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
            foreach (var item in entries) { var entry = zip.CreateEntry(item.Name); using var content = entry.Open(); content.Write(item.Content); }
        }
        return bytes.ToArray();
    }
    private static int FindSignature(byte[] bytes, byte[] signature) {
        for (int index = 0; index <= bytes.Length - signature.Length; index++) if (bytes.AsSpan(index, signature.Length).SequenceEqual(signature)) return index;
        throw new Exception("Fixture ZIP signature missing.");
    }

    private static MmcArtifactReference Artifact(string key, string relative, byte[] bytes, MmcArtifactScope scope = MmcArtifactScope.Shared) =>
        new(Hash(Encoding.UTF8.GetBytes(key)), "example.component", Hash(Encoding.UTF8.GetBytes("revision-1")), MmcArtifactRole.Classpath, scope, null, relative,
            scope == MmcArtifactScope.Shared ? ["https://example.invalid/" + key] : [],
            Convert.ToHexStringLower(SHA1.HashData(bytes)), Hash(bytes), bytes.Length, null, [],
            new("example.component", "1", "self-authored-fixture", "source-hash", "libraries", 0));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class InlineProgress(Action<MmcArtifactProgress> callback) : IProgress<MmcArtifactProgress> { public void Report(MmcArtifactProgress value) => callback(value); }
    private sealed class Fixture : IDisposable {
        public string Scope { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-artifacts-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Cache { get; }
        public MmcInstancePaths Paths { get; }
        public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, string> AssetSources { get; } = new(StringComparer.Ordinal);
        public Fixture() {
            Root = Path.Combine(Scope, "versions", "sample"); Cache = Path.Combine(Scope, "cache");
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Cache); Paths = MmcInstancePaths.Resolve(Root);
        }
        public void Source(string key, byte[] bytes) { string path = Path.Combine(Cache, Hash(Encoding.UTF8.GetBytes(key))); Write(path, bytes); Sources[key] = path; }
        public void AssetSource(byte[] bytes) { string hash = Convert.ToHexStringLower(SHA1.HashData(bytes)); string path = Path.Combine(Cache, "assets", hash); Write(path, bytes); AssetSources[hash] = path; }
        public byte[] Payload(MmcArtifactReference artifact) => File.ReadAllBytes(Sources.TryGetValue(artifact.Key, out var path) ? path : AssetSources[artifact.Sha1!]);
        public void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        public string Resolve(string relative) => Paths.ResolveRelative(MmcPathRole.Instance, relative);
        public MmcArtifactRequest Request(ImmutableArray<MmcArtifactReference> artifacts, ImmutableArray<MmcArtifactLock> locks = default,
            MmcArtifactIntent intent = MmcArtifactIntent.AdoptDeclaredVariant) => new(Paths, "compiler-context-v1", artifacts, [], locks, intent);
        public MmcArtifactSourceAccess Access() => new((artifact, token) => ValueTask.FromResult<MmcArtifactSource?>(
            Sources.TryGetValue(artifact.Key, out string? path) || (MmcArtifactMaterializer.IsAssetObjectReference(artifact) && artifact.Sha1 is not null && AssetSources.TryGetValue(artifact.Sha1, out path)) ? new(Cache, path) : null));
        public void ClearSources() { foreach (string path in Sources.Values.Concat(AssetSources.Values)) File.Delete(path); Sources.Clear(); AssetSources.Clear(); }
        public int ActiveStages() { string parent = Path.Combine(Paths.RuntimeRoot, ".staging"); return Directory.Exists(parent) ? Directory.GetDirectories(parent).Length : 0; }
        public void Dispose() { if (Directory.Exists(Scope)) Directory.Delete(Scope, true); }
    }
}
