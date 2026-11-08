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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class PersonalOptionsPreview {
    public string VersionPath = "", FileName = "", ProfileHash = "", FileHash = "", Original = "", Merged = "";
    public bool HasFile, HasProfile;
    public JObject Current = new(), Saved = new();
    public HashSet<string> Changed = new(StringComparer.Ordinal);
}

/// <summary>Personal settings preview and explicit actions; shares the launch flow's store, gate and file guards.</summary>
public sealed class PersonalOptionsManagement {
    private const string ProfileName = "personal-options.json";
    private readonly LocalGameDataStore data;
    private readonly object gate;
    private readonly Func<string, bool> selected;
    private readonly Func<bool> running;
    private readonly Action<string> invalidate;
    public PersonalOptionsManagement(LocalGameDataStore data, object gate, Func<string, bool> selected,
        Func<bool> running, Action<string> invalidate) {
        this.data = data; this.gate = gate; this.selected = selected; this.running = running; this.invalidate = invalidate;
    }
    public JObject Profile() { lock (gate) return data.ReadJson(ProfileName); }
    private static string Fingerprint(JObject profile) => LocalGameDataStore.Hash(Encoding.UTF8.GetBytes(profile.ToString(Formatting.None)));
    public void EditValues(IEnumerable<PersonalOptionEdit> edits, IReadOnlyDictionary<string, JObject>? expectedConflicts = null) {
        if (running()) throw new IOException("请先关闭游戏，再编辑个人方案。");
        lock (gate) {
            JObject profile = Profile();
            if (profile["values"] is not null && profile["values"] is not JObject) throw new IOException("个人方案格式异常，请先备份并重新导入。");
            JObject values = profile["values"] as JObject ?? new JObject();
            var changes = edits.ToArray();
            if (changes.Select(edit => edit.Key).Distinct(StringComparer.Ordinal).Count() != changes.Length) throw new IOException("同一设置存在重复修改。");
            if (expectedConflicts is not null) foreach (var pair in expectedConflicts)
                if (!changes.Any(edit => edit.Key == pair.Key) || !JToken.DeepEquals((profile["conflicts"] as JObject)?[pair.Key], pair.Value))
                    throw new IOException("同步冲突已变化，请放弃修改并重新选择。");
            // Validate the complete batch before committing; another key may change independently.
            foreach (var edit in changes) {
                if ((string?)values[edit.Key] != edit.Expected) throw new IOException("个人方案中的该项已变化，请刷新后重新编辑。");
                values[edit.Key] = PersonalOptionsCatalog.ValidateValue(edit.Key, edit.Value);
            }
            if (changes.Length == 0) return;
            foreach (var edit in changes) (profile["conflicts"] as JObject)?.Remove(edit.Key);
            profile["version"] = 1;
            profile["values"] = values;
            profile["sourceKind"] = "custom";
            profile["updatedUtc"] = DateTime.UtcNow.ToString("O");
            data.WriteJson(ProfileName, profile);
        }
    }
    public void EditInstanceValues(IPersonalOptionsInstance instance, IEnumerable<PersonalOptionEdit> edits) {
        if (running()) throw new IOException("请先关闭游戏，再修改本版本设置。");
        lock (gate) {
            string file = Path.Combine(instance.GameFolder, "options.txt");
            byte[] bytes = PersonalOptionsFiles.Read(file) ?? throw new IOException("此版本尚未生成游戏设置，请先运行一次游戏并正常退出。");
            string text = PersonalOptionsRules.Decode(bytes);
            var current = PersonalOptionsRules.Parse(text);
            var changes = edits.ToArray();
            if (changes.Select(edit => edit.Key).Distinct(StringComparer.Ordinal).Count() != changes.Length) throw new IOException("同一设置存在重复修改。");
            var values = new JObject();
            foreach (var edit in changes) {
                if (!current.TryGetValue(edit.Key, out string? actual)) throw new IOException("此版本没有对应设置，已停止修改。");
                if (actual != edit.Expected) throw new IOException("本版本的该项设置已变化，请刷新后重新编辑。");
                if (edit.Key.StartsWith("key_", StringComparison.Ordinal) && !instance.VersionValid) throw new IOException("无法确定游戏版本，已停止修改按键。");
                values[edit.Key] = PersonalOptionsCatalog.ValidateValue(edit.Key, edit.Value);
            }
            var keys = new HashSet<string>(values.Properties().Select(pair => pair.Name), StringComparer.Ordinal);
            string merged = PersonalOptionsRules.MergeText(text, values, instance.VersionValid && instance.VanillaMajor >= 13, keys, _ => true,
                instance.VersionValid && instance.VanillaMajor >= 19);
            PersonalOptionsFiles.ApplyMerged(file, bytes, text, merged, invalidate);
        }
    }
    public PersonalOptionsPreview Preview(IPersonalOptionsInstance instance) {
        lock (gate) {
            JObject profile = Profile();
            var result = new PersonalOptionsPreview { VersionPath = instance.VersionPath,
                FileName = Path.Combine(instance.GameFolder, "options.txt"), ProfileHash = Fingerprint(profile),
                Saved = (JObject?)profile["values"]?.DeepClone() ?? new JObject() };
            result.HasProfile = result.Saved.Count > 0;
            byte[]? bytes = PersonalOptionsFiles.Read(result.FileName);
            if (bytes is null) return result;
            result.HasFile = true;
            result.FileHash = LocalGameDataStore.Hash(bytes);
            result.Original = PersonalOptionsRules.Decode(bytes);
            foreach (var pair in PersonalOptionsRules.Parse(result.Original)) result.Current[pair.Key] = pair.Value;
            var keys = new HashSet<string>(result.Saved.Properties().Where(p => selected(p.Name)).Select(p => p.Name), StringComparer.Ordinal);
            if (profile["conflicts"] is JObject pending) keys.RemoveWhere(key => pending[key] is not null);
            if (!instance.VersionValid) keys.RemoveWhere(k => k.StartsWith("key_", StringComparison.Ordinal));
            keys.RemoveWhere(key => result.Current[PersonalOptionsRules.CanonicalKey(key)] is null);
            result.Merged = PersonalOptionsRules.MergeText(result.Original, result.Saved,
                instance.VersionValid && instance.VanillaMajor >= 13, keys, selected, instance.VersionValid && instance.VanillaMajor >= 19);
            foreach (var pair in PersonalOptionsRules.Parse(result.Merged))
                if ((string?)result.Current[pair.Key] != pair.Value) result.Changed.Add(pair.Key);
            return result;
        }
    }
    public int Apply(IPersonalOptionsInstance instance, PersonalOptionsPreview preview) {
        if (running()) throw new IOException("请先关闭游戏，再应用个人设置。");
        if (!instance.SyncEnabled) throw new IOException("请先开启全局同步，并允许此版本跟随全局设置。");
        lock (gate) {
            if (instance.VersionPath != preview.VersionPath || Path.Combine(instance.GameFolder, "options.txt") != preview.FileName)
                throw new IOException("目标版本已变化，请重新预览。");
            if (Fingerprint(Profile()) != preview.ProfileHash) throw new IOException("个人方案已变化，请刷新预览后重试。");
            byte[]? bytes = PersonalOptionsFiles.Read(preview.FileName);
            if (!preview.HasFile || bytes is null) throw new IOException("此版本尚未生成游戏设置，请先运行游戏并正常退出，再应用。");
            if (LocalGameDataStore.Hash(bytes) != preview.FileHash) throw new IOException("游戏设置已变化，请刷新预览后重试。");
            // Recompute with live category/item selection rather than trusting a stale UI plan.
            PersonalOptionsPreview current = Preview(instance);
            if (current.Merged != preview.Merged) throw new IOException("同步项目已变化，请刷新预览后重试。");
            PersonalOptionsFiles.ApplyMerged(preview.FileName, bytes, preview.Original, preview.Merged, invalidate);
            return preview.Changed.Count;
        }
    }
    public void Resolve(string key, bool useGame, JObject expected) {
        if (running()) throw new IOException("请先关闭游戏，再处理同步冲突。");
        lock (gate) {
            JObject profile = Profile();
            var values = profile["values"] as JObject ?? throw new IOException("个人方案不存在。");
            var conflicts = profile["conflicts"] as JObject;
            if (conflicts?[key] is not JObject record || !JToken.DeepEquals(record, expected) ||
                !JToken.DeepEquals(values[key] ?? JValue.CreateNull(), record["profile"] ?? JValue.CreateNull()))
                throw new IOException("该项方案或冲突已变化，请刷新后重试。");
            if (useGame) values[key] = record["game"]!.DeepClone();
            conflicts.Remove(key);
            profile["updatedUtc"] = DateTime.UtcNow.ToString("O");
            data.WriteJson(ProfileName, profile);
        }
    }
}
