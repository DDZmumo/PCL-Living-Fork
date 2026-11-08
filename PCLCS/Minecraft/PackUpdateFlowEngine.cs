// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateFlowEngine<TFile, TPackage, TChange, TPlan, TNetFile, TVersion, TInstance, TTask, TCombo, TWorker, TProgress>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange>
    where TVersion : ResourceVersionModelData where TInstance : class where TTask : class where TCombo : class where TWorker : class where TProgress : class {
    private readonly PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile> update;
    private readonly PackUpdateFlowHost<TPackage, TPlan, TNetFile, TVersion, TInstance, TTask, TCombo, TWorker, TProgress> host;
    public PackUpdateFlowEngine(PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile> update,
        PackUpdateFlowHost<TPackage, TPlan, TNetFile, TVersion, TInstance, TTask, TCombo, TWorker, TProgress> host) { this.update = update; this.host = host; }
    private static bool Same(string? left, string? right) => (left ?? "") == (right ?? "");
    public bool IsUpdating => host.ReadActive() != 0;
    public void Start(TInstance? instance, bool online, string? requestedVersion = null) {
        if (instance is null || host.EnvironmentBusy() || host.HasRunningGame() || host.HasRunningSessions() || host.LaunchLoading() || host.IsLocked(instance)) {
            host.HintRed("请先关闭游戏并等待版本任务完成。"); return;
        }
        string? archivePath = null;
        if (!online) { archivePath = host.SelectFile("选择新版整合包"); if (string.IsNullOrEmpty(archivePath)) return; }
        if (host.CompareExchangeActive(1, 0) != 0) { host.Hint("请等待当前整合包更新完成。"); return; }
        TTask step = host.CreateStep("比较并更新整合包文件", task => Execute(instance, online, archivePath!, task, requestedVersion));
        TCombo loader = host.CreateCombo("更新整合包：" + host.Name(instance), step);
        TWorker worker = host.CreateWorker(loader, host.VersionFolder(instance));
        if (!host.TryAcquire(worker, host.VersionFolder(instance))) { host.ExchangeActive(0); return; }
        host.OnStopped(worker, () => host.ExchangeActive(0));
        host.StartWorker(worker); host.AddTaskbar(loader); host.RefreshButton();
    }
    public void OpenBackups(TInstance? instance) {
        if (instance is null) return;
        string folder = Path.Combine(host.VersionFolder(instance), "PCL", "PackUpdateBackups");
        host.EnsureWithin(host.VersionFolder(instance), folder);
        if (Directory.Exists(folder)) host.Explorer(folder); else host.Hint("此版本还没有整合包更新备份。");
    }
    public string SelectArchive(string title) {
        string? result = null;
        host.UiWait(() => result = host.SelectFile(title));
        if (string.IsNullOrEmpty(result)) throw new OperationCanceledException();
        return result!;
    }
    private TPackage? DownloadRecordedOriginal(JObject baseline, string stage, CancellationToken token, TProgress progress) {
        // A filtered schema-1 inventory is rebuilt only from its exact recorded original.
        if (baseline["schema"]?.Type != JTokenType.Integer || baseline["schema"]!.ToString() != "1" ||
            baseline["source"] is not JObject source || !Enum.TryParse(source["platform"]?.ToString(), out ResourcePlatforms platform) ||
            platform is not (ResourcePlatforms.Modrinth or ResourcePlatforms.CurseForge)) return null;
        string projectId = source["project"]?.ToString() ?? "", versionId = source["version"]?.ToString() ?? "";
        if (!Regex.IsMatch(projectId, @"\A[A-Za-z0-9_-]+\z") || !Regex.IsMatch(versionId, @"\A[A-Za-z0-9_-]+\z")) return null;
        if (platform == ResourcePlatforms.CurseForge && (!Regex.IsMatch(projectId, @"\A[0-9]+\z") || !Regex.IsMatch(versionId, @"\A[0-9]+\z"))) return null;
        token.ThrowIfCancellationRequested();
        ResourceProjectModelData project = host.Project(projectId, platform);
        token.ThrowIfCancellationRequested();
        if (project.Types != ResourceTypes.ModPack || !Regex.IsMatch(project.Id ?? "", @"\A[A-Za-z0-9_-]+\z")) throw new IOException("旧记录的来源不是有效整合包项目。");
        string endpoint = platform == ResourcePlatforms.Modrinth ? "https://api.modrinth.com/v2/version/" + versionId :
            "https://api.curseforge.com/v1/mods/" + project.Id + "/files/" + versionId;
        JToken response = host.Request(endpoint);
        token.ThrowIfCancellationRequested();
        JObject metadata = (platform == ResourcePlatforms.Modrinth ? response : response["data"]) as JObject
            ?? throw new IOException("未获取到旧记录对应的整合包文件。");
        if (!Same(metadata["id"]?.ToString(), versionId) || !Same(metadata[platform == ResourcePlatforms.Modrinth ? "project_id" : "modId"]?.ToString(), project.Id))
            throw new IOException("原包文件与旧记录的项目或版本不一致。");
        if (platform == ResourcePlatforms.Modrinth && metadata["files"] is JArray entries) {
            var primary = entries.Where(file => file["primary"]?.Type == JTokenType.Boolean && file["primary"]!.Value<bool>()).ToList();
            if (primary.Count != 1) throw new IOException("旧版本的主整合包文件无法唯一确定。");
            metadata = (JObject)metadata.DeepClone(); metadata["files"] = new JArray(primary[0].DeepClone());
        }
        TVersion version = host.ParseVersion(metadata, ResourceTypes.ModPack);
        if (!Same(version.Id, versionId) || !version.DownloadAvailable || version.Size <= 0 || version.DownloadUrls!.Any(url =>
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme != "https" || address.UserInfo != ""))
            throw new IOException("旧整合包不可下载或缺少完整校验信息。");
        string algorithm = version.Hash?.Length switch { 32 => "md5", 40 => "sha1", 64 => "sha256", 128 => "sha512", _ => throw new IOException("旧整合包缺少有效校验值。") };
        string hash = PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.ValidHash(algorithm, version.Hash);
        string archive = Path.Combine(stage, platform == ResourcePlatforms.Modrinth ? "recorded-original.mrpack" : "recorded-original.zip");
        host.Download(new List<TNetFile> { host.ToUpdateFile(version, archive, baseline["minecraft"]?.ToString(), version.ModLoaders) }, token, progress);
        token.ThrowIfCancellationRequested();
        if (!File.Exists(archive) || new FileInfo(archive).Length != version.Size || !PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>.Matches(archive, hash, token))
            throw new IOException("下载的旧整合包未通过文件大小和哈希校验。");
        TPackage original = update.ReadPackage(archive, Path.Combine(stage, "original-online"), token);
        original.Source = (JObject)source.DeepClone(); original.Source["project"] = project.Id;
        return original;
    }
    public List<TVersion> GetAvailableVersions(JObject baseline) {
        var source = baseline["source"] as JObject ?? throw new IOException("没有在线来源记录，请先从文件更新或补全整合包记录。");
        if (!Enum.TryParse(source["platform"]?.ToString(), out ResourcePlatforms platform) || platform is not (ResourcePlatforms.Modrinth or ResourcePlatforms.CurseForge)) throw new IOException("无效的整合包更新来源。");
        string id = source["project"]?.ToString() ?? "";
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z")) throw new IOException("无效的整合包项目编号。");
        if (host.Project(id, platform).Types != ResourceTypes.ModPack) throw new IOException("来源不是整合包项目。");
        host.RemoveVersionCache(id);
        return host.Versions(id, platform, false).Where(version => !string.IsNullOrWhiteSpace(version.Id)).GroupBy(version => version.Id).Select(group => group.First()).OrderByDescending(version => version.ReleaseDate).ToList();
    }
    public string ChooseOnline(JObject baseline, string stage, CancellationToken token, ref JObject? source, TProgress progress, string? requestedVersion = null) {
        source = baseline["source"] as JObject;
        if (source is not null) source = (JObject)source.DeepClone();
        if (source is null) {
            string? url = null;
            host.UiWait(() => url = host.InputProjectAddress());
            if (string.IsNullOrWhiteSpace(url)) throw new OperationCanceledException();
            Match match = Regex.Match(url!, @"\Ahttps://(?:www\.)?(modrinth\.com/modpack/([A-Za-z0-9_-]+)|curseforge\.com/minecraft/modpacks/([A-Za-z0-9_-]+))/?\z");
            if (!match.Success) throw new IOException("请输入 CurseForge 或 Modrinth 的整合包项目地址。");
            if (match.Groups[2].Success) source = new JObject(new JProperty("platform", "Modrinth"), new JProperty("project", match.Groups[2].Value));
            else {
                JToken response = host.Request("https://api.curseforge.com/v1/mods/search?gameId=432&classId=4471&slug=" + Uri.EscapeDataString(match.Groups[3].Value));
                JToken? project = ((JArray)response["data"]!).FirstOrDefault(item => Same(item["slug"]?.ToString(), match.Groups[3].Value));
                if (project is null) throw new IOException("没有找到该 CurseForge 整合包。");
                source = new JObject(new JProperty("platform", "CurseForge"), new JProperty("project", project["id"]!.ToString()));
            }
        }
        if (!Enum.TryParse(source["platform"]!.ToString(), out ResourcePlatforms platform) || !new[] { ResourcePlatforms.Modrinth, ResourcePlatforms.CurseForge }.Contains(platform)) throw new IOException("无效的整合包更新来源。");
        string id = source["project"]!.ToString();
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z")) throw new IOException("无效的整合包项目编号。");
        ResourceProjectModelData projectInfo = host.Project(id, platform);
        if (projectInfo.Types != ResourceTypes.ModPack) throw new IOException("此地址不是整合包项目。");
        host.RemoveVersionCache(id);
        string? currentId = source["version"]?.ToString();
        List<TVersion> versions = host.Versions(id, platform, false).Where(item => !Same(item.Id, currentId)).OrderByDescending(item => item.ReleaseDate).ToList();
        token.ThrowIfCancellationRequested();
        if (versions.Count == 0) throw new IOException("没有其他可下载的整合包版本。");
        TVersion? selected = null;
        if (requestedVersion is not null) selected = versions.FirstOrDefault(version => Same(version.Id, requestedVersion)) ?? throw new IOException("所选版本已不在此整合包的可更新列表中，请刷新后重试。");
        else host.UiWait(() => selected = host.SelectVersion(versions.Take(60).ToList(), baseline["name"]?.ToString() ?? "整合包", baseline["version"]?.ToString()));
        if (selected is null) throw new OperationCanceledException();
        token.ThrowIfCancellationRequested();
        string destination = Path.Combine(stage, platform == ResourcePlatforms.Modrinth ? "update.mrpack" : "update.zip");
        host.Download(new List<TNetFile> { host.ToUpdateFile(selected, destination, selected.GameVersions.FirstOrDefault(), selected.ModLoaders) }, token, progress);
        source["project"] = projectInfo.Id; source["version"] = selected.Id;
        return destination;
    }
    public TInstance RecoverInstance(TInstance instance) { update.Recover(host.VersionFolder(instance), host.GameRoot(instance)); return ReloadInstance(instance); }
    public TInstance ReloadInstance(TInstance instance) {
        host.ResetSettings(instance);
        TInstance refreshed = host.LoadInstance(host.VersionFolder(instance));
        host.UiWait(() => {
            if (host.PageInstance() is not null && PathUtils.IsSame(host.VersionFolder(host.PageInstance()!), host.VersionFolder(instance))) host.SetPageInstance(refreshed);
            if (host.SelectedInstance() is not null && PathUtils.IsSame(host.VersionFolder(host.SelectedInstance()!), host.VersionFolder(instance))) host.SetSelectedInstance(refreshed);
        });
        return refreshed;
    }
}
