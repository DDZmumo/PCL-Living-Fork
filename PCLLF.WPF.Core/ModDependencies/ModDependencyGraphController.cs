// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace PCLCS;

// LF Controls/ModDependencyGraphView.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// blob 4a112a072c8cd30cfba21d272167f97c6bf5ddab, under the repository LICENCE.
// Absent from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs/GetLogo was
// checked as C# reference (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); there is no
// equivalent graph view. No CE implementation or graph policy is copied.
/// <summary>LF's complete read-only dependency graph and native WPF view.</summary>
public partial class ModDependencyGraphController<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TNode>
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
    where TNode : ModDependencyGraphNodeData, new() {
    public readonly Canvas GraphCanvas = new() { Width = 708, Background = Brushes.Transparent };
    private const double NodeWidth = 184, RowGap = 10;
    private static readonly SemaphoreSlim IconSlots = new(3);
    private readonly ScrollViewer graphScroll = new();
    private readonly Dictionary<Border, Image> iconImages = new();
    private readonly HashSet<Border> pendingIcons = new();
    private readonly Dictionary<string, ImageSource?> iconCache = new(StringComparer.Ordinal);
    private readonly ModDependencyGraphHost host;
    private readonly Grid view;
    private int iconGeneration;
    public double PreferredWidth => GraphCanvas.Width + 84;
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    private static string FilePathKey(string path) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FilePathKey(path);
    private static bool SameFilePath(string first, string second) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.SameFilePath(first, second);
    private static ModDependencyFileProviderKind FileProviderRelation(TReport report, TEdge edge, string path) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FileProviderRelation(report, edge, path);
    private static bool FileSatisfiesDependency(TReport report, string path, TDependency requirement) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FileSatisfiesDependency(report, path, requirement);
    private static bool Provides(TMod item, string id) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.Identities(item).ContainsKey(id);

    private static string RuntimeOwnerId(string? id, TContext context) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RuntimeOwnerId(id!, context);
    private static string RuntimeDisplayName(string id) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RuntimeDisplayName(id);
    private static bool IsInternalLoaderId(string? id) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IsInternalLoaderId(id!);
    private static string RequirementDisplay(TDependency requirement, TContext context) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RequirementDisplay(requirement, context);

    public ModDependencyGraphController(Grid view, TReport report, string fullPath, ModDependencyGraphHost host) {
        this.host = host; this.view = view;
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        graphScroll.Content = GraphCanvas;
        graphScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        graphScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        graphScroll.HorizontalContentAlignment = HorizontalAlignment.Center;
        view.Children.Add(graphScroll);
        graphScroll.ScrollChanged += (_, _) => RefreshVisibleIcons();
        view.Loaded += (_, _) => RefreshVisibleIcons();
        view.Unloaded += (_, _) => iconGeneration = checked(iconGeneration + 1);
        List<TMod> focus = report.Mods.Where(item => SameFilePath(item.RootPath, fullPath)).ToList();
        TMod? primary = focus.FirstOrDefault(item => !item.IsNested) ?? focus.FirstOrDefault();
        List<TOnlineFile> platformFiles = report.OnlineFiles.Where(item => SameFilePath(item.FullPath, fullPath)).ToList();
        bool hasPlatform = platformFiles.Any(item => item.Matches.Any(match => match.IsExact));
        bool useLocal = !report.OnlineAttempted || report.OnlineTimedOut || !hasPlatform;
        HashSet<string> exactFiles = new(report.OnlineFiles.Where(item => item.Matches.Any(match => match.IsExact)).Select(item => FilePathKey(item.FullPath)), StringComparer.OrdinalIgnoreCase);
        List<TOnlineDependency> platformRequirements = platformFiles.SelectMany(item => item.Matches).Where(item => item.IsExact)
            .SelectMany(item => item.Dependencies).Where(item => item.Kind == "required").ToList();
        HashSet<string> platformPaths = new(platformRequirements.SelectMany(item => item.EnabledPaths).Select(FilePathKey), StringComparer.OrdinalIgnoreCase);
        var incoming = report.Edges.Where(edge => useLocal && edge.Requirement.KindValue == ModDependencyKind.Required && !exactFiles.Contains(FilePathKey(edge.Owner.RootPath)))
            .Select(edge => new { Edge = edge, Relation = FileProviderRelation(report, edge, fullPath) })
            .Where(item => item.Relation != ModDependencyFileProviderKind.None)
            .GroupBy(item => FilePathKey(item.Edge.Owner.RootPath), StringComparer.OrdinalIgnoreCase).ToList();
        List<TDependency> needed = focus.SelectMany(item => item.Dependencies).Where(item => item.KindValue == ModDependencyKind.Required).ToList();
        List<TNode> leftNodes = incoming.Select(group => {
            TMod container = ContainerMod(report, group.First().Edge.Owner);
            bool possible = !group.Any(item => item.Relation == ModDependencyFileProviderKind.Required);
            return new TNode { Title = container.Name, Condition = (possible ? "候选 · " : "本地参考 · ") + container.Version,
                IsCandidate = possible, FileName = container.RootPath, IconPath = container.IconPath,
                Details = string.Join("\r\n", group.Select(item => item.Edge.Requirement.Display).Distinct()) };
        }).ToList();
        foreach (TOnlineFile file in report.OnlineFiles.Where(item => item.Enabled && !SameFilePath(item.FullPath, fullPath))) {
            List<TOnlineDependency> required = file.Matches.Where(item => item.IsExact).SelectMany(item => item.Dependencies)
                .Where(item => item.Kind == "required" && item.EnabledPaths.Concat(item.ProjectPaths).Any(path => SameFilePath(path, fullPath))).ToList();
            if (required.Count == 0) continue;
            TMod? owner = report.Mods.FirstOrDefault(item => !item.IsNested && SameFilePath(item.RootPath, file.FullPath));
            bool versionReview = required.Any(item => item.ProjectPaths.Count > 0 && item.MatchedPaths.Count == 0 && !string.IsNullOrEmpty(item.VersionId));
            leftNodes.Add(new TNode { Title = owner?.Name ?? IOPath.GetFileName(file.FullPath), Condition = "平台声明 · " + (owner?.Version ?? "") + (versionReview ? " · 指定版本待核对" : ""), IsCandidate = versionReview,
                FileName = file.FullPath, IconPath = owner?.IconPath, IsPlatformRequirement = true,
                Details = string.Join("\r\n", required.Select(item => item.Platform + " 必需前置：" + (item.ProjectName ?? item.ProjectId) + "\r\n" + item.Status).Distinct()) });
        }
        List<TNode> rightNodes = needed.Select(requirement => {
            TMod owner = focus.First(item => item.Dependencies.Contains(requirement));
            TEdge? edge = report.Edges.FirstOrDefault(item => ReferenceEquals(item.Owner, owner) && ReferenceEquals(item.Requirement, requirement));
            if (edge is not null && (!edge.Evaluated || edge.ConditionBypassed)) return null;
            if (FileSatisfiesDependency(report, fullPath, requirement)) return null;
            List<TMod> providers = edge is null ? report.Mods.Where(item => item.Enabled && requirement.Id is not null && Provides(item, requirement.Id)).ToList() : edge.Providers;
            if (edge is null && providers.Any(item => !item.IsNested)) providers = providers.Where(item => !item.IsNested).ToList();
            List<TMod> preferred = providers.Where(item => !item.IsBuiltin && platformPaths.Contains(FilePathKey(item.RootPath))).ToList();
            if (requirement.Children.Count == 0 && preferred.Count > 0 && preferred.All(item => SameFilePath(item.RootPath, preferred[0].RootPath))) providers = preferred;
            TMod? provider = providers.FirstOrDefault();
            if (edge is not null && edge.Providers.Count > 0 && edge.Providers.All(item => !item.IsBuiltin && SameFilePath(item.RootPath, fullPath))) return null;
            string? title = provider?.Name ?? requirement.Id ?? requirement.Display;
            string identity = requirement.Children.Count == 0 ? "mod:" + requirement.Id : requirement.Syntax + ":" + requirement.Display;
            string? condition = RangeText(requirement);
            string? iconFile = null, iconPath = null;
            if (provider is not null && !provider.IsBuiltin && providers.All(item => !item.IsBuiltin && SameFilePath(item.RootPath, provider.RootPath))) {
                TMod container = ContainerMod(report, provider);
                title = container.Name; identity = "jar:" + FilePathKey(container.RootPath).ToUpperInvariant(); condition = container.Version;
                iconFile = container.RootPath; iconPath = container.IconPath;
            } else if (provider is not null && !provider.IsBuiltin) {
                title = string.Join(" / ", providers.Select(item => item.IsBuiltin ? item.Name : ContainerMod(report, item).Name).Distinct(StringComparer.Ordinal));
                identity = "choice:" + requirement.Syntax + ":" + requirement.Display; condition = "提供方待确认";
            }
            string runtimeOwner = RuntimeOwnerId(requirement.Id, report.Context);
            if (runtimeOwner != "") {
                title = RuntimeDisplayName(runtimeOwner); identity = "builtin:" + runtimeOwner;
                if (IsInternalLoaderId(requirement.Id)) condition = "加载器内置支持";
            }
            switch (requirement.Id) { case "fabricloader": title = "Fabric Loader"; break; case "minecraft": title = "Minecraft"; break; case "java": title = "Java"; break; }
            return new TNode { Identity = identity, Title = title, Condition = condition, Details = "本地声明：" + RequirementDisplay(requirement, report.Context),
                IsCandidate = identity.StartsWith("choice:", StringComparison.Ordinal), FileName = iconFile, IconPath = iconPath };
        }).Where(item => item is not null).Select(item => item!).GroupBy(item => item.Identity, StringComparer.Ordinal)
            .Select(group => new TNode { Identity = group.Key, Title = group.First().Title,
                Condition = string.Join(" 且 ", group.Select(item => item.Condition).Where(value => !SameText(value, "")).Distinct(StringComparer.Ordinal)),
                Details = string.Join("\r\n", group.Select(item => item.Details).Distinct(StringComparer.Ordinal)),
                IsCandidate = group.First().IsCandidate, FileName = group.First().FileName, IconPath = group.First().IconPath }).ToList();
        if (!useLocal) rightNodes.Clear();
        List<TNode> embeddedNodes = ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EmbeddedModsForDisplay(report, fullPath).Select(item => new TNode {
            Identity = "nested:" + item.Key, Title = item.Name, Condition = item.Version, IsEmbedded = true,
            Details = "随 " + (primary?.Name ?? IOPath.GetFileName(fullPath)) + " 内嵌提供。版本是否兼容仍按各模组的依赖声明检查。" }).ToList();
        foreach (TOnlineDependency requirement in platformRequirements) {
            TMod? local = report.Mods.FirstOrDefault(item => !item.IsNested && requirement.EnabledPaths.Any(path => SameFilePath(path, item.RootPath)));
            local ??= report.Mods.FirstOrDefault(item => !item.IsNested && requirement.MatchedPaths.Any(path => SameFilePath(path, item.RootPath)));
            local ??= report.Mods.FirstOrDefault(item => !item.IsNested && requirement.ProjectPaths.Any(path => SameFilePath(path, item.RootPath)));
            bool versionReview = requirement.ProjectPaths.Count > 0 && requirement.MatchedPaths.Count == 0 && !string.IsNullOrEmpty(requirement.VersionId);
            List<TMod> providers = ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RuntimeProvidersForPlatformDependency(report, fullPath, requirement);
            if (providers.Count > 0 && providers.All(item=>!item.IsNested && SameFilePath(item.RootPath,providers[0].RootPath))) local=providers[0];
            List<TMod> nested = providers.Where(item => item.IsNested).ToList();
            string? runtimeText = null;
            if (providers.Count > 0 && nested.Count == providers.Count && providers.All(item => SameFilePath(item.RootPath, providers[0].RootPath))) {
                string names = string.Join("、", providers.Select(item => item.Name + " " + item.Version).Distinct());
                if (SameFilePath(providers[0].RootPath, fullPath)) continue;
                local = ContainerMod(report, providers[0]); runtimeText = "内嵌提供 " + names + " · 已满足";
            }
            if (local is not null && SameFilePath(local.RootPath, fullPath)) continue;
            string platformKey = !string.IsNullOrEmpty(requirement.ProjectId) ? requirement.ProjectId : !string.IsNullOrEmpty(requirement.VersionId) ? "version:" + requirement.VersionId : "file:" + requirement.DisplayName;
            string key = local is null ? "platform:" + requirement.Platform + ":" + platformKey : "jar:" + FilePathKey(local.RootPath).ToUpperInvariant();
            TNode? existing = rightNodes.FirstOrDefault(item => item.Identity == key);
            string detail = requirement.Platform + " 精确版本：必需前置 " + requirement.DisplayName + " [" + requirement.ProjectId + "]" +
                (string.IsNullOrEmpty(requirement.VersionId) ? "" : "；指定平台版本 " + (string.IsNullOrEmpty(requirement.VersionName) ? "" : requirement.VersionName + " ") + "[" + requirement.VersionId + "]");
            if (existing is not null) {
                existing.IsPlatformRequirement = true;
                existing.NeedsAttention = existing.NeedsAttention || (local is not null && !local.Enabled);
                existing.NeedsVersionReview = existing.NeedsVersionReview || versionReview;
                existing.IsCandidate = existing.IsCandidate || local is null || versionReview;
                if (runtimeText is not null && !existing.Condition!.Contains(runtimeText)) existing.Condition += "\r\n" + runtimeText;
                existing.Details += "\r\n" + detail + "\r\n" + (runtimeText ?? requirement.Status);
            } else rightNodes.Add(new TNode { Identity = key, Title = local?.Name ?? requirement.DisplayName,
                Condition = runtimeText ?? (local is null ? "安装状态待确认" : local.Enabled ? local.Version : "已禁用 · " + local.Version),
                Details = detail + "\r\n" + (runtimeText ?? requirement.Status), NeedsAttention = local is not null && !local.Enabled,
                NeedsVersionReview = versionReview, IsCandidate = local is null || versionReview, IsPlatformRequirement = true,
                FileName = local?.RootPath, IconPath = local?.IconPath });
        }
        foreach (TNode node in rightNodes.Where(item => item.IsPlatformRequirement)) node.Condition = "平台前置 · " + node.Condition + (node.NeedsVersionReview ? " · 指定版本待核对" : "");
        foreach (TNode node in rightNodes.Where(item => !item.IsPlatformRequirement)) node.Condition = "本地参考 · " + node.Condition;
        int neededCount = rightNodes.Count;
        List<TEdge> conflicts = report.Edges.Where(edge => useLocal && edge.Evaluated && !edge.ConditionBypassed && edge.StateValue == ModDependencyMatchState.Satisfied &&
            (edge.Requirement.KindValue == ModDependencyKind.Incompatible || edge.Requirement.KindValue == ModDependencyKind.Conflict) &&
            (focus.Contains(edge.Owner) || edge.Providers.Any(provider => !provider.IsBuiltin && SameFilePath(provider.RootPath, fullPath)))).ToList();
        List<TNode> conflictNodes = new();
        foreach (TEdge edge in conflicts) {
            if (focus.Contains(edge.Owner)) {
                foreach (TMod provider in edge.Providers.Where(item => item.IsBuiltin || !SameFilePath(item.RootPath, fullPath))) {
                    TMod container = provider.IsBuiltin ? provider : ContainerMod(report, provider);
                    conflictNodes.Add(new TNode { Identity = provider.IsBuiltin ? "builtin:" + (RuntimeOwnerId(provider.Id, report.Context) == "" ? provider.Id : RuntimeOwnerId(provider.Id, report.Context)) : "jar:" + FilePathKey(provider.RootPath).ToUpperInvariant(),
                        Title = provider.IsBuiltin && RuntimeOwnerId(provider.Id, report.Context) != "" ? RuntimeDisplayName(RuntimeOwnerId(provider.Id, report.Context)) : container.Name,
                        Condition = provider.IsBuiltin && IsInternalLoaderId(provider.Id) ? "加载器内置支持" : container.Version, Details = RequirementDisplay(edge.Requirement, report.Context), IsConflict = true,
                        FileName = provider.IsBuiltin ? null : container.RootPath, IconPath = container.IconPath });
                }
            } else {
                TMod container = ContainerMod(report, edge.Owner);
                conflictNodes.Add(new TNode { Identity = "jar:" + FilePathKey(container.RootPath).ToUpperInvariant(), Title = container.Name, Condition = container.Version,
                    Details = edge.Requirement.Display, IsConflict = true, FileName = container.RootPath, IconPath = container.IconPath });
            }
        }
        foreach (TNode node in conflictNodes) node.Condition = "本地参考 · " + node.Condition;
        foreach (TOnlineFile file in report.OnlineFiles.Where(item => item.Enabled)) {
            bool isOwner = SameFilePath(file.FullPath, fullPath);
            foreach (TOnlineDependency requirement in file.Matches.Where(item => item.IsExact).SelectMany(item => item.Dependencies).Where(item => item.Kind == "incompatible")) {
                IEnumerable<string> targets = isOwner ? requirement.EnabledPaths.AsEnumerable() : requirement.EnabledPaths.Any(path => SameFilePath(path, fullPath)) ? new[] { file.FullPath } : Enumerable.Empty<string>();
                foreach (string targetPath in targets.Where(path => !SameFilePath(path, fullPath))) {
                    TMod? local = report.Mods.FirstOrDefault(item => !item.IsNested && SameFilePath(item.RootPath, targetPath));
                    conflictNodes.Add(new TNode { Identity = "jar:" + FilePathKey(targetPath).ToUpperInvariant(), Title = local?.Name ?? IOPath.GetFileName(targetPath),
                        Condition = "平台冲突 · " + (local?.Version ?? ""), Details = requirement.Platform + " 精确版本冲突声明", IsConflict = true, IsPlatformRequirement = true,
                        FileName = targetPath, IconPath = local?.IconPath });
                }
            }
        }
        conflictNodes = conflictNodes.GroupBy(item => item.Identity, StringComparer.Ordinal).Select(group => new TNode {
            Identity = group.Key, Title = group.First().Title, IsConflict = true,
            Condition = string.Join("；", group.Where(item => item.IsPlatformRequirement == group.Any(value => value.IsPlatformRequirement)).Select(item => item.Condition).Distinct(StringComparer.Ordinal)),
            Details = string.Join("\r\n", group.Select(item => item.Details).Distinct(StringComparer.Ordinal)),
            FileName = group.First().FileName, IconPath = group.First().IconPath }).ToList();
        RenderGraph(report, leftNodes, rightNodes, embeddedNodes, conflictNodes, primary, fullPath, useLocal, hasPlatform, neededCount);
    }

    private static TMod ContainerMod(TReport report, TMod item) => report.Mods.FirstOrDefault(candidate => !candidate.IsNested && SameFilePath(candidate.RootPath, item.RootPath)) ?? item;
    private static string? RangeText(TDependency requirement) {
        if (requirement.Children.Count > 0) return requirement.Display;
        string? range = requirement.Versions?.ToString();
        return string.IsNullOrWhiteSpace(range) || range == "*" ? "" : range;
    }
}
