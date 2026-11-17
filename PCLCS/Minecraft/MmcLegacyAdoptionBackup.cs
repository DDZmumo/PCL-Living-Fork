using System.IO.Compression;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcLegacyAdoptionBackupSeal(int SchemaVersion, string InventorySha256, string BackupSha256, long BackupBytes);
/// <summary>Private recovery data only. Old PCL/account/log bytes never enter a package source bundle or a shareable export.</summary>
internal static class MmcLegacyAdoptionBackup {
    internal static MmcLegacyAdoptionBackupSeal Create(string operationDirectory, MmcLegacyAdoptionInventory inventory,
        CancellationToken token, IProgress<MmcLegacyAdoptionProgress>? progress) {
        MmcInstancePaths.EnsureNoLinks(operationDirectory);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(inventory);
        if (manifest.LongLength > 64 * 1024 * 1024) throw new IOException("旧实例快照清单超过预算。");
        MmcDurableFiles.WriteNew(Path.Combine(operationDirectory, "snapshot.json"), manifest);
        string path = Path.Combine(operationDirectory, "backup.zip"); long copied = 0, total = inventory.Files.Sum(file => file.SizeBytes);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 131072)) {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8)) {
                byte[] marker = PrivateMarker(MmcDurableFiles.Hash(manifest));
                using (var header = zip.CreateEntry(MmcLegacyAdoptionCapabilities.PrivateBackupMarker, CompressionLevel.NoCompression).Open()) header.Write(marker);
                foreach (var file in inventory.Files) {
                token.ThrowIfCancellationRequested(); string name = file.RelativePath + (file.IsDirectory ? "/" : "");
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                var stamp = new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc);
                entry.LastWriteTime = stamp.Year is >= 1980 and <= 2107 ? new DateTimeOffset(stamp) : new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (file.IsDirectory) continue;
                string source = Path.Combine(inventory.SourceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)); MmcInstancePaths.EnsureNoLinks(source);
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var target = entry.Open();
                _ = MmcLegacyAdoptionSnapshot.Measure(input, target, MmcLegacyAdoptionSnapshot.MaximumFileBytes, token, file.SizeBytes, file.Sha256, file.Sha1, file.Crc32);
                copied = checked(copied + file.SizeBytes); progress?.Report(new("backup-legacy-snapshot", copied, total));
                }
            }
            output.Flush(true); // One sealed recovery object; no fsync for every tiny source file.
        }
        using var sourceZip = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var measured = MmcLegacyAdoptionSnapshot.Measure(sourceZip, null, MmcLegacyAdoptionSnapshot.MaximumBytes + 256 * 1024 * 1024, token);
        var seal = new MmcLegacyAdoptionBackupSeal(1, MmcDurableFiles.Hash(manifest), measured.Sha256, measured.SizeBytes);
        MmcDurableFiles.WriteRecord(Path.Combine(operationDirectory, "backup.record"), seal);
        return seal;
    }
    internal static MmcLegacyAdoptionInventory ReadInventory(string directory, MmcLegacyAdoptionBackupSeal seal) {
        MmcInstancePaths.EnsureNoLinks(directory);
        string path = Path.Combine(directory, "snapshot.json"); MmcInstancePaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024) throw new IOException("旧快照清单超过预算。");
        using MemoryStream memory = new(); input.CopyTo(memory);
        byte[] bytes = memory.ToArray(); if (MmcDurableFiles.Hash(bytes) != seal.InventorySha256) throw new IOException("旧快照清单摘要变化。");
        var inventory = JsonSerializer.Deserialize<MmcLegacyAdoptionInventory>(bytes) ?? throw new IOException("旧快照清单缺失。");
        if (inventory.SchemaVersion != 1 || inventory.Files.IsDefault || inventory.Files.Length > MmcLegacyAdoptionSnapshot.MaximumEntries ||
            inventory.Files.Any(file => file is null || !Enum.IsDefined(file.Role) || file.SizeBytes < 0 || file.SizeBytes > MmcLegacyAdoptionSnapshot.MaximumFileBytes ||
                (file.IsDirectory ? file.SizeBytes != 0 || file.Sha256 is not null || file.Sha1 is not null : !MmcDurableFiles.IsHash(file.Sha256) || file.Sha1?.Length != 40)) ||
            inventory.Files.Sum(file => file.SizeBytes) > MmcLegacyAdoptionSnapshot.MaximumBytes)
            throw new IOException("旧快照清单预算或结构无效。");
        return inventory;
    }
    internal static void CopyPersonalFiles(string directory, MmcLegacyAdoptionBackupSeal seal, MmcLegacyAdoptionInventory inventory,
        MmcInstancePaths target, IReadOnlyDictionary<string, string> derivedGameFiles, IReadOnlyDictionary<string, string> originalLocalLibraries,
        CancellationToken token, IProgress<MmcLegacyAdoptionProgress>? progress, Action<string, string>? producedFile = null, Action<string>? producedDirectory = null) {
        string backup = Path.Combine(directory, "backup.zip"); MmcInstancePaths.EnsureNoLinks(backup);
        using var input = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zipHash = MmcLegacyAdoptionSnapshot.Measure(input, null, seal.BackupBytes, token, seal.BackupBytes, seal.BackupSha256); input.Position = 0;
        _ = zipHash;
        int count = MmcArtifactMaterializer.PreflightArchive(input, MmcLegacyAdoptionSnapshot.MaximumEntries + 1,
            MmcLegacyAdoptionSnapshot.MaximumBytes, Encoding.UTF8, allowZip64EntrySizes: true, maximumEntryBytes: MmcLegacyAdoptionSnapshot.MaximumFileBytes);
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true, Encoding.UTF8);
        if (count != inventory.Files.Length + 1 || archive.Entries.Count != count) throw new IOException("恢复备份目录与固定库存不一致。");
        var entries = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        byte[] expectedMarker = PrivateMarker(seal.InventorySha256);
        if (!entries.TryGetValue(MmcLegacyAdoptionCapabilities.PrivateBackupMarker, out var privateMarker)) throw new IOException("恢复备份缺少私有格式标记。");
        using (var marker = privateMarker.Open()) _ = MmcLegacyAdoptionSnapshot.Measure(marker, null, 1024, token, expectedMarker.Length, MmcDurableFiles.Hash(expectedMarker));
        long copied = 0;
        foreach (var file in inventory.Files) {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.RelativePath + (file.IsDirectory ? "/" : ""), out var entry) || entry.Length != file.SizeBytes || !file.IsDirectory && entry.Crc32 != file.Crc32)
                throw new IOException("恢复备份成员与固定库存不一致。");
            if (file.Role is MmcLegacyAdoptionFileRole.SourceEvidence or MmcLegacyAdoptionFileRole.HostBackupOnly) continue;
            string relative = TargetRelative(file, target);
            if (file.Role == MmcLegacyAdoptionFileRole.Game && string.IsNullOrEmpty(file.GameRelativePath)) continue;
            string destination = target.ResolveRelative(MmcPathRole.Instance, relative);
            if (file.IsDirectory) { MmcArtifactIo.Directory(destination); producedDirectory?.Invoke(relative); continue; }
            string? existing = MmcDurableFiles.Observe(destination);
            if (derivedGameFiles.TryGetValue(relative, out string? derived)) {
                if (file.Sha256 != derived || existing != derived) throw new IOException("旧个人资源与新受锁派生资源冲突；原实例及备份已保留。");
                continue;
            }
            if (originalLocalLibraries.TryGetValue(relative, out string? original)) {
                if (file.Sha256 != original || existing != original) throw new IOException("旧本地库与原包锁定运行库冲突。");
                continue;
            }
            if (existing is not null) throw new IOException("个人快照目标被其它内容占用，不能覆盖。");
            MmcArtifactIo.Directory(Path.GetDirectoryName(destination)!);
            using var member = entry.Open();
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 131072)) {
                _ = MmcLegacyAdoptionSnapshot.Measure(member, output, MmcLegacyAdoptionSnapshot.MaximumFileBytes, token, file.SizeBytes, file.Sha256, file.Sha1, file.Crc32);
                output.Flush(); // Durable backup.zip is the recovery authority for this unpublished personal payload.
            }
            File.SetLastWriteTimeUtc(destination, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
            producedFile?.Invoke(relative, file.Sha256!);
            copied = checked(copied + file.SizeBytes); progress?.Report(new("copy-personal-snapshot", copied));
        }
        VerifyPersonalFiles(inventory, target, derivedGameFiles, token);
    }
    internal static void VerifyPersonalFiles(MmcLegacyAdoptionInventory inventory, MmcInstancePaths target, IReadOnlyDictionary<string, string> derivedGameFiles, CancellationToken token) {
        Dictionary<string, string> expectedGame = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in inventory.Files.Where(file => file.IsDirectory && file.Role is MmcLegacyAdoptionFileRole.Game or MmcLegacyAdoptionFileRole.InstanceExtra or MmcLegacyAdoptionFileRole.LocalLibrary)) {
            string relative = TargetRelative(file, target);
            if (!System.IO.Directory.Exists(target.ResolveRelative(MmcPathRole.Instance, relative))) throw new IOException("个人快照目录缺失。");
        }
        foreach (var file in inventory.Files.Where(file => !file.IsDirectory && file.Role is MmcLegacyAdoptionFileRole.Game or MmcLegacyAdoptionFileRole.InstanceExtra or MmcLegacyAdoptionFileRole.LocalLibrary)) {
            string relative = TargetRelative(file, target);
            string path = target.ResolveRelative(MmcPathRole.Instance, relative);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = MmcLegacyAdoptionSnapshot.Measure(input, null, MmcLegacyAdoptionSnapshot.MaximumFileBytes, token, file.SizeBytes, file.Sha256, file.Sha1, file.Crc32);
            if (file.Role == MmcLegacyAdoptionFileRole.Game) expectedGame.Add(relative, file.Sha256!);
        }
        foreach (var derived in derivedGameFiles) {
            if (MmcDurableFiles.Observe(target.ResolveRelative(MmcPathRole.Instance, derived.Key)) != derived.Value) throw new IOException("受锁派生资源在发布前变化。");
            expectedGame[derived.Key] = derived.Value;
        }
        foreach (string file in Files(target.GameRoot, token)) {
            string relative = Path.GetRelativePath(target.InstanceRoot, file).Replace('\\', '/');
            if (!expectedGame.TryGetValue(relative, out string? hash) || MmcDurableFiles.Observe(file) != hash)
                throw new IOException("管理副本游戏目录出现快照外内容或发生变化，不能发布。");
        }
    }
    internal static IEnumerable<string> Files(string root, CancellationToken token) {
        Stack<string> pending = new(); pending.Push(root); int count = 0;
        while (pending.Count != 0) {
            string directory = pending.Pop(); MmcInstancePaths.EnsureNoLinks(directory);
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(path);
                if (++count > MmcLegacyAdoptionSnapshot.MaximumEntries) throw new IOException("副本目录超过库存预算。");
                if (System.IO.Directory.Exists(path)) pending.Push(path); else yield return path;
            }
        }
    }
    private static string TargetRelative(MmcLegacyAdoptionFile file, MmcInstancePaths target) => file.Role == MmcLegacyAdoptionFileRole.Game ?
        string.IsNullOrEmpty(file.GameRelativePath) ? target.GameRootLeaf : target.GameRootLeaf + "/" + file.GameRelativePath : file.RelativePath;
    private static byte[] PrivateMarker(string inventorySha256) => JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Kind = "PCLLF.LegacyPersonalRecovery", InventorySha256 = inventorySha256 });
}
