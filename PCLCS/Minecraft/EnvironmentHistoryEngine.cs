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
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public static class EnvironmentHistoryRules {
    public const int CurrentCoverageVersion = 2;
    internal static readonly string[] AutomaticLabels = { "游戏启动前", "修改游戏组件前", "更新模组前", "整合包更新前", "恢复前自动保存", "测试副本迁移前" };
    private static readonly string[] LegacyGameFolders = { "mods", "config", "defaultconfigs", "kubejs", "scripts", "resources" };
    public static readonly string[] GameEnvironmentFolders = { "mods", "coremods", "config", "defaultconfigs", "configureddefaults", "kubejs", "scripts", "resources", "global_packs", "openloader" };

    public static int CoverageVersion(JObject point) {
        JToken? value = point["coverageVersion"];
        if (value is null) return 1;
        if (value.Type != JTokenType.Integer) throw new IOException("还原点的覆盖范围版本无效。");
        int version = value.ToObject<int>();
        if (version < 1 || version > CurrentCoverageVersion) throw new IOException("此还原点使用了不支持的覆盖范围，请使用对应版本的启动器。");
        return version;
    }
    public static IEnumerable<string> FoldersForCoverage(int version) => version switch {
        1 => LegacyGameFolders,
        2 => GameEnvironmentFolders,
        _ => throw new IOException("不支持此还原点的覆盖范围。")
    };
    public static bool IsGameFile(string relative) => IsGameFileCovered(relative, CurrentCoverageVersion);
    public static bool IsGameFileCovered(string relative, int version) {
        string lower = relative.Replace("\\", "/").ToLowerInvariant(), top = lower.Split('/')[0];
        if (lower.Contains("waypoint") || lower.StartsWith("config/xaero") || lower.StartsWith("config/journeymap") || lower.StartsWith("config/voxelmap")) return false;
        if (FoldersForCoverage(version).Contains(top)) return true;
        if (lower.Contains("/")) return false;
        return lower == "options.txt" || lower.StartsWith("optionsof") || lower.StartsWith("optionsshaders") ||
            new[] { ".cfg", ".toml", ".properties" }.Contains(Path.GetExtension(lower));
    }
}

/// <summary>LF 内容寻址还原点、跨进程租约、保留策略及恢复事务编排。</summary>
// LF EnvironmentHistory.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// unchanged through 07dd3503b38b39095c8b92b40ae0454907387f9f. No CE code reused.
public sealed partial class EnvironmentHistoryEngine<TInstance> where TInstance : class {
    public readonly object Gate;
    private readonly EnvironmentHistoryHost<TInstance> host;
    public int RestoringCount;
    public string? TestRoot { get; set; }
    public EnvironmentHistoryEngine(object gate, EnvironmentHistoryHost<TInstance> host) { Gate = gate; this.host = host; }
    private static bool SameText(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    public int CoverageVersion(JObject point) => EnvironmentHistoryRules.CoverageVersion(point);
    public IEnumerable<string> FoldersForCoverage(int version) => EnvironmentHistoryRules.FoldersForCoverage(version);
    public bool IsGameFile(string relative) => EnvironmentHistoryRules.IsGameFile(relative);
    public bool IsGameFileCovered(string relative, int version) => EnvironmentHistoryRules.IsGameFileCovered(relative, version);

    public string Store(string versionFolder) {
        string key = host.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(versionFolder).TrimEnd('\\').ToLowerInvariant())).Replace("/", "_").Replace("+", "-").TrimEnd('=');
        return Path.Combine(TestRoot ?? Path.Combine(host.DataRoot(), "environment-history"), key);
    }

    private sealed class StoreLease : IDisposable {
        private readonly Mutex mutex;
        public StoreLease(EnvironmentHistoryEngine<TInstance> owner, string versionFolder, bool directStore = false) {
            string root = directStore ? versionFolder : owner.Store(versionFolder);
            owner.host.EnsureNoLinks(root);
            string key = owner.host.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())).Replace("/", "_").Replace("+", "-").TrimEnd('=');
            mutex = new Mutex(false, "Local\\PCLLF.EnvironmentHistory." + key);
            try {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("其他启动器正在处理此版本的还原点，请稍后重试。");
            } catch (AbandonedMutexException) {
                // 本线程已取得退出进程留下的锁，引用仍按完整清单计算。
            } catch {
                mutex.Dispose();
                throw;
            }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private string Safe(string storeRoot, string relative) => host.Target(storeRoot, relative);
    private string ObjectPath(string storeRoot, string? hash) {
        if (!Regex.IsMatch(hash ?? "", @"\Asha1:[a-f0-9]{40}\z")) throw new IOException("还原点文件校验值无效。");
        return Safe(storeRoot, "objects/" + hash!.Substring(5));
    }
    private void Walk(string root, string folder, Dictionary<string, string> result) {
        host.EnsureWithin(root, folder);
        if (!Directory.Exists(folder)) return;
        foreach (string fileName in Directory.GetFiles(folder)) {
            host.EnsureWithin(root, fileName);
            string relative = fileName.Substring(host.AddSlashSuffix(root).Length).Replace("\\", "/");
            if (IsGameFile(relative)) result["g/" + relative] = fileName;
        }
        foreach (string child in Directory.GetDirectories(folder)) Walk(root, child, result);
    }
    public Dictionary<string, string> Files(string versionFolder, string gameRoot) => FilesWithCoverage(versionFolder, gameRoot, EnvironmentHistoryRules.CurrentCoverageVersion);
    public Dictionary<string, string> FilesWithCoverage(string versionFolder, string gameRoot, int version) {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in FoldersForCoverage(version)) Walk(gameRoot, Path.Combine(gameRoot, name), result);
        if (Directory.Exists(gameRoot)) {
            foreach (string fileName in Directory.GetFiles(gameRoot)) {
                if (IsGameFile(Path.GetFileName(fileName))) result["g/" + Path.GetFileName(fileName)] = host.Target(gameRoot, Path.GetFileName(fileName));
            }
        }
        if (Directory.Exists(versionFolder)) {
            foreach (string fileName in Directory.GetFiles(versionFolder)) {
                if (new[] { ".json", ".jar" }.Contains(Path.GetExtension(fileName).ToLowerInvariant())) result["v/" + Path.GetFileName(fileName)] = host.Target(versionFolder, Path.GetFileName(fileName));
            }
        }
        foreach (string name in new[] { "PCL/Setup.ini", "PCL/config.json", "PCL/ModpackOrigin.json" }) {
            string fileName = host.Target(versionFolder, name);
            if (File.Exists(fileName)) result["v/" + name] = fileName;
        }
        return result;
    }
    public string Capture(string versionFolder, string gameRoot, string label, CancellationToken token,
        Dictionary<string, string>? dependencies = null, string description = "") =>
        CaptureWithProgress(versionFolder, gameRoot, label, token, null, dependencies, description);

    public string CaptureWithProgress(string versionFolder, string gameRoot, string label, CancellationToken token, Action<double>? progress,
        Dictionary<string, string>? dependencies = null, string description = "") {
        token.ThrowIfCancellationRequested();
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                string storeRoot = Store(versionFolder), id = Guid.NewGuid().ToString("N");
                Dictionary<string, string> entries = Files(versionFolder, gameRoot);
                JObject content = new();
                if (dependencies is not null) {
                    foreach (KeyValuePair<string, string> pair in dependencies) entries.Add("m/" + host.RelativePath(pair.Key), pair.Value);
                }
                long total = Math.Max(1L, FileTaskProgress.Size(entries.Values)), completed = 0;
                progress?.Invoke(0.02);
                foreach (KeyValuePair<string, string> pair in entries) {
                    token.ThrowIfCancellationRequested();
                    long weight = File.Exists(pair.Value) ? Math.Max(1L, new FileInfo(pair.Value).Length) : 1L;
                    Action<double>? fileProgress = FileTaskProgress.Slice(progress, 0.02 + 0.8 * completed / total, 0.02 + 0.8 * checked(completed + weight) / total);
                    string? hash = FileTaskProgress.Digest(pair.Value, token, FileTaskProgress.Slice(fileProgress, 0, 0.25));
                    if (hash is null) throw new IOException("创建还原点期间文件被移走：" + pair.Key);
                    string blob = ObjectPath(storeRoot, hash);
                    if (!File.Exists(blob)) {
                        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                        string temporary = blob + ".tmp-" + Guid.NewGuid().ToString("N");
                        try {
                            FileTaskProgress.Copy(pair.Value, temporary, token, FileTaskProgress.Slice(fileProgress, 0.25, 0.5));
                            if (!SameText(FileTaskProgress.Digest(temporary, token, FileTaskProgress.Slice(fileProgress, 0.5, 0.75)), hash)) throw new IOException("创建还原点期间文件发生变化。");
                            File.Move(temporary, blob);
                        } finally {
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    } else if (!SameText(FileTaskProgress.Digest(blob, token, FileTaskProgress.Slice(fileProgress, 0.25, 0.75)), hash)) {
                        throw new IOException("已有还原点内容校验失败，请保留备份并检查磁盘。");
                    }
                    if (!SameText(FileTaskProgress.Digest(pair.Value, token, FileTaskProgress.Slice(fileProgress, 0.75, 1)), hash)) throw new IOException("创建还原点期间文件发生变化。");
                    content[pair.Key] = hash;
                    completed = checked(completed + weight);
                }
                Dictionary<string, string> latest = Files(versionFolder, gameRoot);
                if (!new HashSet<string>(entries.Keys.Where(key => !key.StartsWith("m/")), StringComparer.OrdinalIgnoreCase).SetEquals(latest.Keys)) throw new IOException("创建还原点期间目录内容发生变化。");
                total = Math.Max(1L, FileTaskProgress.Size(latest.Values)); completed = 0;
                foreach (KeyValuePair<string, string> pair in latest) {
                    long weight = Math.Max(1L, new FileInfo(pair.Value).Length);
                    if (!SameText(FileTaskProgress.Digest(pair.Value, token, FileTaskProgress.Slice(progress, 0.82 + 0.16 * completed / total, 0.82 + 0.16 * checked(completed + weight) / total)), content[pair.Key]!.ToString())) throw new IOException("创建还原点期间配置发生变化。");
                    completed = checked(completed + weight);
                }
                JObject data = new(new JProperty("schema", 1), new JProperty("coverageVersion", EnvironmentHistoryRules.CurrentCoverageVersion), new JProperty("id", id), new JProperty("created", DateTime.UtcNow), new JProperty("label", label),
                    new JProperty("description", description), new JProperty("gameRoot", Path.GetFullPath(gameRoot)), new JProperty("healthy", false), new JProperty("manual", !EnvironmentHistoryRules.AutomaticLabels.Contains(label)), new JProperty("files", content));
                token.ThrowIfCancellationRequested();
                host.AtomicWrite(Safe(storeRoot, "points/" + id + ".json"), Encoding.UTF8.GetBytes(data.ToString()));
                progress?.Invoke(1);
                return id;
            }
        }
    }
    public string CaptureInstance(TInstance instance, string label, CancellationToken token) => CaptureInstanceWithProgress(instance, label, token, null);

    public string CaptureInstanceWithProgress(TInstance instance, string label, CancellationToken token, Action<double>? progress) {
        string mcRoot = Path.GetDirectoryName(Path.GetDirectoryName(host.VersionPath(instance).TrimEnd('\\'))) + "\\";
        Dictionary<string, string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        foreach (string libraryPath in host.LibraryPaths(instance)) {
            if (host.IsParentOf(host.VersionPath(instance), libraryPath)) continue;
            host.EnsureWithin(mcRoot, libraryPath);
            string relative = host.RemoveExtendedPrefix(Path.GetFullPath(libraryPath)).Substring(mcRoot.Length).Replace("\\", "/");
            if (!relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) throw new IOException("游戏依赖不在标准目录中，无法完整记录环境。");
            dependencies[relative] = libraryPath;
        }
        JObject json = host.JsonObject(instance);
        HashSet<string> seen = new();
        while (json["inheritsFrom"] is not null) {
            string name = json["inheritsFrom"]!.ToString();
            host.SafeName(name);
            if (!seen.Add(name) || seen.Count > 32) throw new IOException("版本继承关系无效。");
            string relative = "versions/" + name + "/" + name + ".json", address = host.Target(mcRoot, relative);
            dependencies[relative] = address;
            json = JObject.Parse(File.ReadAllText(address));
        }
        string id = CaptureWithProgress(host.VersionPath(instance), host.GameFolder(instance), label, token, FileTaskProgress.Slice(progress, 0, 0.98), dependencies,
            host.VanillaName(instance) + " · " + string.Join("、", host.ModLoaderVersions(instance)));
        ApplyRetention(instance, id, token);
        progress?.Invoke(1);
        return id;
    }
    public List<JObject> Points(string versionFolder) => ReadStorePoints(Store(versionFolder));
    private List<JObject> ReadStorePoints(string storeRoot) {
        // LF atomically publishes manifests; published-list readers do not take snapshot leases.
        string folder = Path.Combine(storeRoot, "points");
        List<JObject> result = new();
        host.EnsureWithin(storeRoot, folder);
        if (!Directory.Exists(folder)) return result;
        foreach (string fileName in Directory.GetFiles(folder, "*.json")) {
            try {
                host.EnsureWithin(storeRoot, fileName);
                if (new FileInfo(fileName).Length > 32 * 1024 * 1024) throw new IOException("还原点清单过大。");
                JObject value;
                using (FileStream input = new(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new(input)) value = JObject.Parse(reader.ReadToEnd());
                if ((int)value["schema"]! != 1) throw new IOException("不支持此版本的环境还原点。");
                result.Add(value);
            } catch (FileNotFoundException) {
                // An atomic cleanup removed a manifest after enumeration; refresh sees the new list.
            }
        }
        return result.OrderByDescending(item => (DateTime)item["created"]!).ToList();
    }
    public bool IsManualPoint(JObject point) {
        JToken? value = point["manual"];
        if (value is not null) return value.Type != JTokenType.Boolean || value.ToObject<bool>();
        return !EnvironmentHistoryRules.AutomaticLabels.Contains(point["label"]?.ToString()!);
    }
    public void ApplyRetention(TInstance instance, string newestId, CancellationToken token) {
        if (!host.AutoCleanup(instance)) return;
        try {
            int removed = PruneAutomatic(host.VersionPath(instance), host.Retention(instance), newestId, token);
            if (removed > 0) host.Info($"已自动清理 {removed} 个旧环境还原点，手动还原点已保留。");
        } catch (OperationCanceledException) { throw; }
        catch (Exception error) { host.Warn(error, "新还原点已保存，但自动清理旧还原点失败"); }
    }
    private void AddReferences(JObject point, HashSet<string> references) {
        if ((int)point["schema"]! != 1 || !Regex.IsMatch(point["id"]?.ToString() ?? "", @"\A[a-f0-9]{32}\z")) throw new IOException("还原点清单无效，未清理旧还原点。");
        CoverageVersion(point);
        JObject? content = point["files"] as JObject;
        if (content is null) throw new IOException("还原点文件清单缺失，未清理旧还原点。");
        foreach (JProperty entry in content.Properties()) {
            string hash = entry.Value.ToString();
            if (!Regex.IsMatch(hash, @"\Asha1:[a-f0-9]{40}\z")) throw new IOException("还原点引用无效，未清理旧还原点。");
            references.Add(hash.Substring(5));
        }
    }
    public int PruneAutomatic(string versionFolder, int count, string? newestId, CancellationToken token) {
        if (count < 1 || count > 100) throw new IOException("自动还原点保留数量必须为 1 到 100。");
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                if (RestoringCount > 0) return 0;
                token.ThrowIfCancellationRequested();
                string storeRoot = Store(versionFolder);
                List<JObject> all = Points(versionFolder);
                HashSet<string> references = new(StringComparer.Ordinal), seen = new(StringComparer.Ordinal);
                foreach (JObject point in all) {
                    AddReferences(point, references);
                    string id = point["id"]!.ToString(), address = Safe(storeRoot, "points/" + id + ".json");
                    if (!seen.Add(id) || !File.Exists(address) || !JToken.DeepEquals(JObject.Parse(File.ReadAllText(address)), point)) throw new IOException("还原点列表已变化，未清理旧还原点。");
                }
                List<JObject> automatic = all.Where(point => !IsManualPoint(point)).ToList();
                if (automatic.Count <= count) return 0;
                HashSet<string> keep = new(automatic.Where(point => SameText(point["id"]!.ToString(), newestId)).Concat(automatic.Where(point => !SameText(point["id"]!.ToString(), newestId))).Take(count).Select(point => point["id"]!.ToString()));
                List<JObject> expired = automatic.Where(point => !keep.Contains(point["id"]!.ToString())).ToList();
                List<string> unused = UnreferencedObjects(storeRoot,
                    all.Where(item => IsManualPoint(item) || keep.Contains(item["id"]!.ToString())), token);
                token.ThrowIfCancellationRequested();
                foreach (JObject point in expired) { token.ThrowIfCancellationRequested(); File.Delete(Safe(storeRoot, "points/" + point["id"]!.ToString() + ".json")); }
                foreach (string address in unused) { token.ThrowIfCancellationRequested(); host.EnsureNoLinks(address); File.Delete(address); }
                return expired.Count;
            }
        }
    }
    public void MarkHealthy(string versionFolder, string? id, bool healthy) {
        if (string.IsNullOrEmpty(id) || !healthy) return;
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                if (!Regex.IsMatch(id!, @"\A[a-f0-9]{32}\z")) throw new IOException("还原点编号无效。");
                string address = Safe(Store(versionFolder), "points/" + id + ".json");
                if (!File.Exists(address)) return;
                JObject value = JObject.Parse(File.ReadAllText(address));
                value["healthy"] = true;
                host.AtomicWrite(address, Encoding.UTF8.GetBytes(value.ToString()));
            }
        }
    }
    public void OpenFolder(string versionFolder) {
        string folder = Store(versionFolder);
        if (Directory.Exists(folder)) host.OpenFolder(folder);
        else host.Hint("此版本还没有环境还原点。");
    }
    private List<JObject> ValidatedPoints(string storeRoot) {
        List<JObject> all = ReadStorePoints(storeRoot);
        HashSet<string> seen = new(StringComparer.Ordinal), references = new(StringComparer.Ordinal);
        foreach (JObject point in all) {
            AddReferences(point, references);
            string id = point["id"]!.ToString(), path = Safe(storeRoot, "points/" + id + ".json");
            if (!seen.Add(id) || !File.Exists(path) || !JToken.DeepEquals(JObject.Parse(File.ReadAllText(path)), point))
                throw new IOException("还原点列表已变化，请刷新后再删除。");
        }
        return all;
    }
    private List<string> UnreferencedObjects(string storeRoot, IEnumerable<JObject> retained, CancellationToken token) {
        HashSet<string> references = new(StringComparer.Ordinal);
        foreach (JObject point in retained) { token.ThrowIfCancellationRequested(); AddReferences(point, references); }
        string objects = Path.Combine(storeRoot, "objects");
        host.EnsureNoLinks(objects);
        List<string> unused = new();
        if (Directory.Exists(objects)) foreach (string path in Directory.GetFiles(objects)) {
            token.ThrowIfCancellationRequested(); host.EnsureNoLinks(path);
            string hash = Path.GetFileName(path);
            if (Regex.IsMatch(hash, @"\A[a-f0-9]{40}\z") && !references.Contains(hash)) unused.Add(path);
        }
        return unused;
    }
    private void MarkCleanupPending(string root) => host.AtomicWrite(Safe(root, "cleanup-pending"), Array.Empty<byte>());
    private void SweepUnused(string root, List<string> unused, CancellationToken token) {
        PermanentResourceDeletion.Delete(root, unused, token);
        File.Delete(Safe(root, "cleanup-pending"));
    }
    public string? DeletePoints(string versionFolder, IEnumerable<string> ids) => DeletePointsWithCancellation(versionFolder, ids, CancellationToken.None);
    public string? DeletePointsWithCancellation(string versionFolder, IEnumerable<string> ids, CancellationToken token) {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                if (RestoringCount > 0) throw new IOException("正在恢复环境，请等待恢复任务结束后再删除还原点。");
                List<string> selected = ids.Distinct(StringComparer.Ordinal).ToList();
                if (selected.Count == 0) return null;
                token.ThrowIfCancellationRequested();
                string root = Store(versionFolder);
                List<JObject> all = ValidatedPoints(root);
                foreach (string id in selected) {
                    if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z") || !all.Any(point => SameText(point["id"]!.ToString(), id)))
                        throw new IOException("还原点编号无效或记录已变化，请刷新列表。");
                }
                List<string> unused = UnreferencedObjects(root, all.Where(point => !selected.Contains(point["id"]!.ToString())), token);
                token.ThrowIfCancellationRequested(); MarkCleanupPending(root);
                PermanentResourceDeletion.Delete(root, selected.Select(id => Safe(root, "points/" + id + ".json")), token);
                SweepUnused(root, unused, token);
                return root;
            }
        }
    }
    public void CleanupDeletedStores(CancellationToken token) {
        lock (Gate) {
            string category = TestRoot ?? Path.Combine(host.DataRoot(), "environment-history");
            host.EnsureNoLinks(category);
            if (RestoringCount > 0 || !Directory.Exists(category)) return;
            int keyLength = host.Hash(Array.Empty<byte>()).TrimEnd('=').Length;
            foreach (string root in Directory.GetDirectories(category)) {
                token.ThrowIfCancellationRequested();
                string key = Path.GetFileName(root);
                if (key.Length != keyLength || !Regex.IsMatch(key, @"\A[A-Za-z0-9_-]+\z")) continue;
                try {
                    using (new StoreLease(this, root, true)) {
                        string deleted = Path.Combine(root, "deleted"), pending = Safe(root, "cleanup-pending");
                        host.EnsureWithin(root, deleted); host.EnsureNoLinks(deleted);
                        if (!Directory.Exists(deleted) && !File.Exists(pending)) continue;
                        List<string> unused = UnreferencedObjects(root, ValidatedPoints(root), token);
                        token.ThrowIfCancellationRequested(); MarkCleanupPending(root);
                        if (Directory.Exists(deleted)) PermanentResourceDeletion.Delete(root, new[] { deleted }, token);
                        SweepUnused(root, unused, token);
                    }
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException) {
                    host.Warn(error, "旧还原点删除目录暂未清理，下次启动重试");
                }
            }
        }
    }
}
