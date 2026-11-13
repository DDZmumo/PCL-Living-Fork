using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Modern.Wpf.Tests;

internal static class ResourceProjectIdentityChecks {
    internal static void Run() {
        ResourceWikiIdentityChecks.Run();
        var projects = new Dictionary<string, ResourceProjectModel>();
        var versions = new Dictionary<string, List<ResourceVersionModel>>();
        var requests = new List<string>();
        var versionServices = new ResourceVersionServices { MirrorUrl = value => value, DebugMode = () => false, ImageRoot = () => "" };
        ResourceVersionModel Version() => new(versionServices);
        var services = new ResourceProjectServices {
            ReadVersion = (json, type) => ResourceVersionRules.FromPlatformJson(json, type, Version),
            StoreCache = project => projects[ResourceProjectKey.Get(project.Platform, project.Id)] = (ResourceProjectModel)project,
            MirrorUrl = value => value, AllDrops = () => new List<int>(), ImageRoot = () => "", Info = _ => { }
        };
        ResourceProjectModel Create(JObject json) => new(json, services);
        var pipeline = new ResourceCatalogPipeline<ResourceProjectModel, ResourceVersionModel>(new() {
            ProjectCache = () => projects, VersionCache = () => versions, CreateProject = Create, CreateVersion = Version, Info = _ => { }, Warn = _ => { },
            Request = (url, method, body, _) => {
                requests.Add(url);
                bool cf = url.Contains("curseforge.com");
                if (url.Contains("/files?")) return new JObject { ["pagination"] = new JObject { ["resultCount"] = 1, ["totalCount"] = 1 }, ["data"] = new JArray(CfVersion()) };
                if (url.Contains("/version?")) return new JArray(MrVersion());
                if (cf) return new JObject { ["data"] = method is null ? Metadata(ResourcePlatforms.CurseForge) : new JArray(Metadata(ResourcePlatforms.CurseForge)) };
                return url.Contains("/projects?") ? new JArray(Metadata(ResourcePlatforms.Modrinth)) : Metadata(ResourcePlatforms.Modrinth);
            }
        });
        var cf = pipeline.FromProjectId("123", ResourcePlatforms.CurseForge);
        var mr = pipeline.FromProjectId("123", ResourcePlatforms.Modrinth);
        Check(cf.Platform == ResourcePlatforms.CurseForge && mr.Platform == ResourcePlatforms.Modrinth && !cf.Equals(mr) && !cf.IsLike(mr), "Same ID merged two providers");
        Check(new HashSet<ResourceProjectModel> { cf, mr, Create(Metadata(ResourcePlatforms.CurseForge)) }.Count == 2, "Project equality/hash lost platform identity");
        var cfVersions = pipeline.VersionsFromProjectId("123", ResourcePlatforms.CurseForge);
        var mrVersions = pipeline.VersionsFromProjectId("123", ResourcePlatforms.Modrinth);
        Check(cfVersions.Single().Id == "9001" && mrVersions.Single().Id == "MrV12345", "Version cache returned the other provider's file");
        Check(ReferenceEquals(cfVersions, pipeline.VersionsFromProjectId("123", ResourcePlatforms.CurseForge)) && requests.Count == 4, "Repeated query did not use the correct provider cache");
        Check(pipeline.FromProjectIds(new[] { "123" }, ResourcePlatforms.CurseForge).Single().Platform == ResourcePlatforms.CurseForge &&
            pipeline.FromProjectIds(new[] { "123" }, ResourcePlatforms.Modrinth).Single().Platform == ResourcePlatforms.Modrinth && requests.Count == 4, "Batch cache lookup crossed providers");
        projects.Clear();
        Check(pipeline.FromProjectIds(new[] { "123" }, ResourcePlatforms.CurseForge).Single().Platform == ResourcePlatforms.CurseForge &&
            pipeline.FromProjectIds(new[] { "123" }, ResourcePlatforms.Modrinth).Single().Platform == ResourcePlatforms.Modrinth, "Batch platform response was mis-cached");
        projects[ResourceProjectKey.Get(ResourcePlatforms.CurseForge, "123")] = mr;
        Check(pipeline.FromProjectId("123", ResourcePlatforms.CurseForge).Platform == ResourcePlatforms.CurseForge, "A cache hit with mismatched platform was trusted");
        versions["123"] = new(); ResourceProjectKey.Invalidate(versions, "123"); Check(versions.Count == 0, "Invalidation left a raw or provider-specific stale version list");
        Check(pipeline.VersionsFromProjectId("123", ResourcePlatforms.Modrinth).Single().Id == "MrV12345" && pipeline.VersionsFromProjectId("123", ResourcePlatforms.CurseForge).Single().Id == "9001", "Invalidated providers did not re-fetch their own versions");
        Check(ResourceProjectKey.Get(ResourcePlatforms.Modrinth, "AbCd1234") != ResourceProjectKey.Get(ResourcePlatforms.Modrinth, "abcd1234"), "Cache key lowercased Modrinth IDs");
        var rawMr = new JObject { ["id"] = "Exact123", ["slug"] = "fixture", ["title"] = "Compatibility fixture", ["description"] = "fixture", ["updated"] = new DateTime(2024, 1, 1),
            ["downloads"] = 0, ["project_type"] = "mod", ["categories"] = new JArray("fabric"), ["versions"] = new JArray("AbCd1234") };
        Check(Create(rawMr).KnownGameVersions.Count == 0, "Modrinth /project version IDs became game compatibility");
        rawMr["versions"] = new JArray("1.20.1", "AbCd1234");
        Check(Create(rawMr).KnownGameVersions.SequenceEqual(new[] { "1.20.1" }), "Validated game labels in search-hit versions were discarded or mixed with version IDs");
        rawMr["game_versions"] = new JArray("1.21.1", "1.21.11");
        var known = Create(rawMr); var restored = Create(known.ToJson());
        Check(restored.KnownGameVersions.SequenceEqual(new[] { "1.21.1", "1.21.11" }) && McVersion.VersionToDrop("1.21.1") == McVersion.VersionToDrop("1.21.11"), "Exact compatibility was lost to a coarse Drop during cache serialization");
        Console.WriteLine("PASS resource catalog identity: real project/batch/version pipeline with synthetic CF/MR responses for the same ID, platform-aware equality/hash, correct cached re-query, mismatched-hit rejection and all-provider invalidation.");
    }
    private static JObject Metadata(ResourcePlatforms platform) => new() {
        ["Platform"] = (int)platform, ["Types"] = (int)ResourceTypes.Mod, ["Id"] = "123", ["Slug"] = "same-slug", ["RawName"] = "Same display name",
        ["Description"] = "Synthetic platform project", ["LastUpdate"] = new DateTime(2024, 1, 1), ["DownloadCount"] = 0, ["Tags"] = new JArray("fixture"), ["Drops"] = new JArray()
    };
    private static JObject CfVersion() => new() {
        ["gameId"] = 432, ["id"] = 9001, ["displayName"] = "CF fixture", ["fileDate"] = new DateTime(2024, 1, 1), ["releaseType"] = 1, ["downloadCount"] = 0,
        ["fileName"] = "cf.jar", ["fileLength"] = 1, ["hashes"] = new JArray(), ["downloadUrl"] = "https://example.invalid/cf.jar", ["dependencies"] = new JArray(), ["gameVersions"] = new JArray("1.21.1", "fabric")
    };
    private static JObject MrVersion() => new() {
        ["id"] = "MrV12345", ["name"] = "MR fixture", ["version_number"] = "1", ["date_published"] = new DateTime(2024, 1, 1), ["version_type"] = "release", ["downloads"] = 0,
        ["files"] = new JArray(new JObject { ["filename"] = "mr.jar", ["url"] = "https://example.invalid/mr.jar", ["size"] = 1, ["hashes"] = new JObject() }),
        ["loaders"] = new JArray("fabric"), ["game_versions"] = new JArray("1.21.1"), ["dependencies"] = new JArray()
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
