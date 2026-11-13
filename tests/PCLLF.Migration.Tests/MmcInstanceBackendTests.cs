using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

internal static partial class MmcInstanceBackendTests {
    internal static IEnumerable<(string Name, Action Run)> PublicationCases() {
        yield return ("MMC import publication waits for a transient reader and retains its exact private identity", () => PublicationReader("release"));
        yield return ("MMC import publication retry preserves a newly claimed destination", () => PublicationReader("claimed"));
        yield return ("MMC import publication retry refuses changed primary bytes", () => PublicationReader("changed"));
        yield return ("MMC import publication leaves a persistently locked private root recoverable", () => PublicationReader("held"));
    }
    private static void PublicationReader(string mode) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This test requires actual Windows directory sharing behavior.");
        using Fixture fixture = new(); FileStream? reader = null; string? privateRoot = null; int waits = 0;
        try {
            var result = fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)),
                progress: new BackendProgress(value => {
                    if (value.Phase == "private-payload-verify" && value.CompletedBytes == value.TotalBytes && privateRoot is null) {
                        privateRoot = Directory.EnumerateDirectories(Path.GetDirectoryName(fixture.VersionRoot)!, ".pcllf-mmc-import-*").Single();
                        reader = new FileStream(Path.Combine(privateRoot, "minecraft", "config", "example.cfg"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    if (value.Phase == "publication-sharing-wait" && waits++ == 0) {
                        if (mode != "held") { reader!.Dispose(); reader = null; }
                        if (mode == "claimed") { Directory.CreateDirectory(fixture.VersionRoot); File.WriteAllText(Path.Combine(fixture.VersionRoot, "foreign.txt"), "keep foreign destination"); }
                        if (mode == "changed") File.AppendAllText(Path.Combine(privateRoot!, "PCL", "InstanceDefinition.json"), " ");
                    }
                })).GetAwaiter().GetResult();
            if (mode == "release") {
                if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception("Publication failed with a temporary reader: " + result.Failure);
                Equal(true, waits >= 1); Equal(false, Directory.Exists(privateRoot)); Equal("player config", File.ReadAllText(Path.Combine(fixture.VersionRoot, "minecraft", "config", "example.cfg")));
                Equal(result.Definition!.InstanceId, MmcSavedSourceStore.ReadPrimary(result.Paths!).Definition.InstanceId);
            } else {
                Equal(MmcInstanceBackendStatus.NeedsRecovery, result.Status); Equal(true, waits >= 1); Equal(true, Directory.Exists(privateRoot));
                Equal(true, File.Exists(Path.Combine(privateRoot!, "PCL", "MmcImportPending.json")));
                if (mode == "claimed") Equal("keep foreign destination", File.ReadAllText(Path.Combine(fixture.VersionRoot, "foreign.txt")));
                else { Equal(false, Directory.Exists(fixture.VersionRoot)); if (mode == "changed") Equal(true, File.ReadAllText(Path.Combine(privateRoot!, "PCL", "InstanceDefinition.json")).EndsWith(' ')); }
            }
        } finally { reader?.Dispose(); }
    }
    internal static IEnumerable<(string Name, Action Run)> VariantCases() {
        yield return ("MMC explicitly adopts a declared Java variant offline and retains adopted bytes and player data", AdoptJavaVariant);
        yield return ("MMC variant adoption rejects stale authority, undeclared Java and predecision cancellation", VariantRefusals);
        yield return ("MMC repeated variant adoption retains the selected digest across preparation boundaries", SameVariantDigest);
        yield return ("MMC repeated variant adoption requires explicit repair for incomplete projections", SameVariantIncomplete);
        yield return ("MMC launch preparation and repair reuse adopted asset originals after every global cache is removed", AssetInputRecovery);
    }
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC import preparation measures the full archive and keeps opaque docs outside live settings", SourcePreparation);
        yield return ("MMC backend imports real files and repairs offline after original ZIP deletion without touching player data", ImportRepair);
        yield return ("MMC backend rejects damaged authority and retains interrupted import ownership", FailureIsolation);
        yield return ("MMC launch preparation keeps complete generations and repairs only missing managed inputs offline", LaunchPreparation);
        yield return ("MMC launch preparation rejects stale identity, new variants and damaged support without adopting bytes", LaunchRefusals);
        yield return ("MMC import reports console and PermGen requirements as inert differences instead of substituting", PreferenceDifferences);
        yield return ("MMC private payload placement binds measured files and preserves canceled or conflicting private roots", PrivatePayload);
        yield return ("MMC initial import publishes thousands of tiny files without journal body copies", BulkPayloadImport);
        yield return ("MMC initial private payload cancellation and post-decision corruption never publish a final root", BulkPayloadFailure);
        yield return ("MMC post-decision payload verification completes publication despite cancellation", BulkPayloadPostDecisionCancel);
        foreach (var test in PrivatePayloadPathCases()) yield return test;
    }
    private static void SourcePreparation() => SourcePreparationAsync().GetAwaiter().GetResult();
    private static async Task SourcePreparationAsync() {
        using Fixture fixture = new();
        using var source = await MmcArchiveSourceReader.ReadAsync(fixture.Archive);
        using var reservation = MmcOwnedImportReservation.Create(fixture.VersionRoot, Guid.NewGuid(), source.PackageSha256);
        var paths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, source.Package.GameRootLeaf);
        using var prepared = await MmcPackageFileStager.PrepareInitialAsync(source, paths);
        Equal(source.ArchiveEntries.Count(entry => !entry.IsDirectory), prepared.Files.Length);
        var baseline = Baseline(source, prepared);
        byte[] bytes = MmcPackageFileBaselineCodec.Serialize(baseline);
        var resolved = MmcComponentResolver.Resolve(source.Package, []); Equal(true, resolved.IsComplete);
        using var metadata = MmcSourceBundleWriter.Prepare(source, new(resolved, []), paths);
        var reference = metadata.StageAuthorBaseline(bytes, prepared.Files.Length); metadata.StageOpaqueOriginals(prepared);
        var proof = MmcPackageFileBaselineCodec.Read(new(bytes), reference, source.PackageSha256, source.Package.RelativeRoot);
        Equal(false, proof.HasErrors); Equal(true, proof.Baseline is not null);
        Equal(true, prepared.InitialGameMutations.Any(item => item.RelativePath == "minecraft/libraries/fixture.jar"));
        Equal(false, prepared.InitialGameMutations.Any(item => item.RelativePath == "PCL/config.json" || item.RelativePath == "README.txt"));
        Equal(true, metadata.Mutations.Any(item => item.RelativePath.Contains("/package-files/", StringComparison.Ordinal)));
        Equal(false, Directory.Exists(fixture.VersionRoot));
        Directory.CreateDirectory(fixture.VersionRoot); File.WriteAllText(Path.Combine(fixture.VersionRoot, "player.txt"), "keep");
        bool refused = false; try { MmcOwnedImportReservation.Create(fixture.VersionRoot, Guid.NewGuid(), source.PackageSha256); } catch (IOException) { refused = true; }
        Equal(true, refused); Equal("keep", File.ReadAllText(Path.Combine(fixture.VersionRoot, "player.txt")));
    }
    internal static MmcPackageFileBaseline Baseline(MmcArchiveSource source, MmcPreparedPackageFiles prepared) => new(1, source.PackageSha256, source.Package.RelativeRoot,
        source.ArchiveEntries.Select(entry => new MmcPackageArchiveDescriptor(entry.Path, entry.IsDirectory, entry.SizeBytes, entry.Crc32)).ToImmutableArray(),
        prepared.Files.Select(file => new MmcPackageFileRecord(file.ArchiveRelativePath!, Enum.Parse<PCLLF.MMCC.Core.MmcPackageFileRole>(file.Role.ToString()), file.TargetRelativePath,
            file.Sha256, file.Sha1, file.SizeBytes, file.Crc32)).ToImmutableArray());
    private static void ImportRepair() => ImportRepairAsync().GetAwaiter().GetResult();
    private static async Task ImportRepairAsync() {
        using Fixture fixture = new(); var backend = fixture.Backend();
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        Equal(MmcInstanceBackendStatus.Succeeded, imported.Status);
        var paths = imported.Paths!;
        Equal("launcher library", File.ReadAllText(Path.Combine(paths.LocalLibraryRoot, "fixture.jar")));
        Equal("different game library", File.ReadAllText(Path.Combine(paths.GameRoot, "libraries", "fixture.jar")));
        Equal(1, imported.Definition!.PackageFileLocks.Length); Equal("libraries/unreferenced.jar", imported.Definition.PackageFileLocks[0].TargetRelativePath); Equal(1, fixture.Downloads);
        Equal(false, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPending.json")));
        JObject settings = JObject.Parse(File.ReadAllText(Path.Combine(paths.InstanceRoot, "PCL", "config.json")));
        Equal(false, settings.ContainsKey("foreign-player-pref"));
        using (var provider = new InstanceConfigProvider(paths.InstanceRoot)) { provider.Set("unknown-personal-pref", "keep", false); provider.Save(); }
        string playerConfig = Path.Combine(paths.GameRoot, "config", "example.cfg"); File.WriteAllText(playerConfig, "edited player config");
        string save = Path.Combine(paths.GameRoot, "saves", "world", "level.dat"); Directory.CreateDirectory(Path.GetDirectoryName(save)!); File.WriteAllText(save, "keep world");
        File.Delete(fixture.Archive); Directory.Delete(fixture.CacheRoot, true);
        foreach (var locked in imported.Definition.ArtifactLocks) File.Delete(paths.ResolveRelative(MmcPathRole.Instance, locked.TargetRelativePath));
        foreach (var locked in imported.Definition.PackageFileLocks) File.Delete(paths.ResolveRelative(MmcPathRole.Instance, locked.TargetRelativePath));
        File.Delete(Path.Combine(paths.InstanceRoot, "fixture.json"));
        var repaired = await fixture.Backend(offline: true).RepairAsync(new(paths.InstanceRoot, new("windows", "x86_64", 8), imported.Definition.InstanceId));
        if (repaired.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(repaired.Failure);
        Equal(MmcInstanceBackendStatus.Succeeded, repaired.Status); Equal(2L, repaired.Definition!.Generation); Equal(1, fixture.Downloads);
        Equal("launcher library", File.ReadAllText(Path.Combine(paths.LocalLibraryRoot, "fixture.jar")));
        Equal("unreferenced original", File.ReadAllText(Path.Combine(paths.LocalLibraryRoot, "unreferenced.jar")));
        Equal("edited player config", File.ReadAllText(playerConfig)); Equal("keep world", File.ReadAllText(save));
        Equal("different game library", File.ReadAllText(Path.Combine(paths.GameRoot, "libraries", "fixture.jar")));
        Equal("keep", JObject.Parse(File.ReadAllText(Path.Combine(paths.InstanceRoot, "PCL", "config.json"))).Value<string>("unknown-personal-pref"));
    }
    private static void FailureIsolation() => FailureIsolationAsync().GetAwaiter().GetResult();
    private static void BulkPayloadImport() => BulkPayloadImportAsync().GetAwaiter().GetResult();
    private static async Task BulkPayloadImportAsync() {
        const int tinyFiles = 2000;
        using Fixture fixture = new(tinyFiles);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int moved = 0, verified = 0, announcedFiles = 0; long movedBytes = 0, verifiedBytes = 0;
        long? payloadStarted = null, payloadFinished = null;
        var result = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)),
            progress: new BackendProgress(value => {
                if (value.Phase == "installing-package") payloadStarted = elapsed.ElapsedMilliseconds;
                if (value.Phase == "publishing-instance") payloadFinished = elapsed.ElapsedMilliseconds;
                // Placement is disclosed before the first move (CompletedFiles = 0), so the number of
                // callbacks is not the number of transferred files. Count each announced CompletedFiles
                // level once; that remains correct whether progress disclosure is added or removed
                // around the real per-file completions, while a skipped or duplicated announcement
                // still cannot pass the inventory and byte assertions below.
                if (value.Phase == "private-payload-install") {
                    movedBytes = value.CompletedBytes;
                    int completed = value.CompletedFiles ?? 0;
                    if (completed > announcedFiles) { moved++; announcedFiles = completed; }
                }
                if (value.Phase == "private-payload-verify") { verified++; verifiedBytes = value.CompletedBytes; }
            }));
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure);
        elapsed.Stop();
        var paths = result.Paths!;
        for (int index = 0; index < tinyFiles; index++) Equal("bulk body " + index, File.ReadAllText(Path.Combine(paths.GameRoot, "config", "bulk", index.ToString("D5") + ".cfg")));
        Equal("instance extra payload", File.ReadAllText(Path.Combine(paths.InstanceRoot, "icon.txt")));
        string history = Path.Combine(paths.InstanceRoot, "PCL", "MmcTransactionHistory");
        string record = Directory.EnumerateFiles(history, "prepared.record", SearchOption.AllDirectories).Single();
        var journal = MmcDurableFiles.ReadRecord<MmcPreparedJournal>(record).Value;
        Equal(true, journal.Mutations.Length < 64);
        Equal(false, journal.Mutations.Any(value => value.RelativePath.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase) || value.RelativePath == "icon.txt"));
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPayload.json")));
        Equal(tinyFiles + 3, ((JArray)manifest["Files"]!).Count); Equal(tinyFiles + 3, moved); Equal(moved, verified); Equal(movedBytes, verifiedBytes);
        Equal(manifest.Value<long>("TotalBytes"), movedBytes);
        var payloadHashes = ((JArray)manifest["Files"]!).OfType<JObject>().Select(value => value.Value<string>("Sha256")!).ToHashSet(StringComparer.Ordinal);
        string[] bodyCopies = Directory.EnumerateFiles(Path.GetDirectoryName(record)!, "new-*.bin").ToArray();
        Equal(false, bodyCopies.Any(file => payloadHashes.Contains(MmcDurableFiles.Observe(file)!)));
        Equal(false, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPending.json")));
        Equal(false, Directory.Exists(Path.Combine(paths.RuntimeRoot, ".staging")));
        Console.WriteLine($"INFO private import {tinyFiles + 3} payload files/{movedBytes} bytes: {elapsed.ElapsedMilliseconds} ms; private preflight+placement {payloadFinished - payloadStarted} ms; journal {journal.Mutations.Length} mutations/{bodyCopies.Length} new objects; zero bulk body copies.");
    }
    private static void BulkPayloadFailure() => BulkPayloadFailureAsync().GetAwaiter().GetResult();
    private static void BulkPayloadPostDecisionCancel() => BulkPayloadPostDecisionCancelAsync().GetAwaiter().GetResult();
    private static async Task BulkPayloadPostDecisionCancelAsync() {
        using Fixture fixture = new(8); using CancellationTokenSource cancel = new();
        bool decided = false, canceledDuringVerification = false; int verified = 0;
        var result = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)) {
            TransactionCheckpoint = (phase, _) => { if (phase == MmcTransactionCheckpoint.CommitDecided) decided = true; }
        }, cancel.Token, new BackendProgress(value => {
            if (value.Phase != "private-payload-verify") return;
            Equal(true, decided); verified++;
            if (!canceledDuringVerification) { canceledDuringVerification = true; cancel.Cancel(); }
        }));
        if (result.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(result.Failure);
        Equal(true, canceledDuringVerification); Equal(true, cancel.IsCancellationRequested); Equal(11, verified);
        Equal(MmcInstanceBackendStatus.Succeeded, result.Status); Equal(fixture.VersionRoot, result.WorkingRoot); Equal(true, Directory.Exists(fixture.VersionRoot));
        Equal(false, File.Exists(Path.Combine(fixture.VersionRoot, "PCL", "MmcImportPending.json")));
        Equal(1L, MmcSavedSourceStore.ReadPrimary(result.Paths!).Definition.Generation);
        for (int index = 0; index < 8; index++) Equal("bulk body " + index, File.ReadAllText(Path.Combine(result.Paths!.GameRoot, "config", "bulk", index.ToString("D5") + ".cfg")));
    }
    private static async Task BulkPayloadFailureAsync() {
        foreach (string mode in new[] { "cancel", "corrupt" }) {
            using Fixture fixture = new(32); using CancellationTokenSource cancel = new();
            var clock = System.Diagnostics.Stopwatch.StartNew(); var phases = new Dictionary<string, (long First, long Last)>();
            void Observe(string phase) { long now = clock.ElapsedMilliseconds; phases[phase] = phases.TryGetValue(phase, out var previous) ? (previous.First, now) : (now, now); }
            string? privateRoot = null;
            string sibling = Path.Combine(Path.GetDirectoryName(fixture.VersionRoot)!, "untouched", "player.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(sibling)!); File.WriteAllText(sibling, "player bytes");
            var result = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)) {
                PrivatePayloadCheckpoint = (phase, index) => {
                    Observe("payload-" + phase);
                    if (phase != MmcInitialPayloadCheckpoint.Moved || index != 3) return;
                    privateRoot = Directory.EnumerateDirectories(Path.GetDirectoryName(fixture.VersionRoot)!, ".pcllf-mmc-import-*").Single();
                    if (mode == "cancel") cancel.Cancel();
                },
                TransactionCheckpoint = (phase, _) => { Observe("tx-" + phase); if (mode == "corrupt" && phase == MmcTransactionCheckpoint.CommitDecided) File.WriteAllText(Path.Combine(privateRoot!, "minecraft", "config", "example.cfg"), "post-decision external bytes"); }
            }, cancel.Token, new BackendProgress(value => Observe(value.Phase)));
            if (mode == "corrupt") Console.WriteLine("INFO 32-file fault smoke phase callback first..last elapsed milliseconds (not complete phase durations): " +
                string.Join("; ", new[] { "package-extract", "payload-Prepared", "payload-Moved", "publishing-instance", "tx-Prepared", "tx-CommitDecided", "tx-Finalized", "private-payload-verify" }
                    .Where(phases.ContainsKey).Select(phase => phase + "=" + phases[phase].First + ".." + phases[phase].Last)) + "; total=" + clock.ElapsedMilliseconds);
            Equal(mode == "cancel" ? MmcInstanceBackendStatus.Cancelled : MmcInstanceBackendStatus.NeedsRecovery, result.Status);
            Equal(false, Directory.Exists(fixture.VersionRoot)); Equal("player bytes", File.ReadAllText(sibling)); Equal(privateRoot, result.WorkingRoot);
            Equal(true, File.Exists(Path.Combine(privateRoot!, "PCL", "MmcImportPending.json")));
            Equal(true, File.Exists(Path.Combine(privateRoot!, "PCL", "MmcImportPayload.json")));
            if (mode == "cancel") {
                var files = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(privateRoot!, "PCL", "MmcImportPayload.json")))["Files"]!;
                Equal(4, files.OfType<JObject>().Count(file => File.Exists(Path.Combine(privateRoot!, file.Value<string>("TargetRelativePath")!.Replace('/', Path.DirectorySeparatorChar)))));
                Equal(true, Directory.EnumerateFiles(Path.Combine(privateRoot!, "PCL", "MmcRuntime", ".staging"), "owner.txt", SearchOption.AllDirectories).Any());
                Equal(false, File.Exists(Path.Combine(privateRoot!, "PCL", "InstanceDefinition.json")));
            } else {
                Equal("post-decision external bytes", File.ReadAllText(Path.Combine(privateRoot!, "minecraft", "config", "example.cfg")));
                Equal(1L, MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(privateRoot!)).Definition.Generation);
            }
        }
    }
    private sealed class BackendProgress(Action<MmcInstanceBackendProgress> report) : IProgress<MmcInstanceBackendProgress> { public void Report(MmcInstanceBackendProgress value) => report(value); }
    private static void PrivatePayload() => PrivatePayloadAsync().GetAwaiter().GetResult();
    private static async Task PrivatePayloadAsync() {
        foreach (string mode in new[] { "complete", "cancel", "conflict" }) {
            using Fixture fixture = new();
            using var source = await MmcArchiveSourceReader.ReadAsync(fixture.Archive);
            using var reservation = MmcOwnedImportReservation.Create(fixture.VersionRoot, Guid.NewGuid(), source.PackageSha256);
            var paths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, source.Package.GameRootLeaf);
            using var prepared = await MmcPackageFileStager.PrepareInitialAsync(source, paths);
            byte[] bytes = MmcPackageFileBaselineCodec.Serialize(Baseline(source, prepared));
            var resolved = MmcComponentResolver.Resolve(source.Package, []);
            using var writer = MmcSourceBundleWriter.Prepare(source, new(resolved, []), paths);
            var reference = writer.StageAuthorBaseline(bytes, prepared.Files.Length);
            var proof = MmcPackageFileBaselineCodec.Read(new(bytes), reference, source.PackageSha256, source.Package.RelativeRoot);
            if (proof.HasErrors || proof.Baseline is null) throw new Exception("Private payload fixture baseline is invalid.");
            using CancellationTokenSource cancel = new();
            string occupied = paths.ResolveRelative(MmcPathRole.Instance, prepared.InitialGameMutations[0].RelativePath);
            if (mode == "conflict") { Directory.CreateDirectory(Path.GetDirectoryName(occupied)!); File.WriteAllText(occupied, "external private bytes"); }
            bool canceled = false, conflicted = false;
            try { reservation.AdoptPreparedGameFiles(prepared, proof.Baseline, cancel.Token, checkpoint: (phase, index) => { if (mode == "cancel" && phase == MmcInitialPayloadCheckpoint.Moved && index == 0) cancel.Cancel(); }); }
            catch (OperationCanceledException) { canceled = true; }
            catch (IOException) when (mode == "conflict") { conflicted = true; }
            Equal(false, Directory.Exists(fixture.VersionRoot)); Equal(true, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPending.json")));
            if (mode == "complete") {
                foreach (var mutation in prepared.InitialGameMutations) {
                    Equal(mutation.ExpectedNewSha256, MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, mutation.RelativePath)));
                    Equal(false, File.Exists(paths.ResolveRelative(MmcPathRole.Instance, mutation.NewSourceRelativePath!)));
                }
                Equal(true, prepared.Files.Where(file => file.Role == PCLCS.MmcPackageFileRole.LocalLibrary).All(file => File.Exists(file.StagedPath)));
                Equal(true, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPayload.json")));
            } else if (mode == "cancel") {
                Equal(true, canceled); prepared.Dispose();
                Equal(true, File.Exists(Path.Combine(prepared.StageRoot, "owner.txt")));
                Equal(true, prepared.InitialGameMutations.Any(mutation => File.Exists(paths.ResolveRelative(MmcPathRole.Instance, mutation.RelativePath))));
                Equal(true, prepared.InitialGameMutations.Any(mutation => File.Exists(paths.ResolveRelative(MmcPathRole.Instance, mutation.NewSourceRelativePath!))));
            } else { Equal(true, conflicted); Equal("external private bytes", File.ReadAllText(occupied)); }
        }
    }
    private static void LaunchPreparation() => LaunchPreparationAsync().GetAwaiter().GetResult();
    private static async Task LaunchPreparationAsync() {
        using Fixture fixture = new();
        var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        Guid id = imported.Definition!.InstanceId;
        File.Delete(fixture.Archive); Directory.Delete(fixture.CacheRoot, true);
        var backend = fixture.Backend(offline: true);
        var request = new MmcInstancePrepareLaunchRequest(fixture.VersionRoot, new("windows", "x86_64", 8), id, 1);
        string primary = Path.Combine(fixture.VersionRoot, "PCL", "InstanceDefinition.json"); byte[] before = File.ReadAllBytes(primary);
        var ready = await backend.PrepareLaunchAsync(request);
        if (ready.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(ready.Failure);
        Equal(false, ready.Repaired); Equal(1L, ready.Snapshot!.Definition.Generation); Equal(true, before.SequenceEqual(File.ReadAllBytes(primary)));
        Equal(imported.BoundPlan!.BoundFingerprint, ready.Snapshot.BoundPlan.BoundFingerprint);
        Equal(true, ready.Snapshot.Targets.All(value => File.Exists(value.ActualPath)));
        Equal(true, ready.Snapshot.PackageConfiguration.Values.ContainsKey("JvmArgs"));
        using (var lease = MmcAdmissionGate.Acquire(fixture.VersionRoot, id, TimeSpan.Zero))
            MmcInstanceBackend<MmcArtifactDownloadSpec>.ValidatePreparedLaunch(lease, ready.Snapshot);
        byte[] supportBytes = Encoding.UTF8.GetBytes("owned reviewed support"); string digest = Convert.ToHexStringLower(SHA256.HashData(supportBytes));
        var support = await MmcInstanceBackend<MmcArtifactDownloadSpec>.PrepareLaunchSupportAsync(ready.Snapshot, [new("wrapper.jar", supportBytes, digest)]);
        Equal(digest, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(support[0].ActualPath))));
        Equal(true, before.SequenceEqual(File.ReadAllBytes(primary)));
        File.Delete(Path.Combine(fixture.VersionRoot, "fixture.json"));
        foreach (var target in ready.Snapshot.Targets) File.Delete(target.ActualPath);
        var missing = await backend.PrepareLaunchAsync(request with { RepairIncomplete = false });
        Equal(MmcInstanceBackendStatus.NeedsRecovery, missing.Status); Equal(false, missing.Repaired);
        var repaired = await backend.PrepareLaunchAsync(request);
        if (repaired.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(repaired.Failure);
        Equal(true, repaired.Repaired); Equal(2L, repaired.Snapshot!.Definition.Generation); Equal(1, fixture.Downloads);
        var stable = await backend.PrepareLaunchAsync(request with { ExpectedGeneration = 2 });
        if (stable.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(stable.Failure);
        Equal(false, stable.Repaired); Equal(2L, stable.Snapshot!.Definition.Generation);
        using var staleLease = MmcAdmissionGate.Acquire(fixture.VersionRoot, id, TimeSpan.Zero);
        bool refused = false; try { MmcInstanceBackend<MmcArtifactDownloadSpec>.ValidatePreparedLaunch(staleLease, ready.Snapshot); } catch (IOException) { refused = true; }
        Equal(true, refused);
    }
    private static void LaunchRefusals() => LaunchRefusalsAsync().GetAwaiter().GetResult();
    private static async Task LaunchRefusalsAsync() {
        using Fixture fixture = new();
        var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        Guid id = imported.Definition!.InstanceId; var backend = fixture.Backend(offline: true);
        var request = new MmcInstancePrepareLaunchRequest(fixture.VersionRoot, new("windows", "x86_64", 8), id, 1);
        Equal(MmcInstanceBackendStatus.NeedsRecovery, (await backend.PrepareLaunchAsync(request with { ExpectedInstanceId = Guid.NewGuid() })).Status);
        var variant = await backend.PrepareLaunchAsync(request with { Environment = new("windows", "arm64", 8) });
        Equal(MmcInstanceBackendStatus.Rejected, variant.Status); Equal("MMC_INSTANCE_VARIANT_REQUIRED", variant.Diagnostics.Single().Code);
        var ready = await backend.PrepareLaunchAsync(request);
        if (ready.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(ready.Failure);
        byte[] bytes = Encoding.UTF8.GetBytes("reviewed bridge"); string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var inputs = new[] { new MmcInstanceLaunchSupportInput("bridge.jar", bytes, digest) };
        var support = await MmcInstanceBackend<MmcArtifactDownloadSpec>.PrepareLaunchSupportAsync(ready.Snapshot!, inputs);
        File.WriteAllText(support[0].ActualPath, "external bytes");
        bool refused = false; try { await MmcInstanceBackend<MmcArtifactDownloadSpec>.PrepareLaunchSupportAsync(ready.Snapshot!, inputs); } catch (IOException) { refused = true; }
        Equal(true, refused); Equal("external bytes", File.ReadAllText(support[0].ActualPath));
        string active = ready.Snapshot!.Targets.First().ActualPath; File.WriteAllText(active, "changed after preparation");
        using var lease = MmcAdmissionGate.Acquire(fixture.VersionRoot, id, TimeSpan.Zero);
        refused = false; try { MmcInstanceBackend<MmcArtifactDownloadSpec>.ValidatePreparedLaunch(lease, ready.Snapshot); } catch (IOException) { refused = true; }
        Equal(true, refused); Equal(1L, MmcSavedSourceStore.ReadPrimary(ready.Snapshot.Paths).Definition.Generation);
    }
    private static async Task FailureIsolationAsync() {
        using (Fixture fixture = new()) {
            var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
            if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
            string primary = Path.Combine(fixture.VersionRoot, "PCL", "InstanceDefinition.json"); JObject json = JObject.Parse(File.ReadAllText(primary));
            json["artifactLocks"]![0]!["adoptedSha256"] = new string('0', 64); File.WriteAllText(primary, json.ToString());
            var failed = await fixture.Backend(offline: true).RepairAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8)));
            Equal(MmcInstanceBackendStatus.NeedsRecovery, failed.Status); Equal("launcher library", File.ReadAllText(Path.Combine(fixture.VersionRoot, "libraries", "fixture.jar")));
        }
        using (Fixture fixture = new()) {
            string existing = Path.Combine(Path.GetDirectoryName(fixture.VersionRoot)!, "existing", "player.dat"); Directory.CreateDirectory(Path.GetDirectoryName(existing)!); File.WriteAllText(existing, "untouched");
            var failed = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)) {
                TransactionCheckpoint = (phase, _) => { if (phase == MmcTransactionCheckpoint.Prepared) throw new MmcTransactionInterruptionException("controlled interruption"); }
            });
            Equal(MmcInstanceBackendStatus.NeedsRecovery, failed.Status); Equal(false, Directory.Exists(fixture.VersionRoot)); Equal("untouched", File.ReadAllText(existing));
            Equal(true, File.Exists(Path.Combine(failed.WorkingRoot!, "PCL", "MmcImportPending.json")));
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            Equal(MmcInstanceBackendStatus.Cancelled, (await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)), cancellation.Token)).Status);
            Equal(false, Directory.Exists(fixture.VersionRoot));
        }
        using (Fixture fixture = new()) {
            using CancellationTokenSource cancellation = new();
            var cancelled = await fixture.Backend(afterDownload: cancellation.Cancel).ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)), cancellation.Token);
            Equal(MmcInstanceBackendStatus.Cancelled, cancelled.Status); Equal(false, Directory.Exists(fixture.VersionRoot));
            Equal(true, File.Exists(Path.Combine(cancelled.WorkingRoot!, "PCL", "MmcImportPending.json")));
        }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void AdoptJavaVariant() => AdoptJavaVariantAsync().GetAwaiter().GetResult();
    private static async Task AdoptJavaVariantAsync() {
        using Fixture fixture = new(multiJava: true);
        var backend = fixture.Backend();
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        var first = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
        string player = Path.Combine(imported.Paths!.GameRoot, "config", "example.cfg"); File.WriteAllText(player, "personal");
        File.Delete(fixture.Archive); Directory.Delete(fixture.CacheRoot, true);
        var offline = fixture.Backend(offline: true);
        var ordinaryRepair = await offline.RepairAsync(new(fixture.VersionRoot, new("windows", "x86_64", 21), first.Definition.InstanceId));
        Equal(MmcInstanceBackendStatus.Rejected, ordinaryRepair.Status);
        Equal(first.Sha256, MmcSavedSourceStore.ReadPrimary(imported.Paths).Sha256);
        var next = await offline.AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 21), first.Definition.InstanceId, 1, first.Sha256));
        if (next.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(next.Failure);
        Equal(2L, next.Definition!.Generation); Equal(21, next.BoundPlan!.CompiledProfile.Environment.JavaMajor);
        foreach (var original in first.Definition.ArtifactLocks) {
            var current = next.Definition.ArtifactLocks.Single(value => value.ArtifactKey == original.ArtifactKey);
            Equal(original.AdoptedSha256, current.AdoptedSha256); Equal(original.RecoveryObjectRelativePath, current.RecoveryObjectRelativePath);
        }
        Equal("personal", File.ReadAllText(player)); Equal(1, fixture.Downloads);
        var second = MmcSavedSourceStore.ReadPrimary(next.Paths!);
        var same = await offline.AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 21), second.Definition.InstanceId, 2, second.Sha256));
        if (same.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(same.Failure);
        Equal(2L, same.Definition!.Generation); Equal(second.Sha256, MmcSavedSourceStore.ReadPrimary(next.Paths!).Sha256);
        var back = await offline.AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8), second.Definition.InstanceId, 2, second.Sha256));
        if (back.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(back.Failure);
        Equal(3L, back.Definition!.Generation); Equal(8, back.BoundPlan!.CompiledProfile.Environment.JavaMajor);
        Equal("personal", File.ReadAllText(player));
    }
    private static void VariantRefusals() => VariantRefusalsAsync().GetAwaiter().GetResult();
    private static async Task VariantRefusalsAsync() {
        using Fixture fixture = new(multiJava: true);
        var backend = fixture.Backend();
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        var primary = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
        var request = new MmcInstanceAdoptVariantRequest(fixture.VersionRoot, new("windows", "x86_64", 21), primary.Definition.InstanceId, 1, primary.Sha256);
        foreach (var invalid in new[] { request with { ExpectedGeneration = 2 }, request with { ExpectedPrimarySha256 = new string('e', 64) }, request with { Environment = new("windows", "x86_64", 17) } }) {
            var result = await backend.AdoptVariantAsync(invalid); Equal(false, result.Status == MmcInstanceBackendStatus.Succeeded);
            Equal(primary.Sha256, MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256);
        }
        using CancellationTokenSource cancellation = new();
        var canceled = await backend.AdoptVariantAsync(request with {
            TransactionCheckpoint = (phase, _) => { if (phase == MmcTransactionCheckpoint.Prepared) cancellation.Cancel(); }
        }, cancellation.Token);
        Equal(MmcInstanceBackendStatus.Cancelled, canceled.Status);
        Equal(primary.Sha256, MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256);
    }

    private static void SameVariantDigest() => SameVariantDigestAsync().GetAwaiter().GetResult();
    private static async Task SameVariantDigestAsync() {
        using Fixture fixture = new(multiJava: true);
        var backend = fixture.Backend();
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        string primaryPath = Path.Combine(fixture.VersionRoot, "PCL", "InstanceDefinition.json");
        foreach (string mutationPhase in new[] { "loading-immutable-sources", "preparing-existing-launch" }) {
            var selected = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
            bool mutated = false;
            var result = await backend.AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8),
                selected.Definition.InstanceId, selected.Definition.Generation, selected.Sha256), progress: new BackendProgress(value => {
                    if (value.Phase == mutationPhase && !mutated) { mutated = true; File.AppendAllText(primaryPath, " "); }
                }));
            Equal(true, mutated); Equal(MmcInstanceBackendStatus.NeedsRecovery, result.Status);
            var after = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
            Equal(selected.Definition.Generation, after.Definition.Generation); Equal(false, selected.Sha256 == after.Sha256);
            Equal(true, File.ReadAllText(primaryPath).EndsWith(' '));
        }
    }

    private static void SameVariantIncomplete() => SameVariantIncompleteAsync().GetAwaiter().GetResult();
    private static async Task SameVariantIncompleteAsync() {
        using Fixture fixture = new(multiJava: true);
        var backend = fixture.Backend();
        var imported = await backend.ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        var selected = MmcSavedSourceStore.ReadPrimary(imported.Paths!);
        string projection = Path.Combine(fixture.VersionRoot, "fixture.json"); File.Delete(projection);
        int checkpoints = 0;
        var result = await backend.AdoptVariantAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8),
            selected.Definition.InstanceId, selected.Definition.Generation, selected.Sha256) {
                TransactionCheckpoint = (_, _) => checkpoints++
            });
        Equal(MmcInstanceBackendStatus.NeedsRecovery, result.Status); Equal(0, checkpoints);
        Equal(selected.Sha256, MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256); Equal(false, File.Exists(projection));
    }

    private static void AssetInputRecovery() => AssetInputRecoveryAsync().GetAwaiter().GetResult();
    private static async Task AssetInputRecoveryAsync() {
        using Fixture fixture = new(withAssetObject: true);
        var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
        if (imported.Status != MmcInstanceBackendStatus.Succeeded) throw new Exception(imported.Failure);
        Equal(2, fixture.Downloads);
        var asset = imported.Definition!.DerivedOutputs.Single(value => value.Kind == MmcDerivedOutputKind.AssetObject);
        Equal("owned original asset", File.ReadAllText(imported.Paths!.ResolveRelative(MmcPathRole.Instance, asset.RecoveryObjectRelativePath!)));
        File.Delete(fixture.Archive); Directory.Delete(fixture.CacheRoot, true);
        var offline = fixture.Backend(offline: true);
        var complete = await offline.PrepareLaunchAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8), imported.Definition.InstanceId, 1));
        if (complete.Snapshot is null) throw new Exception(complete.Failure);
        Equal(false, complete.Repaired); Equal(1L, complete.Snapshot.Definition.Generation);
        File.Delete(imported.Paths.ResolveRelative(MmcPathRole.Instance, asset.TargetRelativePath));
        var repaired = await offline.PrepareLaunchAsync(new(fixture.VersionRoot, new("windows", "x86_64", 8), imported.Definition.InstanceId, 1));
        if (repaired.Snapshot is null) throw new Exception(repaired.Failure);
        Equal(true, repaired.Repaired); Equal(2L, repaired.Snapshot.Definition.Generation); Equal(2, fixture.Downloads);
        Equal("owned original asset", File.ReadAllText(imported.Paths.ResolveRelative(MmcPathRole.Instance, asset.TargetRelativePath)));
    }

    private static void PreferenceDifferences() => PreferenceDifferencesAsync().GetAwaiter().GetResult();
    private static async Task PreferenceDifferencesAsync() {
        // A package that declares the two groups this host has no equivalent for still imports successfully:
        // the requirement is kept and reported, never converted into a capability the launcher does not have.
        using (Fixture fixture = new(extraCfg: "OverrideConsole=true\nShowConsole=true\nAutoCloseConsole=false\nShowConsoleOnError=true\nOverrideMemory=true\nMinMemAlloc=512\nMaxMemAlloc=4096\nPermGen=256")) {
            var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
            Equal(MmcInstanceBackendStatus.Succeeded, imported.Status);
            // Both differences are reported by the real import path, under the codes Core also uses.
            Equal(MmcDisplayDifference.ConsoleCode, imported.Diagnostics.Single(d => d.Code == MmcDisplayDifference.ConsoleCode).Code);
            Equal(MmcDisplayDifference.PermanentGenerationCode, imported.Diagnostics.Single(d => d.Code == MmcDisplayDifference.PermanentGenerationCode).Code);
            Equal(MmcDiagnosticSeverity.Warning, imported.Diagnostics.Single(d => d.Code == MmcDisplayDifference.PermanentGenerationCode).Severity);
            // The declared values survive verbatim: nothing was rewritten into another option.
            var values = imported.PackageConfiguration!.Values;
            Equal("256", values["PermGen"]); Equal("true", values["ShowConsole"]); Equal("false", values["AutoCloseConsole"]);
            // The memory mapping this host does have still works alongside the PermGen difference, so the
            // unresolved PermGen did not silently disable the resolved part of the same group.
            Equal("512", values["MinMemAlloc"]); Equal("4096", values["MaxMemAlloc"]);
            // No Metaspace or permanent-generation substitute is installed in any instance setting.
            string setup = Path.Combine(fixture.VersionRoot, "PCL", "Setup.ini");
            string ini = File.Exists(setup) ? File.ReadAllText(setup) : "";
            Equal(false, ini.Contains("Metaspace", StringComparison.OrdinalIgnoreCase));
            Equal(false, ini.Contains("PermSize", StringComparison.OrdinalIgnoreCase));
        }
        // A package declaring neither group reports neither difference, so the notices are not unconditional.
        using (Fixture fixture = new()) {
            var imported = await fixture.Backend().ImportAsync(new(fixture.Archive, fixture.VersionRoot, "fixture", new("windows", "x86_64", 8)));
            Equal(MmcInstanceBackendStatus.Succeeded, imported.Status);
            Equal(false, imported.Diagnostics.Any(d => d.Code is MmcDisplayDifference.ConsoleCode or MmcDisplayDifference.PermanentGenerationCode));
        }
    }
    private sealed class Fixture : IDisposable {
        public string Scope { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-backend-" + Guid.NewGuid().ToString("N"));
        public string Archive => Path.Combine(Scope, "fixture.zip");
        public string VersionRoot => Path.Combine(Scope, "minecraft", "versions", "fixture");
        public string CacheRoot => Path.Combine(Scope, "cache");
        public int Downloads;
        private readonly List<HttpClient> clients = [];
        private static readonly byte[] AssetObject = Encoding.UTF8.GetBytes("owned original asset");
        private readonly byte[] AssetIndex;
        public Fixture(int tinyFiles = 0, bool multiJava = false, bool withAssetObject = false, string extraCfg = "") {
            string assetSha1 = Convert.ToHexStringLower(SHA1.HashData(AssetObject));
            AssetIndex = Encoding.UTF8.GetBytes(withAssetObject ? "{\"objects\":{\"fixture/asset.txt\":{\"hash\":\"" + assetSha1 + "\",\"size\":" + AssetObject.Length + "}}}" : "{\"objects\":{}}");
            Directory.CreateDirectory(Scope);
            using var zip = ZipFile.Open(Archive, ZipArchiveMode.Create);
            Add(zip, "README.txt", "opaque outer documentation");
            zip.CreateEntry("pack/minecraft/");
            Add(zip, "pack/mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
            Add(zip, "pack/instance.cfg", "name=Fixture\nOverrideJavaArgs=true\nJvmArgs=-Dfixture=yes\n" + extraCfg);
            Add(zip, "pack/patches/net.minecraft.json", """{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/assets.json","sha1":"0000000000000000000000000000000000000000","size":1},"compatibleJavaMajors":[8]}"""
                .Replace(new string('0', 40), Convert.ToHexStringLower(SHA1.HashData(AssetIndex))).Replace("\"size\":1", "\"size\":" + AssetIndex.Length)
                .Replace("\"compatibleJavaMajors\":[8]", multiJava ? "\"compatibleJavaMajors\":[8,21]" : "\"compatibleJavaMajors\":[8]"));
            Add(zip, "pack/libraries/fixture.jar", "launcher library"); Add(zip, "pack/libraries/unreferenced.jar", "unreferenced original");
            Add(zip, "pack/minecraft/libraries/fixture.jar", "different game library"); Add(zip, "pack/minecraft/config/example.cfg", "player config");
            Add(zip, "pack/PCL/config.json", "{\"foreign-player-pref\":\"must-not-import\"}");
            for (int index = 0; index < tinyFiles; index++) Add(zip, "pack/minecraft/config/bulk/" + index.ToString("D5") + ".cfg", "bulk body " + index);
            if (tinyFiles != 0) Add(zip, "pack/icon.txt", "instance extra payload");
        }
        private static void Add(ZipArchive zip, string path, string content) { using var output = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); output.Write(content); }
        public MmcInstanceBackend<MmcArtifactDownloadSpec> Backend(bool offline = false, Action? afterDownload = null) {
            HttpClient client = new(new NoNetwork()); clients.Add(client);
            MmcMetadataStore metadata = new(Path.Combine(CacheRoot, "metadata"), new Uri("https://metadata.invalid/"), client);
            return new(metadata, new(Path.Combine(CacheRoot, "libraries"), Path.Combine(CacheRoot, "assets"), Path.Combine(CacheRoot, "sources")), value => value,
                async (batch, token) => {
                    if (offline) throw new IOException("Controlled offline fixture refused a download.");
                    foreach (var item in batch) {
                        string assetHash = Convert.ToHexStringLower(SHA1.HashData(AssetObject));
                        byte[] content = item.Urls.Contains("https://fixture.invalid/assets.json") ? AssetIndex :
                            item.Urls.Contains("https://resources.download.minecraft.net/" + assetHash[..2] + "/" + assetHash) ? AssetObject :
                            throw new Exception("Unexpected controlled download source.");
                        await File.WriteAllBytesAsync(item.DownloadPath, content, token); Downloads++;
                        afterDownload?.Invoke();
                    }
                });
        }
        private sealed class NoNetwork : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("No live metadata network in this fixture.");
        }
        public void Dispose() { foreach (var client in clients) client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Scope); if (Directory.Exists(Scope)) Directory.Delete(Scope, true); }
    }
}
