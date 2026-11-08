// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PCLCS;

/// <summary>
/// options.txt 的原始读取与合并写回。调用者管理同步锁、规则选择及会话生命周期。
/// </summary>
public static class PersonalOptionsFiles {
    public static bool IsLauncherInitialization(string text) {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = line.IndexOf(':');
            if (separator <= 0 || line.Substring(0, separator) is not ("lang" or "fullscreen")) return false;
        }
        return true;
    }
    public static void CreateInitial(string fileName, string text, Action<string> invalidateCache) {
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        string temporary = fileName + ".pcl-write-" + Guid.NewGuid().ToString("N");
        try {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
            // A file created by another writer wins; never replace it during initialization.
            File.Move(temporary, fileName);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        invalidateCache(fileName);
    }
    public static byte[]? Read(string fileName) {
        if (!File.Exists(fileName)) return null;
        if (new FileInfo(fileName).Length > 4 * 1024 * 1024 || (File.GetAttributes(fileName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不读取过大或链接形式的 options.txt。");
        return File.ReadAllBytes(fileName);
    }

    /// <summary>
    /// 文本变化时保持原 UTF-8 BOM 约定，按原字节哈希保护写入，并在提交成功后清理 INI 缓存。
    /// 缓存回调异常继续传播；已成功提交的文件与备份不会回滚。
    /// </summary>
    public static bool ApplyMerged(string fileName, byte[] originalBytes, string originalText, string mergedText, Action<string> invalidateCache) {
        if (string.Equals(originalText, mergedText, StringComparison.Ordinal)) return false;
        UTF8Encoding encoded = new(originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF);
        byte[] body = encoded.GetPreamble().Concat(encoded.GetBytes(mergedText)).ToArray();
        LocalGameDataStore.AtomicWrite(fileName, body, LocalGameDataStore.Hash(originalBytes), true);
        invalidateCache(fileName);
        return true;
    }
}
