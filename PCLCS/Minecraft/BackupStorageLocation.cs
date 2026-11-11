// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public interface IBackupStoragePreference {
    string? Read();
    // A failed write must keep the previous value, or report an uncertain commit.
    void Write(string root, string? expected);
}
public sealed class BackupStoragePreferenceUncertainException : IOException {
    public BackupStoragePreferenceUncertainException(Exception error) : base("存储位置保存结果无法确认，原目录和新目录的数据均已保留，请重启后检查。", error) { }
}

public sealed class BackupStorageMoveResult {
    public string PreviousRoot = "", Root = "";
    public int Files;
    public long Bytes;
    public bool CleanupComplete;
}

/// <summary>Relocates only published world-backup and environment-history stores.
/// The host holds both engine gates throughout a move. The original profile
/// directory and other personal data are not part of the transaction.</summary>
public sealed class BackupStorageLocation {
    private static readonly string[] Categories = { "world-backups", "environment-history" };
    private readonly object gate = new();
    private readonly string profileRoot, configPath;
    private string currentRoot;
    private byte[]? configuration;
    private readonly IBackupStoragePreference? preference;
    private string? expectedPreference;
    public string Root => Volatile.Read(ref currentRoot);

    public BackupStorageLocation(string profileRoot) : this(profileRoot, null) { }
    public BackupStorageLocation(string profileRoot, IBackupStoragePreference? preference) {
        this.profileRoot = Full(profileRoot);
        this.preference = preference;
        configPath = Path.Combine(this.profileRoot, "backup-storage.json");
        string? saved = preference?.Read();
        if (saved is not null) { currentRoot = Full(saved); expectedPreference = saved; return; }
        NoLinks(configPath);
        if (Directory.Exists(configPath)) throw new IOException("备份位置配置被同名文件夹占用，请检查 backup-storage.json。");
        if (File.Exists(configPath)) {
            if (new FileInfo(configPath).Length > 65536) throw new IOException("备份位置配置过大。");
            configuration = File.ReadAllBytes(configPath);
            JObject data;
            try { data = JObject.Parse(new UTF8Encoding(false, true).GetString(configuration)); }
            catch (Exception error) when (error is Newtonsoft.Json.JsonException or DecoderFallbackException) { throw new IOException("备份位置配置损坏，请检查 backup-storage.json。", error); }
            if ((int?)data["schema"] != 1 || data["root"]?.Type != JTokenType.String) throw new IOException("备份位置配置无效。");
            currentRoot = Full(data["root"]!.ToString());
            // Read the former JSON once for compatibility. The dedicated store
            // is authoritative after import; future moves never rewrite JSON.
            if (preference is not null) { preference.Write(currentRoot, null); expectedPreference = currentRoot; }
        } else currentRoot = this.profileRoot;
    }

    private static string Full(string path) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new IOException("请选择绝对路径形式的存储目录。");
        string full = Path.GetFullPath(path);
        return full.Length == Path.GetPathRoot(full)!.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string parent) => path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void NoLinks(string path) {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份目录不能包含链接或重解析点。");
            string? parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Same(parent ?? "", current)) break;
            current = parent;
        }
    }
    private sealed class Item {
        public string Relative = "", Hash = "";
        public long Bytes, WriteTicks;
    }
    private static List<string> Directories(string root, CancellationToken token, List<Item> files) {
        List<string> folders = new();
        foreach (string category in Categories) {
            string start = Path.Combine(root, category);
            if (!Directory.Exists(start)) { if (File.Exists(start)) throw new IOException("备份存储目录被同名文件占用。"); continue; }
            Stack<string> pending = new(); pending.Push(start);
            while (pending.Count != 0) {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop(); NoLinks(folder);
                folders.Add(folder.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1));
                foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                    token.ThrowIfCancellationRequested(); NoLinks(path);
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else {
                        FileInfo info = new(path);
                        files.Add(new Item { Relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1), Bytes = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks });
                    }
                }
            }
        }
        return folders;
    }
    private static string Digest(Stream input, CancellationToken token) {
        using SHA256 algorithm = SHA256.Create();
        byte[] buffer = new byte[131072];
        while (true) {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            FileTaskProgress.RecordRead(count); algorithm.TransformBlock(buffer, 0, count, buffer, 0);
        }
        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(algorithm.Hash!).Replace("-", "");
    }
    private static bool Matches(string root, Item item, CancellationToken token, bool requireWriteTime = true) {
        string path = Path.Combine(root, item.Relative); NoLinks(path);
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != item.Bytes || (requireWriteTime && info.LastWriteTimeUtc.Ticks != item.WriteTicks)) return false;
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Digest(input, token) == item.Hash;
    }
    private static bool RemoveCopied(string root, List<Item> files, List<string> folders, CancellationToken token) {
        bool complete = true;
        foreach (Item item in files) {
            if (token.IsCancellationRequested) return false;
            string path = Path.Combine(root, item.Relative);
            try {
                NoLinks(path); if (!File.Exists(path)) continue;
                using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (held.Length != item.Bytes || Digest(held, token) != item.Hash) { complete = false; continue; }
                File.Delete(path);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { complete = false; }
        }
        foreach (string name in folders.OrderByDescending(name => name.Length)) {
            string folder = Path.Combine(root, name);
            try { NoLinks(folder); if (Directory.Exists(folder)) Directory.Delete(folder, false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { complete = false; }
        }
        return complete;
    }
    private void Persist(string target) {
        if (preference is not null) {
            preference.Write(target, expectedPreference);
            expectedPreference = target;
            Volatile.Write(ref currentRoot, target);
            return;
        }
        byte[] content = Encoding.UTF8.GetBytes(new JObject(new JProperty("schema", 1), new JProperty("root", target)).ToString());
        NoLinks(configPath);
        if (configuration is not null) LocalGameDataStore.AtomicWrite(configPath, content, LocalGameDataStore.Hash(configuration), false);
        else {
            Directory.CreateDirectory(profileRoot);
            string temporary = configPath + ".pcl-write-" + Guid.NewGuid().ToString("N");
            try {
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(content, 0, content.Length); output.Flush(true); }
                File.Move(temporary, configPath);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        configuration = content;
        Volatile.Write(ref currentRoot, target);
    }

    public BackupStorageMoveResult MoveTo(string destination, CancellationToken token, Action<double>? progress = null, Action? beforeCommit = null) {
        lock (gate) {
            token.ThrowIfCancellationRequested();
            string source = Root, target = Full(destination);
            var result = new BackupStorageMoveResult { PreviousRoot = source, Root = target, CleanupComplete = true };
            if (Same(source, target)) return result;
            if (Inside(source, target) || Inside(target, source)) throw new IOException("新旧备份位置不能互相包含。");
            NoLinks(source); NoLinks(target);
            if (!Directory.Exists(source) && !Same(source, profileRoot)) throw new IOException("当前备份目录不可用，请先连接原存储设备。");
            if (!Directory.Exists(target) || Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("请选择一个已存在的空文件夹，已有数据不会被覆盖。");
            List<Item> files = new(); List<string> folders = Directories(source, token, files);
            long total = 0; foreach (Item item in files) total = checked(total + item.Bytes);
            result.Files = files.Count; result.Bytes = total;
            string stage = Path.Combine(target, ".pcl-backup-move-" + Guid.NewGuid().ToString("N"));
            List<Item> copied = new(); List<string> created = new(); List<string> published = new(); bool committed = false;
            try {
                Directory.CreateDirectory(stage); long done = 0;
                foreach (string folder in folders) { NoLinks(Path.Combine(source, folder)); Directory.CreateDirectory(Path.Combine(stage, folder)); created.Add(folder); }
                foreach (Item item in files) {
                    token.ThrowIfCancellationRequested(); string from = Path.Combine(source, item.Relative), to = Path.Combine(stage, item.Relative);
                    NoLinks(from); NoLinks(to);
                    bool createdOutput = false;
                    try {
                        using (FileStream input = new(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (FileStream output = new(to, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                            createdOutput = true;
                            using SHA256 hash = SHA256.Create(); byte[] buffer = new byte[131072]; long countBytes = 0;
                            while (true) {
                                token.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break;
                                output.Write(buffer, 0, count); hash.TransformBlock(buffer, 0, count, buffer, 0); countBytes += count; done += count;
                                FileTaskProgress.RecordRead(count); FileTaskProgress.RecordWrite(count); progress?.Invoke(0.7 * done / Math.Max(1.0, total));
                            }
                            output.Flush(true); hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            item.Hash = BitConverter.ToString(hash.Hash!).Replace("-", "");
                            if (countBytes != item.Bytes) throw new IOException("迁移期间源备份文件发生变化，已停止切换位置。");
                        }
                    } catch {
                        if (createdOutput) { NoLinks(to); if (File.Exists(to)) File.Delete(to); }
                        throw;
                    }
                    copied.Add(item); File.SetLastWriteTimeUtc(to, new DateTime(item.WriteTicks, DateTimeKind.Utc));
                    if (!Matches(stage, item, token, false)) throw new IOException("新目录中的备份校验失败，原目录保持有效。");
                }
                beforeCommit?.Invoke(); token.ThrowIfCancellationRequested();
                List<Item> latest = new(); List<string> latestFolders = Directories(source, token, latest);
                if (!new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase).SetEquals(latestFolders) || latest.Count != files.Count ||
                    !new HashSet<string>(files.Select(item => item.Relative), StringComparer.OrdinalIgnoreCase).SetEquals(latest.Select(item => item.Relative)) ||
                    files.Any(item => !Matches(source, item, token))) throw new IOException("迁移期间源目录发生变化，原目录保持有效。");
                NoLinks(target);
                if (Directory.EnumerateFileSystemEntries(target).Any(path => !Same(path, stage))) throw new IOException("目标目录出现其他文件，已停止迁移。");
                progress?.Invoke(0.9); token.ThrowIfCancellationRequested();
                foreach (string category in Categories) if (Directory.Exists(Path.Combine(stage, category))) {
                    Directory.Move(Path.Combine(stage, category), Path.Combine(target, category)); published.Add(category);
                }
                try { Persist(target); committed = true; }
                catch (BackupStoragePreferenceUncertainException) {
                    // A pointer may already refer to target. Preserve both copies.
                    committed = true;
                    throw;
                }
                // After the durable pointer commits, cancellation only stops cleanup.
                // New data is already complete and the active root must stay at target.
                result.CleanupComplete = RemoveCopied(source, files, folders, token);
                return result;
            } finally {
                if (!committed) {
                    foreach (string category in published) {
                        var subset = copied.Where(item => item.Relative.StartsWith(category + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
                        RemoveCopied(target, subset, created.Where(name => Same(name, category) || name.StartsWith(category + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList(), CancellationToken.None);
                    }
                }
                // Delete only files produced by this operation, never a foreign tree.
                RemoveCopied(stage, copied, created, CancellationToken.None);
                try { NoLinks(stage); if (Directory.Exists(stage)) Directory.Delete(stage, false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
