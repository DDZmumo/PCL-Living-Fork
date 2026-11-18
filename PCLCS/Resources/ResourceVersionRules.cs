using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Selective C# migration reference: CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/Modules/Minecraft/ModComp.cs (CompFile), CE Plain Craft Launcher 2/LICENCE; LF LICENCE.
// LF ResourceVersion.vb and its historical Newtonsoft/JArray schema remain the behavior source.
public static class ResourceVersionRules {
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);

    public static TVersion FromPlatformJson<TVersion>(JObject data, ResourceTypes expectedResourceType, Func<TVersion> create)
        where TVersion : ResourceVersionModelData {
        TVersion result = create();
        result.ResourceType = expectedResourceType;
        if (data.ContainsKey("gameId")) {
            result.ProjectId = (string?)data["modId"];
            result.RequiredDependencyReferences = data["dependencies"]?.OfType<JObject>()
                .Where(value => (int?)value["relationType"] == 3)
                .Select(value => new ModDownloadDependencyReference((string?)value["modId"], null)).ToList() ?? new();
            result.Id = (string?)data["id"];
            result.Display = data["displayName"]!.ToString().Replace("\t", "").Trim(' ');
            result.Version = null;
            result.ReleaseDate = (DateTime)data["fileDate"]!;
            result.ReleaseTypeCode = data["releaseType"]!.ToObject<int>();
            result.DownloadCount = (int)data["downloadCount"]!;
            result.FileName = (string?)data["fileName"];
            result.Size = (int)data["fileLength"]!;
            if (result.Size == 0) result.Size = -1;
            result.Hash = ((JArray)data["hashes"]!).ToList().FirstOrDefault(value => value["algo"]!.ToObject<int>() == 1)?["value"]?.ToString().ToLowerInvariant();
            result.Hash ??= ((JArray)data["hashes"]!).ToList().FirstOrDefault(value => value["algo"]!.ToObject<int>() == 2)?["value"]?.ToString().ToLowerInvariant();
            string url = data["downloadUrl"]!.ToString();
            if (url == "") url = $"https://edge.forgecdn.net/files/{Conversions.ToInteger(result.Id!.Substring(0, 4))}/{Conversions.ToInteger(result.Id.Substring(4))}/{result.FileName}";
            result.DownloadUrls = ParseCurseForgeDownloadUrls(url.Replace(result.FileName!, StringUtils.UrlEscape(result.FileName!)));
            result.DownloadUrls.Add(result.Services.MirrorUrl(url));
            result.DownloadUrls = result.DownloadUrls.Distinct().ToList();
            if (data.ContainsKey("dependencies")) result.RawDependencies = data["dependencies"]!
                .Where(value => value["relationType"]!.ToObject<int>() == 3 && value["modId"]!.ToObject<int>() != 306612 && value["modId"]!.ToObject<int>() != 634179)
                .Select(value => value["modId"]!.ToString()).ToList();
            List<string> rawVersions = data["gameVersions"]!.Select(value => value.ToString().Trim().ToLowerInvariant()).ToList();
            result.ExactGameVersions = data["gameVersions"]!.Select(value => value.ToString().Trim()).Where(McVersion.IsStandardFormat).ToList();
            result.GameVersions = rawVersions.Where(McVersion.IsStandardFormat).Select(value => value.Replace("-snapshot", " 预览版")).Distinct().ToList();
            if (result.GameVersions.Count == 1) result.GameVersions = result.GameVersions.ToList();
            else if (result.GameVersions.Count > 1) {
                result.GameVersions = SortVersions(result.GameVersions);
                if (result.ResourceType == ResourceTypes.ModPack) result.GameVersions = new List<string> { result.GameVersions[0] };
            } else result.GameVersions = new List<string> { "未知版本" };
            result.ModLoaders = ModLoaders.None;
            foreach (ModLoaders loader in EnumUtils.GetAllFlags<ModLoaders>())
                if (rawVersions.Contains(loader.ToString().ToLowerInvariant())) result.ModLoaders |= loader;
        } else {
            result.ProjectId = (string?)data["project_id"];
            result.RequiredDependencyReferences = data["dependencies"]?.OfType<JObject>()
                .Where(value => Same((string?)value["dependency_type"], "required"))
                .Select(value => new ModDownloadDependencyReference((string?)value["project_id"], (string?)value["version_id"])).ToList() ?? new();
            result.Id = (string?)data["id"];
            result.Display = data["name"]!.ToString().Replace("\t", "").Trim(' ');
            result.Version = (string?)data["version_number"];
            result.ReleaseDate = (DateTime)data["date_published"]!;
            result.ReleaseTypeCode = data["version_type"]!.ToString() == "release" ? 1 : data["version_type"]!.ToString() == "beta" ? 2 : 3;
            result.DownloadCount = (int)data["downloads"]!;
            if (((JArray)data["files"]!).Any()) {
                JToken file = data["files"]!.FirstOrDefault(value => (bool?)value["primary"] == true) ?? data["files"]![0]!;
                result.FileName = (string?)file["filename"];
                string? url = (string?)file["url"];
                result.DownloadUrls = new List<string> { url!, result.Services.MirrorUrl(url!) }.Distinct().ToList();
                result.Size = (int)file["size"]!;
                result.Hash = file["hashes"]!["sha1"]?.ToString().ToLowerInvariant();
            }
            List<string> rawLoaders = data["loaders"]!.Select(value => value.ToString()).ToList();
            result.ModLoaders = ModLoaders.None;
            foreach (ModLoaders loader in EnumUtils.GetAllFlags<ModLoaders>())
                if (rawLoaders.Contains(loader.ToString().ToLowerInvariant())) result.ModLoaders |= loader;
            if (result.ResourceType.HasFlagF(ResourceTypes.Mod) || result.ResourceType.HasFlagF(ResourceTypes.DataPack)) {
                if (rawLoaders.Intersect(new[] { "bukkit", "folia", "paper", "purpur", "spigot" }).Any()) result.ResourceType = ResourceTypes.Plugin;
                if (rawLoaders.Contains("datapack")) result.ResourceType = ResourceTypes.DataPack;
                if (result.ModLoaders.Flags().Any()) result.ResourceType = ResourceTypes.Mod;
            }
            if (data.ContainsKey("dependencies")) result.RawDependencies = data["dependencies"]!
                .Where(value => Same((string?)value["dependency_type"], "required") && !Same((string?)value["project_id"], "P7dR8mSH") &&
                    !Same((string?)value["project_id"], "qvIfYCYJ") && !string.IsNullOrEmpty((string?)value["project_id"]))
                .Select(value => value["project_id"]!.ToString()).ToList();
            List<string> rawVersions = data["game_versions"]!.Select(value => value.ToString().Trim().ToLowerInvariant()).ToList();
            result.ExactGameVersions = data["game_versions"]!.Select(value => value.ToString().Trim()).ToList();
            result.GameVersions = rawVersions.Where(value => value.Contains('.')).Select(value => value.Contains('-') ? value.BeforeFirst("-") + " 预览版" :
                value.StartsWithF("b1.") ? "远古版本" : value).Distinct().ToList();
            if (result.GameVersions.Count > 1) {
                result.GameVersions = SortVersions(result.GameVersions);
                if (result.ResourceType == ResourceTypes.ModPack) result.GameVersions = new List<string> { result.GameVersions[0] };
            } else if (result.GameVersions.Count == 0) {
                if (rawVersions.Any(value => value.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]"))) result.GameVersions = rawVersions.Where(value => value.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]")).ToList();
                else result.GameVersions = new List<string> { "未知版本" };
            }
        }
        return result;
    }

    // Retain LF's destructive selection sort and >= tie behavior, not a new LINQ ordering policy.
    private static List<string> SortVersions(IList<string> source) {
        List<string> result = new();
        while (source.Any()) {
            string highest = source[0];
            for (int index = 1; index < source.Count; index++) if (McUtils.CompareVersionGE(source[index], highest)) highest = source[index];
            source.Remove(highest); result.Add(highest);
        }
        return result;
    }

    public static TVersion FromCacheJson<TVersion, TRelease>(JObject data, Func<TVersion> create)
        where TVersion : ResourceVersionData<TRelease> where TRelease : struct, Enum {
        TVersion result = create();
        result.ResourceType = data["ResourceType"]!.ToObject<ResourceTypes>();
        result.Id = data["Id"]!.ToString(); result.Display = data["DisplayName"]!.ToString();
        if (data.ContainsKey("Version")) result.Version = data["Version"]!.ToString();
        result.ReleaseDate = data["ReleaseDate"]!.ToObject<DateTime>();
        result.DownloadCount = data["DownloadCount"]!.ToObject<int>();
        result.ReleaseType = data["ReleaseType"]!.ToObject<TRelease>();
        if (data.ContainsKey("FileName")) result.FileName = data["FileName"]!.ToString();
        if (data.ContainsKey("DownloadUrls")) result.DownloadUrls = data["DownloadUrls"]!.ToObject<List<string>>();
        if (data.ContainsKey("ModLoaders")) result.ModLoaders = data["ModLoaders"]!.ToObject<ModLoaders>();
        if (data.ContainsKey("Size")) result.Size = data["Size"]!.ToObject<int>();
        if (data.ContainsKey("Hash")) result.Hash = data["Hash"]!.ToString();
        if (data.ContainsKey("GameVersions")) result.GameVersions = data["GameVersions"]!.ToObject<List<string>>()!;
        if (data.ContainsKey("RawDependencies")) result.RawDependencies = data["RawDependencies"]!.ToObject<List<string>>()!;
        if (data.ContainsKey("Dependencies")) result.Dependencies = data["Dependencies"]!.ToObject<List<string>>()!;
        return result;
    }

    public static IEnumerable<string> ParseModrinthTrackArguments(List<string> urls, string? trackReason = null, string? trackGameVersion = null, ModLoaders trackLoader = ModLoaders.None) {
        Dictionary<string, string> arguments = new();
        if (trackReason is not null) arguments.Add("mr_download_reason", trackReason.ToLowerInvariant());
        if (trackGameVersion is not null) arguments.Add("mr_game_version", trackGameVersion);
        if (trackLoader.Flags().Any()) arguments.Add("mr_loader", trackLoader.Flags().First().ToString().ToLowerInvariant());
        string argument = string.Join("&", arguments.Select(pair => $"{pair.Key}={pair.Value}"));
        foreach (string url in urls) {
            if (!url.ContainsIgnoreCase("cdn.modrinth.com/")) yield return url;
            yield return $"{url}{(url.Contains("?") ? "&" : "?")}{argument}";
        }
    }
    public static List<string> ParseCurseForgeDownloadUrls(string url) => new[] {
        url.Replace("-service.overwolf.wtf", ".forgecdn.net").Replace("://edge.", "://mediafilez.").Replace("://media.", "://mediafilez."),
        url.Replace("://edge.", "://mediafilez.").Replace("://media.", "://mediafilez."),
        url.Replace("-service.overwolf.wtf", ".forgecdn.net"), url.Replace("://media.", "://edge."), url
    }.Distinct().ToList();
}

public abstract partial class ResourceVersionModelData {
    public JObject ToCacheJson() {
        JObject result = new() { ["ResourceType"] = (int)ResourceType, ["Id"] = Id };
        if (Version is not null) result.Add("Version", Version);
        result.Add("DisplayName", Display); result.Add("ReleaseDate", ReleaseDate); result.Add("DownloadCount", DownloadCount);
        result.Add("ModLoaders", (int)ModLoaders); result.Add("GameVersions", new JArray(GameVersions)); result.Add("ReleaseType", ReleaseTypeCode);
        if (FileName is not null) result.Add("FileName", FileName);
        if (DownloadUrls is not null) result.Add("DownloadUrls", new JArray(DownloadUrls));
        if (Hash is not null) result.Add("Hash", Hash);
        if (Size >= 0) result.Add("Size", Size);
        result.Add("RawDependencies", new JArray(RawDependencies)); result.Add("Dependencies", new JArray(Dependencies));
        return result;
    }
    public ResourceDownloadDescriptor ToDownloadDescriptor(string localFileOrFolder, string? trackReason = null, string? trackGameVersion = null, ModLoaders trackLoader = ModLoaders.None) => new() {
        Urls = ResourceVersionRules.ParseModrinthTrackArguments(DownloadUrls!, trackReason, trackGameVersion, trackLoader),
        Destination = localFileOrFolder + (localFileOrFolder.EndsWithF("\\") ? FileName : ""), Hash = Hash, Size = Size
    };
}
