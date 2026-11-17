using System.Text;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcImportReservationPurpose { InitialImport, Clone }
internal sealed record MmcImportOwnerRecord(int SchemaVersion, Guid OperationId, MmcImportReservationPurpose Purpose, Guid InstanceId,
    string TemporaryRoot, string FinalRoot, string PackageSha256, string MarkerSha256, string IgnoreSha256);
internal sealed record MmcImportPublicationRecord(int SchemaVersion, Guid OperationId, string OwnerSha256, string PrimarySha256,
    string PayloadSha256, string GameRootLeaf);
internal sealed record MmcImportCompletedRecord(int SchemaVersion, Guid OperationId, string OwnerSha256, string PublicationSha256);
internal sealed record MmcImportRollbackRecord(int SchemaVersion, Guid OperationId, string OwnerSha256);

internal sealed class MmcImportOperation {
    internal const string StoreName = "MmcImportOperations";
    internal string DirectoryPath { get; }
    internal MmcImportOwnerRecord Owner { get; }
    internal string OwnerHash { get; }
    private MmcImportOperation(string directory, MmcImportOwnerRecord owner, string hash) { DirectoryPath = directory; Owner = owner; OwnerHash = hash; }
    internal static (MmcImportOperation Operation, FileStream Use) Create(string temporary, string final, Guid id, string package,
        byte[] marker, byte[] ignore, MmcImportReservationPurpose purpose) {
        string parent = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(final)!)!, "PCL", StoreName);
        MmcArtifactIo.Directory(parent); Guid operationId = Guid.NewGuid(); string directory = Path.Combine(parent, operationId.ToString("N"));
        MmcOwnedImportReservation.CreateExclusiveDirectory(directory);
        var owner = new MmcImportOwnerRecord(1, operationId, purpose, id, temporary, final, package, MmcDurableFiles.Hash(marker), MmcDurableFiles.Hash(ignore));
        var operation = new MmcImportOperation(directory, owner, MmcDurableFiles.WriteRecord(Path.Combine(directory, "reservation.record"), owner));
        operation.Validate(); return (operation, operation.AcquireUse(create: true));
    }
    internal static MmcImportOperation Read(string directory) {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); MmcInstancePaths.EnsureNoLinks(directory);
        var record = ReadSmall<MmcImportOwnerRecord>(Path.Combine(directory, "reservation.record"));
        var operation = new MmcImportOperation(directory, record.Value, record.Hash); operation.Validate(); return operation;
    }
    private void Validate() {
        if (Owner.SchemaVersion != 1 || Owner.OperationId == Guid.Empty || Owner.InstanceId == Guid.Empty || !Enum.IsDefined(Owner.Purpose) ||
            new[] { Owner.PackageSha256, Owner.MarkerSha256, Owner.IgnoreSha256, OwnerHash }.Any(hash => !MmcDurableFiles.IsHash(hash))) throw new IOException("Unknown/damaged import producer record.");
        string temporary = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(Owner.TemporaryRoot));
        string final = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(Owner.FinalRoot));
        if (temporary != Owner.TemporaryRoot || final != Owner.FinalRoot || Path.GetDirectoryName(temporary) != Path.GetDirectoryName(final) || temporary == final ||
            !Path.GetFileName(temporary).StartsWith(".pcllf-mmc-import-", StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(temporary)[".pcllf-mmc-import-".Length..], "N", out _))
            throw new IOException("Import producer paths are not exact reserved siblings.");
        string expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(final)!)!, "PCL", StoreName, Owner.OperationId.ToString("N"));
        if (!string.Equals(expected, DirectoryPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Import operation escaped its owning Minecraft store.");
    }
    internal FileStream AcquireUse(bool create) {
        string path = Path.Combine(DirectoryPath, "producer.use"); MmcInstancePaths.EnsureNoLinks(path);
        // This handle is not thread-affine and stays outside both moving roots.
        // FileShare.Read denies another producer/recovery writer; no PID inference.
        var stream = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try {
            byte[] expected = Encoding.UTF8.GetBytes("PCLLF:MmcImportProducer/v1\n" + Owner.OperationId.ToString("N") + "\n" + OwnerHash + "\n");
            if (create) { stream.Write(expected); stream.Flush(true); }
            else {
                if (stream.Length != expected.Length) throw new IOException("Import producer lease format changed.");
                byte[] actual = new byte[expected.Length]; stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("Import producer lease belongs to another reservation.");
            }
            return stream;
        } catch { stream.Dispose(); throw; }
    }
    internal void VerifySameOwner() { if (Read(DirectoryPath).OwnerHash != OwnerHash) throw new IOException("Import operation owner changed."); }
    internal (MmcImportPublicationRecord Value, string Hash)? Publication() {
        string path = Path.Combine(DirectoryPath, "publication.record"); if (!File.Exists(path)) return null;
        var record = ReadSmall<MmcImportPublicationRecord>(path);
        if (record.Value.SchemaVersion != 1 || record.Value.OperationId != Owner.OperationId || record.Value.OwnerSha256 != OwnerHash ||
            !MmcDurableFiles.IsHash(record.Value.PrimarySha256) || !MmcDurableFiles.IsHash(record.Value.PayloadSha256) || record.Value.GameRootLeaf is not ("minecraft" or ".minecraft")) throw new IOException("Import publication seal is damaged.");
        return record;
    }
    internal void SealPublication(string primary, string payload, string leaf) {
        var expected = new MmcImportPublicationRecord(1, Owner.OperationId, OwnerHash, primary, payload, leaf);
        if (Publication() is { } existing) { if (existing.Value != expected) throw new IOException("Import publication seal changed."); return; }
        MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "publication.record"), expected);
    }
    internal bool Completed() {
        string path = Path.Combine(DirectoryPath, "completed.record"); if (!File.Exists(path)) return false;
        var publication = Publication() ?? throw new IOException("Completed import has no publication seal.");
        if (ReadSmall<MmcImportCompletedRecord>(path).Value != new MmcImportCompletedRecord(1, Owner.OperationId, OwnerHash, publication.Hash)) throw new IOException("Completed import record changed.");
        return true;
    }
    internal void Complete() {
        if (Completed()) return; var publication = Publication() ?? throw new IOException("Import publication was not sealed.");
        MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "completed.record"), new MmcImportCompletedRecord(1, Owner.OperationId, OwnerHash, publication.Hash));
    }
    internal bool RolledBack() {
        string path = Path.Combine(DirectoryPath, "rolled-back.record"); if (!File.Exists(path)) return false;
        if (ReadSmall<MmcImportRollbackRecord>(path).Value != new MmcImportRollbackRecord(1, Owner.OperationId, OwnerHash)) throw new IOException("Import rollback record changed.");
        return true;
    }
    internal void RecordRolledBack() {
        VerifySameOwner(); if (Publication() is not null || Completed()) throw new IOException("A sealed import cannot be marked rolled back.");
        if (!RolledBack()) MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath, "rolled-back.record"), new MmcImportRollbackRecord(1, Owner.OperationId, OwnerHash));
    }
    internal static (T Value, string Hash) ReadSmall<T>(string path) {
        MmcInstancePaths.EnsureNoLinks(path); if (new FileInfo(path).Length > 64 * 1024) throw new IOException("Import recovery record exceeds its budget.");
        return MmcDurableFiles.ReadRecord<T>(path);
    }
}
