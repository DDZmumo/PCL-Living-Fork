using System.IO.Compression;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcPackageRevisionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC package binding replaces measured author provenance in a new monotonic generation with real retained undo", PackageBinding);
        yield return ("MMC package binding rejects stale undo, wrong archive authority and unannounced game-tree migration", PackageRefusals);
    }
    private static void PackageBinding() {
        using Fixture fixture = new(); var input = fixture.Prepare();
        string before = MmcDefinitionCodec.Serialize(input.Current.Definition);
        var result = Bind(input);
        Require(!result.HasErrors && result.Definition is not null && result.Plan is not null, string.Join(";", result.Diagnostics.Select(value => value.Message)));
        Require(result.Definition!.InstanceId == input.Current.Definition.InstanceId && result.Definition.Generation == 2 &&
            result.Definition.Origin == input.Source.Origin && result.Definition.AuthorFileBaseline == input.Source.AuthorFileBaseline &&
            result.Definition.InstalledProjection!.RuntimePlanFingerprint == input.Plan.RuntimePlanFingerprint,
            "Package binding lost current identity or the new measured author source.");
        Require(MmcDefinitionCodec.Validate(result.Definition).IsEmpty && MmcDefinitionCodec.Serialize(input.Current.Definition) == before,
            "Package binding mutated its input or published an invalid definition.");
        Require(File.ReadAllText(Path.Combine(input.Paths.GameRoot, "config", "player.cfg")) == "current player override",
            "The pure package binder changed live player data.");
        Require(new MmcInstanceHistoryBackend().ListAsync(input.Paths.InstanceRoot).GetAwaiter().GetResult().Any(point => point.PointId == input.Options.Retained.PointId),
            "The package binding fixture did not retain a real indexed undo point.");
    }
    private static void PackageRefusals() {
        using Fixture fixture = new(); var input = fixture.Prepare();
        Reject(input, input.Options with { ExpectedGeneration = 2 }, "MMC_PACKAGE_REVISION_AUTHORITY");
        Reject(input, input.Options with { Retained = input.Options.Retained with { PrimarySha256 = Hash("not the current publication") } }, "MMC_PACKAGE_REVISION_AUTHORITY");
        Reject(input, input.Options with { ExpectedPackageSha256 = Hash("another package") }, "MMC_PACKAGE_REVISION_SOURCE");
        var changed = input with { Source = input.Source with { Layout = new(".minecraft") } };
        Reject(changed, changed.Options, "MMC_PACKAGE_REVISION_LAYOUT");
        var oldLock = input.Current.Definition.ArtifactLocks[0];
        var fact = input.Facts[0] with { ArtifactKey = oldLock.ArtifactKey, SourceRevision = oldLock.SourceRevision, ObservedSha256 = Hash("different adopted bytes") };
        changed = input with { Facts = input.Facts.SetItem(0, fact) };
        Reject(changed, changed.Options, "MMC_PACKAGE_REVISION_REBIND");
    }
    private static void Reject(Input input, MmcPackageRevisionBindOptions options, string code) {
        var result = Bind(input, options); Require(result.HasErrors && result.Diagnostics.Any(value => value.Code == code),
            "Expected " + code + ": " + string.Join(";", result.Diagnostics.Select(value => value.Code + " " + value.Message)));
    }
    private static MmcBindingResult Bind(Input input, MmcPackageRevisionBindOptions? options = null) => MmcPlanBinder.BindPackageRevision(
        input.Current.Definition, input.Source, input.Plan, input.Facts, options ?? input.Options, input.Baseline,
        input.Derived, input.AssetIndexes);
    private sealed record Input(MmcInstancePaths Paths, MmcStoredPrimaryDefinition Current, MmcInstanceDefinition Source, MmcCompiledProfile Plan,
        ImmutableArray<MmcMaterializedArtifactFact> Facts, ImmutableArray<MmcDerivedOutputRecord> Derived, ImmutableArray<MmcAssetIndexInput> AssetIndexes,
        MmcPackageBaselineInput Baseline, MmcPackageRevisionBindOptions Options);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pcllf-package-revision-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient client = new(new Offline());
        private readonly byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal Fixture() => Directory.CreateDirectory(root);
        internal Input Prepare() {
            var old = Import("old", 1); var incoming = Import("incoming", 2);
            File.WriteAllText(Path.Combine(old.Paths!.GameRoot, "config", "player.cfg"), "current player override");
            var current = MmcSavedSourceStore.ReadPrimary(old.Paths);
            var undo = new MmcInstanceHistoryBackend().CaptureAsync(new(old.Paths.InstanceRoot, current.Definition.InstanceId, current.Definition.Generation,
                current.Sha256, "before package update")).GetAwaiter().GetResult();
            Require(undo.Status == MmcInstanceBackendStatus.Succeeded && undo.Point is not null, "Real undo capture failed: " + undo.Failure);
            var definition = incoming.Definition!;
            var source = definition with { InstanceId = current.Definition.InstanceId, Generation = current.Definition.Generation, ParentInstanceId = current.Definition.ParentInstanceId,
                InstalledProjection = null, ArtifactInventory = [], ArtifactLocks = [], DerivedOutputs = [], PackageFileInventory = [], PackageFileLocks = [] };
            var facts = definition.ArtifactLocks.Select(value => new MmcMaterializedArtifactFact(value.ArtifactKey, value.SourceRevision,
                value.AdoptedSha256, value.SizeBytes, value.RecoveryObjectRelativePath, value.TargetRelativePath, value.ObservedSha1))
                .Concat(definition.PackageFileLocks.Select(value => new MmcMaterializedArtifactFact(value.ArtifactKey, value.SourceRevision,
                    value.AdoptedSha256, value.SizeBytes, value.RecoveryObjectRelativePath, value.TargetRelativePath, value.ObservedSha1))).ToImmutableArray();
            var assets = incoming.BoundPlan!.CompiledProfile.Assets.Artifact!;
            var baseline = new MmcPackageBaselineInput(MmcSavedSourceStore.ReadBounded(incoming.Paths!, definition.AuthorFileBaseline!.ObjectRelativePath, 64 * 1024 * 1024));
            var options = new MmcPackageRevisionBindOptions(current.Definition.InstanceId, current.Definition.Generation, current.Sha256,
                definition.Origin.PackageSha256, Hash("updated projection"), new(undo.Point!.PointId, undo.Point.ManifestSha256,
                    current.Definition.InstanceId, current.Definition.Generation, current.Sha256, current.Definition.InstalledProjection!.BoundFingerprint));
            return new(old.Paths, current, source, incoming.BoundPlan.CompiledProfile, facts, definition.DerivedOutputs, [new(assets.Key, index)], baseline, options);
        }
        private MmcInstanceBackendResult Import(string name, int version) {
            string archive = Path.Combine(root, name + ".zip"), target = Path.Combine(root, "game", "versions", name);
            string sha1 = Convert.ToHexStringLower(SHA1.HashData(index));
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
                Add("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
                Add("patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Main{{version}}","mainJar":{"name":"fixture:game:{{version}}","MMC-hint":"local","MMC-filename":"runtime.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/index.json","sha1":"{{sha1}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
                Add("libraries/runtime.jar", "library " + version); Add("minecraft/config/player.cfg", "author " + version);
                void Add(string path, string value) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(value); }
            }
            var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(new(Path.Combine(root, "metadata"), new Uri("https://metadata.invalid/"), client),
                new(Path.Combine(root, "libraries"), Path.Combine(root, "assets"), Path.Combine(root, "cache")), value => value,
                async (batch, token) => { foreach (var file in batch) await File.WriteAllBytesAsync(file.DownloadPath, index, token); });
            var result = backend.ImportAsync(new(archive, target, name, new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            Require(result.Status == MmcInstanceBackendStatus.Succeeded, "Package binding import failed: " + result.Failure); return result;
        }
        public void Dispose() { client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(root); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Offline : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Package binding fixture is offline."); }
}
