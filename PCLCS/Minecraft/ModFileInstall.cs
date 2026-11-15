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
using System.Threading;

namespace PCLCS;

public sealed class ModFileIdentity {
    public string Path = null!;
    public string? ProjectKey;
    public IReadOnlyCollection<string> RootIds = Array.Empty<string>();
}

public static class ModDuplicateRules {
    // Only exact platform-scoped projects or actual top-level loader IDs count.
    // Names, timestamps, aliases and nested-library IDs are never identity evidence.
    public static HashSet<string> Find(IEnumerable<ModFileIdentity> files) {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files) {
            IEnumerable<string> identities = file.RootIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => "id:" + id);
            if (!string.IsNullOrWhiteSpace(file.ProjectKey)) identities = identities.Append("project:" + file.ProjectKey);
            foreach (string key in identities.Distinct(StringComparer.Ordinal)) {
                if (!keys.TryGetValue(key, out var paths)) keys[key] = paths = new(StringComparer.OrdinalIgnoreCase);
                paths.Add(System.IO.Path.GetFullPath(file.Path));
            }
        }
        return keys.Values.Where(paths => paths.Count > 1).SelectMany(paths => paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ModFileInstallResult(string Destination, int ReplacedFiles, bool Identified);

/// <summary>Install a verified download, replacing only complete, identical root-ID sets.</summary>
public sealed class ModFileInstall {
    private readonly Func<string, CancellationToken, IReadOnlyCollection<string>?> readIds;
    private readonly Func<string,CancellationToken,bool>? matchesVerifiedProject;
    public Action<Exception>? CleanupWarning { get; set; }
    public ModFileInstall(Func<string, CancellationToken, IReadOnlyCollection<string>?> readIds, Func<string,CancellationToken,bool>? matchesVerifiedProject = null) {
        this.readIds = readIds; this.matchesVerifiedProject = matchesVerifiedProject;
    }
    public static bool IsModFile(string path) {
        path = EnabledPath(path);
        return new[] { ".jar", ".zip", ".litemod" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    private static string EnabledPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] :
        path.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    private static string Full(string path) {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return System.IO.Path.GetFullPath(path);
    }
    private static void NoLinks(string path) {
        for (string? current = Full(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("模组自动替换不支持链接路径，请选择实际的模组文件夹。");
    }
    private static string Hash(string path, CancellationToken token) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static HashSet<string>? Ids(IReadOnlyCollection<string>? ids) => ids is null || ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace)
        ? null : ids.ToHashSet(StringComparer.Ordinal);

    public ModFileInstallResult Install(string downloaded, string destination, CancellationToken token = default, Action<int,int>? progress = null) {
        token.ThrowIfCancellationRequested();
        downloaded = Full(downloaded); destination = Full(destination);
        string root = System.IO.Path.GetDirectoryName(destination)!;
        if (!IsModFile(destination) || !Directory.Exists(root)) throw new IOException("模组下载目录已不存在或目标扩展名不正确。");
        NoLinks(root); NoLinks(downloaded);
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Local\PCLLF.ModInstall." + key);
        bool owned = false;
        try {
            while (!owned) {
                token.ThrowIfCancellationRequested();
                try { owned = mutex.WaitOne(50); } catch (AbandonedMutexException) { owned = true; }
            }
            return Commit(downloaded, destination, root, token, progress);
        } finally { if (owned) mutex.ReleaseMutex(); }
    }

    private ModFileInstallResult Commit(string downloaded, string destination, string root, CancellationToken token, Action<int,int>? progress) {
        HashSet<string>? incoming;
        string incomingHash;
        using (var evidence = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            incoming = Ids(readIds(downloaded, token));
            incomingHash = Hash(downloaded, token);
        }
        var existing = new List<(string Path, string Hash)>();
        string[] files = Directory.GetFiles(root).Where(IsModFile).ToArray();
        for (int index = 0; index < files.Length; index++) {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(files[index]) & FileAttributes.ReparsePoint) != 0) continue;
            if (incoming is not null) {
                using var evidence = new FileStream(files[index], FileMode.Open, FileAccess.Read, FileShare.Read);
                bool projectMatch = matchesVerifiedProject?.Invoke(files[index],token) == true;
                var ids = projectMatch || incoming is null ? null : Ids(readIds(files[index], token));
                if (projectMatch || ids is not null && incoming!.SetEquals(ids)) existing.Add((files[index], Hash(files[index], token)));
            }
            progress?.Invoke(index + 1, files.Length);
        }
        bool Conflicts(string path) => Directory.Exists(path) || (File.Exists(path) && !existing.Any(file => string.Equals(Full(file.Path), path, StringComparison.OrdinalIgnoreCase)));
        if (Conflicts(destination)) throw new IOException("目标存在同名文件，但无法确认是同一模组；已保留原文件，请选择其他文件名。");
        if (existing.Count > 0 && existing.All(file => EnabledPath(file.Path) != file.Path)) {
            string suffix = existing.All(file => file.Path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) ? ".old" : ".disabled";
            destination = EnabledPath(destination) + suffix;
        }
        if (Conflicts(destination))
            throw new IOException("目标存在同名文件，但无法确认是同一模组；已保留原文件，请选择其他文件名。");
        string stage = System.IO.Path.Combine(root, ".pcl-mod-install-" + Guid.NewGuid().ToString("N"));
        NoLinks(root);
        Directory.CreateDirectory(stage);
        string payload = System.IO.Path.Combine(stage, "incoming.jar");
        var moved = new List<(string Original, string Backup, string Hash)>();
        bool installed = false, keepStage = false;
        try {
            using (var input = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            if (Hash(payload, token) != incomingHash) throw new IOException("模组下载文件在安装期间发生变化。");
            for (int index = 0; index < existing.Count; index++) {
                token.ThrowIfCancellationRequested(); var file = existing[index]; NoLinks(file.Path);
                if (Hash(file.Path, token) != file.Hash) throw new IOException("已有模组在检查后发生变化，已停止替换。");
                string backup = System.IO.Path.Combine(stage, index + ".previous");
                File.Move(file.Path, backup); moved.Add((file.Path, backup, file.Hash));
                progress?.Invoke(index + 1, existing.Count);
            }
            token.ThrowIfCancellationRequested(); NoLinks(root);
            File.Move(payload, destination); installed = true;
            return new(destination, existing.Count, incoming is not null);
        } catch (Exception failure) {
            var errors = new List<Exception> { failure };
            if (installed && File.Exists(destination)) {
                if (Hash(destination, CancellationToken.None) == incomingHash) File.Delete(destination);
                else { keepStage = true; errors.Add(new IOException("新模组已被其他程序修改，未覆盖这些修改。")); }
            }
            foreach (var file in moved.AsEnumerable().Reverse()) {
                try { File.Move(file.Backup, file.Original); }
                catch (Exception rollback) { keepStage = true; errors.Add(rollback); }
            }
            if (keepStage) throw new AggregateException("恢复旧模组未完成，原文件备份保留在：" + stage, errors);
            throw;
        } finally {
            // Only this transaction's verified direct child is recursively cleaned.
            if (!keepStage && Directory.Exists(stage)) {
                try {
                    string full = Full(stage);
                    if (!string.Equals(System.IO.Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
                        !System.IO.Path.GetFileName(full).StartsWith(".pcl-mod-install-", StringComparison.Ordinal)) throw new IOException("无效的模组事务目录。");
                    NoLinks(full);
                    foreach (string file in Directory.GetFiles(full)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    Directory.Delete(full, true);
                } catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException) {
                    // A completed commit (or restored cancellation) is not turned into a
                    // misleading failure by an independent temporary-file cleanup error.
                    if(CleanupWarning is not null) CleanupWarning(cleanup);
                    else System.Diagnostics.Trace.TraceWarning("Mod install temporary cleanup: " + cleanup.Message);
                }
            }
        }
    }
}
