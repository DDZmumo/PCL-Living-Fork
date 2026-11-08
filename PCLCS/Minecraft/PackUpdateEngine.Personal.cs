// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCLCS;

public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    private sealed class FinalModFile {
        internal string Relative = "", Path = "";
        internal TChange? Change;
        internal bool Personal;
    }
    private List<FinalModFile> FinalModFiles(TPlan plan, CancellationToken token, bool verify = true) {
        var removed = new HashSet<string>(PlanChanges(plan).Where(change => change.ApplyAuthor && change.LocalRelative is not null)
            .Select(change => change.LocalRelative!), StringComparer.OrdinalIgnoreCase);
        List<FinalModFile> files = new();
        foreach (string path in LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(path => LoadedModPath(plan.Package!, path) && !removed.Contains(path))) {
            TChange? change = PlanChanges(plan).FirstOrDefault(item => string.Equals(item.LocalRelative, path, StringComparison.OrdinalIgnoreCase));
            files.Add(new() { Relative = path, Path = Target(plan.Root!, path), Change = change,
                Personal = change?.Ownership is PackUpdateOwnership.PersonalAdded or PackUpdateOwnership.PersonalModified ||
                    change is not null && !change.ApplyAuthor && !change.PreviewOnly && (change.Incoming is not null || change.AllowPreserveAuthor) });
        }
        foreach (TChange change in PlanChanges(plan).Where(change => change.ApplyAuthor && change.Incoming is not null && LoadedModPath(plan.Package!, change.DestinationRelative))) {
            token.ThrowIfCancellationRequested();
            if (change.Incoming!.Payload is null || verify && !Matches(change.Incoming.Payload, change.Incoming.Hash, token))
                throw new IOException("所选模组尚未完整暂存或已变化：" + change.Relative);
            files.Add(new() { Relative = change.DestinationRelative, Path = change.Incoming.Payload, Change = change,
                Personal = change.Operation is PackUpdateOperation.PersonalDependency or PackUpdateOperation.EnablePersonal });
        }
        return files;
    }
    private PackDependencyGraphResult PreparedGraph(TPlan plan, CancellationToken token) =>
        PackDependencyGraph.Check(FinalModFiles(plan, token, false).Select(file => file.Path), plan.Package!.Minecraft, plan.Package.Loaders, token);
    private static bool SameFile(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool RuntimeDependency(string id) => new[] { "minecraft", "java", "fabricloader", "quilt_loader", "forge", "neoforge", "liteloader" }.Contains(id, StringComparer.Ordinal);

    // Pair one untracked local component with one newly declared author component.
    // This is a previewed replacement, not proof that the local bytes were authored by the pack.
    private void LinkPersonalAuthorUpdates(TPlan plan, CancellationToken token) {
        foreach (TChange linked in plan.Changes.Where(change => change.PersonalComponentMatched && change.Incoming?.Payload is not null)) {
            var before = Inspect(plan.Package!, Target(plan.Root!, linked.LocalRelative!), token);
            var after = Inspect(plan.Package!, linked.Incoming!.Payload!, token);
            var sameIds = LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(path => LoadedModPath(plan.Package!, path))
                .Count(path => { var info = Inspect(plan.Package!, Target(plan.Root!, path), token); return info.Confirmed && before.OwnIds.SetEquals(info.OwnIds); });
            if (!before.Confirmed || !after.Confirmed || before.OwnIds.Count == 0 || !before.OwnIds.SetEquals(after.OwnIds) || sameIds != 1) {
                linked.PersonalComponentMatched = false; linked.AllowAuthor = false; linked.Conflict = true; linked.Association = PackUpdateAssociation.Unknown;
                linked.Reason = "原配对的模组身份已变化或出现多个本地候选，保留当前文件，请重新确认关联。";
            }
        }
        var incoming = plan.Changes.Where(change => change.BaselineRelative is null && change.LocalRelative is null &&
            !change.DisplayOnly && change.AllowAuthor && change.Incoming?.Payload is not null && change.Ownership == PackUpdateOwnership.AuthorUnchanged &&
            LoadedModPath(plan.Package!, change.DestinationRelative)).ToDictionary(change => change, change => Inspect(plan.Package!, change.Incoming!.Payload!, token));
        var personal = plan.Changes.Where(change => change.DisplayOnly && change.BaselineRelative is null && change.LocalRelative is not null &&
            change.Ownership == PackUpdateOwnership.PersonalAdded && LoadedModPath(plan.Package!, change.LocalRelative))
            .ToDictionary(change => change, change => Inspect(plan.Package!, Target(plan.Root!, change.LocalRelative!), token));
        bool Match(PackModInspection left, PackModInspection right) => left.Confirmed && right.Confirmed && left.OwnIds.Count > 0 && left.OwnIds.SetEquals(right.OwnIds);
        foreach (var author in incoming) {
            token.ThrowIfCancellationRequested();
            var matches = personal.Where(local => Match(local.Value, author.Value)).ToArray();
            if (matches.Length != 1 || incoming.Count(other => Match(other.Value, matches[0].Value)) != 1) continue;
            TChange change = author.Key, local = matches[0].Key;
            change.LocalRelative = local.LocalRelative; change.Before = local.Before;
            change.Ownership = PackUpdateOwnership.PersonalAdded;
            change.PersonalComponentMatched = true;
            change.Association = PackUpdateAssociation.PersonalComponent;
            change.Conflict = false; change.AllowPreserveAuthor = true; change.Choice = PackUpdateChoice.UseAuthor;
            change.Reason = "新版整包已包含此模组，默认升级为整包版本；可选择保留本地版本，确认时会重新检查完整组合。";
            plan.Changes.Remove(local);
        }
    }

    // Resolve personal content against N + retained L. Never roll author N back
    // automatically merely to satisfy a personal dependency.
    private void ResolvePersonalChanges(TPlan plan, string stage, CancellationToken token, Action<List<TNetFile>, CancellationToken> download) {
        for (int supplementRound = 0; supplementRound < 4; supplementRound++) {
            var graph = PreparedGraph(plan, token);
            var files = FinalModFiles(plan, token, false);
            var repairs = PreviewRepairs(plan, graph.Problems.Where(issue => issue.IsError).ToArray(), token);
            var missing = graph.Problems.Where(issue => issue.IsError && issue.Kind == ModDependencyIssueKind.MissingRequired && !RuntimeDependency(issue.RequirementId) &&
                !repairs.Values.Any(problems => problems.Contains(issue)))
                .Select(issue => issue.OwnerPath).Where(path => files.Any(file => file.Personal && SameFile(file.Path, path))).Distinct().ToArray();
            if (missing.Length == 0) break;
            int before = plan.Changes.Count(change => change.Operation == PackUpdateOperation.PersonalDependency);
            TryPreparePersonalDependencies(plan, stage, files, missing, token, download);
            if (plan.Changes.Count(change => change.Operation == PackUpdateOperation.PersonalDependency) == before) break;
        }
        for (int round = 0; round < 64; round++) {
            token.ThrowIfCancellationRequested();
            var files = FinalModFiles(plan, token, false);
            var graph = PreparedGraph(plan, token);
            var allErrors = graph.Problems.Where(issue => issue.IsError).ToArray();
            var repairs = PreviewRepairs(plan, allErrors, token);
            var errors = allErrors.Where(issue => !repairs.Values.Any(problems => problems.Contains(issue))).ToArray();
            if (errors.Length == 0) break;
            var affected = files.Where(file => file.Personal && file.Change is not null && errors.Any(issue =>
                SameFile(file.Path, issue.OwnerPath) || issue.RelatedPaths.Any(path => SameFile(path, file.Path)))).ToArray();
            if (affected.Length == 0) break; // The author's own combination is not repaired by disabling unrelated personal files.
            bool changed = false;
            // Prefer a known author replacement before disabling a consumer that
            // may become valid after that replacement. Re-evaluate after each edit.
            foreach (var file in affected.OrderByDescending(file => file.Change!.Incoming is not null)) {
                TChange change = file.Change!;
                if (change.Operation == PackUpdateOperation.PersonalDependency) continue;
                string detail = string.Join("\r\n", errors.Where(issue => SameFile(file.Path, issue.OwnerPath) || issue.RelatedPaths.Any(path => SameFile(path, file.Path)))
                    .Select(issue => DescribeProblem(issue, files)).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().Take(3));
                if (change.Incoming is not null && change.AllowAuthor && change.Ownership != PackUpdateOwnership.Ambiguous &&
                    (change.BaselineRelative is not null || change.PersonalComponentMatched) &&
                    (change.Ownership != PackUpdateOwnership.PersonalAdded || change.PersonalComponentMatched)) {
                    change.Choice = PackUpdateChoice.UseAuthor;
                    change.Conflict = false; change.AllowPreserveAuthor = true; change.PersonalCompatible = false; change.PreviewAuthorRepair = true;
                    change.Reason = "本地版本与新版整包的声明不兼容，默认使用作者新版；保留旧版仍须通过完整组合检查。" + (detail.Length > 0 ? "\r\n" + detail : "");
                } else {
                    if (change.Incoming is not null) continue; // An ambiguous occupied author target requires an explicit decision.
                    string destination = file.Relative + ".disabled";
                    for (int suffix = 1; File.Exists(Target(plan.Root!, destination)) || plan.Changes.Any(other => !ReferenceEquals(other, change) && other.ApplyAuthor &&
                            string.Equals(other.DestinationRelative, destination, StringComparison.OrdinalIgnoreCase)); suffix++) {
                        if (suffix > 128) throw new IOException("无法为个人模组选择无碰撞的禁用路径：" + file.Relative);
                        destination = file.Relative + "." + suffix + ".disabled";
                    }
                    Observe(plan, destination, token);
                    change.Operation = PackUpdateOperation.DisablePersonal; change.DisabledDestination = destination;
                    change.DisplayOnly = false; change.AllowAuthor = true; change.Conflict = true; change.Choice = PackUpdateChoice.UseAuthor;
                    change.PersonalCompatible = false;
                    change.Reason = detail.Length > 0 ? "禁用原因：" + detail : "个人模组与新版整包存在已确认的声明冲突或缺失依赖。";
                }
                changed = true;
                break;
            }
            if (!changed) break;
        }
        PruneUnneededPersonalDependencies(plan, token);
        var final = PreparedGraph(plan, token);
        foreach (var file in FinalModFiles(plan, token, false).Where(file => file.Personal && file.Change is not null && file.Change.Operation != PackUpdateOperation.PersonalDependency)) {
            bool bad = final.Problems.Any(issue => issue.IsError && (SameFile(file.Path, issue.OwnerPath) || issue.RelatedPaths.Any(path => SameFile(path, file.Path))));
            bool unknown = final.UnverifiedFiles.Any(path => SameFile(path, file.Path));
            if (!bad && !unknown) {
                file.Change!.PersonalCompatible = true; file.Change.Conflict = false;
                if (!file.Change.DisplayOnly) file.Change.AllowPreserveAuthor = true;
                file.Change.Choice = PackUpdateChoice.PreservePersonal;
                file.Change.Reason = "个人版本在新版整包中未发现已声明的依赖或运行时冲突，默认保留；实际游戏运行仍需验证。";
            }
        }
    }
    private static string DescribeProblem(PackDependencyProblem issue, List<FinalModFile> files) {
        string message = issue.Message;
        foreach (var file in files) message = message.Replace(file.Path, file.Relative);
        foreach (var group in files.GroupBy(file => Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == 1))
            message = message.Replace(group.Key, Path.GetFileName(group.Single().Relative));
        return message;
    }
    private void TryPreparePersonalDependencies(TPlan plan, string stage, List<FinalModFile> files, string[] missing,
        CancellationToken token, Action<List<TNetFile>, CancellationToken> download) {
        string virtualRoot = Target(stage, "personal-final-" + Guid.NewGuid().ToString("N"));
        var personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var virtualFiles = new List<string>();
        foreach (var file in files) {
            string destination = Target(virtualRoot, file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            FileTaskProgress.Copy(file.Path, destination, token, null); virtualFiles.Add(destination);
            if (file.Personal && missing.Any(path => SameFile(path, file.Path))) personal.Add(destination);
        }
        var report = PackDependencyGraph.Check(virtualFiles, plan.Package!.Minecraft, plan.Package.Loaders, token).Report!;
        var protectedProjects = new HashSet<string>(plan.Package.Files.Values.Where(file => file.Identity is not null).Select(file => file.Identity!), StringComparer.Ordinal);
        PackUpdateDependencyPreparation prepared;
        try { prepared = PackUpdateDependencySupplement.Prepare(Path.Combine(virtualRoot, "mods"), report, personal, protectedProjects, host.ParseVersion, host.Request, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { plan.Warnings.Add("个人前置自动查询未完成：" + error.Message); return; }
        plan.Warnings.AddRange(prepared.Notes);
        int remaining = Math.Max(0, 64 - plan.Changes.Count(change => change.Operation == PackUpdateOperation.PersonalDependency));
        foreach (var candidate in prepared.Candidates.Take(remaining)) {
            token.ThrowIfCancellationRequested();
            foreach (var option in new[] { candidate }.Concat(candidate.Alternatives).Take(8)) {
                var version = option.Version;
                string relative = RelativePath("mods/" + version.FileName);
                if (plan.Package.Files.ContainsKey(relative) || File.Exists(Target(plan.Root!, relative)) ||
                    plan.Changes.Any(change => string.Equals(change.Relative, relative, StringComparison.OrdinalIgnoreCase))) continue;
                string payload = Target(stage, "personal-dependencies/" + Guid.NewGuid().ToString("N") + ".jar");
                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                try {
                    download(new() { host.CreateDownload(version.DownloadUrls!, payload, version.Hash!) }, token);
                    token.ThrowIfCancellationRequested();
                    string hash = ValidHash(version.Hash!.Length == 32 ? "md5" : "sha1", version.Hash);
                    if (!File.Exists(payload) || new FileInfo(payload).Length != version.Size || !Matches(payload, hash, token)) throw new IOException("个人前置下载未通过校验。");
                    var single = PackDependencyGraph.Check(new[] { payload }, plan.Package.Minecraft, plan.Package.Loaders, token);
                    if (single.UnverifiedFiles.Count > 0 || single.Problems.Any(issue => issue.IsError && (issue.Kind == ModDependencyIssueKind.LoaderMismatch || RuntimeDependency(issue.RequirementId))))
                        throw new IOException("个人前置候选的实际元数据不支持目标运行时。");
                    TFile incoming = host.CreateFile(); incoming.Relative = relative; incoming.Hash = hash; incoming.Payload = payload;
                    incoming.Identity = candidate.Platform.ToLowerInvariant() + ":" + candidate.ProjectId; incoming.FileIdentity = candidate.Platform.ToLowerInvariant() + ":" + version.Id;
                    TChange addition = host.CreateChange(); addition.Relative = relative; addition.Incoming = incoming;
                    addition.Operation = PackUpdateOperation.PersonalDependency; addition.Ownership = PackUpdateOwnership.PersonalAdded;
                    addition.Choice = PackUpdateChoice.UseAuthor; addition.Reason = "为个人模组补全的依赖，不属于作者原始文件清单。";
                    var prior = PreparedGraph(plan, token);
                    Observe(plan, relative, token); addition.TargetBefore = plan.Snapshot[relative]; plan.Changes.Add(addition);
                    var next = PreparedGraph(plan, token);
                    string ProblemKey(PackDependencyProblem issue) => issue.Kind + "|" + issue.OwnerPath + "|" + issue.RequirementId + "|" + string.Join("|", issue.RelatedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                    var known = new HashSet<string>(prior.Problems.Where(issue => issue.IsError).Select(ProblemKey), StringComparer.Ordinal);
                    if (next.Problems.Any(issue => issue.IsError && issue.Kind != ModDependencyIssueKind.MissingRequired && !known.Contains(ProblemKey(issue)))) {
                        plan.Changes.Remove(addition);
                        throw new IOException("个人前置候选不满足实际版本约束或引入组合冲突，尝试其他候选。");
                    }
                    break;
                } catch (OperationCanceledException) { throw; }
                catch (Exception error) { plan.Warnings.Add("个人前置候选未采用：" + version.Display + "（" + error.Message + "）"); }
            }
        }
    }
    private void PruneUnneededPersonalDependencies(TPlan plan, CancellationToken token) {
        var additions = plan.Changes.Where(change => change.Operation == PackUpdateOperation.PersonalDependency).ToArray();
        if (additions.Length == 0) return;
        var report = PreparedGraph(plan, token).Report!;
        var files = FinalModFiles(plan, token, false);
        var reachable = new HashSet<string>(files.Where(file => file.Change?.Operation != PackUpdateOperation.PersonalDependency).Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
        bool changed;
        do {
            changed = false;
            foreach (var edge in report.Edges.Where(edge => edge.Evaluated && !edge.ConditionBypassed && edge.StateValue == ModDependencyMatchState.Satisfied &&
                edge.Requirement.KindValue == ModDependencyKind.Required && reachable.Contains(edge.Owner.RootPath)))
                foreach (var provider in edge.Providers) changed |= reachable.Add(provider.RootPath);
        } while (changed);
        plan.Changes.RemoveAll(change => change.Operation == PackUpdateOperation.PersonalDependency && !reachable.Contains(change.Incoming!.Payload!));
    }
}
