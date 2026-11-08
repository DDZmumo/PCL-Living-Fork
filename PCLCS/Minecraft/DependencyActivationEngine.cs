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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ModDependencyAutoFillEngine<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TVersion, TNetFile, TItem, TPlan, TActivation, TOptions, TCache> {
    private readonly ConcurrentDictionary<string, string> accepted = new(StringComparer.OrdinalIgnoreCase);
    public void SettingsChanged() { accepted.Clear(); host.RefreshOptions(); }
    public string Fingerprint(TReport report) {
        List<string> values = new() { JObject.FromObject(report.Context).ToString(Formatting.None) };
        values.Add(report.MetadataIncomplete.ToString());
        if (report.PackOrigin?.IsPack == true) values.Add("pack-origin:" + report.PackOrigin.Fingerprint);
        values.AddRange(report.Notices.OrderBy(value => value, StringComparer.Ordinal));
        foreach (TMod item in report.Mods.OrderBy(modInfo => modInfo.Key, StringComparer.Ordinal)) {
            values.Add(item.Key + "|" + item.Version + "|" + item.Enabled + "|" + item.Format);
            values.AddRange(item.Dependencies.Select(requirement => requirement.KindValue + ":" + requirement.Display).OrderBy(value => value, StringComparer.Ordinal));
        }
        foreach (string fileName in report.Mods.Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) {
            try {
                FileInfo info = new(fileName);
                values.Add(fileName + "|" + (info.Exists ? info.Length.ToString() + "|" + info.LastWriteTimeUtc.Ticks : "missing"));
            } catch { values.Add(fileName + "|unavailable"); }
        }
        values.AddRange(report.Issues.Select(issue => issue.KindValue + "|" + issue.IsError + "|" + issue.Message).OrderBy(value => value, StringComparer.Ordinal));
        using SHA256 hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", values))));
    }
    private string AcceptanceFingerprint(TReport report) => Fingerprint(report) + "\nplatform-review:\n" + string.Join("\n", report.PlatformReviewNotes().OrderBy(note => note, StringComparer.Ordinal));
    public bool WasAccepted(string folder, TReport report) => accepted.TryGetValue(FilePathKey(folder), out string? previous) && SameText(previous, AcceptanceFingerprint(report));
    public void Accept(string folder, TReport report) => accepted[FilePathKey(folder)] = AcceptanceFingerprint(report);
    public string IssueKey(TIssue issue) => (issue.Owner?.Key ?? "") + "|" + issue.KindValue + "|" + (issue.Requirement?.Display ?? issue.Message);
    private static string FilePathKey(string path) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FilePathKey(path);
    private static bool SameFilePath(string first, string second) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.SameFilePath(first, second);
    private static TReport Inspect(string folder, TContext context, CancellationToken token) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.Inspect(folder, context, token);
    private static TReport WithEnabledFiles(TReport report, IEnumerable<string> paths, CancellationToken token) {
        TReport result = ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.WithEnabledFiles(report, paths, token);
        result.PackOrigin = report.PackOrigin;
        return result;
    }
    private static bool FileSatisfiesDependency(TReport report, string path, TDependency requirement) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FileSatisfiesDependency(report, path, requirement);
    private static void BuildReport(TReport report, CancellationToken token) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.BuildReport(report, token);

    public TPlan Prepare(string root, TReport report, CancellationToken token) {
        TPlan plan = new() { Root = Path.GetFullPath(root), Report = report, Stamp = Fingerprint(report) };
        PackDependencyOrigin? origin = report.PackOrigin?.IsPack == true ? report.PackOrigin : null;
        if (origin is not null) {
            plan.Notes.AddRange(origin.Notes);
            plan.Notes.Add("整合包以作者组合为基线；仅为个人新增模组规划前置，作者禁用或缺失组件不会自动启用或补装。");
            if (!origin.Complete) return plan;
        }
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = report.Mods.Where(item => !item.Enabled && (origin is null || origin.IsPersonal(item.RootPath))).Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        TReport current = report;
        for (int round = 0; round <= 63; round++) {
            token.ThrowIfCancellationRequested();
            bool changed = false;
            foreach (TIssue issue in current.Issues.Where(item => item.KindValue == ModDependencyIssueKind.MissingRequired && item.Requirement?.KindValue == ModDependencyKind.Required &&
                (origin is null || origin.IsPersonal(item.Owner.RootPath)))) {
                List<string> matches = roots.Where(fileName => !selected.Contains(fileName) && FileSatisfiesDependency(current, fileName, issue.Requirement)).ToList();
                if (matches.Count != 1) continue;
                selected.Add(matches[0]); changed = true;
            }
            if (!changed) break;
            current = WithEnabledFiles(report, selected, token);
        }
        if (selected.Count == 0) return plan;
        HashSet<string> originalErrors = new(report.Issues.Where(issue => issue.IsError).Select(IssueKey), StringComparer.Ordinal);
        if (current.Issues.Any(issue => issue.IsError && !originalErrors.Contains(IssueKey(issue))) ||
            current.Edges.Any(edge => selected.Contains(edge.Owner.RootPath) && edge.Evaluated && edge.Requirement.KindValue == ModDependencyKind.Required && edge.StateValue != ModDependencyMatchState.Satisfied)) {
            plan.Notes.Add("已禁用的前置还存在版本冲突或未满足的依赖，请在模组管理中处理后启用。"); return plan;
        }
        foreach (string source in selected) {
            token.ThrowIfCancellationRequested();
            try {
                host.EnsureWithin(plan.Root, source);
                string destination = Regex.Replace(source, @"(?:\.disabled|\.old)+$", "", RegexOptions.IgnoreCase);
                host.EnsureWithin(plan.Root, destination);
                if (SameText(destination, source) || !new[] { ".jar", ".zip", ".litemod" }.Contains(Path.GetExtension(destination).ToLowerInvariant()) || File.Exists(destination) || Directory.Exists(destination)) {
                    plan.Notes.Add("启用后的文件名已有占用，未自动处理：" + Path.GetFileName(destination)); plan.Enables.Clear(); return plan;
                }
                TMod? owner = report.Mods.FirstOrDefault(item => SameFilePath(item.RootPath, source) && !item.IsNested);
                TActivation entry = new() { Name = owner?.Name ?? Path.GetFileName(destination), Owner = owner, Source = source, Destination = destination, Sha1 = host.FileHash(source, false) };
                foreach (TIssue issue in report.Issues.Where(value => value.KindValue == ModDependencyIssueKind.MissingRequired && value.Requirement is not null && FileSatisfiesDependency(report, source, value.Requirement))) {
                    if (!entry.RequiredBy.Contains(issue.Owner.Name)) entry.RequiredBy.Add(issue.Owner.Name);
                    if (!entry.Requirements.Contains(issue.Requirement.Display)) entry.Requirements.Add(issue.Requirement.Display);
                    entry.ResolvedIssues.Add(IssueKey(issue));
                }
                plan.Enables.Add(entry);
            } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
                plan.Notes.Add("无法确认已禁用文件，未自动启用：" + Path.GetFileName(source)); plan.Enables.Clear(); return plan;
            }
        }
        return plan;
    }
}
