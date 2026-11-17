using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

public static partial class LegacyBaseOperations {
    // Ordinary settings and verified transactions must use the same file lock.
    // Atomic replacement alone cannot merge two concurrent read/modify/writes.
    private static readonly ConcurrentDictionary<string, object> iniFileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static string IniPath(string fileName) {
        // On Unix, Shared's ForApi maps Windows-shaped addresses to literal names
        // below the current directory. Adding a Unix prefix changes that mapping.
        if (Path.DirectorySeparatorChar != '\\' && (fileName.Contains(@":\") || fileName.StartsWith(@"\\", StringComparison.Ordinal))) return fileName;
        fileName = PathUtils.RemoveExtendedPrefix(fileName);
        return Path.GetFullPath(Path.IsPathRooted(fileName) || fileName.Contains(@":\") ? fileName : $@"{Paths.Base}PCL\{fileName}.ini");
    }
    private static object IniLock(string fileName) => iniFileLocks.GetOrAdd(fileName, _ => new object());
    internal static string ReadLegacyText(string fileName) {
#if NETFRAMEWORK
        return FileUtils.ReadAsString(fileName);
#else
        // Reuse Shared's BOM/strict-UTF8 detection with the same single IO read. Its only named
        // encoding lookup is GB18030; modern .NET requires the existing code-page provider there.
        return DecodeLegacyText(FileUtils.ReadAsBytes(fileName));
#endif
    }
    internal static string DecodeLegacyText(byte[] bytes) {
#if NETFRAMEWORK
        return bytes.GetString();
#else
        try { return bytes.GetString(); }
        catch (ArgumentException ex) when (ex.ParamName == "name") {
            Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding("GB18030") ?? Encoding.GetEncoding("GB18030");
            return encoding.GetString(bytes);
        }
#endif
    }
    public static void IniClearCache(string fileName, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        fileName = IniPath(fileName);
        lock (IniLock(fileName)) cache.Remove(fileName);
    }
    public static ConcurrentDictionary<string, string>? IniGetContent(string fileName, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        try {
            fileName = IniPath(fileName);
            if (MmcIniConfiguration.TryContent(fileName, cache, out var managedContent)) return managedContent;
            lock (IniLock(fileName)) {
                if (cache.TryGetValue(fileName, out ConcurrentDictionary<string, string>? content)) return content;
                if (!FileUtils.Exists(fileName)) return null;
                ConcurrentDictionary<string, string> ini = ParseIni(ReadLegacyText(fileName));
                cache[fileName] = ini;
                return ini;
            }
        } catch (Exception ex) { Logger.Error(ex, $"生成 ini 文件缓存失败（{fileName}）", LogBehavior.Toast); return null; }
    }
    public static string? ReadIni(string fileName, string key, string? defaultValue, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        ConcurrentDictionary<string, string>? content = IniGetContent(fileName, cache);
        if (content is null) return defaultValue;
        return content.GetOrDefault(key, defaultValue);
    }
    public static bool HasIniKey(string fileName, string key, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        ConcurrentDictionary<string, string>? content = IniGetContent(fileName, cache);
        return content is not null && content.ContainsKey(key);
    }
    public static void WriteIni(string fileName, string key, string? value, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        try {
            string normalized = IniPath(fileName);
            if (MmcIniConfiguration.QueueOrAdmit(normalized, key, value, cache, out var admission)) return;
            using (admission) UpdateIniValueCore(normalized, key, _ => value, cache);
        } catch (Exception ex) { Logger.Error(ex, $"写入文件失败（{fileName} → {key}:{value}）", LogBehavior.Toast); }
    }

    /// <summary>
    /// Updates one key from the latest disk snapshot and publishes cache state only
    /// after an atomic, read-back verified save. Throws so callers can retain their UI
    /// state on failure. The updater must not display UI while this file lock is held.
    /// </summary>
    public static string? UpdateIniValue(string fileName, string key, Func<string?, string?> update,
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        fileName = IniPath(fileName);
        using var admission = MmcIniConfiguration.AdmitSynchronousUpdate(fileName, key);
        return UpdateIniValueCore(fileName, key, update, cache);
    }
    private static string? UpdateIniValueCore(string fileName, string key, Func<string?, string?> update,
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache) {
        if (key.Contains(":")) throw new ArgumentException("ini 键名中不能包含冒号", nameof(key));
        key = key.ReplaceLineEndings("");
        fileName = IniPath(fileName);
        lock (IniLock(fileName)) {
            byte[]? before = FileUtils.Exists(fileName) ? FileUtils.ReadAsBytes(fileName) : null;
            string text = before is null ? "" : DecodeLegacyText(before);
            ConcurrentDictionary<string, string> content = ParseIni(text);
            content.TryGetValue(key, out string? previous);
            string? value = update(previous)?.ReplaceLineEndings("");
            if (string.Equals(previous, value, StringComparison.Ordinal)) {
                cache[fileName] = content;
                MmcIniConfiguration.PublishedOrdinary(fileName, text);
                return value;
            }
            string replacement = ReplaceIniValue(text, key, value);
            byte[] expected = new UTF8Encoding(false).GetBytes(replacement);
            bool replaced = false;
            try {
                // Keep the ordinary INI writer's existing IOException retry policy,
                // including transient sharing failures at the atomic replacement.
                ThrottleDebounce.Retrier.Attempt(delay: _ => TimeSpan.FromMilliseconds(200),
                    isRetryAllowed: error => error is IOException, action: _ => FileUtils.WriteAtomic(fileName, expected));
                replaced = true;
                if (!FileUtils.ReadAsBytes(fileName).SequenceEqual(expected)) throw new IOException("ini 写入后读回不一致");
                cache[fileName] = ParseIni(replacement);
                MmcIniConfiguration.PublishedOrdinary(fileName, replacement);
                return value;
            } catch {
                try {
                    // WriteAtomic preserves the old target on failure. If replacement
                    // finished, restore its exact bytes before reporting a failed save.
                    if (replaced || (FileUtils.Exists(fileName) && FileUtils.ReadAsBytes(fileName).SequenceEqual(expected))) {
                        if (before is null) FileUtils.Delete(fileName);
                        else FileUtils.WriteAtomic(fileName, before);
                    }
                } finally { cache.Remove(fileName); }
                throw;
            }
        }
    }
    internal static ConcurrentDictionary<string, string> ParseIni(string text) {
        ConcurrentDictionary<string, string> content = new();
        foreach (string line in text.SplitLines(true)) {
            int index = line.IndexOf(':');
            if (index > 0) content[line.Substring(0, index)] = line.Substring(index + 1);
        }
        return content;
    }
    internal static string ReplaceIniValue(string text, string key, string? value) {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        StringBuilder result = new();
        bool written = false;
        for (int i = 0; i < lines.Length; i++) {
            string line = lines[i];
            if (i == lines.Length - 1 && line.Length == 0) break;
            int index = line.IndexOf(':');
            if (index > 0 && string.Equals(line.Substring(0, index), key, StringComparison.Ordinal)) {
                if (!written && value is not null) result.Append(key).Append(':').Append(value).Append("\r\n");
                written = true;
            } else result.Append(line).Append("\r\n");
        }
        if (!written && value is not null) result.Append(key).Append(':').Append(value).Append("\r\n");
        return result.ToString();
    }
    public static bool CheckPermission(string? folder, Func<int> newUuid) {
        try {
            if (string.IsNullOrEmpty(folder)) return false;
            folder = PathUtils.AddSlashSuffix(folder!);
            if (folder.EndsWithF(@":\System Volume Information\", true) || folder.EndsWithF(@":\$RECYCLE.BIN\", true)) return false;
            if (!DirectoryUtils.Exists(folder)) return false;
            string file = $"{folder}CheckPermission{newUuid()}.txt";
            FileUtils.Write(file, "临时文件，用于检测该文件夹的权限是否正常。"); FileUtils.Delete(file); return true;
        } catch (Exception ex) { Logger.Warn(ex, $"没有对文件夹 {folder} 的权限，请尝试以管理员权限运行 PCL"); return false; }
    }
    public static void CheckPermissionWithException(string? folder, Func<int> newUuid) {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentNullException("文件夹名不能为空！");
        folder = PathUtils.AddSlashSuffix(folder!);
        if (folder.EndsWithF(@":\System Volume Information\", true) || folder.EndsWithF(@":\$RECYCLE.BIN\", true)) throw new UnauthorizedAccessException("没有对系统文件夹的权限！");
        if (!DirectoryUtils.Exists(folder)) throw new DirectoryNotFoundException("文件夹不存在！");
        string file = $"{folder}CheckPermission{newUuid()}.txt";
        FileUtils.Write(file, "临时文件，用于检测该文件夹的权限是否正常。"); FileUtils.Delete(file);
    }
    public static void ExtractResources(string filePath, string resourceName, Func<string, object?> readResource) {
        object? resource = readResource(resourceName);
        if (resource is byte[] bytes) {
            if (FileUtils.Exists(filePath) && CryptographyUtils.ComputeFileHash(filePath) == CryptographyUtils.ComputeHash(bytes)) return;
            Logger.Info($"将资源写入到文件：{resourceName} → {filePath}"); FileUtils.Write(filePath, bytes);
        } else if (resource is string content) {
            if (FileUtils.Exists(filePath) && Operators.CompareString(ReadLegacyText(filePath), content, false) == 0) return;
            Logger.Info($"将资源写入到文件：{resourceName} → {filePath}"); FileUtils.Write(filePath, content);
        } else throw new Exception($"资源 {resourceName} 的类型不支持：{resource!.GetType().Name}");
    }
}
