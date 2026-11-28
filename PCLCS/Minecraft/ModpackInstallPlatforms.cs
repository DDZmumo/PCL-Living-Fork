using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ModpackInstallEngine<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine> {
    public TLoader InstallPackCurseForge(string fileAddress, ZipArchive archive, string archiveBaseFolder, string? instanceName = null, string? logo = null, JObject? source = null) {
        JObject json;
        try { json = (JObject)archive.GetEntry(archiveBaseFolder + "manifest.json")!.Open().ReadString().DeserializeJson()!; }
        catch (Exception error) { throw new Exception("CurseForge 整合包安装信息存在问题", error); }
        if (json["minecraft"] is null || json["minecraft"]!["version"] is null) throw new Exception("CurseForge 整合包未提供 Minecraft 版本信息");
        if (instanceName is null) instanceName = SelectName((string?)(json["name"] ?? new JValue("")));
        string versionFolder = PathUtils.AddSlashSuffix(Path.Combine(host.MinecraftFolder(), "versions", instanceName!) + "\\");
        McVersion targetVersion = new(json["minecraft"]!["version"]!.ToString());
        foreach (JToken entry in Values(json["minecraft"]!["modLoaders"])) {
            string id = (entry["id"] ?? new JValue("")).ToString().Lower();
            if (id.StartsWithF("forge-")) {
                if (id.Contains("recommended")) throw new Exception("该整合包版本过老，已不支持进行安装！");
                Logger.Info("整合包 Forge 版本：" + id); targetVersion.ModLoaderVersions[ModLoaders.Forge] = id.Replace("forge-", "");
            } else if (id.StartsWithF("neoforge-")) {
                Logger.Info("整合包 NeoForge 版本：" + id); targetVersion.ModLoaderVersions[ModLoaders.NeoForge] = id.Replace("neoforge-", "");
            } else if (id.StartsWithF("fabric-")) {
                Logger.Info("整合包 Fabric 版本：" + id); targetVersion.ModLoaderVersions[ModLoaders.Fabric] = id.Replace("fabric-", "");
            } else NotifyIncompatibleLoader(id);
        }
        string installTemp = host.RequestTempFolder();
        List<TLoader> installLoaders = new();
        string? overrideHome = (string?)(json["overrides"] ?? new JValue(""));
        if (!Same(overrideHome, "")) {
            string overridesFolder = installTemp + archiveBaseFolder + (overrideHome == "." || overrideHome == "./" ? "" : overrideHome);
            if (!PathUtils.IsParentOf(installTemp, overridesFolder)) throw new Exception("整合包的 overrides 路径超出了解压目录，请向整合包作者反馈此问题！");
            installLoaders.Add(host.TextIntTask("解压整合包文件", task => {
                ExtractModpackFiles(installTemp, fileAddress, task.Cancellation(), task.ProgressProvider(0.6));
                CopyOverrideDirectory(overridesFolder, versionFolder, null!);
            }, new MinecraftDownloadLoaderSettings { ProgressWeight = ExtractWeight(fileAddress), Block = false }));
        }
        List<int> modList = new(), modOptionalList = new();
        foreach (JToken modEntry in Values(json["files"])) {
            if (modEntry["projectID"] is null || modEntry["fileID"] is null) { host.Hint("某项 Mod 缺少必要信息，已跳过：" + modEntry, false); continue; }
            modList.Add((int)modEntry["fileID"]!);
            if (modEntry["required"] is not null && !modEntry["required"]!.ToObject<bool>()) modOptionalList.Add((int)modEntry["fileID"]!);
        }
        if (modList.Any()) {
            List<TLoader> modDownloadLoaders = new();
            modDownloadLoaders.Add(host.IntJsonTask("获取 Mod 下载信息", task => {
                JArray output = (JArray)host.ModRequest("https://api.curseforge.com/v1/mods/files", HttpMethod.Post, "{\"fileIds\": [" + string.Join(",", modList) + "]}", "application/json")!["data"]!;
                task.Output(output);
                if (modList.Count == output.Count) return;
                List<int> missingIds = modList.Except(output.Select(value => value["id"]!.ToObject<int>())).ToList();
                Logger.Info("缺失的 fileIds：" + string.Join("、", missingIds));
                List<(int ProjectId, int FileId)> missings = missingIds.Select(id => (((JObject)json["files"]!.AsEnumerable().First(value => value["fileID"]!.ToObject<int>() == id))["projectID"]!.ToObject<int>(), id)).ToList();
                Logger.Info("缺失项：" + string.Join("、", missings));
                List<TProject> projects = new();
                try { projects = host.CurseForgeProjects(missings.Select(entry => entry.ProjectId.ToString())).ToList(); }
                catch (Exception error) { Logger.Warn(error, "获取缺失的工程信息失败"); }
                List<string> descriptions = new();
                foreach (var missing in missings) {
                    TProject? project = projects.FirstOrDefault(value => Operators.ConditionalCompareObjectEqual(value.Id, missing.ProjectId, false));
                    descriptions.Add(project is null ? "未知 CurseForge 项目 (项目 ID " + missing.ProjectId + "，文件 ID " + missing.FileId + ")" : project.TranslatedName + " (文件 ID " + missing.FileId + ")");
                }
                if (Message("未找到该整合包所需的以下内容，它们可能已被原作者删除：\r\n- " + string.Join("\r\n- ", descriptions) + "\r\n\r\n是否继续安装？缺失这些内容可能导致整合包出现异常！",
                    "整合包内容缺失", "继续", "取消", true) == 2) task.Cancel();
            }, new MinecraftDownloadLoaderSettings { ProgressWeight = modList.Count / 10.0 }));
            modDownloadLoaders.Add(host.JsonFilesTask("构造 Mod 下载信息", task => {
                Dictionary<int, TNetFile> files = new();
                foreach (JToken modJson in task.Input()) {
                    int id = modJson["id"]!.ToObject<int>();
                    if (files.ContainsKey(id)) continue;
                    if (modOptionalList.Contains(id) && Message("是否要下载整合包中的可选文件 " + modJson["displayName"] + "？", "下载可选文件", "是", "否") == 2) continue;
                    string targetFolder; ResourceTypes type;
                    if (modJson["modules"]!.Any()) {
                        List<string> names = ((JArray)modJson["modules"]!).Select(value => value["name"]!.ToString()).ToList();
                        if (names.Contains("META-INF") || names.Contains("mcmod.info") || names.Contains("fabric.mod.json") || modJson?["FileName"]?.ToString().EndsWithF(".jar", true) == true) { targetFolder = "mods"; type = ResourceTypes.Mod; }
                        else if (names.Contains("pack.mcmeta")) { targetFolder = "resourcepacks"; type = ResourceTypes.ResourcePack; }
                        else if (names.Contains("shaders")) { targetFolder = "shaderpacks"; type = ResourceTypes.Shader; }
                        else { targetFolder = "saves"; type = ResourceTypes.Map; }
                    } else { targetFolder = "mods"; type = ResourceTypes.Mod; }
                    TVersion file = host.ParseVersion(modJson!, type);
                    if (!file.DownloadAvailable) continue;
                    files.Add(id, host.VersionToNetFile(file, PathUtils.AddSlashSuffix(Path.Combine(versionFolder, targetFolder)), targetVersion.VanillaName, targetVersion.ModLoaders));
                    task.SetProgress(task.Progress() + 1.0 / (1 + modList.Count));
                }
                task.Output(files.Values.ToList());
            }, new MinecraftDownloadLoaderSettings { ProgressWeight = modList.Count / 200.0, Show = false }));
            modDownloadLoaders.Add(host.Download("下载 Mod", new List<TNetFile>(), new MinecraftDownloadLoaderSettings { ProgressWeight = modList.Count * 1.5 }));
            installLoaders.Add(host.IntCombo("下载 Mod（主加载器）", modDownloadLoaders, new MinecraftDownloadLoaderSettings { Show = false, ProgressWeight = modDownloadLoaders.Sum(host.Weight) }));
        }
        List<TLoader> mergeLoaders = host.InstallGame(instanceName!, versionFolder, new McComponents(targetVersion.VanillaName, targetVersion.ModLoaderVersions));
        List<TLoader> loaders = new() { PackCombo(installLoaders), GameCombo(mergeLoaders) };
        loaders.Add(host.TextTextTask("最终整理文件", task => {
            SetLogo(logo, versionFolder);
            host.RecordInstalled(fileAddress, versionFolder, source);
            string saves = versionFolder + "saves\\";
            foreach (string map in DirectoryUtils.EnumerateFiles(saves, searchPattern: "*.zip")) { FileUtils.ExtractToDirectory(map, saves, task.Cancellation()); FileUtils.Delete(map); }
            DeleteOriginalPacks(fileAddress, versionFolder);
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = 0.1, Show = false }));
        return Start("CurseForge 整合包安装：" + instanceName + " ", loaders, versionFolder);
    }
    public TLoader InstallPackModrinth(string fileAddress, ZipArchive archive, string archiveBaseFolder, string? instanceName = null, string? logo = null, JObject? source = null) {
        TIsolation? isolation = host.ReadIsolation(archive, archiveBaseFolder);
        JObject json;
        try { json = (JObject)archive.GetEntry(archiveBaseFolder + "modrinth.index.json")!.Open().ReadString().DeserializeJson()!; }
        catch (Exception error) { throw new Exception("Modrinth 整合包安装信息存在问题", error); }
        if (json["dependencies"] is null || json["dependencies"]!["minecraft"] is null) throw new Exception("Modrinth 整合包未提供 Minecraft 版本信息");
        McVersion targetVersion = new("Unknown");
        foreach (JProperty entry in Values(json["dependencies"])) {
            switch (entry.Name.Lower()) {
                case "minecraft": targetVersion.VanillaName = entry.Value.ToString(); break;
                case "forge": targetVersion.ModLoaderVersions[ModLoaders.Forge] = entry.Value.ToString(); Logger.Info("整合包 Forge 版本：" + targetVersion.ModLoaderVersions[ModLoaders.Forge]); break;
                case "neoforge": case "neo-forge": targetVersion.ModLoaderVersions[ModLoaders.NeoForge] = entry.Value.ToString(); Logger.Info("整合包 NeoForge 版本：" + targetVersion.ModLoaderVersions[ModLoaders.NeoForge]); break;
                case "fabric-loader": targetVersion.ModLoaderVersions[ModLoaders.Fabric] = entry.Value.ToString(); Logger.Info("整合包 Fabric 版本：" + targetVersion.ModLoaderVersions[ModLoaders.Fabric]); break;
                default: NotifyIncompatibleLoader(entry.Name); break;
            }
        }
        if (instanceName is null) instanceName = SelectName((string?)(json["name"] ?? new JValue("")));
        string minecraftFolder = host.MinecraftFolder();
        string versions = Path.Combine(minecraftFolder, "versions");
        string versionRoot = Path.GetFullPath(Path.Combine(versions, instanceName!));
        if (!PathUtils.IsSame(Path.GetDirectoryName(versionRoot), versions))
            throw new InvalidDataException("安装目标必须是 versions 下的新实例目录。");
        string versionFolder = PathUtils.AddSlashSuffix(PathUtils.ForCompare(versionRoot));
        string installTemp = host.RequestTempFolder(), gameFolder = host.GameFolder(versionFolder, minecraftFolder, isolation);
        bool isolated = isolation?.Enabled ?? true;
        string marker = Path.Combine(versionFolder, ".pclignore");
        string Target(string relative) {
            relative = InstanceCloneExportIsolationRules.NormalizeRelative(relative);
            string target = isolation is null ? Path.Combine(gameFolder, relative) : host.OverrideTarget(relative, versionFolder, gameFolder, isolation);
            if (!PathUtils.IsParentOf(gameFolder, target) && !PathUtils.IsParentOf(versionFolder, target))
                throw new InvalidDataException("整合包的文件路径超出了游戏文件夹。");
            if (PathUtils.IsParentOf(marker, target))
                throw new InvalidDataException("整合包不能覆盖安装任务的忽略标识。");
            return PathUtils.ForCompare(target);
        }
        var overrides = new List<(string Source, string Target)>();
        var overrideFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overrideDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string layer in new[] { "overrides/", "client-overrides/" }) {
            string prefix = archiveBaseFolder + layer;
            foreach (var entry in archive.Entries) {
                string entryName = entry.FullName.Replace('\\', '/');
                if (entry.Name.Length == 0 || !entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Target(entryName[prefix.Length..]);
                overrides.Add((Path.Combine(installTemp, entryName), target));
                overrideFiles.Add(target);
                for (string? parent = Path.GetDirectoryName(target); parent is not null && overrideDirectories.Add(parent); parent = Path.GetDirectoryName(parent)) { }
            }
        }
        List<TNetFile> preparationFiles = new(), parallelFiles = new();
        var downloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JToken file in Values(json["files"])) {
            if (file["env"] is not null) {
                switch (file["env"]!["client"]!.ToString()) {
                    case "optional": if (Message("是否要下载可选文件 " + PathUtils.GetLastPart(file["path"]!.ToString()) + "？", "下载可选文件", "是", "否") == 2) continue; break;
                    case "unsupported": continue;
                }
            }
            List<string> urls = file["downloads"]!.SelectMany(value => host.CurseForgeUrls(value.ToString())).ToList();
            urls.AddRange(urls.Select(host.ModMirror).ToList());
            urls = host.TrackUrls(urls, targetVersion.VanillaName, targetVersion.ModLoaders).ToList();
            urls = urls.Distinct().ToList();
            string targetPath = Target(file["path"]!.ToString());
            string hash = file["hashes"]!["sha1"]!.ToString();
            if (!downloads.TryAdd(targetPath, hash)) {
                if (!string.Equals(downloads[targetPath], hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("整合包为同一文件声明了互相冲突的下载内容。");
                continue;
            }
            // LF intentionally verifies the hash, without using Modrinth's declared file size.
            var pending = host.NetFile(urls, targetPath, hash);
            if (isolated && CanDownloadWhileInstalling(targetPath, versionFolder, overrideFiles, overrideDirectories)) parallelFiles.Add(pending);
            else preparationFiles.Add(pending);
        }
        var game = host.PlanModrinthGame(instanceName!, versionFolder, new McComponents(targetVersion.VanillaName, targetVersion.ModLoaderVersions));
        var finish = game.FinishDirectory ?? throw new InvalidOperationException("整合包安装缺少目录发布任务。");

        // Files that can affect the instance configuration or be replaced by an override belong to the preparation chain.
        List<TLoader> prepareAndInstall = [];
        if (preparationFiles.Count > 0)
            prepareAndInstall.Add(host.Download("准备整合包文件", preparationFiles, new MinecraftDownloadLoaderSettings { ProgressWeight = preparationFiles.Count * 1.5, Block = true }));
        prepareAndInstall.Add(host.TextIntTask("应用整合包设置", task => {
            var token = task.Cancellation();
            token.ThrowIfCancellationRequested();
            OrdinaryInstallCleanupProof.AssertOwned(game.PrepareDirectory);
            MmcInstanceCapabilities.RequireLegacy(versionFolder, "普通整合包文件覆盖");
            MmcInstanceCapabilities.RequireLegacyContainingInstance(gameFolder, "普通整合包文件覆盖");
            if (overrides.Count > 0) {
                ExtractModpackFiles(installTemp, fileAddress, token, task.ProgressProvider(0.5));
                OrdinaryInstallCleanupProof.AssertOwned(game.PrepareDirectory);
                foreach (var file in overrides) {
                    token.ThrowIfCancellationRequested();
                    FileUtils.Copy(file.Source, file.Target);
                }
            }
            token.ThrowIfCancellationRequested();
            OrdinaryInstallCleanupProof.AssertOwned(game.PrepareDirectory);
            host.SaveModrinthIsolation(versionFolder, isolated, token);
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = ExtractWeight(fileAddress), Block = true }));
        prepareAndInstall.Add(GameCombo(game.GameLoaders));
        List<TLoader> installation = [];
        if (parallelFiles.Count > 0)
            installation.Add(host.Download("下载额外文件", parallelFiles, new MinecraftDownloadLoaderSettings { ProgressWeight = parallelFiles.Count * 1.5, Block = false }));
        installation.Add(host.TextCombo("准备并安装游戏", prepareAndInstall, new MinecraftDownloadLoaderSettings {
            Block = false, Show = false, ProgressWeight = prepareAndInstall.Sum(host.Weight)
        }));
        List<TLoader> loaders = [
            game.PrepareDirectory,
            host.TextCombo("整合包安装", installation, new MinecraftDownloadLoaderSettings { Block = true, Show = false, ProgressWeight = installation.Sum(host.Weight) }),
            host.TextTextTask("记录整合包文件", task => {
                task.Cancellation().ThrowIfCancellationRequested();
                OrdinaryInstallCleanupProof.AssertOwned(game.PrepareDirectory);
                host.RecordInstalled(fileAddress, versionFolder, source);
            }, new MinecraftDownloadLoaderSettings { Show = false }),
            host.TextTextTask("最终整理文件", task => {
                task.Cancellation().ThrowIfCancellationRequested();
                OrdinaryInstallCleanupProof.AssertOwned(game.PrepareDirectory);
                SetLogo(logo, versionFolder);
                task.Cancellation().ThrowIfCancellationRequested();
                DeleteOriginalPacks(fileAddress, versionFolder);
            }, new MinecraftDownloadLoaderSettings { ProgressWeight = 0.1, Show = false }),
            finish
        ];
        return Start("Modrinth 整合包安装：" + instanceName + " ", loaders, versionFolder);
    }

    private static bool CanDownloadWhileInstalling(string target, string versionFolder, HashSet<string> overrideFiles, HashSet<string> overrideDirectories) {
        // These directories have independent file payloads. Game installation only creates their containers.
        if (!target.StartsWith(versionFolder + "mods\\", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(versionFolder + "resourcepacks\\", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(versionFolder + "shaderpacks\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (overrideFiles.Contains(target) || overrideDirectories.Contains(target)) return false;
        for (string? parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent))
            if (overrideFiles.Contains(parent)) return false;
        return true;
    }
}
