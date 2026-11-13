using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ModpackExportPipeline<TInstance, TLocal, TProject, TVersion>
    where TInstance : class where TLocal : LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    public void Online(ModpackExportRequest<TInstance> request, ModpackExportOnlineTask<TLocal> task) {
        task.SetOutput(new Dictionary<TLocal, List<string>>());
        if (!request.CheckHostedAssets) { host.InfoLog("要求跳过联网获取步骤"); return; }
        if (!task.Input().Any()) { host.InfoLog("没有需要联网检查的文件，跳过联网获取步骤"); return; }
        int ended = 0; List<Exception> errors = new();
        host.StartThread(() => {
            try {
                var hashes = task.Input().Select(m => m.ModrinthHash);
                JObject raw = (JObject)host.Request("https://api.modrinth.com/v2/version_files", HttpMethod.Post,
                    "{\"hashes\": [\"" + string.Join("\",\"", hashes) + "\"], \"algorithm\": \"sha1\"}", "application/json");
                foreach (TLocal mod in task.Input()) {
                    if (!raw.ContainsKey(mod.ModrinthHash)) continue;
                    if ((string?)raw[mod.ModrinthHash]?["files"]?[0]!["hashes"]?["sha1"] != mod.ModrinthHash) continue;
                    AddUrl(task.Output(), mod, (string)raw[mod.ModrinthHash]!["files"]![0]!["url"]!);
                }
                host.InfoLog($"从 Modrinth 获取到 {raw.Count} 个本地资源项的对应信息");
            } catch (Exception ex) { host.Warn(ex, "从 Modrinth 获取本地 Mod 信息失败"); errors.Add(ex); }
            finally { ended += 1; task.SetProgress(task.Progress() + 0.45); }
        }, "Modrinth - " + request.LoaderName);
        host.StartThread(() => {
            try {
                if (request.ModrinthUploadMode) return;
                var hashes = task.Input().Select(m => m.CurseForgeHash);
                JContainer raw = (JContainer)host.Request("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post,
                    "{\"fingerprints\": [" + string.Join(",", hashes) + "]}", "application/json")["data"]!["exactMatches"]!;
                foreach (JObject result in raw) {
                    if (!result.ContainsKey("file")) continue;
                    JObject file = (JObject)result["file"]!;
                    if (string.IsNullOrEmpty((string?)file["downloadUrl"])) continue;
                    TLocal? mod = task.Input().FirstOrDefault(m => m.CurseForgeHash == file["fileFingerprint"]!.ToObject<uint>());
                    if (mod is null) continue;
                    foreach (string url in ResourceVersionRules.ParseCurseForgeDownloadUrls(file["downloadUrl"]!.ToString())) AddUrl(task.Output(), mod, url);
                }
                host.InfoLog($"从 CurseForge 获取到 {raw.Count} 个本地资源项的对应信息");
            } catch (Exception ex) { host.Warn(ex, "从 CurseForge 获取本地 Mod 信息失败"); errors.Add(ex); }
            finally { ended += 1; task.SetProgress(task.Progress() + 0.45); }
        }, "CurseForge - " + request.LoaderName);
        while (ended != 2) { if (task.IsCanceled()) return; Thread.Sleep(10); }
        if (errors.Count == 1) {
            if (host.Confirm("联网获取部分文件信息失败，是否继续导出？\r\n\r\n若继续，无法获取信息的文件将被直接打包。\r\n由于二次分发可能违反使用协议，请尽量不要公开发布导出的整合包！", "部分文件信息获取失败", "继续", "取消") == 2) throw errors.First();
        } else if (errors.Count > 1) {
            if (host.Confirm("联网获取文件信息失败，是否继续导出？\r\n\r\n若继续，所有文件都将被直接打包。\r\n由于二次分发可能违反使用协议，请尽量不要公开发布导出的整合包！", "文件信息获取失败", "继续", "取消") == 2) throw errors.First();
        }
    }
    private static void AddUrl(Dictionary<TLocal, List<string>> output, TLocal file, string url) {
        output.AddIntoValueCollection(file, url);
    }
    public void Archive(ModpackExportRequest<TInstance> request, ModpackExportArchiveTask<TLocal> task) {
        JArray files = new();
        foreach (var pair in task.Input()) {
            TLocal mod = pair.Key;
            files.Add(new JObject {
                ["path"] = PathUtils.RemoveExtendedPrefix(mod.File.FullName).AfterFirst(request.OverridesFolder).Replace("\\", "/"),
                ["hashes"] = new JObject { ["sha1"] = mod.ModrinthHash, ["sha512"] = CryptographyUtils.ComputeFileHash(mod.File.FullName, CryptographyUtils.HashMethod.Sha512) },
                ["downloads"] = new JArray(pair.Value.OrderBy(url => url.Contains("modrinth.com"))),
                ["fileSize"] = mod.File.Length
            });
            // Copies can inherit readonly attributes from the game files. Only
            // the staged copy is removed after its hosted metadata is recorded.
            FileUtils.Delete(mod.File.FullName);
        }
        task.SetProgress(0.2);
        JObject dependencies = new() { ["minecraft"] = host.Version(request.Instance).VanillaName };
        if (host.Version(request.Instance).ModLoaders.HasFlagF(ModLoaders.Forge)) dependencies.Add("forge", host.Version(request.Instance).ModLoaderVersions[ModLoaders.Forge]);
        if (host.Version(request.Instance).ModLoaders.HasFlagF(ModLoaders.Fabric)) dependencies.Add("fabric-loader", host.Version(request.Instance).ModLoaderVersions[ModLoaders.Fabric]);
        if (host.Version(request.Instance).ModLoaders.HasFlagF(ModLoaders.NeoForge)) dependencies.Add("neoforge", host.Version(request.Instance).ModLoaderVersions[ModLoaders.NeoForge]);
        JObject result = new() { ["game"] = "minecraft", ["formatVersion"] = 1, ["versionId"] = request.Version, ["name"] = request.Name, ["summary"] = host.Info(request.Instance), ["files"] = files, ["dependencies"] = dependencies };
        FileUtils.Write(request.CacheFolder + @"modpack\modrinth.index.json", result.ToString(Formatting.Indented));
        if (request.IncludeLauncher) {
            FileUtils.CreateZipFromDirectory(request.CacheFolder + "modpack.mrpack", request.CacheFolder + @"modpack\"); task.SetProgress(0.5);
            DirectoryUtils.Delete(request.CacheFolder + @"modpack\"); task.SetProgress(0.6);
            FileUtils.CreateZipFromDirectory(request.PackPath, request.CacheFolder); task.SetProgress(0.9);
        } else { FileUtils.CreateZipFromDirectory(request.PackPath, request.CacheFolder + @"modpack\"); task.SetProgress(0.8); }
        host.Explorer(request.PackPath);
    }
}
