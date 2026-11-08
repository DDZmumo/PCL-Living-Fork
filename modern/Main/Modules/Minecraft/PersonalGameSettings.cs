// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{
    internal class PersonalOptionsSession : PersonalOptionsSessionData
    {
    }

    internal static class PersonalGameSettings
    {
        private readonly static PersonalOptionsFlow Flow = new PersonalOptionsFlow(LocalGameData.Service, LocalGameData.Gate, () => ModWatcher.HasRunningMinecraft, VersionPath => Adapt(new ModMinecraft.McInstance(VersionPath)), Selected, ModBase.IniClearCache, (First, Second) => PathUtils.IsSame(First, Second), (ErrorValue, Message) => Logger.Warn(ErrorValue, Message), () => new PersonalOptionsSession(), () => Settings.Get<bool>("LaunchPersonalOptionsCapture"), existingOnly: true);
        internal static readonly PersonalOptionsManagement Management = new(LocalGameData.Service, LocalGameData.Gate, Selected, () => ModWatcher.HasRunningMinecraft, ModBase.IniClearCache);
        internal static event Action Changed;
        private static string excludedRaw;
        private static HashSet<string> excluded = new(StringComparer.Ordinal);
        internal static HashSet<string> Excluded() {
            lock (LocalGameData.Gate) {
                string raw = Settings.Get<string>("LaunchPersonalOptionsExcluded");
                if (raw != excludedRaw) {
                    var parsed = JArray.Parse(raw);
                    excluded = new HashSet<string>(parsed.Values<string>(), StringComparer.Ordinal);
                    excludedRaw = raw;
                }
                return new HashSet<string>(excluded, StringComparer.Ordinal);
            }
        }
        internal static void SetExcluded(IEnumerable<string> keys) {
            Settings.Set("LaunchPersonalOptionsExcluded", new JArray(System.Linq.Enumerable.OrderBy(keys, key => key, StringComparer.Ordinal)).ToString(Newtonsoft.Json.Formatting.None));
        }
        internal static PersonalOptionsPreview Preview(ModMinecraft.McInstance instance) => Management.Preview(Adapt(instance));
        internal static int ApplyNow(ModMinecraft.McInstance instance, PersonalOptionsPreview preview) {
            if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new System.IO.IOException("请等待版本文件任务结束，再应用个人设置。");
            return Management.Apply(Adapt(instance), preview);
        }
        internal static void EditInstance(ModMinecraft.McInstance instance, IEnumerable<PersonalOptionEdit> edits) {
            if (instance is null) throw new System.IO.IOException("请先打开版本设置。");
            if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new System.IO.IOException("请等待版本文件任务结束，再修改设置。");
            Management.EditInstanceValues(Adapt(instance), edits);
        }
        internal static IReadOnlyDictionary<string, string> BindingAnnotations(ModMinecraft.McInstance instance) {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var values = Management.Profile()["values"] as JObject;
            foreach (var item in PersonalOptionsCatalog.Items) {
                if (!item.Key.StartsWith("key_", StringComparison.Ordinal)) continue;
                string value = (string)values?[item.Key];
                result[item.Key] = value is null ? "基础键位 · 个人方案未保存"
                    : "个人方案：" + InstanceKeybindingView.DisplayKey(PersonalOptionsRules.NormalizeBinding(value)) +
                        (Enabled(instance) && Selected(item.Key) ? " · 参与同步" : " · 未参与同步");
            }
            return result;
        }

        internal static bool Enabled(ModMinecraft.McInstance Instance)
        {
            return Flow.Enabled(Adapt(Instance));
        }
        private static string CanonicalKey(string Key)
        {
            return PersonalOptionsRules.CanonicalKey(Key);
        }
        private static bool Selected(string Key)
        {
            if (!PersonalOptionsCatalog.Contains(Key)) return false;
            try { if (Excluded().Contains(PersonalOptionsRules.CanonicalKey(Key))) return false; }
            catch { return false; } // An invalid selection cannot silently enable additional writes.
            if (Key.StartsWith("key_", StringComparison.Ordinal))
                return Settings.Get<bool>("LaunchPersonalOptionsKeys") && PersonalOptionsRules.VanillaBinding(Key);
            if (PersonalOptionsRules.IsMouseKey(Key))
                return Settings.Get<bool>("LaunchPersonalOptionsMouse");
            if (PersonalOptionsRules.IsViewKey(Key))
                return Settings.Get<bool>("LaunchPersonalOptionsView");
            if (PersonalOptionsRules.IsAccessibilityKey(Key))
                return Settings.Get<bool>("LaunchPersonalOptionsAccessibility");
            return Key.StartsWith("soundCategory_", StringComparison.Ordinal) && Settings.Get<bool>("LaunchPersonalOptionsAudio");
        }
        internal static Dictionary<string, string> Parse(string Text)
        {
            return PersonalOptionsRules.Parse(Text);
        }
        private static byte[] ReadOptions(string FileName)
        {
            return PersonalOptionsFiles.Read(FileName);
        }
        private static string Decode(byte[] Bytes)
        {
            return PersonalOptionsRules.Decode(Bytes);
        }
        private static bool Modern(ModMinecraft.McInstance Instance)
        {
            return Flow.Modern(Adapt(Instance));
        }
        private static string CanonicalValue(string Key, string Value)
        {
            return PersonalOptionsRules.CanonicalValue(Key, Value);
        }
        private static string ForVersion(string Key, string Value, bool IsModern)
        {
            return PersonalOptionsRules.ForVersion(Key, Value, IsModern);
        }
        internal static string MergeText(string Text, JObject Values, bool IsModern, ISet<string> Keys, bool FovDegrees = true)
        {
            return PersonalOptionsRules.MergeText(Text, Values, IsModern, Keys, Selected, FovDegrees);
        }
        internal static bool HasProfile()
        {
            return Flow.HasProfile();
        }
        internal static void ImportFrom(ModMinecraft.McInstance Instance)
        {
            if (Instance is not null && ModMinecraft.McInstanceLock.IsLocked(Instance)) throw new System.IO.IOException("请等待版本文件任务结束，再导入个人设置。");
            Flow.ImportFrom(Adapt(Instance));
            Changed?.Invoke();
        }
        internal static void ImportGlobalFrom(ModMinecraft.McInstance instance) {
            if (instance is not null && ModMinecraft.McInstanceLock.IsLocked(instance)) throw new System.IO.IOException("请等待版本文件任务结束，再导入全局设置。");
            Flow.ImportFrom(Adapt(instance), PersonalOptionsCatalog.Contains, instance?.Name);
            Changed?.Invoke();
        }
        internal static PersonalOptionsSession Prepare(ModMinecraft.McInstance Instance)
        {
            return (PersonalOptionsSession)Flow.Prepare(Adapt(Instance));
        }
        internal static void Track(PersonalOptionsSession Session, Process Game)
        {
            Flow.Track(Session, Game);
        }
        internal static string NormalizeBinding(string Value)
        {
            return PersonalOptionsRules.NormalizeBinding(Value);
        }
        internal static string BindingForVersion(string Value, bool IsModern)
        {
            return PersonalOptionsRules.BindingForVersion(Value, IsModern);
        }
        internal static bool VanillaBinding(string Key)
        {
            return PersonalOptionsRules.VanillaBinding(Key);
        }
        internal static void CaptureEditedBinding(ModMinecraft.McInstance Instance, string Key, string Value)
        {
            Flow.CaptureEditedBinding(Adapt(Instance), Key, Value);
            Changed?.Invoke();
        }
        internal static void Finish(PersonalOptionsSession Session)
        {
            Flow.Finish(Session);
            Changed?.Invoke();
        }
        private static void Capture(PersonalOptionsSession Session)
        {
            Flow.Capture(Session);
        }
        internal static void RecoverExitedSessions()
        {
            Flow.RecoverExitedSessions();
        }
        private static bool HasActiveSession(string GameFolder)
        {
            return Flow.HasActiveSession(GameFolder);
        }
        private static Dictionary<int, string> CreateLegacyKeys()
        {
            return PersonalOptionsRules.CreateLegacyKeys();
        }
        private static IPersonalOptionsInstance Adapt(ModMinecraft.McInstance Instance)
        {
            return Instance is null ? null : new PersonalOptionsInstance(Instance);
        }
        private sealed class PersonalOptionsInstance : IPersonalOptionsInstance
        {
            private readonly ModMinecraft.McInstance Owner;
            internal PersonalOptionsInstance(ModMinecraft.McInstance Instance)
            {
                Owner = Instance;
            }
            public string VersionPath
            {
                get
                {
                    return Owner.PathVersion;
                }
            }
            public string GameFolder
            {
                get
                {
                    return Owner.PathIndie;
                }
            }
            public bool SyncEnabled
            {
                get
                {
                    return Settings.Get<bool>("LaunchPersonalOptionsSync") && Settings.Get<int>("VersionPersonalOptionsSync", Instance: Owner) == 0;
                }
            }
            public bool VersionValid
            {
                get
                {
                    return Owner.Version.Valid;
                }
            }
            public int VanillaMajor
            {
                get
                {
                    return Owner.Version.Vanilla.Major;
                }
            }
        }
    }
}
