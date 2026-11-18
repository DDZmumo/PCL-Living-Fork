using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcLegacyAdoptionSnapshot {
    internal const int MaximumEntries = 100_000;
    internal const long MaximumBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index => {
        uint value = (uint)index; for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ value >> 1; return value;
    }).ToArray();
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static string Canonical(string root) => MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(root));
    internal static string GameRoot(string root, string gameRoot) {
        gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)); MmcInstancePaths.EnsureNoLinks(gameRoot);
        bool flat = string.Equals(root, gameRoot, Comparison);
        if (!flat && !new[] { "minecraft", ".minecraft" }.Any(leaf => string.Equals(Path.Combine(root, leaf), gameRoot, Comparison)))
            throw new IOException("只支持旧实例内明确的独立游戏目录，不能迁移全局共享存档。");
        if (!Directory.Exists(root) || !Directory.Exists(gameRoot)) throw new IOException("旧实例或游戏目录不存在。");
        foreach (string leaf in new[] { "minecraft", ".minecraft" }) if (!string.Equals(Path.Combine(root, leaf), gameRoot, Comparison) && MmcInstancePaths.Attributes(Path.Combine(root, leaf)) is not null)
            throw new IOException("旧实例含有额外 minecraft/.minecraft 游戏树，首期不合并目录，请先确认游戏目录角色。");
        if (!flat && new[] { "mods", "config", "saves", "screenshots", "resourcepacks", "shaderpacks", "options.txt", "servers.dat" }.Any(name => MmcInstancePaths.Attributes(Path.Combine(root, name)) is not null))
            throw new IOException("旧实例同时存在根游戏数据与嵌套游戏目录，不能猜测或合并。");
        return flat ? root : Path.Combine(root, new[] { "minecraft", ".minecraft" }.Single(leaf => string.Equals(Path.Combine(root, leaf), gameRoot, Comparison)));
    }
    internal static void RequireLegacy(string root) {
        var management = MmcManagementProbe.Read(root);
        if (management.State != MmcManagementState.Legacy || !management.ObservationComplete)
            throw new IOException("只能主动转换未受 MMC 管理且没有待恢复记录的旧实例。");
        if (MmcInstancePaths.Attributes(Path.Combine(root, ".pclignore")) is not null) throw new IOException("旧实例仍有未完成安装标记，不能转换。");
    }
    internal static MmcLegacyAdoptionInventory Capture(string root, string gameRoot, CancellationToken token,
        IProgress<MmcLegacyAdoptionProgress>? progress = null) {
        root = Canonical(root); gameRoot = GameRoot(root, gameRoot); RequireLegacy(root);
        List<MmcLegacyAdoptionFile> files = []; Stack<string> directories = new(); directories.Push(root);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase); long total = 0;
        string name = Path.GetFileName(root), gameRelative = string.Equals(root, gameRoot, Comparison) ? "" : Path.GetRelativePath(root, gameRoot).Replace('\\', '/') + "/";
        while (directories.Count != 0) {
            string directory = directories.Pop(); MmcInstancePaths.EnsureNoLinks(directory);
            string[] entries = Directory.EnumerateFileSystemEntries(directory).Take(MaximumEntries - files.Count + 1).ToArray();
            foreach (string path in entries.Order(StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(path);
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/'); MmcArtifactIo.Relative(relative);
                if (relative.Equals(MmcLegacyAdoptionCapabilities.PrivateBackupMarker, StringComparison.OrdinalIgnoreCase)) throw new IOException("旧目录包含私有恢复格式标记，不能将恢复包内容冒充旧运行实例。");
                if (relative.Split('/').Length > 128 || !names.Add(relative) || files.Count >= MaximumEntries) throw new IOException("旧实例目录数量、深度或大小写冲突超过边界。");
                var role = Role(relative, name, gameRelative, out string? gamePath); bool isDirectory = Directory.Exists(path);
                if (isDirectory) { files.Add(new(relative, true, 0, null, null, Directory.GetLastWriteTimeUtc(path).Ticks, role, gamePath)); directories.Push(path); continue; }
                long modified = File.GetLastWriteTimeUtc(path).Ticks; long expectedLength = new FileInfo(path).Length;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var measured = Measure(input, null, MaximumFileBytes, token, expectedLength);
                if (File.GetLastWriteTimeUtc(path).Ticks != modified) throw new IOException("旧实例文件在快照期间变化。");
                total = checked(total + measured.SizeBytes); if (total > MaximumBytes) throw new IOException("旧实例总大小超过转换预算。");
                files.Add(new(relative, false, measured.SizeBytes, measured.Sha256, measured.Sha1, modified, role, gamePath, measured.Crc32));
                progress?.Report(new("snapshot-legacy-files", total));
            }
        }
        var ordered = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        string? HashOf(string relative) => ordered.SingleOrDefault(file => string.Equals(file.RelativePath, relative, Comparison))?.Sha256;
        string fingerprint = MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, SourceRoot = root, GameRoot = gameRoot, Files = ordered }));
        return new(root, gameRoot, name, ordered, fingerprint, HashOf(name + ".json"), HashOf("PCL/config.json"), HashOf("PCL/Setup.ini"));
    }
    internal static void Verify(MmcLegacyAdoptionInventory expected, CancellationToken token, IProgress<MmcLegacyAdoptionProgress>? progress = null) {
        var actual = Capture(expected.SourceRoot, expected.GameRoot, token, progress);
        if (actual.Fingerprint != expected.Fingerprint) throw new IOException("旧实例在预览后发生变化；未发布副本，请重新预览。");
    }
    internal static string ReadText(string root, string relative, bool optional = false) {
        MmcArtifactIo.Relative(relative); string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); MmcInstancePaths.EnsureNoLinks(path);
        if (optional && MmcInstancePaths.Attributes(path) is null) return "";
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16 * 1024 * 1024) throw new IOException("旧配置超过读取预算。");
        using MemoryStream memory = new(); byte[] buffer = new byte[81920]; int count;
        while ((count = input.Read(buffer)) != 0) { if (memory.Length + count > 16 * 1024 * 1024) throw new IOException("旧配置读取期间超过预算。"); memory.Write(buffer, 0, count); }
        return new UTF8Encoding(false, true).GetString(memory.ToArray());
    }
    internal sealed record Measurement(string Sha256, string Sha1, long SizeBytes, uint Crc32);
    internal static Measurement Measure(Stream input, Stream? output, long maximum, CancellationToken token, long? expectedSize = null,
        string? expectedSha256 = null, string? expectedSha1 = null, uint? expectedCrc32 = null, Action<long>? copied = null) {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[131072]; long total = 0; uint crc = uint.MaxValue; int count;
        while ((count = input.Read(buffer)) != 0) {
            token.ThrowIfCancellationRequested(); total = checked(total + count);
            if (total > maximum || expectedSize is not null && total > expectedSize) throw new IOException("转换文件超过已观测大小或预算。");
            sha256.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
            for (int index = 0; index < count; index++) crc = CrcTable[(crc ^ buffer[index]) & 255] ^ crc >> 8;
            output?.Write(buffer, 0, count); copied?.Invoke(total);
        }
        var result = new Measurement(Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()), total, ~crc);
        if (expectedSize is not null && result.SizeBytes != expectedSize || expectedSha256 is not null && result.Sha256 != expectedSha256 ||
            expectedSha1 is not null && result.Sha1 != expectedSha1 || expectedCrc32 is not null && result.Crc32 != expectedCrc32)
            throw new IOException("转换文件的实际字节、摘要或 CRC 与固定快照不一致。");
        return result;
    }
    private static MmcLegacyAdoptionFileRole Role(string relative, string name, string gameRelative, out string? gamePath) {
        gamePath = null;
        if (relative.Equals("PCL/Logo.png", StringComparison.OrdinalIgnoreCase)) return MmcLegacyAdoptionFileRole.InstanceExtra;
        if (relative.Equals("PCL", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals(name + ".json", StringComparison.OrdinalIgnoreCase) || relative.Equals(name + ".jar", StringComparison.OrdinalIgnoreCase)) return MmcLegacyAdoptionFileRole.HostBackupOnly;
        if (relative is "mmc-pack.json" or "instance.cfg" or "patches" || relative.StartsWith("patches/", StringComparison.OrdinalIgnoreCase)) return MmcLegacyAdoptionFileRole.SourceEvidence;
        if (gameRelative.Length != 0 && (relative.Equals("libraries", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))) return MmcLegacyAdoptionFileRole.LocalLibrary;
        bool game = gameRelative.Length == 0 || relative.StartsWith(gameRelative, Comparison) || relative + "/" == gameRelative;
        if (!game) return MmcLegacyAdoptionFileRole.InstanceExtra;
        gamePath = gameRelative.Length == 0 ? relative : relative.Length >= gameRelative.Length ? relative[gameRelative.Length..] : "";
        if (gamePath.Equals("logs", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
            gamePath.Equals("crash-reports", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase) ||
            new[] { "accounts.json", "accounts.cfg", "launcher_accounts.json", "launcher_profiles.json", "multimc.cfg", "prismlauncher.cfg" }.Contains(gamePath, StringComparer.OrdinalIgnoreCase)) {
            gamePath = null; return MmcLegacyAdoptionFileRole.HostBackupOnly;
        }
        return MmcLegacyAdoptionFileRole.Game;
    }
}
