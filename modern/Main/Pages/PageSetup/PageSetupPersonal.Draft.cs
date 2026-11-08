// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

public partial class PageSetupPersonal {
    private readonly Dictionary<string, PersonalOptionEdit> stagedValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (object Original, object Value)> stagedSettings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JObject> stagedConflictChoices = new(StringComparer.Ordinal);
    private JObject localValues = new();
    private ModMinecraft.McInstance draftOwner;
    private bool HasDraft => stagedValues.Count > 0 || stagedSettings.Count > 0;
    private JObject EditorValues => IsInstanceContext ? localValues : globalValues;
    private bool CanEditLocal => IsInstanceContext && ComboPolicy.SelectedIndex == 1 && Target is not null &&
        !ModWatcher.HasRunningMinecraft && !ModMinecraft.McInstanceLock.IsLocked(Target);

    private T PageSetting<T>(string key) => !IsInstanceContext && stagedSettings.TryGetValue(key, out var edit)
        ? (T)edit.Value : Settings.Get<T>(key);
    private HashSet<string> PageExcluded() => new(JArray.Parse(PageSetting<string>("LaunchPersonalOptionsExcluded")).Values<string>(), StringComparer.Ordinal);
    private static object ReadGlobalSetting(string key) => key == "LaunchPersonalOptionsExcluded" ? Settings.Get<string>(key) : Settings.Get<bool>(key);
    private void StageSetting(string key, object value) {
        object original = stagedSettings.TryGetValue(key, out var edit) ? edit.Original : ReadGlobalSetting(key);
        if (Equals(original, value)) stagedSettings.Remove(key);
        else stagedSettings[key] = (original, value);
    }
    private void SynchronizeDraftOwner() {
        if (!IsInstanceContext || ReferenceEquals(draftOwner, Target)) return;
        CancelEditorState(); stagedValues.Clear(); draftOwner = Target;
    }
    private void OverlayDraft() {
        foreach (var edit in stagedValues.Values) EditorValues[edit.Key] = edit.Value;
    }
    private void StageEditorValues(IEnumerable<PersonalOptionEdit> edits) {
        var changes = edits.Select(edit => edit with { Value = PersonalOptionsCatalog.ValidateValue(edit.Key, edit.Value) }).ToArray();
        foreach (var edit in changes)
            if ((string)EditorValues[edit.Key] != edit.Expected) throw new InvalidOperationException("该项设置已变化，请重新编辑。");
        foreach (var edit in changes) {
            string original = stagedValues.TryGetValue(edit.Key, out var previous) ? previous.Expected : edit.Expected;
            if (edit.Value == original) stagedValues.Remove(edit.Key);
            else stagedValues[edit.Key] = edit with { Expected = original };
            EditorValues[edit.Key] = edit.Value;
        }
    }
    internal void SaveGlobalSettings() {
        if (IsInstanceContext) throw new InvalidOperationException("请在全局设置页保存全局方案。");
        FlushPendingEdits(false);
        if (!HasDraft) return;
        if (ModWatcher.HasRunningMinecraft) throw new InvalidOperationException("请先关闭游戏，再保存全局设置。");
        lock (LocalGameData.Gate) {
            foreach (var pair in stagedSettings)
                if (!Equals(ReadGlobalSetting(pair.Key), pair.Value.Original)) throw new InvalidOperationException("全局同步开关已变化，请放弃修改并重新编辑。");
            PersonalGameSettings.Management.EditValues(stagedValues.Values, stagedConflictChoices);
            foreach (var pair in stagedSettings) Settings.Set(pair.Key, pair.Value.Value);
        }
        stagedValues.Clear(); stagedSettings.Clear(); stagedConflictChoices.Clear(); RefreshPage(); Notify("全局设置已保存。");
    }
    internal void SaveInstanceSettings() {
        if (!CanEditLocal || Settings.Get<int>("VersionPersonalOptionsSync", Instance: Target) != 1)
            throw new InvalidOperationException("请先选择不同步，再编辑并保存本版本个人设置。");
        FlushPendingEdits(false);
        if (stagedValues.Count == 0) return;
        PersonalGameSettings.EditInstance(Target, stagedValues.Values);
        stagedValues.Clear(); RefreshPage(); Notify("此版本个人设置已保存。");
    }
    private void DiscardDraft() {
        CancelEditorState(); stagedValues.Clear(); stagedSettings.Clear(); stagedConflictChoices.Clear(); RefreshPage();
    }
    private void StageConflict(string key, bool useGame, JObject expected) {
        FlushPendingEdits(false);
        if (!JToken.DeepEquals((PersonalGameSettings.Management.Profile()["conflicts"] as JObject)?[key], expected))
            throw new InvalidOperationException("该同步冲突已变化，请刷新后重新选择。");
        string previous = (string)EditorValues[key];
        string original = stagedValues.TryGetValue(key, out var edit) ? edit.Expected : previous;
        string value = PersonalOptionsCatalog.ValidateValue(key, (string)expected[useGame ? "game" : "profile"]);
        StageEditorValues(new[] { new PersonalOptionEdit(key, value, previous) });
        // Keeping the existing value also resolves a conflict, so retain the explicit choice.
        stagedValues[key] = new PersonalOptionEdit(key, value, original);
        stagedConflictChoices[key] = (JObject)expected.DeepClone();
        RefreshPage();
    }
    private void StageExcluded(HashSet<string> excluded) => StageSetting("LaunchPersonalOptionsExcluded",
        new JArray(excluded.OrderBy(key => key, StringComparer.Ordinal)).ToString(Formatting.None));
}
