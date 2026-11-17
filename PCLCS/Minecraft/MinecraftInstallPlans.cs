using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class MinecraftInstallEngine<TLoader, TNetFile, TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TLib, TResource, TRequest> {
    public List<TLoader> McInstallLoadersGet(TRequest request) {
        MmcInstanceCapabilities.RequireLegacy(request.InstanceFolder, "普通组件安装");
        string temp = host.RequestTempFolder(request.Components.OptiFineEntry is not null || request.Components.ModLoaderVersions.ContainsKey(ModLoaders.Forge) || request.Components.ModLoaderVersions.ContainsKey(ModLoaders.NeoForge));
        TOptiFine? optiFine = request.Components.OptiFineEntry?.ToObject<TOptiFine>();
        string? optiFolder = optiFine is null ? null : temp + "versions\\" + optiFine.InstanceName;
        string? forgeVersion = request.Components.ModLoaderVersions.TryGetValue(ModLoaders.Forge, out string? forgeValue) ? forgeValue : null;
        string? forgeFolder = forgeVersion is null ? null : temp + "versions\\forge-" + forgeVersion;
        string? neoVersion = request.Components.ModLoaderVersions.TryGetValue(ModLoaders.NeoForge, out string? neoValue) ? neoValue : null;
        string? neoFolder = neoVersion is null ? null : temp + "versions\\neoforge-" + neoVersion;
        string? liteVersion = request.Components.ModLoaderVersions.TryGetValue(ModLoaders.LiteLoader, out string? liteValue) ? liteValue : null;
        string? liteFolder = liteVersion is null ? null : temp + "versions\\" + request.Components.MinecraftVersion + "-LiteLoader";
        string? fabricVersion = request.Components.ModLoaderVersions.TryGetValue(ModLoaders.Fabric, out string? fabricValue) ? fabricValue : null;
        string? fabricFolder = fabricVersion is null ? null : temp + "versions\\fabric-loader-" + fabricVersion + "-" + request.Components.MinecraftVersion;
        bool modable = request.Components.ModLoaderVersions.Any();
        string modsTemp = temp + "mods\\";
        bool optiAsMod = optiFine is not null && modable;
        if (optiAsMod) { Logger.Info("OptiFine 将作为 Mod 进行下载"); optiFolder = liteVersion is not null ? modsTemp + request.Components.MinecraftVersion + "\\" : modsTemp; }
        if (optiFolder is not null) Logger.Info("OptiFine 缓存：" + optiFolder);
        if (forgeFolder is not null) Logger.Info("Forge 缓存：" + forgeFolder);
        if (neoFolder is not null) Logger.Info("NeoForge 缓存：" + neoFolder);
        if (fabricFolder is not null) Logger.Info("Fabric 缓存：" + fabricFolder);
        if (liteFolder is not null) Logger.Info("LiteLoader 缓存：" + liteFolder);
        Logger.Info("对应的原版版本：" + request.Components.MinecraftVersion);
        string versionFolder = PathUtils.AddSlashSuffix(request.InstanceFolder);
        if (FileUtils.Exists(versionFolder + request.NewInstanceName + ".json")) { Hint("版本 " + request.NewInstanceName + " 已经存在！", 1); throw new OperationCanceledException(); }
        List<TLoader> loaders = new();
        Dictionary<string, string> managedSources = new();
        loaders.Add(Task<int, int>("添加忽略标识", _ => FileUtils.Write(versionFolder + ".pclignore", "用于临时地在 PCL 的版本列表中屏蔽此版本。"), show: false));
        if (request.Components.FabricApiVersion is not null) {
            TNetFile file = host.ResourceNetFile(request.FabricApi!, modsTemp, request.Components.MinecraftVersion, ModLoaders.Fabric);
            managedSources.Add("FabricApi", host.LocalPath(file)); loaders.Add(Download("下载 Fabric API", new() { file }, 3, true, false));
        }
        if (request.Components.OptiFabricVersion is not null) {
            TNetFile file = host.ResourceNetFile(request.OptiFabric!, modsTemp, request.Components.MinecraftVersion, ModLoaders.Fabric);
            managedSources.Add("OptiFabric", host.LocalPath(file)); loaders.Add(Download("下载 OptiFabric", new() { file }, 3, true, false));
        }
        TLoader client = Combo<string>("下载原版 " + request.Components.MinecraftVersion, McDownloadClientLoader(request.Components.MinecraftVersion, request.MinecraftJson, versionFolder), 39, false, !modable && optiFine is null);
        loaders.Add(client);
        if (optiFine is not null) {
            if (optiAsMod) managedSources.Add("OptiFine", optiFolder + optiFine.FileName);
            loaders.Add(Combo<string>("下载 OptiFine " + optiFine.DisplayName, optiAsMod ? McDownloadOptiFineSaveLoader(optiFine, optiFolder + optiFine.FileName) : McDownloadOptiFineLoader(optiFine, temp, client, versionFolder), modable ? 16 : 24, false, !modable));
        }
        if (forgeVersion is not null) loaders.Add(Combo<string>("下载 Forge " + forgeVersion, McDownloadForgelikeLoader(false, forgeVersion, "forge-" + forgeVersion, request.Components.MinecraftVersion, request.ForgeEntry, temp, client, versionFolder), 25, false, fabricVersion is null && request.LiteLoaderEntry is null && request.NeoForgeEntry is null));
        if (neoVersion is not null) loaders.Add(Combo<string>("下载 NeoForge " + neoVersion, McDownloadForgelikeLoader(true, neoVersion, "neoforge-" + neoVersion, request.Components.MinecraftVersion, request.NeoForgeEntry, temp, client, versionFolder), 25, false, request.ForgeEntry is null && fabricVersion is null && request.LiteLoaderEntry is null));
        if (liteVersion is not null) loaders.Add(Combo<string>("下载 LiteLoader " + request.Components.MinecraftVersion, McDownloadLiteLoaderLoader(request.LiteLoaderEntry!, temp), 1, false, fabricVersion is null));
        if (fabricVersion is not null) loaders.Add(Combo<string>("下载 Fabric " + fabricVersion, McDownloadFabricLoader(fabricVersion, request.Components.MinecraftVersion, temp), 2, false, true));
        string minecraftFolder = request.MinecraftFolder ?? host.MinecraftFolder();
        string? targetFolder = null;
        loaders.Add(Task<string, string>("安装游戏", task => {
            MergeJson(versionFolder, optiFolder, optiAsMod, forgeFolder, neoFolder, fabricFolder, liteFolder); task.Progress = 0.2;
            DirectoryUtils.Copy(temp + "libraries", minecraftFolder + "libraries"); task.Progress = 0.8;
            targetFolder = request.IsIntermediateOutput ? versionFolder : host.NewInstance(versionFolder).PathIndie;
            string mods = targetFolder + "mods\\";
            if (DirectoryUtils.Exists(modsTemp)) DirectoryUtils.Copy(modsTemp, mods);
            else if (modable) { DirectoryUtils.Create(mods); Logger.Info("自动创建 Mod 文件夹：" + mods); }
            string packs = targetFolder + "resourcepacks\\"; DirectoryUtils.Create(packs); Logger.Info("自动创建资源包文件夹：" + packs);
        }, 2, block: true));
        List<TLoader> libraries = new() {
            Task<string, List<TNetFile>>("分析游戏支持库文件（副加载器）", task => task.Output = host.LibraryFiles(host.NewInstance(versionFolder)), 1, false),
            Download("下载游戏支持库文件（副加载器）", new(), 7, false)
        };
        loaders.Add(Combo<string>("下载游戏支持库文件", libraries, 8));
        loaders.Add(Task<int, int>("记录安装清单", _ => {
            List<McInstallManifest.ManagedFile> managed = new();
            foreach (KeyValuePair<string, string> entry in managedSources) {
                string relative = entry.Value.Substring(modsTemp.Length);
                string fullPath = PathUtils.ForCompare(Path.Combine(targetFolder!, "mods", relative));
                managed.Add(new McInstallManifest.ManagedFile("mods/" + relative.Replace("\\", "/").ToLowerInvariant(), CryptographyUtils.ComputeFileHash(fullPath, CryptographyUtils.HashMethod.Sha1), FileUtils.GetInfo(fullPath).LastWriteTimeUtc, entry.Key));
            }
            Dictionary<ModLoaders, string> versions = new(request.Components.ModLoaderVersions);
            if (forgeVersion is not null && forgeVersion.StartsWithF("1.") && forgeVersion.Contains("-")) versions[ModLoaders.Forge] = forgeVersion.AfterLast("-");
            if (neoVersion is not null && neoVersion.StartsWithF("1.20.1-")) versions[ModLoaders.NeoForge] = neoVersion.Replace("1.20.1-", "");
            McComponents target = new(request.Components.MinecraftVersion, versions, request.Components.OptiFineEntry, request.Components.FabricApiVersion, request.Components.OptiFabricVersion);
            IInstanceConfigProvider config = host.NewInstance(versionFolder).Config;
            Configs.McInstallManifest.Set(new McInstallManifest(Versions.McInstallManifestVersion, target, DateTime.UtcNow, managed), config); config.Save();
        }, show: false));
        if (!request.IsIntermediateOutput) loaders.Add(Task<int, int>("删除忽略标识", _ => FileUtils.Delete(versionFolder + ".pclignore"), show: false));
        return loaders;
    }
    public void MergeJson(string versionFolder, string? optiFolder, bool optiAsMod, string? forgeFolder, string? neoFolder, string? fabricFolder, string? liteFolder) {
        List<(string Name, string Folder)> sources = new() { ("Minecraft", versionFolder) };
        if (optiFolder is not null && !optiAsMod) sources.Add(("OptiFine", optiFolder));
        if (forgeFolder is not null) sources.Add(("Forge", forgeFolder));
        if (neoFolder is not null) sources.Add(("NeoForge", neoFolder));
        if (liteFolder is not null) sources.Add(("LiteLoader", liteFolder));
        if (fabricFolder is not null) sources.Add(("Fabric", fabricFolder));
        Logger.Info("开始进行版本合并，输出：" + versionFolder + "，" + string.Join("，", sources.Select(source => source.Name + "：" + source.Folder)));
        List<(string Name, JObject Json)> profiles = new();
        foreach (var source in sources) {
            string path = Path.Combine(source.Folder, PathUtils.GetLastPart(source.Folder) + ".json");
            string text = FileUtils.TryReadAsString(path) ?? "";
            if (!text.StartsWithF("{")) throw new Exception(source.Name + " json 有误，地址：" + path + "，前段内容：" + text.Substring(0, Math.Min(text.Length, 1000)));
            profiles.Add((source.Name, text.DeserializeJson<JObject>()!));
        }
        string arguments = string.Join(" ", profiles.Select(profile => profile.Json["minecraftArguments"]?.ToString() ?? " "));
        JObject merged = profiles[0].Json;
        foreach (var profile in profiles.Skip(1)) { profile.Json.Remove("releaseTime"); profile.Json.Remove("time"); merged.Merge(profile.Json); }
        List<string> split = new();
        foreach (string argument in arguments.Split(" ", true).Select(value => value.Trim())) {
            if (!argument.StartsWithF("-") && split.Any()) {
                string last = split.Last();
                if (last.StartsWithF("-") && !last.Contains(" ")) { split[split.Count - 1] = last + " " + argument; continue; }
            }
            split.Add(argument);
        }
        if (split.Any()) merged["minecraftArguments"] = string.Join(" ", split.Distinct());
        merged.Remove("_comment_"); merged.Remove("inheritsFrom"); merged.Remove("jar");
        string name = PathUtils.GetLastPart(versionFolder); merged["id"] = name;
        FileUtils.Write(Path.Combine(versionFolder, name + ".json"), merged.ToString()); Logger.Info("版本合并 " + name + " 完成");
    }
}
