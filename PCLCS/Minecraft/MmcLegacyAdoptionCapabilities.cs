namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public static class MmcLegacyAdoptionCapabilities {
    internal const string PrivateBackupMarker = ".pcllf-legacy-private-backup.json";
    /// <summary>Called after bounded archive preflight. Private recovery snapshots are never original package authority.</summary>
    public static void RefusePrivateRecoveryArchive(IEnumerable<string> archiveNames) {
        int count = 0;
        foreach (string raw in archiveNames) {
            if (++count > MmcLegacyAdoptionSnapshot.MaximumEntries + 1) throw new IOException("整合包目录超过来源验证预算。");
            string name = raw.Replace('\\', '/').TrimEnd('/');
            if (name.Equals(PrivateBackupMarker, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/" + PrivateBackupMarker, StringComparison.OrdinalIgnoreCase))
                throw new IOException("这是包含旧实例私有数据的恢复备份，不能作为 MMC 作者整合包来源。请通过对应转换操作恢复，选择真正原始 MMC ZIP 创建副本。");
        }
    }
    /// <summary>Ordinary folders without an adoption store retain their old trust semantics.</summary>
    public static void RequireLegacyGameFolderSafe(string gameRoot, string operation) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        string[] stores = Path.GetFileName(root).Equals("PCL", StringComparison.OrdinalIgnoreCase)
            ? [Path.Combine(root, "MmcLegacyAdoption"), Path.Combine(root, "PCL", "MmcLegacyAdoption")]
            : [Path.Combine(root, "PCL", "MmcLegacyAdoption")];
        foreach (string store in stores) {
            if (MmcInstancePaths.Attributes(store) is null) continue;
            MmcInstancePaths.EnsureNoLinks(store);
            if (!Directory.Exists(store) || Directory.EnumerateFileSystemEntries(store).Any())
                throw new InvalidOperationException(operation + "不可通过旧目录清理入口处理：该游戏文件夹含有旧 MMC 转换的私有备份或待恢复操作，请保留这些恢复数据。");
        }
    }
}
