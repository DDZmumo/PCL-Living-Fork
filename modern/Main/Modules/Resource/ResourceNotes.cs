// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeloongCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

/// <summary>
/// Personal notes on resource projects (issue #62): what the user remembers about a mod, its compatibility quirks and so
/// on. Keyed by platform project, like favorites, but stored on their own so a note survives un-favoriting and the
/// favorites file keeps its format. Shown on the project's detail page and under its row on the favorites page.
/// </summary>
internal static class ResourceNotes
{
    internal const int MaxLength = 2000;
    private const int MaxCount = 5000;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Paths.Base, "PCL", "ResourceNotes.json");
    private static Dictionary<string, string>? _notes;

    /// <summary>Raised on the UI thread after a note was saved or removed.</summary>
    internal static event EventHandler? Changed;

    internal static bool CanNote(ResourceProject? project) =>
        project is not null && ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out _);

    /// <summary>The note on a project, or null when there is none.</summary>
    internal static string? Get(ResourceProject? project)
    {
        if (!TryKey(project, out string key)) return null;
        lock (Gate) return Load().TryGetValue(key, out string? note) ? note : null;
    }

    internal static bool HasAny(IEnumerable<ResourceProject> projects)
    {
        lock (Gate)
        {
            var notes = Load();
            return notes.Count > 0 && projects.Any(project => TryKey(project, out string key) && notes.ContainsKey(key));
        }
    }

    /// <summary>Saves the note (blank removes it). False, with a hint already shown, when it could not be written.</summary>
    internal static bool Set(ResourceProject project, string? text)
    {
        if (!TryKey(project, out string key)) return false;
        string note = (text ?? "").Replace("\r\n", "\n").Trim();
        if (note.Length > MaxLength) note = note[..MaxLength];
        try
        {
            lock (Gate)
            {
                // Start from the file as it is now, so another launcher window's notes are not lost.
                var current = new Dictionary<string, string>(Read(), StringComparer.Ordinal);
                if (note.Length == 0) current.Remove(key);
                else
                {
                    if (!current.ContainsKey(key) && current.Count >= MaxCount) throw new InvalidDataException("备注数量已达上限。");
                    current[key] = note;
                }
                string serialized = new JObject {
                    ["Schema"] = 1,
                    ["Notes"] = new JObject(current.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new JProperty(pair.Key, pair.Value)))
                }.ToString(Formatting.Indented);
                if (Encoding.UTF8.GetByteCount(serialized) > MaxBytes) throw new InvalidDataException("备注数据已达上限。");
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicWrite(FilePath, serialized);
                _notes = current;
            }
        }
        catch (Exception error)
        {
            Logger.Warn(error, "保存资源备注失败，已保留原备注文件", LogBehavior.None);
            ModMain.Hint("备注保存失败：" + error.Message, ModMain.HintType.Red, false);
            return false;
        }
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>A note as it is shown everywhere: wrapped in Chinese quotation marks.</summary>
    internal static string Quote(string note) => "“" + note + "”";

    private static bool TryKey(ResourceProject? project, out string key)
    {
        key = "";
        if (project is null || !ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out var identity)) return false;
        key = identity.ToString();
        return true;
    }

    private static Dictionary<string, string> Load()
    {
        if (_notes is not null) return _notes;
        try { _notes = Read(); }
        catch (Exception error)
        {
            // Keep the file untouched; notes are simply not shown until it can be read again.
            Logger.Warn(error, "读取资源备注失败，已保留原备注文件", LogBehavior.None);
            _notes = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return _notes;
    }

    private static Dictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(FilePath)) return result;
        if (new FileInfo(FilePath).Length > MaxBytes) throw new InvalidDataException("备注文件过大。");
        JObject root = JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
        if (root["Schema"]?.Type != JTokenType.Integer || (int)root["Schema"]! != 1 || root["Notes"] is not JObject notes)
            throw new InvalidDataException("无法识别备注文件格式。");
        foreach (var property in notes.Properties())
            if (property.Value.Type == JTokenType.String && (string?)property.Value is { Length: > 0 } text)
                result[property.Name] = text.Length > MaxLength ? text[..MaxLength] : text;
        return result;
    }

    private static void AtomicWrite(string path, string serialized)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(serialized);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
