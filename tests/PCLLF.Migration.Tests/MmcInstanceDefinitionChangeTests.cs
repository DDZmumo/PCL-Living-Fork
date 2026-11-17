using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcInstanceDefinitionChangeTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC component changes preview effective launch fields and transact enable disable reset with fresh preferences", Switches);
        yield return ("MMC exact component version replaces same-name local bytes with complete undo and resolves new native dependencies", Revisions);
        yield return ("MMC component changes reject stale candidates, changed local inputs, unknown targets and session or pending barriers", Refusals);
        yield return ("MMC component transactions roll back predecision cancellation and recover a decided interruption", Transactions);
        yield return ("MMC component asset mapping preserves unknown and edited resources while allowing owned revisions and safe missing or identical targets", MappedResources);
        yield return ("MMC history rejects ordinary author to mapped asset role migration without claiming a partial restoration", MappedRoleRestore);
    }
    private static void MappedRoleRestore() {
        using Fixture fixture = new(authorResource: true); fixture.Import(); var primary = fixture.Primary();
        var captured = new MmcInstanceHistoryBackend().CaptureAsync(new(fixture.VersionRoot, primary.Definition.InstanceId, primary.Definition.Generation,
            primary.Sha256, "ordinary author resource") { FullSafeEnvironment = true }).GetAwaiter().GetResult();
        if (captured.Point is null) throw new Exception(captured.Failure);
        var a = Preview(fixture, new("net.minecraft", PatchContentUtf8: fixture.MinecraftPatch(fixture.MappedIndex("role-a", new() { ["owned.txt"] = "mapped A" }))));
        Require(fixture.Changes().CommitAsync(new(a, a.CandidateSha256)).GetAwaiter().GetResult());
        var c = Preview(fixture, new("net.minecraft", PatchContentUtf8: fixture.MinecraftPatch(fixture.MappedIndex("role-c", new() { ["owned.txt"] = "mapped C" }))));
        Require(fixture.Changes().CommitAsync(new(c, c.CandidateSha256)).GetAwaiter().GetResult());
        primary = fixture.Primary(); Equal("mapped C", fixture.Read("minecraft/resources/owned.txt"));
        var refused = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, primary.Definition.InstanceId, primary.Definition.Generation,
            primary.Sha256, captured.Point.PointId, captured.Point.ManifestSha256)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, refused.Status); Equal(null, refused.UndoPoint);
        Equal(primary.Sha256, fixture.Primary().Sha256); Equal("mapped C", fixture.Read("minecraft/resources/owned.txt"));
        var point = MmcInstanceHistoryStore.Read(MmcInstancePaths.Resolve(fixture.VersionRoot), captured.Point.PointId, captured.Point.ManifestSha256, CancellationToken.None);
        var original = point.Manifest.EnvironmentFiles.Single(value => value.RelativePath == "minecraft/resources/owned.txt");
        Equal("mapped A", File.ReadAllText(MmcInstanceHistoryEnvironmentObjects.Resolve(point.StorePaths, original)));
    }
    private static void MappedResources() {
        using Fixture fixture = new(); fixture.Import(); var backend = fixture.Changes();
        var indexA = fixture.MappedIndex("a", new() { ["owned.txt"] = "mapped A" });
        var first = Preview(fixture, new("net.minecraft", PatchContentUtf8: fixture.MinecraftPatch(indexA)));
        fixture.Put("minecraft/resources/owned.txt", "unknown resource"); string primary = fixture.Primary().Sha256;
        var unknown = backend.CommitAsync(new(first, first.CandidateSha256)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, unknown.Status); Equal(null, unknown.UndoPoint);
        Equal(primary, fixture.Primary().Sha256); Equal("unknown resource", fixture.Read("minecraft/resources/owned.txt"));
        File.Delete(Path.Combine(fixture.VersionRoot, "minecraft", "resources", "owned.txt"));
        var missing = backend.CommitAsync(new(first, first.CandidateSha256)).GetAwaiter().GetResult(); Require(missing);
        Equal(2L, fixture.Primary().Definition.Generation); Equal("mapped A", fixture.Read("minecraft/resources/owned.txt"));
        var indexB = fixture.MappedIndex("b", new() { ["owned.txt"] = "mapped B" });
        var next = Preview(fixture, new("net.minecraft", PatchContentUtf8: fixture.MinecraftPatch(indexB)));
        fixture.Put("minecraft/resources/owned.txt", "edited owned resource"); primary = fixture.Primary().Sha256;
        var edited = backend.CommitAsync(new(next, next.CandidateSha256)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, edited.Status); Equal(null, edited.UndoPoint);
        Equal(primary, fixture.Primary().Sha256); Equal("edited owned resource", fixture.Read("minecraft/resources/owned.txt"));
        fixture.Put("minecraft/resources/owned.txt", "mapped A");
        var raced = backend.CommitAsync(new(next, next.CandidateSha256) { BeforeTransaction = () => fixture.Put("minecraft/resources/owned.txt", "late edited resource") }).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.NeedsRecovery, raced.Status); Equal(true, raced.UndoPoint is not null);
        Equal(primary, fixture.Primary().Sha256); Equal("late edited resource", fixture.Read("minecraft/resources/owned.txt"));
        fixture.Put("minecraft/resources/owned.txt", "mapped A");
        var upgraded = backend.CommitAsync(new(next, next.CandidateSha256)).GetAwaiter().GetResult(); Require(upgraded);
        Equal(3L, fixture.Primary().Definition.Generation); Equal("mapped B", fixture.Read("minecraft/resources/owned.txt"));
        var current = fixture.Primary(); var undo = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, current.Definition.InstanceId,
            current.Definition.Generation, current.Sha256, upgraded.UndoPoint!.PointId, upgraded.UndoPoint.ManifestSha256)).GetAwaiter().GetResult();
        if (undo.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(undo.Failure);
        Equal(4L, fixture.Primary().Definition.Generation); Equal("mapped A", fixture.Read("minecraft/resources/owned.txt"));
        current = fixture.Primary(); var redo = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, current.Definition.InstanceId,
            current.Definition.Generation, current.Sha256, undo.UndoPoint!.PointId, undo.UndoPoint.ManifestSha256)).GetAwaiter().GetResult();
        if (redo.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(redo.Failure);
        Equal(5L, fixture.Primary().Definition.Generation); Equal("mapped B", fixture.Read("minecraft/resources/owned.txt"));
        var indexC = fixture.MappedIndex("c", new() { ["owned.txt"] = "mapped B", ["new.txt"] = "mapped C" });
        var added = Preview(fixture, new("net.minecraft", PatchContentUtf8: fixture.MinecraftPatch(indexC)));
        fixture.Put("minecraft/resources/new.txt", "unknown added target"); primary = fixture.Primary().Sha256;
        Equal(MmcInstanceBackendStatus.NeedsRecovery, backend.CommitAsync(new(added, added.CandidateSha256)).GetAwaiter().GetResult().Status);
        Equal(primary, fixture.Primary().Sha256); Equal("unknown added target", fixture.Read("minecraft/resources/new.txt"));
        fixture.Put("minecraft/resources/new.txt", "mapped C");
        Require(backend.CommitAsync(new(added, added.CandidateSha256)).GetAwaiter().GetResult());
        Equal(6L, fixture.Primary().Definition.Generation); Equal("mapped C", fixture.Read("minecraft/resources/new.txt"));
    }
    private static void Switches() {
        using Fixture fixture = new(); fixture.Import();
        using var provider = new InstanceConfigProvider(fixture.VersionRoot);
        var disabled = Preview(fixture, new("fixture.custom", Enabled: false));
        Equal(true, disabled.Differences.Any(value => value.Field == "mainClass" && value.After == "fixture.Base"));
        Equal(true, disabled.Differences.Any(value => value.Field == "jvmArguments"));
        var off = fixture.Changes().CommitAsync(new(disabled, disabled.CandidateSha256) { BeforeTransaction = () =>
            MmcIniConfiguration.QueuePreferenceBatch(provider, new Dictionary<string, string?> { ["MmcJvmArgsMode"] = "Custom", ["VersionAdvanceJvm"] = "" },
                new JObject { ["unknown-current"] = "preserve current" }) }).GetAwaiter().GetResult();
        Require(off); Equal(2L, off.Definition!.Generation); Equal("fixture.Base", off.BoundPlan!.CompiledProfile.MainClass);
        Equal(false, off.BoundPlan.CompiledProfile.Classpath.Any(value => value.ComponentUid == "fixture.custom"));
        Ready(fixture, 8, "fixture.Base");
        var enabled = Preview(fixture, new("fixture.custom", Enabled: true));
        var on = fixture.Changes().CommitAsync(new(enabled, enabled.CandidateSha256)).GetAwaiter().GetResult(); Require(on);
        Equal(3L, on.Definition!.Generation); Equal("fixture.Custom", on.BoundPlan!.CompiledProfile.MainClass);
        var preferences = MmcIniConfiguration.CapturePreferences(provider); Equal("", preferences.Ini["VersionAdvanceJvm"]);
        Equal("preserve current", preferences.Config.Value<string>("unknown-current"));
        File.Delete(Path.Combine(fixture.VersionRoot, "libraries", "custom.jar"));
        var repair = fixture.Backend().RepairAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8), on.Definition.InstanceId)).GetAwaiter().GetResult();
        if (repair.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(repair.Failure);
        Equal(4L, repair.Definition!.Generation); Equal("custom original", fixture.Read("libraries/custom.jar")); Ready(fixture, 8, "fixture.Custom");
        var reset = Preview(fixture, new("fixture.custom", Reset: true)); Require(fixture.Changes().CommitAsync(new(reset, reset.CandidateSha256)).GetAwaiter().GetResult());
        Equal(5L, fixture.Primary().Definition.Generation); Equal(0, fixture.Primary().Definition.ComponentOverrides.Length);
    }
    private static void Revisions() {
        using Fixture fixture = new(); fixture.Import();
        fixture.Put("minecraft/saves/world/level.dat", "current world"); fixture.Put(".minecraft/saves/world/level.dat", "alternate current world");
        string file = Path.Combine(fixture.Root, "replacement.jar"); File.WriteAllText(file, "custom revision two");
        var candidate = Preview(fixture, new("fixture.custom", ExactVersion: "2"), 21, new("custom.jar", file, Hash("custom revision two")));
        Equal(true, candidate.Differences.Any(value => value.Field == "javaPolicy")); Equal(true, candidate.Differences.Any(value => value.Field == "natives"));
        Equal(true, candidate.Differences.Any(value => value.Field == "component:fixture.dep"));
        var result = fixture.Changes().CommitAsync(new(candidate, candidate.CandidateSha256)).GetAwaiter().GetResult(); Require(result);
        Equal(2L, result.Definition!.Generation); Equal("custom revision two", fixture.Read("libraries/custom.jar"));
        Equal("fixture.Remote2", result.BoundPlan!.CompiledProfile.MainClass); Equal(1, result.BoundPlan.CompiledProfile.NativeArtifacts.Length);
        Equal(true, result.Definition.ArtifactLocks.Any(value => value.AdoptedSha256 == Hash("custom revision two")));
        Equal(false, result.Definition.ArtifactLocks.Any(value => value.TargetRelativePath == "libraries/custom.jar" && value.AdoptedSha256 == Hash("custom original")));
        Equal(true, result.Definition.SourceBundle.MetadataRecords.Any(value => value.ComponentSourceKind == MmcComponentSourceKind.RemoteMetadata && value.Uid == "fixture.dep"));
        File.Delete(Path.Combine(fixture.Root, "fixture.zip")); Directory.Delete(fixture.CacheRoot, true); Ready(fixture, 21, "fixture.Remote2");
        var current = fixture.Primary(); var restored = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, current.Definition.InstanceId,
            current.Definition.Generation, current.Sha256, result.UndoPoint!.PointId, result.UndoPoint.ManifestSha256)).GetAwaiter().GetResult();
        if (restored.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(restored.Failure);
        Equal(3L, restored.Definition!.Generation); Equal("custom original", fixture.Read("libraries/custom.jar")); Ready(fixture, 8, "fixture.Custom");
        current = fixture.Primary(); var late = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, current.Definition.InstanceId,
            current.Definition.Generation, current.Sha256, restored.UndoPoint!.PointId, restored.UndoPoint.ManifestSha256)).GetAwaiter().GetResult();
        if (late.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(late.Failure);
        Equal(4L, late.Definition!.Generation); Equal("custom revision two", fixture.Read("libraries/custom.jar")); Ready(fixture, 21, "fixture.Remote2");
        Equal("current world", fixture.Read("minecraft/saves/world/level.dat")); Equal("alternate current world", fixture.Read(".minecraft/saves/world/level.dat"));
    }
    private static void Refusals() {
        using Fixture fixture = new(); fixture.Import(); var backend = fixture.Changes();
        Equal(MmcInstanceBackendStatus.Rejected, backend.PreviewAsync(Request(fixture, [new("fixture.custom", Reset: true)])).GetAwaiter().GetResult().Status);
        var candidate = Preview(fixture, new("fixture.custom", PatchContentUtf8: fixture.CustomPatch("1", "custom original", "fixture.Edited", "-Dedited=true")));
        string before = fixture.Primary().Sha256;
        Equal(MmcInstanceBackendStatus.NeedsRecovery, backend.CommitAsync(new(candidate, new string('e', 64))).GetAwaiter().GetResult().Status);
        foreach (string marker in new[] { "PCL/MmcImportPending.json", "PCL/MmcSessions/uncertain/receipt.record" }) {
            fixture.Put(marker, "foreign pending"); Equal(MmcInstanceBackendStatus.NeedsRecovery, backend.CommitAsync(new(candidate, candidate.CandidateSha256)).GetAwaiter().GetResult().Status);
            Equal(before, fixture.Primary().Sha256); Equal("foreign pending", fixture.Read(marker));
            File.Delete(Path.Combine(fixture.VersionRoot, marker.Replace('/', Path.DirectorySeparatorChar)));
            if (marker.Contains("MmcSessions", StringComparison.Ordinal)) Directory.Delete(Path.Combine(fixture.VersionRoot, "PCL", "MmcSessions", "uncertain"));
        }
        Equal(MmcInstanceBackendStatus.Rejected, backend.PreviewAsync(Request(fixture, [new("unknown.required", Enabled: true)])).GetAwaiter().GetResult().Status);
        string unknown = fixture.CustomPatch("1", "custom original", "fixture.Edited", "-Dedited=true").Replace("\"mainClass\"", "\"unknownCriticalField\":true,\"mainClass\"");
        Equal(MmcInstanceBackendStatus.Rejected, backend.PreviewAsync(Request(fixture, [new("fixture.custom", PatchContentUtf8: unknown)])).GetAwaiter().GetResult().Status);
        string file = Path.Combine(fixture.Root, "replacement.jar"); File.WriteAllText(file, "custom revision two");
        var fixedInput = Preview(fixture, new("fixture.custom", ExactVersion: "2"), 21, new("custom.jar", file, Hash("custom revision two")));
        File.WriteAllText(file, "changed after preview"); Equal(MmcInstanceBackendStatus.NeedsRecovery, backend.CommitAsync(new(fixedInput, fixedInput.CandidateSha256)).GetAwaiter().GetResult().Status);
        Equal(before, fixture.Primary().Sha256);
        fixture.Put("libraries/new.jar", "unknown personal neighbor"); File.WriteAllText(file, "new declared library");
        var patch = JObject.Parse(fixture.CustomPatch("1", "custom original", "fixture.Edited", "-Dedited=true"));
        patch["+libraries"] = new JArray(new JObject { ["name"] = "fixture:new:1", ["MMC-hint"] = "local", ["MMC-filename"] = "new.jar",
            ["downloads"] = new JObject { ["artifact"] = new JObject { ["sha256"] = Hash("new declared library"), ["size"] = Encoding.UTF8.GetByteCount("new declared library") } } });
        var refused = backend.PreviewAsync(Request(fixture, [new("fixture.custom", PatchContentUtf8: patch.ToString())]) with {
            LocalInputs = [new("new.jar", file, Hash("new declared library"))] }).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.Rejected, refused.Status); Equal("unknown personal neighbor", fixture.Read("libraries/new.jar"));
        var changed = backend.CommitAsync(new(candidate, candidate.CandidateSha256)).GetAwaiter().GetResult(); Require(changed);
        Equal(MmcInstanceBackendStatus.NeedsRecovery, backend.CommitAsync(new(candidate, candidate.CandidateSha256)).GetAwaiter().GetResult().Status);
        Ready(fixture, 8, "fixture.Edited");
    }
    private static void Transactions() {
        using Fixture fixture = new(); fixture.Import();
        foreach (string mode in new[] { "cancel-before", "interrupt-before", "interrupt-decided", "cancel-decided" }) {
            var candidate = Preview(fixture, new("fixture.custom", PatchContentUtf8: fixture.CustomPatch("1", "custom original", "fixture." + mode.Replace('-', '_'), "-Dphase=" + mode)));
            var before = fixture.Primary(); using CancellationTokenSource cancellation = new(); bool decided = mode.EndsWith("decided", StringComparison.Ordinal);
            var result = fixture.Changes().CommitAsync(new(candidate, candidate.CandidateSha256) { TransactionCheckpoint = (phase, _) => {
                if (phase != (decided ? MmcTransactionCheckpoint.CommitDecided : MmcTransactionCheckpoint.Prepared)) return;
                if (mode.StartsWith("cancel", StringComparison.Ordinal)) cancellation.Cancel(); else throw new MmcTransactionInterruptionException("controlled revision interruption");
            } }, cancellation.Token).GetAwaiter().GetResult();
            if (mode.StartsWith("interrupt", StringComparison.Ordinal)) {
                Equal(MmcInstanceBackendStatus.NeedsRecovery, result.Status); if (result.UndoPoint is null) throw new Exception(result.Failure);
                using var quiet = MmcConfigurationCoordinator.Quiesce(fixture.VersionRoot); using var lease = MmcAdmissionGate.Acquire(fixture.VersionRoot, before.Definition.InstanceId, TimeSpan.Zero);
                var recovered = MmcFileTransactions.RecoverPending(lease); Equal(1, recovered.Count); Equal(decided ? MmcTransactionOutcome.Committed : MmcTransactionOutcome.Aborted, recovered[0].Outcome);
            } else Equal(decided ? MmcInstanceBackendStatus.Succeeded : MmcInstanceBackendStatus.Cancelled, result.Status);
            Equal(before.Definition.Generation + (decided ? 1 : 0), fixture.Primary().Definition.Generation);
            if (!decided) Equal(before.Sha256, fixture.Primary().Sha256);
            Ready(fixture, 8, decided ? "fixture." + mode.Replace('-', '_') : before.Definition.Generation == 1 ? "fixture.Custom" : "fixture.interrupt_decided");
        }
    }
    private static MmcInstanceDefinitionChangeRequest Request(Fixture fixture, ImmutableArray<MmcInstanceComponentEdit> edits, int java = 8) {
        var current = fixture.Primary(); return new(fixture.VersionRoot, current.Definition.InstanceId, current.Definition.Generation, current.Sha256, new("windows", "x86_64", java), edits);
    }
    private static MmcInstanceDefinitionChangePreview Preview(Fixture fixture, MmcInstanceComponentEdit edit, int java = 8, MmcInstanceLocalLibraryInput? input = null) {
        var result = fixture.Changes().PreviewAsync(Request(fixture, [edit], java) with { LocalInputs = input is null ? [] : [input] }).GetAwaiter().GetResult();
        if (result.Status != MmcInstanceBackendStatus.Succeeded || result.Preview is null) throw new Exception(result.Failure); return result.Preview;
    }
    private static void Require(MmcInstanceDefinitionChangeResult result) { if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure); }
    private static void Ready(Fixture fixture, int java, string main) {
        var current = fixture.Primary(); var prepared = fixture.Backend().PrepareLaunchAsync(new(fixture.VersionRoot, new("windows", "x86_64", java), current.Definition.InstanceId, current.Definition.Generation) { ExpectedPrimarySha256 = current.Sha256 }).GetAwaiter().GetResult();
        if (prepared.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(prepared.Failure); Equal(false, prepared.Repaired); Equal(main, prepared.Snapshot!.BoundPlan.CompiledProfile.MainClass);
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-revisions-" + Guid.NewGuid().ToString("N"));
        internal string VersionRoot => Path.Combine(Root, "game", "versions", "fixture");
        internal string CacheRoot => Path.Combine(Root, "cache");
        private readonly byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        private readonly byte[] native;
        private readonly HttpClient client;
        private readonly Dictionary<string, byte[]> mappedSources = new(StringComparer.Ordinal);
        internal Fixture(bool authorResource = false) {
            using (MemoryStream bytes = new()) { using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(zip.CreateEntry("native.dll").Open()); writer.Write("controlled native bytes"); } native = bytes.ToArray(); }
            client = new(new Metadata(this)); Directory.CreateDirectory(Root);
            using var pack = ZipFile.Open(Path.Combine(Root, "fixture.zip"), ZipArchiveMode.Create);
            Add("pack/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},{"uid":"fixture.custom","version":"1"}]}""");
            Add("pack/instance.cfg", "name=Fixture\n");
            Add("pack/patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Base","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"game.jar"},"assetIndex":{"id":"fixture","url":"https://files.invalid/index.json","sha1":"{{Convert.ToHexStringLower(SHA1.HashData(index))}}","size":{{index.Length}}},"compatibleJavaMajors":[8,21]}""");
            Add("pack/patches/fixture.custom.json", CustomPatch("1", "custom original", "fixture.Custom", "-Dcustom=1"));
            Add("pack/libraries/game.jar", "game original"); Add("pack/libraries/custom.jar", "custom original"); Add("pack/minecraft/config/test.cfg", "player config"); Add("readme.txt", "opaque provenance");
            if (authorResource) Add("pack/minecraft/resources/owned.txt", "mapped A");
            void Add(string path, string text) { using var writer = new StreamWriter(pack.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        internal string CustomPatch(string version, string bytes, string main, string jvm) => new JObject {
            ["formatVersion"] = 1, ["uid"] = "fixture.custom", ["version"] = version, ["mainClass"] = main, ["+jvmArgs"] = new JArray(jvm),
            ["+libraries"] = new JArray(new JObject { ["name"] = "fixture:custom:" + version, ["MMC-hint"] = "local", ["MMC-filename"] = "custom.jar",
                ["downloads"] = new JObject { ["artifact"] = new JObject { ["sha256"] = Hash(bytes), ["size"] = Encoding.UTF8.GetByteCount(bytes) } } })
        }.ToString(Newtonsoft.Json.Formatting.None);
        internal JObject MappedIndex(string tag, Dictionary<string, string> contents) {
            JObject objects = new();
            foreach (var item in contents.OrderBy(value => value.Key, StringComparer.Ordinal)) {
                byte[] bytes = Encoding.UTF8.GetBytes(item.Value); string sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
                mappedSources["https://resources.download.minecraft.net/" + sha1[..2] + "/" + sha1] = bytes;
                objects[item.Key] = new JObject { ["hash"] = sha1, ["size"] = bytes.Length };
            }
            byte[] indexBytes = Encoding.UTF8.GetBytes(new JObject { ["objects"] = objects, ["map_to_resources"] = true }.ToString(Newtonsoft.Json.Formatting.None));
            string url = "https://files.invalid/index-" + tag + ".json"; mappedSources[url] = indexBytes;
            return new JObject { ["id"] = tag, ["url"] = url, ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(indexBytes)), ["size"] = indexBytes.Length };
        }
        internal string MinecraftPatch(JObject indexDeclaration) {
            var current = Primary().Definition;
            var record = current.SourceBundle.MetadataRecords.Single(value => value.Kind == MmcSourceRecordKind.Component && value.Uid == "net.minecraft" && value.ComponentSourceKind == MmcComponentSourceKind.PackageLocal);
            var patch = JObject.Parse(Encoding.UTF8.GetString(MmcSavedSourceStore.ReadBounded(MmcInstancePaths.Resolve(VersionRoot), record.ObjectRelativePath, 16 * 1024 * 1024)));
            patch["assetIndex"] = indexDeclaration.DeepClone(); return patch.ToString(Newtonsoft.Json.Formatting.None);
        }
        private MmcMetadataStore Store() => new(Path.Combine(CacheRoot, "metadata"), new Uri("https://metadata.invalid/"), client);
        private MmcArtifactCacheRoots Caches() => new(Path.Combine(CacheRoot, "libraries"), Path.Combine(CacheRoot, "assets"), Path.Combine(CacheRoot, "sources"));
        private async Task Downloads(ImmutableArray<MmcArtifactDownloadSpec> files, CancellationToken token) {
            foreach (var file in files) {
                byte[]? mapped = file.Urls.Select(value => mappedSources.TryGetValue(value, out var bytes) ? bytes : null).FirstOrDefault(value => value is not null);
                await File.WriteAllBytesAsync(file.DownloadPath, mapped ?? (file.Artifact.Role == MmcArtifactRole.AssetIndex ? index : native), token);
            }
        }
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(Store(), Caches(), value => value, Downloads);
        internal MmcInstanceDefinitionChangeBackend<MmcArtifactDownloadSpec> Changes() => new(Store(), Caches(), value => value, Downloads);
        internal void Import() { var result = Backend().ImportAsync(new(Path.Combine(Root, "fixture.zip"), VersionRoot, "fixture", new("windows", "x86_64", 8))).GetAwaiter().GetResult(); if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure); }
        internal MmcStoredPrimaryDefinition Primary() => MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(VersionRoot));
        internal void Put(string relative, string content) { string path = Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        internal string Read(string relative) => File.ReadAllText(Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        public void Dispose() { client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        private sealed class Metadata(Fixture fixture) : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
                string text;
                if (request.RequestUri!.AbsolutePath.EndsWith("fixture.custom/2.json", StringComparison.Ordinal)) {
                    var patch = JObject.Parse(fixture.CustomPatch("2", "custom revision two", "fixture.Remote2", "-Dcustom=2"));
                    patch["compatibleJavaMajors"] = new JArray(17, 21); patch["requires"] = new JArray(new JObject { ["uid"] = "fixture.dep", ["equals"] = "1" });
                    ((JArray)patch["+libraries"]!).Add(new JObject { ["name"] = "fixture:native:2", ["natives"] = new JObject { ["windows"] = "natives-windows" },
                        ["downloads"] = new JObject { ["classifiers"] = new JObject { ["natives-windows"] = new JObject { ["url"] = "https://files.invalid/native.jar",
                            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(fixture.native)), ["size"] = fixture.native.Length } } } }); text = patch.ToString();
                } else if (request.RequestUri.AbsolutePath.EndsWith("fixture.dep/1.json", StringComparison.Ordinal)) text = """{"formatVersion":1,"uid":"fixture.dep","version":"1"}""";
                else throw new IOException("Unexpected controlled metadata URI: " + request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
            }
        }
    }
}
