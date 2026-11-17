using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcInitialPayloadCheckpoint { Prepared, BeforeMove, Moved }
internal sealed record MmcPrivatePayloadFile(string SourceRelativePath, string TargetRelativePath, MmcPackageFileRole Role,
    string Sha256, long SizeBytes, uint Crc32);
internal sealed record MmcPrivatePayloadManifest(int SchemaVersion, Guid InstanceId, string PackageSha256, string BaselineSha256,
    string TemporaryRoot, string FinalRoot, string GameRootLeaf, string WorkspaceRelativePath, long TotalBytes,
    ImmutableArray<MmcPrivatePayloadFile> Files);

public sealed partial class MmcOwnedImportReservation {
    private const string PayloadManifestPath = "PCL/MmcImportPayload.json";
    private const long MaximumPayloadBytes = 64L * 1024 * 1024 * 1024;
    private MmcPrivatePayloadManifest? payloadManifest;
    private string? payloadManifestHash;
    private bool payloadPlaced;

    /// <summary>
    /// Only the exclusive unpublished reservation can adopt initial player payload. Each source was
    /// already CRC/hash checked and flushed by the stager; this validates it again, then uses a
    /// no-overwrite same-volume rename. Final publication must remeasure every placed file.
    /// </summary>
    internal void AdoptPreparedGameFiles(MmcPreparedPackageFiles prepared, MmcValidatedPackageBaseline baseline,
        CancellationToken cancellationToken = default, IProgress<MmcInstanceBackendProgress>? progress = null,
        Action<MmcInitialPayloadCheckpoint, int>? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(baseline);
        if (payloadManifest is not null) throw new IOException("Private payload adoption was already attempted; preserve its recovery record.");
        var paths = MmcInstancePaths.Resolve(TemporaryRoot, baseline.GameRootLeaf);
        using var gate = MmcAdmissionGate.Acquire(TemporaryRoot, InstanceId, TimeSpan.Zero, cancellationToken);
        using var pendingPermit = EnterPending(gate);
        VerifyMarker(TemporaryRoot); MmcFileTransactions.AssertNoPending(gate); MmcSessions.AssertNoActive(gate);
        if (MmcInstancePaths.Attributes(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json")) is not null)
            throw new IOException("Private payload adoption is allowed only before the initial structural definition exists.");
        if (baseline.Baseline.PackageSha256 != packageSha256 || baseline.Baseline.FormatVersion != 1 ||
            baseline.BaselineSha256 != baseline.Reference.ContentSha256)
            throw new IOException("Private payload facts do not belong to this package reservation.");
        string workspace = VerifyPayloadWorkspace(paths, prepared.StageRoot);
        var observations = prepared.Files.Where(file => file.TargetRelativePath is not null && file.Role is MmcPackageFileRole.Game or MmcPackageFileRole.InstanceExtra)
            .ToDictionary(file => file.TargetRelativePath!, StringComparer.OrdinalIgnoreCase);
        var expected = baseline.Baseline.Files.Where(file => file.TargetRelativePath is not null && file.Role is PCLLF.MMCC.Core.MmcPackageFileRole.Game or PCLLF.MMCC.Core.MmcPackageFileRole.InstanceExtra)
            .OrderBy(file => file.TargetRelativePath, StringComparer.Ordinal).ToArray();
        if (expected.Length > 100000 || expected.Length != observations.Count || expected.Length != prepared.InitialGameMutations.Length)
            throw new IOException("Private payload inventory is incomplete or exceeds its count budget.");
        var mutations = prepared.InitialGameMutations.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var files = ImmutableArray.CreateBuilder<MmcPrivatePayloadFile>(); long total = 0;
        progress?.Report(new("private-payload-plan") { CompletedFiles = 0, TotalFiles = expected.Length });
        foreach (var member in expected) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!observations.TryGetValue(member.TargetRelativePath!, out var file) || !mutations.TryGetValue(member.TargetRelativePath!, out var mutation) ||
                file.ArchiveRelativePath != member.RelativePath || file.Role.ToString() != member.Role.ToString() || file.Sha256 != member.Sha256 ||
                file.SizeBytes != member.SizeBytes || file.Crc32 != member.Crc32 || file.Sha1 != member.Sha1 || mutation.ExpectedOldSha256 is not null || mutation.NewContent is not null ||
                mutation.ExpectedNewSha256 != member.Sha256 || mutation.NewSourceRelativePath is null || file.StagedPath is null)
                throw new IOException("Private payload mutation differs from its measured author baseline.");
            string relative = member.TargetRelativePath!;
            if (relative.Equals(InstanceName + ".json", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) || relative.Equals(".pclignore", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A package payload cannot occupy a structural/library/import marker target.");
            // Only compare the owned staging identity here; VerifyPayloadFile checks its entire
            // live ancestor chain before reading it. A changed source can therefore be rejected
            // after the manifest is written, with that manifest and workspace retained below.
            string input = paths.ResolvePrivatePayloadPath(mutation.NewSourceRelativePath);
            if (!string.Equals(input, file.StagedPath, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(input) != Path.Combine(workspace, "files") ||
                !Guid.TryParseExact(Path.GetFileName(input), "N", out Guid sourceId) || sourceId == Guid.Empty)
                throw new IOException("Private payload input is not a file owned by its prepared GUID workspace.");
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (MmcInstancePaths.Attributes(target) is not null) throw new IOException("Private payload target already exists; it was preserved.");
            if (!MmcDurableFiles.IsHash(file.Sha256) || file.SizeBytes < 0 || file.SizeBytes > MmcDurableFiles.MaximumFileBytes ||
                (total = checked(total + file.SizeBytes)) > MaximumPayloadBytes) throw new IOException("Private payload byte budget is invalid.");
            files.Add(new(member.RelativePath, relative, file.Role, file.Sha256, file.SizeBytes, file.Crc32));
            progress?.Report(new("private-payload-plan") { CompletedFiles = files.Count, TotalFiles = expected.Length });
        }
        var manifest = new MmcPrivatePayloadManifest(1, InstanceId, packageSha256, baseline.BaselineSha256, TemporaryRoot, FinalRoot,
            paths.GameRootLeaf, Path.GetRelativePath(TemporaryRoot, workspace).Replace('\\', '/'), total, files.ToImmutable());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (bytes.Length > 64 * 1024 * 1024) throw new IOException("Private payload manifest exceeds its metadata budget.");
        string manifestPath = paths.ResolveRelative(MmcPathRole.Instance, PayloadManifestPath);
        // Persist the complete expected inventory before the first move. This record describes
        // prepared facts, not a claim that an interrupted partial placement is complete.
        progress?.Report(new("private-payload-manifest", 0, bytes.Length) { CompletedFiles = 0, TotalFiles = manifest.Files.Length });
        MmcDurableFiles.WriteNew(manifestPath, bytes);
        payloadManifest = manifest; payloadManifestHash = MmcDurableFiles.Hash(bytes);
        cleanupFiles[PayloadManifestPath] = payloadManifestHash;
        foreach (var file in manifest.Files) cleanupFiles[file.TargetRelativePath] = file.Sha256;
        foreach (var file in prepared.Files.Where(file => file.StagedPath is not null))
            cleanupFiles[Path.GetRelativePath(TemporaryRoot, file.StagedPath!).Replace('\\', '/')] = file.Sha256;
        cleanupFiles[Path.GetRelativePath(TemporaryRoot, Path.Combine(workspace, "owner.txt")).Replace('\\', '/')] =
            MmcDurableFiles.Hash(Encoding.UTF8.GetBytes("PCLLF.MmcArtifactWorkspace/v1\n" + Path.GetFileName(workspace) + "\n" + TemporaryRoot));
        try {
            checkpoint?.Invoke(MmcInitialPayloadCheckpoint.Prepared, -1);
            long completed = 0; byte[] buffer = new byte[131072];
            progress?.Report(new("private-payload-install", 0, total) { CompletedFiles = 0, TotalFiles = manifest.Files.Length });
            for (int index = 0; index < manifest.Files.Length; index++) {
                cancellationToken.ThrowIfCancellationRequested();
                var file = manifest.Files[index]; var mutation = mutations[file.TargetRelativePath];
                // Pure path computation must not repeat the ancestor walks at the actual I/O
                // boundaries below. Neither result authorizes a read, directory creation or move.
                string input = paths.ResolvePrivatePayloadPath(mutation.NewSourceRelativePath!);
                string target = paths.ResolvePrivatePayloadPath(file.TargetRelativePath);
                checkpoint?.Invoke(MmcInitialPayloadCheckpoint.BeforeMove, index);
                cancellationToken.ThrowIfCancellationRequested();
                VerifyPayloadFile(input, file, cancellationToken, buffer);
                if (MmcInstancePaths.Attributes(target) is not null) throw new IOException("Private payload target was claimed; existing bytes were preserved.");
                MmcArtifactIo.Directory(Path.GetDirectoryName(target)!);
                File.Move(input, target, false);
                completed = checked(completed + file.SizeBytes);
                progress?.Report(new("private-payload-install", completed, total) { CompletedFiles = index + 1, TotalFiles = manifest.Files.Length });
                checkpoint?.Invoke(MmcInitialPayloadCheckpoint.Moved, index);
            }
            cancellationToken.ThrowIfCancellationRequested(); VerifyMarker(TemporaryRoot); VerifyPayloadWorkspace(paths, workspace); payloadPlaced = true;
        } catch { prepared.RetainForImportRecovery(); throw; }
    }

    private void VerifyInitialPayload(MmcInstancePaths paths, MmcInstanceDefinition definition, IProgress<MmcInstanceBackendProgress>? progress = null) {
        // This runs after the single structural CommitDecided marker. Cancellation may no longer
        // turn a completed decision into an aborted import: finish verification/publication, or
        // report a genuine recovery error while preserving the pending private root.
        if (!payloadPlaced || payloadManifest is not { } manifest || payloadManifestHash is null || manifest.SchemaVersion != 1 ||
            manifest.InstanceId != InstanceId || manifest.PackageSha256 != packageSha256 || manifest.TemporaryRoot != TemporaryRoot ||
            manifest.FinalRoot != FinalRoot || manifest.GameRootLeaf != paths.GameRootLeaf || definition.AuthorFileBaseline?.ContentSha256 != manifest.BaselineSha256)
            throw new IOException("Initial private payload placement has no complete reservation-bound preparation record.");
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, PayloadManifestPath, 64 * 1024 * 1024);
        if (MmcDurableFiles.Hash(bytes) != payloadManifestHash)
            throw new IOException("Private payload preparation record changed before publication.");
        if (MmcDurableFiles.Hash(MmcSavedSourceStore.ReadBounded(paths, definition.AuthorFileBaseline.ObjectRelativePath, 64 * 1024 * 1024)) != manifest.BaselineSha256)
            throw new IOException("Private payload author baseline changed before publication.");
        long completed = 0; int checkedFiles = 0; byte[] buffer = new byte[131072];
        foreach (var file in manifest.Files) {
            VerifyPayloadFile(paths.ResolveRelative(MmcPathRole.Instance, file.TargetRelativePath), file, CancellationToken.None, buffer);
            completed = checked(completed + file.SizeBytes);
            progress?.Report(new("private-payload-verify", completed, manifest.TotalBytes) { CompletedFiles = ++checkedFiles, TotalFiles = manifest.Files.Length });
        }
        if (completed != manifest.TotalBytes) throw new IOException("Private payload final byte count differs from its prepared inventory.");
    }
    private static string VerifyPayloadWorkspace(MmcInstancePaths paths, string stageRoot) {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageRoot));
        string id = Path.GetFileName(full);
        if (!Guid.TryParseExact(id, "N", out Guid value) || value == Guid.Empty || Path.GetDirectoryName(full) != paths.ResolveRelative(MmcPathRole.Runtime, ".staging"))
            throw new IOException("Private payload staging boundary is not an owned instance GUID workspace.");
        MmcInstancePaths.EnsureNoLinks(full);
        string marker = Path.Combine(full, "owner.txt"); MmcInstancePaths.EnsureNoLinks(marker);
        if (new FileInfo(marker).Length > 4096 || File.ReadAllText(marker) != "PCLLF.MmcArtifactWorkspace/v1\n" + id + "\n" + paths.InstanceRoot)
            throw new IOException("Private payload staging ownership marker changed.");
        return full;
    }
    private static void VerifyPayloadFile(string path, MmcPrivatePayloadFile file, CancellationToken token, byte[] buffer) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        if (input.Length != file.SizeBytes) throw new IOException("Private payload length differs from its measured author bytes.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long size = 0; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            token.ThrowIfCancellationRequested(); size = checked(size + count);
            if (size > file.SizeBytes) throw new IOException("Private payload grew beyond its measured length.");
            hash.AppendData(buffer, 0, count);
        }
        if (size != file.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
            throw new IOException("Private payload bytes changed after their initial CRC/hash observation.");
    }
}
