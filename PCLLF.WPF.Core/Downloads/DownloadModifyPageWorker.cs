using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class DownloadModifyPageController<TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TLoader, TLoading, TAnimation> {
    public void LoadInstance(DownloadModifyTask<TInstance> task) {
        try { task.SetOutput(host.DetectPack(task.Input().PathVersion)); }
        catch (Exception error) { Logger.Warn(error, "读取整合包信息失败"); task.SetOutput(new JObject(new JProperty("error", true))); }
        host.UiWait(() => { animation.Native.Stop("FrmDownloadInstall SelectPageSwitch"); Selection.IsSelectNameEdited = false; host.BaseClearSelected(); State.PendingComponents.Clear(); State.InitialTarget = null; Selection.IsInSelectPage = false; host.View().AllBack.IsHitTestVisible = false; });
        task.Input().Config.DiscardCache();
        McInstallManifest? manifest = Configs.McInstallManifest.Get(task.Input().Config);
        if (manifest is not null && manifest.SchemaVersion > Versions.McInstallManifestVersion) {
            Logger.Warn($"安装清单由更新版本的 PCL 创建（清单版本：{manifest.SchemaVersion}，当前版本：{Versions.McInstallManifestVersion}）");
            throw new Exception("更新 PCL 后才能修改它！点击【设置 → 其他 → 检查更新】以更新。");
        }
        if (manifest is not null) { McInstallManifest latest = manifest.RefreshModComponents(task.Input().PathIndie); if (!ReferenceEquals(latest, manifest)) { manifest = latest; Configs.McInstallManifest.Set(manifest, task.Input().Config); task.Input().Config.Save(); } }
        McComponents components; string? optiFineVersion = null;
        if (manifest is not null) { components = manifest.Components; if (components is null) throw new Exception("安装清单缺少组件信息，无法还原已安装组件。"); State.IsVanillaVersionReliable = true; }
        else {
            McVersion version = host.NewInstance(task.Input().PathVersion).Version;
            if (Same(version.VanillaName, "Unknown")) throw new Exception("无法确定 Minecraft 版本，无法修改");
            List<string> unknown = version.ModLoaderVersions.Where(loader => string.IsNullOrWhiteSpace(loader.Value) || Same(loader.Value, "未知版本")).Select(loader => loader.Key.ToString()).ToList();
            if (unknown.Any()) throw new Exception($"无法确定 {string.Join("、", unknown)} 的版本，无法修改");
            components = new McComponents(version.VanillaName, version.ModLoaderVersions); optiFineVersion = version.OptiFine; State.IsVanillaVersionReliable = version.Reliable;
        }
        string minecraft = components.MinecraftVersion; if (string.IsNullOrWhiteSpace(minecraft)) throw new Exception("无法确定当前 Minecraft 版本，请检查版本文件");
        Dictionary<string, TLoader> loaders = host.CatalogLoaders(); TLoader? forgeLoader = null;
        if (McVersion.IsStandardFormat(minecraft)) { forgeLoader = host.CreateForgeLoader(minecraft); loaders.Add("Forge", forgeLoader); }
        foreach (TLoader loader in loaders.Values) host.StartLoader(loader, ReferenceEquals(loader, forgeLoader) ? minecraft : null, task.ForceRestart());
        while (true) {
            if (task.Canceled()) return;
            foreach (var pair in loaders) {
                TLoader loader = pair.Value;
                if (host.LoaderState(loader) == host.States.Failed) { if (ReferenceEquals(loader, forgeLoader) && Same(host.LoaderError(loader).Message, "无")) continue; throw new Exception($"获取 {pair.Key} 版本列表失败，请稍后再试，或使用 VPN 改善网络环境", host.LoaderError(loader)); }
                if (host.LoaderState(loader) == host.States.Canceled) throw new Exception($"获取 {pair.Key} 版本列表被中断，请稍后再试，或使用 VPN 改善网络环境");
            }
            if (loaders.Values.All(loader => host.LoaderState(loader) == host.States.Finished || host.LoaderState(loader) == host.States.Failed)) break;
            task.Progress(.7 * loaders.Values.Average(host.LoaderProgress)); Thread.Sleep(10);
        }
        if (task.Canceled()) return; task.Progress(.7);
        Selection.MinecraftVersions = (JArray)host.ClientOutput()["versions"]!.DeepClone();
        Selection.VanillaData = Selection.MinecraftVersions.OfType<JObject>().FirstOrDefault(version => Same(version["id"]!.ToString().Replace("∞", "infinite"), minecraft.Replace("∞", "infinite")));
        if (Selection.VanillaData is null) throw new Exception($"Minecraft 列表中没有已安装的 {minecraft}"); Selection.VanillaData["id"] = minecraft;
        if (manifest is not null) {
            Selection.SelectedOptiFine = components.OptiFineEntry?.ToObject<TOptiFine>();
            if (Selection.SelectedOptiFine is not null && !host.OptiFineOutput().Any(version => Same(version.FileName, Selection.SelectedOptiFine.FileName))) { if (!host.OptiFineMirrorFinished()) host.WaitOptiFineMirror(); host.CopyOptiFineMirror(); }
        } else if (optiFineVersion is not null && !Same(optiFineVersion, "未知版本")) {
            Selection.SelectedOptiFine = host.OptiFineOutput().FirstOrDefault(version => Same(version.Inherit, minecraft) && Same(version.InstanceName.AfterLast("-OptiFine_").Replace("HD_U_", ""), optiFineVersion.Replace("HD_U_", "")));
            if (Selection.SelectedOptiFine is null) throw new Exception($"OptiFine 列表中没有已安装的 {optiFineVersion}");
        }
        if (components.ModLoaderVersions.ContainsKey(ModLoaders.Forge)) {
            Selection.SelectedForge = forgeLoader is null ? null : host.ForgeOutput(forgeLoader)?.FirstOrDefault(version => Same(version.Inherit, minecraft) && Same(version.VersionName, components.ModLoaderVersions[ModLoaders.Forge]) && !Same(version.Category, "universal") && !Same(version.Category, "client") && (Selection.SelectedOptiFine is null || OptiFineSuits(Selection.SelectedOptiFine, version)));
            if (Selection.SelectedForge is null) throw new Exception($"Forge 列表中没有已安装的 {components.ModLoaderVersions[ModLoaders.Forge]}");
        }
        if (components.ModLoaderVersions.ContainsKey(ModLoaders.NeoForge)) { Selection.SelectedNeoForge = host.NeoForgeOutput().FirstOrDefault(version => Same(version.Inherit, minecraft) && Same(version.VersionName, components.ModLoaderVersions[ModLoaders.NeoForge])); if (Selection.SelectedNeoForge is null) throw new Exception($"NeoForge 列表中没有已安装的 {components.ModLoaderVersions[ModLoaders.NeoForge]}"); }
        if (components.ModLoaderVersions.ContainsKey(ModLoaders.Fabric)) {
            IEnumerable<JObject> versions = host.FabricOutput()["loader"]!.OfType<JObject>();
            JObject? fabric = versions.FirstOrDefault(version => Same(version["version"]!.ToString(), components.ModLoaderVersions[ModLoaders.Fabric])) ?? versions.FirstOrDefault(version => Same(version["version"]!.ToString().Replace("+build", ""), components.ModLoaderVersions[ModLoaders.Fabric]));
            if (fabric is null || !host.FabricOutput()["game"]!.OfType<JObject>().Any(version => Same(version["version"]!.ToString(), minecraft.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3")))) throw new Exception($"Fabric 列表中没有已安装的 {components.ModLoaderVersions[ModLoaders.Fabric]}");
            Selection.SelectedFabric = fabric["version"]!.ToString();
        }
        if (components.ModLoaderVersions.ContainsKey(ModLoaders.LiteLoader)) { Selection.SelectedLiteLoader = host.LiteLoaderOutput().FirstOrDefault(version => Same(version.Inherit, minecraft) && Same(version.JsonToken["version"]!.ToString(), components.ModLoaderVersions[ModLoaders.LiteLoader])); if (Selection.SelectedLiteLoader is null) throw new Exception($"LiteLoader 列表中没有已安装的 {components.ModLoaderVersions[ModLoaders.LiteLoader]}"); }
        if (components.FabricApiVersion is not null) { Selection.SelectedFabricApi = host.FabricApiOutput().FirstOrDefault(version => (Same(version.Version, components.FabricApiVersion) || Same(version.Id, components.FabricApiVersion) || Same(version.Display, components.FabricApiVersion)) && ApiSuits(version, minecraft)); if (Selection.SelectedFabricApi is null) throw new Exception($"Fabric API 列表中没有已安装的 {components.FabricApiVersion}"); }
        if (components.OptiFabricVersion is not null) { Selection.SelectedOptiFabric = host.OptiFabricOutput().FirstOrDefault(version => (Same(version.Version, components.OptiFabricVersion) || Same(version.Id, components.OptiFabricVersion) || Same(version.Display, components.OptiFabricVersion)) && OptiFabricSuits(version, minecraft)); if (Selection.SelectedOptiFabric is null) throw new Exception($"OptiFabric 列表中没有已安装的 {components.OptiFabricVersion}"); }
        List<McInstallManifest.ManagedFile> managed = new(); if (manifest is not null) managed.AddRange(manifest.ManagedFiles);
        Lazy<List<(FileInfo File, string Sha1)>> mods = new(() => {
            string folder = task.Input().PathIndie + "mods\\"; List<string> files = DirectoryUtils.EnumerateFiles(folder, searchPattern: "*.jar").ToList();
            if (Selection.SelectedLiteLoader is not null) files.AddRange(DirectoryUtils.EnumerateFiles(Path.Combine(folder, minecraft), searchPattern: "*.jar"));
            List<(FileInfo File, string Sha1)> entries = new(); foreach (string path in files) { if (task.Canceled()) return entries; FileInfo file = FileUtils.GetInfo(path); entries.Add((file, host.LocalSha1(file))); task.Progress(.7 + .28 * entries.Count / files.Count); } return entries;
        });
        if (Selection.SelectedFabric is not null && Selection.SelectedFabricApi is null) {
            List<TResource> versions = host.FabricApiOutput().Where(version => ApiSuits(version, minecraft)).ToList(); List<(FileInfo File, string Sha1, TResource Version)> matches = new();
            foreach (var entry in mods.Value) { TResource? match = versions.FirstOrDefault(version => Same(version.Hash, entry.Sha1)); if (match is not null) matches.Add((entry.File, entry.Sha1, match)); }
            var selected = SelectMod("Fabric API", matches); if (selected is not null) { Selection.SelectedFabricApi = selected.Value.Version; managed.Add(new McInstallManifest.ManagedFile("mods/" + selected.Value.File.Name.Lower(), selected.Value.Sha1, selected.Value.File.LastWriteTimeUtc, "FabricApi")); }
        }
        if (task.Canceled()) return;
        if (Selection.SelectedFabric is not null && Selection.SelectedOptiFabric is null) {
            List<TResource> versions = host.OptiFabricOutput().Where(version => OptiFabricSuits(version, minecraft)).ToList(); bool hasMd5 = versions.Any(version => (version.Hash ?? "").Length == 32); List<(FileInfo File, string Sha1, TResource Version)> matches = new();
            foreach (var entry in mods.Value) { TResource? match = versions.FirstOrDefault(version => Same(version.Hash, entry.Sha1)); if (match is null && hasMd5) { string md5 = CryptographyUtils.ComputeFileHash(entry.File.FullName, CryptographyUtils.HashMethod.Md5); match = versions.FirstOrDefault(version => Same(version.Hash, md5)); } if (match is not null) matches.Add((entry.File, entry.Sha1, match)); }
            var selected = SelectMod("OptiFabric", matches); if (selected is not null) { Selection.SelectedOptiFabric = selected.Value.Version; managed.Add(new McInstallManifest.ManagedFile("mods/" + selected.Value.File.Name.Lower(), selected.Value.Sha1, selected.Value.File.LastWriteTimeUtc, "OptiFabric")); }
        }
        if (task.Canceled()) return;
        if (Selection.SelectedOptiFine is null && (Selection.SelectedForge is not null || Selection.SelectedLiteLoader is not null || Selection.SelectedFabric is not null)) {
            JObject hashes = host.OptiFineHashes(); var files = mods.Value.Select(entry => (entry.File, entry.Sha1, OriginalName: hashes[entry.Sha1]?.ToString())).Where(entry => entry.OriginalName is not null).ToList();
            List<(FileInfo File, string Sha1, TOptiFine Version)> matches = new();
            foreach (var entry in files) { TOptiFine? match = host.OptiFineOutput().FirstOrDefault(version => Same(version.Inherit, minecraft) && Same(version.FileName, entry.OriginalName) && (Selection.SelectedForge is null || OptiFineSuits(version, Selection.SelectedForge))); if (match is null) { host.Hint($"无法从 OptiFine 列表还原 {entry.File.Name} 对应的 {entry.OriginalName}，将跳过该文件。", SelectionPageHint.Blue); continue; } matches.Add((entry.File, entry.Sha1, match)); }
            var selected = SelectMod("OptiFine", matches); if (selected is not null) { Selection.SelectedOptiFine = selected.Value.Version; string relative = PathUtils.RemoveExtendedPrefix(selected.Value.File.FullName).Substring(task.Input().PathIndie.Length).Replace("\\", "/").Lower(); managed.Add(new McInstallManifest.ManagedFile(relative, selected.Value.Sha1, selected.Value.File.LastWriteTimeUtc, "OptiFine")); }
        }
        if (task.Canceled()) return;
        if (State.IsVanillaVersionReliable) {
            components = new McComponents(minecraft, components.ModLoaderVersions, Selection.SelectedOptiFine is null ? null : JObject.FromObject(Selection.SelectedOptiFine), Selection.SelectedFabricApi?.Version ?? Selection.SelectedFabricApi?.Id ?? Selection.SelectedFabricApi?.Display, Selection.SelectedOptiFabric?.Version ?? Selection.SelectedOptiFabric?.Id ?? Selection.SelectedOptiFabric?.Display);
            if (manifest is null || !components.IsEquivalentTo(manifest.Components) || !managed.SequenceEqual(manifest.ManagedFiles)) { Configs.McInstallManifest.Set(new McInstallManifest(Versions.McInstallManifestVersion, components, DateTime.UtcNow, managed), task.Input().Config); task.Input().Config.Save(); Logger.Info($"已为 {task.Input().Name} 更新安装清单"); }
        }
        if (task.Canceled()) return; State.ForgeVersionLoader = forgeLoader;
    }
    public (FileInfo File, string Sha1, TVersion Version)? SelectMod<TVersion>(string componentName, List<(FileInfo File, string Sha1, TVersion Version)> matches) {
        if (matches.Count == 0) return null; if (matches.Count == 1) return matches[0]; int? choice = null;
        host.UiWait(() => choice = host.ChooseMod(matches.Select(entry => entry.File.Name), $"选择当前使用的 {componentName}"));
        if (!choice.HasValue) throw new Exception($"发现了多个 {componentName}"); return matches[choice.Value];
    }
}
