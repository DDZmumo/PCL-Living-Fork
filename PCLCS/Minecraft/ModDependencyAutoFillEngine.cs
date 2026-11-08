// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF ModDependencyAutoFill.vb at 50b028da5fcad2226d74d901a41d8c43a249bfb2 and
// original LaunchDependencyCheck.vb, under the MPL-2.0 for PCLLF-original code.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 has neither module.
// Reviewed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/Modules/Minecraft/ModCompDependency.cs and
// PCL.Core/Minecraft/ResourceProject/ModDependencyResolver.cs were reviewed.
// ModCompDependency uses Plain Craft Launcher 2/LICENCE; PCL.Core uses root LICENSE.
// Their download confirmation and project selection do not implement LF's local
// declaration recheck, acceptance stamp, disabled-JAR activation or transaction policy.
public sealed partial class ModDependencyAutoFillEngine<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TVersion, TNetFile, TItem, TPlan, TActivation, TOptions, TCache>
    where TContext : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ContextData, new()
    where TMod : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ModData, new()
    where TDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DependencyData, new()
    where TEdge : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EdgeData, new()
    where TIssue : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IssueData, new()
    where TImpact : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ImpactData, new()
    where TReport : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ReportData, new()
    where TOnlineProject : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineProjectData, new()
    where TOnlineDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineDependencyData, new()
    where TOnlineFile : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineFileData, new()
    where TVersion : class where TNetFile : class
    where TItem : ModDependencyAutoFillItemData<TVersion, TItem>, new()
    where TPlan : ModDependencyAutoFillPlanData<TReport, TItem, TActivation>, new()
    where TActivation : DependencyActivationItemData<TMod>, new()
    where TOptions : ModDependencyLookupOptions<TCache>, new()
    where TCache : ModDependencyLookupCache, new() {
    private readonly ModDependencyAutoFillHost<TReport, TVersion, TNetFile, TPlan, TOptions> host;
    private readonly ConcurrentDictionary<string, Tuple<DateTime, JToken>> cached = new();
    public ModDependencyAutoFillEngine(ModDependencyAutoFillHost<TReport, TVersion, TNetFile, TPlan, TOptions> host) { this.host = host; }
    private sealed class Requirement {
        internal string Platform = null!, Project = null!, Version = null!, Owner = null!;
    }
    private static string S(JToken? value) => value is null || value.Type == JTokenType.Null ? "" : value.ToString();
    private static IEnumerable<JToken> A(JToken? value) => value as JArray ?? new JArray();
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    private static string Key(string? platform, string? project) => platform + "|" + project;
    private static bool ValidId(string? platform, string? id) => Regex.IsMatch(id ?? "", SameText(platform, "CurseForge") ? @"\A[1-9][0-9]{0,12}\z" : @"\A[A-Za-z0-9_-]{1,64}\z");
    private static ModLoaders Loader(TContext context) {
        if (context.LoaderVersions.ContainsKey("quilt_loader")) return ModLoaders.Fabric;
        if (context.LoaderVersions.ContainsKey("neoforge")) return ModLoaders.NeoForge;
        if (context.LoaderVersions.ContainsKey("forge")) return ModLoaders.Forge;
        if (context.LoaderVersions.ContainsKey("fabricloader")) return ModLoaders.Fabric;
        if (context.LoaderVersions.ContainsKey("liteloader")) return ModLoaders.LiteLoader;
        return ModLoaders.None;
    }
    private bool Compatible(TVersion? version, TContext context) {
        if (version is null || host.ResourceType(version) != ResourceTypes.Mod || !host.DownloadAvailable(version) || host.Size(version) <= 0 ||
            host.GameVersions(version) is null || !host.GameVersions(version)!.Contains(context.MinecraftVersion!, StringComparer.OrdinalIgnoreCase) ||
            Loader(context) == ModLoaders.None || !host.ModLoaders(version).HasFlag(Loader(context))) return false;
        if (!Regex.IsMatch(host.Hash(version) ?? "", @"\A(?:[0-9a-f]{40}|[0-9a-f]{32})\z")) return false;
        try {
            host.SafeName(host.FileName(version)!);
            if (!host.FileName(version)!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
            host.SetDownloadUrls(version, host.DownloadUrls(version).Where(value => {
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)) return false;
                Uri address = parsed!;
                return address.Scheme == "https" && address.UserInfo == "" && address.IsDefaultPort &&
                    (address.Host == "cdn.modrinth.com" || address.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase));
            }).Distinct().ToList());
            return host.DownloadUrls(version).Count > 0;
        } catch { return false; }
    }
    private TVersion? ParseVersion(JObject raw, string platform) {
        if (SameText(platform, "CurseForge")) {
            if (!SameText(S(raw["gameId"]), "432") || SameText(S(raw["downloadUrl"]), "") || SameText(S(raw["isAvailable"]), "False")) return null;
        } else {
            List<JObject> files = A(raw["files"]).OfType<JObject>().ToList();
            JObject? primary = files.FirstOrDefault(value => value["primary"]?.ToObject<bool>() == true);
            if (primary is null && files.Count == 1) primary = files[0];
            if (primary is null) return null;
            raw = (JObject)raw.DeepClone(); raw["files"] = new JArray(primary.DeepClone());
        }
        TVersion version = host.ParseVersion(raw);
        if (A(raw[SameText(platform, "Modrinth") ? "loaders" : "gameVersions"]).Any(value => S(value).Equals("quilt", StringComparison.OrdinalIgnoreCase)))
            host.SetModLoaders(version, host.ModLoaders(version) | ModLoaders.Fabric);
        return version;
    }
    private static IEnumerable<Requirement> Required(JObject raw, string platform, string owner) => A(raw["dependencies"])
        .Where(value => SameText(platform, "Modrinth") ? SameText(S(value["dependency_type"]), "required") : SameText(S(value["relationType"]), "3"))
        .Select(value => new Requirement { Platform = platform, Project = S(value[SameText(platform, "Modrinth") ? "project_id" : "modId"]),
            Version = SameText(platform, "Modrinth") ? S(value["version_id"]) : "", Owner = owner }).ToList();
    private static bool IsRange(JToken? value) {
        if (value is JArray array) return array.Count > 0 && array.All(IsRange);
        if (value is null || value.Type != JTokenType.String) return false;
        string text = value.ToString();
        return Regex.IsMatch(text, @"[<>~^*]|(?:^|\.)[xX](?:$|\.)") || (text.Contains(",") && (text.StartsWith("[") || text.StartsWith("(")));
    }
    public void ResetIndexLookups() => cached.Clear();
    private static DependencyReplacementData? FindAuthorReplacement(TReport report, string platform, string project) {
        PackDependencyOrigin? origin = report.PackOrigin;
        if (origin?.IsPack != true || !origin.Complete) return null;
        string identity = platform.ToLowerInvariant() + ":" + project;
        var files = origin.Files.Where(file => string.Equals(file.Identity, identity, StringComparison.Ordinal) &&
            !file.Disabled && file.LocalRelative is not null && file.ActualHash is not null &&
            file.Role is PackDependencyFileRole.AuthorUnchanged or PackDependencyFileRole.AuthorRenamed).ToArray();
        if (files.Length != 1) return null;
        PackDependencyFile declared = files[0];
        string source = Path.Combine(origin.GameRoot, declared.LocalRelative!.Replace('/', Path.DirectorySeparatorChar));
        bool personalMismatch = report.Issues.Any(issue => issue.KindValue == ModDependencyIssueKind.VersionMismatch &&
            issue.Requirement?.KindValue == ModDependencyKind.Required && origin.IsPersonal(issue.Owner.RootPath) &&
            issue.RelatedMods.Any(mod => SameFilePath(mod.RootPath, source)));
        if (!personalMismatch) return null;
        return new() { Source = source, BeforeHash = declared.ActualHash!, AuthorRelative = declared.Relative, ProjectIdentity = identity };
    }
    private static bool NeedsPersonalRepair(TReport report, string path) {
        bool ActiveOwner(TMod owner) => owner.Enabled && !owner.IsCandidate && SameFilePath(owner.RootPath, path);
        return report.Issues.Any(issue => ActiveOwner(issue.Owner) && issue.Requirement?.KindValue == ModDependencyKind.Required &&
                issue.KindValue is ModDependencyIssueKind.MissingRequired or ModDependencyIssueKind.VersionMismatch) ||
            report.Edges.Any(edge => ActiveOwner(edge.Owner) && edge.Requirement.KindValue == ModDependencyKind.Required &&
                (edge.Requirement.IsUnknown || edge.Evaluated && edge.StateValue != ModDependencyMatchState.Satisfied)) ||
            report.Mods.Where(ActiveOwner).Any(owner => owner.Dependencies.Any(requirement =>
                requirement.KindValue == ModDependencyKind.Required && requirement.IsUnknown && !(report.Context.IgnoreJavaRequirements && requirement.Id == "java"))) ||
            report.OnlineFiles.Where(file => file.Enabled && SameFilePath(file.FullPath, path)).Any(file => file.Matches.Where(match => match.IsExact).Any(match =>
                match.Dependencies.Any(dependency => dependency.Kind == "required" && dependency.EnabledPaths.Count == 0 &&
                    ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.RuntimeProvidersForPlatformDependency(report, file.FullPath, dependency).Count == 0)));
    }
    public TPlan BuildPlan(string root, TReport report, CancellationToken token, TOptions? options = null) {
        TPlan result = Prepare(root, report, token);
        PackDependencyOrigin? origin = report.PackOrigin?.IsPack == true ? report.PackOrigin : null;
        HashSet<string>? personalRepairRoots = null;
        if (origin is not null) {
            if (!origin.Complete) return result;
            // Freeze locally proven needs before platform enrichment can introduce
            // project-level declarations that the loaded combination already fulfils.
            TReport eligibility = result.Enables.Count == 0 ? report : WithEnabledFiles(report, result.Enables.Select(item => item.Source), token);
            if (!ReferenceEquals(eligibility, report)) eligibility.OnlineFiles.AddRange(report.OnlineFiles);
            personalRepairRoots = new(eligibility.Mods.Where(value => value.Enabled && !value.IsNested && origin.IsPersonal(value.RootPath) &&
                NeedsPersonalRepair(eligibility, value.RootPath)).Select(value => FilePathKey(value.RootPath)), StringComparer.Ordinal);
            if (personalRepairRoots.Count == 0) return result;
        }
        if (!report.Mods.Any(value => value.Enabled)) return result;
        if (report.Issues.Any(value => value.KindValue == ModDependencyIssueKind.LoaderMismatch)) {
            result.Items.Clear(); result.Enables.Clear();
            result.Notes.Add("请先在 Mod 管理中处理加载器不匹配的文件，再检查和补全依赖。"); return result;
        }
        if (report.Issues.Any(value => value.Requirement is not null && (value.KindValue == ModDependencyIssueKind.MissingRequired || value.KindValue == ModDependencyIssueKind.VersionMismatch) && ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.RuntimeOwnerId(value.Requirement.Id, report.Context) != "")) {
            result.Items.Clear(); result.Enables.Clear();
            result.Notes.Add("请先调整游戏、Java 或加载器版本；加载器的内置支持随加载器一起提供，不会作为独立 Mod 下载。"); return result;
        }
        bool legacy = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.IsLegacyForge(report.Context);
        if (legacy && !report.Issues.Any(value => value.KindValue == ModDependencyIssueKind.MissingRequired && value.IsError && value.Requirement is not null && !value.Requirement.IsUnknown)) {
            if (report.Issues.Any(value => value.KindValue == ModDependencyIssueKind.MissingRequired)) result.Notes.Add("旧版核心插件的运行时声明尚未确认，未自动选择可能重复的前置文件。");
            return result;
        }
        if (Loader(report.Context) == ModLoaders.None || string.IsNullOrEmpty(report.Context.MinecraftVersion)) {
            result.Notes.Add("尚未确认当前 Minecraft 版本或加载器，未选择自动补全文件。"); return result;
        }
        TOptions configuration = options ?? new TOptions { ComparePlatformDependencies = true, MaxFiles = 512, MaxSearchFiles = 0,
            MaxRequests = 40, TimeBudgetMs = 12000, MaxTotalBytes = 2L * 1024 * 1024 * 1024 };
        configuration.ComparePlatformDependencies = true; configuration.MaxSearchFiles = 0;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(20000);
        host.Enrich(report, root, budget.Token, configuration);
        HashSet<string> existing = new(report.OnlineFiles.Where(value => value.Enabled).SelectMany(value => value.Matches)
            .Where(value => value.IsExact).Select(value => Key(value.Platform, value.ProjectId)), StringComparer.Ordinal);
        HashSet<string> disabled = new(report.OnlineFiles.Where(value => !value.Enabled).SelectMany(value => value.Matches)
            .Where(value => value.IsExact).Select(value => Key(value.Platform, value.ProjectId)), StringComparer.Ordinal);
        Queue<Requirement> pending = new();
        HashSet<string> flexibleProjects = new(StringComparer.Ordinal);
        foreach (TOnlineFile file in report.OnlineFiles.Where(value => value.Enabled &&
            (personalRepairRoots is null || personalRepairRoots.Contains(FilePathKey(value.FullPath))))) {
            List<TOnlineProject> matches = file.Matches.Where(value => value.IsExact).OrderBy(value => SameText(value.Platform, "Modrinth") ? 0 : 1).ToList();
            if (matches.Count == 0) continue;
            TOnlineProject match = matches[0];
            List<TIssue> missing = report.Issues.Where(issue => issue.KindValue == ModDependencyIssueKind.MissingRequired && SameFilePath(issue.Owner.RootPath, file.FullPath)).ToList();
            if (legacy && !missing.Any(value => value.IsError)) continue;
            bool localRanges = missing.Count > 0 && missing.All(issue => issue.Requirement is not null && !issue.Requirement.IsUnknown && issue.Requirement.Children.Count == 0 && IsRange(issue.Requirement.Versions));
            foreach (TOnlineDependency dependency in match.Dependencies.Where(value => SameText(value.Kind, "required"))) {
                if (ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.RuntimeProvidersForPlatformDependency(report, file.FullPath, dependency).Count > 0) continue;
                TMod? owner = report.Mods.FirstOrDefault(value => !value.IsNested && SameFilePath(value.RootPath, file.FullPath));
                pending.Enqueue(new Requirement { Platform = dependency.Platform, Project = dependency.ProjectId, Version = dependency.VersionId, Owner = owner?.Name ?? match.ProjectName });
                if (localRanges || string.IsNullOrEmpty(dependency.VersionId)) flexibleProjects.Add(Key(dependency.Platform, dependency.ProjectId));
            }
        }
        Dictionary<string, TItem> selected = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        int requestCount = 0;
        JToken? Request(string url) {
            budget.Token.ThrowIfCancellationRequested();
            if (options is null && cached.TryGetValue(url, out Tuple<DateTime, JToken>? saved) && saved.Item1 > DateTime.UtcNow) return saved.Item2.DeepClone();
            requestCount = checked(requestCount + 1);
            if (requestCount > 64) throw new IOException("依赖项目过多，已停止继续查询。");
            JToken? data = (configuration.Request ?? host.DefaultRequest)(url, HttpMethod.Get, null, budget.Token);
            if (options is null && data is not null) {
                if (cached.Count > 256) cached.Clear();
                cached[url] = Tuple.Create(DateTime.UtcNow.AddMinutes(10), data.DeepClone());
            }
            return data;
        }
        try {
            while (pending.Count > 0 && visited.Count < 64) {
                budget.Token.ThrowIfCancellationRequested();
                Requirement need = pending.Dequeue();
                if (SameText(need.Platform, "Modrinth") && SameText(need.Project, "") && ValidId(need.Platform, need.Version))
                    need.Project = S(Request("https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(need.Version))?["project_id"]);
                if (!ValidId(need.Platform, need.Project)) continue;
                string identity = Key(need.Platform, need.Project);
                string? fixedVersion = flexibleProjects.Contains(identity) ? "" : need.Version;
                DependencyReplacementData? replacement = FindAuthorReplacement(report, need.Platform, need.Project);
                if (existing.Contains(identity) && replacement is null) continue;
                if (origin?.ProtectsProject(need.Platform, need.Project) == true && replacement is null) {
                    result.Notes.Add("前置属于作者组合，未找到可唯一验证的活动作者版本可替换：" + need.Project + "。请核对原包、可选组件或个人模组版本。");
                    continue;
                }
                if (disabled.Contains(identity)) {
                    List<TOnlineFile> disabledFiles = report.OnlineFiles.Where(value => !value.Enabled && value.Matches.Any(match => match.IsExact && SameText(match.Platform, need.Platform) && SameText(match.ProjectId, need.Project))).ToList();
                    if (disabledFiles.Any(value => result.Enables.Any(enableItem => SameFilePath(enableItem.Source, value.FullPath)))) continue;
                    TMod? owner = report.Mods.FirstOrDefault(value => !value.IsNested && disabledFiles.Any(file => SameFilePath(file.FullPath, value.RootPath)));
                    string? name = owner is not null && !SameText(owner.Name, owner.Id) ? owner.Name : null;
                    if (string.IsNullOrWhiteSpace(name)) name = disabledFiles.SelectMany(file => file.Matches).Select(value => value.ProjectName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !SameText(value, need.Project));
                    if (string.IsNullOrWhiteSpace(name)) name = owner?.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = disabledFiles.FirstOrDefault() is null ? "前置模组" : Path.GetFileName(disabledFiles[0].FullPath);
                    result.Notes.Add(name + " 已安装但被禁用，请在模组管理中选择兼容版本后启用。"); continue;
                }
                if (selected.TryGetValue(identity, out TItem? entry)) {
                    if (!SameText(fixedVersion, "") && !SameText(host.Id(entry.Version), fixedVersion)) throw new IOException("不同模组要求不同的固定依赖版本，需手动处理：" + entry.Name);
                    if (SameText(fixedVersion, "") && !SameText(need.Version, "") && !SameText(host.Id(entry.Version), need.Version))
                        result.Notes.Add(entry.Name + " 的平台记录指向较早文件；本次已合并未锁定的声明，安装前将核对各模组的实际版本要求。");
                    if (!entry.RequiredBy.Contains(need.Owner)) entry.RequiredBy.Add(need.Owner);
                    continue;
                }
                if (!visited.Add(identity)) continue;
                string projectUrl = (SameText(need.Platform, "Modrinth") ? "https://api.modrinth.com/v2/project/" : "https://api.curseforge.com/v1/mods/") + Uri.EscapeDataString(need.Project);
                JToken? project = Request(projectUrl);
                if (SameText(need.Platform, "CurseForge")) project = project?["data"];
                if (project is null || (SameText(need.Platform, "Modrinth") ? !SameText(S(project["project_type"]), "mod") : !SameText(S(project["classId"]), "6") || !SameText(S(project["gameId"]), "432"))) {
                    result.Notes.Add("无法确认依赖项目的模组类型：" + need.Project); continue;
                }
                if (SameText(need.Platform, "Modrinth") && SameText(S(project["client_side"]), "unsupported")) {
                    result.Notes.Add("此依赖仅支持服务端，未选择客户端安装：" + S(project["title"])); continue;
                }
                IEnumerable<JToken> rawFiles;
                if (SameText(need.Platform, "Modrinth")) {
                    if (!SameText(fixedVersion, "")) rawFiles = new[] { Request("https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(fixedVersion!))! };
                    else rawFiles = A(Request(projectUrl + "/version?game_versions=" + Uri.EscapeDataString(new JArray(report.Context.MinecraftVersion!).ToString(Formatting.None))));
                } else rawFiles = A(Request(projectUrl + "/files?gameVersion=" + Uri.EscapeDataString(report.Context.MinecraftVersion!) + "&pageSize=50")?["data"]);
                List<TItem> candidates = new();
                foreach (JObject raw in rawFiles.OfType<JObject>()) {
                    try {
                        if (!SameText(S(raw[SameText(need.Platform, "Modrinth") ? "project_id" : "modId"]), need.Project)) continue;
                        TVersion? version = ParseVersion(raw, need.Platform);
                        if (!Compatible(version, report.Context) || (!SameText(fixedVersion, "") && !SameText(host.Id(version!), fixedVersion))) continue;
                        candidates.Add(new TItem { Platform = need.Platform, ProjectId = need.Project, Name = S(project[SameText(need.Platform, "Modrinth") ? "title" : "name"]), Version = version!, Raw = raw });
                    } catch (Exception error) when (error is not OperationCanceledException) { }
                }
                entry = candidates.OrderBy(value => host.ReleaseType(value.Version)).ThenByDescending(value => host.ReleaseDate(value.Version)).FirstOrDefault();
                if (entry is null) { result.Notes.Add("没有找到匹配当前游戏版本和加载器的可下载前置：" + S(project[SameText(need.Platform, "Modrinth") ? "title" : "name"])); continue; }
                entry.Replacement = replacement;
                if (replacement is not null) result.Notes.Add("拟替换作者版本 " + Path.GetFileName(replacement.Source) + " → " + host.FileName(entry.Version) + "；须明确确认并通过整包完整依赖与冲突验证，保留作者原始基线。");
                if (replacement is null && SameText(fixedVersion, "")) entry.Alternatives.AddRange(candidates.Where(value => !ReferenceEquals(value, entry)).OrderBy(value => host.ReleaseType(value.Version)).ThenByDescending(value => host.ReleaseDate(value.Version)).Take(7));
                entry.RequiredBy.Add(need.Owner); selected.Add(identity, entry); result.Items.Add(entry);
                foreach (Requirement dependency in Required(entry.Raw, entry.Platform, entry.Name)) {
                    if (string.IsNullOrEmpty(dependency.Version)) flexibleProjects.Add(Key(dependency.Platform, dependency.Project));
                    pending.Enqueue(dependency);
                }
            }
            if (pending.Count > 0) throw new IOException("必需前置数量超过本次自动补全上限，请分批管理模组。");
        } catch (OperationCanceledException) {
            if (token.IsCancellationRequested) throw;
            result.Notes.Add("依赖查询超时，本次未准备自动安装。"); result.Items.Clear();
        } catch (Exception error) { result.Notes.Add("依赖查询未完成：" + error.Message); result.Items.Clear(); }
        return result;
    }
}
