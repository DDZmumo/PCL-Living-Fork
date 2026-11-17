using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed record MmcPackageUpdateOwner(int SchemaVersion, Guid OperationId, string DirectoryRoot, string CandidateRoot,
    string VersionRoot, Guid InstanceId, long Generation, string PrimarySha256, string PackageSha256);
internal sealed record MmcPackageUpdateCandidate(int SchemaVersion, Guid OperationId, Guid CandidateId, string PrimarySha256, string SnapshotSha256);
internal sealed class MmcPackageUpdateJob {
    internal MmcPackageUpdateOwner Owner { get; }
    internal string Root => Owner.DirectoryRoot;
    internal string CandidateRoot => Owner.CandidateRoot;
    private MmcPackageUpdateJob(MmcPackageUpdateOwner owner) => Owner = owner;
    internal static MmcPackageUpdateJob Open(string root, Guid operationId) {
        if (!Path.IsPathFullyQualified(root) || operationId == Guid.Empty) throw new IOException("A fixed owned operation directory is required.");
        var owner = MmcDurableFiles.ReadRecord<MmcPackageUpdateOwner>(Path.Combine(Path.GetFullPath(root), "owner.record")).Value;
        var job = new MmcPackageUpdateJob(owner);
        if (!string.Equals(job.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || owner.OperationId != operationId) throw new IOException("Operation ownership selection differs.");
        job.Verify(); return job;
    }
    internal IDisposable Use() {
        Verify(); var mutex = new Mutex(false, "Local\\PCLLF.MmcPackageUpdate." + MmcDurableFiles.Hash(System.Text.Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
        bool held = false;
        try {
            try { if (!mutex.WaitOne(TimeSpan.Zero)) throw new IOException("Another worker is still using this ZIP candidate."); held = true; }
            catch (AbandonedMutexException) { held = true; /* Acquired; every file/owner is still revalidated. */ }
            Verify(); return new UseScope(mutex);
        } catch { if (held) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private sealed class UseScope(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
    internal static MmcPackageUpdateJob Create(MmcInstancePackageUpdateRequest request, string packageSha256) {
        string version = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.VersionRoot));
        string mcRoot = Path.GetDirectoryName(Path.GetDirectoryName(version)!)!;
        string parent = Path.Combine(mcRoot, "PCL", "MmcPackageUpdates"), id = Guid.NewGuid().ToString("N");
        MmcArtifactIo.Directory(parent); string root = Path.Combine(parent, id);
        MmcOwnedImportReservation.CreateExclusiveDirectory(root); MmcArtifactIo.Directory(Path.Combine(root, "versions"));
        var owner = new MmcPackageUpdateOwner(1, Guid.ParseExact(id, "N"), root, Path.Combine(root, "versions", "candidate"), version,
            request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256, packageSha256);
        MmcDurableFiles.WriteRecord(Path.Combine(root, "owner.record"), owner); return new(owner);
    }
    internal void Verify() {
        MmcInstancePaths.EnsureNoLinks(Root); MmcInstancePaths.EnsureNoLinks(CandidateRoot);
        MmcInstancePaths.ValidateVersionRoot(Owner.VersionRoot);
        if (Path.GetFileName(Root) != Owner.OperationId.ToString("N") || Path.GetFileName(Path.GetDirectoryName(Root)) != "MmcPackageUpdates" ||
            Owner.SchemaVersion != 1 || Owner.InstanceId == Guid.Empty || Owner.Generation < 1 || !MmcDurableFiles.IsHash(Owner.PrimarySha256) || !MmcDurableFiles.IsHash(Owner.PackageSha256) ||
            MmcDurableFiles.ReadRecord<MmcPackageUpdateOwner>(Path.Combine(Root, "owner.record")).Value != Owner)
            throw new IOException("MMC package-update ownership changed; the candidate was preserved.");
        string expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Owner.VersionRoot)!)!, "PCL", "MmcPackageUpdates", Owner.OperationId.ToString("N"));
        if (!string.Equals(expected, Root, StringComparison.OrdinalIgnoreCase) || CandidateRoot != Path.Combine(Root, "versions", "candidate"))
            throw new IOException("MMC package-update candidate is outside its owned external staging role.");
    }
    internal ImmutableArray<MmcHistoryFile> Snapshot(CancellationToken token) {
        Verify(); var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(CandidateRoot));
        var paths = MmcInstancePaths.Resolve(CandidateRoot, primary.Definition.Layout.GameRootLeaf);
        var inventory = MmcInstanceCloneSnapshot.Capture(paths, primary.Definition, new(true, true, true), token);
        var result = inventory.Files.Select(value => new MmcHistoryFile(value.RelativePath, value.Sha256, value.SizeBytes))
            .Append(new("PCL/InstanceDefinition.json", primary.Sha256, new FileInfo(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json")).Length))
            .Append(MmcInstanceHistoryFiles.Measure(paths, "candidate.json", token)).OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        return result;
    }
    internal void Seal(ImmutableArray<MmcHistoryFile> snapshot) {
        Verify(); var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(CandidateRoot));
        if (primary.Definition.Origin.PackageSha256 != Owner.PackageSha256) throw new IOException("Candidate import belongs to a different ZIP.");
        string digest = HashSnapshot(snapshot);
        MmcDurableFiles.WriteRecord(Path.Combine(Root, "candidate.record"), new MmcPackageUpdateCandidate(1, Owner.OperationId, primary.Definition.InstanceId, primary.Sha256, digest));
    }
    internal MmcStoredPrimaryDefinition VerifyCandidate(ImmutableArray<MmcHistoryFile> snapshot, CancellationToken token) {
        Verify(); var sealedCandidate = MmcDurableFiles.ReadRecord<MmcPackageUpdateCandidate>(Path.Combine(Root, "candidate.record")).Value;
        var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(CandidateRoot));
        if (sealedCandidate.SchemaVersion != 1 || sealedCandidate.OperationId != Owner.OperationId || primary.Sha256 != sealedCandidate.PrimarySha256 ||
            primary.Definition.InstanceId != sealedCandidate.CandidateId || primary.Definition.Origin.PackageSha256 != Owner.PackageSha256 ||
            HashSnapshot(snapshot) != sealedCandidate.SnapshotSha256 || !snapshot.SequenceEqual(Snapshot(token)))
            throw new IOException("The fixed author candidate files/definition changed after import.");
        return primary;
    }
    private static string HashSnapshot(ImmutableArray<MmcHistoryFile> files) => MmcDurableFiles.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(files));
    internal void DeleteOwned() {
        Verify(); int nodes = 0;
        Inspect(Root, 0); // No deletion until the complete ownership boundary/link/session scan succeeds.
        Delete(Root, true);
        string parent = Path.GetDirectoryName(Root)!;
        try { if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent, false); } catch (IOException) { }
        void Inspect(string directory, int depth) {
            MmcInstancePaths.EnsureNoLinks(directory); if (depth > 128 || ++nodes > 400000) throw new IOException("Candidate cleanup path budget exceeded.");
            if (Path.GetFileName(directory) is "MmcSessions" or "MmcCommands" && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Candidate cleanup found an active or unconfirmed session or package command.");
            foreach (string child in Directory.EnumerateFileSystemEntries(directory)) {
                MmcInstancePaths.EnsureNoLinks(child); if (++nodes > 400000) throw new IOException("Candidate cleanup count budget exceeded.");
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) Inspect(child, depth + 1);
            }
        }
        void Delete(string directory, bool ownerRoot = false) {
            Verify(); MmcInstancePaths.EnsureNoLinks(directory);
            foreach (string child in Directory.EnumerateFileSystemEntries(directory)) {
                if (ownerRoot && Path.GetFileName(child) == "owner.record") continue;
                MmcInstancePaths.EnsureNoLinks(child);
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) Delete(child); else File.Delete(child);
            }
            if (ownerRoot) { Verify(); File.Delete(Path.Combine(Root, "owner.record")); }
            Directory.Delete(directory, false);
        }
    }
}
