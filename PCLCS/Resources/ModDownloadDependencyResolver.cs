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

public sealed class ModDownloadDependency<TVersion> where TVersion : ResourceVersionModelData {
    public string ProjectId = "";
    public ResourceProjectModelData? Project;
    public TVersion? Version;
    public bool Installed;
    public string? Failure;
    public string Name => Project?.TranslatedName ?? ProjectId;
}

public sealed class ModDownloadDependencyPlan<TVersion> where TVersion : ResourceVersionModelData {
    public List<ModDownloadDependency<TVersion>> Dependencies { get; } = new();
    public List<string> Problems { get; } = new();
    public string MinecraftVersion = "";
    public ModLoaders Loader;
    public bool CanInstall => Problems.Count == 0 && Dependencies.All(value => value.Failure is null);
    public bool NeedsPrompt => !CanInstall || Dependencies.Any(value => !value.Installed);
}

/// <summary>Build a bounded, dependency-first proposal. No download or install is performed here.</summary>
public sealed class ModDownloadDependencyResolver<TVersion> where TVersion : ResourceVersionModelData {
    public Func<ResourcePlatforms, string, CancellationToken, ResourceProjectModelData> Project = null!;
    public Func<ResourcePlatforms, string, CancellationToken, IReadOnlyList<TVersion>> Versions = null!;
    public Func<ResourcePlatforms, string?, string, CancellationToken, TVersion> ExactVersion = null!;
    public Func<TVersion, CancellationToken, bool> Installed = (_, _) => false;

    public ModDownloadDependencyPlan<TVersion> Resolve(ResourceProjectModelData project, TVersion selected,
        string minecraft, ModLoaders loader, CancellationToken token) {
        var plan = new ModDownloadDependencyPlan<TVersion> { MinecraftVersion = minecraft, Loader = loader };
        var chosen = new Dictionary<string, ModDownloadDependency<TVersion>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var forcedPins = new Dictionary<string, TVersion>(StringComparer.Ordinal);
        var activePins = new Dictionary<string, TVersion>(StringComparer.Ordinal);
        var exactCache = new Dictionary<string, TVersion>(StringComparer.Ordinal);
        string rootId = project.Id ?? throw new ArgumentException("模组缺少平台项目标识。");
        try {
            token.ThrowIfCancellationRequested();
            TVersion root = Metadata(selected, rootId);
            if (root.RequiredDependencyReferences!.Count == 0) return plan;
            if (string.IsNullOrWhiteSpace(minecraft) || loader == ModLoaders.None || (((int)loader & ((int)loader - 1)) != 0)) {
                plan.Problems.Add("无法确定下载目标的 Minecraft 版本或加载器，请选择对应的游戏实例后重试，或仅下载此模组。");
                return plan;
            }
            if (!Compatible(root)) {
                plan.Problems.Add("当前所选 Mod 文件不兼容目标 Minecraft 或加载器，请核对版本后重试，或仅下载此模组。");
                return plan;
            }
            bool settled = false;
            // A pin discovered through a later parent can change an earlier
            // project-level selection. Rebuild reachable edges, dropping pins
            // and children that belonged only to the superseded version.
            for (int pass = 0; pass < 128; pass++) {
                chosen.Clear(); visiting.Clear(); activePins.Clear(); plan.Dependencies.Clear(); plan.Problems.Clear();
                foreach (var dependency in root.RequiredDependencyReferences) Visit(dependency, 0);
                if (activePins.Count == forcedPins.Count && activePins.All(pair => forcedPins.TryGetValue(pair.Key, out var old) && old.Id == pair.Value.Id)) {
                    settled = true; break;
                }
                forcedPins = new(activePins, StringComparer.Ordinal);
            }
            if (!settled) plan.Problems.Add("前置的固定版本要求无法收敛，请手动核对。");
            var names = plan.Dependencies.Where(value => value.Version is not null && !value.Installed)
                .Select(value => value.Version!.FileName).Append(selected.FileName);
            if (names.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                plan.Problems.Add("不同模组使用了相同的文件名，请分别选择保存位置。");
        } catch (OperationCanceledException) { throw; }
        catch (Exception error) { token.ThrowIfCancellationRequested(); plan.Problems.Add("无法完整读取必需前置：" + error.Message); }
        return plan;

        TVersion Metadata(TVersion version, string? expectedProject) {
            token.ThrowIfCancellationRequested();
            TVersion result = version.RequiredDependencyReferences is null
                ? ExactVersion(project.Platform, expectedProject, version.Id ?? throw new IOException("前置缺少版本标识。"), token) : version;
            token.ThrowIfCancellationRequested();
            if (result.Id != version.Id || (expectedProject is not null && result.ProjectId != expectedProject))
                throw new IOException("平台返回的前置版本与所选项目不一致。");
            if (result.RequiredDependencyReferences is null) throw new IOException("平台没有提供可验证的依赖声明。");
            return result;
        }
        bool Compatible(TVersion version) => version.ResourceType.HasFlag(ResourceTypes.Mod) && version.DownloadAvailable &&
            (version.ExactGameVersions ?? version.GameVersions).Contains(minecraft, StringComparer.Ordinal) && version.ModLoaders.HasFlag(loader);
        void Visit(ModDownloadDependencyReference reference, int depth) {
            token.ThrowIfCancellationRequested();
            if (depth >= 32 || chosen.Count >= 128) throw new IOException("前置关系超过自动检查上限，请分批安装。");
            TVersion? pinned = null;
            string? id = reference.ProjectId;
            if (!string.IsNullOrWhiteSpace(reference.VersionId)) {
                string key = id + ":" + reference.VersionId;
                if (!exactCache.TryGetValue(key, out pinned)) exactCache[key] = pinned = ExactVersion(project.Platform, id, reference.VersionId!, token);
                token.ThrowIfCancellationRequested();
                if (pinned.Id != reference.VersionId || (id is not null && pinned.ProjectId != id))
                    throw new IOException("平台返回的固定前置版本与声明不一致。");
                id ??= pinned.ProjectId;
            }
            if (string.IsNullOrWhiteSpace(id)) throw new IOException("必需前置只有外部文件名，没有可下载的平台项目或版本。");
            if (id == rootId) {
                if (pinned is not null && pinned.Id != selected.Id) plan.Problems.Add("前置要求当前模组的另一个固定版本，无法同时安装。");
                return;
            }
            if (pinned is not null) {
                if (activePins.TryGetValue(id!, out var required) && required.Id != pinned.Id)
                    plan.Problems.Add("多个模组对前置 " + id + " 要求不同的固定版本，请手动核对。");
                else activePins[id!] = pinned;
            } else forcedPins.TryGetValue(id!, out pinned);
            if (chosen.TryGetValue(id!, out var previous)) {
                return;
            }
            var item = new ModDownloadDependency<TVersion> { ProjectId = id! };
            chosen.Add(id!, item);
            try {
                item.Project = Project(project.Platform, id!, token);
                if (item.Project.Platform != project.Platform || item.Project.Id != id || !item.Project.Types.HasFlag(ResourceTypes.Mod))
                    throw new IOException("前置项目的平台、身份或资源类型不匹配。");
                if (pinned is not null) {
                    if (!Compatible(pinned)) throw new IOException("作者指定的前置版本不兼容当前 Minecraft 或加载器。");
                    item.Version = Metadata(pinned, id);
                    item.Installed = Installed(item.Version, token);
                } else {
                    var versions = Versions(project.Platform, id!, token).Where(Compatible)
                        .OrderBy(value => value.ReleaseTypeCode == 1 ? 0 : 1).ThenByDescending(value => value.ReleaseDate).ToList();
                    foreach (TVersion version in versions) {
                        token.ThrowIfCancellationRequested();
                        if (Installed(version, token)) { item.Version = Metadata(version, id); item.Installed = true; break; }
                    }
                    item.Version ??= versions.Count == 0 ? throw new IOException("没有兼容当前 Minecraft 和加载器的可下载版本。") : Metadata(versions[0], id);
                }
                if (!Compatible(item.Version)) throw new IOException("重新读取的前置版本不兼容当前 Minecraft 或加载器。");
                item.Installed = Installed(item.Version, token);
                string name = item.Version.FileName!;
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("前置文件名不是有效的 Mod JAR 文件名。");
                if (visiting.Add(id!)) {
                    foreach (var child in item.Version.RequiredDependencyReferences!) Visit(child, depth + 1);
                    visiting.Remove(id!);
                }
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) { token.ThrowIfCancellationRequested(); item.Failure = error.Message; }
            plan.Dependencies.Add(item);
        }
    }
}
