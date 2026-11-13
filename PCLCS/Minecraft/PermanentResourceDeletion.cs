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

/// <summary>Permanent deletion inside an explicit resource boundary. Reparse
/// points are removed as leaves; their targets are never enumerated.</summary>
public static class PermanentResourceDeletion {
    private sealed class Node { public string Path = ""; public bool Directory; }
    private static string Full(string path) {
        string full = Path.GetFullPath(path);
        return full.Length == Path.GetPathRoot(full)!.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static void Within(string root, string path) {
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("删除目标不在当前资源目录内。");
        string? current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current)) {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不通过链接目录删除资源。");
            string? parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
    public static void Delete(string root, IEnumerable<string> paths, CancellationToken token) {
        string boundary = Full(root);
        List<Node> plan = new(); HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string selected in paths.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase)) {
            Within(boundary, selected);
            Stack<(string Path, bool Expanded)> pending = new(); pending.Push((selected, false));
            while (pending.Count != 0) {
                token.ThrowIfCancellationRequested(); var next = pending.Pop(); Within(boundary, next.Path);
                if (next.Expanded) { plan.Add(new Node { Path = next.Path, Directory = true }); continue; }
                if (!visited.Add(next.Path)) continue;
                FileAttributes attributes = File.GetAttributes(next.Path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory && (attributes & FileAttributes.ReparsePoint) == 0) {
                    pending.Push((next.Path, true));
                    foreach (string child in Directory.EnumerateFileSystemEntries(next.Path)) pending.Push((Full(child), false));
                } else plan.Add(new Node { Path = next.Path, Directory = directory });
            }
        }
        // Finish preflight for the whole selection before deleting its first member.
        foreach (Node node in plan) {
            token.ThrowIfCancellationRequested(); Within(boundary, node.Path);
            FileAttributes attributes = File.GetAttributes(node.Path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if (directory != node.Directory) throw new IOException("资源类型已改变，请刷新后再删除。");
            if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(node.Path, attributes & ~FileAttributes.ReadOnly);
            if (directory) Directory.Delete(node.Path, false);
            else File.Delete(node.Path);
        }
    }
    public static bool CleanupLegacy(string minecraftFolder, CancellationToken token) {
        string root = Full(minecraftFolder), recycle = Path.Combine(root, "PCL", "ResourceRecycle");
        // The old LF cache has one exact location; no manifest can supply a path.
        Within(root, recycle);
        return CleanupLegacyDirectory(root, recycle, token);
    }

    /// <summary>Find every PCL/ResourceRecycle below an explicit shared-cache
    /// root, including ResourceRecycle directly inside a cache named PCL.</summary>
    public static int CleanupLegacyCache(string cacheFolder, CancellationToken token, Action<Exception, string>? warn = null) {
        string boundary = Full(cacheFolder);
        if (!Directory.Exists(boundary)) return 0;
        Within(boundary, Path.Combine(boundary, "boundary-check"));
        Stack<string> pending = new(); pending.Push(boundary);
        int removed = 0;
        while (pending.Count != 0) {
            token.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            try {
                if (!string.Equals(folder, boundary, StringComparison.OrdinalIgnoreCase)) Within(boundary, folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                if (string.Equals(Path.GetFileName(folder), "PCL", StringComparison.OrdinalIgnoreCase)) {
                    if (CleanupLegacyDirectory(boundary, Path.Combine(folder, "ResourceRecycle"), token)) removed++;
                }
            } catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                warn?.Invoke(error, folder);
                continue;
            }
            try {
                // Recheck before enumeration in case the directory changed
                // while an old recycle directory was being removed.
                if (!string.Equals(folder, boundary, StringComparison.OrdinalIgnoreCase)) Within(boundary, folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string child in Directory.GetDirectories(folder)) {
                    if (string.Equals(Path.GetFileName(folder), "PCL", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetFileName(child), "ResourceRecycle", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(child);
                }
            } catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warn?.Invoke(error, folder); }
        }
        return removed;
    }

    private static bool CleanupLegacyDirectory(string boundary, string recycle, CancellationToken token) {
        try { _ = File.GetAttributes(recycle); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        Delete(boundary, new[] { recycle }, token);
        return true;
    }
}
