using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed record MmcLegacyAdoptionRecoveryEntry(string OperationDirectory, string Name, string Description, bool CanRecover, string? IntentSha256 = null);
public sealed record MmcLegacyAdoptionRecoveryCatalog(ImmutableArray<MmcLegacyAdoptionRecoveryEntry> Entries, bool Truncated);
public static class MmcLegacyAdoptionCatalog {
    public static MmcLegacyAdoptionRecoveryCatalog Read(string minecraftRoot) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftRoot)), store = Path.Combine(root, "PCL", "MmcLegacyAdoption");
        if (MmcInstancePaths.Attributes(store) is null) return new([], false);
        List<MmcLegacyAdoptionRecoveryEntry> entries = [];
        try {
            MmcInstancePaths.EnsureNoLinks(store);
            string[] paths = Directory.EnumerateFileSystemEntries(store).Take(257).ToArray();
            foreach (string path in paths.Take(256).Order(StringComparer.Ordinal)) {
                string name = "无法确认的转换记录";
                try {
                    if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)) throw new IOException("不是受支持的操作目录。");
                    var operation = MmcLegacyAdoptionJournal.Read(path); name = operation.Intent.TargetName;
                    if (Path.GetDirectoryName(operation.DirectoryPath) != store) throw new IOException("操作目录不属于当前游戏文件夹。");
                    if (operation.RolledBack()) { entries.Add(new(path, name, "未发布的副本已自动回退；旧实例及私有备份保留。", false, operation.IntentHash)); continue; }
                    if (!operation.HasDecision()) { entries.Add(new(path, name, "尚未决定发布，旧实例保留。私有备份/半成品仅供恢复查看；请重新预览创建副本。", false, operation.IntentHash)); continue; }
                    using var driver = operation.AcquireDriver(create: false);
                    bool ownPrivate = operation.Owns(operation.PrivateRoot), ownFinal = operation.Owns(operation.FinalRoot);
                    if (ownPrivate && ownFinal) throw new IOException("副本同时存在于两个位置，已保留所有内容。");
                    if (ownPrivate || ownFinal) {
                        string current = ownPrivate ? operation.PrivateRoot : operation.FinalRoot; _ = operation.RequirePrimary(current);
                        entries.Add(new(path, name, "副本发布或收尾尚未完成，可重放已有决定；不覆盖同名内容。", true, operation.IntentHash));
                    } else if (operation.IsCompleted()) {
                        _ = operation.RequirePrimary(operation.FinalRoot);
                        if (MmcInstancePaths.Attributes(Path.Combine(operation.FinalRoot, MmcLegacyAdoptionJournal.MarkerRelative.Replace('/', Path.DirectorySeparatorChar))) is not null) throw new IOException("当前位置存在其它操作标记。");
                        entries.Add(new(path, name, "管理副本已创建，旧实例和私有备份仍保留。", false, operation.IntentHash));
                    } else throw new IOException("未找到与决定一致的副本归属，所有内容已保留。");
                } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or OverflowException) {
                    entries.Add(new(path, name, "记录正被使用、发生冲突或无法验证，不能自动恢复：" + error.Message, false));
                }
            }
            return new(entries.ToImmutableArray(), paths.Length > 256);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return new([new(store, "无法读取转换恢复记录", error.Message, false)], false);
        }
    }
}
