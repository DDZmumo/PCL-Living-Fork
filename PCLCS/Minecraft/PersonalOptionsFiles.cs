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

namespace PCLCS;

/// <summary>
/// options.txt 的原始读取与合并写回。调用者管理同步锁、规则选择及会话生命周期。
/// </summary>
public static class PersonalOptionsFiles {
    public static string ApplyDefaultLanguage(string gameFolder, bool chinese, McVersion gameVersion, Action<string> invalidateCache,
        Func<int?> readDataVersion) {
        string path = Path.Combine(gameFolder, "options.txt");
        string template = Path.Combine(gameFolder, "config", "yosbr", "options.txt");
        bool usingTemplate = !File.Exists(path) && File.Exists(template);
        if (usingTemplate) path = template;
        byte[]? original = Read(path);
        string text = original is null ? "" : PersonalOptionsRules.Decode(original);
        var options = PersonalOptionsRules.Parse(text);
        options.TryGetValue("lang", out string? current);
        string language = usingTemplate || string.IsNullOrWhiteSpace(current) || current == "none" || !Directory.Exists(Path.Combine(gameFolder, "saves"))
            ? (chinese ? "zh_cn" : "en_us") : current!.ToLowerInvariant();
        if ((!gameVersion.Valid || gameVersion.Vanilla.Major <= 10) && language.Length >= 2)
            language = language.Substring(0, language.Length - 2) + language.Substring(language.Length - 2).ToUpperInvariant();
        string merged = string.Equals(current, language, StringComparison.Ordinal) ? text : LegacyBaseOperations.ReplaceIniValue(text, "lang", language);
        if (gameVersion.Valid && gameVersion.Vanilla.Major >= 13 && !usingTemplate) merged = MarkModernKeyFormat(merged, readDataVersion);
        if (original is null) CreateInitial(path, merged, invalidateCache);
        else ApplyMerged(path, original, text, merged, invalidateCache);
        return path;
    }
    // Minecraft's options fixes at 1344 and 1446 convert numeric keys and then introduce key.keyboard.*.
    internal static bool HasNumericBindings(Dictionary<string, string> options) =>
        options.Any(pair => pair.Key.StartsWith("key_", StringComparison.Ordinal) && int.TryParse(pair.Value.Split(':')[0], out _));

    internal static string MarkModernKeyFormat(string text, Func<int?>? readDataVersion) {
        var options = PersonalOptionsRules.Parse(text);
        if (options.ContainsKey("version") || HasNumericBindings(options) ||
            !options.Any(pair => pair.Key.StartsWith("key_", StringComparison.Ordinal) &&
                (pair.Value.StartsWith("key.keyboard.", StringComparison.Ordinal) || pair.Value.StartsWith("key.mouse.", StringComparison.Ordinal)))) return text;
        int? dataVersion = readDataVersion?.Invoke();
        int version = dataVersion is >= 1446 ? dataVersion.Value : 1446;
        if (options.TryGetValue("fov", out var fov) && double.TryParse(fov, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double degrees) && degrees is >= 30 and <= 110)
            text = LegacyBaseOperations.ReplaceIniValue(text, "fov", ((degrees - 70) / 40).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture));
        return "version:" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            (text.Contains("\r\n") ? "\r\n" : "\n") + text;
    }

    internal static bool GameReportedOptionsLoadFailure(string gameFolder, long startedUtcTicks) {
        if (startedUtcTicks <= 0) return false;
        string log = Path.Combine(gameFolder, "logs", "latest.log");
        try {
            if (!File.Exists(log) || File.GetLastWriteTimeUtc(log).Ticks < startedUtcTicks) return false;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while (stream.Position <= 4 * 1024 * 1024 && (line = reader.ReadLine()) is not null) {
                if (line.EndsWith("]: Failed to load options", StringComparison.Ordinal) &&
                    (line.Contains("[Render thread/ERROR]") || line.Contains("[Client thread/ERROR]"))) return true;
            }
        } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
    public static bool IsLauncherInitialization(string text) {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = line.IndexOf(':');
            if (separator <= 0 || line.Substring(0, separator) is not ("lang" or "fullscreen" or "version")) return false;
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
