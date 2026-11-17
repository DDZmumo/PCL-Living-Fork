using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcImportStageFile(string RelativePath, string Sha256, long SizeBytes);
internal sealed record MmcImportStage(string RelativeRoot, string OwnerSha256, ImmutableArray<MmcImportStageFile> Files);
internal sealed record MmcImportPreparation(int SchemaVersion, string OwnerSha256, ImmutableArray<MmcImportStage> Stages);

public sealed partial class MmcOwnedImportReservation {
    internal void VerifyRecoveryMarker(string root, bool moved) => VerifyMarker(root, allowMissingIgnore: moved && operation?.Publication() is not null);
    internal static MmcOwnedImportReservation Reopen(MmcImportOperation operation) {
        if (operation.Owner.Purpose != MmcImportReservationPurpose.InitialImport) throw new IOException("Clone reservations require their independent personal snapshot protocol.");
        var record = operation.Owner;
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, InstanceId = record.InstanceId, PackageSha256 = record.PackageSha256, FinalRoot = record.FinalRoot, TemporaryRoot = record.TemporaryRoot });
        byte[] ignore = Encoding.UTF8.GetBytes("PCLLF.MmcImport/v1\n" + record.InstanceId.ToString("D") + "\n" + record.PackageSha256);
        if (MmcDurableFiles.Hash(marker) != record.MarkerSha256 || MmcDurableFiles.Hash(ignore) != record.IgnoreSha256) throw new IOException("Reservation marker facts do not match their producer proof.");
        var use = operation.AcquireUse(create: false);
        return new(record.FinalRoot, record.TemporaryRoot, record.InstanceId, record.PackageSha256, marker, ignore) { operation = operation, producerUse = use };
    }
    internal void RecordRecoveryPreparation(params string[] stageRoots) {
        AssertProducerUse(); if (operation!.Owner.Purpose != MmcImportReservationPurpose.InitialImport) throw new IOException("Only initial import can record this preparation.");
        var paths = MmcInstancePaths.Resolve(TemporaryRoot); var stages = ImmutableArray.CreateBuilder<MmcImportStage>(); int count = 0;
        foreach (string root in stageRoots.Distinct(StringComparer.OrdinalIgnoreCase)) {
            _ = VerifyPayloadWorkspace(paths, root); string owner = Path.Combine(root, "owner.txt");
            var files = ImmutableArray.CreateBuilder<MmcImportStageFile>();
            foreach (string file in BoundedFiles(root)) {
                if (++count > 100000) throw new IOException("Import stage manifest exceeds its entry budget.");
                string hash = MmcDurableFiles.Observe(file)!;
                files.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), hash, new FileInfo(file).Length));
            }
            stages.Add(new(Path.GetRelativePath(TemporaryRoot, root).Replace('\\', '/'), MmcDurableFiles.Observe(owner)!, files.ToImmutable()));
        }
        MmcDurableFiles.WriteRecord(Path.Combine(operation.DirectoryPath, "preparation.record"), new MmcImportPreparation(1, operation.OwnerHash, stages.ToImmutable()));
    }
    internal void RecordPreparedCleanupProducts(IEnumerable<MmcFileMutation> mutations, params string[] stageRoots) {
        AssertProducerUse(); var paths = MmcInstancePaths.Resolve(TemporaryRoot);
        foreach (var mutation in mutations) {
            _ = paths.ResolveRelative(MmcPathRole.Instance, mutation.RelativePath);
            int separator = mutation.RelativePath.LastIndexOf('/'); if (separator > 0) RecordCreatedDirectory(mutation.RelativePath[..separator]);
            if (mutation.NewSourceRelativePath is { } source) {
                _ = paths.ResolveRelative(MmcPathRole.Instance, source);
                if (!MmcDurableFiles.IsHash(mutation.ExpectedNewSha256)) throw new IOException("A prepared cleanup product needs its producer's expected digest.");
                cleanupFiles[source] = mutation.ExpectedNewSha256!;
            }
        }
        foreach (string root in stageRoots.Distinct(StringComparer.OrdinalIgnoreCase)) {
            _ = VerifyPayloadWorkspace(paths, root); RecordCreatedDirectory(Path.GetRelativePath(TemporaryRoot, root).Replace('\\', '/'));
            cleanupFiles[Path.GetRelativePath(TemporaryRoot, Path.Combine(root, "owner.txt")).Replace('\\', '/')] =
                MmcDurableFiles.Hash(Encoding.UTF8.GetBytes("PCLLF.MmcArtifactWorkspace/v1\n" + Path.GetFileName(root) + "\n" + TemporaryRoot));
        }
    }
    internal void RecordRecoveryProduct(MmcTransactionProducedFile product) {
        CurrentOperation.VerifySameOwner();
        string[] parts = product.RelativePath.Split('/');
        string[] name = parts.Length == 4 && parts[3].EndsWith(".bin", StringComparison.Ordinal) ? parts[3][..^4].Split('-') : [];
        if (product.InstanceId != InstanceId || product.CanonicalRoot != TemporaryRoot || product.ExpectedGeneration != 0 || product.NewGeneration != 1 ||
            product.TransactionId == Guid.Empty || product.MutationIndex < 0 || !MmcDurableFiles.IsHash(product.Sha256) || parts.Length != 4 ||
            parts[0] != "PCL" || parts[1] != "MmcTransactions" || parts[2] != product.TransactionId.ToString("N") || name.Length != 3 || name[0] != "backup" ||
            name[1] != product.MutationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) || !Guid.TryParseExact(name[2], "N", out Guid objectId) || objectId == Guid.Empty)
            throw new IOException("A recovery product does not belong to this exact initial import producer.");
        string relative = "PCL/MmcTransactionHistory/" + parts[2] + "/" + parts[3];
        cleanupFiles[relative] = product.Sha256;
        RecordCreatedDirectory("PCL/MmcTransactionHistory/" + parts[2]);
    }
    // Only the still-owned producer object has this in-memory list of bytes it prepared.
    // Historical records never reconstruct permission to delete a user's directory.
    internal void DiscardProducerTemporary(Action<MmcInstancePurgeCheckpoint, string?>? checkpoint = null) {
        if (producerUse is not null) throw new IOException("The failed import producer has not drained.");
        producerUse = CurrentOperation.AcquireUse(create: false);
        try {
            AssertProducerUse();
            if (CurrentOperation.Publication() is not null || CurrentOperation.Completed()) throw new IOException("A sealed import must finish publication, never discard its instance.");
            InstanceConfigProviders.FlushAndDisposeWithin(TemporaryRoot);
            using var quiet = MmcConfigurationCoordinator.Quiesce(TemporaryRoot);
            using var lease = MmcAdmissionGate.Acquire(TemporaryRoot, InstanceId, TimeSpan.Zero);
            VerifyMarker(TemporaryRoot); using var permit = EnterPending(lease);
            MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
            MmcProducerCleanup.Discard(TemporaryRoot, payloadManifest?.GameRootLeaf ?? "minecraft", cleanupFiles, CurrentOperation.DirectoryPath, CurrentOperation.OwnerHash, checkpoint, cleanupDirectories);
            CurrentOperation.RecordRolledBack();
        } finally { Dispose(); }
    }
    internal void RecoverRecordedStages(MmcAdmissionLease lease) {
        AssertProducerUse(); lease.AssertHeld();
        var prep = MmcDurableFiles.ReadRecord<MmcImportPreparation>(Path.Combine(operation!.DirectoryPath, "preparation.record")).Value;
        if (prep.SchemaVersion != 1 || prep.OwnerSha256 != operation.OwnerHash || prep.Stages.IsDefault || prep.Stages.Length > 16) throw new IOException("Import stage ownership record is unknown/damaged.");
        var paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        foreach (var stage in prep.Stages) {
            if (stage.Files.IsDefault || stage.Files.Length > 100000 || !MmcDurableFiles.IsHash(stage.OwnerSha256)) throw new IOException("Import stage facts are damaged.");
            string root = paths.ResolveRelative(MmcPathRole.Instance, stage.RelativeRoot);
            if (!Directory.Exists(root)) continue;
            _ = VerifyPayloadWorkspace(paths, root);
            if (MmcDurableFiles.Observe(Path.Combine(root, "owner.txt")) != stage.OwnerSha256) throw new IOException("Import stage owner changed.");
            var expected = stage.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (string file in BoundedFiles(root)) {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!expected.TryGetValue(relative, out var known) || new FileInfo(file).Length != known.SizeBytes || MmcDurableFiles.Observe(file) != known.Sha256)
                    throw new IOException("Import stage contains changed/foreign bytes; preserving every file.");
            }
        }
        // All stages were checked before removing any. The exclusive producer Use
        // is already held, and only these durable GUIDs can be cleaned.
        foreach (var stage in prep.Stages) MmcArtifactWorkspace.RecoverOwned(lease, stage.RelativeRoot, TemporaryRoot);
    }
    internal void RestorePayloadFacts(MmcInstancePaths paths, MmcInstanceDefinition definition) {
        AssertProducerUse();
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, PayloadManifestPath, 64 * 1024 * 1024);
        var manifest = JsonSerializer.Deserialize<MmcPrivatePayloadManifest>(bytes) ?? throw new IOException("Import payload inventory is missing.");
        if (manifest.SchemaVersion != 1 || manifest.InstanceId != InstanceId || manifest.PackageSha256 != packageSha256 || manifest.TemporaryRoot != TemporaryRoot || manifest.FinalRoot != FinalRoot ||
            manifest.GameRootLeaf != definition.Layout.GameRootLeaf || manifest.Files.IsDefault || manifest.Files.Length > 100000 || definition.ParentInstanceId is not null ||
            definition.AuthorFileBaseline is not { } baselineRef || baselineRef.ContentSha256 != manifest.BaselineSha256) throw new IOException("Import payload facts belong to another source/reservation.");
        var baselineBytes = MmcSavedSourceStore.ReadBounded(paths, baselineRef.ObjectRelativePath, 64 * 1024 * 1024);
        var baseline = MmcPackageFileBaselineCodec.Read(new(baselineBytes), baselineRef, packageSha256, definition.SourceBundle.RelativeRoot);
        if (baseline.HasErrors || baseline.Baseline is null) throw new IOException("Import author baseline cannot validate its payload.");
        var expected = baseline.Baseline.Baseline.Files.Where(file => file.TargetRelativePath is not null && file.Role is PCLLF.MMCC.Core.MmcPackageFileRole.Game or PCLLF.MMCC.Core.MmcPackageFileRole.InstanceExtra)
            .ToDictionary(file => file.TargetRelativePath!, StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (expected.Count != manifest.Files.Length) throw new IOException("Import payload membership differs from its adopted baseline.");
        foreach (var file in manifest.Files) {
            if (!expected.Remove(file.TargetRelativePath, out var author) || file.SourceRelativePath != author.RelativePath || file.Role.ToString() != author.Role.ToString() || file.Sha256 != author.Sha256 || file.SizeBytes != author.SizeBytes || file.Crc32 != author.Crc32 ||
                file.SizeBytes < 0 || file.SizeBytes > MmcDurableFiles.MaximumFileBytes || (total = checked(total + file.SizeBytes)) > MaximumPayloadBytes) throw new IOException("Import payload member changed.");
        }
        if (total != manifest.TotalBytes) throw new IOException("Import payload total changed.");
        var seal = operation!.Publication();
        if (seal is { } publication && (publication.Value.PrimarySha256 != MmcSavedSourceStore.ReadPrimary(paths).Sha256 || publication.Value.PayloadSha256 != MmcDurableFiles.Hash(bytes))) throw new IOException("Sealed import publication bytes changed.");
        payloadManifest = manifest; payloadManifestHash = MmcDurableFiles.Hash(bytes); payloadPlaced = true;
        VerifyInitialPayload(paths, definition);
        var gameExpected = manifest.Files.ToDictionary(file => file.TargetRelativePath, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var output in definition.DerivedOutputs.Where(output => output.Kind == MmcDerivedOutputKind.AssetMappedResource && output.Sha256 is not null)) gameExpected[output.TargetRelativePath] = output.Sha256!;
        foreach (string leaf in new[] { "minecraft", ".minecraft" }) {
            string root = paths.ResolveRelative(MmcPathRole.Instance, leaf); if (!Directory.Exists(root)) continue;
            foreach (string file in BoundedFiles(root)) {
                string relative = Path.GetRelativePath(paths.InstanceRoot, file).Replace('\\', '/');
                if (!gameExpected.TryGetValue(relative, out string? hash) || MmcDurableFiles.Observe(file) != hash) throw new IOException("Unpublished import contains foreign/changed game data; preserving it for review.");
            }
        }
    }
    private static IEnumerable<string> BoundedFiles(string root) {
        Stack<string> directories = new(); directories.Push(root); int count = 0;
        while (directories.Count != 0) {
            string directory = directories.Pop(); MmcInstancePaths.EnsureNoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                MmcInstancePaths.EnsureNoLinks(path); if (++count > 100000) throw new IOException("Import recovery directory exceeds its entry budget.");
                if (Directory.Exists(path)) directories.Push(path); else yield return path;
            }
        }
    }
}
