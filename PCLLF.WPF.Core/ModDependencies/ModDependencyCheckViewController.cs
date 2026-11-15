// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF Controls/ModDependencyCheckView.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// MPL-2.0 for PCLLF-original code. This LF-specific view is absent from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyLocalModItem.xaml.cs and
// ModCompDependency.cs were reviewed (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); their layout/policy is not copied.
public static class ModDependencyCheckViewController<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TVersion, TDownload, TEnable, TPlan, TCard>
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
    where TVersion : class
    where TDownload : ModDependencyAutoFillItemData<TVersion, TDownload>
    where TEnable : DependencyActivationItemData<TMod>
    where TPlan : ModDependencyAutoFillPlanData<TReport, TDownload, TEnable>, new()
    where TCard : FrameworkElement {
    public const double PreferredWidth = 740;

    public static TCard? Build(Grid view, StackPanel groups, TReport report, TPlan? plan,
        ModDependencyCheckViewHost<TVersion, TIssue, TCard> host) {
        view.Height = 430;
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        int files = report.Mods.Where(item => item.Enabled && !item.IsNested).Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        plan ??= new TPlan { Report = report };
        HashSet<string> covered = new(plan.Enables.SelectMany(item => item.ResolvedIssues), StringComparer.Ordinal);
        List<string> actions = new();
        if (plan.Enables.Count > 0) actions.Add(plan.Enables.Count + " 个前置已安装但禁用");
        if (plan.Items.Count > 0) actions.Add(plan.Items.Count + " 个必需前置待补全");
        if (report.PackOrigin?.NeedsAttention == true) actions.Add("整合包作者文件需要核对");
        if (report.PackOrigin?.IsPack == true && report.MetadataIncomplete) actions.Add("部分模组声明尚未确认");
        List<string> platformNotes = report.PlatformReviewNotes();
        if (platformNotes.Count > 0) actions.Add(platformNotes.Count + " 项平台声明需要核对");
        if (actions.Count == 0) {
            int environmentCount = report.Issues.Where(item => item.IsError && item.Requirement is not null).Select(item => RuntimeOwnerId(item.Requirement!.Id, report.Context)).Where(id => id != "").Distinct().Count();
            int modCount = report.Issues.Count(item => item.IsError && (item.Requirement is null || RuntimeOwnerId(item.Requirement.Id, report.Context) == ""));
            if (environmentCount > 0) actions.Add(environmentCount + " 项运行环境要求未满足");
            if (modCount > 0 || environmentCount == 0) actions.Add(modCount + " 项模组问题需要处理");
        }
        TextBlock summary = new() { Text = "已检查 " + files + " 个启用文件 · " + string.Join(" · ", actions),
            Margin = new Thickness(3, 0, 3, 12), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        view.Children.Add(summary);
        ScrollViewer scroll = host.CreateScroll(groups);
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(scroll, 1); view.Children.Add(scroll);
        if (plan.Items.Count == 0 && plan.Enables.Count == 0 && report.OnlineAttempted && report.HasErrors) {
            string reason = plan.Notes.LastOrDefault() ?? report.OnlineNotes.LastOrDefault() ?? "尚未匹配到可用的前置下载信息，请检查下载源连接后重试。";
            groups.Children.Add(host.CreateWarningHint("暂不能自动补全：" + reason, new Thickness(3, 3, 7, 10)));
        }
        if (plan.Enables.Count > 0) {
            List<Tuple<string?, string?, TMod?>> entries = plan.Enables.Select(item => Tuple.Create<string?, string?, TMod?>(item.Name + "（已安装，当前禁用）",
                "这些模组需要它：" + Referrers(item.RequiredBy) + "\r\n点击「启用并启动」即可恢复使用。", item.Owner)).ToList();
            AddGroup(groups, "可启用的必需前置（" + entries.Count + " 个文件）", entries, false, host);
        }
        if (report.PackOrigin?.IsPack == true) {
            PackDependencyOrigin origin = report.PackOrigin;
            groups.Children.Add(host.CreateWarningHint(origin.Complete ?
                "已识别完整作者基线。优先保留整合包组合；个人模组使用已有前置，必要的作者版本替换须确认并通过完整依赖与冲突检查。作者清单不等于兼容性保证。" :
                "整合包作者记录缺失、不完整或无法确认。本次仅检查和报告，不自动添加、启用或替换模组；请使用正确的原始整合包补录记录。", new Thickness(3, 3, 7, 10)));
            var differences = origin.Files.Where(file => file.Role is not (PackDependencyFileRole.AuthorUnchanged or PackDependencyFileRole.AuthorRenamed or PackDependencyFileRole.PersonalAdded)).ToList();
            if (differences.Count > 0) AddGroup(groups, "作者文件与本地状态（" + differences.Count + "）", differences.Select(file =>
                Tuple.Create<string?, string?, TMod?>(file.LocalRelative ?? file.Relative, OriginState(file.Role), null)).ToList(), true, host, true);
            foreach (string note in origin.Notes.Distinct(StringComparer.Ordinal))
                groups.Children.Add(host.CreateWarningHint(note, new Thickness(3, 3, 7, 10)));
        }
        var replacements = plan.Items.Where(item => item.Replacement is not null).ToList();
        if (replacements.Count > 0) AddGroup(groups, "需确认的作者模组版本替换（" + replacements.Count + "）", replacements.Select(item =>
            Tuple.Create<string?, string?, TMod?>(item.Name, "原文件：" + System.IO.Path.GetFileName(item.Replacement!.Source) +
                "\r\n候选：" + host.VersionDisplay(item.Version) + "\r\n验证整合包与个人模组的完整组合后再替换；存在冲突或无法确认时不会执行。原作者基线保留，替换记录为个人覆盖。", null)).ToList(), true, host, true);
        if (plan.Items.Any(item => item.Replacement is null)) {
            List<Tuple<string?, string?, TMod?>> entries = plan.Items.Where(item => item.Replacement is null).Select(item => Tuple.Create<string?, string?, TMod?>(item.Name,
                "候选：" + host.VersionDisplay(item.Version) + "\r\n需要它：" + Referrers(item.RequiredBy) +
                (item.Alternatives.Count > 0 ? "\r\n将校验实际兼容性，必要时尝试较早版本" : ""), null)).ToList();
            AddGroup(groups, "可补全的必需前置（" + entries.Count + "）", entries, false, host);
        }
        List<TIssue> runtimeIssues = report.Issues.Where(item => item.Requirement is not null &&
            (item.KindValue == ModDependencyIssueKind.MissingRequired || item.KindValue == ModDependencyIssueKind.VersionMismatch) &&
            RuntimeOwnerId(item.Requirement.Id, report.Context) != "").ToList();
        if (runtimeIssues.Count > 0) {
            List<Tuple<string?, string?, TMod?>> entries = runtimeIssues.GroupBy(item => RuntimeOwnerId(item.Requirement!.Id, report.Context)).Select(group => {
                string name = RuntimeDisplayName(group.Key);
                string text = "相关模组：" + string.Join("、", group.Select(item => item.Owner.Name).Distinct());
                if (group.Any(item => IsInternalLoaderId(item.Requirement!.Id))) text += "\r\n" + name + " 内置支持不满足这些模组的要求。";
                IEnumerable<string> direct = group.Where(item => !IsInternalLoaderId(item.Requirement!.Id)).Select(item => RequirementDisplay(item.Requirement!, report.Context)).Distinct();
                if (direct.Any()) text += "\r\n需要：" + string.Join("；", direct);
                text += "\r\n" + (group.Key == "java" ? "处理：在版本设置中选择符合要求的 Java。" : group.Key == "minecraft" ? "处理：选择匹配的 Minecraft 版本，或更换适用的模组。" : "处理：在版本设置中更新或重装 " + name + "，或更换适用的模组。");
                return Tuple.Create<string?, string?, TMod?>(name, text, null);
            }).ToList();
            AddGroup(groups, "运行环境要求不满足（" + entries.Count + "）", entries, true, host, true);
        }
        foreach (ModDependencyIssueKind kind in new[] { ModDependencyIssueKind.LoaderMismatch, ModDependencyIssueKind.MissingRequired, ModDependencyIssueKind.VersionMismatch, ModDependencyIssueKind.DuplicateId, ModDependencyIssueKind.Incompatible }) {
            List<TIssue> issues = report.Issues.Where(item => item.KindValue == kind && !runtimeIssues.Contains(item) && !covered.Contains(host.IssueKey(item))).ToList();
            if (issues.Count == 0) continue;
            string title = kind == ModDependencyIssueKind.LoaderMismatch ? "加载器不匹配" : kind == ModDependencyIssueKind.MissingRequired ? "缺少依赖" : kind == ModDependencyIssueKind.VersionMismatch ? "版本不匹配" : kind == ModDependencyIssueKind.DuplicateId ? "重复模组" : "模组冲突";
            List<Tuple<string?, string?, TMod?>> entries = issues.GroupBy(item => (item.Owner?.RootPath ?? "") + "|" + (item.Owner?.Id ?? "")).Select(group => {
                TMod? owner = group.First().Owner;
                return Tuple.Create<string?, string?, TMod?>(owner?.Name ?? owner?.Id ?? "模组问题", string.Join("\r\n", group.Select(Describe)), owner);
            }).ToList();
            AddGroup(groups, title + "（" + issues.Count + "）", entries, true, host);
        }
        TCard? details = null;
        if (platformNotes.Count > 0) AddGroup(groups, "平台声明与本地状态（" + platformNotes.Count + "）",
            platformNotes.Select(note => Tuple.Create<string?, string?, TMod?>("平台声明待核对", note, null)).ToList(), true, host, true);
        List<string> notes = report.Notices.Concat(plan.Notes ?? new List<string>()).Distinct().ToList();
        if (notes.Count > 0 || report.MetadataIncomplete || report.OnlineNotes.Count > 0) {
            notes.AddRange(report.OnlineNotes);
            if (report.MetadataIncomplete) notes.Insert(0, "部分模组声明未读完整，无法确认的项目不会自动安装。");
            details = host.CreateCard("检查说明（" + notes.Count + "）", new Thickness(3, 3, 7, 8));
            TextBlock text = new() { Text = string.Join("\r\n\r\n", notes), Margin = new Thickness(15, 40, 15, 15), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3");
            host.CollapseDetails(details, text);
            host.EnsureCaption(details);
            groups.Children.Add(details);
        }
        groups.Measure(new Size(PreferredWidth - 70, double.PositiveInfinity));
        view.Height = Math.Min(430, Math.Max(110, groups.DesiredSize.Height + 36));
        return details;
    }

    private static string RuntimeOwnerId(string? id, TContext context) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RuntimeOwnerId(id!, context);
    private static string RuntimeDisplayName(string id) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RuntimeDisplayName(id);
    private static bool IsInternalLoaderId(string? id) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IsInternalLoaderId(id!);
    private static string RequirementDisplay(TDependency requirement, TContext context) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.RequirementDisplay(requirement, context);

    private static string Referrers(List<string> names) => string.Join("、", names.Take(3)) + (names.Count > 3 ? " 等 " + names.Count + " 个模组" : "");
    private static string OriginState(PackDependencyFileRole role) => role switch {
        PackDependencyFileRole.AuthorModified => "个人覆盖或修改；原作者哈希仍保留，实际依赖由当前文件检查",
        PackDependencyFileRole.AuthorDisabled => "作者组件已禁用，本次不会自动启用",
        PackDependencyFileRole.AuthorMissing => "作者文件缺失，请使用正确原包核对修复",
        PackDependencyFileRole.OptionalAbsent => "作者可选组件未安装，保留当前选择",
        PackDependencyFileRole.Ambiguous => "归属存在多个候选，请先确认",
        _ => "文件来源无法确认，不自动修改"
    };

    private static string? Describe(TIssue issue) {
        TDependency? requirement = issue.Requirement;
        if (requirement is null) return issue.Message;
        string prefix = issue.KindValue == ModDependencyIssueKind.MissingRequired ? "需要：" : issue.KindValue == ModDependencyIssueKind.VersionMismatch ? "要求：" : "冲突：";
        string expected = requirement.Display;
        if (requirement.Versions is JArray versions && versions.Count > 0 && versions.All(value => value.Type == JTokenType.String))
            expected = requirement.Id + " " + string.Join(" 或 ", versions.Select(value => value.ToString()));
        string found = string.Join("、", issue.RelatedMods.Select(item => (item.IsBuiltin ? item.Name ?? item.Id : item.Display) + " " + item.Version));
        int disabledAt = (issue.Message ?? "").IndexOf("已禁用：", StringComparison.Ordinal);
        return (issue.IsError ? "" : "待确认 · ") + prefix + expected + (found == "" ? "" : "\r\n当前：" + found) +
            (disabledAt < 0 ? "" : "\r\n" + issue.Message!.Substring(disabledAt));
    }

    private static void AddGroup(StackPanel groups, string title, List<Tuple<string?, string?, TMod?>> entries, bool warning,
        ModDependencyCheckViewHost<TVersion, TIssue, TCard> host, bool wide = false) {
        TCard card = host.CreateCard(title, new Thickness(3, 3, 7, 10));
        UniformGrid rows = new() { Columns = wide ? 1 : 2, Margin = new Thickness(9, 38, 9, 10) };
        rows.SizeChanged += (_, _) => rows.Columns = wide || rows.ActualWidth < 510 ? 1 : 2;
        foreach (Tuple<string?, string?, TMod?> entry in entries) {
            Grid content = new();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ImageSource? picture = entry.Item3 is null ? null : ModDependencyIconFiles.Read(entry.Item3.RootPath, entry.Item3.IconPath);
            content.Children.Add(new Image { Source = picture ?? host.DefaultIcon(), Width = 28, Height = 28,
                VerticalAlignment = VerticalAlignment.Top, Clip = new RectangleGeometry(new Rect(0, 0, 28, 28), 5, 5) });
            StackPanel text = new() { Margin = new Thickness(9, 0, 0, 0) };
            TextBlock name = new() { Text = entry.Item1, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1"); text.Children.Add(name);
            TextBlock reason = new() { Text = entry.Item2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), FontSize = 12 };
            reason.SetResourceReference(TextBlock.ForegroundProperty, warning ? "ColorBrushRedLight" : "ColorBrush3"); text.Children.Add(reason);
            Grid.SetColumn(text, 1); content.Children.Add(text);
            Border box = new() { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10),
                Margin = new Thickness(4), Background = ModDependencyVisualTheme.Brush("ColorBrushWhite"), ToolTip = entry.Item3?.RootPath };
            box.SetResourceReference(Border.BorderBrushProperty, warning ? "ColorBrushRedLight" : "ColorBrush5");
            rows.Children.Add(box);
        }
        host.AddCardChild(card, rows);
        host.EnsureCaption(card);
        groups.Children.Add(card);
    }
}
