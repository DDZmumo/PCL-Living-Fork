// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Inspection = PCLCS.ModDependencyInspection<PCLCS.PackUpdateModInspection.InspectionContext, PCLCS.PackUpdateModInspection.Mod, PCLCS.PackUpdateModInspection.Dependency, PCLCS.PackUpdateModInspection.Edge, PCLCS.PackUpdateModInspection.Issue, PCLCS.PackUpdateModInspection.Impact, PCLCS.PackUpdateModInspection.Report, PCLCS.PackUpdateModInspection.Project, PCLCS.PackUpdateModInspection.OnlineDependency, PCLCS.PackUpdateModInspection.OnlineFile>;

namespace PCLCS;

/// <summary>Loader declarations describe load identity, never platform ownership or author provenance.</summary>
public sealed class PackModInspection {
    public bool Confirmed, Enabled = true;
    public string? IconPath;
    public HashSet<string> OwnIds = new(StringComparer.Ordinal);
    public HashSet<string> ProvidedIds = new(StringComparer.Ordinal);
    public HashSet<string> NestedIds = new(StringComparer.Ordinal);
    public List<string> Warnings = new();
    public List<string> CompatibilityWarnings = new();
}

/// <summary>Read the same metadata as dependency checking, without a directory scan or index mutation.</summary>
public static class PackUpdateModInspection {
    public static PackModInspection Read(string path, string? minecraft, Dictionary<ModLoaders, string> loaders, CancellationToken token = default) {
        Dictionary<string, string?> names = new(StringComparer.Ordinal);
        foreach (var pair in loaders) {
            string? id = pair.Key switch {
                ModLoaders.Fabric => "fabricloader", ModLoaders.Forge => "forge",
                ModLoaders.NeoForge => "neoforge", ModLoaders.LiteLoader => "liteloader", _ => null
            };
            if (id is not null) names[id] = pair.Value;
        }
        return ReadForLoaderContext(path, minecraft, names, token);
    }

    // Quilt has no value in the existing pack-format ModLoaders enum. Keep the shared parser's
    // exact loader IDs available for inspection without inventing a new package-format mapping.
    public static PackModInspection ReadForLoaderContext(string path, string? minecraft,
        IReadOnlyDictionary<string, string?> loaders, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        PackModInspection result = new() {
            Enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
        };
        InspectionContext context = new() { MinecraftVersion = minecraft, LoaderKnown = loaders.Count > 0, IgnoreJavaRequirements = true };
        foreach (var pair in loaders) context.LoaderVersions[pair.Key] = pair.Value;
        if (loaders.ContainsKey("quilt_loader") && !context.LoaderVersions.ContainsKey("fabricloader")) context.LoaderVersions["fabricloader"] = null;
        if (loaders.ContainsKey("neoforge") && minecraft == "1.20.1") context.LoaderVersions["forge"] = loaders["neoforge"];
        Report report = Inspection.ReadSingleArchive(path, context, token);
        var own = report.Mods.Where(item => !item.IsNested && !item.IsMetadataOnly && !item.IsGeneratedLibrary).ToArray();
        result.IconPath = own.Select(item => item.IconPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var item in own) {
            result.OwnIds.Add(item.Id);
            result.ProvidedIds.UnionWith(item.Provides.Keys);
        }
        // Embedded IDs are selectable loader candidates. They are not the outer file's identity
        // and may coexist with a directly installed provider; never flatten them into OwnIds.
        result.NestedIds.UnionWith(report.Mods.Where(item => item.IsNested && !item.IsMetadataOnly).Select(item => item.Id));
        result.Confirmed = own.Length > 0 && !report.MetadataIncomplete && own.All(item => !item.IncompatibleLoader) &&
            own.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == own.Length;
        result.Warnings.AddRange(report.Notices);
        if (!result.Confirmed) result.Warnings.Add("模组加载身份无法完整确认；不会仅凭弱匹配判为重复或确定文件归属。");

        var runtimeIds = new HashSet<string>(new[] { "minecraft", "forge", "neoforge", "fabricloader", "quilt_loader", "liteloader", "fml", "javafml", "lowcodefml" }, StringComparer.Ordinal);
        foreach (var issue in report.Issues.Where(issue => issue.KindValue == ModDependencyIssueKind.LoaderMismatch ||
            (issue.Requirement is not null && RuntimeRequirement(issue.Requirement, runtimeIds)))) {
            if (issue.IsError && !issue.Owner.IsCandidate) result.CompatibilityWarnings.Add(issue.Message);
        }
        var runtimeEdges = report.Edges.Where(edge => !edge.Owner.IsCandidate && RuntimeRequirement(edge.Requirement, runtimeIds)).ToArray();
        if (string.IsNullOrWhiteSpace(minecraft) || !context.LoaderKnown || !result.Confirmed || runtimeEdges.Length == 0 ||
            runtimeEdges.Any(edge => edge.StateValue == ModDependencyMatchState.Unknown || edge.Requirement.IsUnknown)) {
            result.CompatibilityWarnings.Add("与目标 Minecraft／加载器的声明兼容性未能完整确认，需实机验证。");
        }
        // Even satisfied metadata is not an assertion about runtime behavior or undeclared constraints.
        if (result.CompatibilityWarnings.Count == 0) result.CompatibilityWarnings.Add("未发现已声明的 Minecraft／加载器约束冲突；实际运行兼容性尚未验证。");
        return result;
    }

    private static bool RuntimeRequirement(Dependency value, HashSet<string> ids) =>
        ids.Contains(value.Id ?? "") || (value.Children.Count > 0 && value.Children.All(child => RuntimeRequirement(child, ids)));

    internal sealed class InspectionContext : Inspection.ContextData { public InspectionContext() { } }
    internal sealed class Mod : Inspection.ModData { public Mod() { } }
    internal sealed class Dependency : Inspection.DependencyData { public Dependency() { } public override ModDependencyKind KindValue { get; set; } }
    internal sealed class Edge : Inspection.EdgeData { public Edge() { } public override ModDependencyMatchState StateValue { get; set; } }
    internal sealed class Issue : Inspection.IssueData { public Issue() { } public override ModDependencyIssueKind KindValue { get; set; } }
    internal sealed class Impact : Inspection.ImpactData { public Impact() { } }
    internal sealed class Report : Inspection.ReportData { public Report() { } }
    internal sealed class Project : Inspection.OnlineProjectData { public Project() { } }
    internal sealed class OnlineDependency : Inspection.OnlineDependencyData { public OnlineDependency() { } }
    internal sealed class OnlineFile : Inspection.OnlineFileData { public OnlineFile() { } }
}
