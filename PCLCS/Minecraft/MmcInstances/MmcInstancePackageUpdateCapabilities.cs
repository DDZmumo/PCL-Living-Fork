namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstancePackageUpdateCapabilities {
    internal static void AssertOutsideStore(string path, string operation) {
        string? current = Path.GetFullPath(path);
        while (current is not null) {
            if (Path.GetFileName(current).Equals("MmcPackageUpdates", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(current)), "PCL", StringComparison.OrdinalIgnoreCase))
                throw new IOException("此路径属于 MMC ZIP 更新候选存储，普通入口不能执行" + operation + "；请先通过更新操作完成或清理所属候选。");
            current = Path.GetDirectoryName(current);
        }
    }
    internal static void AssertFolderHasNoCandidates(string root, string operation) {
        AssertOutsideStore(root, operation);
        string[] candidates = Path.GetFileName(root).Equals("PCL", StringComparison.OrdinalIgnoreCase)
            ? [Path.Combine(root, "MmcPackageUpdates"), Path.Combine(root, "PCL", "MmcPackageUpdates")]
            : [Path.Combine(root, "PCL", "MmcPackageUpdates")];
        foreach (string store in candidates) {
            MmcInstancePaths.EnsureNoLinks(store); var attributes = MmcInstancePaths.Attributes(store);
            if (attributes is null) continue;
            if ((attributes.Value & FileAttributes.Directory) == 0 || Directory.EnumerateFileSystemEntries(store).Any())
                throw new IOException("游戏文件夹仍含 MMC ZIP 更新候选或未知恢复记录，已停止" + operation + "；候选可能不属于当前 versions 列表中的实例。");
        }
    }
}
