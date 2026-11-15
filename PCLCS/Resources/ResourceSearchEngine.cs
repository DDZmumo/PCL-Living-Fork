using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class ResourceSearchEngine<TProject, TResult>
    where TProject : ResourceProjectModelData where TResult : ResourceSearchResultData<TProject> {
    private const string NoChineseResult = "无搜索结果，请尝试搜索其英文名称";
    private readonly ResourceSearchHost<TProject> host;
    private List<ResourceSearchEntry<WikiEntry>>? curseForgeEntries, modrinthEntries;
    public ResourceSearchEngine(ResourceSearchHost<TProject> host) { this.host = host; }

    public void Run(ResourceSearchRequestData<TProject, TResult> request, ResourceSearchTask task) {
        TResult storage = request.Storage; // Capture the original storage even if another thread replaces request.Storage.
        if (storage.Results.Count >= request.TargetResultCount) {
            host.Info($"已有 {storage.Results.Count} 个结果，多于所需的 {request.TargetResultCount} 个结果，结束处理"); return;
        } else if (!request.CanContinue) {
            if (!storage.Results.Any()) throw new Exception("没有符合条件的结果");
            host.Info($"已有 {storage.Results.Count} 个结果，少于所需的 {request.TargetResultCount} 个结果，但无法继续获取，结束处理"); return;
        }
        string rawSearchText = (request.SearchText ?? "").Trim().Lower();
        host.Info($"工程列表搜索原始文本：{rawSearchText}");
        rawSearchText = host.SimplifyChinese(rawSearchText);
        bool isChineseSearch = rawSearchText.RegexCheck(@"[\u4e00-\u9fbb]") && !string.IsNullOrEmpty(rawSearchText) && (request.Type == ResourceTypes.Mod || request.Type == ResourceTypes.DataPack);
        string? curseForgeAltSearchText = null, modrinthAltSearchText = null;
        List<string> modrinthSlugs = new();
        List<WikiEntry> modrinthDirectEntries = new();
        if (isChineseSearch) {
            if (request.Sources.HasFlagF(ResourcePlatforms.CurseForge)) {
                if (curseForgeEntries is null) curseForgeEntries = ResourceSearchMatching.WikiEntries(host.WikiEntries(), ResourcePlatforms.CurseForge, host.SimplifyChinese);
                var searchResults = ResourceSearchMatching.Search(curseForgeEntries, rawSearchText, 100, 0.25);
                if (searchResults.Any()) {
                    var candidates = searchResults.First().AbsoluteRight ? searchResults.Where(s => s.AbsoluteRight).ToList() : searchResults.MaxByAll(s => s.Similarity);
                    var target = MeloongCore.Extensions.EnumerableExtensions.MaxBy(candidates, s => s.Item.Popularity)!;
                    curseForgeAltSearchText = string.Join(" ", ResourceSearchMatching.ExtractWords(target, ResourcePlatforms.CurseForge));
                    host.Warning($"中文搜索关键词（CurseForge）：{curseForgeAltSearchText}");
                }
            }
            if (request.Sources.HasFlagF(ResourcePlatforms.Modrinth)) {
                if (modrinthEntries is null) modrinthEntries = ResourceSearchMatching.WikiEntries(host.WikiEntries(), ResourcePlatforms.Modrinth, host.SimplifyChinese);
                var searchResults = ResourceSearchMatching.Search(modrinthEntries, rawSearchText, 100, 0.25);
                if (searchResults.Any()) {
                    var keywordResults = searchResults.First().AbsoluteRight ? searchResults.Where(result => result.AbsoluteRight).ToList() : searchResults.MaxByAll(result => result.Similarity);
                    Dictionary<string, double> weights = new();
                    foreach (var result in keywordResults) foreach (string word in ResourceSearchMatching.ExtractWords(result, ResourcePlatforms.Modrinth)) {
                        if (!weights.ContainsKey(word)) weights.Add(word, 0);
                        double similarity = result.Sources.Any(s => s.Aliases.Contains(rawSearchText)) ? 1000 : result.Similarity;
                        weights[word] += similarity * result.Item.Popularity;
                    }
                    modrinthAltSearchText = MeloongCore.Extensions.EnumerableExtensions.MaxBy(weights, w => w.Value).Key;
                    host.Warning($"中文搜索关键词（Modrinth）：{modrinthAltSearchText}");
                    modrinthDirectEntries = searchResults.Take(100).Select(r => r.Item)
                        .Where(entry => entry.Slugs.TryGetValue(ResourcePlatforms.Modrinth, out string? slug) && !string.IsNullOrWhiteSpace(slug)).ToList();
                    modrinthSlugs = modrinthDirectEntries.Select(entry => entry.Slugs[ResourcePlatforms.Modrinth]).Distinct(StringComparer.Ordinal).ToList();
                }
            }
            if (string.IsNullOrEmpty(curseForgeAltSearchText) && string.IsNullOrEmpty(modrinthAltSearchText) && !modrinthSlugs.Any()) throw new Exception(NoChineseResult);
        }
        task.SetProgress(0.05);
        List<TProject> realResults = new();
        while (true) {
            List<TProject> rawResults;
            List<Thread> workThreads = new();
            ConcurrentBag<(Exception Ex, ResourcePlatforms Source)> errors = new();
            bool ignoreModLoaderFilter = request.ModLoaders == ModLoaders.Forge && McVersion.VersionToDrop(request.GameVersion) < 140;
            ConcurrentBag<TProject> searchResults = new();
            void AdvanceProgress() { if (task.Progress() < 0.75) task.SetProgress(task.Progress() + 0.25); }
            try {
                if (request.Sources.HasFlagF(ResourcePlatforms.CurseForge) && !(storage.CurseForgeTotal > -1 && storage.CurseForgeTotal <= storage.CurseForgeOffset) && (!isChineseSearch || !string.IsNullOrEmpty(curseForgeAltSearchText))) {
                    workThreads.Add(host.StartThread(() => {
                        try {
                            string url = request.GetCurseForgeAddress(curseForgeAltSearchText ?? rawSearchText, ignoreModLoaderFilter);
                            host.Info($"开始 CurseForge 搜索：{url}");
                            JObject result = (JObject)host.Request(url);
                            List<TProject> projects = new();
                            foreach (JObject json in result["data"]!) {
                                TProject project = host.CreateProject(json);
                                if (request.Type == ResourceTypes.ResourcePack && project.Tags.Contains("数据包")) continue;
                                if (request.Type != ResourceTypes.Any && !project.Types.HasFlagF(request.Type)) continue;
                                projects.Add(project);
                            }
                            projects.ForEach(p => searchResults.Add(p));
                            storage.CurseForgeOffset += result["data"]!.Count();
                            storage.CurseForgeTotal = result["pagination"]!["totalCount"]!.ToObject<int>();
                            host.Info($"从 CurseForge 搜索到了 {projects.Count} 个工程（总计已获取 {storage.CurseForgeOffset} 个，共 {storage.CurseForgeTotal} 个）");
                        } catch (Exception ex) { host.Warn(ex, "CurseForge 搜索失败"); storage.CurseForgeTotal = -1; errors.Add((ex, ResourcePlatforms.CurseForge)); }
                        AdvanceProgress();
                    }, "CurseForge Search"));
                }
                if (request.Sources.HasFlagF(ResourcePlatforms.Modrinth) && !(storage.ModrinthTotal > -1 && storage.ModrinthTotal <= storage.ModrinthOffset) && (!isChineseSearch || !string.IsNullOrEmpty(modrinthAltSearchText))) {
                    workThreads.Add(host.StartThread(() => {
                        try {
                            string url = request.GetModrinthAddress(modrinthAltSearchText ?? rawSearchText, ignoreModLoaderFilter);
                            host.Info($"开始 Modrinth 搜索：{url}");
                            JObject result = (JObject)host.Request(url);
                            List<TProject> projects = new();
                            foreach (JObject json in result["hits"]!) projects.Add(host.CreateProject(json));
                            projects.ForEach(p => searchResults.Add(p));
                            storage.ModrinthOffset += result["hits"]!.Count();
                            storage.ModrinthTotal = result["total_hits"]!.ToObject<int>();
                            host.Info($"从 Modrinth 搜索到了 {projects.Count} 个工程（总计已获取 {storage.ModrinthOffset} 个，共 {storage.ModrinthTotal} 个）");
                        } catch (Exception ex) { host.Warn(ex, "Modrinth 搜索失败"); storage.ModrinthTotal = -1; errors.Add((ex, ResourcePlatforms.Modrinth)); }
                        AdvanceProgress();
                    }, "Modrinth Search"));
                }
                if (request.Sources.HasFlagF(ResourcePlatforms.Modrinth) && !(storage.ModrinthTotal > -1 && storage.ModrinthTotal <= storage.ModrinthOffset) && modrinthSlugs.Any()) {
                    workThreads.Add(host.StartThread(() => {
                        try {
                            string url = "https://api.modrinth.com/v2/projects?ids=[\"" + string.Join("\",\"", modrinthSlugs) + "\"]";
                            host.Info($"开始 Modrinth 直接获取：{url}");
                            List<TProject> projects = new();
                            foreach (JObject json in host.Request(url)) {
                                TProject project = host.CreateProject(json);
                                if (project.Platform != ResourcePlatforms.Modrinth || !modrinthDirectEntries.Any(entry => ResourceWikiIdentity.DirectMatch(entry, project.Platform, project.Slug!, project.RawName!))) {
                                    host.Info($"跳过与中文索引名称不符的 Modrinth 直接匹配：{project.Id} / {project.RawName}");
                                    continue;
                                }
                                if (request.Type != ResourceTypes.Any && !project.Types.HasFlagF(request.Type)) continue;
                                if (!string.IsNullOrEmpty(request.Tag) && !json["categories"]!.Any(c => c.ToString() == request.Tag.AfterLast("/"))) continue;
                                if (request.ModLoaders != ModLoaders.None && !ignoreModLoaderFilter && !project.ModLoaders.Flags().Intersect(request.ModLoaders.Flags()).Any()) continue;
                                if (!string.IsNullOrEmpty(request.GameVersion) && !project.UnsafeGameVersions!.Any(d => d == request.GameVersion)) continue;
                                projects.Add(project);
                            }
                            if (projects.Any() && request.ModLoaders != ModLoaders.None && !ignoreModLoaderFilter && !string.IsNullOrEmpty(request.GameVersion)) {
                                string ids = string.Join("\",\"project_id:", projects.Select(p => p.Id));
                                string loaders = string.Join("\",\"categories:", request.ModLoaders.Flags().Select(f => f.ToString().Lower()));
                                string filterUrl = $"https://api.modrinth.com/v2/search?limit={projects.Count}&facets=[[\"project_id:{ids}\"],[\"categories:{loaders}\"],[\"versions:{request.GameVersion}\"]]";
                                var matchedIds = host.Request(filterUrl)["hits"]!.Select(h => h["project_id"]!.ToString()).ToList();
                                projects = projects.Where(p => matchedIds.Contains(p.Id!)).ToList();
                            }
                            projects.ForEach(p => searchResults.Add(p));
                            host.Info($"从 Modrinth 直接获取到了 {projects.Count} 个工程");
                            modrinthSlugs.Clear();
                        } catch (Exception ex) { host.Warn(ex, "Modrinth 直接获取失败"); errors.Add((ex, ResourcePlatforms.Modrinth)); }
                        AdvanceProgress();
                    }, "Modrinth Get"));
                }
                foreach (Thread thread in workThreads) { thread.Join(); if (task.IsCanceled()) return; }
                rawResults = new List<TProject>(searchResults);
                if (ignoreModLoaderFilter) rawResults = rawResults.Where(p => p.ModLoaders == ModLoaders.None || p.ModLoaders.HasFlagF(ModLoaders.Forge)).ToList();
                storage.ErrorMessage = null;
                if (!rawResults.Any()) {
                    if (errors.Any()) throw errors.First().Ex;
                    if (isChineseSearch && !(request.Type == ResourceTypes.Mod || request.Type == ResourceTypes.DataPack)) throw new Exception(NoChineseResult);
                    if (request.Sources == ResourcePlatforms.CurseForge && request.Tag.StartsWithF("/")) throw new Exception("CurseForge 不兼容所选的类型");
                    if (request.Sources == ResourcePlatforms.Modrinth && request.Tag.EndsWithF("/")) throw new Exception("Modrinth 不兼容所选的类型");
                    throw new Exception("没有搜索结果");
                } else if (errors.Any()) {
                    storage.ErrorMessage = errors.Any(e => e.Source == ResourcePlatforms.CurseForge)
                        ? "无法连接到 CurseForge，所以目前仅显示了来自 Modrinth 的内容，搜索结果可能不全。\r\n请稍后再试，或使用 VPN 改善网络环境。"
                        : "无法连接到 Modrinth，所以目前仅显示了来自 CurseForge 的内容，搜索结果可能不全。\r\n请稍后再试，或使用 VPN 改善网络环境。";
                }
            } finally {
                foreach (Thread thread in workThreads) if (thread.IsAlive) thread.Interrupt();
            }
            rawResults = rawResults.Where(x => x.Platform == ResourcePlatforms.CurseForge).Concat(rawResults.Where(x => x.Platform != ResourcePlatforms.CurseForge)).ToList();
            rawResults = rawResults.Distinct((a, b) => a.IsLike(b)).ToList();
            rawResults = rawResults.Where(r => !realResults.Any(b => r.IsLike(b)) && !storage.Results.Any(b => r.IsLike(b))).ToList();
            realResults.AddRange(rawResults);
            host.Info($"去重、筛选后累计新增结果 {realResults.Count} 个（目前已有结果 {storage.Results.Count} 个）");
            if (realResults.Count + storage.Results.Count < request.TargetResultCount) {
                host.Info($"总结果数需求最少 {request.TargetResultCount} 个，仅获得了 {realResults.Count + storage.Results.Count} 个");
                if (request.CanContinue && !errors.Any()) { host.Info("将继续尝试加载下一页"); continue; }
                host.Info("无法继续加载，将强制结束");
            }
            break;
        }
        Dictionary<TProject, double> scores = new();
        double DownloadMultiplier(TProject project) => request.Type switch {
            ResourceTypes.Mod or ResourceTypes.ModPack => project.Platform == ResourcePlatforms.CurseForge ? 1 : 5,
            ResourceTypes.DataPack => project.Platform == ResourcePlatforms.CurseForge ? 10 : 1,
            ResourceTypes.ResourcePack or ResourceTypes.Shader => project.Platform == ResourcePlatforms.CurseForge ? 1 : 4,
            _ => 1
        };
        if (string.IsNullOrEmpty(rawSearchText)) {
            foreach (TProject result in realResults) scores[result] = result.DownloadCount * DownloadMultiplier(result);
        } else {
            List<ResourceSearchEntry<TProject>> entries = new();
            foreach (TProject result in realResults) {
                scores[result] = (result.WikiId > 0 ? 0.2 : 0) + Math.Log10(Math.Max(result.DownloadCount, 1) * DownloadMultiplier(result)) / 9;
                entries.Add(new ResourceSearchEntry<TProject> { Item = result, Sources = new() {
                    new ResourceSearchSource((isChineseSearch ? result.TranslatedName : result.RawName)!.Split('/', true), 1),
                    new ResourceSearchSource(result.Description!, 0.05)
                } });
            }
            var searchResults = ResourceSearchMatching.Search(entries, rawSearchText, 10000, -1);
            foreach (var result in searchResults) scores[result.Item] += (result.AbsoluteRight ? 10 : result.Similarity) / (searchResults.First().AbsoluteRight ? 10 : searchResults.First().Similarity);
        }
        if (task.IsCanceled()) throw new OperationCanceledException();
        storage.Results.AddRange(scores.OrderByDescending(s => s.Value).Select(r => r.Key));
    }
}
