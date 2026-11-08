// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;

namespace PCLCS;

/// <summary>本地资源的原始字段；WPF 图标留在调用方的派生类型。</summary>
public class InstanceResourceEntryData {
    public string? RecycleId;
    public string? Name;
    public string? FullPath;
    public string? Description;
    public bool IsDirectory;
    public DateTime ModifiedUtc;
    public DateTime CreatedUtc;
    // Read from level.dat during the background world scan, never inferred from filesystem times.
    public DateTime? LastPlayedUtc;
    public long Length;
}

public static class InstanceResourceEntryValidator {
    public static void ValidateEntry(string root, InstanceResourceEntryData entry, Action<string, string> ensureWithin) {
        ensureWithin(root, entry.FullPath!);
        if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
            throw new IOException("资源已被移动或删除，请刷新列表后重试。");
        // 目录内容变化不应令整个存档失去可操作性；目录只以创建时间识别替换。
        if (entry.IsDirectory) {
            if (Directory.GetCreationTimeUtc(entry.FullPath!) != entry.CreatedUtc)
                throw new IOException("资源已变化，请刷新列表后重试。");
        } else if (File.GetLastWriteTimeUtc(entry.FullPath!) != entry.ModifiedUtc || new FileInfo(entry.FullPath!).Length != entry.Length) {
            throw new IOException("资源已变化，请刷新列表后重试。");
        }
    }
}
