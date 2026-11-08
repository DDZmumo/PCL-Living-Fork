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
    private static IEnumerable<TChange> PlanChanges(TPlan plan) => plan.Changes.Concat(plan.PreviewChanges).Distinct();

    private void PreparePreviewChanges(TPlan plan, string stage, CancellationToken token) {
        plan.PreviewChanges.Clear();
        foreach (TFile file in plan.Package!.Files.Values.Where(file => LoadedModPath(plan.Package!, file.Relative!))) {
            token.ThrowIfCancellationRequested();
            if (plan.Changes.Any(change => string.Equals(change.Incoming?.Relative, file.Relative, StringComparison.OrdinalIgnoreCase) || string.Equals(change.LocalRelative, file.Relative, StringComparison.OrdinalIgnoreCase))) continue;
            var previous = plan.Baseline?["files"]?[file.Relative!];
            if (previous is null || previous["local"]?["ownership"]?.ToString() is "personal-added" or "ambiguous" or "unknown") continue;
            string[] found = new[] { file.Relative!, file.Relative + ".disabled", file.Relative + ".old" }
                .Where(relative => File.Exists(Target(plan.Root!, relative))).ToArray();
            if (found.Length != 1 || !Matches(Target(plan.Root!, found[0]), file.Hash, token) || !Matches(Target(plan.Root!, found[0]), previous["hash"]?.ToString(), token)) continue;
            if (file.Payload is null) {
                file.Payload = Target(stage, "preview-existing/" + Guid.NewGuid().ToString("N") + ".jar");
                Directory.CreateDirectory(Path.GetDirectoryName(file.Payload)!);
                FileTaskProgress.Copy(Target(plan.Root!, found[0]), file.Payload, token, null);
            }
            Observe(plan, found[0], token); Observe(plan, file.Relative!, token); Observe(plan, file.Relative + ".disabled", token);
            TChange candidate = host.CreateChange();
            candidate.Relative = file.Relative; candidate.Incoming = file; candidate.BaselineRelative = file.Relative;
            candidate.LocalRelative = found[0]; candidate.Before = plan.Snapshot[found[0]];
            candidate.Ownership = DisabledFile(found[0]) ? PackUpdateOwnership.Disabled : PackUpdateOwnership.AuthorUnchanged;
            candidate.PreviewOnly = true; candidate.Choice = PackUpdateChoice.UseAuthor;
            candidate.Association = PackUpdateAssociation.DeclaredPath;
            candidate.TargetBefore = plan.Snapshot[candidate.DestinationRelative];
            plan.PreviewChanges.Add(candidate);
        }
        PreparePersonalPreviewChanges(plan, stage, token);
        // Every state-switch target is observed before the player sees a checkbox.
        foreach (TChange change in PlanChanges(plan).Where(change => change.AllowAuthor && change.Incoming is not null && ModFile(change.Relative!))) {
            Observe(plan, change.Relative!, token); Observe(plan, change.Relative + ".disabled", token);
        }
    }

    private void PreparePersonalPreviewChanges(TPlan plan, string stage, CancellationToken token) {
        var candidates = plan.Changes.Where(change => change.DisplayOnly && !change.AllowAuthor && change.BaselineRelative is null &&
            change.Incoming is null && change.Ownership == PackUpdateOwnership.Disabled && change.LocalRelative is not null && DisabledFile(change.LocalRelative))
            .Select(change => new { Change = change, Relative = change.LocalRelative![..change.LocalRelative!.LastIndexOf('.')],
                Info = Inspect(plan.Package!, Target(plan.Root!, change.LocalRelative!), token) }).ToArray();
        foreach (var candidate in candidates) {
            token.ThrowIfCancellationRequested();
            string relative = candidate.Relative, local = candidate.Change.LocalRelative!;
            if (!LoadedModPath(plan.Package!, relative) || !candidate.Info.Confirmed || candidate.Info.OwnIds.Count == 0 ||
                candidates.Count(other => other.Info.Confirmed && candidate.Info.OwnIds.Overlaps(other.Info.OwnIds)) != 1 ||
                File.Exists(Target(plan.Root!, relative)) ||
                new[] { relative, relative + ".disabled", relative + ".old" }.Count(path => File.Exists(Target(plan.Root!, path))) != 1 ||
                PlanChanges(plan).Any(change => change.Incoming is not null && (string.Equals(change.Relative, relative, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(change.DestinationRelative, relative, StringComparison.OrdinalIgnoreCase))) ||
                plan.Package!.Files.Keys.Any(path => string.Equals(path, local, StringComparison.OrdinalIgnoreCase) || string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)) ||
                ((Newtonsoft.Json.Linq.JObject)plan.Baseline!["files"]!).Properties().Any(entry =>
                    string.Equals(entry.Name, relative, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Name, local, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value["local"]?["relative"]?.ToString(), local, StringComparison.OrdinalIgnoreCase))) continue;
            Observe(plan, relative, token);
            TFile incoming = host.CreateFile(); incoming.Relative = relative; incoming.Hash = candidate.Change.Before;
            incoming.Payload = Target(stage, "preview-personal/" + Guid.NewGuid().ToString("N") + ".jar");
            Directory.CreateDirectory(Path.GetDirectoryName(incoming.Payload)!);
            FileTaskProgress.Copy(Target(plan.Root!, local), incoming.Payload, token, null);
            TChange choice = host.CreateChange(); choice.Relative = relative; choice.LocalRelative = local; choice.Before = candidate.Change.Before;
            choice.Incoming = incoming; choice.TargetBefore = plan.Snapshot[relative]; choice.Ownership = PackUpdateOwnership.Disabled;
            choice.Operation = PackUpdateOperation.EnablePersonal; choice.PreviewOnly = true; choice.Choice = PackUpdateChoice.UseAuthor;
            choice.Association = PackUpdateAssociation.PersonalComponent;
            choice.Reason = "本地个人禁用文件；仅在明确选择并通过依赖检查后启用，不属于作者文件。";
            plan.PreviewChanges.Add(choice);
        }
    }

    public TPlan SnapshotForPreview(TPlan plan) {
        TPlan copy = host.CreatePlan(); copy.Root = plan.Root; copy.Package = plan.Package; copy.Baseline = (Newtonsoft.Json.Linq.JObject?)plan.Baseline?.DeepClone();
        copy.Changes = plan.Changes.Select(change => (TChange)change.SnapshotForPreview()).ToList();
        copy.PreviewChanges = plan.PreviewChanges.Select(change => (TChange)change.SnapshotForPreview()).ToList();
        copy.Snapshot = new(plan.Snapshot, StringComparer.OrdinalIgnoreCase);
        copy.ObservedModFiles = new(plan.ObservedModFiles, StringComparer.OrdinalIgnoreCase);
        copy.AllowUnverifiedDependencies = plan.AllowUnverifiedDependencies;
        return copy;
    }

    private Dictionary<TChange, PackDependencyProblem[]> PreviewRepairs(TPlan plan, PackDependencyProblem[] errors, CancellationToken token) {
        var missing = errors.Where(problem => problem.Kind == ModDependencyIssueKind.MissingRequired).ToArray();
        var providers = PlanChanges(plan).Where(change => missing.Length > 0 && (change.KeepDisabled || change.DisableAfterUpdate) && change.AllowAuthor && !change.DisplayOnly && change.Ownership == PackUpdateOwnership.Disabled && change.Incoming?.Payload is not null)
            .ToDictionary(change => change, change => Inspect(plan.Package!, change.Incoming!.Payload!, token));
        bool Provides(PackModInspection info, string id) => info.OwnIds.Contains(id) || info.ProvidedIds.Contains(id) || info.NestedIds.Contains(id);
        // An ID alone is not proof that a disabled archive can satisfy this versioned requirement.
        // Evaluate an isolated choice so the UI never mutates the plan while a review is in flight.
        string ProblemKey(PackDependencyProblem problem) => problem.Kind + "|" + problem.OwnerPath + "|" + problem.RequirementId;
        var currentErrors = new HashSet<string>(errors.Select(ProblemKey), StringComparer.OrdinalIgnoreCase);
        var repairs = new Dictionary<TChange, PackDependencyProblem[]>();
        foreach (var provider in providers.Where(provider => provider.Value.Confirmed && missing.Any(problem => Provides(provider.Value, problem.RequirementId)))) {
            TPlan enabled = SnapshotForPreview(plan);
            TChange choice = PlanChanges(enabled).Single(change => string.Equals(change.Relative, provider.Key.Relative, StringComparison.OrdinalIgnoreCase));
            choice.KeepDisabled = false; choice.DisableAfterUpdate = false; choice.Choice = PackUpdateChoice.UseAuthor;
            if (!choice.ApplyAuthor || !enabled.Snapshot.TryGetValue(choice.DestinationRelative, out string? occupied) ||
                occupied is not null && !string.Equals(choice.LocalRelative, choice.DestinationRelative, StringComparison.OrdinalIgnoreCase)) continue;
            var proposed = CheckDependencies(enabled, token);
            // A chain of missing requirements can be reviewed in subsequent choices; a definite
            // new incompatibility or version mismatch must not masquerade as an available repair.
            if (proposed.Problems.Any(problem => problem.IsError && problem.Kind != ModDependencyIssueKind.MissingRequired && !currentErrors.Contains(ProblemKey(problem)))) continue;
            repairs[provider.Key] = missing.Where(problem => Provides(provider.Value, problem.RequirementId) &&
                (provider.Key.Operation != PackUpdateOperation.EnablePersonal || !proposed.UnverifiedFiles.Any(path => SameFile(path, problem.OwnerPath) || SameFile(path, choice.Incoming!.Payload!))) &&
                !proposed.Problems.Any(next => next.IsError && SameFile(next.OwnerPath, problem.OwnerPath) && next.RequirementId == problem.RequirementId)).ToArray();
        }
        return repairs;
    }

    public PackUpdatePreviewReview ReviewPreview(TPlan plan, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        ValidateSnapshot(plan, token);
        var review = new PackUpdatePreviewReview { Dependencies = CheckDependencies(plan, token) };
        var files = FinalModFiles(plan, token, false);
        var errors = review.Dependencies.Problems.Where(problem => problem.IsError).ToArray();
        // Judge personal content against the author's proposed baseline. A conflict with
        // a personal addition is not evidence that a healthy author file should be disabled.
        var authorPaths = new List<string>();
        bool authorBaselineComplete = true;
        foreach (TFile author in plan.Package!.Files.Values.Where(file => LoadedModPath(plan.Package!, file.Relative!))) {
            string path = author.Payload ?? Target(plan.Root!, author.Relative!);
            if (!Matches(path, author.Hash, token)) { authorBaselineComplete = false; continue; }
            authorPaths.Add(path);
        }
        string LogicalPath(string path) => plan.Package.Files.Values.FirstOrDefault(file => file.Payload is not null && SameFile(file.Payload, path))?.Relative ??
            files.FirstOrDefault(file => SameFile(file.Path, path))?.Relative ?? path;
        var authorGraph = PackDependencyGraph.Check(authorPaths, plan.Package.Minecraft, plan.Package.Loaders, token);
        bool AuthorError(PackDependencyProblem problem) => (authorBaselineComplete || problem.Kind != ModDependencyIssueKind.MissingRequired) && authorGraph.Problems.Any(author => author.IsError && (author.Kind == problem.Kind ||
                problem.Kind == ModDependencyIssueKind.MissingRequired && author.Kind == ModDependencyIssueKind.VersionMismatch) &&
            string.Equals(LogicalPath(author.OwnerPath), LogicalPath(problem.OwnerPath), StringComparison.OrdinalIgnoreCase) && author.RequirementId == problem.RequirementId);
        var replacements = new Dictionary<TChange, PackDependencyProblem[]>();
        foreach (TChange change in PlanChanges(plan).Where(change => !change.ApplyAuthor && change.AllowAuthor && !change.DisplayOnly &&
            change.Incoming?.Payload is not null && change.Operation == PackUpdateOperation.AuthorChange && !change.PreviewOnly &&
            change.LocalRelative is not null && LoadedModPath(plan.Package!, change.LocalRelative))) {
            TPlan replaced = SnapshotForPreview(plan);
            TChange choice = PlanChanges(replaced).Single(item => string.Equals(item.Relative, change.Relative, StringComparison.OrdinalIgnoreCase));
            choice.Choice = PackUpdateChoice.UseAuthor; choice.DisableAfterUpdate = false;
            var proposed = CheckDependencies(replaced, token);
            replacements[change] = errors.Where(problem => !proposed.Problems.Any(next => next.IsError && next.Kind == problem.Kind &&
                string.Equals(LogicalPath(next.OwnerPath), LogicalPath(problem.OwnerPath), StringComparison.OrdinalIgnoreCase) && next.RequirementId == problem.RequirementId)).ToArray();
        }
        var repairs = PreviewRepairs(plan, errors, token);
        bool HasRepair(PackDependencyProblem problem) => repairs.Any(pair => pair.Value.Contains(problem));
        foreach (TChange change in PlanChanges(plan).Where(change => change.AllowAuthor && !change.DisplayOnly && change.Ownership is not (PackUpdateOwnership.Ambiguous or PackUpdateOwnership.Unknown) && ModFile(change.Relative!))) {
            token.ThrowIfCancellationRequested();
            var selectedFile = files.FirstOrDefault(file => ReferenceEquals(file.Change, change));
            string? selectedPath = selectedFile?.Path;
            // Required-version and loader errors belong to the declaring consumer. Related
            // providers are alternatives for explicit conflicts/duplicate identities only.
            var problems = errors.Where(problem => selectedPath is not null && (SameFile(problem.OwnerPath, selectedPath) ||
                (problem.Kind is ModDependencyIssueKind.DuplicateId or ModDependencyIssueKind.Incompatible) &&
                problem.RelatedPaths.Any(path => SameFile(path, selectedPath)))).ToArray();
            var incompatible = problems.Where(problem => (selectedFile?.Personal == true || AuthorError(problem)) &&
                (problem.Kind != ModDependencyIssueKind.MissingRequired || !HasRepair(problem)) &&
                !replacements.Any(pair => !ReferenceEquals(pair.Key, change) && pair.Value.Contains(problem))).ToArray();
            var replacementProblems = replacements.TryGetValue(change, out var solved) ? solved : Array.Empty<PackDependencyProblem>();
            bool personalReplacement = change.Incoming is not null && change.Operation == PackUpdateOperation.AuthorChange &&
                (selectedFile?.Personal == true || change.Ownership is PackUpdateOwnership.PersonalAdded or PackUpdateOwnership.PersonalModified || change.PersonalComponentMatched || change.PreviewAuthorRepair);
            if (personalReplacement && (incompatible.Length > 0 || replacementProblems.Length > 0 || change.ApplyAuthor && change.PreviewAuthorRepair)) {
                review.Actions.Add(new() { Relative = change.Relative!, Kind = PackUpdatePreviewActionKind.UseAuthorVersion, Selected = change.ApplyAuthor,
                    Details = "个人版本与整包作者基准不兼容，改用作者新版后重新检查完整组合。" +
                        (incompatible.Length > 0 || replacementProblems.Length > 0 ? "\r\n" + string.Join("\r\n", incompatible.Concat(replacementProblems).Select(problem => DescribeProblem(problem, files)).Distinct()) : "") });
                incompatible = Array.Empty<PackDependencyProblem>();
            }
            bool disabled = change.DisableAfterUpdate || change.Operation == PackUpdateOperation.DisablePersonal && change.ApplyAuthor;
            if (change.Operation != PackUpdateOperation.EnablePersonal && (incompatible.Length > 0 || disabled) && (change.Incoming is not null || change.Operation == PackUpdateOperation.DisablePersonal)) {
                review.Actions.Add(new() { Relative = change.Relative!, Kind = PackUpdatePreviewActionKind.DisableIncompatible, Selected = disabled,
                    Details = incompatible.Length > 0 ? (selectedFile?.Personal == true ? "个人模组与整包作者基准不兼容：\r\n" : "作者新版整合包自身存在声明不兼容：\r\n") +
                        string.Join("\r\n", incompatible.Select(problem => DescribeProblem(problem, files)).Distinct()) : "此项将在备份后保持禁用，文件保留。" });
            }
            if (change.Ownership == PackUpdateOwnership.Disabled && change.Incoming?.Payload is not null) {
                var required = repairs.TryGetValue(change, out var repaired) ? repaired.Where(HasRepair).ToArray() : Array.Empty<PackDependencyProblem>();
                if (required.Length > 0 || !change.KeepDisabled && !change.DisableAfterUpdate) review.Actions.Add(new() {
                    Relative = change.Relative!, Kind = PackUpdatePreviewActionKind.EnableRequired, Selected = !change.KeepDisabled && !change.DisableAfterUpdate,
                    Details = required.Length > 0 ? string.Join("\r\n", required.Select(problem => DescribeProblem(problem, files)).Distinct()) : change.Operation == PackUpdateOperation.EnablePersonal ? "启用本地个人前置；保留原文件字节和个人来源，确认前仍会检查整个组合。" : "使用整包新版前置并启用；确认前仍会检查整个组合。"
                });
            }
            if (change.ApplyAuthor && change.Incoming is not null && (change.DisableAfterUpdate || change.Ownership == PackUpdateOwnership.Disabled && !change.KeepDisabled) &&
                !string.Equals(change.LocalRelative, change.DestinationRelative, StringComparison.OrdinalIgnoreCase) &&
                (!plan.Snapshot.TryGetValue(change.DestinationRelative, out string? occupied) || occupied is not null)) {
                review.Dependencies.HasErrors = true;
                review.Dependencies.Errors.Add("启用／禁用目标已被占用或未经预览，不会覆盖：" + change.DestinationRelative);
            }
        }
        try { ValidatePlannedOperations(plan, token); }
        catch (IOException error) { review.Dependencies.HasErrors = true; review.Dependencies.Errors.Add(error.Message); }
        return review;
    }
}
