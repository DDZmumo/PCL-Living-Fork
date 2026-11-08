// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    private static void ValidateAssociationBindings(TPlan plan) {
        foreach (TChange change in PlanChanges(plan).Where(change => change.ApplyAuthor)) {
            string destination = RelativePath(change.DestinationRelative);
            string? local = change.LocalRelative is null ? null : RelativePath(change.LocalRelative);
            if (IsPrivate(destination) || IsRuntimeData(destination) || local is not null && (IsPrivate(local) || IsRuntimeData(local)))
                throw new IOException("更新操作涉及受保护的启动器记录或玩家运行数据，未提交：" + change.Relative);
            if (change.Operation == PackUpdateOperation.EnablePersonal) {
                if (local is null || !DisabledFile(local) || !LoadedModPath(plan.Package!, destination) ||
                    !string.Equals(local[..local.LastIndexOf('.')], destination, StringComparison.OrdinalIgnoreCase) ||
                    change.Incoming is null || !Same(change.Before, change.Incoming.Hash) || change.BaselineRelative is not null ||
                    change.Ownership != PackUpdateOwnership.Disabled || !change.PreviewOnly ||
                    !plan.Snapshot.TryGetValue(local, out string? observedSource) || !Same(observedSource, change.Before) ||
                    !plan.Snapshot.TryGetValue(destination, out string? observedTarget) || observedTarget is not null ||
                    plan.Package!.Files.ContainsKey(destination) || plan.Package.Files.ContainsKey(local))
                    throw new IOException("个人前置缺少唯一的原禁用文件和空闲目标，未启用：" + change.Relative);
            }
            if (local is null) continue;
            if (ModFile(local)) {
                if (!ModFile(change.Relative!)) throw new IOException("模组来源不能用于其他类别的文件：" + change.Relative);
                continue;
            }
            string? original = change.BaselineRelative is null ? null : RelativePath(change.BaselineRelative);
            if (original is null || !string.Equals(original, local, StringComparison.OrdinalIgnoreCase) || plan.Baseline?["files"]?[original] is not JObject declared)
                throw new IOException("普通内容缺少原声明路径关联，未处理其他文件：" + local);
            if (change.Incoming is null || string.Equals(original, change.Relative, StringComparison.OrdinalIgnoreCase)) continue;
            if (change.Association != PackUpdateAssociation.PlatformProject || !PlatformArchive(original) || !PlatformArchive(change.Relative!) ||
                ContentRole(original) != ContentRole(change.Relative!) || string.IsNullOrEmpty(change.Incoming.Identity) || declared["identity"]?.ToString() != change.Incoming.Identity)
                throw new IOException("普通内容不支持按相同字节跨路径替换：" + original + " / " + change.Relative);
        }
    }
    private List<Tuple<string, string?, string?>> BuildFileOperations(TPlan plan, CancellationToken token) {
        List<Tuple<string, string?, string?>> operations = new();
        foreach (TChange change in PlanChanges(plan).Where(item => item.ApplyAuthor)) {
            token.ThrowIfCancellationRequested();
            string? local = change.LocalRelative;
            if (local is not null && !Same(Digest(Target(plan.Root!, local), token: token), change.Before)) throw new IOException("文件已被其他程序修改，请重新检查更新：" + local);
            if (change.Operation == PackUpdateOperation.DisablePersonal) {
                string disabled = Target(plan.Root!, change.DestinationRelative);
                if (!plan.Snapshot.TryGetValue(change.DestinationRelative, out string? disabledBefore) || disabledBefore is not null || File.Exists(disabled))
                    throw new IOException("禁用目标已经存在，请重新检查：" + change.DestinationRelative);
                operations.Add(Tuple.Create(disabled, (string?)Target(plan.Root!, local!), (string?)null));
                operations.Add(Tuple.Create(Target(plan.Root!, local!), (string?)null, change.Before));
                continue;
            }
            if (change.Incoming is null) {
                if (local is not null) operations.Add(Tuple.Create(Target(plan.Root!, local), (string?)null, change.Before));
                continue;
            }
            if ((change.DisableAfterUpdate || change.Ownership == PackUpdateOwnership.Disabled && !change.KeepDisabled) &&
                (!plan.Snapshot.TryGetValue(change.DestinationRelative, out string? switchBefore) ||
                    switchBefore is not null && !string.Equals(local, change.DestinationRelative, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("启用／禁用目标已经存在或未经预览，未覆盖：" + change.DestinationRelative);
            string fileName = Target(plan.Root!, change.DestinationRelative);
            string? targetBefore = plan.Snapshot.TryGetValue(change.DestinationRelative, out string? observed) ? observed : change.TargetBefore;
            if (!Same(Digest(fileName, token: token), targetBefore)) throw new IOException("文件已被其他程序修改，请重新检查更新：" + change.DestinationRelative);
            operations.Add(Tuple.Create(fileName, change.Incoming.Payload, targetBefore));
            if (local is not null && !local.Equals(change.DestinationRelative, StringComparison.OrdinalIgnoreCase))
                operations.Add(Tuple.Create(Target(plan.Root!, local), (string?)null, change.Before));
        }
        return operations;
    }
    public int PlannedOperationCount(TPlan plan, CancellationToken token = default) => BuildFileOperations(plan, token).Count;

    private void ValidatePlannedOperations(TPlan plan, CancellationToken token) {
        ValidateAssociationBindings(plan);
        foreach (TChange change in PlanChanges(plan).Where(change => change.DisableAfterUpdate || change.Ownership == PackUpdateOwnership.Disabled && !change.KeepDisabled))
            if (!change.AllowAuthor || change.DisplayOnly || change.Ownership is PackUpdateOwnership.Ambiguous or PackUpdateOwnership.Unknown)
                throw new IOException("此项归属尚未确认，不能切换启用状态：" + change.Relative);
        foreach (TChange change in PlanChanges(plan)) if (change.Choice == PackUpdateChoice.UseAuthor && (!change.AllowAuthor || change.DisplayOnly))
            throw new IOException("此项归属或重复冲突尚未确认，不能自动替换：" + change.Relative);
        var operations = BuildFileOperations(plan, token);
        if (operations.GroupBy(operation => Path.GetFullPath(operation.Item1), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new IOException("更新存在重复的目标操作，请先确认启用／禁用目标，未修改当前实例。");
    }

    // The complete original transaction boundary: persist backups/log before writing,
    // keep the current before/after guards and reverse rollback under the caller's real workload lock.
    public string Commit(TPlan plan, string versionFolder, CancellationToken token, Dictionary<string, string>? extraFiles = null, Action<int>? beforeApply = null, Action<double>? progress = null) {
        token.ThrowIfCancellationRequested();
        ValidateChoices(plan, token);
        var operations = BuildFileOperations(plan, token);
        if (extraFiles is not null) foreach (var pair in extraFiles)
            operations.Add(Tuple.Create(Target(versionFolder, pair.Key), (string?)pair.Value, Digest(Target(versionFolder, pair.Key))));
        string box = Target(versionFolder, "PCL/PackUpdateBackups/" + Guid.NewGuid().ToString("N") + "/transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(box)!);
        string statePayload = Path.Combine(Path.GetDirectoryName(box)!, "next-manifest.json");
        JObject nextManifest = BuildNextManifest(plan, token);
        File.WriteAllText(statePayload, nextManifest.ToString());
        string statePath = Target(versionFolder, ManifestName);
        operations.Add(Tuple.Create(statePath, (string?)statePayload, Digest(statePath)));
        return ApplyTransactionWithProgress(plan.Root!, versionFolder, operations, token, progress, beforeApply, box);
    }
    public string ApplyTransaction(string gameRoot, string versionFolder, List<Tuple<string, string?, string?>> operations, CancellationToken token, Action<int>? beforeApply = null, string? box = null) =>
        ApplyTransactionWithProgress(gameRoot, versionFolder, operations, token, null, beforeApply, box);

    public string ApplyTransactionWithProgress(string gameRoot, string versionFolder, List<Tuple<string, string?, string?>> operations, CancellationToken token, Action<double>? progress, Action<int>? beforeApply = null, string? box = null) {
        token.ThrowIfCancellationRequested();
        if (operations.GroupBy(op => Path.GetFullPath(op.Item1), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new IOException("更新存在重复的目标操作，请先确认文件关联，未修改当前实例。");
        if (box is null) box = Target(versionFolder, "PCL/PackUpdateBackups/" + Guid.NewGuid().ToString("N") + "/transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(box)!);
        int checkedCount = 0, count = Math.Max(1, operations.Count);
        foreach (var op in operations) {
            if (PathUtils.IsParentOf(versionFolder, op.Item1)) host.EnsureWithin(versionFolder, op.Item1);
            else host.EnsureWithin(gameRoot, op.Item1);
            if (!Same(FileTaskProgress.Digest(op.Item1, token, FileTaskProgress.Slice(progress, 0.15 * checkedCount / count, 0.15 * checked(checkedCount + 1) / count)), op.Item3)) throw new IOException("文件已变化，请重新检查环境。");
            checkedCount = checked(checkedCount + 1);
        }
        JObject log = new(new JProperty("complete", false), new JProperty("gameRoot", Path.GetFullPath(gameRoot)), new JProperty("files", new JArray()));
        JArray logFiles = (JArray)log["files"]!;
        for (int index = 0; index < operations.Count; index++) {
            token.ThrowIfCancellationRequested();
            var op = operations[index]; string backup = Path.Combine(Path.GetDirectoryName(box)!, index + ".bak");
            if (op.Item3 is not null) FileTaskProgress.Copy(op.Item1, backup, token, FileTaskProgress.Slice(progress, 0.15 + 0.3 * index / count, 0.15 + 0.3 * checked(index + 1) / count));
            logFiles.Add(new JObject(new JProperty("target", op.Item1), new JProperty("backup", op.Item3 is null ? null : backup), new JProperty("before", op.Item3), new JProperty("after", op.Item2 is null ? null : Digest(op.Item2))));
        }
        token.ThrowIfCancellationRequested();
        File.WriteAllText(box, log.ToString());
        List<int> applied = new();
        try {
            for (int index = 0; index < operations.Count; index++) {
                beforeApply?.Invoke(index); token.ThrowIfCancellationRequested();
                var op = operations[index];
                Action<double>? fileProgress = FileTaskProgress.Slice(progress, 0.45 + 0.54 * index / count, 0.45 + 0.54 * checked(index + 1) / count);
                if (!Same(FileTaskProgress.Digest(op.Item1, token, FileTaskProgress.Slice(fileProgress, 0, 0.5)), op.Item3)) throw new IOException("提交期间文件发生变化，已停止更新。");
                applied.Add(index);
                if (op.Item2 is null) File.Delete(op.Item1); else ReplaceFromWithProgress(op.Item2, op.Item1, token, FileTaskProgress.Slice(fileProgress, 0.5, 1));
                fileProgress?.Invoke(1);
            }
            token.ThrowIfCancellationRequested();
            log["complete"] = true;
            host.AtomicWrite(box, Encoding.UTF8.GetBytes(log.ToString()));
            progress?.Invoke(1);
        } catch (Exception failure) {
            List<string> rollbackErrors = new();
            foreach (int index in applied.AsEnumerable().Reverse()) {
                JToken item = logFiles[index]; string destination = item["target"]!.ToString();
                try {
                    string? current = Digest(destination), before = item["before"]!.Type == JTokenType.Null ? null : item["before"]!.ToString(),
                        after = item["after"]!.Type == JTokenType.Null ? null : item["after"]!.ToString();
                    if (Same(current, before)) continue;
                    if (!Same(current, after)) { rollbackErrors.Add("文件已被外部修改，保留现场：" + destination); continue; }
                    if (item["backup"]!.Type == JTokenType.Null) File.Delete(destination);
                    else {
                        string backup = item["backup"]!.ToString();
                        host.EnsureWithin(Path.GetDirectoryName(box)!, backup);
                        if (!Same(Digest(backup), before)) { rollbackErrors.Add("更新备份校验失败，保留现场：" + destination); continue; }
                        ReplaceFrom(backup, destination);
                    }
                } catch (Exception rollbackFailure) { rollbackErrors.Add("回滚未完成：" + destination + "（" + rollbackFailure.Message + "）"); }
            }
            bool restored = rollbackErrors.Count == 0;
            log["complete"] = restored; log["rolledBack"] = restored;
            if (!restored) { log["rollbackIncomplete"] = true; log["rollbackErrors"] = new JArray(rollbackErrors); }
            host.AtomicWrite(box, Encoding.UTF8.GetBytes(log.ToString()));
            if (!restored) throw new IOException("自动回滚未完成，已保留外部修改与更新备份，请先检查恢复现场。\r\n" + string.Join("\r\n", rollbackErrors), failure);
            throw;
        }
        return Path.GetDirectoryName(box)!;
    }
    public static void ReplaceFrom(string source, string destination) => ReplaceFromWithProgress(source, destination, CancellationToken.None, null);

    public static void ReplaceFromWithProgress(string source, string destination, CancellationToken token, Action<double>? progress) {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".pcl-update-" + Guid.NewGuid().ToString("N");
        try {
            FileTaskProgress.Copy(source, temporary, token, progress);
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public List<string> Pending(string versionFolder) {
        string folder = Path.Combine(versionFolder, "PCL", "PackUpdateBackups");
        host.EnsureWithin(versionFolder, folder);
        List<string> result = new();
        if (!Directory.Exists(folder)) return result;
        foreach (string child in Directory.GetDirectories(folder)) {
            host.EnsureWithin(versionFolder, child);
            string logFile = Path.Combine(child, "transaction.json");
            host.EnsureWithin(versionFolder, logFile);
            if (File.Exists(logFile) && new FileInfo(logFile).Length <= 16 * 1024 * 1024 && !(bool)JObject.Parse(File.ReadAllText(logFile))["complete"]!) result.Add(logFile);
        }
        return result;
    }
    public void Recover(string versionFolder, string gameRoot) {
        foreach (string logFile in Pending(versionFolder)) {
            JObject log = JObject.Parse(File.ReadAllText(logFile)); JArray items = (JArray)log["files"]!;
            if (!PathUtils.IsSame(log["gameRoot"]!.ToString(), gameRoot)) throw new IOException("更新备份对应的游戏目录已经变化，请手动检查备份。");
            foreach (JToken item in items) {
                string destination = item["target"]!.ToString();
                if (PathUtils.IsParentOf(versionFolder, destination)) host.EnsureWithin(versionFolder, destination); else host.EnsureWithin(gameRoot, destination);
                string? current = Digest(destination), before = item["before"]!.Type == JTokenType.Null ? null : item["before"]!.ToString(), after = item["after"]!.Type == JTokenType.Null ? null : item["after"]!.ToString();
                if (!Same(current, before) && !Same(current, after)) throw new IOException("中断后文件又发生变化，已保留现场：" + destination);
                if (item["backup"]!.Type != JTokenType.Null) {
                    string backup = item["backup"]!.ToString();
                    host.EnsureWithin(Path.GetDirectoryName(logFile)!, backup);
                    if (!Same(Digest(backup), before)) throw new IOException("更新备份校验失败。");
                }
            }
            foreach (JToken item in items.Reverse()) {
                string destination = item["target"]!.ToString();
                if (item["backup"]!.Type == JTokenType.Null) { if (File.Exists(destination)) File.Delete(destination); }
                else ReplaceFrom(item["backup"]!.ToString(), destination);
            }
            log["complete"] = true; log["rolledBack"] = true;
            host.AtomicWrite(logFile, Encoding.UTF8.GetBytes(log.ToString()));
        }
    }
}
