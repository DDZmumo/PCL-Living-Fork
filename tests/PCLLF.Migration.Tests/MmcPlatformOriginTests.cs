using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcPlatformOriginTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC platform evidence validates exact MR secondary files and refuses ambiguous or changed API relationships", Relationships);
        yield return ("MMC platform evidence requires CF game modpack type availability SHA1 and measured ZIP size", CurseForgeBoundaries);
        yield return ("MMC CF official-shaped HTTP metadata attaches only the exact available SHA1 verified original ZIP", CurseForgeHttp);
        yield return ("MMC historical platform strings preserve null shape and cannot become verified from a mutable mirror", HistoricalIdentity);
        yield return ("MMC original ZIP verification survives rename and roundtrips identity through clone history online ZIP and unverified local ZIP", Continuity);
        yield return ("MMC platform attach rejects wrong original digests stale publication and changed confirmed metadata", AttachRefusals);
        yield return ("MMC platform online acquisition rejects wrong project spoofed file hashes size and real non-MMC containers", OnlineRefusals);
        yield return ("MMC platform hash discovery keeps all exact candidates and online cancellation awaits the real batch drain", CandidatesAndDrain);
        yield return ("MMC repair and component revisions reject late deleted or replaced platform evidence before publication", EvidencePublicationRaces);
        yield return ("MMC online Java selection runs after the genuine container reader and cancellation awaits the actual selector", EnvironmentSelection);
    }
    private static void Relationships() {
        byte[] bytes = Encoding.UTF8.GetBytes("measured original"); var measured = Measurement(bytes);
        var identity = new MmcPlatformProjectIdentity("Modrinth", "ProjectA", "VersionA");
        var wrong = ModrinthFile(Encoding.UTF8.GetBytes("first primary is different"), "first.zip", "https://cdn.modrinth.com/first.zip");
        var actual = ModrinthFile(bytes, "release.zip", "https://cdn.modrinth.com/actual.zip");
        string project = """{"id":"ProjectA","project_type":"modpack"}""";
        JObject version = new() { ["id"] = "VersionA", ["project_id"] = "ProjectA", ["files"] = new JArray(wrong, actual) };
        var release = MmcPlatformOriginCodec.ReadRelease(identity, "https://api.modrinth.com/v2/project/ProjectA", "https://api.modrinth.com/v2/version/VersionA", project, version.ToString());
        string fileId = "sha512:" + measured.Sha512;
        Equal("release.zip", MmcPlatformOriginCodec.SelectFile(release, fileId).FileName);
        byte[] evidence = MmcPlatformOriginCodec.Create(release, fileId, measured);
        var origin = new MmcInstanceOrigin(measured.Sha256, "renamed.zip", identity) { PlatformEvidenceReference = MmcPlatformOriginCodec.Reference(evidence) };
        Equal(fileId, MmcPlatformOriginCodec.Read(evidence, origin).SelectedFile.FileId);
        Throws(() => MmcPlatformOriginCodec.Create(release, release.Files[0].FileId, measured));
        foreach (var mutation in new Action<JObject>[] { value => value["project_id"] = "OtherProject", value => value["id"] = "OtherVersion" }) {
            var changed = (JObject)version.DeepClone(); mutation(changed);
            Throws(() => MmcPlatformOriginCodec.ReadRelease(identity, release.ProjectSource, release.VersionSource, project, changed.ToString()));
        }
        Throws(() => MmcPlatformOriginCodec.ReadRelease(identity, release.ProjectSource, release.VersionSource, project.Replace("modpack", "mod", StringComparison.Ordinal), version.ToString()));
        var duplicate = (JObject)version.DeepClone(); ((JArray)duplicate["files"]!).Add(actual.DeepClone());
        var ambiguous = MmcPlatformOriginCodec.ReadRelease(identity, release.ProjectSource, release.VersionSource, project, duplicate.ToString());
        Throws(() => MmcPlatformOriginCodec.SelectFile(ambiguous, fileId));
        var separate = (JObject)version.DeepClone(); var secondName = (JObject)actual.DeepClone(); secondName["filename"] = "explicit-other.zip"; secondName["url"] = "https://cdn.modrinth.com/other.zip";
        ((JArray)separate["files"]!).Add(secondName);
        var equalDigest = MmcPlatformOriginCodec.ReadRelease(identity, release.ProjectSource, release.VersionSource, project, separate.ToString());
        Throws(() => MmcPlatformOriginCodec.SelectFile(equalDigest, fileId));
        _ = MmcPlatformOriginCodec.Create(equalDigest, equalDigest.Files.Single(value => value.FileName == "explicit-other.zip"), measured);
        Throws(() => MmcPlatformOriginCodec.Read(evidence, origin with { PackageSha256 = new string('a', 64) }));
        evidence[^1] ^= 1; Throws(() => MmcPlatformOriginCodec.Read(evidence, origin));
    }
    private static void CurseForgeBoundaries() {
        byte[] bytes = Encoding.UTF8.GetBytes("CF original ZIP bytes"); var measured = Measurement(bytes);
        JObject project = new() { ["id"] = 123, ["gameId"] = 432, ["classId"] = 4471, ["isAvailable"] = true };
        JObject file = new() { ["id"] = 456, ["modId"] = 123, ["gameId"] = 432, ["isAvailable"] = true, ["fileName"] = "pack.zip", ["fileLength"] = bytes.Length,
            ["downloadUrl"] = "https://edge.forgecdn.net/files/pack.zip", ["hashes"] = new JArray(new JObject { ["algo"] = 1, ["value"] = measured.Sha1 }) };
        var id = new MmcPlatformProjectIdentity("CurseForge", "123", "456");
        MmcPlatformRelease Read(JObject p, JObject f) => MmcPlatformOriginCodec.ReadRelease(id, "https://api.curseforge.com/v1/mods/123", "https://api.curseforge.com/v1/mods/123/files/456", p.ToString(), f.ToString());
        var release = Read(project, file); byte[] evidence = MmcPlatformOriginCodec.Create(release, "456", measured);
        Equal("456", MmcPlatformOriginCodec.Read(evidence, new(measured.Sha256, "renamed.zip", id) { PlatformEvidenceReference = MmcPlatformOriginCodec.Reference(evidence) }).Identity.VersionId);
        foreach (var mutation in new Action<JObject>[] { value => value["modId"] = 124, value => value["id"] = 457, value => value["gameId"] = 999,
            value => value["isAvailable"] = false, value => value["hashes"] = new JArray(new JObject { ["algo"] = 2, ["value"] = new string('a', 32) }) }) {
            var changed = (JObject)file.DeepClone(); mutation(changed); Throws(() => Read(project, changed));
        }
        var wrongType = (JObject)project.DeepClone(); wrongType["classId"] = 6; Throws(() => Read(wrongType, file));
        Throws(() => MmcPlatformOriginCodec.Create(release, "456", measured with { SizeBytes = measured.SizeBytes + 1 }));
        Throws(() => MmcPlatformOriginCodec.Create(release, "456", measured with { Sha1 = new string('a', 40) }));
    }
    private static void HistoricalIdentity() {
        var old = MmcDefinitionTests.Published(); string json = MmcDefinitionCodec.Serialize(old);
        Equal(false, json.Contains("platformEvidenceReference", StringComparison.Ordinal));
        var legacy = old with { Origin = old.Origin with { PlatformProjectIdentity = new("Modrinth", "ProjectA", "VersionA") } };
        legacy = legacy with { InstalledProjection = legacy.InstalledProjection! with { BoundFingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(legacy) } };
        Equal(MmcDefinitionReadStatus.Valid, MmcDefinitionCodec.Read(MmcDefinitionCodec.Serialize(legacy)).Status);
        Equal(old.InstalledProjection!.BoundFingerprint, MmcDefinitionCodec.ComputeBoundFingerprint(old with { Origin = old.Origin with { PlatformEvidenceReference = null } }));
        using Fixture fixture = new(); fixture.Import(fixture.Pack("A", "runtimeA", "author A"));
        fixture.Put("PCL/ModpackOrigin.json", """{"source":{"platform":"Modrinth","projectId":"ProjectA","versionId":"VersionA"},"verified":true}""");
        Equal(null, MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection()));
        Throws(() => fixture.Origin.GetAvailableReleasesAsync(fixture.Selection()).GetAwaiter().GetResult());
    }
    private static void CurseForgeHttp() {
        using Fixture fixture = new(); string a = fixture.Pack("CF", "runtimeCF", "CF author"); fixture.Import(a); fixture.PublishCurseForge(a);
        MmcPlatformReleaseDescriptor Get() => fixture.Catalog.GetReleaseAsync(ResourcePlatforms.CurseForge, "123", "456").GetAwaiter().GetResult().Single();
        var selected = Get(); var attached = fixture.Attach(a, selected);
        Equal("CurseForge", attached.Definition!.Origin.PlatformProjectIdentity!.Platform);
        Equal("456", MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection())!.SelectedFile.FileId);
        foreach (var mutation in new Action<JToken>[] { file => file["modId"] = 124, file => file["isAvailable"] = false,
            file => file["hashes"] = new JArray(new JObject { ["algo"] = 2, ["value"] = new string('a', 32) }) }) {
            fixture.PublishCurseForge(a); mutation(fixture.Server.Json["/v1/mods/123/files/456"]["data"]!); Throws(() => Get());
        }
        fixture.PublishCurseForge(a); fixture.Server.Json["/v1/mods/123"]["data"]!["classId"] = 6; Throws(() => Get());
    }
    private static void EvidencePublicationRaces() {
        foreach (bool component in new[] { false, true }) foreach (bool replace in new[] { false, true }) {
            using Fixture fixture = new(); string zip = fixture.Pack("race", "runtime", "author"); fixture.Import(zip);
            fixture.Publish("VersionA", zip); fixture.Attach(zip, fixture.Release("VersionA")); var before = fixture.Primary();
            string evidence = Path.Combine(fixture.VersionRoot, before.Definition.Origin.PlatformEvidenceReference!.ObjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            int changes = 0;
            void Mutate() { changes++; if (replace) File.WriteAllText(evidence, "late foreign evidence"); else File.Delete(evidence); }
            MmcInstanceBackendStatus status;
            if (component) {
                using var archive = ZipFile.OpenRead(zip); using var input = new StreamReader(archive.GetEntry("patches/net.minecraft.json")!.Open());
                var patch = JObject.Parse(input.ReadToEnd()); patch["mainClass"] = "fixture.Changed";
                var preview = fixture.Changes().PreviewAsync(new(fixture.VersionRoot, before.Definition.InstanceId, before.Definition.Generation,
                    before.Sha256, new("windows", "x86_64", 8), [new("net.minecraft", PatchContentUtf8: patch.ToString())])).GetAwaiter().GetResult();
                Require(preview.Status, preview.Failure);
                status = fixture.Changes().CommitAsync(new(preview.Preview!, preview.Preview!.CandidateSha256) { BeforeTransaction = Mutate }).GetAwaiter().GetResult().Status;
            } else {
                status = fixture.Backend().RepairAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8), before.Definition.InstanceId) {
                    ExpectedGeneration = before.Definition.Generation, ExpectedPrimarySha256 = before.Sha256
                }, progress: new InlineProgress(value => { if (value.Phase == "publishing-repair") Mutate(); })).GetAwaiter().GetResult().Status;
            }
            Equal(1, changes); Equal(false, status == MmcInstanceBackendStatus.Succeeded);
            Equal(before.Sha256, fixture.Primary().Sha256); Equal(before.Definition.Generation, fixture.Primary().Definition.Generation);
            Equal("author", fixture.Read("minecraft/config/author.cfg")); Equal("runtime", fixture.Read("libraries/runtime.jar"));
            Equal(replace, File.Exists(evidence)); if (replace) Equal("late foreign evidence", File.ReadAllText(evidence));
        }
    }
    private sealed class InlineProgress(Action<MmcInstanceBackendProgress> report) : IProgress<MmcInstanceBackendProgress> {
        public void Report(MmcInstanceBackendProgress value) => report(value);
    }
    private static void Continuity() {
        using Fixture fixture = new(); string a = fixture.Pack("A", "runtimeA", "author A"); fixture.Import(a);
        fixture.Publish("VersionA", a, secondary: true); var selected = fixture.Release("VersionA");
        string renamed = Path.Combine(fixture.Root, "renamed-original.zip"); File.Copy(a, renamed);
        var before = fixture.Primary(); var attached = fixture.Attach(renamed, selected);
        Equal(before.Definition.InstanceId, attached.Definition!.InstanceId); Equal(before.Definition.Generation + 1, attached.Definition.Generation);
        Equal(System.Text.Json.JsonSerializer.Serialize(before.Definition.SourceBundle), System.Text.Json.JsonSerializer.Serialize(attached.Definition.SourceBundle));
        Equal(true, before.Definition.ComponentOverrides.SequenceEqual(attached.Definition.ComponentOverrides));
        Equal("author A", fixture.Read("minecraft/config/author.cfg")); Equal("runtimeA", fixture.Read("libraries/runtime.jar"));
        Equal("VersionA", MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection())!.Identity.VersionId);
        var p = fixture.Primary(); string cloneRoot = Path.Combine(fixture.Root, "game", "versions", "cloned");
        var savedCurrent = MmcSavedSourceStore.LoadAsync(MmcInstancePaths.Resolve(fixture.VersionRoot), p.Definition).GetAwaiter().GetResult();
        var compiledCurrent = MmcInstanceCloneBackend.CompileEnvironment(savedCurrent.Resolved.Resolution, p.Definition, []);
        string fakeHash = new('f', 64); var changedEvidence = p.Definition with { InstalledProjection = null,
            Origin = p.Definition.Origin with { PlatformEvidenceReference = new(fakeHash, "PCL/MmcSource/platform-origin/" + fakeHash + ".json") } };
        var forbiddenRepair = MmcPlanBinder.Bind(changedEvidence, compiledCurrent, [], new(p.Definition.Generation, p.Definition.Generation, MmcBindingIntent.RepairExisting,
            p.Definition.InstalledProjection!.OutputJsonSha256, p.Definition.Generation), publishedBaseline: p.Definition);
        Equal(true, forbiddenRepair.Diagnostics.Any(value => value.Code == "MMC_BIND_SOURCE_CHANGE"));
        var clone = new MmcInstanceCloneBackend().CloneAsync(new(fixture.VersionRoot, cloneRoot, "cloned", p.Definition.InstanceId, p.Definition.Generation, p.Sha256, new())).GetAwaiter().GetResult();
        Require(clone.Status, clone.Failure); Equal(p.Definition.Origin.PlatformEvidenceReference, clone.Definition!.Origin.PlatformEvidenceReference);
        _ = MmcSavedSourceStore.LoadAsync(MmcInstancePaths.Resolve(cloneRoot), clone.Definition).GetAwaiter().GetResult();
        var captured = new MmcInstanceHistoryBackend().CaptureAsync(new(fixture.VersionRoot, p.Definition.InstanceId, p.Definition.Generation, p.Sha256, "verified origin")).GetAwaiter().GetResult();
        Require(captured.Status, captured.Failure);
        var stored = MmcInstanceHistoryStore.Read(MmcInstancePaths.Resolve(fixture.VersionRoot), captured.Point!.PointId, captured.Point.ManifestSha256, CancellationToken.None);
        Equal(true, stored.Manifest.SourceFiles.Any(value => value.RelativePath == p.Definition.Origin.PlatformEvidenceReference!.ObjectRelativePath));
        string b = fixture.Pack("B", "runtimeB", "author B"); fixture.Publish("VersionB", b, secondary: true);
        var list = fixture.Origin.GetAvailableReleasesAsync(fixture.Selection()).GetAwaiter().GetResult();
        Equal(true, list.Any(value => value.VersionId == "VersionB" && value.File.FileName == "VersionB.zip"));
        var online = fixture.Online.PrepareAsync(new(fixture.Selection(), fixture.Release("VersionB"), new("windows", "x86_64", 8))).GetAwaiter().GetResult();
        var preview = fixture.Ready(online); var updated = fixture.Online.CommitAsync(new(preview, preview.PreviewSha256)).GetAwaiter().GetResult(); Require(updated.Status, updated.Failure);
        Equal("VersionB", MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection())!.Identity.VersionId);
        Equal(MmcDurableFiles.Observe(b), fixture.Primary().Definition.Origin.PackageSha256); Equal("author B", fixture.Read("minecraft/config/author.cfg")); Equal("runtimeB", fixture.Read("libraries/runtime.jar"));
        p = fixture.Primary(); var restored = new MmcInstanceHistoryBackend().RestoreAsync(new(fixture.VersionRoot, p.Definition.InstanceId, p.Definition.Generation,
            p.Sha256, captured.Point.PointId, captured.Point.ManifestSha256)).GetAwaiter().GetResult(); Require(restored.Status, restored.Failure);
        Equal("VersionA", MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection())!.Identity.VersionId);
        string c = fixture.Pack("C", "runtimeC", "author C"); p = fixture.Primary();
        var local = fixture.Updates.PrepareAsync(new(fixture.VersionRoot, p.Definition.InstanceId, p.Definition.Generation, p.Sha256, c, new("windows", "x86_64", 8))).GetAwaiter().GetResult();
        preview = fixture.Ready(local); updated = fixture.Updates.CommitAsync(new(preview, preview.PreviewSha256)).GetAwaiter().GetResult(); Require(updated.Status, updated.Failure);
        Equal(null, updated.Definition!.Origin.PlatformProjectIdentity); Equal(null, updated.Definition.Origin.PlatformEvidenceReference);
        Equal(null, MmcInstancePlatformOriginBackend.ReadVerified(fixture.Selection()));
    }
    private static void AttachRefusals() {
        using Fixture fixture = new(); string a = fixture.Pack("A", "runtimeA", "A"); fixture.Import(a); fixture.Publish("VersionA", a);
        var selected = fixture.Release("VersionA"); var primary = fixture.Primary();
        string wrong = fixture.Pack("other", "runtimeOther", "other");
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Origin.PreviewAsync(new(fixture.Selection(), wrong, selected)).GetAwaiter().GetResult().Status);
        var good = fixture.Origin.PreviewAsync(new(fixture.Selection(), a, selected)).GetAwaiter().GetResult(); Require(good.Status, good.Failure);
        fixture.Server.Json["/v2/version/VersionA"]!["project_id"] = "OtherProject";
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Origin.CommitAsync(new(good.Preview!, good.Preview!.CandidateSha256)).GetAwaiter().GetResult().Status); Equal(primary.Sha256, fixture.Primary().Sha256);
        fixture.Publish("VersionA", a); selected = fixture.Release("VersionA"); good = fixture.Origin.PreviewAsync(new(fixture.Selection(), a, selected)).GetAwaiter().GetResult();
        fixture.Attach(a, selected);
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Origin.CommitAsync(new(good.Preview!, good.Preview!.CandidateSha256)).GetAwaiter().GetResult().Status);
        File.Delete(a); Equal(MmcInstanceBackendStatus.Rejected, fixture.Origin.PreviewAsync(new(fixture.Selection(), a, selected)).GetAwaiter().GetResult().Status);
    }
    private static void OnlineRefusals() {
        using Fixture fixture = new(); string a = fixture.Pack("A", "runtimeA", "A"); fixture.Import(a); fixture.Publish("VersionA", a); fixture.Attach(a, fixture.Release("VersionA"));
        string b = fixture.Pack("B", "runtimeB", "B"); fixture.Publish("VersionB", b); var selected = fixture.Release("VersionB"); string before = fixture.Primary().Sha256;
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Online.PrepareAsync(new(fixture.Selection(), selected with { ProjectId = "OtherProject" }, new("windows", "x86_64", 8))).GetAwaiter().GetResult().Status);
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Online.PrepareAsync(new(fixture.Selection(), selected with { File = selected.File with { FileName = "spoof.zip" } }, new("windows", "x86_64", 8))).GetAwaiter().GetResult().Status);
        fixture.Server.Bytes["/downloads/VersionB.zip"] = Encoding.UTF8.GetBytes("different payload");
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Online.PrepareAsync(new(fixture.Selection(), selected, new("windows", "x86_64", 8))).GetAwaiter().GetResult().Status);
        fixture.Publish("VersionB", b); var sizeChanged = fixture.Server.Json["/v2/version/VersionB"]!; sizeChanged["files"]![0]!["size"] = new FileInfo(b).Length + 1;
        selected = fixture.Release("VersionB"); Equal(MmcInstanceBackendStatus.Rejected, fixture.Online.PrepareAsync(new(fixture.Selection(), selected, new("windows", "x86_64", 8))).GetAwaiter().GetResult().Status);
        string ordinary = Path.Combine(fixture.Root, "ordinary.mrpack"); using (var zip = ZipFile.Open(ordinary, ZipArchiveMode.Create)) { using var output = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()); output.Write("{}"); }
        fixture.Publish("Ordinary", ordinary); selected = fixture.Release("Ordinary");
        Equal(MmcInstanceBackendStatus.Rejected, fixture.Online.PrepareAsync(new(fixture.Selection(), selected, new("windows", "x86_64", 8))).GetAwaiter().GetResult().Status); Equal(before, fixture.Primary().Sha256);
        fixture.Publish("VersionB", b); selected = fixture.Release("VersionB"); var preview = fixture.Ready(fixture.Online.PrepareAsync(new(fixture.Selection(), selected, new("windows", "x86_64", 8))).GetAwaiter().GetResult());
        fixture.Server.Json["/v2/version/VersionB"]!["files"]![0]!["size"] = new FileInfo(b).Length + 1;
        Equal(MmcInstanceBackendStatus.NeedsRecovery, fixture.Online.CommitAsync(new(preview, preview.PreviewSha256)).GetAwaiter().GetResult().Status); Equal(before, fixture.Primary().Sha256);
    }
    private static void CandidatesAndDrain() {
        using Fixture fixture = new(); string a = fixture.Pack("A", "runtimeA", "A"); fixture.Import(a); fixture.Publish("VersionA", a, secondary: true);
        var m = Measurement(File.ReadAllBytes(a)); JObject other = (JObject)fixture.Server.Json["/v2/version/VersionA"]!.DeepClone(); other["id"] = "OtherVersion"; other["project_id"] = "ProjectB";
        fixture.Server.Json["/v2/project/ProjectB"] = new JObject { ["id"] = "ProjectB", ["project_type"] = "modpack" };
        fixture.Server.Json["/v2/version/OtherVersion"] = other;
        fixture.Server.Json["/v2/version_file/" + m.Sha512] = new JArray(fixture.Server.Json["/v2/version/VersionA"]!.DeepClone(), other.DeepClone());
        var candidates = fixture.Catalog.FindModrinthCandidatesAsync(a).GetAwaiter().GetResult(); Equal(2, candidates.Length); Equal(2, candidates.Select(value => value.CanonicalProjectKey).Distinct().Count()); Equal(true, candidates.All(value => value.File.FileName == "VersionA.zip"));
        fixture.Attach(a, fixture.Release("VersionA")); string b = fixture.Pack("B", "runtimeB", "B"); fixture.Publish("VersionB", b); var selected = fixture.Release("VersionB");
        fixture.HoldDownload = true; using CancellationTokenSource cancellation = new();
        var task = fixture.Online.PrepareAsync(new(fixture.Selection(), selected, new("windows", "x86_64", 8)), cancellation.Token);
        if (!fixture.DownloadStarted.Task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The controlled batch did not start.");
        cancellation.Cancel(); if (!fixture.CancellationSeen.Task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The controlled batch did not observe cancellation.");
        Equal(false, task.IsCompleted); Equal(1, fixture.ActiveDownloads); fixture.AllowDrain.TrySetResult(true);
        Equal(MmcInstanceBackendStatus.Cancelled, task.GetAwaiter().GetResult().Status); Equal(0, fixture.ActiveDownloads);
    }
    private static void EnvironmentSelection() {
        using Fixture fixture = new(); string a = fixture.Pack("A", "runtimeA", "A"); fixture.Import(a); fixture.Publish("VersionA", a); fixture.Attach(a, fixture.Release("VersionA"));
        string ordinary = Path.Combine(fixture.Root, "wrong.mrpack");
        using (var zip = ZipFile.Open(ordinary, ZipArchiveMode.Create)) { using var output = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()); output.Write("{}"); }
        fixture.Publish("Ordinary", ordinary); int calls = 0;
        var refused = fixture.Online.PrepareAsync(fixture.Selection(), fixture.Release("Ordinary"), (_, _) => {
            Interlocked.Increment(ref calls); return Task.FromResult(new MmcRuntimeEnvironment("windows", "x86_64", 8));
        }).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.Rejected, refused.Status); Equal(0, calls);
        string b = fixture.Pack("B", "runtimeB", "B"); fixture.Publish("VersionB", b); string before = fixture.Primary().Sha256;
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationSeen = new(TaskCreationOptions.RunContinuationsAsynchronously), drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = fixture.Online.PrepareAsync(fixture.Selection(), fixture.Release("VersionB"), async (archive, token) => {
            using var read = await MmcArchiveSourceReader.ReadAsync(archive, cancellationToken: token);
            Equal(MmcDurableFiles.Observe(b), read.PackageSha256); Interlocked.Increment(ref calls); entered.TrySetResult(true);
            using var registration = token.Register(() => cancellationSeen.TrySetResult(true));
            await drained.Task; return new("windows", "x86_64", 8);
        }, cancellation.Token);
        if (!entered.Task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The verified MMC package did not reach the actual environment selector.");
        cancellation.Cancel(); if (!cancellationSeen.Task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The environment selector did not observe cancellation.");
        Equal(false, task.IsCompleted); Equal(before, fixture.Primary().Sha256); Equal(0, fixture.ActiveDownloads);
        drained.TrySetResult(true); Equal(MmcInstanceBackendStatus.Cancelled, task.GetAwaiter().GetResult().Status); Equal(1, calls); Equal(before, fixture.Primary().Sha256);
    }

    private static JObject ModrinthFile(byte[] bytes, string name, string url) => new() { ["filename"] = name, ["url"] = url, ["size"] = bytes.LongLength,
        ["hashes"] = new JObject { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) } };
    private static MmcPlatformPackageMeasurement Measurement(byte[] bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.LongLength);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Require(MmcInstanceBackendStatus status, string? failure) { if (status != MmcInstanceBackendStatus.Succeeded) throw new Exception(status + ": " + failure); }
    private static void Throws(Action action) { try { action(); } catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException) { return; } throw new Exception("Invalid platform evidence was accepted."); }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-platform-" + Guid.NewGuid().ToString("N"));
        internal string VersionRoot => Path.Combine(Root, "game", "versions", "live");
        internal Server Server { get; } = new();
        private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });
        internal MmcPlatformOriginCatalog Catalog { get; }
        internal MmcInstancePlatformOriginBackend Origin { get; }
        internal MmcInstancePackageUpdateBackend<MmcArtifactDownloadSpec> Updates { get; }
        internal MmcInstancePlatformOnlineBackend<MmcArtifactDownloadSpec> Online { get; }
        internal bool HoldDownload;
        internal int ActiveDownloads;
        internal TaskCompletionSource<bool> DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal Fixture() {
            Directory.CreateDirectory(Root);
            Catalog = MmcPlatformOriginCatalog.ForLoopbackFixtures(async (uri, token) => {
                using var response = await http.GetAsync(uri, token); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(token);
            }, MmcPlatformApiPolicy.ForLoopbackFixtures(new(Server.BaseUri, "v2/"), new(Server.BaseUri, "v1/")));
            Origin = new(Catalog); Updates = new(Store(), Caches(), value => value, Downloads); Online = new(Catalog, Updates, Caches(), value => value, Downloads);
        }
        private MmcMetadataStore Store() => new(Path.Combine(Root, "cache", "metadata"), new Uri("https://metadata.invalid/"), http);
        private MmcArtifactCacheRoots Caches() => new(Path.Combine(Root, "cache", "libraries"), Path.Combine(Root, "cache", "assets"), Path.Combine(Root, "cache", "sources"));
        internal MmcInstanceBackend<MmcArtifactDownloadSpec> Backend() => new(Store(), Caches(), value => value, Downloads);
        internal MmcInstanceDefinitionChangeBackend<MmcArtifactDownloadSpec> Changes() => new(Store(), Caches(), value => value, Downloads);
        private async Task Downloads(ImmutableArray<MmcArtifactDownloadSpec> files, CancellationToken token) {
            Interlocked.Increment(ref ActiveDownloads);
            try {
                foreach (var file in files) {
                    if (HoldDownload && file.Artifact.ComponentUid == "pcllf.mmc.platform-origin") {
                        DownloadStarted.TrySetResult(true);
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) { CancellationSeen.TrySetResult(true); await AllowDrain.Task; throw; }
                    }
                    byte[] bytes = file.Artifact.ComponentUid == "pcllf.mmc.platform-origin" ? await http.GetByteArrayAsync(file.Urls[0], token) : index;
                    Directory.CreateDirectory(Path.GetDirectoryName(file.DownloadPath)!); await File.WriteAllBytesAsync(file.DownloadPath, bytes, token);
                }
            } finally { Interlocked.Decrement(ref ActiveDownloads); }
        }
        internal string Pack(string tag, string runtime, string author) {
            string path = Path.Combine(Root, tag + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            Add("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}"""); Add("instance.cfg", "name=Platform fixture\n");
            Add("patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Main","mainJar":{"name":"fixture:runtime:1","MMC-hint":"local","MMC-filename":"runtime.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/assets.json","sha1":"{{Convert.ToHexStringLower(SHA1.HashData(index))}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
            Add("libraries/runtime.jar", runtime); Add("minecraft/config/author.cfg", author); return path;
            void Add(string name, string value) { using var output = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); output.Write(value); }
        }
        internal void Import(string zip) { var result = new MmcInstanceBackend<MmcArtifactDownloadSpec>(Store(), Caches(), value => value, Downloads).ImportAsync(new(zip, VersionRoot, "live", new("windows", "x86_64", 8))).GetAwaiter().GetResult(); Require(result.Status, result.Failure); }
        internal MmcStoredPrimaryDefinition Primary() => MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(VersionRoot));
        internal MmcPlatformInstanceSelection Selection() { var p = Primary(); return new(VersionRoot, p.Definition.InstanceId, p.Definition.Generation, p.Sha256); }
        internal MmcPlatformOriginResult Attach(string zip, MmcPlatformReleaseDescriptor release) { var prepared = Origin.PreviewAsync(new(Selection(), zip, release)).GetAwaiter().GetResult(); Require(prepared.Status, prepared.Failure); var result = Origin.CommitAsync(new(prepared.Preview!, prepared.Preview!.CandidateSha256)).GetAwaiter().GetResult(); Require(result.Status, result.Failure); return result; }
        internal MmcPlatformReleaseDescriptor Release(string version) => Catalog.GetReleaseAsync(ResourcePlatforms.Modrinth, "ProjectA", version).GetAwaiter().GetResult().Single(value => value.File.FileName == version + ".zip");
        internal void Publish(string version, string path, bool secondary = false) {
            Server.Json["/v2/project/ProjectA"] = new JObject { ["id"] = "ProjectA", ["project_type"] = "modpack" };
            byte[] bytes = File.ReadAllBytes(path); Server.Bytes["/downloads/" + version + ".zip"] = bytes;
            var files = new JArray(); if (secondary) files.Add(ModrinthFile(Encoding.UTF8.GetBytes("unselected primary"), "first.zip", new Uri(Server.BaseUri, "downloads/first.zip").AbsoluteUri));
            files.Add(ModrinthFile(bytes, version + ".zip", new Uri(Server.BaseUri, "downloads/" + version + ".zip").AbsoluteUri));
            Server.Json["/v2/version/" + version] = new JObject { ["id"] = version, ["project_id"] = "ProjectA", ["files"] = files };
            Server.Json["/v2/project/ProjectA/version"] = new JArray(Server.Json.Where(pair => pair.Key.StartsWith("/v2/version/", StringComparison.Ordinal)).Select(pair => pair.Value.DeepClone()));
        }
        internal void PublishCurseForge(string path) {
            byte[] bytes = File.ReadAllBytes(path); Server.Bytes["/downloads/cf.zip"] = bytes;
            Server.Json["/v1/mods/123"] = new JObject { ["data"] = new JObject { ["id"] = 123, ["gameId"] = 432, ["classId"] = 4471, ["isAvailable"] = true } };
            Server.Json["/v1/mods/123/files/456"] = new JObject { ["data"] = new JObject { ["id"] = 456, ["modId"] = 123, ["gameId"] = 432, ["isAvailable"] = true,
                ["fileName"] = "cf.zip", ["fileLength"] = bytes.Length, ["downloadUrl"] = new Uri(Server.BaseUri, "downloads/cf.zip").AbsoluteUri,
                ["hashes"] = new JArray(new JObject { ["algo"] = 1, ["value"] = Measurement(bytes).Sha1 }) } };
        }
        internal MmcInstancePackageUpdatePreview Ready(MmcInstancePackageUpdatePreviewResult result) {
            Require(result.Status, result.Failure); var preview = result.Preview ?? throw new Exception("Missing package preview.");
            var choices = new MmcInstancePackageUpdateChoices([], preview.Files.Where(value => value.CanChoose).Select(value => new MmcPackageFileDecision(value.ChangeId, PackUpdateChoice.UseAuthor)).ToImmutableArray());
            result = Updates.SelectAsync(preview, preview.PreviewSha256, choices).GetAwaiter().GetResult(); Require(result.Status, result.Failure);
            if (result.Preview!.RequiresChoices || !result.Preview.Errors.IsEmpty) throw new Exception(string.Join("; ", result.Preview.Errors)); return result.Preview;
        }
        internal string Read(string relative) => File.ReadAllText(Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        internal void Put(string relative, string content) { string path = Path.Combine(VersionRoot, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        public void Dispose() { AllowDrain.TrySetResult(true); http.Dispose(); Server.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Server : IDisposable {
        internal Uri BaseUri { get; }
        internal ConcurrentDictionary<string, JToken> Json { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private TcpClient? active;
        internal Server() {
            // Keep the socket bound: HTTP.sys URL ACLs and a close/rebind race
            // must not make an owned loopback fixture depend on administrator state.
            listener.Start(); BaseUri = new("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"); loop = Task.Run(Loop);
        }
        private async Task Loop() {
            try {
                while (!stop.IsCancellationRequested) {
                    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token); active = client;
                    try {
                        await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        string first = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Missing fixture HTTP request.");
                        if (first.Length > 8192 || !first.StartsWith("GET /", StringComparison.Ordinal)) throw new IOException("Invalid fixture HTTP request.");
                        string path = new Uri(BaseUri, first.Split(' ')[1]).AbsolutePath; int count = 0;
                        while (true) { string? line = await reader.ReadLineAsync(stop.Token); if (string.IsNullOrEmpty(line)) break; if (++count > 64 || line.Length > 8192) throw new IOException("Oversized fixture HTTP headers."); }
                        byte[]? bytes; string type = "application/zip"; bool found;
                        if (Json.TryGetValue(path, out var json)) { bytes = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)); type = "application/json"; found = true; }
                        else found = Bytes.TryGetValue(path, out bytes);
                        bytes ??= [];
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, stop.Token); await stream.WriteAsync(bytes, stop.Token); await stream.FlushAsync(stop.Token);
                    } catch (IOException) { }
                    finally { active = null; }
                }
            } catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); active?.Dispose(); loop.GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
