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

public sealed partial class EnvironmentHistoryEngine<TInstance> where TInstance : class {
    public string Restore(string versionFolder, string gameRoot, string mcRoot, string id, CancellationToken token,
        Action<int>? beforeApply = null, Func<string>? captureBefore = null) =>
        RestoreWithProgress(versionFolder, gameRoot, mcRoot, id, token, null, beforeApply, captureBefore);

    public string RestoreWithProgress(string versionFolder, string gameRoot, string mcRoot, string id, CancellationToken token, Action<double>? progress,
        Action<int>? beforeApply = null, Func<string>? captureBefore = null) {
        lock (Gate) {
            using (new StoreLease(this, versionFolder)) {
                RestoringCount = checked(RestoringCount + 1);
                try {
                    JObject? point = Points(versionFolder).FirstOrDefault(item => SameText(item["id"]!.ToString(), id));
                    if (point is null) throw new IOException("找不到该环境还原点。");
                    if (!host.SamePath(point["gameRoot"]!.ToString(), gameRoot)) throw new IOException("此还原点的游戏目录与当前版本隔离设置不同，请先恢复原隔离设置。");
                    int coverage = CoverageVersion(point);
                    string storeRoot = Store(versionFolder);
                    Dictionary<string, string> current = FilesWithCoverage(versionFolder, gameRoot, coverage), sources = new(StringComparer.OrdinalIgnoreCase);
                    List<JProperty> entries = ((JObject)point["files"]!).Properties().ToList(); int checkedCount = 0;
                    foreach (JProperty entry in entries) {
                        token.ThrowIfCancellationRequested();
                        string key = entry.Name, hash = entry.Value.ToString(), blob = ObjectPath(storeRoot, hash);
                        if (!SameText(FileTaskProgress.Digest(blob, token, FileTaskProgress.Slice(progress, 0.02 + 0.16 * checkedCount / Math.Max(1, entries.Count), 0.02 + 0.16 * checked(checkedCount + 1) / Math.Max(1, entries.Count))), hash)) throw new IOException("还原点文件缺失或损坏，当前环境未修改。");
                        if (key.StartsWith("m/")) {
                            string relative = host.RelativePath(key.Substring(2));
                            if (!relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) throw new IOException("无效的依赖文件路径。");
                            string destination = host.Target(mcRoot, relative);
                            if (File.Exists(destination) && !SameText(host.Digest(destination), hash)) throw new IOException("共享支持库已被修改，已停止恢复：" + relative);
                            sources[key] = blob;
                        } else if (key.StartsWith("g/") && IsGameFileCovered(key.Substring(2), coverage)) {
                            host.Target(gameRoot, key.Substring(2));
                            sources[key] = blob;
                        } else if (key.StartsWith("v/") && ((!key.Substring(2).Contains("/") && new[] { ".json", ".jar" }.Contains(Path.GetExtension(key).ToLowerInvariant())) || new[] { "v/PCL/Setup.ini", "v/PCL/config.json", "v/PCL/ModpackOrigin.json" }.Contains(key))) {
                            host.Target(versionFolder, key.Substring(2));
                            sources[key] = blob;
                        } else {
                            throw new IOException("还原点包含不支持的文件范围。");
                        }
                        checkedCount = checked(checkedCount + 1);
                    }
                    List<Tuple<string, string?, string?>> operations = new();
                    List<string> keys = current.Keys.Union(sources.Keys.Where(value => !value.StartsWith("m/")), StringComparer.OrdinalIgnoreCase).ToList();
                    checkedCount = 0;
                    foreach (string key in keys) {
                        string destination = host.Target(key.StartsWith("v/") ? versionFolder : gameRoot, key.Substring(2));
                        sources.TryGetValue(key, out string? payload);
                        string? before = FileTaskProgress.Digest(destination, token, FileTaskProgress.Slice(progress, 0.18 + 0.14 * checkedCount / Math.Max(1, keys.Count), 0.18 + 0.14 * checked(checkedCount + 1) / Math.Max(1, keys.Count)));
                        checkedCount = checked(checkedCount + 1);
                        if (payload is not null && SameText(before, host.Digest(payload))) continue;
                        operations.Add(Tuple.Create(destination, payload, before));
                    }
                    // 完整环境先留档；缺失共享依赖与原事务的处理顺序保持不变。
                    string undoId = captureBefore is null ? CaptureWithProgress(versionFolder, gameRoot, "恢复前自动保存", token, FileTaskProgress.Slice(progress, 0.32, 0.62)) : captureBefore();
                    progress?.Invoke(0.62);
                    List<KeyValuePair<string, string>> libraries = sources.Where(item => item.Key.StartsWith("m/")).ToList();
                    for (int index = 0; index < libraries.Count; index++) {
                        token.ThrowIfCancellationRequested();
                        var pair = libraries[index];
                        Action<double>? libraryProgress = FileTaskProgress.Slice(progress, 0.62 + 0.03 * index / Math.Max(1, libraries.Count), 0.62 + 0.03 * checked(index + 1) / Math.Max(1, libraries.Count));
                        string destination = host.Target(mcRoot, pair.Key.Substring(2));
                        if (!File.Exists(destination)) {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            string temporary = destination + ".pcl-restore-" + Guid.NewGuid().ToString("N");
                            try {
                                FileTaskProgress.Copy(pair.Value, temporary, token, FileTaskProgress.Slice(libraryProgress, 0, 0.8));
                                if (!SameText(FileTaskProgress.Digest(temporary, token, FileTaskProgress.Slice(libraryProgress, 0.8, 1)), point["files"]![pair.Key]!.ToString())) throw new IOException("共享支持库复制校验失败，当前版本未修改。");
                                token.ThrowIfCancellationRequested();
                                File.Move(temporary, destination);
                            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                        libraryProgress?.Invoke(1);
                    }
                    if (host.ApplyTransactionWithProgress is not null)
                        host.ApplyTransactionWithProgress(gameRoot, versionFolder, operations, token, FileTaskProgress.Slice(progress, 0.65, 0.99), beforeApply);
                    else host.ApplyTransaction(gameRoot, versionFolder, operations, token, beforeApply);
                    progress?.Invoke(1);
                    return undoId;
                } finally { RestoringCount = checked(RestoringCount - 1); }
            }
        }
    }
}
