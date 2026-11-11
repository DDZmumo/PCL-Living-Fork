// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>LF 本地资源的导入、导出和直接删除。</summary>
public sealed class InstanceResourceTransactions {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore:
    // DeleteStage, Rename, import/export and direct deletion. Existing path/rule adapters keep the
    // original LF host's PathUtils.RemoveExtendedPrefix dependency one-way.
    private readonly Func<string, string> removeExtendedPrefix;

    public InstanceResourceTransactions(Func<string, string> removeExtendedPrefix) {
        this.removeExtendedPrefix = removeExtendedPrefix;
    }

    private string Normalize(string value) => InstanceResourcePaths.Normalize(value, removeExtendedPrefix);
    private void EnsureWithin(string root, string fileName, bool allowRoot = false) =>
        InstanceResourcePaths.EnsureWithin(root, fileName, allowRoot, removeExtendedPrefix);
    private static string SafeName(string value) => InstanceResourceNameRules.SafeName(value);
    private void ValidateEntry(string root, InstanceResourceEntryData entry) =>
        InstanceResourceEntryValidator.ValidateEntry(root, entry, (parent, fileName) => EnsureWithin(parent, fileName));

    public void DeleteStage(string root, string stage) {
        if (!Directory.Exists(stage)) return;
        EnsureWithin(root, stage);
        if (!Regex.IsMatch(Path.GetFileName(stage), @"\A\.pcl-import-[0-9a-f]{32}\z"))
            throw new IOException("无效的导入临时目录。");
        Directory.Delete(stage, true);
    }

    public string Rename(string root, InstanceResourceEntryData entry, string newName) {
        ValidateEntry(root, entry);
        string destination = Path.Combine(Path.GetDirectoryName(entry.FullPath!)!, SafeName(newName));
        EnsureWithin(root, destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("已存在同名资源，未覆盖原文件。");
        if (entry.IsDirectory) Directory.Move(entry.FullPath!, destination);
        else File.Move(entry.FullPath!, destination);
        return destination;
    }

    private static void CopyStream(Stream input, Stream output, CancellationToken token) {
        byte[] buffer = new byte[65536];
        while (true) {
            token.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
    }

    private static IEnumerable<string> TreeFiles(string folder, CancellationToken token) {
        Stack<string> pending = new();
        pending.Push(folder);
        while (pending.Count > 0) {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("资源中包含目录链接，请先通过资源管理器处理。");
            foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos()) {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("资源中包含链接文件，请先通过资源管理器处理。");
                if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(entry.FullName);
                else yield return entry.FullName;
            }
        }
    }

    private static void AtomicCopy(string source, string destination, bool replace, CancellationToken token) {
        string temporary = destination + ".pcl-copy-" + Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                CopyStream(input, output, token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (replace && File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string ImportFile(string root, string folder, int kind, string source, CancellationToken token) {
        if (kind == 0 || !InstanceResourceFileRules.AcceptFile(kind, source))
            throw new IOException("文件格式不受此资源页面支持。");
        EnsureWithin(root, folder, true);
        string destination = Path.Combine(folder, SafeName(Path.GetFileName(source)));
        EnsureWithin(root, destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("已存在同名资源，未覆盖原文件。");
        AtomicCopy(source, destination, false, token);
        return destination;
    }

    public string ImportWorld(string root, string source, string name, CancellationToken token) {
        string destination = Path.Combine(root, SafeName(name));
        EnsureWithin(root, destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("已存在同名存档，未覆盖原存档。");
        Directory.CreateDirectory(root);
        string stage = Path.Combine(root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            // Same original OpenRead/Share.Read lifetime; reuse only the already
            // verified modern Windows path and non-EFS filename decoding adapter.
            using (ZipArchive archive = InstanceResourcePackageRules.OpenRead(source)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    token.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace("/", "\\");
                    if (Path.IsPathRooted(relative) || relative.Split('\\').Any(part => part == ".." || part.Contains(":")) ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new IOException("存档压缩包包含越界路径或链接。");
                    if (relative == "") continue;
                    string target = InstanceResourcePathCompatibility.GetFullPath(Path.Combine(stage, relative));
                    EnsureWithin(stage, target);
                    if (relative.EndsWith("\\")) Directory.CreateDirectory(target);
                    else {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (Stream input = entry.Open())
                        using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            CopyStream(input, output, token);
                    }
                }
            }
            string? world = null;
            string?[] candidates = Directory.EnumerateFiles(stage, "level.dat", SearchOption.AllDirectories)
                .Select(fileName => Path.GetDirectoryName(fileName)).ToArray();
            if (candidates.Length == 1) world = candidates[0];
            if (world is null)
                throw new IOException("压缩包中没有找到单个 Minecraft 存档（level.dat）。");
            token.ThrowIfCancellationRequested();
            Directory.Move(world, destination);
            return destination;
        } finally {
            DeleteStage(root, stage);
        }
    }

    public string ImportFolder(string root, string folder, int kind, string source, string name, CancellationToken token) {
        EnsureWithin(root, folder, true);
        string destination = Path.Combine(folder, SafeName(name));
        EnsureWithin(root, destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("已存在同名资源，未覆盖原文件夹。");
        if (InstanceResourcePathText.SamePathText(Normalize(folder), Normalize(source)) ||
            InstanceResourcePathText.StartsWithPathText(Normalize(folder), Normalize(source) + "\\"))
            throw new IOException("不能把文件夹导入到它自身内部。");
        switch (kind) {
            case 0:
                if (!File.Exists(Path.Combine(source, "level.dat")))
                    throw new IOException("该文件夹中没有找到 Minecraft 存档 level.dat。");
                break;
            case 2:
                if (!File.Exists(Path.Combine(source, "pack.mcmeta")))
                    throw new IOException("该文件夹中没有找到资源包 pack.mcmeta。");
                break;
            case 3:
                if (!Directory.Exists(Path.Combine(source, "shaders")))
                    throw new IOException("该文件夹中没有找到光影包 shaders 目录。");
                break;
            case 1:
                throw new IOException("请使用从文件导入来添加截图。");
        }
        Directory.CreateDirectory(folder);
        string stage = Path.Combine(folder, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            string prefix = Normalize(source) + "\\";
            foreach (string fileName in TreeFiles(source, token)) {
                string target = Path.Combine(stage, fileName.Substring(prefix.Length));
                EnsureWithin(stage, target);
                AtomicCopy(fileName, target, false, token);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return destination;
        } finally {
            DeleteStage(root, stage);
        }
    }

    public void Export(string root, InstanceResourceEntryData entry, string destination, CancellationToken token) =>
        ExportCore(root, entry, destination, token, true);

    private void ExportCore(string root, InstanceResourceEntryData entry, string destination, CancellationToken token, bool overwrite) {
        ValidateEntry(root, entry);
        if (InstanceResourcePathText.SamePathText(Normalize(entry.FullPath!), Normalize(destination)))
            throw new IOException("导出位置与原文件相同。");
        if (!entry.IsDirectory) {
            AtomicCopy(entry.FullPath!, destination, overwrite, token);
            return;
        }
        if (InstanceResourcePathText.StartsWithPathText(Normalize(destination), Normalize(entry.FullPath!) + "\\"))
            throw new IOException("请将压缩包保存到待导出的文件夹之外。");
        string temporary = destination + ".pcl-export-" + Guid.NewGuid().ToString("N");
        try {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(output, ZipArchiveMode.Create)) {
                string prefix = Normalize(entry.FullPath!) + "\\";
                foreach (string fileName in TreeFiles(entry.FullPath!, token)) {
                    EnsureWithin(entry.FullPath!, fileName);
                    token.ThrowIfCancellationRequested();
                    ZipArchiveEntry zipEntry = archive.CreateEntry(fileName.Substring(prefix.Length).Replace("\\", "/"), CompressionLevel.Fastest);
                    using (FileStream input = new(fileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (Stream target = zipEntry.Open())
                        CopyStream(input, target, token);
                }
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination) && overwrite) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void ExportMany<TEntry>(string root, IEnumerable<TEntry> items, string folder, CancellationToken token) where TEntry : InstanceResourceEntryData {
        List<TEntry> list = items.ToList();
        // Preserve the original Framework comparer in the current product. Its
        // modern Unicode hash domain is a separate N002 validation boundary.
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (TEntry entry in list) {
            ValidateEntry(root, entry);
            string destination = Path.Combine(folder, entry.IsDirectory ? entry.Name + ".zip" : entry.Name!);
            if (!destinations.Add(destination) || File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("导出目标存在同名资源，请选择其他文件夹。");
            if (entry.IsDirectory && InstanceResourcePathText.StartsWithPathText(Normalize(destination), Normalize(entry.FullPath!) + "\\"))
                throw new IOException("不能导出到所选资源的文件夹内。");
        }
        foreach (TEntry entry in list) {
            token.ThrowIfCancellationRequested();
            ExportCore(root, entry, Path.Combine(folder, entry.IsDirectory ? entry.Name + ".zip" : entry.Name!), token, false);
        }
    }

    public void DeleteMany<TEntry>(string root, IEnumerable<TEntry> items, CancellationToken token) where TEntry : InstanceResourceEntryData {
        List<TEntry> list = items.ToList();
        foreach (TEntry entry in list) ValidateEntry(root, entry);
        PermanentResourceDeletion.Delete(root, list.Select(entry => entry.FullPath!), token);
    }

}
