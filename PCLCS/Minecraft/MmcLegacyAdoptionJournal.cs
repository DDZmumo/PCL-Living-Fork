using System.Collections.Immutable;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcLegacyAdoptionIntentRecord(int SchemaVersion, Guid OperationId, string SourceRoot, string GameRoot,
    string TargetRoot, string TargetName, string PackageSha256, string SourceFingerprint, string PreviewFingerprint,
    string ConfirmationFingerprint, MmcLegacyAdoptionIntent Intent);
internal sealed record MmcLegacyAdoptionPublication(int SchemaVersion, Guid OperationId, Guid InstanceId, long Generation,
    string PrimarySha256, string GameRootLeaf, string BackupSealSha256);
internal sealed record MmcLegacyAdoptionDecision(int SchemaVersion, Guid OperationId, string IntentSha256, string PublicationSha256);
internal sealed record MmcLegacyAdoptionCompletion(int SchemaVersion, Guid OperationId, string IntentSha256, string PublicationSha256);
internal sealed record MmcLegacyAdoptionRollback(int SchemaVersion, Guid OperationId, string IntentSha256, string? PublicationSha256);
internal sealed record MmcLegacyAdoptionMarker(int SchemaVersion, Guid OperationId, Guid InstanceId, string OperationDirectory, string IntentSha256, string PublicationSha256);

internal sealed class MmcLegacyAdoptionJournal {
    internal const string MarkerRelative = "PCL/MmcLegacyAdoptionPending.json";
    internal string DirectoryPath { get; }
    internal MmcLegacyAdoptionIntentRecord Intent { get; }
    internal string IntentHash { get; }
    internal string PrivateRoot => Path.Combine(DirectoryPath, "private-minecraft", "versions", Intent.TargetName);
    internal string FinalRoot => Intent.TargetRoot;
    private Dictionary<string, string>? producerCleanup;
    private readonly HashSet<string> producerDirectories = new(StringComparer.OrdinalIgnoreCase);
    internal void InitializeProducerCleanup(MmcProducerFileProof? proof) {
        var primary = RequirePrimary(PrivateRoot);
        if (producerCleanup is not null || proof is null || proof.Root != PrivateRoot || proof.InstanceId != primary.Definition.InstanceId || proof.PrimarySha256 != primary.Sha256 || HasDecision())
            throw new IOException("Private cleanup needs the original importer's proven products, never a scan of existing files.");
        producerCleanup = proof.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        producerDirectories.UnionWith(proof.Directories);
    }
    internal void ProducedFile(string relative, string sha256) {
        if (producerCleanup is null) return; // Raw abandoned-producer/recovery operations never acquire deletion permission.
        _ = MmcInstancePaths.Resolve(PrivateRoot).ResolveRelative(MmcPathRole.Instance, relative);
        if (!MmcDurableFiles.IsHash(sha256) || HasDecision()) throw new IOException("A produced cleanup fact needs exact bytes before publication.");
        producerCleanup[relative] = sha256;
    }
    internal void ProducedDirectory(string relative) {
        if (producerCleanup is null) return;
        _ = MmcInstancePaths.Resolve(PrivateRoot).ResolveRelative(MmcPathRole.Instance, relative); producerDirectories.Add(relative);
    }
    internal void DiscardProducerPrivate(Action<MmcInstancePurgeCheckpoint, string?>? checkpoint = null) {
        if (producerCleanup is null || HasDecision() || !Owns(PrivateRoot)) throw new IOException("Unpublished copy has no current-producer cleanup facts.");
        _ = RequirePrimary(PrivateRoot);
        MmcProducerCleanup.Discard(PrivateRoot, Publication()!.Value.Record.GameRootLeaf, producerCleanup, DirectoryPath, IntentHash, checkpoint, producerDirectories);
    }
    internal bool RolledBack() {
        string path = Path.Combine(DirectoryPath, "rolled-back.record"); if (!File.Exists(path)) return false;
        if (MmcDurableFiles.ReadRecord<MmcLegacyAdoptionRollback>(path).Value != new MmcLegacyAdoptionRollback(1, Intent.OperationId, IntentHash, Publication()?.Hash)) throw new IOException("Private copy rollback record changed.");
        return true;
    }
    internal void RecordRolledBack() {
        if (HasDecision() || Directory.Exists(PrivateRoot)) throw new IOException("Only a discarded undecided copy can be marked rolled back.");
        if (!RolledBack()) MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "rolled-back.record"), new MmcLegacyAdoptionRollback(1, Intent.OperationId, IntentHash, Publication()?.Hash));
    }
    internal FileStream AcquireDriver(bool create) {
        string path = Path.Combine(DirectoryPath, "use.lock"); MmcInstancePaths.EnsureNoLinks(path);
        var stream = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try {
            byte[] expected = Encoding.UTF8.GetBytes("PCLLF:LegacyAdoptionUse:v1\n" + Intent.OperationId.ToString("N") + "\n" + IntentHash + "\n");
            if (create) { stream.Write(expected); stream.Flush(true); }
            else {
                if (stream.Length != expected.Length) throw new IOException("转换使用租约的归属或格式变化。");
                byte[] actual = new byte[expected.Length]; stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("转换使用租约不属于当前操作。");
            }
            return stream;
        } catch { stream.Dispose(); throw; }
    }
    private MmcLegacyAdoptionJournal(string directory, MmcLegacyAdoptionIntentRecord intent, string intentHash) { DirectoryPath = directory; Intent = intent; IntentHash = intentHash; }
    internal static MmcLegacyAdoptionJournal Create(MmcLegacyAdoptionPreview preview, MmcLegacyAdoptionCopyRequest request) {
        string target = MmcLegacyAdoptionSnapshot.Canonical(request.TargetRoot);
        if (request.InstanceName != Path.GetFileName(target)) throw new IOException("副本名称与目标目录不一致。");
        Guid id = Guid.NewGuid();
        using var gate = MmcAdmissionGate.Acquire(target, id, TimeSpan.Zero);
        if (MmcInstancePaths.Attributes(target) is not null) throw new IOException("副本名称已存在，不会覆盖原有内容。");
        var intent = new MmcLegacyAdoptionIntentRecord(1, id, preview.Inventory.SourceRoot, preview.Inventory.GameRoot, target, request.InstanceName,
            preview.PackageSha256, preview.Inventory.Fingerprint, preview.PreviewFingerprint, request.ConfirmedPreviewFingerprint, request.Intent);
        string store = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)!)!, "PCL", "MmcLegacyAdoption");
        MmcArtifactIo.Directory(store); string directory = Path.Combine(store, id.ToString("N"));
        MmcOwnedImportReservation.CreateExclusiveDirectory(directory);
        var result = new MmcLegacyAdoptionJournal(directory, intent, MmcDurableFiles.WriteRecord(Path.Combine(directory, "intent.record"), intent));
        result.ValidateLocation(); MmcInstanceCloneReservation.ValidateRoots(intent.SourceRoot, target, result.PrivateRoot);
        MmcArtifactIo.Directory(Path.GetDirectoryName(result.PrivateRoot)!);
        return result;
    }
    internal static MmcLegacyAdoptionJournal Read(string directory) {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); MmcInstancePaths.EnsureNoLinks(directory);
        string record = Path.Combine(directory, "intent.record");
        if (new FileInfo(record).Length > 64 * 1024) throw new IOException("转换操作记录超过预算。");
        var (intent, hash) = MmcDurableFiles.ReadRecord<MmcLegacyAdoptionIntentRecord>(record);
        var result = new MmcLegacyAdoptionJournal(directory, intent, hash); result.ValidateLocation(); return result;
    }
    private void ValidateLocation() {
        if (Intent.SchemaVersion != 1 || Intent.OperationId == Guid.Empty || !Enum.IsDefined(Intent.Intent) ||
            new[] { Intent.PackageSha256, Intent.SourceFingerprint, Intent.PreviewFingerprint, Intent.ConfirmationFingerprint, IntentHash }.Any(hash => !MmcDurableFiles.IsHash(hash)))
            throw new IOException("转换操作记录格式或摘要无效。");
        string target = MmcLegacyAdoptionSnapshot.Canonical(Intent.TargetRoot), source = MmcLegacyAdoptionSnapshot.Canonical(Intent.SourceRoot);
        if (target != Intent.TargetRoot || source != Intent.SourceRoot || Path.GetFileName(target) != Intent.TargetName ||
            !string.Equals(DirectoryPath, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)!)!, "PCL", "MmcLegacyAdoption", Intent.OperationId.ToString("N")), MmcLegacyAdoptionSnapshot.Comparison))
            throw new IOException("转换操作记录不属于目标游戏文件夹。");
        MmcInstanceCloneReservation.ValidateRoots(source, target, PrivateRoot);
        string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Intent.GameRoot));
        if (!new[] { source, Path.Combine(source, "minecraft"), Path.Combine(source, ".minecraft") }.Any(root => string.Equals(game, root, MmcLegacyAdoptionSnapshot.Comparison)))
            throw new IOException("转换记录不能引用共享游戏目录。");
        MmcInstancePaths.EnsureNoLinks(game);
    }
    internal (MmcLegacyAdoptionPublication Record, string Hash)? Publication() {
        string path = Path.Combine(DirectoryPath, "publication.record"); if (!File.Exists(path)) return null;
        var value = MmcDurableFiles.ReadRecord<MmcLegacyAdoptionPublication>(path);
        if (value.Value.SchemaVersion != 1 || value.Value.OperationId != Intent.OperationId || value.Value.InstanceId == Guid.Empty || value.Value.Generation != 1 ||
            !MmcDurableFiles.IsHash(value.Value.PrimarySha256) || !MmcDurableFiles.IsHash(value.Value.BackupSealSha256) || value.Value.GameRootLeaf is not ("minecraft" or ".minecraft"))
            throw new IOException("转换发布记录损坏或不支持。");
        return (value.Value, value.Hash);
    }
    internal void RecordPublication(MmcStoredPrimaryDefinition primary) {
        var backup = MmcDurableFiles.ReadRecord<MmcLegacyAdoptionBackupSeal>(Path.Combine(DirectoryPath, "backup.record"));
        if (primary.Definition.Generation != 1 || primary.Definition.Origin.PackageSha256 != Intent.PackageSha256) throw new IOException("转换模板没有采用所选原包的新身份。");
        MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "publication.record"), new MmcLegacyAdoptionPublication(1, Intent.OperationId,
            primary.Definition.InstanceId, 1, primary.Sha256, primary.Definition.Layout.GameRootLeaf, backup.Hash));
    }
    internal MmcLegacyAdoptionBackupSeal Backup() {
        var backup = MmcDurableFiles.ReadRecord<MmcLegacyAdoptionBackupSeal>(Path.Combine(DirectoryPath, "backup.record"));
        if (backup.Value.SchemaVersion != 1 || !MmcDurableFiles.IsHash(backup.Value.InventorySha256) || !MmcDurableFiles.IsHash(backup.Value.BackupSha256) || backup.Value.BackupBytes < 0 ||
            backup.Value.BackupBytes > MmcLegacyAdoptionSnapshot.MaximumBytes + 256 * 1024 * 1024 || Publication() is { } publication && publication.Record.BackupSealSha256 != backup.Hash)
            throw new IOException("转换备份封存记录不一致。");
        return backup.Value;
    }
    private byte[] Marker() {
        var publication = Publication() ?? throw new IOException("转换模板尚未完整发布。");
        return JsonSerializer.SerializeToUtf8Bytes(new MmcLegacyAdoptionMarker(1, Intent.OperationId, publication.Record.InstanceId, DirectoryPath, IntentHash, publication.Hash));
    }
    internal void MarkPrivate() {
        byte[] marker = Marker(); MmcDurableFiles.WriteNew(MmcInstancePaths.Resolve(PrivateRoot).ResolveRelative(MmcPathRole.Instance, MarkerRelative), marker);
        ProducedFile(MarkerRelative, MmcDurableFiles.Hash(marker));
    }
    internal bool Owns(string root) {
        if (!Directory.Exists(root) || Publication() is null) return false;
        try {
            string path = MmcInstancePaths.Resolve(root).ResolveRelative(MmcPathRole.Instance, MarkerRelative);
            if (MmcInstancePaths.Attributes(path) is not FileAttributes attributes || (attributes & FileAttributes.Directory) != 0 || new FileInfo(path).Length > 8192) return false;
            return MmcDurableFiles.Observe(path) == MmcDurableFiles.Hash(Marker());
        }
        catch (IOException) { return false; }
    }
    internal string ValidatePendingPermit(MmcAdmissionLease lease) {
        lease.AssertHeld(); var publication = Publication() ?? throw new IOException("转换发布记录缺失。");
        if (lease.InstanceId != publication.Record.InstanceId || lease.VersionRoot != PrivateRoot && lease.VersionRoot != FinalRoot || !Owns(lease.VersionRoot))
            throw new IOException("转换许可没有当前路径、新实例身份和精确标记。");
        var reread = Read(DirectoryPath);
        if (reread.IntentHash != IntentHash || reread.Publication() != publication) throw new IOException("转换记录在实际写入前变化。");
        RequirePrimary(lease.VersionRoot); return MmcDurableFiles.Hash(Marker());
    }
    internal MmcPendingOperationPermit Enter(MmcAdmissionLease lease) => MmcPendingOperationPermit.ForLegacyAdoption(lease, this);
    internal MmcStoredPrimaryDefinition RequirePrimary(string root) {
        var publication = Publication() ?? throw new IOException("转换发布记录缺失。");
        var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(root, publication.Record.GameRootLeaf));
        if (primary.Definition.InstanceId != publication.Record.InstanceId || primary.Definition.Generation != publication.Record.Generation || primary.Sha256 != publication.Record.PrimarySha256)
            throw new IOException("转换副本被替换或主定义变化，所有路径已保留。");
        return primary;
    }
    internal bool HasDecision() {
        string path = Path.Combine(DirectoryPath, "CommitDecided.record"); if (!File.Exists(path)) return false;
        var publication = Publication() ?? throw new IOException("转换决定缺少模板事实。");
        if (MmcDurableFiles.ReadRecord<MmcLegacyAdoptionDecision>(path).Value != new MmcLegacyAdoptionDecision(1, Intent.OperationId, IntentHash, publication.Hash))
            throw new IOException("转换发布决定不一致。");
        return true;
    }
    internal void Decide() { var publication = Publication() ?? throw new IOException("转换模板未就绪。"); MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "CommitDecided.record"), new MmcLegacyAdoptionDecision(1, Intent.OperationId, IntentHash, publication.Hash)); }
    internal bool IsCompleted() {
        string path = Path.Combine(DirectoryPath, "completed.record"); if (!File.Exists(path)) return false;
        var publication = Publication() ?? throw new IOException("转换完成缺少模板事实。");
        if (!HasDecision() || MmcDurableFiles.ReadRecord<MmcLegacyAdoptionCompletion>(path).Value != new MmcLegacyAdoptionCompletion(1, Intent.OperationId, IntentHash, publication.Hash))
            throw new IOException("转换完成记录不一致。");
        return true;
    }
    internal void Complete() {
        if (IsCompleted()) return;
        var publication = Publication() ?? throw new IOException("转换模板未就绪。"); MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "completed.record"), new MmcLegacyAdoptionCompletion(1, Intent.OperationId, IntentHash, publication.Hash));
    }
    internal void ClearMarker(string root) {
        if (!Owns(root)) throw new IOException("转换标记归属变化，不能代为删除。");
        File.Delete(MmcInstancePaths.Resolve(root).ResolveRelative(MmcPathRole.Instance, MarkerRelative));
    }
}
internal sealed partial class MmcPendingOperationPermit {
    internal static MmcPendingOperationPermit ForLegacyAdoption(MmcAdmissionLease lease, MmcLegacyAdoptionJournal owner) =>
        new(lease, ImmutableDictionary<string, string>.Empty.Add(MmcLegacyAdoptionJournal.MarkerRelative, owner.ValidatePendingPermit(lease)));
}
