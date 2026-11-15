using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed class ResourceProjectServices {
    public Func<JObject, ResourceTypes, ResourceVersionModelData> ReadVersion = null!;
    public Action<ResourceProjectModelData> StoreCache = null!;
    public Func<string, string> MirrorUrl = null!;
    public Func<List<int>?> AllDrops = null!;
    public Func<string> ImageRoot = null!;
    public Action<string> Info = null!;
    public Func<IReadOnlyList<WikiEntry>>? WikiEntries;
}

// LF ResourceProject.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; LF LICENCE.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 differs by LF's Map recognition.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModComp.cs/CompProject
// was reviewed (CE Plain Craft Launcher 2/LICENCE; LF LICENCE). LF's schema, mutable cache and tags are retained.
public partial class ResourceProjectModelData {
    [JsonIgnore] public ResourceProjectServices Services { get; }
    public readonly ResourcePlatforms Platform;
    public readonly ResourceTypes Types;
    public readonly string? Slug, Id;
    public readonly List<int>? CurseForgeFileIds;
    public readonly string? RawName, Description, Website;
    public readonly DateTime LastUpdate;
    public readonly int DownloadCount;
    public readonly ModLoaders ModLoaders = ModLoaders.None;
    public readonly List<string> Tags;
    public string? LogoUrl;
    public readonly List<int> Drops;
    public readonly List<string>? UnsafeGameVersions;
    // Exact cached game-version labels, never the coarse Drops or Modrinth version IDs.
    public readonly List<string> KnownGameVersions;
    private bool loadedDatabase;
    private WikiEntry? wikiEntry;

    private WikiEntry? WikiEntry {
        get {
            if (!loadedDatabase) {
                loadedDatabase = true;
                if (Types.HasFlagF(ResourceTypes.Mod) || Types.HasFlagF(ResourceTypes.DataPack))
                    wikiEntry = ResourceWikiIdentity.Resolve(Services.WikiEntries?.Invoke() ?? PCLCS.WikiEntry.All.Value, Platform, Slug, RawName);
            }
            return wikiEntry;
        }
        set { loadedDatabase = true; wikiEntry = value; }
    }
    public int WikiId => WikiEntry is null ? 0 : WikiEntry.Id;
    public string? TranslatedName => WikiEntry?.ChineseName ?? RawName;
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);

    protected ResourceProjectModelData(JObject data, ResourceProjectServices services) {
        Services = services;
        if (data.ContainsKey("Tags")) {
            Platform = (ResourcePlatforms)data["Platform"]!.ToObject<int>();
            Types = (ResourceTypes)data["Types"]!.ToObject<int>();
            Slug = (string?)data["Slug"]; Id = (string?)data["Id"];
            if (data.ContainsKey("CurseForgeFileIds")) CurseForgeFileIds = ((JArray)data["CurseForgeFileIds"]!).Select(value => value.ToObject<int>()).ToList();
            RawName = (string?)data["RawName"]; Description = (string?)data["Description"]; Website = (string?)data["Website"];
            LastUpdate = (DateTime)data["LastUpdate"]!; DownloadCount = (int)data["DownloadCount"]!;
            if (data.ContainsKey("ModLoaders")) ModLoaders = (ModLoaders)data["ModLoaders"]!.ToObject<int>();
            Tags = ((JArray)data["Tags"]!).Select(value => value.ToString()).ToList();
            if (data.ContainsKey("LogoUrl")) LogoUrl = (string?)data["LogoUrl"];
            Drops = data.ContainsKey("Drops") ? ((JArray)data["Drops"]!).Select(value => value.ToObject<int>()).ToList() : new List<int>();
            KnownGameVersions = ReadKnownGameVersions(data["KnownGameVersions"] as JArray);
            if (Platform == ResourcePlatforms.Modrinth) UnsafeGameVersions = KnownGameVersions;
        } else {
            Platform = data.ContainsKey("summary") ? ResourcePlatforms.CurseForge : ResourcePlatforms.Modrinth;
            if (Platform == ResourcePlatforms.CurseForge) {
                Id = (string?)data["id"]; Slug = (string?)data["slug"]; RawName = (string?)data["name"]; Description = (string?)data["summary"];
                Website = data["links"]!["websiteUrl"]!.ToString().TrimEnd('/');
                LastUpdate = (DateTime)data["dateReleased"]!; DownloadCount = (int)data["downloadCount"]!;
                if (data["logo"]!.Count() > 0) {
                    if (data["logo"]!["thumbnailUrl"] is null || Same((string?)data["logo"]!["thumbnailUrl"], "")) LogoUrl = (string?)data["logo"]!["url"];
                    else LogoUrl = (string?)data["logo"]!["thumbnailUrl"];
                }
                if (Same(LogoUrl, "")) LogoUrl = null;
                if (Website.Contains("/mc-mods/") || Website.Contains("/mod/")) Types = ResourceTypes.Mod;
                else if (Website.Contains("/modpacks/")) Types = ResourceTypes.ModPack;
                else if (Website.Contains("/resourcepacks/") || Website.Contains("/texture-packs/")) Types = ResourceTypes.ResourcePack;
                else if (Website.Contains("/shaders/")) Types = ResourceTypes.Shader;
                else if (Website.Contains("/worlds/") || Website.Contains("/maps/") || data["classId"]?.ToObject<int>() == 17) Types = ResourceTypes.Map;
                else Types = ResourceTypes.DataPack;
                ModLoaders = ModLoaders.None;
                List<KeyValuePair<int, List<string>>> files = new();
                foreach (JToken file in data["latestFiles"] ?? new JArray()) {
                    ResourceVersionModelData newFile = services.ReadVersion((JObject)file, Types);
                    if (!newFile.DownloadAvailable) continue;
                    ModLoaders |= newFile.ModLoaders;
                    List<string> gameVersions = file["gameVersions"]!.ToObject<List<string>>()!;
                    if (!gameVersions.Any(McVersion.IsStandardFormat)) continue;
                    files.Add(new KeyValuePair<int, List<string>>((int)file["id"]!, gameVersions));
                }
                foreach (JToken file in data["latestFilesIndexes"] ?? new JArray()) {
                    if (!McVersion.IsStandardFormat((string?)file["gameVersion"])) continue;
                    files.Add(new KeyValuePair<int, List<string>>((int)file["fileId"]!, new List<string> { file["gameVersion"]!.ToString() }));
                }
                CurseForgeFileIds = files.Select(file => file.Key).Distinct().ToList();
                KnownGameVersions = ReadKnownGameVersions(new JArray(files.SelectMany(file => file.Value)));
                Drops = files.SelectMany(file => file.Value).Select(McVersion.VersionToDrop).Where(value => value != 209).Distinct().OrderByDescending(value => value).ToList();
                Tags = new List<string>();
                foreach (int category in (data["categories"] ?? new JArray()).Select(value => (int)value["id"]!).Distinct().OrderByDescending(value => value))
                    if (CurseForgeTags.TryGetValue(category, out string? tag)) Tags.Add(tag);
            } else {
                Id = (string?)(data["project_id"] ?? data["id"]); Slug = (string?)data["slug"]; RawName = (string?)data["title"]; Description = (string?)data["description"];
                LastUpdate = (DateTime)(data["date_modified"] ?? data["updated"])!; DownloadCount = (int)data["downloads"]!;
                LogoUrl = (string?)data["icon_url"]; if (Same(LogoUrl, "")) LogoUrl = null;
                Website = $"https://modrinth.com/{data["project_type"]}/{Slug}";
                // Search hits can expose game labels in versions; /project can expose
                // base62 file-version IDs there. Validate labels before retaining either.
                KnownGameVersions = ReadKnownGameVersions((data["game_versions"] ?? data["versions"]) as JArray);
                UnsafeGameVersions = KnownGameVersions;
                Drops = UnsafeGameVersions.Select(McVersion.VersionToDrop).Where(value => value != 209).Distinct().OrderByDescending(value => value).ToList();
                Types = data["project_type"]!.ToString() switch { "modpack" => ResourceTypes.ModPack, "resourcepack" => ResourceTypes.ResourcePack, "shader" => ResourceTypes.Shader, _ => ResourceTypes.Mod };
                Tags = new List<string>(); ModLoaders = ModLoaders.None;
                foreach (string category in (data["loaders"]?.Select(value => value.ToString()) ?? Array.Empty<string>()).Concat(data["categories"]!.Select(value => value.ToString()))) {
                    foreach (ModLoaders loader in EnumUtils.GetAllFlags<ModLoaders>()) {
                        if (Same(category, loader.ToString().ToLowerInvariant())) { ModLoaders |= loader; break; }
                    }
                }
                foreach (string category in data["categories"]!.Select(value => value.ToString())) {
                    if (category == "datapack") Types = ResourceTypes.DataPack;
                    else if (ModrinthTags.TryGetValue(category, out string? tag) &&
                        (Types != ResourceTypes.ResourcePack || (category != "decoration" && category != "mobs" && category != "equipment"))) Tags.Add(tag);
                }
                if (Types == ResourceTypes.DataPack && ModLoaders != ModLoaders.None) Types = ResourceTypes.DataPack | ResourceTypes.Mod;
            }
            if (!Tags.Any()) Tags.Add("其他");
            Tags.Sort();
        }
        services.StoreCache(this);
    }

    public JObject ToJson() {
        JObject json = new() { ["Platform"] = (int)Platform, ["Types"] = (int)Types, ["Slug"] = Slug, ["Id"] = Id };
        if (CurseForgeFileIds is not null) json["CurseForgeFileIds"] = new JArray(CurseForgeFileIds);
        json["RawName"] = RawName; json["Description"] = Description; json["Website"] = Website; json["LastUpdate"] = LastUpdate; json["DownloadCount"] = DownloadCount;
        if (ModLoaders != ModLoaders.None) json["ModLoaders"] = (int)ModLoaders;
        json["Tags"] = new JArray(Tags);
        if (LogoUrl is not null) json["LogoUrl"] = LogoUrl;
        if (Drops.Any()) json["Drops"] = new JArray(Drops);
        if (KnownGameVersions.Count > 0) json["KnownGameVersions"] = new JArray(KnownGameVersions);
        json["CacheTime"] = DateTime.Now;
        return json;
    }
    private static List<string> ReadKnownGameVersions(JArray? versions) => versions is null ? new List<string>() : versions
        .Where(value => value.Type == JTokenType.String).Select(value => value.ToString())
        .Where(ResourceFavoriteFilter.IsGameVersionLabel).Distinct(StringComparer.Ordinal).ToList();

    public bool IsLike(ResourceProjectModelData project) {
        if (Same(Id, project.Id)) return Platform == project.Platform;
        string GetRaw(string data) => new string(data.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (Platform == project.Platform || ModLoaders != project.ModLoaders) return false;
        // This seemingly inverted comparison is LF behavior; do not introduce a similarity policy change.
        if (Types != ResourceTypes.Shader && Drops.Ordered().SequenceEqual(project.Drops.Ordered())) return false;
        if (Math.Abs((LastUpdate - project.LastUpdate).TotalDays) > 7) return false;
        bool wikiIdentity = WikiId > 0 && WikiId == project.WikiId;
        bool platformNames = !string.IsNullOrWhiteSpace(RawName) && !string.IsNullOrWhiteSpace(Slug) && !string.IsNullOrWhiteSpace(project.Slug) &&
            Same(RawName, project.RawName) && Same(GetRaw(Slug!), GetRaw(project.Slug!));
        if (wikiIdentity || platformNames) {
            Services.Info($"将 {RawName} ({Slug}) 与 {project.RawName} ({project.Slug}) 认定为相似工程");
            return true;
        }
        return false;
    }
    public override string ToString() => $"{Id} ({Slug}): {RawName}";
    public override bool Equals(object? value) => value is ResourceProjectModelData project && Platform == project.Platform && Same(Id, project.Id);
    public override int GetHashCode() => unchecked(((int)Platform * 397) ^ StringComparer.Ordinal.GetHashCode(Id ?? ""));
}

public sealed class ResourceProjectModel : ResourceProjectModelData {
    public ResourceProjectModel(JObject data, ResourceProjectServices services) : base(data, services) { }
}
