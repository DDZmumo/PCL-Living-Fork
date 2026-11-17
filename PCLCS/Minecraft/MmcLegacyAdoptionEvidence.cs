using System.Collections.Immutable;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcLegacyAdoptionEvidence {
    internal static (JObject Config, string Ini, string OldComponents) ReadMetadata(MmcLegacyAdoptionInventory inventory) {
        string json = MmcLegacyAdoptionSnapshot.ReadText(inventory.SourceRoot, "PCL/config.json", optional: true);
        JObject config = json.Length == 0 ? new() : Json(json);
        string ini = MmcLegacyAdoptionSnapshot.ReadText(inventory.SourceRoot, "PCL/Setup.ini", optional: true);
        var settings = LegacyBaseOperations.ParseIni(ini);
        if (settings.TryGetValue("VersionArgumentIndieV2", out string? isolated)) {
            if (!bool.TryParse(isolated, out bool enabled) || !enabled) throw new IOException("旧实例未明确使用独立游戏目录，不能复制全局共享存档。");
        } else if (settings.TryGetValue("VersionArgumentIndie", out string? old)) {
            if (old != "1") throw new IOException("旧隔离设置不明确或使用共享目录，不能自动迁移。");
        } else if (inventory.SourceRoot == inventory.GameRoot) throw new IOException("旧平铺实例没有明确的隔离设置，请先确认游戏目录。");
        string summary = "未保留可确认的旧组件记录";
        if (config["MinecraftInstallManifest"] is JObject manifest) {
            if (manifest.Value<int?>("SchemaVersion") is not (null or 0 or 1)) throw new IOException("不支持的旧安装清单版本，请保留原实例。");
            if (manifest["Components"] is not JObject components) throw new IOException("旧安装清单缺少组件声明。");
            string mc = components.Value<string>("MinecraftVersion") ?? throw new IOException("旧组件缺少 Minecraft 版本。");
            var loaders = components["ModLoaderVersions"] as JObject ?? new();
            if (loaders.Properties().Any(pair => pair.Value.Type != JTokenType.String)) throw new IOException("旧加载器版本格式不明确。");
            summary = "Minecraft " + mc + string.Concat(loaders.Properties().Select(pair => "; " + pair.Name + " " + pair.Value));
        }
        string versionText = MmcLegacyAdoptionSnapshot.ReadText(inventory.SourceRoot, inventory.SourceName + ".json", optional: true);
        if (versionText.Length != 0) {
            var version = Json(versionText);
            if (version["mainClass"] is { Type: not (JTokenType.String or JTokenType.Null) } || version["libraries"] is { Type: not (JTokenType.Array or JTokenType.Null) })
                throw new IOException("旧启动 JSON 格式不明确，不能自动转换。");
            if (summary == "未保留可确认的旧组件记录") summary = "旧启动入口：" + (version.Value<string>("mainClass") ?? "未记录") + "；旧 JSON 仅保留备份，不作为新启动定义。";
        }
        CheckOldTransactions(inventory.SourceRoot);
        return (config, ini, summary);
    }
    internal static ImmutableArray<MmcLegacyAdoptionFile> ReadAuthorFiles(MmcArchiveSource source, CancellationToken token,
        IProgress<MmcLegacyAdoptionProgress>? progress) {
        List<MmcLegacyAdoptionFile> files = []; long total = 0;
        string game = source.Package.GameRootLeaf + "/";
        string other = source.Package.GameRootLeaf == "minecraft" ? ".minecraft/" : "minecraft/";
        if (source.Files.Any(file => file.RelativePath.StartsWith(other, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("原包包含两棵游戏文件树，转换副本首期不能猜测或合并这些角色。");
        foreach (var file in source.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested(); MmcArtifactIo.Relative(file.RelativePath);
            MmcLegacyAdoptionFileRole? role = file.RelativePath.StartsWith(game, StringComparison.OrdinalIgnoreCase) ? MmcLegacyAdoptionFileRole.Game :
                file.RelativePath.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) ? MmcLegacyAdoptionFileRole.LocalLibrary : null;
            if (role is null) continue;
            if (files.Count >= MmcLegacyAdoptionSnapshot.MaximumEntries) throw new IOException("原包文件数量超过转换预算。");
            using var input = source.OpenFile(file.RelativePath);
            var observed = MmcLegacyAdoptionSnapshot.Measure(input, null, MmcLegacyAdoptionSnapshot.MaximumFileBytes, token, file.SizeBytes, expectedCrc32: file.Crc32);
            total = checked(total + observed.SizeBytes); if (total > MmcLegacyAdoptionSnapshot.MaximumBytes) throw new IOException("原包内容超过转换预算。");
            files.Add(new(file.RelativePath, false, observed.SizeBytes, observed.Sha256, observed.Sha1, 0, role.Value,
                role == MmcLegacyAdoptionFileRole.Game ? file.RelativePath[game.Length..] : null, observed.Crc32));
            progress?.Report(new("compare-original-package", total));
        }
        return files.ToImmutableArray();
    }
    internal static MmcLegacyAdoptionSourceCheck Compare(MmcArchiveSource source, MmcResolvedSourceBundle resolved,
        MmcLegacyAdoptionInventory inventory, ImmutableArray<MmcLegacyAdoptionFile> author, string oldComponents) {
        var current = inventory.Files.Where(file => !file.IsDirectory).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var evidence = source.Metadata.Where(record => record.RelativePath is "mmc-pack.json" or "instance.cfg" || record.RelativePath.StartsWith("patches/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var evidenceByName = evidence.ToDictionary(record => record.RelativePath, StringComparer.OrdinalIgnoreCase);
        bool verified = evidence.Any(record => record.RelativePath == "mmc-pack.json") && evidence.All(record => current.TryGetValue(record.RelativePath, out var file) && file.Sha256 == record.Sha256) &&
            current.Values.Where(file => file.Role == MmcLegacyAdoptionFileRole.SourceEvidence).All(file => evidenceByName.TryGetValue(file.RelativePath, out var record) && record.Sha256 == file.Sha256);
        var personal = inventory.Files.Where(file => !file.IsDirectory && file.Role == MmcLegacyAdoptionFileRole.Game).ToDictionary(file => file.GameRelativePath!, StringComparer.OrdinalIgnoreCase);
        var original = author.Where(file => file.Role == MmcLegacyAdoptionFileRole.Game).ToDictionary(file => file.GameRelativePath!, StringComparer.OrdinalIgnoreCase);
        List<MmcLegacyAdoptionDifference> differences = [];
        foreach (string relative in personal.Keys.Union(original.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)) {
            personal.TryGetValue(relative, out var local); original.TryGetValue(relative, out var raw);
            string kind = local is null ? "个人快照中已删除" : raw is null ? "个人新增" : local.Sha256 == raw.Sha256 ? "与原包一致" : "个人修改";
            differences.Add(new(relative, kind, local?.SizeBytes ?? 0, raw?.SizeBytes ?? 0));
        }
        var authorLibraries = author.Where(file => file.Role == MmcLegacyAdoptionFileRole.LocalLibrary).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in inventory.Files.Where(file => !file.IsDirectory && file.Role == MmcLegacyAdoptionFileRole.LocalLibrary)) {
            authorLibraries.TryGetValue(file.RelativePath, out var raw);
            if (raw is not null && raw.Sha256 != file.Sha256) throw new IOException("旧实例本地启动库已被修改，首期不能把它冒充所选原包的运行库。原实例保留，请先整理组件来源。");
        }
        if (inventory.GameRoot == inventory.SourceRoot && original.Keys.Any(path => path.Equals("PCL", StringComparison.OrdinalIgnoreCase) || path.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(inventory.SourceName + ".json", StringComparison.OrdinalIgnoreCase) || path.Equals(inventory.SourceName + ".jar", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("原包游戏文件与旧平铺实例的 PCL/启动 JSON/JAR 角色冲突，不能无损猜测转换。");
        string next = string.Join("; ", resolved.Resolution.Profile!.Components.Where(component => component.Enabled).Select(component => component.Uid + " " + component.Version));
        return new(verified, verified ? "保留的 MMC 清单、本地补丁与配置和所选原包逐字节一致，已建立内容关联；不提供发行者签名认证。" :
            "旧历史来源记录缺失或与所选原包不一致。严格转换不可用；只有另行明确选择新来源重绑定，才可按所选原包创建管理副本，未验证旧历史来源或运行兼容。", oldComponents, next, differences.ToImmutableArray());
    }
    private static JObject Json(string text) {
        using var strict = MmcManagementReader.ParseJson(text);
        if (strict.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new IOException("旧配置必须是 JSON 对象。");
        return JObject.Parse(strict.RootElement.GetRawText());
    }
    private static void CheckOldTransactions(string root) {
        string backups = Path.Combine(root, "PCL", "PackUpdateBackups");
        MmcInstancePaths.EnsureNoLinks(backups);
        if (!Directory.Exists(backups)) return;
        string[] directories = Directory.EnumerateDirectories(backups).Take(1025).ToArray();
        if (directories.Length > 1024) throw new IOException("旧事务记录超过读取预算，请先整理。");
        foreach (string directory in directories) {
            MmcInstancePaths.EnsureNoLinks(directory); string record = Path.Combine(directory, "transaction.json");
            if (!File.Exists(record)) continue;
            string relative = Path.GetRelativePath(root, record).Replace('\\', '/');
            var json = Json(MmcLegacyAdoptionSnapshot.ReadText(root, relative));
            if (json.Value<bool?>("complete") != true) throw new IOException("旧实例存在未完成或无法确认的文件事务，请先恢复旧事务。");
        }
    }
}
