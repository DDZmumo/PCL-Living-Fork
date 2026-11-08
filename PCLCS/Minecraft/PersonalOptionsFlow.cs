// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>按原访问时机获取实例和设置，不提前拍摄版本或开关快照。</summary>
public interface IPersonalOptionsInstance {
    string VersionPath { get; }
    string GameFolder { get; }
    bool SyncEnabled { get; }
    bool VersionValid { get; }
    int VanillaMajor { get; }
}

/// <summary>原个人选项会话字段；允许旧程序集的门面继承并保持对象身份。</summary>
public class PersonalOptionsSessionData {
    public string Id = Guid.NewGuid().ToString("N");
    public string? VersionPath;
    public string? GameFolder;
    public JObject? Baseline;
    public HashSet<string> Keys = new(StringComparer.Ordinal);
    public bool Tracked;
}

/// <summary>
/// LF 个人设置导入、启动合并、退出捕获及遗留会话恢复。
/// 复用调用者的 Monitor 锁与资料存储，不管理 WPF、全局设置或进程生命周期。
/// </summary>
public sealed class PersonalOptionsFlow {
    private const string ProfileName = "personal-options.json";
    private readonly LocalGameDataStore _data;
    private readonly object _gate;
    private readonly Func<bool> _hasRunning;
    private readonly Func<string, IPersonalOptionsInstance> _instanceFactory;
    private readonly Func<string, bool> _selected;
    private readonly Action<string> _invalidateCache;
    private readonly Func<string, string, bool> _samePath;
    private readonly Action<Exception, string> _warn;
    private readonly Func<PersonalOptionsSessionData> _sessionFactory;
    private readonly Func<bool> _captureEnabled;
    private readonly bool _existingOnly;

    public PersonalOptionsFlow(LocalGameDataStore data, object gate, Func<bool> hasRunning,
        Func<string, IPersonalOptionsInstance> instanceFactory, Func<string, bool> selected,
        Action<string> invalidateCache, Func<string, string, bool> samePath,
        Action<Exception, string> warn, Func<PersonalOptionsSessionData> sessionFactory, Func<bool>? captureEnabled = null, bool existingOnly = false) {
        _data = data;
        _gate = gate;
        _hasRunning = hasRunning;
        _instanceFactory = instanceFactory;
        _selected = selected;
        _invalidateCache = invalidateCache;
        _samePath = samePath;
        _warn = warn;
        _sessionFactory = sessionFactory;
        _captureEnabled = captureEnabled ?? (() => true);
        _existingOnly = existingOnly;
    }

    public bool Enabled(IPersonalOptionsInstance? instance) => instance is not null && instance.SyncEnabled;
    public bool Modern(IPersonalOptionsInstance instance) => instance.VersionValid && instance.VanillaMajor >= 13;

    public bool HasProfile() {
        lock (_gate) {
            JObject? values = _data.ReadJson(ProfileName)["values"] as JObject;
            return values is not null && values.Count > 0;
        }
    }

    public void ImportFrom(IPersonalOptionsInstance? instance, Func<string, bool>? importSelected = null, string? importSourceName = null) {
        if (instance is null) throw new IOException("请先选择一个已经运行过的游戏版本。");
        if (_hasRunning()) throw new IOException("请先关闭游戏，再导入个人设置。");
        byte[]? bytes = PersonalOptionsFiles.Read(Path.Combine(instance.GameFolder, "options.txt"));
        if (bytes is null) throw new IOException("该版本尚未生成 options.txt，请先启动一次游戏并保存设置。");
        JObject values = new();
        foreach (KeyValuePair<string, string> pair in PersonalOptionsRules.Parse(PersonalOptionsRules.Decode(bytes)).Where(item => (importSelected ?? _selected)(item.Key))) {
            string? value = CaptureValue(pair.Key, pair.Value);
            if (value is not null) values[pair.Key] = value;
        }
        if (values.Count == 0) throw new IOException("该版本没有可导入的个人设置。");
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject combined = profile["values"] as JObject ?? new JObject();
            foreach (JProperty pair in values.Properties()) combined[pair.Name] = pair.Value.DeepClone();
            profile["version"] = 1;
            profile["values"] = combined;
            profile["updatedUtc"] = DateTime.UtcNow.ToString("O");
            if (importSourceName is not null) {
                profile["sourceKind"] = "version";
                profile["importSource"] = new JObject(
                new JProperty("name", importSourceName), new JProperty("versionPath", instance.VersionPath),
                new JProperty("importedUtc", profile["updatedUtc"]!.DeepClone()));
            }
            if (profile["conflicts"] is JObject conflicts)
                foreach (JProperty pair in values.Properties()) conflicts.Remove(pair.Name);
            _data.WriteJson(ProfileName, profile);
        }
    }

    public PersonalOptionsSessionData? Prepare(IPersonalOptionsInstance? instance) {
        if (!Enabled(instance) || _hasRunning()) return null;
        RecoverExitedSessions();
        string folder = instance!.GameFolder;
        string fileName = Path.Combine(folder, "options.txt");
        if (HasActiveSession(folder)) return null;
        byte[]? bytes = PersonalOptionsFiles.Read(fileName);
        if (bytes is null && !_existingOnly) return null;
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject? values = profile["values"] as JObject;
            if (values is null || values.Count == 0) return null;
            HashSet<string> keys = new(values.Properties().Where(pair => _selected(pair.Name)).Select(pair => pair.Name), StringComparer.Ordinal);
            if (profile["conflicts"] is JObject pending) keys.RemoveWhere(key => pending[key] is not null);
            if (!instance.VersionValid) keys.RemoveWhere(key => key.StartsWith("key_", StringComparison.Ordinal));
            string initialText = bytes is null ? "" : PersonalOptionsRules.Decode(bytes);
            bool initializing = _existingOnly && PersonalOptionsFiles.IsLauncherInitialization(initialText);
            if (initializing && (!instance.VersionValid || instance.VanillaMajor < 7)) return null;
            string text = initialText;
            if (initializing) {
                keys.RemoveWhere(key => !PersonalOptionsCatalog.Contains(key) || (key == "key_key.fullscreen" && instance.VanillaMajor < 8));
                if (keys.Count == 0) return null;
                // Preserve modpack-provided defaults on the first launch.
                byte[]? template = PersonalOptionsFiles.Read(Path.Combine(folder, "config", "yosbr", "options.txt"));
                if (template is not null) {
                    string defaults = PersonalOptionsRules.Decode(template);
                    text = defaults + (defaults.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n") + text;
                }
                JObject valid = new();
                foreach (string key in keys.ToArray()) {
                    try { valid[key] = PersonalOptionsCatalog.ValidateValue(key, values[key]!.ToString()); }
                    catch (IOException) { keys.Remove(key); }
                }
                if (keys.Count == 0) return null;
                values = valid;
            } else if (_existingOnly) {
                var present = PersonalOptionsRules.Parse(text);
                keys.RemoveWhere(key => !present.ContainsKey(PersonalOptionsRules.CanonicalKey(key)));
            }
            string merged = initializing
                ? PersonalOptionsRules.MergeInitialText(text, values, Modern(instance), keys, _selected, instance.VanillaMajor >= 19)
                : PersonalOptionsRules.MergeText(text, values, Modern(instance), keys, _selected, instance.VersionValid && instance.VanillaMajor >= 19);
            if (bytes is null) PersonalOptionsFiles.CreateInitial(fileName, merged, _invalidateCache);
            else PersonalOptionsFiles.ApplyMerged(fileName, bytes, initialText, merged, _invalidateCache);
            JObject baseline = new();
            foreach (KeyValuePair<string, string> pair in PersonalOptionsRules.Parse(merged).Where(item => keys.Contains(item.Key))) {
                string? value = CaptureValue(pair.Key, pair.Value);
                if (value is not null) baseline[pair.Key] = value;
            }
            PersonalOptionsSessionData session = _sessionFactory();
            session.VersionPath = instance.VersionPath;
            session.GameFolder = folder;
            session.Baseline = baseline;
            session.Keys = keys;
            return session;
        }
    }

    public void Track(PersonalOptionsSessionData? session, Process game) {
        if (session is null) return;
        lock (_gate) {
            _data.WriteJson("options-sessions/" + session.Id + ".json", new JObject(
                new JProperty("versionPath", session.VersionPath), new JProperty("gameFolder", session.GameFolder),
                new JProperty("pid", game.Id), new JProperty("started", game.StartTime.ToUniversalTime().Ticks),
                new JProperty("baseline", session.Baseline), new JProperty("keys", new JArray(session.Keys))));
            session.Tracked = true;
        }
    }

    public void CaptureEditedBinding(IPersonalOptionsInstance? instance, string key, string value) {
        if (!Enabled(instance) || !_selected(key)) return;
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject? values = profile["values"] as JObject;
            if (values is null) return;
            string? canonical = CaptureValue(key, value);
            if (canonical is null) return;
            values[PersonalOptionsRules.CanonicalKey(key)] = canonical;
            _data.WriteJson(ProfileName, profile);
        }
    }

    public void Finish(PersonalOptionsSessionData? session) {
        if (session is null) return;
        lock (_gate) {
            string journal = _data.Address("options-sessions/" + session.Id + ".json");
            if (session.Tracked && !File.Exists(journal)) return;
            Capture(session);
            if (File.Exists(journal)) File.Delete(journal);
        }
    }

    public void Capture(PersonalOptionsSessionData session) {
        if (!_captureEnabled()) return;
        IPersonalOptionsInstance instance = _instanceFactory(session.VersionPath!);
        if (!Enabled(instance)) return;
        byte[]? bytes = PersonalOptionsFiles.Read(Path.Combine(session.GameFolder!, "options.txt"));
        if (bytes is null) return;
        Dictionary<string, string> current = PersonalOptionsRules.Parse(PersonalOptionsRules.Decode(bytes));
        lock (_gate) {
            JObject profile = _data.ReadJson(ProfileName);
            JObject? values = profile["values"] as JObject;
            if (values is null) return;
            foreach (string key in session.Keys.Where(_selected)) {
                if (!current.TryGetValue(key, out string? raw)) continue;
                string? value = CaptureValue(key, raw);
                if (value is null || session.Baseline![key] is null || value == session.Baseline[key]!.ToString()) continue;
                string? latest = (string?)values[key];
                if (latest != (string?)session.Baseline[key] && latest != value) {
                    JObject conflicts = profile["conflicts"] as JObject ?? new JObject();
                    conflicts[key] = new JObject(new JProperty("baseline", session.Baseline[key]!.DeepClone()),
                        new JProperty("profile", latest), new JProperty("game", value),
                        new JProperty("versionPath", session.VersionPath), new JProperty("utc", DateTime.UtcNow.ToString("O")));
                    profile["conflicts"] = conflicts;
                } else {
                    values[key] = value;
                    (profile["conflicts"] as JObject)?.Remove(key);
                }
            }
            profile["updatedUtc"] = DateTime.UtcNow.ToString("O");
            _data.WriteJson(ProfileName, profile);
        }
    }

    public void RecoverExitedSessions() {
        string folder = _data.Address("options-sessions");
        if (!Directory.Exists(folder)) return;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                try {
                    JObject json = _data.ReadJson("options-sessions/" + Path.GetFileName(fileName));
                    if (LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) continue;
                    PersonalOptionsSessionData session = _sessionFactory();
                    session.Id = Path.GetFileNameWithoutExtension(fileName);
                    session.VersionPath = json["versionPath"]!.ToString();
                    session.GameFolder = json["gameFolder"]!.ToString();
                    session.Baseline = (JObject?) json["baseline"];
                    session.Keys = new HashSet<string>(json["keys"]!.Select(item => item.ToString()), StringComparer.Ordinal);
                    session.Tracked = true;
                    Finish(session);
                } catch (Exception error) {
                    _warn(error, "恢复个人游戏设置同步会话失败");
                }
            }
        }
    }

    public bool HasActiveSession(string gameFolder) {
        string folder = _data.Address("options-sessions");
        if (!Directory.Exists(folder)) return false;
        lock (_gate) {
            foreach (string fileName in Directory.EnumerateFiles(folder, "*.json")) {
                JObject json = _data.ReadJson("options-sessions/" + Path.GetFileName(fileName));
                if (_samePath(json["gameFolder"]!.ToString(), gameFolder) && LocalGameDataStore.ProcessMatches(json["pid"]!.ToObject<int>(), json["started"]!.ToObject<long>())) return true;
            }
        }
        return false;
    }
    private static string? CaptureValue(string key, string value) {
        if (value.Length > 4096 || value.IndexOfAny(['\0', '\n', '\r']) >= 0) return null;
        return key.StartsWith("key_", StringComparison.Ordinal) ? PersonalOptionsRules.NormalizeBinding(value) : PersonalOptionsRules.CanonicalValue(key, value);
    }
}
