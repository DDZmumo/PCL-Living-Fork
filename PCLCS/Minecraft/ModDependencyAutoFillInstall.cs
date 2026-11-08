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

public sealed partial class ModDependencyAutoFillEngine<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TVersion, TNetFile, TItem, TPlan, TActivation, TOptions, TCache> {
    public DependencyDownloadBatch<TNetFile> CreateDownloadTask(TPlan plan, List<TNetFile> files) {
        DependencyDownloadBatch<TNetFile> batch = new();
        for (int index = 0; index < files.Count; index++) batch.Steps.Add(new DependencyDownloadStep<TNetFile> {
            Name = "下载 " + plan.Items[index].Name + " " + host.Display(plan.Items[index].Version), File = files[index], Block = false,
            ProgressWeight = Math.Max(1, host.Size(plan.Items[index].Version) / 1024.0 / 1024.0)
        });
        batch.Name = "补全模组依赖：" + (plan.Caption ?? new DirectoryInfo(plan.Root).Parent!.Name);
        return batch;
    }
    private string StagedFile(TPlan plan, string stage, int index) => Path.Combine(stage, index + "-" + host.FileName(plan.Items[index].Version));
    public int Install(TPlan plan, CancellationToken token, Action<List<TNetFile>, CancellationToken>? download = null) {
        for (int attempt = 0; attempt <= 15; attempt++) {
            token.ThrowIfCancellationRequested();
            try { return InstallAttempt(plan, token, download); }
            catch (DependencyCandidateMismatchException error) {
                if (plan.Items.Any(item => item.Replacement is not null)) throw;
                if (error.Index < 0 || error.Index >= plan.Items.Count) throw;
                TItem entry = plan.Items[error.Index];
                TItem? nextVersion = null;
                string requirements = string.Join("|", Required(entry.Raw, entry.Platform, "").Select(value => Key(value.Platform, value.Project) + ":" + value.Version).OrderBy(value => value, StringComparer.Ordinal));
                while (entry.Alternatives.Count > 0) {
                    TItem candidate = entry.Alternatives[0]; entry.Alternatives.RemoveAt(0);
                    string otherRequirements = string.Join("|", Required(candidate.Raw, candidate.Platform, "").Select(value => Key(value.Platform, value.Project) + ":" + value.Version).OrderBy(value => value, StringComparer.Ordinal));
                    if (!SameText(otherRequirements, requirements)) continue;
                    nextVersion = candidate; break;
                }
                if (nextVersion is null) throw new IOException(error.Message + "\r\n没有找到其他可验证的兼容候选。", error);
                host.Info("前置候选不兼容，尝试较早版本：" + entry.Name + " " + host.Display(nextVersion.Version));
                entry.Version = nextVersion.Version; entry.Raw = nextVersion.Raw;
            }
        }
        throw new IOException("兼容版本尝试次数已达上限，未自动安装。");
    }
    private int CandidateIndex(TPlan plan, string stage, IEnumerable<string> paths) {
        for (int index = 0; index < plan.Items.Count; index++) {
            string candidatePath = StagedFile(plan, stage, index);
            if (paths.Any(value => SameFilePath(value, candidatePath))) return index;
        }
        return -1;
    }
    private int InstallAttempt(TPlan plan, CancellationToken token, Action<List<TNetFile>, CancellationToken>? download) {
        token.ThrowIfCancellationRequested();
        if (plan.Items.Count == 0 && plan.Enables.Count == 0) return 0;
        TReport before = plan.Report.PackOrigin?.IsPack == true
            ? ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.ScanRaw(plan.Root, plan.Report.Context, token, true)
            : Inspect(plan.Root, plan.Report.Context, token);
        before.PackOrigin = plan.Report.PackOrigin?.Refresh(token);
        if (!SameText(Fingerprint(before), plan.Stamp)) throw new IOException("模组文件已变化，请重新启动以检查依赖。");
        ValidatePackPolicy(plan, before.PackOrigin);
        var replacements = plan.Items.Where(item => item.Replacement is not null).Select(item => item.Replacement!).ToArray();
        string stage = Path.Combine(plan.Root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        List<Tuple<string, string?>> created = new();
        List<TActivation> enabled = new();
        host.EnsureWithin(plan.Root, stage); Directory.CreateDirectory(stage);
        try {
            List<TNetFile> files = new();
            for (int index = 0; index < plan.Items.Count; index++) {
                TItem entry = plan.Items[index];
                if (!Compatible(entry.Version, before.Context)) throw new IOException("前置文件的版本或下载信息已经变化。");
                files.Add(host.ToNetFile(entry.Version, StagedFile(plan, stage, index), before.Context.MinecraftVersion, Loader(before.Context)));
            }
            download ??= (downloads, cancellation) => host.Download(plan, downloads, cancellation);
            if (files.Count > 0) download(files, token);
            for (int index = 0; index < plan.Items.Count; index++) {
                token.ThrowIfCancellationRequested();
                TItem entry = plan.Items[index]; string fullPath = StagedFile(plan, stage, index);
                if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != host.Size(entry.Version) || !SameText(host.FileHash(fullPath, host.Hash(entry.Version)!.Length == 32), host.Hash(entry.Version)))
                    throw new IOException("下载的前置文件校验失败，未加入模组目录。");
            }
            TReport added = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.ScanRaw(stage, before.Context, token, true);
            if (added.MetadataIncomplete) throw new IOException("前置文件的元数据未能完整识别，未自动安装。\r\n文件：" +
                string.Join("、", plan.Items.Select(item => host.FileName(item.Version))) + "\r\n" + string.Join("\r\n", added.Notices.Take(8)));
            TReport ready = WithEnabledFiles(before, plan.Enables.Select(item => item.Source), token);
            HashSet<string> existingIds = new(ready.Mods.Where(value => value.Enabled && !value.IsNested && !replacements.Any(replacement => SameFilePath(replacement.Source, value.RootPath))).Select(value => value.Id), StringComparer.Ordinal);
            Dictionary<string, string> addedIds = new(StringComparer.Ordinal);
            List<int> chosen = new();
            for (int index = 0; index < plan.Items.Count; index++) {
                string fullPath = StagedFile(plan, stage, index);
                List<TMod> mods = added.Mods.Where(value => SameFilePath(value.RootPath, fullPath) && !value.IsNested).ToList();
                if (mods.Count == 0) throw new IOException("下载文件没有可识别的模组声明。");
                if (!mods.Any(value => !SameText(value.Side, "server"))) throw new IOException("下载的前置仅支持服务端，未加入客户端模组目录。");
                if (mods.Any(value => existingIds.Contains(value.Id))) {
                    if (before.PackOrigin?.IsPack == true) throw new IOException("候选前置与最终保留模组声明相同 ID，未向整合包添加重复模组：" + host.FileName(plan.Items[index].Version));
                    string hash = host.FileHash(fullPath, false);
                    if (mods.Any(value => addedIds.ContainsKey(value.Id) && !SameText(addedIds[value.Id], hash))) throw new IOException("多个前置文件声明同一个模组 ID，未自动安装。");
                    continue;
                }
                chosen.Add(index);
                foreach (TMod value in mods) { existingIds.Add(value.Id); addedIds[value.Id] = host.FileHash(fullPath, false); }
            }
            TReport combined = new() { Context = before.Context, Builtins = before.Builtins, MetadataIncomplete = before.MetadataIncomplete };
            combined.PackOrigin = before.PackOrigin;
            combined.Mods.AddRange(ready.Mods.Where(value => !replacements.Any(replacement => SameFilePath(replacement.Source, value.RootPath))));
            combined.Mods.AddRange(added.Mods.Where(value => chosen.Any(index => SameFilePath(value.RootPath, StagedFile(plan, stage, index)))));
            BuildReport(combined, token);
            if (replacements.Length > 0 && (combined.MetadataIncomplete || combined.HasErrors ||
                combined.Edges.Any(edge => edge.Owner.Enabled && !edge.Owner.IsCandidate && edge.Requirement.KindValue == ModDependencyKind.Required &&
                    (edge.Requirement.IsUnknown || edge.Evaluated && edge.StateValue != ModDependencyMatchState.Satisfied))))
                throw new IOException("作者模组替换后的整包依赖与冲突尚未全部验证通过，未修改任何模组。\r\n" +
                    string.Join("\r\n", combined.Issues.Where(issue => issue.IsError).Take(8).Select(issue => issue.Message)));
            if (replacements.Length > 0) {
                var loaders = new Dictionary<ModLoaders, string>();
                foreach (var pair in before.Context.LoaderVersions) {
                    ModLoaders kind = pair.Key switch { "fabricloader" => ModLoaders.Fabric, "forge" => ModLoaders.Forge,
                        "neoforge" => ModLoaders.NeoForge, "liteloader" => ModLoaders.LiteLoader, _ => ModLoaders.None };
                    if (kind != ModLoaders.None && pair.Value is not null) loaders[kind] = pair.Value;
                }
                if (before.Context.LoaderVersions.ContainsKey("quilt_loader")) throw new IOException("当前完整整包替换验证尚不能确认 Quilt 加载上下文，未自动替换作者版本。");
                var finalPaths = before.PackOrigin!.Files.Where(file => !file.Disabled && file.LocalRelative is not null && file.ActualHash is not null)
                    .Select(file => Path.Combine(before.PackOrigin.GameRoot, file.LocalRelative!.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(path => !replacements.Any(replacement => SameFilePath(replacement.Source, path))).ToList();
                finalPaths.AddRange(chosen.Select(index => StagedFile(plan, stage, index)));
                // The graph reader deliberately ignores disabled inputs. Validate each
                // explicitly proposed activation through an active-named staged copy.
                for (int index = 0; index < plan.Enables.Count; index++) {
                    TActivation activation = plan.Enables[index];
                    string copy = Path.Combine(stage, "activated", index + "-" + Path.GetFileName(activation.Destination));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    FileTaskProgress.Copy(activation.Source, copy, token, null);
                    if (!SameText(TransactionHash(copy), "sha1:" + activation.Sha1)) throw new IOException("待启用的个人前置已变化，未替换作者版本。");
                    finalPaths.Add(copy);
                }
                PackDependencyGraphResult graph = PackDependencyGraph.Check(finalPaths, before.Context.MinecraftVersion, loaders, token, before.Context.JavaVersion);
                if (graph.HasErrors || graph.HasUnknown) throw new IOException("作者替换方案的完整依赖、冲突或提供别名仍有错误或未知，未自动安装。\r\n" +
                    string.Join("\r\n", graph.Errors.Concat(graph.Warnings).Take(12)));
            }
            // Keep LF's existing Windows path test; this port adds no cross-platform policy.
            List<TEdge> unmet = combined.Edges.Where(value => value.Owner.RootPath.StartsWith(stage + "\\", StringComparison.OrdinalIgnoreCase) && value.Evaluated &&
                value.Requirement.KindValue == ModDependencyKind.Required && value.StateValue != ModDependencyMatchState.Satisfied).ToList();
            unmet.AddRange(combined.Edges.Where(value => plan.Enables.Any(item => SameFilePath(item.Source, value.Owner.RootPath)) && value.Evaluated && value.Requirement.KindValue == ModDependencyKind.Required && value.StateValue != ModDependencyMatchState.Satisfied));
            if (unmet.Count > 0) throw host.CandidateMismatch(CandidateIndex(plan, stage, unmet.Select(value => value.Owner.RootPath)), "前置文件仍有未满足或无法确认的必需依赖，未自动安装：\r\n" +
                string.Join("\r\n", unmet.Take(5).Select(value => value.Owner.Display + " 需要 " + value.Requirement.Display)));
            HashSet<string> previousIssues = new(before.Issues.Select(value => value.KindValue + "|" + value.Message), StringComparer.Ordinal);
            List<TIssue> introduced = combined.Issues.Where(value => (value.IsError || (value.KindValue == ModDependencyIssueKind.MissingRequired && value.Owner.RootPath.StartsWith(stage + "\\", StringComparison.OrdinalIgnoreCase))) &&
                !previousIssues.Contains(value.KindValue + "|" + value.Message)).ToList();
            if (introduced.Count > 0) throw host.CandidateMismatch(CandidateIndex(plan, stage, introduced.Select(value => value.Owner.RootPath).Concat(introduced.SelectMany(value => value.RelatedMods).Select(value => value.RootPath))),
                "前置文件会引入依赖缺失、版本不匹配或冲突，未自动安装：\r\n" + string.Join("\r\n", introduced.Take(5).Select(value => value.Message)));
            TReport current = before.PackOrigin?.IsPack == true
                ? ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.ScanRaw(plan.Root, before.Context, token, true)
                : Inspect(plan.Root, before.Context, token);
            current.PackOrigin = plan.Report.PackOrigin?.Refresh(token);
            if (!SameText(Fingerprint(current), plan.Stamp)) throw new IOException("下载期间模组文件发生变化，已停止自动安装。");
            ValidatePackPolicy(plan, current.PackOrigin);
            foreach (TActivation entry in plan.Enables) {
                host.EnsureWithin(plan.Root, entry.Source); host.EnsureWithin(plan.Root, entry.Destination);
                if (File.Exists(entry.Destination) || Directory.Exists(entry.Destination) || !SameText(host.FileHash(entry.Source, false), entry.Sha1)) throw new IOException("已禁用的前置或目标文件已变化，未自动启用。");
            }
            if (current.PackOrigin?.IsPack == true) return CommitPackPlan(plan, stage, chosen, current.PackOrigin, token);
            foreach (TActivation entry in plan.Enables) {
                token.ThrowIfCancellationRequested(); File.Move(entry.Source, entry.Destination); enabled.Add(entry);
            }
            foreach (int index in chosen) {
                token.ThrowIfCancellationRequested();
                TItem entry = plan.Items[index]; string destination = Path.Combine(plan.Root, host.FileName(entry.Version)!);
                host.EnsureWithin(plan.Root, destination);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("已有同名文件，未覆盖：" + host.FileName(entry.Version));
                File.Move(StagedFile(plan, stage, index), destination); created.Add(Tuple.Create(destination, host.Hash(entry.Version)));
            }
            token.ThrowIfCancellationRequested();
            return checked(created.Count + enabled.Count);
        } catch {
            foreach (Tuple<string, string?> entry in created)
                if (File.Exists(entry.Item1) && SameText(host.FileHash(entry.Item1, entry.Item2!.Length == 32), entry.Item2)) File.Delete(entry.Item1);
            foreach (TActivation entry in enabled.AsEnumerable().Reverse())
                if (!File.Exists(entry.Source) && File.Exists(entry.Destination) && SameText(host.FileHash(entry.Destination, false), entry.Sha1)) File.Move(entry.Destination, entry.Source);
            throw;
        } finally {
            try { host.DeleteStage(plan.Root, stage); }
            catch (IOException error) { host.Warn(error, "前置下载临时文件暂时被占用，已保留在 " + stage); }
        }
    }
    private string? TransactionHash(string path) => File.Exists(path) ? "sha1:" + host.FileHash(path, false) : null;
    private void ValidatePackPolicy(TPlan plan, PackDependencyOrigin? origin) {
        if (origin?.IsPack != true) {
            if (plan.Items.Any(item => item.Replacement is not null)) throw new IOException("作者来源记录已变化，不能替换作者模组。");
            return;
        }
        if (!origin.Complete || !SameFilePath(Path.Combine(origin.GameRoot, "mods"), plan.Root))
            throw new IOException("整合包作者来源未完整确认，未自动修改模组。");
        if (host.ApplyTransaction is null) throw new IOException("整合包安全事务不可用，未自动修改模组。");
        foreach (TActivation entry in plan.Enables) {
            if (!origin.IsPersonal(entry.Source) || origin.Files.Any(file => file.Role != PackDependencyFileRole.PersonalAdded &&
                (SameFilePath(Path.Combine(origin.GameRoot, file.Relative.Replace('/', Path.DirectorySeparatorChar)), entry.Destination) ||
                 file.LocalRelative is not null && SameFilePath(Path.Combine(origin.GameRoot, file.LocalRelative.Replace('/', Path.DirectorySeparatorChar)), entry.Destination))))
                throw new IOException("不能把作者组件作为个人前置自动启用或覆盖。");
        }
        foreach (TItem item in plan.Items) {
            string rawProject = S(item.Raw[SameText(item.Platform, "Modrinth") ? "project_id" : "modId"]);
            bool protectedProject = origin.ProtectsProject(item.Platform, item.ProjectId) || origin.ProtectsProject(item.Platform, rawProject);
            if (protectedProject && item.Replacement is null) throw new IOException("作者项目只能通过已确认的整包替换方案处理，不能重复下载其他版本。");
            if (item.Replacement is null) continue;
            if (!plan.AuthorReplacementConfirmed) throw new IOException("作者模组替换尚未得到明确确认，未修改整合包。");
            DependencyReplacementData? expected = FindAuthorReplacement(plan.Report, item.Platform, item.ProjectId);
            if (!protectedProject || expected is null || !SameFilePath(expected.Source, item.Replacement.Source) ||
                !SameText(expected.AuthorRelative, item.Replacement.AuthorRelative) || !SameText(expected.ProjectIdentity, item.Replacement.ProjectIdentity) ||
                !SameText(expected.BeforeHash, item.Replacement.BeforeHash) || !SameText(TransactionHash(item.Replacement.Source), item.Replacement.BeforeHash))
                throw new IOException("作者模组替换来源已变化或存在歧义，未自动替换。");
        }
        if (plan.Items.Where(item => item.Replacement is not null).GroupBy(item => FilePathKey(item.Replacement!.Source), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new IOException("同一作者文件存在多个替换候选，未自动选择。");
    }
    private int CommitPackPlan(TPlan plan, string stage, List<int> chosen, PackDependencyOrigin origin, CancellationToken token) {
        var operations = new List<Tuple<string, string?, string?>>();
        var overrides = new List<PackDependencyOverride>();
        foreach (TActivation entry in plan.Enables) {
            operations.Add(Tuple.Create(entry.Destination, (string?)entry.Source, (string?)null));
            operations.Add(Tuple.Create(entry.Source, (string?)null, (string?)("sha1:" + entry.Sha1)));
        }
        foreach (int index in chosen) {
            TItem item = plan.Items[index];
            string destination = Path.Combine(plan.Root, host.FileName(item.Version)!);
            host.EnsureWithin(plan.Root, destination);
            string? before = TransactionHash(destination);
            if (before is not null && (item.Replacement is null || !SameFilePath(item.Replacement.Source, destination)))
                throw new IOException("已有同名文件，未覆盖：" + host.FileName(item.Version));
            operations.Add(Tuple.Create(destination, (string?)StagedFile(plan, stage, index), before));
            if (item.Replacement is not null) {
                DependencyReplacementData replacement = item.Replacement;
                if (!SameFilePath(replacement.Source, destination)) operations.Add(Tuple.Create(replacement.Source, (string?)null, (string?)replacement.BeforeHash));
                overrides.Add(new PackDependencyOverride { AuthorRelative = replacement.AuthorRelative,
                    LocalRelative = "mods/" + host.FileName(item.Version), ActualHash = TransactionHash(StagedFile(plan, stage, index))!,
                    FileIdentity = item.Platform.ToLowerInvariant() + ":" + host.Id(item.Version), Reason = "个人模组依赖所需，整包验证并确认后替换" });
            }
        }
        if (overrides.Count > 0) {
            string payload = Path.Combine(stage, "pack-origin-next.json");
            File.WriteAllText(payload, origin.BuildOverrideManifest(overrides, token).ToString());
            string target = Path.Combine(origin.VersionFolder, "PCL", "ModpackOrigin.json");
            operations.Add(Tuple.Create(target, (string?)payload, TransactionHash(target)));
        }
        PackDependencyOrigin fresh = origin.Refresh(token);
        if (!SameText(fresh.Fingerprint, origin.Fingerprint)) throw new IOException("整合包来源或本地文件已变化，未提交补全方案。");
        ValidatePackPolicy(plan, fresh);
        host.ApplyTransaction!(origin.GameRoot, origin.VersionFolder, operations, token);
        return checked(chosen.Count + plan.Enables.Count);
    }
}
