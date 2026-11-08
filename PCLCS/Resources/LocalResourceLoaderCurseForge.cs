using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LocalResourceLoaderEngine<TLocal, TProject, TVersion>
    where TLocal : LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    private void LoadCurseForge(List<TLocal> mods, string vanillaVersion, ModLoaders targetLoaders, LocalResourceOnlineTask<TLocal> task, Thread currentTaskThread) {
        List<uint> hashes = new();
        foreach (TLocal entry in mods) {
            hashes.Add(entry.CurseForgeHash);
            if (task.IsCanceledWithThread(currentTaskThread)) return;
        }
        JContainer raw = (JContainer)host.Request("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post,
            "{\"fingerprints\": [" + string.Join(",", hashes) + "]}", "application/json")["data"]!["exactMatches"]!;
        host.Info($"从 CurseForge 获取到 {raw.Count} 个本地 Mod 的对应信息");
        if (!raw.Any()) return;
        Dictionary<int, List<TLocal>> mapping = new();
        Dictionary<TLocal, ModLoaders> loaders = new();
        Dictionary<TLocal,TVersion> installed = new();
        foreach (JToken project in raw) {
            string projectId = project["id"]!.ToString();
            uint hash = (uint)project["file"]!["fileFingerprint"]!;
            foreach (TLocal entry in mods) {
                if (entry.CurseForgeHash != hash) continue;
                string key = ResourceProjectKey.Get(ResourcePlatforms.CurseForge, projectId);
                if (host.ProjectCache().TryGetValue(key, out TProject? cached) && cached.Platform == ResourcePlatforms.CurseForge && entry.Project is null) entry.Project = cached;
                int id = Conversions.ToInteger(projectId);
                if (!mapping.ContainsKey(id)) mapping[id] = new List<TLocal>();
                mapping[id].Add(entry);
                TVersion file = host.ReadVersion((JObject)project["file"]!, ResourceTypes.Mod);
                installed[entry]=file;
                loaders[entry] = file.ModLoaders;
                if (entry.ProjectVersion is null || entry.ProjectVersion.ReleaseDate < file.ReleaseDate) entry.ProjectVersion = file;
            }
        }
        if (task.IsCanceledWithThread(currentTaskThread)) return;
        host.Info($"需要从 CurseForge 获取 {mapping.Count} 个本地 Mod 的工程信息");
        if (!mapping.Any()) return;
        JToken projects = host.Request("https://api.curseforge.com/v1/mods", HttpMethod.Post,
            "{\"modIds\": [" + string.Join(",", mapping.Keys) + "]}", "application/json")["data"]!;
        Dictionary<int, List<TLocal>> updateFileIds = new();
        Dictionary<int, string> slugs = new();
        foreach (JToken json in projects) {
            if (json["isAvailable"] is not null && !json["isAvailable"]!.ToObject<bool>()) continue;
            TProject project = host.CreateProject((JObject)json);
            foreach (TLocal local in mapping[Conversions.ToInteger(project.Id)]) {
                if (local.Project?.Platform == ResourcePlatforms.Modrinth) local.Project = local.Project;
                else local.Project = project;
                var latest = (json["latestFilesIndexes"] as JArray ?? new JArray()).Where(entry => {
                    ModLoaders entryLoader = entry["modLoader"] is null ? ModLoaders.None : Resource.FromCurseForgeModLoaderType(entry["modLoader"]!.ToObject<int>());
                    if (!SupportsUpdateLoader(entryLoader,targetLoaders,loaders[local])) return false;
                    string? entryVersion = (string?)entry["gameVersion"];
                    return entryVersion is not null && entryVersion == vanillaVersion;
                }).MaxByAll(entry => entry["gameVersion"]!.ToString(), Comparer<string>.Create(McUtils.CompareVersion));
                foreach (JToken entry in latest) {
                    int fileId = entry["fileId"]!.ToObject<int>();
                    if (!updateFileIds.ContainsKey(fileId)) updateFileIds[fileId] = new List<TLocal>();
                    updateFileIds[fileId].Add(local);
                    slugs[fileId] = project.Slug!;
                }
            }
        }
        host.Info($"已从 CurseForge 获取本地 Mod 信息，需要获取 {updateFileIds.Count} 个用于检查更新的文件信息");
        if (!updateFileIds.Any()) return;
        JToken files = host.Request("https://api.curseforge.com/v1/mods/files", HttpMethod.Post,
            "{\"fileIds\": [" + string.Join(",", updateFileIds.Keys) + "]}", "application/json")["data"]!;
        Dictionary<TLocal, TVersion> updates = new();
        foreach (JToken json in files) {
            TVersion file = host.ReadVersion((JObject)json, ResourceTypes.Mod);
            if (!file.DownloadAvailable) continue;
            if (json["gameVersions"]?.Any(version=>version.ToString()==vanillaVersion)!=true) continue;
            foreach (TLocal entry in updateFileIds[Conversions.ToInteger(file.Id)]) {
                if (!SupportsUpdateLoader(file.ModLoaders,targetLoaders,loaders[entry])) {
                    host.WarnQuiet($"从 CurseForge 获取到的更新文件 {file.FileName} 加载器为 {file.ModLoaders}，与本地文件 {entry.File.Name} 的加载器 {loaders[entry]} 不一致，已忽略"); continue;
                }
                if (updates.ContainsKey(entry) && updates[entry].ReleaseDate >= file.ReleaseDate) continue;
                updates[entry] = file;
            }
        }
        foreach (var pair in updates) {
            TLocal entry = pair.Key;
            TVersion update = pair.Value;
            TVersion current=installed[entry];
            host.Trace(() => $"本地文件 {current.FileName} 在 CurseForge 上的最新版为 {update.FileName}");
            if (current.ReleaseDate >= update.ReleaseDate || (current.Hash ?? "") == (update.Hash ?? "")) continue;
            string changelog = $"https://www.curseforge.com/minecraft/mc-mods/{slugs[Conversions.ToInteger(update.Id)]}/files/{update.Id}";
            if (entry.UpdateFile is not null && (update.Hash ?? "") == (entry.UpdateFile.Hash ?? "")) {
                entry.ChangelogUrls.Add(changelog);
                entry.UpdateFile.DownloadUrls!.AddRange(update.DownloadUrls!);
            } else if (entry.UpdateFile is null || update.ReleaseDate > entry.UpdateFile.ReleaseDate) {
                entry.ChangelogUrls = new List<string> { changelog };
                entry.UpdateFile = update;
            }
        }
        host.Info("从 CurseForge 获取 Mod 更新信息结束");
    }
}
