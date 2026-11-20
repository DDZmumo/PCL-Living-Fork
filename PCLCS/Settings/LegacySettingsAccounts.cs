using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualBasic.CompilerServices;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LegacySettingsEngine<TInstance, TEntry, TPlan>
    where TInstance : class
    where TEntry : LegacySettingData<TInstance>
    where TPlan : LegacyAccountMigrationPlan {
    private static readonly string[] accountGroups = { "CacheMsV2", "CacheMs", "CacheNide", "CacheAuth", "LoginNide", "LoginAuth" };

    public string? DecodeAccountValue(TEntry entry, string rawValue, string? deviceIdentify) {
        try {
            string value = entry.Encrypted ? LegacySettingsCrypto.Decrypt(rawValue, "PCL" + deviceIdentify) : rawValue;
            if (entry.Encrypted && !VbStringEqual(LegacySettingsCrypto.Encrypt(value, "PCL" + deviceIdentify), Convert.ToBase64String(Convert.FromBase64String(rawValue)))) return null;
            object checkedValue = Coerce(value, entry.Type);
            if (entry.Type.IsEnum && !Enum.IsDefined(entry.Type, checkedValue)) return null;
            if (entry.Key == "LoginMsJson") {
                JObject accounts = JObject.Parse(value);
                if (accounts.Properties().Any(item => item.Value.Type != JTokenType.String)) return null;
            } else if (entry.Key.EndsWith("ProfileJson", StringComparison.Ordinal) && !VbStringEqual(value, "")) {
                JObject.Parse(value);
            }
            return value;
        } catch {
            // Ciphertext, tokens, account names and parsing exceptions must never be logged.
            return null;
        }
    }
    public string? DecodeAccountWithKeys(TEntry entry, string rawValue, IEnumerable<string> keys) {
        foreach (string key in keys.Distinct(StringComparer.Ordinal)) {
            string? value = DecodeAccountValue(entry, rawValue, key);
            if (value is not null) return value;
        }
        return null;
    }
    public static string? AccountGroup(string key) => key == "CacheMsV2Migrated" ? null : accountGroups.FirstOrDefault(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    public TPlan PlanAccountMigration(IDictionary<string, string> official, IDictionary<string, string> legacy,
        IEnumerable<string> officialKeys, IEnumerable<string> legacyKeys, string currentIdentify) {
        TPlan plan = createPlan();
        plan.CurrentIdentify = currentIdentify;
        var nativePlain = new Dictionary<string, string>(StringComparer.Ordinal);
        var legacyPlain = new Dictionary<string, string>(StringComparer.Ordinal);
        bool hasMicrosoft = official.Keys.Any(key => entries().ContainsKey(key) && IsMicrosoftAccountSetting(entries()[key]));
        foreach (TEntry entry in entries().Values.Where(IsAccountSetting)) {
            if (official.ContainsKey(entry.Key)) {
                string? value = DecodeAccountWithKeys(entry, official[entry.Key], officialKeys);
                if (value is not null) nativePlain[entry.Key] = value;
            }
            if (legacy.ContainsKey(entry.Key) && !(hasMicrosoft && IsMicrosoftAccountSetting(entry))) {
                string? value = DecodeAccountWithKeys(entry, legacy[entry.Key], legacyKeys);
                if (value is not null) legacyPlain[entry.Key] = value;
            }
        }
        // Convert each local-cache group as one account, never joining validated
        // fields from one user with a corrupt/incomplete group from another.
        foreach (string group in accountGroups) {
            List<string> groupKeys = legacy.Keys.Where(key => AccountGroup(key) == group && entries().ContainsKey(key) && IsAccountSetting(entries()[key])).ToList();
            if (!groupKeys.Any()) continue;
            if (groupKeys.Any(key => !legacyPlain.ContainsKey(key))) {
                foreach (string key in groupKeys) legacyPlain.Remove(key);
                continue;
            }
            foreach (TEntry entry in entries().Values.Where(item => IsAccountSetting(item) && AccountGroup(item.Key) == group)) {
                nativePlain[entry.Key] = legacyPlain.ContainsKey(entry.Key) ? legacyPlain[entry.Key] : Convert.ToString(entry.DefaultValue, CultureInfo.InvariantCulture)!;
            }
        }
        foreach (KeyValuePair<string, string> item in legacyPlain.ToList()) {
            if (AccountGroup(item.Key) is not null) continue;
            if (official.ContainsKey(item.Key) && !nativePlain.ContainsKey(item.Key)) {
                legacyPlain.Remove(item.Key);
                continue;
            }
            string value = item.Value;
            if (nativePlain.ContainsKey(item.Key) && item.Key == "LoginMsJson") {
                JObject accounts = JObject.Parse(value);
                foreach (JProperty account in JObject.Parse(nativePlain[item.Key]).Properties()) {
                    if (accounts.Property(account.Name, StringComparison.OrdinalIgnoreCase) is null) accounts.Add(account.Name, account.Value.DeepClone());
                }
                value = accounts.ToString(Formatting.None);
            } else if (nativePlain.ContainsKey(item.Key) && item.Key == "LoginLegacyName") {
                value = string.Join("¨", (value + "¨" + nativePlain[item.Key]).Split('¨').Where(name => !VbStringEqual(name, "")).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            nativePlain[item.Key] = value;
        }
        foreach (KeyValuePair<string, string> item in nativePlain) {
            TEntry entry = entries()[item.Key];
            string rawValue = entry.Encrypted ? LegacySettingsCrypto.Encrypt(item.Value, "PCL" + currentIdentify) : item.Value;
            if (!official.ContainsKey(item.Key) || !VbStringEqual(official[item.Key], rawValue)) plan.Values[item.Key] = rawValue;
        }
        plan.DeleteLegacy.AddRange(legacyPlain.Keys);
        return plan;
    }

    public void ApplyAccountMigration(TPlan plan, ILegacySettingsStore official, ILegacySettingsStore? legacy) {
        var original = new Dictionary<string, object?>(StringComparer.Ordinal);
        // RegistryValueKind is only a kind tag through ILegacySettingsStore here.
        // The actual Registry adapter stays Windows-only; no Registry API is used.
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
        var originalKinds = new Dictionary<string, RegistryValueKind>(StringComparer.Ordinal);
#pragma warning restore CA1416
        var old = new Dictionary<string, object?>(StringComparer.Ordinal);
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
        var oldKinds = new Dictionary<string, RegistryValueKind>(StringComparer.Ordinal);
#pragma warning restore CA1416
        foreach (string key in plan.Values.Keys) {
            string storedKey = RegistryValueName(entries()[key]);
            original[storedKey] = official.GetValue(storedKey, null);
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
            if (original[storedKey] is not null) originalKinds[storedKey] = official.GetValueKind(storedKey);
#pragma warning restore CA1416
        }
        foreach (string key in plan.DeleteLegacy) {
            old[key] = legacy?.GetValue(key, null);
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
            if (old[key] is not null) oldKinds[key] = legacy!.GetValueKind(key);
#pragma warning restore CA1416
        }
        try {
            foreach (KeyValuePair<string, string> item in plan.Values.OrderBy(value => value.Key == "CacheMsV2Migrated" ? 1 : 0)) {
                string storedKey = RegistryValueName(entries()[item.Key]);
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
                official.SetValue(storedKey, item.Value, RegistryValueKind.String);
#pragma warning restore CA1416
                if (!VbStringEqual(Convert.ToString(official.GetValue(storedKey), CultureInfo.InvariantCulture), item.Value) ||
                    DecodeAccountValue(entries()[item.Key], item.Value, plan.CurrentIdentify) is null) throw new InvalidOperationException("账号格式转换校验失败");
            }
            foreach (string key in plan.DeleteLegacy) {
                if (legacy is null) continue;
                legacy.DeleteValue(key, false);
                if (legacy.GetValue(key, null) is not null) throw new InvalidOperationException("旧账号数据清理失败");
            }
            foreach (string key in plan.Values.Keys.Concat(plan.DeleteLegacy).Distinct()) entries()[key].ValueCache.Clear();
        } catch {
            // Preserve LF's account rollback behavior, including failure propagation.
            foreach (KeyValuePair<string, object?> item in original) {
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
                if (item.Value is null) official.DeleteValue(item.Key, false);
                else official.SetValue(item.Key, item.Value, originalKinds[item.Key]);
#pragma warning restore CA1416
            }
            if (legacy is not null) {
                foreach (KeyValuePair<string, object?> item in old) {
#pragma warning disable CA1416 // Inert RegistryValueKind metadata only.
                    if (item.Value is not null) legacy.SetValue(item.Key, item.Value, oldKinds[item.Key]);
#pragma warning restore CA1416
                }
            }
            throw;
        }
    }

    // Only the LF physical namespace is read. Old private MS caches have no client-origin
    // proof and are left untouched; other account/Identify migration keeps LF's rules.
    public Dictionary<string, string> ReadAccountRegistrySnapshot(ILegacySettingsStore? store, bool legacy = false) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store is null) return result;
        foreach (TEntry entry in entries().Values.Where(item => IsAccountSetting(item) || item.Key == "Identify")) {
            if (legacy && IsMicrosoftAccountSetting(entry)) continue;
            object? value = store.GetValue(legacy ? entry.Key : RegistryValueName(entry), null);
            if (value is not null) result[entry.Key] = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }
        return result;
    }

    public void ImportCompatibleAccounts() {
        lock (accountSync()) ImportCompatibleAccountsCore();
    }
    public void ImportCompatibleAccountsCore() {
        if (VbStringEqual(host.RegistryFolder(), "PCL")) return;
        try {
            using ILegacySettingsStore? official = host.OpenRegistry("PCL", true, true);
            using ILegacySettingsStore? legacy = host.OpenRegistry(host.RegistryFolder(), true, false);
            Dictionary<string, string> native = ReadAccountRegistrySnapshot(official);
            Dictionary<string, string> old = ReadAccountRegistrySnapshot(legacy, true);
            if (!native.ContainsKey("Identify")) return;
            List<string> officialKeys = host.IdentifyCandidates(native["Identify"]);
            List<string> legacyKeys = old.ContainsKey("Identify") ? host.IdentifyCandidates(old["Identify"]) : new List<string>();
            legacyKeys.AddRange(officialKeys);
            legacyKeys.Add("0000-0000-0000-0000");
            TPlan plan = PlanAccountMigration(native, old, officialKeys, legacyKeys, host.Identify());
            ApplyAccountMigration(plan, official!, legacy);
            if (plan.Values.Any() || plan.DeleteLegacy.Any()) Log(null, $"已转换 {plan.Values.Count} 项官方格式账号数据，并清理 {plan.DeleteLegacy.Count} 项旧账号数据。", LegacySettingsLogLevel.Info);
        } catch {
            Log(null, "账号格式转换未完成，未通过校验的数据保持原样。", LegacySettingsLogLevel.Warning);
        }
    }

    public Dictionary<string, string> PlanLegacyMicrosoftUpgrade(IDictionary<string, string> raw, string deviceIdentify) {
        var plan = new Dictionary<string, string>(StringComparer.Ordinal);
        const string marker = "CacheMsV2Migrated";
        if (raw.ContainsKey(marker)) {
            string? value = DecodeAccountValue(entries()[marker], raw[marker], deviceIdentify);
            if (value is null || Conversions.ToBoolean(value)) return plan;
        }
        string[] fields = { "OAuthRefresh", "Access", "ProfileJson", "Uuid", "Name" };
        // Any explicit V2 key, including empty logout values, forbids resurrection.
        if (fields.Concat(new[] { "Expires" }).Any(name => raw.ContainsKey("CacheMsV2" + name))) {
            plan[marker] = bool.TrueString;
            return plan;
        }
        var legacy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string field in fields) {
            string key = "CacheMs" + field;
            string? value = "";
            if (raw.ContainsKey(key)) {
                value = DecodeAccountValue(entries()[key], raw[key], deviceIdentify);
                if (value is null) return plan;
            }
            legacy[field] = value;
        }
        if (string.IsNullOrWhiteSpace(legacy["OAuthRefresh"])) return plan;
        foreach (string field in fields) plan["CacheMsV2" + field] = LegacySettingsCrypto.Encrypt(legacy[field], "PCL" + deviceIdentify);
        plan["CacheMsV2Expires"] = LegacySettingsCrypto.Encrypt("0", "PCL" + deviceIdentify);
        plan[marker] = bool.TrueString;
        return plan;
    }
    public void EnsureMicrosoftCacheUpgrade() {
        lock (accountSync()) {
            using ILegacySettingsStore? store = host.OpenRegistry("PCL", true, true);
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in entries().Keys.Where(name => name.StartsWith("CacheMs", StringComparison.Ordinal))) {
                object? value = store!.GetValue(RegistryValueName(entries()[key]), null);
                if (value is not null) raw[key] = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }
            TPlan plan = createPlan();
            plan.CurrentIdentify = host.Identify();
            plan.Values = PlanLegacyMicrosoftUpgrade(raw, host.Identify());
            if (plan.Values.Any()) ApplyAccountMigration(plan, store!, null);
        }
    }
    public Dictionary<string, object?> AccountSnapshot(string prefix) {
        lock (accountSync()) return entries().Values.Where(item => IsAccountSetting(item) && item.Key.StartsWith(prefix, StringComparison.Ordinal)).
            ToDictionary(item => item.Key, item => GetCore(item.Key, null), StringComparer.Ordinal);
    }
    public void WriteAccountValues(IDictionary<string, object?> values) {
        lock (accountSync()) {
            TPlan plan = createPlan();
            plan.CurrentIdentify = host.Identify();
            foreach (KeyValuePair<string, object?> item in values) {
                if (!entries().TryGetValue(item.Key, out TEntry? entry) || !IsAccountSetting(entry)) throw new ArgumentException("不支持的账号设置项");
                object typed = Coerce(item.Value, entry.Type);
                string plain = entry.Type.IsEnum ? Convert.ToInt32(typed, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : Convert.ToString(typed, CultureInfo.InvariantCulture)!;
                string raw = entry.Encrypted ? LegacySettingsCrypto.Encrypt(plain, "PCL" + host.Identify()) : plain;
                if (DecodeAccountValue(entry, raw, host.Identify()) is null) throw new ArgumentException("账号设置格式不正确");
                plan.Values[item.Key] = raw;
            }
            using ILegacySettingsStore? store = host.OpenRegistry("PCL", true, true);
            ApplyAccountMigration(plan, store!, null);
        }
    }
    public JObject UpdateMicrosoftAccounts(Action<JObject> change) {
        lock (accountSync()) {
            JObject accounts = JObject.Parse(VbString(GetCore("LoginMsJson", null))!);
            change(accounts);
            WriteAccountValues(new Dictionary<string, object?> { { "LoginMsJson", accounts.ToString(Formatting.None) } });
            return accounts;
        }
    }
    public void SaveMicrosoftSession(IDictionary<string, object?> values, string? previousName, Func<bool>? canceled = null) {
        lock (accountSync()) {
            if (canceled is not null && canceled()) throw new OperationCanceledException();
            JObject accounts = JObject.Parse(VbString(GetCore("LoginMsJson", null))!);
            string? name = VbString(values["CacheMsV2Name"]);
            foreach (JProperty account in accounts.Properties().Where(item => item.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()) account.Remove();
            accounts[name!] = VbString(values["CacheMsV2OAuthRefresh"]);
            var batch = new Dictionary<string, object?>(values, StringComparer.Ordinal) {
                ["LoginMsJson"] = accounts.ToString(Formatting.None), ["CacheMsV2Migrated"] = true
            };
            WriteAccountValues(batch);
        }
    }
    public void SaveAccountGroup(IDictionary<string, object?> values, Func<bool>? canceled = null) {
        lock (accountSync()) {
            if (canceled is not null && canceled()) throw new OperationCanceledException();
            WriteAccountValues(values);
        }
    }
    public List<string> UpdateLegacyNames(Action<List<string>> change) {
        lock (accountSync()) {
            List<string> names = VbString(GetCore("LoginLegacyName", null))!.Split('¨').Where(name => !VbStringEqual(name, "")).ToList();
            change(names);
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            WriteAccountValues(new Dictionary<string, object?> { { "LoginLegacyName", string.Join("¨", names) } });
            return names;
        }
    }
    public Dictionary<string, string> UpdateServerAccounts(string? token, string name, string password, bool remove) {
        if (!VbStringEqual(token, "Auth") && !VbStringEqual(token, "Nide")) throw new ArgumentException("未知认证类型");
        lock (accountSync()) {
            string? emails = VbString(GetCore("Login" + token + "Email", null));
            string? passwords = VbString(GetCore("Login" + token + "Pass", null));
            string[] names = VbStringEqual(emails, "") ? new string[0] : emails!.Split('¨');
            string[] passes = VbStringEqual(passwords, "") ? new string[0] : passwords!.Split('¨');
            // Names saved without a remembered password leave the two lists out of step; pair them up again (missing
            // passwords stay empty) instead of refusing every later save.
            if (names.Length != passes.Length) passes = Enumerable.Range(0, names.Length).Select(i => i < passes.Length ? passes[i] : "").ToArray();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!remove) {
                if (name.Contains("¨") || password.Contains("¨")) throw new ArgumentException("原版账号记录不支持分隔字符 ¨。");
                result[name] = password;
            }
            for (int i = 0; i < names.Length; i++) {
                if (!names[i].Equals(name, StringComparison.OrdinalIgnoreCase)) result[names[i]] = passes[i];
            }
            WriteAccountValues(new Dictionary<string, object?> {
                { "Login" + token + "Email", string.Join("¨", result.Keys) }, { "Login" + token + "Pass", string.Join("¨", result.Values) }
            });
            return result;
        }
    }
}
