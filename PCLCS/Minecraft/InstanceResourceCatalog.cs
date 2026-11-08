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
using System.Threading;
using Newtonsoft.Json;

namespace PCLCS;

/// <summary>Page-owned world metadata/icon cache; file stamps are checked on each directory listing.</summary>
public sealed class InstanceResourceScanCache<TEntry> where TEntry : InstanceResourceEntryData {
    private readonly object gate = new();
    private readonly Dictionary<string, (string Stamp, TEntry Entry)> values = new(StringComparer.OrdinalIgnoreCase);
    public void Clear() { lock (gate) values.Clear(); }
    private static string FileStamp(string path) {
        var file = new FileInfo(path);
        return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.CreationTimeUtc.Ticks + ":" + (int)file.Attributes : "missing";
    }
    internal static string Stamp(FileSystemInfo directory) {
        // Windows enumeration can lag the directory timestamp after closing a new file.
        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不能缓存链接目录中的存档。");
        return directory.LastWriteTimeUtc.Ticks + ":" + directory.CreationTimeUtc.Ticks + ":" +
            FileStamp(Path.Combine(directory.FullName, "level.dat")) + ":" + FileStamp(Path.Combine(directory.FullName, "icon.png"));
    }
    internal bool TryGet(string path, string stamp, out TEntry? entry) {
        lock (gate) {
            if (values.TryGetValue(path, out var cached) && cached.Stamp == stamp) { entry = cached.Entry; return true; }
        }
        entry = null; return false;
    }
    internal void Store(string path, string stamp, TEntry entry) { lock (gate) values[path] = (stamp, entry); }
    internal void Prune(string folder, ISet<string> current) {
        lock (gate) foreach (string path in values.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase) && !current.Contains(path)).ToArray()) values.Remove(path);
    }
}

/// <summary>LF 本地资源扫描、包信息与图片文件读取；图片呈现由原宿主同步完成。</summary>
public static class InstanceResourceCatalog {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore:
    // Scan / ReadPackInfo / LoadImage. The factory preserves the caller's actual
    // derived entry and image callbacks update that same object before metadata.
    public static List<TEntry> Scan<TEntry>(string root, string folder, int kind, CancellationToken token,
            string downloadFolderName, Func<TEntry> factory, Action<TEntry, string, int> loadFileIcon,
            Action<TEntry, Stream, int> decodeStreamIcon, Func<string, string> normalizeLineEndings,
            Func<string, string> removeExtendedPrefix) where TEntry : InstanceResourceEntryData =>
        ScanCached(root, folder, kind, token, downloadFolderName, factory, loadFileIcon, decodeStreamIcon, normalizeLineEndings, removeExtendedPrefix, null);

    public static List<TEntry> ScanCached<TEntry>(string root, string folder, int kind, CancellationToken token,
            string downloadFolderName, Func<TEntry> factory, Action<TEntry, string, int> loadFileIcon,
            Action<TEntry, Stream, int> decodeStreamIcon, Func<string, string> normalizeLineEndings,
            Func<string, string> removeExtendedPrefix, InstanceResourceScanCache<TEntry>? cache) where TEntry : InstanceResourceEntryData {
        InstanceResourcePaths.EnsureWithin(root, folder, true, removeExtendedPrefix);
        var result = new List<TEntry>();
        if (!Directory.Exists(folder)) return result;
        FileSystemInfo[] children = new DirectoryInfo(folder).GetFileSystemInfos();
        int previewCount = 0;
        // Enumeration/sort keys are evaluated outside the per-child try, before
        // the original first cancellation check. Empty/missing folders stay lazy.
        foreach (FileSystemInfo child in children.OrderByDescending(item => item.LastWriteTimeUtc)) {
            token.ThrowIfCancellationRequested();
            try {
                if (InstanceResourcePathText.StartsWithPathText(child.Name, ".pcl-") ||
                        (child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory) {
                    if (kind == 0 && InstanceResourcePathText.SamePathText(child.Name, downloadFolderName) &&
                            !File.Exists(ChildPath(child.FullName, "level.dat"))) continue;
                    if (kind == 1) continue;
                } else if (!InstanceResourceFileRules.AcceptFile(kind, child.Name)) {
                    continue;
                }

                string? worldStamp = kind == 0 && isDirectory && cache is not null ? InstanceResourceScanCache<TEntry>.Stamp(child) : null;
                if (worldStamp is not null && cache!.TryGet(child.FullName, worldStamp, out TEntry? unchanged)) { result.Add(unchanged!); continue; }
                TEntry entry = factory();
                entry.Name = child.Name;
                entry.FullPath = child.FullName;
                entry.IsDirectory = isDirectory;
                entry.ModifiedUtc = child.LastWriteTimeUtc;
                entry.CreatedUtc = child.CreationTimeUtc;
                entry.Length = isDirectory ? 0 : ((FileInfo)child).Length;
                string detail = isDirectory ? "文件夹" : InstanceResourceDetailsRules.FormatSize(entry.Length);
                if (kind == 0) {
                    detail = File.Exists(ChildPath(child.FullName, "level.dat"))
                        ? "Minecraft 存档" : "未找到 level.dat，存档可能不完整";
                    if (isDirectory) entry.LastPlayedUtc = ReadLastPlayed(child.FullName, token, removeExtendedPrefix);
                    loadFileIcon(entry, ChildPath(child.FullName, "icon.png"), 64);
                } else if (kind == 2) {
                    ReadPackInfo(entry, ref detail, loadFileIcon, decodeStreamIcon, normalizeLineEndings);
                } else if (kind == 1 && previewCount < 80) {
                    loadFileIcon(entry, child.FullName, 64);
                    // This is the original attempt count, even when no image
                    // was returned. A failed callback does not advance it.
                    previewCount++;
                }
                entry.Description = kind == 0 ? detail + " · 最后游玩：" +
                    (entry.LastPlayedUtc.HasValue ? entry.LastPlayedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "未知") :
                    detail + " · " + child.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                result.Add(entry);
                if (worldStamp is not null) cache!.Store(child.FullName, worldStamp, entry);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                // Moving/unreadable individual children do not hide other entries.
            }
        }
        if (kind == 0 && cache is not null) cache.Prune(folder, new HashSet<string>(result.Select(entry => entry.FullPath!), StringComparer.OrdinalIgnoreCase));
        if (kind != 0 && kind != 1)
            return result.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, Comparer<string?>.Create(InstanceResourcePathText.ComparePathText)).ToList();
        return result;
    }

    private static DateTime? ReadLastPlayed(string folder, CancellationToken token, Func<string, string> removeExtendedPrefix) {
        token.ThrowIfCancellationRequested();
        DateTime? result;
        try {
            WorldMetadataInfo? data = WorldMetadataFiles.Read(folder, false,
                (parent, path) => EnsureWorldMetadataPath(parent, path, removeExtendedPrefix));
            result = data is not null && data.LastPlayedUtc != DateTime.MinValue ? data.LastPlayedUtc : null;
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException or OverflowException) {
            // A damaged, busy or unsupported level.dat does not hide the world.
            // Missing time remains unknown; directory mtime is not evidence of playing.
            result = null;
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static void EnsureWorldMetadataPath(string parent, string path, Func<string, string> removeExtendedPrefix) {
        if (Path.DirectorySeparatorChar == '\\') {
            InstanceResourcePaths.EnsureWithin(parent, path, false, removeExtendedPrefix);
            return;
        }
        // The legacy boundary above deliberately models Windows separators. Use
        // the host separator for this new read-only cross-platform metadata path.
        string directory = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.Ordinal))
            throw new IOException("存档元数据路径超出了当前目录。");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不能读取链接文件或链接目录中的存档元数据。");
    }

    public static void ReadPackInfo<TEntry>(TEntry entry, ref string detail,
            Action<TEntry, string, int> loadFileIcon, Action<TEntry, Stream, int> decodeStreamIcon,
            Func<string, string> normalizeLineEndings) where TEntry : InstanceResourceEntryData {
        try {
            string? metadata = null;
            if (entry.IsDirectory) {
                string fileName = ChildPath(entry.FullPath, "pack.mcmeta");
                if (File.Exists(fileName) && new FileInfo(fileName).Length <= 1024 * 1024)
                    metadata = File.ReadAllText(fileName);
                loadFileIcon(entry, ChildPath(entry.FullPath, "pack.png"), 64);
            } else {
                // Unlike ValidatePackage's OpenRead, the old metadata stream
                // permits concurrent writers and deletion while it is open.
                using (var stream = new FileStream(ReadablePath(entry.FullPath), FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false,
                        InstanceResourcePackageRules.EntryNameEncoding())) {
                    ZipArchiveEntry? json = archive.GetEntry("pack.mcmeta");
                    if (json is not null && json.Length <= 1024 * 1024) {
                        using (var reader = new StreamReader(json.Open())) {
                            metadata = reader.ReadToEnd();
                        }
                    }
                    ZipArchiveEntry? icon = archive.GetEntry("pack.png");
                    if (icon is not null && icon.Length <= 4 * 1024 * 1024) {
                        using (Stream input = icon.Open())
                        using (var buffer = new MemoryStream()) {
                            input.CopyTo(buffer);
                            buffer.Position = 0;
                            // Keep both archive and buffer alive during the
                            // synchronous decode; image assignment precedes JSON.
                            decodeStreamIcon(entry, buffer, 64);
                        }
                    }
                }
            }
            InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, normalizeLineEndings);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException or ArgumentException) {
            // Keep the original five-class filter. In particular, a ZIP decoder's
            // FormatException, InvalidDataException or cancellation still escapes.
        }
    }

    public static TImage? LoadImage<TImage>(string? fileName, int width, Func<Stream, int, TImage> decode)
            where TImage : class {
        try {
            if (!File.Exists(fileName) || new FileInfo(fileName!).Length > 64L * 1024 * 1024) return null;
            using (var stream = new FileStream(ReadablePath(fileName), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                return decode(stream, width);
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or FormatException) {
            return null;
        }
    }

    private static string ChildPath(string? folder, string name) {
        // Framework Combine checked the raw directory before any later IO.
        LegacyPathCharacters.Check(folder);
        return Path.Combine(folder!, name);
    }

    private static string ReadablePath(string? fileName) {
        // Same public FileStream raw-character/normalization boundary already
        // established for C007c4; preserve this method's distinct share flags.
        LegacyPathCharacters.Check(fileName);
#if !NETFRAMEWORK
        if (Path.DirectorySeparatorChar == '\\' && !string.IsNullOrEmpty(fileName))
            return InstanceResourcePathCompatibility.GetFullPath(fileName);
#endif
        return fileName!;
    }
}
