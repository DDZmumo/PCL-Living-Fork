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
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateFlowEngine<TFile, TPackage, TChange, TPlan, TNetFile, TVersion, TInstance, TTask, TCombo, TWorker, TProgress>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange>
    where TVersion : ResourceVersionModelData where TInstance : class where TTask : class where TCombo : class where TWorker : class where TProgress : class {
    public void Execute(TInstance instance, bool online, string? archivePath, TTask task, string? requestedVersion = null) {
        CancellationToken token = host.Cancellation(task); string stage = host.TempFolder();
        void Stage(PackUpdateStage phase, string? detail = null) => host.SetStage(task, phase, PackUpdateStageState.Running, detail);
        void Done(PackUpdateStage phase) => host.SetStage(task, phase, PackUpdateStageState.Finished, null);
        Stage(PackUpdateStage.ReadRecord);
        host.SetProgress(task, 0.03);
        if (update.Pending(host.VersionFolder(instance)).Any()) {
            if (host.Message("上次整合包更新被中断，需要先恢复更新前的文件。", "恢复整合包", "恢复", "取消", true) != 1) throw new OperationCanceledException();
            instance = RecoverInstance(instance);
        }
        string root = host.GameRoot(instance);
        string originPath = update.Target(host.VersionFolder(instance), PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.ManifestName);
        string? originBefore = PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Digest(originPath, token: token);
        JObject? baseline = update.ReadManifest(host.VersionFolder(instance));
        EnsureOriginUnchanged();
        double inputStart = 0.03;
        if (PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.RequiresBaselineRepair(baseline)) {
            TPackage? original = null;
            Stage(PackUpdateStage.ReadRecord, "获取并核对当前版本原始整合包");
            string? automaticFailure = null;
            if (baseline is not null) {
                try {
                    original = DownloadRecordedOriginal(baseline, stage, token, host.Progress(task, 0.03, 0.08));
                    if (original is not null) ValidateOriginalPackage(original, baseline, host.Version(instance).VanillaName, host.Version(instance).ModLoaderVersions);
                } catch (OperationCanceledException) { throw; }
                catch (Exception error) when (!token.IsCancellationRequested) {
                    original = null;
                    automaticFailure = "自动获取或核对当前原包未完成，请手动选择。\r\n";
                    host.Info("自动补全整合包作者记录未完成：" + error.Message);
                }
            }
            string known = baseline is null ? "" : "\r\n已记录：" + baseline["name"] + " " + baseline["version"];
            if (original is null) {
                if (host.Message(automaticFailure + "作者文件记录缺失或不完整。请选择当前版本原始整合包，以恢复作者声明并区分个人修改。" + known, "补全整合包记录", "选择原始包", "取消", false) != 1) throw new OperationCanceledException();
                original = update.ReadPackage(SelectArchive("选择当前版本原始整合包"), Path.Combine(stage, "original"), token);
                ValidateOriginalPackage(original, baseline, host.Version(instance).VanillaName, host.Version(instance).ModLoaderVersions);
                original.Source = baseline?["source"] as JObject;
            }
            baseline = PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Manifest(original);
            inputStart = 0.08; host.SetProgress(task, inputStart);
        }
        JObject? source = null;
        Done(PackUpdateStage.ReadRecord);
        Stage(PackUpdateStage.PreparePackage, "选择并下载新版整合包");
        if (online) archivePath = ChooseOnline(baseline!, stage, token, ref source, host.Progress(task, inputStart, 0.12), requestedVersion);
        host.SetProgress(task, 0.12);
        Stage(PackUpdateStage.PreparePackage, "读取新版文件清单");
        TPackage package = update.ReadPackage(archivePath!, Path.Combine(stage, "next"), token);
        package.Source = source;
        EnsureOriginUnchanged();
        Done(PackUpdateStage.PreparePackage);
        Stage(PackUpdateStage.CompareFiles);
        TPlan plan = update.Plan(root, baseline, package, token);
        host.SetProgress(task, 0.15);
        Done(PackUpdateStage.CompareFiles);
        Stage(PackUpdateStage.PrepareFiles);
        update.DownloadChanges(plan, stage, token, (files, cancel) => host.Download(files, cancel, host.Progress(task, 0.15, 0.65)),
            text => Stage(PackUpdateStage.PrepareFiles, text), value => host.SetStageProgress(task, PackUpdateStage.PrepareFiles, value));
        host.SetProgress(task, 0.65);
        bool runtimeChanged = !Same(host.Version(instance).VanillaName, package.Minecraft) || host.Version(instance).ModLoaderVersions.Count != package.Loaders.Count ||
            package.Loaders.Any(pair => !host.Version(instance).ModLoaderVersions.ContainsKey(pair.Key) || !Same(host.Version(instance).ModLoaderVersions[pair.Key], pair.Value));
        Dictionary<string, string?> runtimeBefore = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in new[] { host.Name(instance) + ".json", host.Name(instance) + ".jar", "PCL/config.json" })
            runtimeBefore[relative] = PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Digest(update.Target(host.VersionFolder(instance), relative), token: token);
        string summary = package.Name + " " + package.Version + "\r\n" + $"默认执行作者变更 {plan.Changes.Count(item => item.ApplyAuthor)} 项，保留或待确认 {plan.Changes.Count(item => !item.ApplyAuthor)} 项；冲突可在预览中逐项选择。" + "\r\n" +
            (runtimeChanged ? "Minecraft 或加载器将更新；存档会保留，请确认新版本兼容。" : "");
        Done(PackUpdateStage.PrepareFiles);
        host.SetStage(task, PackUpdateStage.ConfirmPreview, PackUpdateStageState.AwaitingConfirmation, "等待玩家确认，尚未应用文件");
        if (host.Preview(summary, plan, () => host.Version(instance)) != 1) throw new OperationCanceledException();
        Done(PackUpdateStage.ConfirmPreview);
        Stage(PackUpdateStage.ValidateChoices);
        PackDependencyGraphResult dependencies = update.CheckDependencies(plan, token);
        plan.DependencyCheck = dependencies;
        if (dependencies.HasErrors) throw new IOException("所选更新后的模组组合存在依赖或冲突问题，未提交更新。请调整作者版本与个人覆盖的选择：\r\n" + string.Join("\r\n", dependencies.Errors));
        if (dependencies.HasUnknown) {
            if (host.Message("以下声明无法完整确认，未据此宣称兼容。请核对所选作者版本、个人模组及目标运行时：\r\n" + string.Join("\r\n", dependencies.Warnings),
                "更新组合兼容性待确认", "仍然更新", "取消", true) != 1) throw new OperationCanceledException();
            plan.AllowUnverifiedDependencies = true;
        }
        update.ValidateChoices(plan, token);
        Done(PackUpdateStage.ValidateChoices);
        Dictionary<string, string> extras = new(StringComparer.OrdinalIgnoreCase);
        if (runtimeChanged) {
            Stage(PackUpdateStage.PrepareRuntime);
            string buildFolder = Path.Combine(stage, "runtime", host.Name(instance)) + "\\";
            var request = new PackUpdateRuntimeRequest { Name = host.Name(instance), BuildFolder = buildFolder, Minecraft = package.Minecraft!, Loaders = package.Loaders,
                MinecraftFolder = Path.GetDirectoryName(Path.GetDirectoryName(host.VersionFolder(instance).TrimEnd('\\'))) + "\\" };
            TCombo build = host.CreateRuntime(request);
            Action prepareRuntime = host.RuntimeWorkload(build, buildFolder, host.Progress(task, 0.65, 0.85));
            using (token.Register(() => host.CancelCombo(build))) prepareRuntime();
            foreach (string extension in new[] { ".json", ".jar" }) {
                string payload = Path.Combine(buildFolder, host.Name(instance) + extension);
                if (File.Exists(payload)) extras.Add(host.Name(instance) + extension, payload);
            }
            if (!extras.ContainsKey(host.Name(instance) + ".json")) throw new IOException("新版本游戏组件未生成。");
            string existingConfig = Path.Combine(host.VersionFolder(instance), "PCL", "config.json");
            host.EnsureWithin(host.VersionFolder(instance), existingConfig);
            JObject config = File.Exists(existingConfig) ? JObject.Parse(File.ReadAllText(existingConfig)) : new JObject();
            JObject newConfig = JObject.Parse(File.ReadAllText(Path.Combine(buildFolder, "PCL", "config.json")));
            config["MinecraftInstallManifest"] = newConfig["MinecraftInstallManifest"]?.DeepClone();
            string configPayload = Path.Combine(stage, "updated-config.json");
            File.WriteAllText(configPayload, config.ToString()); extras.Add("PCL/config.json", configPayload);
            Done(PackUpdateStage.PrepareRuntime);
        }
        else host.SetStage(task, PackUpdateStage.PrepareRuntime, PackUpdateStageState.Skipped, "Minecraft 与加载器版本未变化，无需更新");
        Stage(PackUpdateStage.MigrateSettings, "复查当前环境与运行状态");
        token.ThrowIfCancellationRequested();
        if (host.HasRunningGame() || host.HasRunningSessions() || host.LaunchLoading()) throw new IOException("游戏已开始运行，未替换文件。");
        foreach (var pair in runtimeBefore)
            if (!Same(PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Digest(update.Target(host.VersionFolder(instance), pair.Key), token: token), pair.Value)) throw new IOException("游戏组件或版本设置在预览后发生变化，请重新检查更新。");
        host.SetProgress(task, 0.85);
        Stage(PackUpdateStage.MigrateSettings);
        host.BeforeMinecraftChange(instance, host.Version(instance).VanillaName, package.Minecraft!, token);
        Done(PackUpdateStage.MigrateSettings);
        host.SetProgress(task, 0.87); Stage(PackUpdateStage.CaptureHistory); host.CaptureHistory(instance, "整合包更新前", token);
        Done(PackUpdateStage.CaptureHistory);
        Stage(PackUpdateStage.BackupFiles);
        host.SetProgress(task, 0.9);
        EnsureOriginUnchanged();
        update.ValidateChoices(plan, token);
        int operationCount = update.PlannedOperationCount(plan, token) + extras.Count + 1;
        bool applying = false;
        string backup = update.Commit(plan, host.VersionFolder(instance), token, extras,
            index => {
                // The first callback runs only after all original files and the
                // transaction journal have been durably staged by Commit.
                if (index == 0) Done(PackUpdateStage.BackupFiles);
                applying = true;
                Stage(PackUpdateStage.ApplyFiles, "文件 " + (index + 1) + "/" + operationCount);
            },
            value => {
                host.SetProgress(task, 0.9 + 0.08 * Math.Min(1, Math.Max(0, value)));
                host.SetStageProgress(task, applying ? PackUpdateStage.ApplyFiles : PackUpdateStage.BackupFiles,
                    applying ? (value - 0.45) / 0.55 : value / 0.45);
            });
        Done(PackUpdateStage.ApplyFiles);
        host.SetProgress(task, 0.98); host.SetOutput(task, backup);
        Stage(PackUpdateStage.RefreshInstance);
        ReloadInstance(instance); host.Info("整合包更新完成，原文件备份：" + backup);
        Done(PackUpdateStage.RefreshInstance);
        void EnsureOriginUnchanged() {
            if (!Same(originBefore, PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Digest(originPath, token: token)))
                throw new IOException("整合包作者记录在处理期间发生变化，请重新检查更新；未覆盖变动后的记录。");
        }
    }
    // A same-Minecraft archive is insufficient evidence for a legacy, possibly
    // pruned origin record. Keep every surviving author declaration as a check.
    private static void ValidateOriginalPackage(TPackage original, JObject? baseline, string minecraft, Dictionary<ModLoaders, string> currentLoaders) {
        if (!Same(minecraft, original.Minecraft)) throw new IOException("原始整合包的 Minecraft 版本与当前版本不同，请选择正确的旧包。");
        if (currentLoaders.Count != original.Loaders.Count || original.Loaders.Any(pair => !currentLoaders.TryGetValue(pair.Key, out string? value) || !Same(value, pair.Value)))
            throw new IOException("原始整合包的加载器与当前版本不同，请选择正确的旧包。");
        if (baseline is null) return;
        foreach ((string key, string? value) in new[] { ("name", original.Name), ("version", original.Version), ("minecraft", original.Minecraft) })
            if (!string.IsNullOrWhiteSpace(baseline[key]?.ToString()) && !Same(baseline[key]!.ToString(), value))
                throw new IOException("所选旧包与已记录的整合包信息不同（" + key + "），未更改作者记录。");
        if (baseline["loaders"] is JObject loaders && !JToken.DeepEquals(loaders, JObject.FromObject(original.Loaders)))
            throw new IOException("所选旧包与已记录的加载器信息不同，未更改作者记录。");
        if (baseline["files"] is not JObject files) return;
        foreach (JProperty item in files.Properties()) {
            if (item.Value is not JObject record || string.IsNullOrWhiteSpace(record["hash"]?.ToString())) continue;
            if (!original.Files.TryGetValue(item.Name, out TFile? declared))
                throw new IOException("所选旧包不能核对已记录的作者文件：" + item.Name + "。请检查是否为正确的旧包或已有记录是否损坏。");
            string recordedHash = record["hash"]!.ToString();
            if (!Same(recordedHash, declared.Hash) && (declared.Payload is null || !PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Matches(declared.Payload, recordedHash)))
                throw new IOException("所选旧包无法按原记录的哈希核对作者文件：" + item.Name + "。请检查旧包、校验算法或已有记录；未使用本地修改内容补写作者基线。");
            string? project = record["identity"]?.ToString();
            if (!string.IsNullOrEmpty(project) && !Same(project, declared.Identity))
                throw new IOException("所选旧包的项目来源与已记录文件不同：" + item.Name);
        }
    }
}
