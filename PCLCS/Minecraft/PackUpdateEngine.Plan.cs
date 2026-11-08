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
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    private sealed class LocalAuthor {
        public string? Relative;
        public bool HashVerified;
        public PackUpdateOwnership Ownership;
        public bool PersonalComponentMatched;
        public PackUpdateAssociation Association;
        public List<string> Candidates = new();
    }
    private static bool ModFile(string path) => path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
        (EnabledModFile(path) || DisabledFile(path));
    private static bool EnabledModFile(string path) => new[] { ".jar", ".zip", ".litemod" }.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    private static bool DisabledFile(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
    private static int ContentRole(string path) => ModFile(path) ? 1 :
        path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase) ? 2 :
        path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase) ? 3 :
        path.StartsWith("datapacks/", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
    private static bool PlatformArchive(string path) => (ContentRole(path) is 2 or 3 or 4) &&
        (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
    private static bool CurrentAssociationPolicy(JObject? baseline) => baseline?["associationPolicyVersion"]?.Type == JTokenType.Integer &&
        baseline!["associationPolicyVersion"]!.Value<int>() == 2;
    private static bool LegacyAssociationPolicy(JObject? baseline) => baseline?["associationPolicyVersion"] is not JToken version ||
        version.Type == JTokenType.Null || version.Type == JTokenType.Integer && version.Value<int>() is >= 0 and < 2;
    // Match the shared inspector's ContextForInstance/ScanRaw legacy Forge scope.
    private static string? LegacyVersionFolder(TPackage package) => package.Loaders.ContainsKey(ModLoaders.Forge) &&
        Regex.IsMatch(package.Minecraft ?? "", @"^1\.(?:[0-9]|1[0-2])(?:\.|$)") &&
        Regex.IsMatch(package.Minecraft ?? "", @"^[0-9.]+$") ? package.Minecraft : null;
    private static bool LoadedModPath(TPackage package, string relative) {
        if (!ModFile(relative) || !EnabledModFile(relative)) return false;
        string[] parts = relative.Split('/');
        return parts.Length == 2 || parts.Length == 3 &&
            string.Equals(parts[1], LegacyVersionFolder(package), StringComparison.OrdinalIgnoreCase);
    }

    // Only content areas, never runtime libraries, saves, accounts or PCL journals.
    // Observations are not additions to the author manifest.
    private List<string> LocalContent(string root, CancellationToken token = default, string? legacyVersion = null) {
        List<string> files = new();
        foreach (string area in new[] { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "datapacks" }) {
            string directory = Path.Combine(root, area);
            host.EnsureWithin(root, directory);
            if (Directory.Exists(directory)) {
                Visit(directory, area != "mods");
                if (area == "mods" && legacyVersion is not null) {
                    string versionFolder = Path.Combine(directory, legacyVersion);
                    host.EnsureWithin(root, versionFolder);
                    if (Directory.Exists(versionFolder)) Visit(versionFolder, false);
                }
            }
        }
        return files;
        void Visit(string folder, bool recursive) {
            token.ThrowIfCancellationRequested();
            host.EnsureWithin(root, folder);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("内容目录包含链接，请先检查：" + folder);
            foreach (string file in Directory.GetFiles(folder)) {
                host.EnsureWithin(root, file);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("内容文件包含链接，请先检查：" + file);
                files.Add(RelativePath(file.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace('\\', '/')));
            }
            if (recursive) foreach (string child in Directory.GetDirectories(folder)) Visit(child, true);
        }
    }
    private void Observe(TPlan plan, string relative, CancellationToken token) {
        if (!plan.Snapshot.ContainsKey(relative)) plan.Snapshot[relative] = Digest(Target(plan.Root!, relative), "sha1", token);
    }
    private LocalAuthor LocateAuthor(TPlan plan, string relative, JToken previous, List<string> local, Dictionary<string, string?> hashes, CancellationToken token) {
        string? expected = previous["hash"]?.ToString();
        string? direct = File.Exists(Target(plan.Root!, relative)) ? relative : null;
        string? localOwnership = previous["local"]?["ownership"]?.ToString();
        string? previousLocal = previous["local"]?["relative"]?.ToString();
        // Ordinary content has path identity. Equal bytes do not establish a rename,
        // and a historical observation cannot authorize deleting another configuration.
        if (!ModFile(relative)) {
            if (previousLocal is not null && !IsPrivate(previousLocal)) Observe(plan, previousLocal, token);
            if (direct is null) return new() { Ownership = (bool?)previous["optional"] == true ? PackUpdateOwnership.OptionalAbsent : PackUpdateOwnership.Deleted };
            bool inherited = previousLocal is not null && !string.Equals(previousLocal, relative, StringComparison.OrdinalIgnoreCase);
            bool recovered = LegacyAssociationPolicy(plan.Baseline) && localOwnership == "ambiguous" && !inherited &&
                string.Equals(previous["baselineRelative"]?.ToString(), relative, StringComparison.OrdinalIgnoreCase) &&
                previous["originalHash"] is not null && MatchHash(direct, previous["originalHash"]!.ToString());
            if (localOwnership == "personal-added" && !inherited) return new() { Relative = direct, Ownership = PackUpdateOwnership.PersonalAdded, Association = PackUpdateAssociation.Unknown };
            if (!recovered && (inherited || localOwnership is "ambiguous" or "unknown"))
                return new() { Relative = direct, Ownership = PackUpdateOwnership.Unknown, Association = PackUpdateAssociation.Unknown };
            bool verified = recovered || MatchCached(direct);
            return new() { Relative = direct, HashVerified = verified, Ownership = verified ? PackUpdateOwnership.AuthorUnchanged : PackUpdateOwnership.PersonalModified };
        }
        if ((localOwnership is "personal-added" or "ambiguous" or "unknown") && previousLocal is not null && ModFile(previousLocal) && File.Exists(Target(plan.Root!, previousLocal))) {
            bool matched = localOwnership == "personal-added" && CurrentAssociationPolicy(plan.Baseline) &&
                previous["local"]?["association"]?.ToString() == nameof(PackUpdateAssociation.PersonalComponent) && previous["local"]?["personalComponentMatched"]?.Type == JTokenType.Boolean &&
                previous["local"]!["personalComponentMatched"]!.Value<bool>() && MatchHash(previousLocal, previous["local"]?["hash"]?.ToString());
            if (matched) {
                var copies = local.Where(path => ModFile(path) && MatchHash(path, previous["local"]?["hash"]?.ToString())).ToList();
                if (copies.Count > 1) return new() { Ownership = PackUpdateOwnership.Ambiguous, Candidates = copies };
            }
            return new() { Relative = previousLocal, Ownership = localOwnership == "personal-added" ? PackUpdateOwnership.PersonalAdded :
                    localOwnership == "ambiguous" ? PackUpdateOwnership.Ambiguous : PackUpdateOwnership.Unknown,
                PersonalComponentMatched = matched, Association = matched ? PackUpdateAssociation.PersonalComponent : PackUpdateAssociation.Unknown };
        }
        List<string> candidates = local.Where(path => !IsPrivate(path) && ModFile(path) && MatchCached(path)).ToList();
        bool MatchCached(string path) => MatchHash(path, expected);
        bool MatchHash(string path, string? expectedHash) {
            if (expectedHash is null) return false;
            string normalized = NormalizeHash(expectedHash), algorithm = normalized.Split(':')[0], key = algorithm + ":" + path;
            if (!hashes.TryGetValue(key, out string? digest)) hashes[key] = digest = Digest(Target(plan.Root!, path), algorithm, token);
            return Same(digest, normalized);
        }
        if (direct is not null && !candidates.Contains(direct, StringComparer.OrdinalIgnoreCase)) candidates.Insert(0, direct);
        if (candidates.Count > 1) return new() { Ownership = PackUpdateOwnership.Ambiguous, Candidates = candidates };
        if (direct is not null) return new() { Relative = direct, HashVerified = MatchCached(direct), Ownership = DisabledFile(direct) ? PackUpdateOwnership.Disabled :
            MatchCached(direct) ? PackUpdateOwnership.AuthorUnchanged : PackUpdateOwnership.PersonalModified };
        if (candidates.Count == 1) return new() { Relative = candidates[0], HashVerified = true, Association = PackUpdateAssociation.VerifiedModHash,
            Ownership = DisabledFile(candidates[0]) ? PackUpdateOwnership.Disabled : PackUpdateOwnership.AuthorRenamed };
        string? recorded = previous["local"]?["relative"]?.ToString();
        List<string> inactive = new[] { relative + ".disabled", relative + ".old", recorded }.Where(path => !string.IsNullOrEmpty(path) && ModFile(path!) && File.Exists(Target(plan.Root!, path!))).Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (inactive.Count > 1) return new() { Ownership = PackUpdateOwnership.Ambiguous, Candidates = inactive };
        if (!string.IsNullOrEmpty(recorded) && ModFile(recorded!) && File.Exists(Target(plan.Root!, recorded!))) return new() {
            Relative = recorded, Association = PackUpdateAssociation.RecordedMod, Ownership = DisabledFile(recorded!) ? PackUpdateOwnership.Disabled : PackUpdateOwnership.PersonalModified };
        foreach (string suffix in new[] { ".disabled", ".old" }) if (File.Exists(Target(plan.Root!, relative + suffix)))
            return new() { Relative = relative + suffix, Ownership = PackUpdateOwnership.Disabled };
        return new() { Ownership = (bool?)previous["optional"] == true ?
            PackUpdateOwnership.OptionalAbsent : PackUpdateOwnership.Deleted };
    }
    private PackModInspection Inspect(TPackage package, string file, CancellationToken token = default) => PackUpdateModInspection.Read(file, package.Minecraft, package.Loaders, token);
    private bool SameAuthor(TPlan plan, string previousRelative, JToken previous, LocalAuthor local, TFile incoming, CancellationToken token, Dictionary<string, PackModInspection> metadata) {
        if (ContentRole(previousRelative) != ContentRole(incoming.Relative!) || IsPersonal(previousRelative) || IsPersonal(incoming.Relative!)) return false;
        string? project = previous["identity"]?.ToString();
        if (ModFile(previousRelative) || PlatformArchive(previousRelative) && PlatformArchive(incoming.Relative!)) {
            if (!string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(incoming.Identity)) return project == incoming.Identity;
        }
        if (!ModFile(previousRelative) || !ModFile(incoming.Relative!)) return false;
        if (Same(previous["hash"]?.ToString(), incoming.Hash) && incoming.Hash is not null) return true;
        // ID linking requires BOTH author declarations plus proof of the OLD author bytes.
        // An identical modId alone never establishes ownership of a local file.
        if (local.Relative is null || incoming.Payload is null ||
            local.Ownership is not (PackUpdateOwnership.AuthorUnchanged or PackUpdateOwnership.AuthorRenamed or PackUpdateOwnership.Disabled) ||
            !local.HashVerified || !ModFile(incoming.Relative!)) return false;
        var oldLoaders = plan.Baseline?["loaders"]?.ToObject<Dictionary<ModLoaders, string>>() ?? new();
        string oldKey = "old:" + local.Relative, newKey = "new:" + incoming.Payload;
        if (!metadata.TryGetValue(oldKey, out PackModInspection? before)) metadata[oldKey] = before = PackUpdateModInspection.Read(Target(plan.Root!, local.Relative), plan.Baseline?["minecraft"]?.ToString(), oldLoaders, token);
        if (!metadata.TryGetValue(newKey, out PackModInspection? after)) metadata[newKey] = after = Inspect(plan.Package!, incoming.Payload, token);
        return before.Confirmed && after.Confirmed && before.OwnIds.Count > 0 && before.OwnIds.SetEquals(after.OwnIds);
    }
    public TPlan Plan(string root, JObject? baseline, TPackage package, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        if (RequiresBaselineRepair(baseline)) throw new IOException("作者基线缺失或不完整，请选择正确的旧版原始整合包补录，未修改当前实例。");
        TPlan result = host.CreatePlan(); result.Root = Path.GetFullPath(root); result.Package = package; result.Baseline = baseline;
        var old = ((JObject)baseline!["files"]!).Properties().ToDictionary(item => RelativePath(item.Name), item => item.Value, StringComparer.OrdinalIgnoreCase);
        List<string> local = LocalContent(result.Root, token, LegacyVersionFolder(package));
        foreach (string path in local) { Observe(result, path, token); if (ModFile(path)) result.ObservedModFiles.Add(path); }
        Dictionary<string, LocalAuthor> locations = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> hashes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PackModInspection> metadata = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in old) {
            if (IsPrivate(pair.Key)) continue;
            Observe(result, pair.Key, token); locations[pair.Key] = LocateAuthor(result, pair.Key, pair.Value, local, hashes, token);
        }
        // One physical file cannot discharge two different old author declarations.
        foreach (var group in locations.Values.Where(value => value.Relative is not null).GroupBy(value => value.Relative!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            foreach (LocalAuthor value in group) { value.Candidates.Add(value.Relative!); value.Relative = null; value.Ownership = PackUpdateOwnership.Ambiguous; }
        HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase), claimedLocal = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguousIncoming = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in old) {
            if (!locations.TryGetValue(pair.Key, out LocalAuthor? found)) continue;
            TFile? incoming = null; bool ambiguous = false;
            if (package.Files.TryGetValue(pair.Key, out TFile? samePath)) incoming = samePath;
            else {
                var matches = package.Files.Values.Where(file => !assigned.Contains(file.Relative!) && !old.ContainsKey(file.Relative!) && SameAuthor(result, pair.Key, pair.Value, found, file, token, metadata)).ToList();
                if (matches.Count == 1 && locations.Count(other => !package.Files.ContainsKey(other.Key) && SameAuthor(result, other.Key, old[other.Key], other.Value, matches[0], token, metadata)) == 1) incoming = matches[0];
                else if (matches.Count == 1) ambiguous = true;
                else if (matches.Count > 1) ambiguous = true;
                if (ambiguous) foreach (TFile candidate in matches) ambiguousIncoming.Add(candidate.Relative!);
            }
            if (incoming is not null && !assigned.Add(incoming.Relative!)) { incoming = null; ambiguous = true; }
            if (found.Relative is not null) { claimedLocal.Add(found.Relative); Observe(result, found.Relative, token); }
            foreach (string candidate in found.Candidates) { claimedLocal.Add(candidate); Observe(result, candidate, token); }
            if (found.Relative is null && incoming is null && !ambiguous && found.Ownership != PackUpdateOwnership.Ambiguous) continue;
            bool personal = found.Ownership is not (PackUpdateOwnership.AuthorUnchanged or PackUpdateOwnership.AuthorRenamed);
            if (!ambiguous && incoming is not null && found.Relative is not null &&
                found.Ownership is PackUpdateOwnership.AuthorUnchanged or PackUpdateOwnership.AuthorRenamed or PackUpdateOwnership.PersonalModified &&
                Matches(Target(result.Root, found.Relative), incoming.Hash, token)) {
                if (string.Equals(found.Relative, incoming.Relative, StringComparison.OrdinalIgnoreCase)) continue;
                found.Ownership = PackUpdateOwnership.AuthorRenamed; personal = false;
            }
            if (incoming is not null && found.Ownership == PackUpdateOwnership.Disabled && found.HashVerified &&
                Same(incoming.Hash, pair.Value["hash"]?.ToString()) && string.Equals(pair.Key, incoming.Relative, StringComparison.OrdinalIgnoreCase)) continue;
            if (incoming is not null && !personal && Same(incoming.Hash, pair.Value["hash"]?.ToString()) &&
                string.Equals(found.Relative, incoming.Relative, StringComparison.OrdinalIgnoreCase) && Matches(Target(result.Root, found.Relative!), incoming.Hash, token)) continue;
            TChange change = host.CreateChange(); change.BaselineRelative = pair.Key; change.LocalRelative = found.Relative;
            change.Relative = incoming?.Relative ?? pair.Key; change.Incoming = incoming; change.Ownership = ambiguous ? PackUpdateOwnership.Ambiguous : found.Ownership;
            change.Association = found.Association;
            if (incoming is not null && PlatformArchive(pair.Key) && PlatformArchive(incoming.Relative!) && !string.Equals(pair.Key, incoming.Relative, StringComparison.OrdinalIgnoreCase))
                change.Association = PackUpdateAssociation.PlatformProject;
            change.Candidates = found.Candidates;
            change.Conflict = personal || ambiguous || found.Relative is not null && IsPersonal(found.Relative) || PersonalAreaExists(root, change.Relative);
            change.PersonalComponentMatched = found.PersonalComponentMatched;
            change.AllowAuthor = !ambiguous && found.Ownership is not (PackUpdateOwnership.Ambiguous or PackUpdateOwnership.OptionalAbsent) &&
                (found.Ownership != PackUpdateOwnership.PersonalAdded || found.PersonalComponentMatched);
            change.Before = found.Relative is null ? null : result.Snapshot[found.Relative];
            if (incoming is not null && change.Ownership == PackUpdateOwnership.Disabled) {
                Observe(result, change.Relative!, token); Observe(result, change.Relative! + ".disabled", token);
                if (found.HashVerified) { change.Conflict = false; change.Choice = PackUpdateChoice.UseAuthor; }
            }
            Observe(result, change.DestinationRelative, token); change.TargetBefore = result.Snapshot[change.DestinationRelative];
            if (incoming is not null && change.TargetBefore is not null && !string.Equals(change.LocalRelative, change.DestinationRelative, StringComparison.OrdinalIgnoreCase)) {
                change.Conflict = true; change.AllowAuthor = false; change.Ownership = PackUpdateOwnership.Ambiguous;
            }
            if (IsRuntimeData(pair.Key) || IsRuntimeData(change.Relative)) {
                change.Conflict = true; change.AllowAuthor = false; change.DisplayOnly = true;
            }
            change.Reason = Reason(change); result.Changes.Add(change);
        }
        foreach (TFile incoming in package.Files.Values.Where(file => !assigned.Contains(file.Relative!))) {
            if (IsPrivate(incoming.Relative!)) continue;
            TChange change = host.CreateChange(); change.Relative = incoming.Relative; change.Incoming = incoming;
            Observe(result, change.Relative!, token); change.TargetBefore = result.Snapshot[change.Relative!];
            change.LocalRelative = change.TargetBefore is null ? null : change.Relative; change.Before = change.TargetBefore;
            if (IsRuntimeData(change.Relative!)) {
                change.Ownership = change.LocalRelative is null ? PackUpdateOwnership.Deleted : PackUpdateOwnership.PersonalAdded;
                change.Conflict = true; change.AllowAuthor = false; change.DisplayOnly = true;
                change.Reason = "保留玩家运行数据，整包更新不替换、删除或注入世界／服务器状态文件";
            } else if (ambiguousIncoming.Contains(change.Relative!)) {
                change.Ownership = PackUpdateOwnership.Ambiguous; change.Conflict = true; change.AllowAuthor = false;
                change.Reason = "同一作者项目有多个旧版或新版文件候选，请先确认版本关联";
                change.Candidates.AddRange(locations.Where(pair => SameAuthor(result, pair.Key, old[pair.Key], pair.Value, incoming, token, metadata)).Select(pair => pair.Value.Relative ?? pair.Key));
            } else if (change.LocalRelative is not null) {
                claimedLocal.Add(change.LocalRelative); change.Ownership = PackUpdateOwnership.PersonalAdded;
                change.Conflict = true; change.AllowAuthor = false;
                change.Reason = "作者目标路径已被本地新增文件占用，请先确认归属；不会覆盖或删除该文件";
            } else if (incoming.OptionalFile) {
                change.Ownership = PackUpdateOwnership.OptionalAbsent; change.Conflict = true; change.AllowAuthor = false; change.DisplayOnly = true;
                change.Reason = "保留未安装的可选组件";
            } else { change.Ownership = PackUpdateOwnership.AuthorUnchanged; change.Reason = "新增作者文件"; }
            result.Changes.Add(change);
        }
        foreach (string path in local.Where(path => !claimedLocal.Contains(path) && !old.ContainsKey(path) && !package.Files.ContainsKey(path))) {
            TChange change = host.CreateChange(); change.Relative = path; change.LocalRelative = path; change.Before = result.Snapshot[path];
            change.Ownership = DisabledFile(path) ? PackUpdateOwnership.Disabled : PackUpdateOwnership.PersonalAdded;
            change.DisplayOnly = true; change.Conflict = true; change.AllowAuthor = false;
            change.Reason = DisabledFile(path) ? "保留本地禁用文件，不自动启用，不纳入作者归属" : "保留本地新增文件（可能由玩家添加或运行生成），不纳入作者归属";
            result.Changes.Add(change);
        }
        return result;
    }
    private static string Reason(TChange change) => IsRuntimeData(change.Relative!) ? "保留玩家运行数据，整包更新不替换、删除或注入世界／服务器状态文件" : change.Ownership switch {
        PackUpdateOwnership.Ambiguous => "存在多个匹配候选或重复副本，需先确认，未安排删除或替换",
        PackUpdateOwnership.Disabled => change.Conflict ? "本地禁用文件包含个人修改，可选择作者新版并确认更新后状态" : "作者模组将更新，请确认更新后继续禁用还是启用新版",
        PackUpdateOwnership.Deleted => "保留玩家删除状态",
        PackUpdateOwnership.OptionalAbsent => "保留未安装的可选组件",
        PackUpdateOwnership.PersonalModified => "保留个人替换的版本或内容，可逐项选择作者版本",
        PackUpdateOwnership.Unknown => "旧关联证据不足，保留当前文件；可明确选择作者版本，不自动处理其他路径的文件",
        _ => change.Conflict ? "保留个人数据文件" : change.Incoming is null ? "移除作者已删除的文件" :
            change.Ownership == PackUpdateOwnership.AuthorRenamed ? "按旧作者哈希识别纯改名，升级并处理实际旧路径" : "更新作者文件"
    };
    public void ValidateSnapshot(TPlan plan, CancellationToken token = default) {
        foreach (var pair in plan.Snapshot) if (!Same(Digest(Target(plan.Root!, pair.Key), "sha1", token), pair.Value))
            throw new IOException("文件在预览或下载后发生变化，请重新检查更新：" + pair.Key);
        if (plan.Snapshot.Count > 0) {
            var current = new HashSet<string>(LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(ModFile), StringComparer.OrdinalIgnoreCase);
            if (!current.SetEquals(plan.ObservedModFiles)) throw new IOException("模组目录在预览或下载后发生变化，请重新检查更新。");
        }
    }
    public void DownloadChanges(TPlan plan, string stage, CancellationToken token, Action<List<TNetFile>, CancellationToken>? download = null, Action<string>? status = null, Action<double>? verificationProgress = null) {
        List<TNetFile> files = new();
        status?.Invoke("准备更新文件");
        // Stage selectable conflicts too; choosing in the preview never starts unpreviewed downloads.
        foreach (TFile value in plan.Changes.Where(change => !change.DisplayOnly && change.AllowAuthor && change.Incoming is not null).Select(change => change.Incoming!).Distinct()) {
            token.ThrowIfCancellationRequested();
            if (value.Payload is not null) continue;
            value.Payload = Target(stage, "download/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(value.Payload)!);
            files.Add(host.CreateDownload(value.Urls, value.Payload, value.Hash!.Split(':')[1]));
        }
        if (files.Count > 0) { status?.Invoke("下载更新文件（" + files.Count + " 个）"); (download ?? host.Download)(files, token); }
        var stagedFiles = plan.Package!.Files.Values.Where(file => file.Payload is not null).ToArray();
        int verified = 0;
        foreach (TFile value in stagedFiles) {
            token.ThrowIfCancellationRequested();
            status?.Invoke("校验更新文件（" + verified + "/" + stagedFiles.Length + "）");
            if (!Matches(value.Payload!, value.Hash, token)) throw new IOException("整合包更新文件校验失败：" + value.Relative);
            verified++;
            verificationProgress?.Invoke(verified / (double)stagedFiles.Length);
        }
        status?.Invoke("核对本地修改与文件归属");
        ValidateSnapshot(plan, token);
        TPlan staged = Plan(plan.Root!, plan.Baseline, plan.Package, token);
        plan.Changes = staged.Changes;
        foreach (var pair in staged.Snapshot) if (!plan.Snapshot.ContainsKey(pair.Key)) plan.Snapshot.Add(pair.Key, pair.Value);
        LinkPersonalAuthorUpdates(plan, token);
        status?.Invoke("准备预览与原有禁用状态");
        PreparePreviewChanges(plan, stage, token);
        status?.Invoke("检查个人模组与补全前置");
        ResolvePersonalChanges(plan, stage, token, (dependencies, cancel) => {
            status?.Invoke("下载所需前置（" + dependencies.Count + " 个）");
            (download ?? host.Download)(dependencies, cancel);
            status?.Invoke("复查个人模组与前置依赖");
        });
        status?.Invoke("检查重复模组与完整依赖组合");
        PreserveModCollisions(plan, token);
        plan.DependencyCheck = CheckDependencies(plan, token);
        if (plan.DependencyCheck.HasErrors) {
            // Preserve the author's default actions, but expose a way to retain a
            // current author file when its upgrade/removal would break a personal
            // mod. The final selected combination is rechecked before any write.
            foreach (TChange change in plan.Changes.Where(change => !change.Conflict && !change.DisplayOnly && change.AllowAuthor && !change.PersonalCompatible && !change.AllowPreserveAuthor &&
                change.Ownership is PackUpdateOwnership.AuthorUnchanged or PackUpdateOwnership.AuthorRenamed &&
                change.BaselineRelative is not null && change.LocalRelative is not null && ModFile(change.LocalRelative) && EnabledModFile(change.LocalRelative))) {
                change.AllowPreserveAuthor = true;
                change.Choice = PackUpdateChoice.UseAuthor;
                change.Reason = change.Incoming is null ? "作者新版移除此文件；可选择保留当前版本，确认前会重新检查完整模组组合。" :
                    "默认使用作者新版；可选择保留当前版本以调整个人模组组合，确认前会重新检查全部依赖与冲突。";
            }
        }
    }
    public static HashSet<string> ModIds(string fileName) => PackUpdateModInspection.Read(fileName, null, new()).OwnIds;
    // Evaluate the actual final enabled files, including preserved personal mods and
    // the selected staged author payloads. Author declarations are provenance, not
    // proof that this chosen combination satisfies its dependencies.
    public PackDependencyGraphResult CheckDependencies(TPlan plan, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        var files = FinalModFiles(plan, token).Select(file => file.Path).ToList();
        PackDependencyGraphResult result = PackDependencyGraph.Check(files, plan.Package!.Minecraft, plan.Package.Loaders, token);
        foreach (TChange change in PlanChanges(plan).Where(change => change.ApplyAuthor && ModFile(change.DestinationRelative) &&
            EnabledModFile(change.DestinationRelative) && !LoadedModPath(plan.Package!, change.DestinationRelative))) {
            string[] parts = change.DestinationRelative.Split('/');
            // A different numeric MC version directory is outside the standard
            // loader scope. Deeper/custom directories may be plugin-managed.
            if (parts.Length != 3 || !Regex.IsMatch(parts[1], @"^[0-9.]+$")) {
                result.HasUnknown = true;
                result.Warnings.Add(change.DestinationRelative + "：此子目录不在可确认的标准模组加载范围中，不能确认其依赖关系。");
            }
        }
        string DisplayPath(string message) {
            foreach (TChange change in PlanChanges(plan).Where(change => change.ApplyAuthor && change.Incoming?.Payload is not null)) {
                message = message.Replace(change.Incoming!.Payload!, change.DestinationRelative);
                message = message.Replace(Path.GetFileName(change.Incoming.Payload!), change.DestinationRelative);
            }
            return message;
        }
        result.Errors = result.Errors.Select(DisplayPath).ToList();
        result.Warnings = result.Warnings.Select(DisplayPath).ToList();
        return result;
    }
    // Aliases may be loader-selected providers. Only duplicate actual self IDs
    // can use this file-collision guard; the combined graph handles aliases.
    private static bool Overlaps(PackModInspection a, PackModInspection b) => a.Confirmed && b.Confirmed && a.OwnIds.Overlaps(b.OwnIds);
    public void PreserveModCollisions(TPlan plan, CancellationToken token = default) {
        var candidates = plan.Changes.Where(item => !item.DisplayOnly && item.Incoming?.Payload is not null && LoadedModPath(plan.Package!, item.DestinationRelative)).ToArray();
        var incoming = candidates.ToDictionary(change => change, change => Inspect(plan.Package!, change.Incoming!.Payload!, token));
        foreach (TChange change in candidates) if (!incoming[change].Confirmed) plan.Warnings.Add(change.Relative + "：身份无法确认，未按弱匹配判为重复模组。");
        var local = LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(path => LoadedModPath(plan.Package!, path))
            .ToDictionary(path => path, path => Inspect(plan.Package!, Target(plan.Root!, path), token), StringComparer.OrdinalIgnoreCase);
        bool changed;
        do {
            token.ThrowIfCancellationRequested();
            changed = false;
            var removed = new HashSet<string>(plan.Changes.Where(change => change.ApplyAuthor && change.LocalRelative is not null).Select(change => change.LocalRelative!), StringComparer.OrdinalIgnoreCase);
            foreach (TChange change in candidates) {
                var collisions = local.Where(item => !removed.Contains(item.Key) && !string.Equals(item.Key, change.LocalRelative, StringComparison.OrdinalIgnoreCase) && Overlaps(item.Value, incoming[change])).Select(item => item.Key).ToList();
                if (collisions.Count == 0) continue;
                bool permanent = collisions.Any(path => !plan.Changes.Any(other => other.AllowAuthor && !other.DisplayOnly && string.Equals(other.LocalRelative, path, StringComparison.OrdinalIgnoreCase)));
                if (!change.Conflict || permanent && change.AllowAuthor) changed = true;
                change.Conflict = true; change.AllowAuthor &= !permanent;
                change.Reason = "与保留文件的自身 ID 或有效提供别名冲突：" + string.Join("、", collisions) + "；需确认相关选择，不会删除个人新增文件";
                foreach (string path in collisions) if (!change.Candidates.Contains(path)) change.Candidates.Add(path);
            }
            for (int a = 0; a < candidates.Length; a++) for (int b = a + 1; b < candidates.Length; b++) {
                TChange first = candidates[a], second = candidates[b];
                if (!first.ApplyAuthor || !second.ApplyAuthor || !Overlaps(incoming[first], incoming[second])) continue;
                if (!first.Conflict || !second.Conflict) changed = true;
                first.Conflict = second.Conflict = true;
                first.Reason = "新版作者文件之间存在自身 ID 或有效提供别名冲突：" + second.Relative;
                second.Reason = "新版作者文件之间存在自身 ID 或有效提供别名冲突：" + first.Relative;
            }
        } while (changed);
        RefreshRuntimeWarnings(plan, token);
    }
    private void RefreshRuntimeWarnings(TPlan plan, CancellationToken token) {
        plan.RuntimeWarnings.Clear();
        var removed = new HashSet<string>(PlanChanges(plan).Where(change => change.ApplyAuthor && change.LocalRelative is not null).Select(change => change.LocalRelative!), StringComparer.OrdinalIgnoreCase);
        foreach (string path in LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(path => LoadedModPath(plan.Package!, path) && !removed.Contains(path))) {
            PackModInspection info = Inspect(plan.Package!, Target(plan.Root!, path), token);
            foreach (string warning in info.CompatibilityWarnings) plan.RuntimeWarnings.Add(path + "：" + warning);
            foreach (string warning in info.Warnings) if (!plan.Warnings.Contains(path + "：" + warning)) plan.Warnings.Add(path + "：" + warning);
        }
    }
    public void ValidateChoices(TPlan plan, CancellationToken token = default) {
        ValidateSnapshot(plan, token);
        ValidatePlannedOperations(plan, token);
        List<(string Path, PackModInspection Ids)> final = new();
        var removed = new HashSet<string>(PlanChanges(plan).Where(change => change.ApplyAuthor && change.LocalRelative is not null).Select(change => change.LocalRelative!), StringComparer.OrdinalIgnoreCase);
        foreach (string path in LocalContent(plan.Root!, token, LegacyVersionFolder(plan.Package!)).Where(path => LoadedModPath(plan.Package!, path) && !removed.Contains(path)))
            final.Add((path, Inspect(plan.Package!, Target(plan.Root!, path), token)));
        foreach (TChange change in PlanChanges(plan).Where(change => change.ApplyAuthor && change.Incoming is not null)) {
            if (!Matches(change.Incoming!.Payload!, change.Incoming.Hash, token)) throw new IOException("暂存文件已变化：" + change.Relative);
            if (!LoadedModPath(plan.Package!, change.DestinationRelative)) continue;
            PackModInspection info = Inspect(plan.Package!, change.Incoming.Payload!, token);
            foreach (var other in final) if (Overlaps(info, other.Ids)) throw new IOException("所选作者版本与最终保留文件存在自身 ID 或提供别名冲突：" + change.Relative + " / " + other.Path);
            final.Add((change.DestinationRelative, info));
        }
        RefreshRuntimeWarnings(plan, token);
        PackDependencyGraphResult dependencies = CheckDependencies(plan, token);
        plan.DependencyCheck = dependencies;
        if (dependencies.HasErrors) throw new IOException("所选更新后的模组组合存在依赖或冲突问题，未提交更新。请调整作者版本与个人覆盖的选择：\r\n" + string.Join("\r\n", dependencies.Errors));
        if (dependencies.HasUnknown && !plan.AllowUnverifiedDependencies)
            throw new IOException("所选更新后的模组组合存在尚未确认的依赖或兼容性，请先明确确认风险再更新：\r\n" + string.Join("\r\n", dependencies.Warnings));
    }
}
